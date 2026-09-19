using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public partial class GameManager : Node, IBotTable, ITableHost, ITableUiHost
{
    private GameState _gameState;
    private Player _player1;
    private Player _player2;

    [ExportGroup("Player 1 UI")]
    [Export] private Label _p1ScoreLabel;
    [Export] private Label _p1StatusLabel;
    [Export] private Label _p1WinsLabel;
    [Export] private Control _p1BoardContainer;
    [Export] private Control _p1ModifierContainer;
    [Export] private Button _p1DrawCardButton; // 2-player scene: P1's own Draw Card / Hold row under their hand
    [Export] private Button _p1HoldButton;

    [ExportGroup("Player 2 UI (AI)")]
    [Export] private Label _p2ScoreLabel;
    [Export] private Label _p2StatusLabel;
    [Export] private Label _p2WinsLabel;
    [Export] private Control _p2BoardContainer;
    [Export] private Control _p2ModifierContainer;
    [Export] private Control _p2Rotator;
    [Export] private Button _p2DrawCardButton; // 2-player scene: inside P2Rotator, so it flips with P2's side
    [Export] private Button _p2HoldButton;

    [ExportGroup("Shared UI")]
    [Export] private Label _setInfoLabel;
    [Export] private OptionButton _gameModeButton;
    [Export] private CheckButton _mirrorToggle; // 2-player scene only: rotate P2's side 180 degrees
    [Export] private Button _startButton;
    [Export] private Button _drawCardButton; // solo scene: one shared pair in the middle panel (Player 1, the human)
    [Export] private Button _holdButton;
    [Export] private Control _mainDeckPosition;

    [ExportGroup("System UI")]
    [Export] private Button _restartButton;
    [Export] private Button _exitButton;

    private Random _random = new Random();
    private PackedScene _cardViewScene = GD.Load<PackedScene>("res://CardView.tscn");
    private bool _isGameStarted = false;
    private bool _isVsBot = false;
    private bool _setOverPending = false; // the set-end explanation is up; nothing moves until it's acknowledged

    // ------------------------------------------------------------------
    // Tap to pick up, tap again to play
    //
    // Nothing is spent by a single tap. Tapping a Modifier picks it up (it lifts, the others dim,
    // and the score shows, in colour, what it would become); the Draw Card / Hold row is
    // then replaced by big Play / Put back / Flip Value buttons, and tapping the same card again plays it.
    // Chosen over long-press or drag-and-drop: both need sustained precision, which is exactly what
    // small children and older hands struggle with.
    // ------------------------------------------------------------------
    // The single-player ladder run, when there is one (RunData autoload). Local 2-player ignores
    // it entirely and keeps its self-contained randomized hands.
    private bool _inRun = false;

    private Card _p1SelectedCard;
    private Card _p2SelectedCard;


    // The intermission between two rungs: the market, then the deck. Overlays over this same
    // table rather than scenes of their own, so the run never leaves the table it is playing on.
    private ShopOverlay _shopOverlay;
    private DeckOverlay _deckOverlay;

    public override void _Ready()
    {
        _gameState = new GameState();
        _player1 = new Player("Player 1");
        _player2 = new Player("Player 2");
        // The scene's nodes are exported onto THIS class, because that is what the .tscn wires.
        // They are handed to the picture once, here, and it keeps them for good.
        _ui = new TableUi(this, this, new TableUi.Nodes
        {
            MainLayout = GetNodeOrNull<BoxContainer>("GameUI/MainLayout"),
            P1BoardContainer = _p1BoardContainer,       P2BoardContainer = _p2BoardContainer,
            P1ModifierContainer = _p1ModifierContainer, P2ModifierContainer = _p2ModifierContainer,
            P1ScoreLabel = _p1ScoreLabel,               P2ScoreLabel = _p2ScoreLabel,
            P1StatusLabel = _p1StatusLabel,             P2StatusLabel = _p2StatusLabel,
            P1WinsLabel = _p1WinsLabel,                 P2WinsLabel = _p2WinsLabel,
            P1DrawCardButton = _p1DrawCardButton,       P2DrawCardButton = _p2DrawCardButton,
            P1HoldButton = _p1HoldButton,               P2HoldButton = _p2HoldButton,
            DrawCardButton = _drawCardButton,           HoldButton = _holdButton,
            P2Rotator = _p2Rotator,                     MainDeckPosition = _mainDeckPosition,
            SetInfoLabel = _setInfoLabel,               MirrorToggle = _mirrorToggle,
            GameModeButton = _gameModeButton,
        });

        _table = new Table(this);
        _bot = new Bot(this); // before the first deal: the table asks it for the bot's hand
        _table.DealMatchHands();


        // Both scenes work in portrait and landscape (ApplyResponsiveLayout re-flows on every
        // resize / rotation), so the phone is free to follow its sensor.

        if (_gameModeButton != null)
        {
            _gameModeButton.Clear();
            _gameModeButton.AddItem("Local 2-Player", 0);
            _gameModeButton.AddItem("vs. Bot", 1);
            _gameModeButton.Select(1);
            _gameModeButton.ItemSelected += id =>
            {
                if (_mirrorToggle != null) _mirrorToggle.Visible = (id == 0);
            };
        }

        if (_mirrorToggle != null)
        {
            // Face-to-face on a phone wants P2 flipped by default; on a desktop it doesn't.
            _mirrorToggle.SetPressedNoSignal(OS.HasFeature("mobile"));
            _mirrorToggle.Visible = _gameModeButton != null && _gameModeButton.GetSelectedId() == 0;
            // UpdateUI FIRST. It is what decides the score's font and whether the score is one
            // line or two, and ApplyResponsiveLayout is what measures the side around it. The
            // other way round, every slot was sized against the form that was on its way off the
            // screen (Alexander, 2026-09-18: toggling the mirror shifts Player 1's side).
            _mirrorToggle.Toggled += _ => { _ui.Refresh(); _ui.ApplyResponsiveLayout(); };
        }

        // The button is "Draw Card" (Alexander, 2026-09-15), and the code uses the same words the
        // player reads (2026-09-16): Match > Set > Turn, Modifiers, Draw Card, Flip Value. Pressing
        // Draw Card ends your part of this turn, which is why the state is still HasEndedTurn.
        //
        // Connect button signals. The solo scene has one shared Draw Card / Hold pair in the middle
        // panel (it belongs to Player 1, the human); the 2-player scene gives each player their own
        // pair under their hand instead.
        _startButton.Pressed += OnStartButtonPressed;
        if (_drawCardButton != null) _drawCardButton.Pressed += () => OnDrawCardPressed(_player1);
        if (_holdButton != null) _holdButton.Pressed += () => OnHoldPressed(_player1);
        if (_p1DrawCardButton != null) _p1DrawCardButton.Pressed += () => OnDrawCardPressed(_player1);
        if (_p1HoldButton != null) _p1HoldButton.Pressed += () => OnHoldPressed(_player1);
        if (_p2DrawCardButton != null) _p2DrawCardButton.Pressed += () => OnDrawCardPressed(_player2);
        if (_p2HoldButton != null) _p2HoldButton.Pressed += () => OnHoldPressed(_player2);
        if (_restartButton != null) _restartButton.Pressed += OnRestartPressed;
        if (_exitButton != null) _exitButton.Pressed += OnExitPressed;

        // Set initial waiting message (UpdateUI below disables every action button until Start).
        _setInfoLabel.Text = "Press Start Game to Begin";

        // Show the empty 3x3 boards and the win chips before the game starts.
        _ui.FillBoardWithSlots(_p1BoardContainer);
        _ui.FillBoardWithSlots(_p2BoardContainer);
        _ui.BuildWinChips();
        _ui.BuildDeckCounter();
        BuildSpotlight();
        BuildSetEndOverlay();
        BuildHowToPlay();
        _ui.CompactControlPanel(); // must precede BuildConfirmRows - see the method
        _ui.BuildConfirmRows();
        BuildIntermissionOverlays();
        _ui.BuildTableBanners();
        GameSettings.EnsureLoaded();
        GameSettings.Changed += OnSettingsChanged;
        BuildDebugRow();
        BuildOptions();
        BuildStartMenu();
        _ui.ConfigureStatusLabels();
        _ui.StyleTableForReadability(); // after BuildConfirmRows: it styles those buttons too
        _ui.BuildScoreLines();          // after StyleTableForReadability moved the labels

        GetTree().Root.SizeChanged += _ui.ApplyResponsiveLayout;
        _ui.ApplyResponsiveLayout();
        _ui.Refresh();

        // Two ways to arrive already holding a decision, and in both the player has pressed a
        // button to get here - so deal the match rather than showing them a second front door.
        //
        // The run's note is RunData's (the deck screen, a debug stage jump, or the start menu
        // sending the player over from the two-player table); the local 2-player note is this
        // class's own static, for the reason written where it is declared. Both survive the
        // reload; neither is saved to disk, because both describe THIS reload and not the run.
        bool autoRun = _gameModeButton == null && RunData.Instance != null && RunData.Instance.AutoStartNextMatch;
        if (autoRun) RunData.Instance.AutoStartNextMatch = false;

        bool autoLocal2P = _gameModeButton != null && _pendingLocal2Player;
        if (autoLocal2P)
        {
            _pendingLocal2Player = false;
            _gameModeButton.Select(0); // or OnStartButtonPressed routes straight back to the solo scene
            if (_mirrorToggle != null) _mirrorToggle.Visible = true;
        }

        if (autoRun || autoLocal2P) CallDeferred(MethodName.OnStartButtonPressed);
        else ShowStartMenu();
    }

    public override void _ExitTree()
    {
        if (GetTree() != null) GetTree().Root.SizeChanged -= _ui.ApplyResponsiveLayout;
        // A static event outlives the scene; a Restart would otherwise leave it calling a freed table.
        GameSettings.Changed -= OnSettingsChanged;
    }

    // ------------------------------------------------------------------
    // Options (GameSettings / OptionsOverlay)
    // ------------------------------------------------------------------
    private OptionsOverlay _optionsOverlay;
    private readonly List<Control> _debugRows = new List<Control>();

    private void BuildOptions()
    {
        _optionsOverlay = new OptionsOverlay();
        AddChild(_optionsOverlay);
        _optionsOverlay.Build();
    }

    private void OpenOptions(Action onClosed = null) => _optionsOverlay?.Open(onClosed);

    private void OnSettingsChanged()
    {
        if (!IsInsideTree()) return;
        foreach (Control row in _debugRows)
            if (IsInstanceValid(row)) row.Visible = GameSettings.ShowDebugButtons;
        // Some settings reshape a whole side, which is a new layout: let the fixed slots settle
        // on the new sizes rather than keep the old ones, and let the fit find its own scale for
        // the new shape instead of keeping the one it settled on for the old one.
        _ui.ResetFitState();
        _ui.ApplyResponsiveLayout(); // also re-runs EnsureLayoutFits (the debug rows change the height)
    }


    // ------------------------------------------------------------------
    // Buttons
    // ------------------------------------------------------------------
    /// The table's own Restart: THIS match again, in the mode already being played. Restart on a
    /// table has never meant "back to the front door", and now that there is a front door it would
    /// mean making the player choose their mode a second time to get back where they were.
    private void OnRestartPressed() => RestartScene(sameMatch: true);

    /// ...and the other half: reload to the start menu. This is what the end of a run wants, and
    /// what "Play Again" after a match with nothing left to continue means - the menu is where the
    /// medals and the record are, and where climbing again becomes a decision rather than a reflex.
    private void RestartToMenu() => RestartScene(sameMatch: false);

    private void RestartScene(bool sameMatch)
    {
        // Reloading the scene rebuilds GameManager, GameState and both Players from scratch,
        // so this fully resets the match (set wins, scores, hands).
        if (sameMatch && _isGameStarted)
        {
            // The same notes the start menu leaves when it sends the player between scenes; _Ready
            // reads them and deals instead of opening the menu.
            if (_gameModeButton == null)
            {
                if (RunData.Instance != null) RunData.Instance.AutoStartNextMatch = true;
            }
            else _pendingLocal2Player = true;
        }
        else
        {
            // Land on the menu, and leave nothing behind that would skip past it.
            if (RunData.Instance != null) RunData.Instance.AutoStartNextMatch = false;
            _pendingLocal2Player = false;
        }

        GD.Print(sameMatch ? "Restarting match..." : "Returning to the start menu...");
        GetTree().ReloadCurrentScene();
    }

    /// The table's Exit leaves the MATCH, not the app (Alexander, 2026-09-16: "Exit doesn't allow
    /// you to go back to start menu"). Quitting the app lives on the start menu now.
    private void OnExitPressed()
    {
        HideTableMenu();
        RestartToMenu();
    }

    public override void _Notification(int what)
    {
        // Android hardware/gesture "Back" and the desktop window close button both arrive here.
        if (what == NotificationWMGoBackRequest || what == NotificationWMCloseRequest)
        {
            GetTree().Quit();
        }
    }

    private void OnStartButtonPressed()
    {
        // Route the player to the dedicated solo scene if bot is selected
        if (_gameModeButton != null && _gameModeButton.GetSelectedId() == 1)
        {
            GetTree().ChangeSceneToFile("res://solo_table_scene.tscn");
            return;
        }

        _isGameStarted = true;

        // If _gameModeButton is null (meaning we are already in the solo scene), force AI to true
        _isVsBot = (_gameModeButton == null) ? true : false;

        _player2.PlayerName = _isVsBot ? "AI Bot" : "Player 2";

        _startButton.Visible = false;
        if (_gameModeButton != null) _gameModeButton.Visible = false;

        BeginRunMatch();

        // Decided BEFORE the hand is dealt and before the first shuffle, because staging is a
        // change to both of them.
        bool tutorial = ShouldRunTutorial();
        _tutorialStaged = tutorial && ShouldStageTutorial();

        _table.DealMatchHands(); // the hand has to last all three sets of the match

        StartNewSet(); // UpdateUI enables the Draw Card / Hold buttons

        if (tutorial) StartTutorial();
    }

    /// Puts the solo scene onto the ladder: picks up the run in progress (or starts one), and takes
    /// this venue's target score. Local 2-player is never part of a run.
    private void BeginRunMatch()
    {
        _inRun = false;
        if (!_isVsBot)
        {
            _gameState.TargetScore = _local2PlayerTarget; // the setup page's choice
            _ui.ApplyRankTheme(); // the clear colour is global: put the plain felt back for 2-player
            return;
        }

        RunData run = RunData.Instance;
        if (run == null)
        {
            _ui.ApplyRankTheme();
            return; // autoload missing (e.g. the scene opened on its own) - play a one-off
        }

        if (!run.RunActive || run.RunComplete) run.StartNewRun();
        run.EnsureRuleset(); // a save from before the finale was rolled on arrival

        _inRun = true;
        _gameState.TargetScore = run.CurrentTarget;
        _player2.PlayerName = run.CurrentOpponent;
        _ui.ApplyRankTheme();
    }


    private void StartNewSet()
    {
        _player1.ResetForNewSet();
        _player2.ResetForNewSet();
        ClearSelections();
        _ui.ClearEffectBanner();

        // Clear old cards and lay out fresh empty 3x3 boards
        _ui.FillBoardWithSlots(_p1BoardContainer);
        _ui.FillBoardWithSlots(_p2BoardContainer);

        _table.StartSet(); // a fresh forty, and the next turn is this set's opening one

        DealCards();
    }


    // ------------------------------------------------------------------
    // Turns
    //
    // There is no turn order. A set is a series of turns: every player who isn't holding draws
    // a card at the same time, then both play modifiers and press Draw Card / Hold blind. Once
    // neither player can act any more the turn is resolved (ResolveTurn) - busts, both holding,
    // or simply the next turn.
    // ------------------------------------------------------------------

    /// A turn begins: the bot forgets last turn's re-opening, the table deals, the screen catches
    /// up, and the bot starts thinking. The cards themselves are one line of that - Table.DealTurn.
    private void DealCards()
    {
        _bot.ResetForTurn();  // the bot's re-opening is the bot's, not the deck's
        _table.DealTurn();    // ...and everything the cards do is the table's
        _ui.Refresh();

        if (_isVsBot) _bot.ProcessTurn();
    }

    /// Called whenever someone finishes their part of the turn (Draw Card, Hold, or the bot).
    /// Does nothing until BOTH players are done; then either ends the set or deals again.
    private void ResolveTurn()
    {
        if (!_isGameStarted || _gameState.IsGameOver || _setOverPending) return;

        if (_player1.CanAct || _player2.CanAct)
        {
            _ui.Refresh(); // one side is still deciding
            return;
        }

        int target = _gameState.TargetScore;
        bool anyBust = _player1.CurrentScore > target || _player2.CurrentScore > target;
        bool bothHolding = _player1.IsHolding && _player2.IsHolding;

        if (anyBust || bothHolding)
        {
            if (OfferRescue()) return;
            EndSet();
            return;
        }

        DealCards();
    }

    // ------------------------------------------------------------------
    // The rescue offer (claude/monetization-spec.md §3)
    //
    // Solo run, ladder stage 4+ or endless. A turn that ends with the player bust - and the bot
    // not, since both over is a tie that is replayed anyway - rolls 15%. On a hit the set waits:
    //
    //   free player   Watch an ad -> a one-off card that puts them on exactly target - 1.
    //                 No thanks   -> a copy of a random card from their 12-card deck (may not help).
    //                 Closing the ad early counts as No thanks; no ad to show offers No thanks only.
    //   No Ads owner  One "Rescue" button -> the exact card. Same 15%, no ad.
    //   (Steam / desktop release builds have no ads, so they get the No Ads owner's version.)
    //
    // Whatever card is given goes into the hand and the turn re-opens: the player still has to
    // play it, and the bust is judged again when the turn ends. At most one rescue per match -
    // the flag is spent the moment the offer appears, whichever way the player answers.
    // ------------------------------------------------------------------
    private Control _rescueOverlay;
    private VBoxContainer _rescueBox;

    /// True from the moment an offer appears until its card is handed over (the ad included), so
    /// nothing on the table can be pressed underneath it.
    private bool _rescuePending;

    /// Debug row: roll 100% instead of 15%, so the offer can be tested without busting for an hour.
    private static bool _debugAlwaysRescue;

    private bool RescueShowing => _rescuePending;

    /// True if the offer is now up and the set must wait for it.
    private bool OfferRescue()
    {
        RunData run = _inRun ? RunData.Instance : null;
        if (run == null || !_isVsBot || _tutorialActive || !run.RescueEligible) return false;

        int target = _gameState.TargetScore;
        // Only a bust that LOSES the set. Both over is a tie and is replayed anyway.
        if (_player1.CurrentScore <= target || _player2.CurrentScore > target) return false;

        double chance = _debugAlwaysRescue ? 1.0 : RunData.RescueChance;
        if (_random.NextDouble() >= chance)
        {
            GD.Print($"Rescue roll missed ({chance:P0}).");
            return false;
        }

        run.UseMatchRescue(); // spent now: an offer is the match's one rescue, whatever the answer
        _rescuePending = true;

        if (_rescueOverlay == null)
        {
            _rescueOverlay = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
            AddChild(_rescueOverlay);
            _rescueOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            OverlayUi.AddDim(_rescueOverlay);
            _rescueBox = OverlayUi.AddPanel(_rescueOverlay);
        }
        OverlayUi.ClearChildren(_rescueBox);

        int landing = target - 1;
        bool guaranteed = !AdService.AdsActive; // bought No Ads, or a platform with no ads

        if (guaranteed)
        {
            _rescueBox.AddChild(OverlayUi.MakeLabel("Bust!  Rescue!", 30, OverlayUi.MedalGold));
            _rescueBox.AddChild(OverlayUi.MakeLabel(
                $"Take a card that puts you on {landing}.\nPlay it before you end your turn.",
                16, OverlayUi.Muted));
            AddRescueButton("Rescue", 48, GiveExactRescue);
        }
        else
        {
            bool adReady = AdService.RewardedReady;
            _rescueBox.AddChild(OverlayUi.MakeLabel("Bust!  Try again?", 30, OverlayUi.MedalGold));
            _rescueBox.AddChild(OverlayUi.MakeLabel(
                adReady
                    ? $"Watch a short ad for a card that puts you on {landing}.\n"
                      + "Or take a random card from your deck - it might not help."
                    : "Take a random card from your deck.\nIt might be enough. It might not.",
                16, OverlayUi.Muted));

            if (adReady) AddRescueButton($"Watch ad - land on {landing}", 48, WatchRescueAd);
            AddRescueButton("No thanks - random card", adReady ? 40 : 48, GiveRandomRescue);
        }

        MoveChild(_rescueOverlay, GetChildCount() - 1);
        _rescueOverlay.Visible = true;
        _ui.Refresh();
        return true;
    }

    private void AddRescueButton(string text, int height, Action onPressed)
    {
        Button button = new Button { Text = text, CustomMinimumSize = new Vector2(300, height) };
        button.Pressed += onPressed;
        _rescueBox.AddChild(button);
    }

    private void WatchRescueAd()
    {
        _rescueOverlay.Visible = false; // the ad covers the table; the lock stays on
        AdService.ShowRewarded(this, result =>
        {
            GD.Print($"Rescue ad: {result}");
            if (result == AdService.RewardResult.Completed) GiveExactRescue();
            else GiveRandomRescue(); // closed early, or nothing to show: that is "No thanks"
        });
    }

    /// The card that lands the player on target - 1. Its value can be as low as -11 (a bust
    /// overshoots by up to 10), which is below any card the game sells - hence its own card rather
    /// than a lookup into the ordinary modifiers.
    private void GiveExactRescue()
    {
        int value = (_gameState.TargetScore - 1) - _player1.CurrentScore;
        Card rescue = new Card(value, CardType.Modifier) { IsRescue = true };
        GiveRescueCard(rescue, $"Rescue: play your {rescue.DisplayText} to land on {_gameState.TargetScore - 1}.");
    }

    private void GiveRandomRescue()
    {
        Card copy = RunData.Instance?.DrawRescueCopy()
                    ?? new Card(-_random.Next(1, 7), CardType.Modifier); // an empty deck; a live run never has one
        copy.IsRescue = true;
        GiveRescueCard(copy, $"Rescue: you take a {copy.DisplayText}. It might be enough.");
    }

    private void GiveRescueCard(Card card, string banner)
    {
        if (_rescueOverlay != null) _rescueOverlay.Visible = false;
        _rescuePending = false;

        // Into the hand - as a 5th card if the hand is full; it never replaces one the player chose.
        // Deliberately NOT NoteModifierMet: a rescue card is not part of the collection.
        _player1.Modifiers.Add(card);

        // Re-open the turn exactly as an effect card does: no new draw, just a chance to play.
        _player1.IsHolding = false;
        _player1.HasEndedTurn = false;
        _ui.ShowEffectBanner(banner);
        _ui.Refresh();
    }

    // ------------------------------------------------------------------
    // The table's picture
    //
    // Everything the live table looks like is in TableUi.cs. What is left here is the contract it
    // is handed, and the direction of every member is the point: the picture asks questions and
    // reports presses. There is nothing in this list that changes a score, spends a card or ends
    // a turn - those stay on this side, which is what keeps "what it looks like" from quietly
    // becoming "what it does" again.
    // ------------------------------------------------------------------
    private TableUi _ui;

    Player ITableUiHost.Player1 => _player1;
    Player ITableUiHost.Player2 => _player2;
    GameState ITableUiHost.State => _gameState;
    Table ITableUiHost.Table => _table;
    bool ITableUiHost.GameStarted => _isGameStarted;
    bool ITableUiHost.VsBot => _isVsBot;
    bool ITableUiHost.InRun => _inRun;
    bool ITableUiHost.SetOverPending => _setOverPending;

    Card ITableUiHost.SelectedFor(Player player) => SelectedFor(player);
    bool ITableUiHost.CanAct(Player player) => HumanCanActFor(player);
    bool ITableUiHost.CanPlayEffect(Player owner, Card card) => CanPlayEffect(owner, card);
    string ITableUiHost.StatusFor(Player player) => StatusFor(player);
    string ITableUiHost.SetInfoLine() => $"{RunHeader()}{TurnStatusText()}";
    void ITableUiHost.ValidateSelections() => ValidateSelections();
    void ITableUiHost.ModifierPressed(Player player, Card card) => OnModifierCardPressed(player, card);
    void ITableUiHost.PlayPressed(Player player) => PlaySelectedCard(player);
    void ITableUiHost.PutBackPressed(Player player) => SetSelection(player, null);
    void ITableUiHost.FlipValuePressed(Player player) => FlipSelectedValue(player);
    void ITableUiHost.BuildTableMenu() => BuildTableMenu();
    void ITableUiHost.LayoutChanged() => RefreshSpotlight();
    string ITableUiHost.FinaleRulesLine(RunData run, string prefix) => FinaleRulesLine(run, prefix);

    /// The table has just been repainted. The tutorial is watching the screen for the step it set,
    /// and a coach mark waits for a quiet moment to appear - both get their look here, after the
    /// paint and before anything else happens.
    void ITableUiHost.AfterRefresh()
    {
        CheckTutorialProgress();
        DrainCoachMarks();
    }

    // ------------------------------------------------------------------
    // The cards
    //
    // The deck, the hands and what has been drawn live in Table.cs. What is left here is the
    // contract it is handed - same pattern as the bot's above, and read the same way: this list
    // is the whole of what the cards may touch.
    // ------------------------------------------------------------------
    private Table _table;

    Player ITableHost.Player1 => _player1;
    Player ITableHost.Player2 => _player2;
    GameState ITableHost.State => _gameState;
    Random ITableHost.Rng => _random;
    RunData ITableHost.Run => _inRun ? RunData.Instance : null;
    bool ITableHost.VsBot => _isVsBot;
    bool ITableHost.LocalSpecials => _local2PlayerSpecials;
    bool ITableHost.TutorialStaged => _tutorialStaged;
    IReadOnlyList<int> ITableHost.TutorialOpening => TutorialOpening;
    IReadOnlyList<int> ITableHost.TutorialModifiers => TutorialModifiers;

    List<CardEffect> ITableHost.UnlockedLocalSpecials() => UnlockedLocalSpecials();
    void ITableHost.DealBotHand() => _bot.DealHand();

    void ITableHost.ShowDrawnCard(Player player, Card card, float delay) =>
        _ui.InstantiateCardView(card, player == _player1 ? _p1BoardContainer : _p2BoardContainer, delay);

    void ITableHost.HandsDealt(bool introduceCards)
    {
        ClearSelections();
        if (!introduceCards) return;
        QueueCoachMarksForModifiers();
        foreach (Card card in _player1.Modifiers) NoteModifierMet(card);
    }

    // ------------------------------------------------------------------
    // The opponent
    //
    // Every decision it makes lives in Bot.cs. What is left here is the contract it is handed:
    // the table it may read, and the handful of things it may do to it. The list is deliberately
    // short and deliberately explicit - implemented on the interface rather than as ordinary
    // methods, so nothing in GameManager can call them by accident and the bot's reach stays
    // exactly as wide as it looks.
    // ------------------------------------------------------------------
    private Bot _bot;

    Player IBotTable.BotPlayer => _player2;
    Player IBotTable.HumanPlayer => _player1;
    GameState IBotTable.State => _gameState;
    RunData IBotTable.Run => _inRun ? RunData.Instance : null; // a one-off solo match is never a boss fight
    Random IBotTable.Rng => _random;
    int IBotTable.HandSize => Table.HandSize;
    int IBotTable.MaxModifierMagnitude => Table.MaxModifierMagnitude;
    bool IBotTable.VsBot => _isVsBot;
    bool IBotTable.LocalSpecials => _local2PlayerSpecials;
    bool IBotTable.BotPlayedEffectThisTurn => _table.HasPlayedEffect(_player2);
    bool IBotTable.TutorialHoldsBot => _tutorialActive;

    bool IBotTable.IsRecallLocked(Player owner, Card card) => _table.IsRecallLocked(owner, card);
    bool IBotTable.CanPlayEffect(Player owner, Card card) => CanPlayEffect(owner, card);
    bool IBotTable.PlayEffectCard(Player owner, Card card, Card chosen) => PlayEffectCard(owner, card, chosen);
    void IBotTable.AddLocalSpecial(Player player) => _table.AddLocalSpecial(player);
    void IBotTable.Refresh() => _ui.Refresh();
    void IBotTable.ResolveTurn() => ResolveTurn();

    void IBotTable.DealPlainHand(Player player) => _table.DealPlainHand(player);

    void IBotTable.PlayBotModifier(Card card)
    {
        _player2.PlayModifierCard(card, _gameState);
        NoteModifierMet(card); // played at you, so you have met it
        _ui.InstantiateCardView(card, _p2BoardContainer);
    }

    async Task<bool> IBotTable.Pause(double seconds)
    {
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        return IsInsideTree(); // false: the scene was restarted or exited while it waited
    }

    // ------------------------------------------------------------------
    // Effect cards
    //
    // One path for both sides. The AI reaches this from ProcessAiTurn (pass 2) and the player
    // from their hand buttons once the market sells them one; neither gets its own rules.
    // ------------------------------------------------------------------

    /// Can this player play this ORDINARY modifier right now? The only rule is the Recall lock -
    /// everything else about a plain card is decided by the player's own arithmetic.
    private bool CanPlayModifierNow(Player owner, Card card) =>
        card != null && card.Effect == CardEffect.None && !_table.IsRecallLocked(owner, card);

    /// Can this player reach across the table with this card right now? Legality is the card's
    /// own business (CardEffects.CanPlay); the once-per-turn limit is the turn's.
    private bool CanPlayEffect(Player owner, Card card)
    {
        if (card == null || card.Effect == CardEffect.None) return false;
        if (!CardEffects.Implemented(card.Effect)) return false;
        if (_table.HasPlayedEffect(owner)) return false;

        Player target = (owner == _player1) ? _player2 : _player1;
        return CardEffects.CanPlay(card, owner, target, _gameState.TargetScore);
    }

    /// WHY this card cannot be played right now, as a sentence, or null when it can be. The
    /// once-per-turn limit belongs to the TURN, so it is answered here; every other rule is the
    /// card's own and is answered by CardEffects.
    ///
    /// The same sentence is what the status line shows and what explains the greyed-out Play
    /// button - a rule the player cannot see is a rule they cannot learn.
    private string EffectRefusal(Player owner, Card card)
    {
        if (card == null || card.Effect == CardEffect.None) return null;

        if (_table.HasPlayedEffect(owner))
            return "one card across the table per turn, and you have played yours.";

        Player target = (owner == _player1) ? _player2 : _player1;
        return CardEffects.RefusalReason(card, owner, target, _gameState.TargetScore);
    }

    /// Spends an effect card and applies it. Returns false without touching anything if the play
    /// was not legal, so a card is never silently eaten.
    /// `chosen` is Recall's only: which spent card comes back. The player picks it in the Recall
    /// overlay, the bot in PickRecallTarget; every other effect ignores it.
    private bool PlayEffectCard(Player owner, Card card, Card chosen = null)
    {
        if (!CanPlayEffect(owner, card)) return false;
        if (!owner.Modifiers.Remove(card)) return false;

        Player target = (owner == _player1) ? _player2 : _player1;

        // Veto destroys a card that is already face-up on the target's board. Resolve takes it out
        // of ActiveCardsOnBoard and then clears LastPlayedModifier, and it knows nothing about
        // nodes - so the card has to be grabbed HERE, before resolving, or there is nothing left
        // to point the animation at.
        Card destroyed = (card.Effect == CardEffect.Veto) ? target.LastPlayedModifier : null;

        CardEffects.EffectResult result = CardEffects.Resolve(card, owner, target, _gameState.TargetScore, chosen);

        if (!result.Applied)
        {
            owner.Modifiers.Add(card); // put it back rather than lose it to a rule we misread
            return false;
        }

        _table.NoteEffectPlayed(owner);

        // Recall's card is back in hand but dead until the next turn. Set AFTER Resolve, because
        // Resolve is what moved it out of the spent pile.
        if (card.Effect == CardEffect.Recall) _table.LockRecall(owner, chosen);

        // THE ANSWERING RULE. A card played at you re-opens your turn for this turn, so you always
        // get a say - unless you are holding, which is the locked state Shave exists to punish.
        //
        // ReleasesHold is the one exception to that exception (Veto, pass 7): it un-locks a score
        // that was already committed, so the target is re-opened even from a hold. Neither flag
        // ever deals a card - a re-opened player plays a Modifier, holds, or ends the turn.
        if (result.ReleasesHold) target.IsHolding = false;
        bool reopened = result.ReopensTarget && !target.IsHolding;
        if (reopened) target.HasEndedTurn = false;

        // The two effects that change the other player's score sit in THEIR board, so the number
        // that moved and the card that moved it are in the same place.
        bool onTarget = CardEffects.LandsOnTarget(card.Effect);
        Player boardOwner = onTarget ? target : owner;
        Control board = (boardOwner == _player1) ? _p1BoardContainer : _p2BoardContainer;

        // The vetoed card leaves the table. Burn it BEFORE the Veto card drops, so the eye follows
        // the card being destroyed rather than the one arriving - a score that ticks down on its
        // own tells the target nothing about WHICH card they just lost.
        if (destroyed != null)
            _ui.BurnCardView(destroyed, (target == _player1) ? _p1BoardContainer : _p2BoardContainer);

        boardOwner.ActiveCardsOnBoard.Add(card);
        _ui.InstantiateCardView(card, board);

        // A card that rewrote a drawn card mutated a Card object that is already face-up on a
        // board. Without this the board still reads 10 while the score has been paid at 2, which
        // is the one thing a card called Copy cannot afford to get wrong.
        if (CardEffects.RewritesDrawnCards(card.Effect))
        {
            _ui.RefreshCardFace(owner.LastDrawnCard, (owner == _player1) ? _p1BoardContainer : _p2BoardContainer);
            _ui.RefreshCardFace(target.LastDrawnCard, (target == _player1) ? _p1BoardContainer : _p2BoardContainer);
        }

        GD.Print(result.Narration);
        _ui.ShowEffectBanner(result.Narration); // the log is not on the table - the player has to SEE it

        // The ladder's promise, kept: you meet a card when it is used on you, and the game says
        // once what it was. Only the bot's cards - your own were introduced when you were dealt them.
        if (owner == _player2) QueueCoachMark(card, fromOpponent: true);
        _ui.Refresh();

        // A re-opened BOT has to be sent round again: ResolveTurn refuses to move while either
        // side can act, and nothing else would ever call the bot back. How it goes round - now, or
        // as a note for the turn already in flight - is the bot's own business (Bot.TurnReopened).
        if (reopened && _isVsBot && target == _player2) _bot.TurnReopened();

        return true;
    }

    /// True when a person is allowed to press Draw Card / Hold / a Modifier right now.
    /// (The bot thinking does NOT lock the human - both sides act at the same time.)
    private bool HumanCanAct()
    {
        if (!_isGameStarted || _gameState.IsGameOver || _setOverPending) return false;
        if (_recallOverlay != null && _recallOverlay.Visible) return false;
        if (RescueShowing) return false;
        return true;
    }

    /// True when this particular player can be driven by a person right now.
    private bool HumanCanActFor(Player player)
    {
        if (!HumanCanAct() || !player.CanAct) return false;
        if (_isVsBot && player == _player2) return false; // the bot drives itself
        return true;
    }

    // Each button knows which player it belongs to (the solo scene's shared pair is Player 1's),
    // so Player 2's row can only ever act for Player 2.
    private void OnDrawCardPressed(Player player) => FinishTurn(player, hold: false);
    private void OnHoldPressed(Player player) => FinishTurn(player, hold: true);

    /// A player is done with this turn: Draw Card keeps them in for the next turn, Hold takes them
    /// out for the rest of the set. Nothing is decided until the other player is done too.
    private void FinishTurn(Player player, bool hold)
    {
        if (!HumanCanActFor(player)) return;

        SetSelection(player, null); // a card that was only picked up is put back, not spent

        if (hold)
        {
            player.IsHolding = true;
            GD.Print($"{player.PlayerName} chose to HOLD at {player.CurrentScore}");
        }
        else
        {
            player.HasEndedTurn = true;
            GD.Print($"{player.PlayerName} ended the turn at {player.CurrentScore}");
        }

        ResolveTurn();
    }

    /// The set is over: at least one player finished the turn over the target (a bust), or
    /// both players are holding. Who busted is derived from the scores. Records the result, then
    /// shows an explanation that has to be acknowledged - the next set (or a restart, after the
    /// match) starts from that button.
    /// Finished local co-op matches this session. In memory only: relaunching resets it, so
    /// nobody sees an ad just for opening the game (monetization-spec.md §2).
    private static int _coopMatchesFinished;
    private const int CoopMatchesPerAd = 2;

    private void EndSet()
    {
        // A rescue card lasts the set it was given in, played or not (monetization-spec.md §3.4).
        _player1.DiscardRescueCards();

        int target = _gameState.TargetScore;
        int p1 = _player1.CurrentScore;
        int p2 = _player2.CurrentScore;
        bool p1Bust = p1 > target;
        bool p2Bust = p2 > target;

        int setWinner;
        if (p1Bust && p2Bust) setWinner = 0;
        else if (p1Bust) setWinner = 2;
        else if (p2Bust) setWinner = 1;
        else if (p1 == p2) setWinner = 0;
        else setWinner = (p1 > p2) ? 1 : 2; // both under the target: the higher score is closer

        // Why the set ended.
        bool anyBust = p1Bust || p2Bust;
        string why;
        if (p1Bust && p2Bust)
            why = $"Both players busted: {p1} and {p2} are over the target of {target}.";
        else if (p1Bust)
            why = $"{_player1.PlayerName} busted: {p1} is over the target of {target}.";
        else if (p2Bust)
            why = $"{_player2.PlayerName} busted: {p2} is over the target of {target}.";
        else
            why = $"Both players held.\n{_player1.PlayerName}: {p1}      {_player2.PlayerName}: {p2}";

        // What that means.
        string title;
        string buttonText;
        if (setWinner == 0)
        {
            title = "The set is a tie";
            why += "\nSame score, so the set is replayed.";
            buttonText = "Replay Set";
        }
        else
        {
            Player winner = (setWinner == 1) ? _player1 : _player2;
            _gameState.RecordSetWinner(setWinner);
            if (!anyBust) why += $"\n{winner.PlayerName} is closest to {target}.";
            title = $"{winner.PlayerName} wins the set!";
            buttonText = "Next Set";
        }

        Action next;
        if (_gameState.CheckMatchWinner(out int matchWinner))
        {
            Player champion = (matchWinner == 1) ? _player1 : _player2;
            int champWins = (matchWinner == 1) ? _gameState.SetsWonPlayer1 : _gameState.SetsWonPlayer2;
            int otherWins = (matchWinner == 1) ? _gameState.SetsWonPlayer2 : _gameState.SetsWonPlayer1;
            title = $"{champion.PlayerName} wins the match!";
            why += $"\n{champion.PlayerName} took the match {champWins} sets to {otherWins}.";
            why += ReportRunResult(matchWinner == 1, _gameState.SetsWonPlayer1);
            // A won match on a live run goes to the market and the deck before the next rung;
            // a loss (or the end of the ladder) just offers a fresh run. ReportRunResult above has
            // already banked the result, so RunActive/RunComplete describe what happens next.
            RunData run = _inRun ? RunData.Instance : null;
            bool runContinues = run != null && run.RunActive && !run.RunComplete;
            if (runContinues)
            {
                buttonText = "Continue";
                next = OpenIntermission;
            }
            else
            {
                // Nothing left to continue: the ladder is finished, the run was lost, or this was
                // a one-off local match. That is the end of something, so it goes to the start
                // menu rather than silently dealing the next thing - the menu is where the medals
                // and the record are, and where the next climb becomes a choice.
                buttonText = "Play Again";
                next = RestartToMenu;

                // Local co-op: a short ad the players can close, after every 2nd finished match
                // (monetization-spec.md §2). On the match-end screen, before anything else starts.
                if (!_isVsBot)
                {
                    _coopMatchesFinished++;
                    if (_coopMatchesFinished % CoopMatchesPerAd == 0)
                    {
                        Action afterAd = next;
                        next = () => AdService.ShowInterstitial(this, afterAd);
                    }
                }
            }
        }
        else
        {
            next = () =>
            {
                _setOverPending = false;
                StartNewSet();
            };
        }

        string whyOneLine = why.Replace('\n', ' ');
        GD.Print($"Set over: {title} ({whyOneLine})");

        _setOverPending = true;
        _ui.Refresh(); // locks every button and Modifier; sides show Bust! / Holding
        if (_setInfoLabel != null) _setInfoLabel.Text = title;
        ShowSetEnd(title, why, buttonText, next);
    }

    /// Banks the match result against the run and says what it was worth. The market and the deck
    /// open next (OpenIntermission), which is where those medals get spent.
    private string ReportRunResult(bool playerWon, int playerSetsWon)
    {
        RunData run = _inRun ? RunData.Instance : null;
        if (run == null) return string.Empty;

        int before = run.Medals;
        run.CompleteMatch(playerSetsWon, playerWon);

        if (!playerWon)
        {
            // The cards are the progress, and they survive - say so plainly, on the screen where
            // losing is most likely to make someone put the game down.
            if (run.EndlessStreak > 0 || run.Endless)
                return $"\n\nYour endless streak ends at {run.EndlessStreak} (best {run.EndlessBest})." +
                       EndlessBoardLine(run) +
                       $"\nEvery card you own ({run.Inventory.Count}) and your {run.Medals} medals are still yours.";
            return $"\n\nThe run ends here. Every card you have unlocked ({run.Inventory.Count}) is " +
                   $"still yours, along with {run.Medals} medals.\nPlay Again starts a fresh climb " +
                   $"from stage 1 with your deck intact.";
        }

        int earned = run.Medals - before;
        if (run.RunComplete)
            return $"\n\nYou earned {earned} medals - and you have cleared the whole ladder." +
                   "\nEndless mode is open on the start menu.";

        if (run.Endless)
            return $"\n\nStreak {run.EndlessStreak} (best {run.EndlessBest}). You earned {earned} medals." +
                   $"\nNext: target {run.CurrentTarget}." + FinaleRulesLine(run, "\n") +
                   "\nThe market is open first.";

        return $"\n\nYou earned {earned} medals ({run.Medals} banked)." +
               $"\nNext, stage {run.MatchNumber}: {run.CurrentOpponent}, target {run.CurrentTarget}." +
               FinaleRulesLine(run, "\n") +
               "\nThe market is open first.";
    }

    /// Where the streak just banked landed on the endless board, if it landed at all. CompleteMatch
    /// has already written it, so the run being reported on is row 0 when it is the new best.
    private static string EndlessBoardLine(RunData run)
    {
        List<RunData.EndlessScore> board = run.EndlessScores;
        if (board.Count == 0) return string.Empty;

        for (int i = 0; i < board.Count; i++)
        {
            if (board[i].Streak != run.EndlessStreak) continue;
            if (i == 0) return "\nThat is your best yet - it tops the endless board.";
            return $"\nThat is {Ordinal(i + 1)} on the endless board.";
        }
        return $"\nNot enough for the board - {board[board.Count - 1].Streak} is the score to beat.";
    }

    private static string Ordinal(int place) => place switch
    {
        1 => "first",
        2 => "second",
        3 => "third",
        4 => "fourth",
        5 => "fifth",
        _ => $"{place}th",
    };

    /// "Stage 3/10 - Silver" while a run is on; nothing otherwise. The rank is named here because
    /// the table is already wearing its colour - the words label what the player can see.
    /// "Rules: Copy + Shave" for the finale, prefixed; empty on any other rung.
    private static string FinaleRulesLine(RunData run, string prefix)
    {
        List<CardEffect> rolled = run?.CurrentRolledEffects;
        if (rolled == null || rolled.Count == 0) return string.Empty;
        List<string> names = new List<string>();
        foreach (CardEffect effect in rolled) names.Add(CardEffects.Label(effect));
        return $"{prefix}Rules: {string.Join(" + ", names)}";
    }

    private string RunHeader()
    {
        RunData run = _inRun ? RunData.Instance : null;
        if (run == null) return string.Empty;
        if (run.Endless) return $"Endless - streak {run.EndlessStreak} - ";
        return $"Stage {run.MatchNumber}/{RunData.LadderLength} - {run.CurrentRank.Name} - ";
    }

    // ------------------------------------------------------------------
    // The middle panel's two added lines
    // ------------------------------------------------------------------


    // ------------------------------------------------------------------
    // Debug row
    //
    // Behind OS.IsDebugBuild(), so it cannot ship: an exported build never builds these buttons.
    // Solo scene only - local 2-player has no run to jump around in.
    // ------------------------------------------------------------------
    private void BuildDebugRow()
    {
        if (!OS.IsDebugBuild()) return;
        if (_gameModeButton != null) return;

        Control column = _setInfoLabel?.GetParent() as Control;
        if (column == null) return;

        HBoxContainer row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 6);
        column.AddChild(row);
        _debugRows.Add(row);
        row.Visible = GameSettings.ShowDebugButtons;   // Options > Debug

        row.AddChild(OverlayUi.MakeLabel("debug", 12, OverlayUi.Muted));

        Button back = new Button { Text = "< Stage" };
        back.Pressed += () => DebugJumpStage(-1);
        row.AddChild(back);

        Button forward = new Button { Text = "Stage >" };
        forward.Pressed += () => DebugJumpStage(+1);
        row.AddChild(forward);

        Button wipe = new Button { Text = "Wipe Save" };
        wipe.Pressed += () =>
        {
            RunData.Instance?.DebugWipeSave();
            GD.Print("DEBUG: save wiped - collection, deck, medals and run are gone");
            RestartToMenu(); // there is no match left to go back to
        };
        row.AddChild(wipe);

        // Monetization switches (monetization-spec.md), on a second row so the first stays narrow.
        HBoxContainer adRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        adRow.AddThemeConstantOverride("separation", 6);
        column.AddChild(adRow);
        _debugRows.Add(adRow);
        adRow.Visible = GameSettings.ShowDebugButtons;

        Button rescue = new Button();
        void RescueText() => rescue.Text = _debugAlwaysRescue ? "Rescue 100%" : "Rescue 15%";
        RescueText();
        rescue.Pressed += () => { _debugAlwaysRescue = !_debugAlwaysRescue; RescueText(); };
        adRow.AddChild(rescue);

        Button fill = new Button();
        void FillText() => fill.Text = AdService.DebugSimulateNoFill ? "Ads: no fill" : "Ads: fill";
        FillText();
        fill.Pressed += () => { AdService.DebugSimulateNoFill = !AdService.DebugSimulateNoFill; FillText(); };
        adRow.AddChild(fill);

        Button noAds = new Button();
        void NoAdsText() => noAds.Text = PurchaseService.OwnsNoAds ? "No Ads: owned" : "No Ads: not owned";
        NoAdsText();
        noAds.Pressed += () => { PurchaseService.DebugSetOwned(!PurchaseService.OwnsNoAds); NoAdsText(); };
        adRow.AddChild(noAds);
    }

    /// Drops the run one rung either way and walks straight into that match, so a stage can be
    /// tested without climbing to it.
    private void DebugJumpStage(int delta)
    {
        RunData run = RunData.Instance;
        if (run == null) return;

        run.DebugJumpToStep(run.StepIndex + delta);
        run.AutoStartNextMatch = true;
        GD.Print($"DEBUG: jumped to stage {run.MatchNumber} ({run.CurrentOpponent}, target {run.CurrentTarget})");
        OnRestartPressed();
    }

    // ------------------------------------------------------------------
    // The intermission: market, then deck, then the next rung
    // ------------------------------------------------------------------
    private void BuildIntermissionOverlays()
    {
        // Only the single-player ladder has a run to spend medals on; local 2-player never does.
        if (_gameModeButton != null) return;

        _shopOverlay = new ShopOverlay();
        AddChild(_shopOverlay);
        _shopOverlay.Setup(_ui.CreateCardView);

        _deckOverlay = new DeckOverlay();
        AddChild(_deckOverlay);
        _deckOverlay.Setup(_ui.CreateCardView);
    }

    /// Market first (spend the medals just won), then the deck (choose the twelve those cards
    /// go into), then the next match.
    private void OpenIntermission()
    {
        if (_shopOverlay == null || _deckOverlay == null)
        {
            StartNextMatch();
            return;
        }

        // The deck screen works in smaller cards than the table: twelve slots and a collection
        // have to fit side by side on a phone in portrait.
        Vector2 deckCardSize = _ui.CardSize * 0.7f;
        _shopOverlay.Open(_ui.CardSize, () => _deckOverlay.Open(deckCardSize, StartNextMatch));
    }

    /// Reloading the scene is what resets the board, the scores and the set wins (the same path
    /// Restart takes); RunData is an autoload, so the run itself survives it.
    private void StartNextMatch()
    {
        if (RunData.Instance != null) RunData.Instance.AutoStartNextMatch = true;
        GetTree().ReloadCurrentScene();
    }


    /// What the middle panel says about the current deal.
    private string TurnStatusText()
    {
        bool p1 = _player1.CanAct;
        bool p2 = _player2.CanAct;
        if (_isVsBot)
        {
            if (p1 && p2) return "Bot thinking...";
            if (p1) return "Your move";
            if (p2) return "Waiting for bot";
            return "Dealing...";
        }
        if (p1 && p2) return "Both players: play or draw";
        if (p1) return "Waiting for P1";
        if (p2) return "Waiting for P2";
        return "Dealing...";
    }

    private string StatusFor(Player player)
    {
        bool over = player.CurrentScore > _gameState.TargetScore;

        if (_setOverPending && over) return "Bust!";
        if (_setOverPending) return player.IsHolding ? "Holding" : "Done";

        if (player.IsHolding) return "Holding";
        if (player.HasEndedTurn) return "Done - waiting";

        // A card is picked up: spell the arithmetic out. This is the game's teaching moment, so
        // it is shown as a full sum rather than just the answer.
        Card picked = SelectedFor(player);
        if (picked != null)
        {
            if (picked.Effect != CardEffect.None) return EffectPreview(player, picked);
            if (_table.IsRecallLocked(player, picked)) return "Just recalled - playable from your next turn";
            // A plain Modifier says nothing here: its result is shown on the score itself.
        }

        // Still acting this turn.
        if (over) return "Over target!"; // a warning, not a bust yet: play a minus Modifier before ending the turn
        if (_isVsBot && player == _player2) return "Thinking...";
        return "Your move";
    }

    /// The same teaching moment as the sum above, for a card whose arithmetic happens on the OTHER
    /// side of the table. Says what it would do, or says it cannot be played right now - never a
    /// sum of this player's score and a number that is not going to be added to it.
    private string EffectPreview(Player player, Card picked)
    {
        Player other = (player == _player1) ? _player2 : _player1;
        string name = CardEffects.Label(picked.Effect);

        string refusal = EffectRefusal(player, picked);
        if (refusal != null) return $"{name}: {refusal}";

        switch (picked.Effect)
        {
            case CardEffect.Copy:
            {
                int mine = player.LastDrawnCard?.Value ?? 0;
                int theirs = other.LastDrawnCard?.Value ?? 0;
                int after = player.CurrentScore - mine + theirs;
                return $"Your {mine} becomes a {theirs}: {player.CurrentScore} to {after}";
            }
            case CardEffect.Shave:
                return $"{other.PlayerName}: {other.CurrentScore} - 1 = {other.CurrentScore - 1}";
            case CardEffect.TradeTotals:
                return $"Trade Totals: {player.CurrentScore} and {other.CurrentScore} change places";
            case CardEffect.TradeHands:
                return $"Trade Hands: your {player.Modifiers.Count - 1} Modifiers for their {other.Modifiers.Count}";
            case CardEffect.Recall:
                return "Take a Modifier back - you can play it from your next turn";
            case CardEffect.Veto:
            {
                // EffectRefusal returned null above, so CanPlay said yes, so LastPlayedModifier is
                // a plain modifier they played this turn. Named with its sign, because vetoing a
                // minus card sends their score UP and the preview has to show that honestly.
                Card theirs = other.LastPlayedModifier;
                string theirSign = theirs.Value < 0 ? "-" : "+";
                return $"Destroy their {theirSign}{Math.Abs(theirs.Value)}: "
                     + $"{other.CurrentScore} back to {other.CurrentScore - theirs.Value}";
            }
        }

        return name;
    }


    // ------------------------------------------------------------------
    // Picking a card up
    // ------------------------------------------------------------------
    private Card SelectedFor(Player player) => (player == _player1) ? _p1SelectedCard : _p2SelectedCard;

    private void SetSelection(Player player, Card card)
    {
        if (player == _player1) _p1SelectedCard = card;
        else _p2SelectedCard = card;
    }

    private void ClearSelections()
    {
        _p1SelectedCard = null;
        _p2SelectedCard = null;
    }

    /// Drops any picked-up card that has since been played, or whose owner can no longer act.
    private void ValidateSelections()
    {
        if (_p1SelectedCard != null && (!_player1.Modifiers.Contains(_p1SelectedCard) || !HumanCanActFor(_player1)))
            _p1SelectedCard = null;
        if (_p2SelectedCard != null && (!_player2.Modifiers.Contains(_p2SelectedCard) || !HumanCanActFor(_player2)))
            _p2SelectedCard = null;
    }


    private void PlaySelectedCard(Player player)
    {
        Card card = SelectedFor(player);
        if (card == null || !HumanCanActFor(player)) return;

        SetSelection(player, null);

        // An effect card is not arithmetic on your own score - PlayModifierCard would quietly add
        // a Shave's Value of 1 to the score of whoever played it.
        if (card.Effect != CardEffect.None)
        {
            // Recall is the one effect whose outcome its OWNER picks, so it asks before it spends.
            // The card is not taken out of the hand until a choice is made - Cancel costs nothing.
            if (card.Effect == CardEffect.Recall && CanPlayEffect(player, card))
            {
                ShowRecallOverlay(player, card);
                return;
            }

            // A refused play puts the card back in the player's hand AND back under their finger,
            // so the status line keeps explaining why it would not go.
            if (!PlayEffectCard(player, card)) SetSelection(player, card);
            _ui.Refresh();
            return;
        }

        // A card that came back this turn through a Recall is not playable until the next one.
        if (_table.IsRecallLocked(player, card))
        {
            SetSelection(player, card); // keep it under their finger so the status line explains
            _ui.Refresh();
            return;
        }

        if (player.PlayModifierCard(card, _gameState))
        {
            _ui.InstantiateCardView(card, (player == _player1) ? _p1BoardContainer : _p2BoardContainer);
        }

        _ui.Refresh();
    }

    /// Swaps a picked-up "+/-" card between plus and minus. Nothing is spent - the sum in the
    /// status line just changes, so it can be flipped back and forth as often as the player likes.
    private void FlipSelectedValue(Player player)
    {
        Card card = SelectedFor(player);
        if (card == null || !HumanCanActFor(player) || !card.FlipValue()) return;

        _ui.SoundPlace();
        _ui.Refresh();
    }


    private void OnModifierCardPressed(Player player, Card card)
    {
        if (!HumanCanActFor(player)) return;

        // Second tap on the card already in hand plays it - the quick path for anyone who has
        // learned the game. The first tap only picks it up; nothing is spent yet.
        if (SelectedFor(player) == card)
        {
            PlaySelectedCard(player);
            return;
        }

        SetSelection(player, card);
        _ui.SoundSlide();
        _ui.Refresh();
    }


    // When P2's side is mirrored, every card shows its value twice - like the corner indices on
    // a real playing card: once in the top half and once upside down in the bottom half - so
    // both players can read every card. Otherwise a single centred value is used.
    // ------------------------------------------------------------------
    // Card faces: corners and pips (playtest feedback, 2026-09-14)
    //
    // The 55+ blackjack players could not read the table. Two changes, both taken straight from
    // how an ordinary playing card works: the number sits in two OPPOSITE corners, and the middle
    // of a main-deck card carries pips.
    //
    // The corners also replace what LabelMinus used to do on a mirrored board - a second,
    // upside-down copy of the number in the lower half, so the player across the table could read
    // it. Two opposite corners do that permanently, for every card, in both scenes, and they leave
    // the middle of the card free for the pips. LabelMinus is now ONLY the minus half of a "+/-"
    // card, which is the one job the corners cannot do.
    // ------------------------------------------------------------------


    // ------------------------------------------------------------------
    // Reading the table at arm's length (playtest feedback, 2026-09-14)
    // ------------------------------------------------------------------


    private Control _tableMenuOverlay;
    private VBoxContainer _tableMenuBox;

    private void BuildTableMenu()
    {
        Node systemRow = _restartButton?.GetParent();
        Control column = systemRow?.GetParent() as Control;
        if (column == null) return;

        _tableMenuOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(_tableMenuOverlay);
        _tableMenuOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        OverlayUi.AddDim(_tableMenuOverlay);

        VBoxContainer box = OverlayUi.AddPanel(_tableMenuOverlay, contentMargin: 30, separation: 12);
        box.AddChild(OverlayUi.MakeLabel("Menu", MenuTitleFont));

        Button howTo = new Button { Text = "How to Play" };
        howTo.Pressed += () => { HideTableMenu(); ShowHowToPlay(); };
        box.AddChild(howTo);

        // Closing Options lands back on this menu, where the player opened it from.
        Button options = new Button { Text = "Options" };
        options.Pressed += () => { HideTableMenu(); OpenOptions(ShowTableMenu); };
        box.AddChild(options);

        // Restart, Exit and the mirror toggle MOVE rather than being rebuilt here. They are
        // exported nodes whose signals are already connected in _Ready, and a rebuilt copy would
        // need a second connection to the same handlers - two buttons, one of them dead.
        if (_mirrorToggle != null)
        {
            _mirrorToggle.GetParent()?.RemoveChild(_mirrorToggle);
            box.AddChild(_mirrorToggle);
        }
        if (_exitButton != null) _exitButton.Text = "Main Menu";
        foreach (Button moved in new[] { _restartButton, _exitButton })
        {
            if (moved == null) continue;
            moved.GetParent()?.RemoveChild(moved);
            box.AddChild(moved);
        }

        // Replaying reloads the match, because the walkthrough is staged into the turn - so it
        // has to be decided before the cards are dealt, not after. The static survives the reload.
        Button replay = new Button { Text = "Replay the tutorial" };
        replay.Pressed += () =>
        {
            HideTableMenu();
            _pendingTutorial = true;
            RunData.Instance?.ReplayTutorial();
            OnRestartPressed();
        };
        box.AddChild(replay);

        Button close = new Button { Text = "Back to the table" };
        close.Pressed += HideTableMenu;
        box.AddChild(close);

        _tableMenuBox = box; // re-sized on every open: MenuButtonWidth follows the viewport

        // ...and the one button left on the table, in the slot the Restart / Exit row had.
        Button open = new Button { Text = "Menu" };
        open.Pressed += ShowTableMenu;
        column.AddChild(open);
        if (systemRow.GetParent() == column) column.MoveChild(open, systemRow.GetIndex());
    }

    /// One size for every button on a full-screen menu. See the MenuButton* constants for why
    /// this can be generous where the table cannot.
    private void StyleMenuButton(Button button)
    {
        if (button == null) return;
        button.AddThemeFontSizeOverride("font_size", MenuButtonFont);
        button.CustomMinimumSize = new Vector2(MenuButtonWidth, MenuButtonHeight);
    }

    private void ShowTableMenu()
    {
        if (_tableMenuOverlay == null) return;

        // Every button in here, on every open - including Restart, Main Menu and the mirror
        // toggle, which were MOVED in from the table and so arrive carrying the table's sizing.
        // Done here rather than at build time because MenuButtonWidth follows the viewport, and
        // the viewport changes with the orientation and with how far portrait has zoomed in.
        if (_tableMenuBox != null)
            foreach (Node child in _tableMenuBox.GetChildren())
                if (child is Button menuButton) StyleMenuButton(menuButton);

        // The toggle only means anything with two people at one device.
        if (_mirrorToggle != null) _mirrorToggle.Visible = !_isVsBot;

        MoveChild(_tableMenuOverlay, GetChildCount() - 1); // above every other overlay
        _tableMenuOverlay.Visible = true;
    }

    private void HideTableMenu()
    {
        if (_tableMenuOverlay != null) _tableMenuOverlay.Visible = false;
    }


    // ------------------------------------------------------------------
    // Recall: choosing which spent card comes back
    //
    // Built in code from OverlayUi's pieces, like every other overlay here, so both the solo and
    // the 2-player table get it with no NodePath wiring.
    //
    // It asks rather than picking for you. Always returning the most recently spent card would
    // need no screen at all, and it would turn the interesting decision - spend a +4 early KNOWING
    // you can have it again - into a lookup.
    // ------------------------------------------------------------------
    private Control _recallOverlay;
    private VBoxContainer _recallBox;
    private Player _recallChooser;
    private Card _recallCard;

    private void ShowRecallOverlay(Player chooser, Card recallCard)
    {
        _recallChooser = chooser;
        _recallCard = recallCard;

        if (_recallOverlay == null)
        {
            _recallOverlay = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
            AddChild(_recallOverlay); // scene root, after GameUI, so it draws and takes input on top
            _recallOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            OverlayUi.AddDim(_recallOverlay);
            _recallBox = OverlayUi.AddPanel(_recallOverlay);
        }

        OverlayUi.ClearChildren(_recallBox);

        _recallBox.AddChild(OverlayUi.MakeLabel("Recall", 30));
        _recallBox.AddChild(OverlayUi.MakeLabel(
            "Take one spent Modifier back.\nYou can play it from your next turn.", 16, OverlayUi.Muted));

        HBoxContainer row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        _recallBox.AddChild(row);

        foreach (Card spent in chooser.SpentCards)
        {
            if (!CardEffects.IsPlainModifier(spent)) continue;
            Card choice = spent; // capture per iteration, not the loop variable
            row.AddChild(OverlayUi.CardButton(_ui.CreateCardView(choice, _ui.ModifierCardSize), _ui.ModifierCardSize,
                () => OnRecallChosen(choice)));
        }

        Button cancel = new Button { Text = "Cancel" };
        cancel.Pressed += HideRecallOverlay;
        _recallBox.AddChild(cancel);

        // Mirrored 2-player: Player 2 reads the table upside down, so their chooser does too.
        if (_recallOverlay.GetChildCount() > 1 && _recallOverlay.GetChild(1) is Control panel)
        {
            panel.PivotOffset = panel.Size / 2f;
            panel.RotationDegrees = (_ui.IsMirrored && chooser == _player2) ? 180f : 0f;
        }

        _recallOverlay.Visible = true;
        _ui.Refresh();
    }

    private void OnRecallChosen(Card chosen)
    {
        Player chooser = _recallChooser;
        Card recallCard = _recallCard;
        HideRecallOverlay();

        if (chooser == null || recallCard == null) return;

        // A refused play puts the card back under their finger with the reason showing, exactly as
        // every other effect card does.
        if (!PlayEffectCard(chooser, recallCard, chosen)) SetSelection(chooser, recallCard);
        _ui.Refresh();
    }

    private void HideRecallOverlay()
    {
        if (_recallOverlay != null) _recallOverlay.Visible = false;
        _recallChooser = null;
        _recallCard = null;
        _ui.Refresh();
    }

    // ------------------------------------------------------------------
    // Set-end overlay
    //
    // A full-screen layer over the table (blocks every tap underneath) with a centred panel:
    // title, why the set ended, and one button. In mirrored 2-player there's also an
    // upside-down copy of the text at the top of the panel, nearest Player 2. Built in code so
    // both scenes get it without any NodePath wiring.
    // ------------------------------------------------------------------
    private Control _setEndOverlay;
    private Label _setEndTitle;
    private Label _setEndBody;
    private Button _setEndButton;
    private Control _setEndFlippedHolder;   // plain Control: containers reset a child's rotation, holders don't
    private VBoxContainer _setEndFlippedBox; // the node that is rotated 180 degrees
    private Label _setEndFlippedTitle;
    private Label _setEndFlippedBody;
    private HSeparator _setEndDivider;
    private Control _setEndSpacerTop;
    private Control _setEndSpacerBottom;
    private PanelContainer _setEndPanel;
    private VBoxContainer _setEndBox;
    private Action _setEndAction;

    /// Pass 23: "set won text is much better, but ... also increase text size" (Alexander,
    /// 2026-09-17). This panel is read once per set, from wherever the player is sitting, and it
    /// is the only thing on screen while it is up - so it can afford to be the biggest text in
    /// the game.
    private const int SetEndTitleFont = 42;          // was 30
    private const int SetEndBodyFont = 32;           // was 22
    private const int SetEndTitleFontMirrored = 46;  // was 36
    private const int SetEndBodyFontMirrored = 34;   // was 26
    private const int SetEndButtonFont = 34;
    /// The gap either side of the divider when two copies share the panel.
    private const float SetEndMirrorGap = 34f;

    private void BuildSetEndOverlay()
    {
        _setEndOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(_setEndOverlay); // on the scene root, after GameUI, so it draws (and gets input) on top
        _setEndOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        ColorRect dim = new ColorRect { Color = new Color(0, 0, 0, 0.5f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _setEndOverlay.AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        PanelContainer panel = new PanelContainer();
        StyleBoxFlat style = new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.14f, 0.2f, 0.98f),
            BorderColor = new Color(0.55f, 0.65f, 0.8f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(12);
        style.SetContentMarginAll(28);
        panel.AddThemeStyleboxOverride("panel", style);
        _setEndOverlay.AddChild(panel);
        // Anchored to the centre with zero offsets: a Control grows to its minimum size, and with
        // grow "both" it stays centred, so the panel always hugs its content.
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;

        VBoxContainer box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 14);
        panel.AddChild(box);
        _setEndPanel = panel;
        _setEndBox = box;

        // Player 2's upside-down copy (mirrored 2-player only). Same pattern as P2Holder/P2Rotator.
        _setEndFlippedHolder = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_setEndFlippedHolder);
        _setEndFlippedBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _setEndFlippedBox.AddThemeConstantOverride("separation", 6);
        _setEndFlippedHolder.AddChild(_setEndFlippedBox);
        _setEndFlippedBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _setEndFlippedBox.Resized += () =>
        {
            _setEndFlippedBox.PivotOffset = _setEndFlippedBox.Size / 2f;
            _setEndFlippedBox.RotationDegrees = 180f;
        };
        _setEndFlippedTitle = MakeOverlayLabel(SetEndTitleFont);
        _setEndFlippedBody = MakeOverlayLabel(SetEndBodyFont);
        _setEndFlippedBox.AddChild(_setEndFlippedTitle);
        _setEndFlippedBox.AddChild(_setEndFlippedBody);
        // Mirrored only: a gap either side of the divider so the two copies read as two blocks
        // rather than one. Pass 22 made these EXPANDING, inside a panel stretched to 86% x 62% of
        // the screen, which threw each copy out to its own edge; Alexander, 2026-09-17: "centre
        // the text instead of having them at the edges". So they are FIXED gaps now, the panel
        // hugs its content again, and the whole block sits in the middle of the screen with each
        // player's copy the right way up for them.
        _setEndSpacerTop = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_setEndSpacerTop);
        _setEndDivider = new HSeparator { Visible = false };
        box.AddChild(_setEndDivider);
        _setEndSpacerBottom = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_setEndSpacerBottom);

        _setEndTitle = MakeOverlayLabel(SetEndTitleFont);
        _setEndBody = MakeOverlayLabel(SetEndBodyFont);
        box.AddChild(_setEndTitle);
        box.AddChild(_setEndBody);

        _setEndButton = new Button { Text = "Next Set" };
        _setEndButton.AddThemeFontSizeOverride("font_size", SetEndButtonFont);
        _setEndButton.CustomMinimumSize = new Vector2(260, 84);
        _setEndButton.Pressed += OnSetEndButtonPressed;
        box.AddChild(_setEndButton);
    }

    private static Label MakeOverlayLabel(int fontSize)
    {
        Label label = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }

    private void ShowSetEnd(string title, string why, string buttonText, Action onAcknowledged)
    {
        _setEndAction = onAcknowledged;
        if (_setEndOverlay == null)
        {
            onAcknowledged?.Invoke(); // overlay failed to build: don't strand the game
            return;
        }

        // Make it visible first: minimum sizes are only reliable for nodes visible in the tree,
        // and everything below resolves in the same frame before it is drawn.
        MoveChild(_setEndOverlay, GetChildCount() - 1); // above any stray animation card
        _setEndOverlay.Visible = true;

        _setEndTitle.Text = title;
        _setEndBody.Text = why;
        _setEndButton.Text = buttonText;

        bool mirrored = _ui.IsMirrored;
        _setEndFlippedHolder.Visible = mirrored;
        _setEndDivider.Visible = mirrored;
        _setEndSpacerTop.Visible = mirrored;
        _setEndSpacerBottom.Visible = mirrored;

        // Centred, both forms: the panel hugs its content and the content sits in the middle of
        // the screen. Mirrored only adds the two fixed gaps and the divider between the copies.
        _setEndPanel.CustomMinimumSize = Vector2.Zero;
        float gap = mirrored ? SetEndMirrorGap : 0f;
        _setEndSpacerTop.CustomMinimumSize = new Vector2(0, gap);
        _setEndSpacerBottom.CustomMinimumSize = new Vector2(0, gap);
        _setEndBox.AddThemeConstantOverride("separation", mirrored ? 18 : 16);
        int titleFont = mirrored ? SetEndTitleFontMirrored : SetEndTitleFont;
        int bodyFont = mirrored ? SetEndBodyFontMirrored : SetEndBodyFont;
        // One column width for every line, so both copies are the same block and each line is
        // centred in it. The body is a sentence, and at this size a long one would otherwise push
        // the panel wider than the phone, so it wraps instead.
        float wrap = Mathf.Clamp(GetViewport().GetVisibleRect().Size.X * 0.78f, 340f, 620f);
        foreach (Label heading in new[] { _setEndTitle, _setEndFlippedTitle })
        {
            heading.AddThemeFontSizeOverride("font_size", titleFont);
            heading.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            heading.CustomMinimumSize = new Vector2(wrap, 0);
        }
        foreach (Label text in new[] { _setEndBody, _setEndFlippedBody })
        {
            text.AddThemeFontSizeOverride("font_size", bodyFont);
            text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            text.CustomMinimumSize = new Vector2(wrap, 0);
        }
        _setEndFlippedBox.AddThemeConstantOverride("separation", mirrored ? 12 : 6);
        if (mirrored)
        {
            _setEndFlippedTitle.Text = title;
            _setEndFlippedBody.Text = why;
            // The holder reports 0x0 on its own; give it the rotated block's footprint.
            _setEndFlippedHolder.CustomMinimumSize = _setEndFlippedBox.GetCombinedMinimumSize();
        }
        CallDeferred(MethodName.UpdateSetEndFlippedSize); // re-measure once the first layout pass has run
    }

    private void UpdateSetEndFlippedSize()
    {
        // Hug the content again (a panel never shrinks by itself after being mirrored-size) and
        // stay centred.
        if (_setEndPanel != null)
        {
            _setEndPanel.ResetSize();
            _setEndPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
            _setEndPanel.GrowHorizontal = Control.GrowDirection.Both;
            _setEndPanel.GrowVertical = Control.GrowDirection.Both;
        }
        if (_setEndFlippedHolder == null || !_setEndFlippedHolder.Visible) return;
        _setEndFlippedHolder.CustomMinimumSize = _setEndFlippedBox.GetCombinedMinimumSize();
        _setEndFlippedBox.PivotOffset = _setEndFlippedBox.Size / 2f;
        _setEndFlippedBox.RotationDegrees = 180f;
    }

    private void OnSetEndButtonPressed()
    {
        _setEndOverlay.Visible = false;
        Action action = _setEndAction;
        _setEndAction = null;
        action?.Invoke();
    }

    // ------------------------------------------------------------------
    // The start menu
    //
    // The front door. Before this the game opened straight onto a live-looking table with an empty
    // board, a dropdown and a Start button - which named neither the game nor the fact that there
    // was a climb waiting halfway up the ladder, and which asked for TWO presses to reach the bot:
    // one to change scene, another in the scene it changed to.
    //
    // Built in code and over the table, like every other overlay here (see OverlayUi), so both
    // scenes get it with no NodePath wiring. It is the only screen that knows about both scenes:
    // the ladder lives in the solo scene and the mirrored face-to-face table in the other, so
    // choosing a mode IS choosing a scene, and the menu does that itself rather than making the
    // player discover it.
    // ------------------------------------------------------------------

    /// Set just before ChangeSceneToFile sends the player to the two-player table, and read by the
    /// _Ready on the other side, so they land in a game rather than on a second front door.
    ///
    /// A static rather than a field on RunData, which is where AutoStartNextMatch lives: RunData is
    /// the RUN, and local 2-player never touches a run - putting this there would be the first
    /// thing to contradict that file's opening line. A static outlives ChangeSceneToFile for the
    /// same reason the autoload does, which is the whole reason either of them works.
    private static bool _pendingLocal2Player;

    private Control _startMenuOverlay;
    private VBoxContainer _startMenuBox;
    private Button _newRunButton;
    private bool _newRunArmed; // "New Run" over an unfinished climb asks a second time
    private bool _endlessArmed; // ...and so does Endless

    /// Opaque, and a deeper shade of the table's own felt so the menu still reads as this game.
    private static readonly Color MenuBackdrop = new Color(0.04f, 0.10f, 0.07f);

    private void BuildStartMenu()
    {
        _startMenuOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(_startMenuOverlay);
        _startMenuOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // OPAQUE, not OverlayUi.AddDim (Alexander, 2026-09-13). Every other overlay in the game
        // sits on top of a live table and wants it showing through - that is the point of the dim,
        // and why the intermission is an overlay rather than a scene change. This one is the screen
        // BEFORE there is a table, and a dealt hand behind it says a game is already running.
        ColorRect backdrop = new ColorRect { Color = MenuBackdrop, MouseFilter = Control.MouseFilterEnum.Ignore };
        _startMenuOverlay.AddChild(backdrop);
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _startMenuBox = OverlayUi.AddPanel(_startMenuOverlay, contentMargin: 32, separation: 12);

        _collectionOverlay = new CollectionOverlay();
        AddChild(_collectionOverlay);
        _collectionOverlay.Setup(_ui.CreateCardView);
    }

    // ------------------------------------------------------------------
    // The collection log
    // ------------------------------------------------------------------
    private CollectionOverlay _collectionOverlay;

    /// Fixed rather than scaled with the table: six across has to fit a phone held upright.
    private static readonly Vector2 CollectionCardSize = TableUi.BaseCardSize * 0.65f;

    private void OpenCollection()
    {
        // Closing refreshes the menu (the count on its button) and the deck back (the toggle).
        _collectionOverlay?.Open(CollectionCardSize, () => { FillStartMenu(); _ui.ApplyRankTheme(); });
    }

    /// Plain and flip-value Modifiers only; effect cards are marked when a coach-mark explains
    /// them. Ladder only - local 2-player deals random hands and has no profile to write to.
    private void NoteModifierMet(Card card)
    {
        if (!_inRun || RunData.Instance == null) return;
        if (RunData.Instance.MarkPlainModifierMet(card)) AnnounceCollectionComplete();
    }

    private void AnnounceCollectionComplete()
    {
        _ui.ShowEffectBanner("Collection complete! Your deck now has a gilded back.");
        _ui.ApplyRankTheme();
    }

    private void ShowStartMenu()
    {
        if (_startMenuOverlay == null) return;

        FillStartMenu();
        _startMenuOverlay.Visible = true;

        // The menu replaces them both, and they are behind an opaque backdrop anyway. Leaving them
        // live is two ways to do one thing, and the dropdown's answer is not the menu's.
        if (_startButton != null) _startButton.Visible = false;
        if (_gameModeButton != null) _gameModeButton.Visible = false;
    }

    private void HideStartMenu()
    {
        if (_startMenuOverlay != null) _startMenuOverlay.Visible = false;
    }

    /// Rebuilt on every show rather than once, because what it has to say changes: whether there is
    /// a climb to continue, which rung it is on, and what the player has banked.
    private void FillStartMenu()
    {
        OverlayUi.ClearChildren(_startMenuBox);
        _newRunArmed = false;

        // The project's own name, so renaming the game renames this too instead of leaving a second
        // copy of the title to go stale.
        string title = ProjectSettings.GetSetting("application/config/name").AsString();
        if (string.IsNullOrWhiteSpace(title)) title = "Card Game";
        _startMenuBox.AddChild(OverlayUi.MakeLabel(title, MenuTitleFont));

        RunData run = RunData.Instance;
        bool runInProgress = run != null && run.RunActive && !run.RunComplete;

        Label blurb = OverlayUi.MakeLabel(
            runInProgress ? "A climb is in progress." : "Climb the ladder, or play someone across the table.",
            MenuNoteFont, OverlayUi.Muted);
        blurb.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        blurb.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
        _startMenuBox.AddChild(blurb);
        _startMenuBox.AddChild(MenuSpacer());

        // First, and named with the rung, because a player who left mid-ladder came back for this
        // one thing and should not have to guess which button keeps their climb.
        if (runInProgress && run.Endless)
        {
            AddMenuButton($"Continue - Endless, streak {run.EndlessStreak}",
                          $"target {run.CurrentTarget}{FinaleRulesLine(run, "   -   ")}",
                          () => MenuStartRun(fresh: false));
        }
        else if (runInProgress)
        {
            AddMenuButton($"Continue - Match {run.MatchNumber} of {RunData.LadderLength}",
                          $"{run.CurrentOpponent}   -   target {run.CurrentTarget}",
                          () => MenuStartRun(fresh: false));
        }

        // Over an unfinished climb this button throws the climb away, so it asks twice. A second
        // tap is the cheapest confirmation there is and it costs no second overlay.
        _newRunButton = AddMenuButton(
            runInProgress ? "New Run" : "Start a Run",
            runInProgress ? "Gives up the climb above. Your cards and medals stay." : null,
            () =>
            {
                if (runInProgress && !_newRunArmed)
                {
                    _newRunArmed = true;
                    _newRunButton.Text = "New Run - tap again to give up the climb";
                    return;
                }
                MenuStartRun(fresh: true);
            });

        // No explanatory line under either of the two plain modes (Alexander, 2026-09-13): a menu
        // that describes its own buttons is a menu that does not trust them. The one note that
        // stays is the New Run warning, which is not a description - it is a consequence.
        // Endless: earned by clearing the ladder once. Over a run in progress it gives that run up,
        // so it asks twice, exactly as New Run does.
        if (run != null && run.EndlessUnlocked)
        {
            _endlessArmed = false;
            Button endless = null;
            endless = AddMenuButton(
                run.EndlessBest > 0 ? $"Endless   (best streak {run.EndlessBest})" : "Endless",
                null,
                () =>
                {
                    if (runInProgress && !_endlessArmed)
                    {
                        _endlessArmed = true;
                        endless.Text = "Endless - tap again to give up the run above";
                        return;
                    }
                    RunData.Instance.StartEndless();
                    MenuStartRun(fresh: false);
                });

            // Only once there is something on it. An empty board on a player who has unlocked
            // endless but never played it is a row that explains nothing.
            if (run.EndlessScores.Count > 0)
                AddMenuButton("Endless Scores", null, FillEndlessScores);
        }

        AddMenuButton("Local 2-Player", null, FillLocal2PlayerSetup);

        _startMenuBox.AddChild(MenuSpacer());
        AddMenuButton("How to Play", null, ShowHowToPlay);
        AddMenuButton("Options", null, () => OpenOptions());
        if (run != null)
            AddMenuButton($"Collection   {run.CollectionFound}/{RunData.CollectionKeys.Length}", null, OpenCollection);

        // Quit everywhere but iOS (Alexander, 2026-09-16: "start menu should have quit game").
        // Android allows an app to close itself; Apple's review guidelines reject a quit button,
        // and iOS apps are left to the home gesture.
        if (!OS.HasFeature("ios")) AddMenuButton("Quit Game", null, () => GetTree().Quit());

        // The proof that a lost run did not erase anything - which is the promise the run makes,
        // and the one place the player can be shown it before deciding to climb again.
        if (run != null && (run.Medals > 0 || run.FurthestStep > 0))
        {
            _startMenuBox.AddChild(MenuSpacer());
            Label banked = OverlayUi.MakeLabel(
                $"{run.Medals} medals   -   {run.Inventory.Count} cards owned   -   best: match {run.FurthestStep + 1}",
                MenuNoteFont, OverlayUi.Muted);
            banked.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            banked.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
            _startMenuBox.AddChild(banked);
        }
    }

    /// One row of the menu: a wide button, and optionally a line under it saying what it does. The
    /// note is a separate label rather than a second line inside the button so that arming the New
    /// Run button has exactly one string to rewrite.
    private Button AddMenuButton(string text, string note, Action onPressed)
    {
        Button button = new Button { Text = text, CustomMinimumSize = new Vector2(MenuButtonWidth, MenuButtonHeight) };
        button.AddThemeFontSizeOverride("font_size", MenuButtonFont);
        if (onPressed != null) button.Pressed += onPressed;
        _startMenuBox.AddChild(button);

        if (!string.IsNullOrEmpty(note))
        {
            Label label = OverlayUi.MakeLabel(note, MenuNoteFont, OverlayUi.Muted);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
            _startMenuBox.AddChild(label);
        }

        return button;
    }

    // Pass 25 (Alexander, 2026-09-17: "menu text needs to be significantly enlarged"). These are
    // FREE in a way the table's fonts are not: every menu is a full-screen overlay over the table
    // rather than part of MainLayout, so EnsureLayoutFits never measures them and nothing shrinks
    // to pay for them. The only ceiling is the panel fitting a phone held upright, which at a
    // 720-wide base leaves room for a 420 button and its margins.
    /// The widest a menu button may get, and how much air is left either side of it. The width
    /// is CLAMPED to the viewport rather than fixed, because portrait enlarges the whole UI when
    /// there is room (EnsureLayoutFits) - which shrinks the design-pixel viewport, sometimes well
    /// below the 720 base. A fixed 420 would hang off both edges of a table zoomed that far in.
    private const float MenuButtonWidthMax = 460f; // was a fixed 340
    private const float MenuSideGutter = 44f;
    private float MenuButtonWidth => Mathf.Clamp(
        GetViewport().GetVisibleRect().Size.X - 2f * MenuSideGutter, 240f, MenuButtonWidthMax);
    private const float MenuButtonHeight = 62; // was 44
    private const int MenuButtonFont = 26;     // was 18
    private const int MenuTitleFont = 52;      // was 40
    private const int MenuNoteFont = 17;       // was 12
    private const int MenuHeadingFont = 34;    // a sub-page's title (Local 2-Player, Endless Scores)
    private const int MenuSectionFont = 26;    // a heading inside a sub-page (Target, Specials)

    private static Control MenuSpacer() => new Control { CustomMinimumSize = new Vector2(0, 8) };

    /// The ladder lives in the solo scene. From the two-player table that is a scene change, and
    /// the note RunData already keeps for the deck screen is what makes the new scene deal itself.
    private void MenuStartRun(bool fresh)
    {
        if (fresh) RunData.Instance?.StartNewRun();

        if (_gameModeButton != null)
        {
            if (RunData.Instance != null) RunData.Instance.AutoStartNextMatch = true;
            GetTree().ChangeSceneToFile("res://solo_table_scene.tscn");
            return;
        }

        HideStartMenu();
        OnStartButtonPressed();
    }

    // ------------------------------------------------------------------
    // Local 2-player setup (roadmap, from the mobile playtest group's ask for effect cards in
    // local 2-player). Two choices before the deal: the target, and whether the special Modifiers
    // are in the hands. Static for the same reason _pendingLocal2Player is - they have to survive
    // the scene change and Restart's reload - and, like it, never saved to disk: the menu
    // remembers the last choice for this launch only.
    // ------------------------------------------------------------------
    private static readonly int[] Local2PlayerTargets = { 18, 20, 23 };
    private static int _local2PlayerTarget = 20;
    private static bool _local2PlayerSpecials;

    /// The start menu's second page. Reuses the menu panel rather than opening another overlay,
    /// so Back is a refill and there is no second panel to stack or dismiss.
    private void FillLocal2PlayerSetup()
    {
        // Only what the player has met in single player is offered (Alexander, 2026-09-16):
        // a target once a ladder rung has been played at it, a special once its rung has been
        // reached. Nothing unlocked means nothing to choose, so the page is skipped.
        List<int> targets = UnlockedLocalTargets();
        List<CardEffect> effects = UnlockedLocalSpecials();
        SanitizeLocal2PlayerChoices(targets, effects);
        if (targets.Count <= 1 && effects.Count == 0)
        {
            MenuStartLocal2Player();
            return;
        }

        OverlayUi.ClearChildren(_startMenuBox);
        _startMenuBox.AddChild(OverlayUi.MakeLabel("Local 2-Player", MenuHeadingFont));
        _startMenuBox.AddChild(MenuSpacer());

        if (targets.Count > 1)
        {
            _startMenuBox.AddChild(OverlayUi.MakeLabel("Target", MenuSectionFont));
            HBoxContainer targetRow = AddChoiceRow();
            foreach (int target in targets)
            {
                int value = target;
                AddChoice(targetRow, value.ToString(), _local2PlayerTarget == value,
                          () => _local2PlayerTarget = value);
            }
        }

        if (effects.Count > 0)
        {
            _startMenuBox.AddChild(MenuSpacer());
            _startMenuBox.AddChild(OverlayUi.MakeLabel("Special Modifiers", MenuSectionFont));
            HBoxContainer specials = AddChoiceRow();
            AddChoice(specials, "Off", !_local2PlayerSpecials, () => _local2PlayerSpecials = false);
            AddChoice(specials, "On", _local2PlayerSpecials, () => _local2PlayerSpecials = true);

            List<string> names = new List<string>();
            foreach (CardEffect effect in effects) names.Add(CardEffects.Label(effect));
            Label note = OverlayUi.MakeLabel(
                $"On: each hand has one special Modifier - {string.Join(", ", names)}.",
                MenuNoteFont, OverlayUi.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.HorizontalAlignment = HorizontalAlignment.Center;
            note.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
            _startMenuBox.AddChild(note);
        }

        _startMenuBox.AddChild(MenuSpacer());
        AddMenuButton("Deal", null, MenuStartLocal2Player);
        AddMenuButton("Back", null, FillStartMenu);
    }

    /// The endless board (pass 24). A page of the start menu rather than an overlay of its own:
    /// it is read from the menu, it is five rows long, and FillLocal2PlayerSetup already proved
    /// the pattern - swap the menu's contents, and Back swaps them straight back.
    private void FillEndlessScores()
    {
        RunData run = RunData.Instance;
        if (run == null) { FillStartMenu(); return; }

        OverlayUi.ClearChildren(_startMenuBox);
        _startMenuBox.AddChild(OverlayUi.MakeLabel("Endless Scores", MenuHeadingFont));
        _startMenuBox.AddChild(OverlayUi.MakeLabel(
            "How many matches in a row, before the run ended.", MenuNoteFont, OverlayUi.Muted));
        _startMenuBox.AddChild(MenuSpacer());

        for (int i = 0; i < run.EndlessScores.Count; i++)
        {
            RunData.EndlessScore score = run.EndlessScores[i];
            bool best = i == 0;

            HBoxContainer row = new HBoxContainer();
            row.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
            row.AddThemeConstantOverride("separation", 10);
            _startMenuBox.AddChild(row);

            Label place = OverlayUi.MakeLabel($"{i + 1}.", 26, best ? OverlayUi.MedalGold : OverlayUi.Muted);
            place.CustomMinimumSize = new Vector2(44, 0);
            place.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(place);

            Label streak = OverlayUi.MakeLabel($"{score.Streak}", 34, best ? OverlayUi.MedalGold : Colors.White);
            streak.CustomMinimumSize = new Vector2(72, 0);
            streak.HorizontalAlignment = HorizontalAlignment.Left;
            row.AddChild(streak);

            Label when = OverlayUi.MakeLabel(
                score.UnixTime > 0 ? Time.GetDateStringFromUnixTime(score.UnixTime) : string.Empty,
                MenuNoteFont, OverlayUi.Muted);
            when.HorizontalAlignment = HorizontalAlignment.Left;
            when.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(when);
        }

        _startMenuBox.AddChild(MenuSpacer());
        Label footer = OverlayUi.MakeLabel(
            $"Best streak {run.EndlessBest}.   The rules are re-rolled every match; "
            + $"past a streak of {RunData.EndlessThirdRuleStreak} the opponent carries three specials.",
            MenuNoteFont, OverlayUi.Muted);
        footer.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        footer.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
        _startMenuBox.AddChild(footer);

        _startMenuBox.AddChild(MenuSpacer());
        AddMenuButton("Back", null, FillStartMenu);
    }

    private const int DefaultLocalTarget = 20;

    /// 20 always; 18 and 23 once a ladder rung at that target has been reached.
    private static List<int> UnlockedLocalTargets()
    {
        List<int> targets = new List<int>();
        foreach (int target in Local2PlayerTargets)
        {
            if (target == DefaultLocalTarget || (RunData.Instance?.TargetReached(target) ?? false))
                targets.Add(target);
        }
        return targets;
    }

    private static List<CardEffect> UnlockedLocalSpecials() =>
        RunData.Instance?.MetEffects() ?? new List<CardEffect>();

    /// A remembered choice that is not on offer (a wiped save, say) falls back to the default.
    private static void SanitizeLocal2PlayerChoices(List<int> targets, List<CardEffect> effects)
    {
        if (!targets.Contains(_local2PlayerTarget)) _local2PlayerTarget = DefaultLocalTarget;
        if (effects.Count == 0) _local2PlayerSpecials = false;
    }

    private HBoxContainer AddChoiceRow()
    {
        HBoxContainer row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        _startMenuBox.AddChild(row);
        return row;
    }

    /// One option of a pick-one row. Toggle buttons sharing the row's ButtonGroup, so the pressed
    /// look IS the current choice and there is nothing else to keep in sync. The group is found
    /// from the row's first button rather than stored, so the row needs no bookkeeping of its own.
    private static void AddChoice(HBoxContainer row, string text, bool selected, Action onChosen)
    {
        ButtonGroup group = (row.GetChildCount() > 0 && row.GetChild(0) is Button first)
            ? first.ButtonGroup
            : new ButtonGroup();

        Button button = new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonGroup = group,
            ButtonPressed = selected,
            CustomMinimumSize = new Vector2(120, 58),
        };
        button.AddThemeFontSizeOverride("font_size", 26);
        button.Toggled += pressed => { if (pressed) onChosen(); };
        row.AddChild(button);
    }

    /// ...and the mirrored face-to-face table lives in the other scene.
    private void MenuStartLocal2Player()
    {
        SanitizeLocal2PlayerChoices(UnlockedLocalTargets(), UnlockedLocalSpecials());

        if (_gameModeButton == null)
        {
            _pendingLocal2Player = true;
            GetTree().ChangeSceneToFile("res://table_scene.tscn");
            return;
        }

        // Before starting, not after: OnStartButtonPressed reads this dropdown to decide whether to
        // route to the solo scene, and on "vs. Bot" it would send us straight back out again.
        _gameModeButton.Select(0);
        if (_mirrorToggle != null) _mirrorToggle.Visible = true;

        HideStartMenu();
        OnStartButtonPressed();
    }

    // ------------------------------------------------------------------
    // The first-launch tutorial (Alexander, 2026-09-15)
    //
    // Against the bot there is nobody in the room to explain the game, so the game has to teach
    // it. Six steps, each highlighting ONE control with one or two lines - the anti-wall-of-text
    // the tenets ask for, and the only form that works at both ends of the 5-to-85 range.
    //
    // It teaches THE TABLE AND ONLY THE TABLE. The ladder is already the tutorial for the cards:
    // ten rungs, one new card each, used on you by an opponent before the market will sell it to
    // you. A tutorial that also explained effect cards would be competing with a teaching
    // structure that already works, and would have to explain six cards the player cannot yet own.
    //
    // THE FIRST MATCH IS STAGED (Alexander's call): the deck is stacked so the opening deal is
    // 10 + 6 = 16 against the bot's 9 + 5, and the hand holds a +4. The lesson therefore ends with
    // the player making the RIGHT play - picking up the +4, seeing 16 + 4 = 20 in green, playing
    // it and holding on the target - rather than any play. Staging only happens at a target of 20
    // (see ShouldStageTutorial); replayed at any other rung the same six steps run on a real deal,
    // and every caption reads live values so none of them can lie.
    // ------------------------------------------------------------------

    /// The opening cards, APPENDED in this order - the deck is drawn from the end, and the turn
    /// order is P1, P2, P1, P2. So the last entry is Player 1's first card.
    private static readonly int[] TutorialOpening = { 5, 6, 9, 10 };

    /// Player 1's staged hand. The +4 is the lesson; the rest are there so the hand looks normal.
    private static readonly int[] TutorialModifiers = { 4, 3, -2, -1 };

    private const int TutorialSteps = 5;
    private const float SpotlightPad = 10f;

    /// Set by "Replay the tutorial", which reloads the scene - so it is a static, for the same
    /// reason _pendingLocal2Player is. It survives the reload; it is never saved to disk.
    private static bool _pendingTutorial;

    private bool _tutorialActive;
    private bool _tutorialStaged;   // this match's deck and hand are stacked for the lesson
    private int _tutorialIndex;
    private int _tutorialModifierCount;  // to notice a card actually being played

    private Control _spotlightOverlay;
    private ColorRect[] _spotlightShades;
    private ColorRect _spotlightHoleBlock;
    private PanelContainer _spotlightCaption;
    private Label _spotlightLabel;
    private Button _spotlightNext;
    private Button _spotlightSkip;
    private bool _spotlightSettling;

    // ---- the overlay -------------------------------------------------

    /// A hole cut in a dim, made of FOUR rects around the highlighted control rather than a
    /// shader. Cheap, no material, correct at every scale and orientation - and it degrades
    /// honestly: a wrong rect shows a misplaced hole rather than a black screen.
    ///
    /// The shades are the input gate as well as the dim. They stop mouse events; the hole has no
    /// child, so taps inside it fall straight through to the control being taught. That is the
    /// whole mechanism behind a "do" step, and it needs no changes to HumanCanActFor at all. A
    /// "tell" step drops a transparent blocker over the hole as well, and nothing is clickable.
    private void BuildSpotlight()
    {
        _spotlightOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_spotlightOverlay);
        _spotlightOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        Color shade = new Color(0, 0, 0, 0.72f);
        _spotlightShades = new ColorRect[4];
        for (int i = 0; i < _spotlightShades.Length; i++)
        {
            ColorRect rect = new ColorRect { Color = shade, MouseFilter = Control.MouseFilterEnum.Stop };
            _spotlightOverlay.AddChild(rect);
            _spotlightShades[i] = rect;
        }

        _spotlightHoleBlock = new ColorRect
        {
            Color = new Color(0, 0, 0, 0),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        _spotlightOverlay.AddChild(_spotlightHoleBlock);

        _spotlightCaption = new PanelContainer();
        StyleBoxFlat style = new StyleBoxFlat { BgColor = OverlayUi.PanelBg, BorderColor = OverlayUi.PanelBorder };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(12);
        style.SetContentMarginAll(18);
        _spotlightCaption.AddThemeStyleboxOverride("panel", style);
        _spotlightOverlay.AddChild(_spotlightCaption);

        VBoxContainer box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        _spotlightCaption.AddChild(box);

        _spotlightLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _spotlightLabel.AddThemeFontSizeOverride("font_size", 22);
        box.AddChild(_spotlightLabel);

        HBoxContainer buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 16);
        box.AddChild(buttons);

        // Skip is on EVERY step, not earned by sitting through the first one. Somebody who has
        // played this kind of game before does not want six taps, and making them work for the
        // way out is how you lose them on the first screen.
        _spotlightSkip = new Button { Text = "Skip" };
        _spotlightSkip.Pressed += () => FinishTutorial();
        buttons.AddChild(_spotlightSkip);

        _spotlightNext = new Button { Text = "Got it" };
        _spotlightNext.Pressed += OnSpotlightNextPressed;
        buttons.AddChild(_spotlightNext);
    }

    /// The highlighted control's axis-aligned box in screen space. Taken through the full
    /// transform rather than GlobalPosition, for the same reason AnimateCardDrop does it: a side
    /// of the table may be rotated 180 degrees, and a rotated control's position is not its corner.
    private static Rect2 ScreenRectOf(Control target)
    {
        Transform2D t = target.GetGlobalTransform();
        Vector2 size = target.Size;
        Vector2 a = t * Vector2.Zero;
        Vector2 b = t * new Vector2(size.X, 0f);
        Vector2 c = t * new Vector2(0f, size.Y);
        Vector2 d = t * size;

        Vector2 min = new Vector2(Mathf.Min(Mathf.Min(a.X, b.X), Mathf.Min(c.X, d.X)),
                                  Mathf.Min(Mathf.Min(a.Y, b.Y), Mathf.Min(c.Y, d.Y)));
        Vector2 max = new Vector2(Mathf.Max(Mathf.Max(a.X, b.X), Mathf.Max(c.X, d.X)),
                                  Mathf.Max(Mathf.Max(a.Y, b.Y), Mathf.Max(c.Y, d.Y)));
        return new Rect2(min, max - min);
    }

    private void PlaceSpotlight(Control target, bool blockHole)
    {
        if (_spotlightOverlay == null) return;

        Vector2 vp = GetViewport().GetVisibleRect().Size;
        Rect2 hole = (target != null && target.IsInsideTree() && target.Size.X > 1f)
            ? ScreenRectOf(target).Grow(SpotlightPad)
            : new Rect2(vp / 2f, Vector2.Zero); // no target: a plain dim, no hole

        float left = Mathf.Clamp(hole.Position.X, 0f, vp.X);
        float top = Mathf.Clamp(hole.Position.Y, 0f, vp.Y);
        float right = Mathf.Clamp(hole.End.X, 0f, vp.X);
        float bottom = Mathf.Clamp(hole.End.Y, 0f, vp.Y);

        SetRect(_spotlightShades[0], 0f, 0f, vp.X, top);                       // above
        SetRect(_spotlightShades[1], 0f, bottom, vp.X, vp.Y - bottom);         // below
        SetRect(_spotlightShades[2], 0f, top, left, bottom - top);             // left
        SetRect(_spotlightShades[3], right, top, vp.X - right, bottom - top);  // right

        SetRect(_spotlightHoleBlock, left, top, right - left, bottom - top);
        _spotlightHoleBlock.Visible = blockHole;

        // The caption goes under the hole, or over it when the hole is low - it must never cover
        // the thing it is pointing at.
        //
        // The width comes from the VIEWPORT, never from the caption's own minimum: an autowrapping
        // Label reports its UNWRAPPED single-line width as its minimum until it has been laid out
        // once. Measure first and the panel comes out screen-wide and one line tall, with the text
        // clipped - the same trap the stats row hit with HFlowContainer. Give it the width, and
        // the height follows from it. The floor covers the frame before that height is right.
        float width = Mathf.Min(vp.X * 0.72f, vp.X - 32f);
        _spotlightLabel.CustomMinimumSize = new Vector2(Mathf.Max(80f, width - 40f), 0f);

        float height = Mathf.Max(_spotlightCaption.GetCombinedMinimumSize().Y, vp.Y * 0.14f);
        float x = Mathf.Max(16f, (vp.X - width) / 2f);
        float y = (bottom + 16f + height <= vp.Y - 16f) ? bottom + 16f : Mathf.Max(16f, top - 16f - height);
        SetRect(_spotlightCaption, x, y, width, height);
    }

    private static void SetRect(Control control, float x, float y, float width, float height)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        control.Position = new Vector2(x, y);
        control.Size = new Vector2(Mathf.Max(0f, width), Mathf.Max(0f, height));
    }

    // ---- the steps ---------------------------------------------------

    private Control TutorialTarget(int step)
    {
        switch (step)
        {
            case 0: return _ui.P1ScoreBlock ?? _p1ScoreLabel;
            case 1: return _ui.DeckFootprint ?? _mainDeckPosition;
            case 2: return _p1ModifierContainer;
            case 3: return _ui.P1ActionRow;
            case 4: return _p1WinsLabel?.GetParent() as Control;
            default: return null;
        }
    }

    /// Steps 2-3 are DO steps: the player performs the thing rather than reading about it. That is
    /// the difference between a tutorial and a slideshow, and for both ends of the 5-to-85 range
    /// it is the whole point - playing a card is learned by playing one.
    ///
    /// Picking a card up and committing it used to be two steps, with the second one highlighting
    /// the Play button. That was wrong twice over (Alexander, S25 Ultra): the hole landed beside
    /// the confirm row rather than on it, so the button the step asked for was under the dim and
    /// could not be pressed - and the lesson did not need two steps anyway. Tapping the same card
    /// again commits it (the quick path the touch model has always had), so one step teaches both
    /// halves and never has to find a control that only exists mid-gesture.
    private static bool TutorialIsDoStep(int step) => step == 2 || step == 3;

    private string TutorialTextFor(int step)
    {
        switch (step)
        {
            case 0:
                return $"This is your score. You are at {_player1.CurrentScore}, and you are aiming "
                     + $"for {_gameState.TargetScore} without going over.";
            case 1:
                return "Four of each card numbered 1 to 10. The number on the deck is how many "
                     + "are left.";
            case 2:
                return "Tap a Modifier to see its effect. Tap it again to Play.";
            case 3:
                // Reads the live score, so it is honest on a staged first match and on a replay.
                return (_player1.CurrentScore >= _gameState.TargetScore - 2)
                    ? "You are on target. Hold stops you taking cards and locks your score in for "
                    + "the rest of the set."
                    : "Draw Card takes another card next turn. Hold stops you there and locks your "
                    + "score in. Choose one.";
            case 4:
                return $"Win {GameState.SetsToWinMatch} sets to take the match. These are yours "
                     + "so far. That is everything - good luck.";
            default:
                return string.Empty;
        }
    }

    /// True once the player has done the thing the current DO step asked for.
    private bool TutorialStepDone(int step)
    {
        switch (step)
        {
            case 2: return _player1.Modifiers.Count < _tutorialModifierCount;
            case 3: return !_player1.CanAct;
            default: return false;
        }
    }

    // ---- running it --------------------------------------------------

    /// First launch, or an explicit replay. Gated on the run's OWN step rather than the all-time
    /// best, and on the profile-level flag, so it happens once and never nags.
    private bool ShouldRunTutorial()
    {
        if (!_isVsBot || !_inRun) return false;
        if (_pendingTutorial) return true;

        RunData run = RunData.Instance;
        return run != null && !run.TutorialSeen && run.StepIndex == 0;
    }

    /// Only stage the turn where the staged numbers are true. The lesson lands on exactly the
    /// target, which needs a two-card opening (target 20 or more) and a +4 that reaches it from
    /// 16 - so it is stage 1's ruleset or nothing. A replay at any other rung runs the same six
    /// steps on a real deal, and every caption reads live values, so nothing said is ever wrong.
    private bool ShouldStageTutorial() => _gameState.TargetScore == 20;

    private async void StartTutorial()
    {
        _tutorialActive = true;
        _tutorialIndex = 0;
        _tutorialModifierCount = _player1.Modifiers.Count;

        // Let the opening deal land first - being taught about a score before the cards that made
        // it have arrived is worse than waiting half a second.
        for (int i = 0; i < 40; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!IsInsideTree() || !_tutorialActive) return;
        }

        MoveChild(_spotlightOverlay, GetChildCount() - 1);
        _spotlightOverlay.Visible = true;
        RefreshSpotlight();
    }

    private void AdvanceTutorial()
    {
        if (!_tutorialActive) return;

        _tutorialIndex++;
        if (_tutorialIndex >= TutorialSteps)
        {
            FinishTutorial();
            return;
        }

        RefreshSpotlight();
    }

    private void RefreshSpotlight()
    {
        PlaceCurrentSpotlight();
        SettleSpotlight();
    }

    /// Where the hole goes right now, for whichever of the two owners has the overlay.
    private void PlaceCurrentSpotlight()
    {
        if (_spotlightOverlay == null || !_spotlightOverlay.Visible) return;

        // A coach-mark borrows the same overlay, so it has to be re-placed on a rotation too.
        if (_coachShowing.HasValue)
        {
            PlaceSpotlight(CoachTarget(_coachShowing.Value), blockHole: true);
            return;
        }

        if (!_tutorialActive) return;

        bool doStep = TutorialIsDoStep(_tutorialIndex);
        _spotlightLabel.Text = TutorialTextFor(_tutorialIndex);
        _spotlightNext.Visible = !doStep;   // a DO step is finished by doing it, not by a button
        PlaceSpotlight(TutorialTarget(_tutorialIndex), blockHole: !doStep);
    }

    /// ...and then again once the layout has actually settled.
    ///
    /// THE BUG this exists for (Alexander, S25 Ultra, 2026-09-15): rotating portrait to landscape
    /// left the hole over the score off to one side. A single deferred pass measures the layout
    /// mid-move - the sides are re-ordered, the base resolution is rewritten, and EnsureLayoutFits
    /// may still be scaling - so GetGlobalTransform returns where the control WAS. This is the
    /// same two-frame lesson EnsureLayoutFits already learned, and for the same reason: a
    /// container's geometry is only right on the pass after the one that changed it.
    ///
    /// Placed immediately as well, so the hole never blinks; the settle pass only corrects it.
    /// Latched, because RefreshSpotlight is deferred from every UpdateUI and a pile of overlapping
    /// waits would all place the same rect.
    private async void SettleSpotlight()
    {
        if (_spotlightSettling || _spotlightOverlay == null || !_spotlightOverlay.Visible) return;
        _spotlightSettling = true;

        try
        {
            for (int i = 0; i < 2; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!IsInsideTree()) return;
            }
            PlaceCurrentSpotlight();
        }
        finally
        {
            _spotlightSettling = false;
        }
    }

    /// Runs from UpdateUI, which is called after every action that could complete a step - so a
    /// step's completion never has to be wired into the five handlers that could cause it.
    private void CheckTutorialProgress()
    {
        if (!_tutorialActive || _spotlightOverlay == null || !_spotlightOverlay.Visible) return;

        if (TutorialIsDoStep(_tutorialIndex) && TutorialStepDone(_tutorialIndex))
        {
            AdvanceTutorial();
            return;
        }

        CallDeferred(MethodName.RefreshSpotlight); // the highlighted control may have moved
    }

    private void FinishTutorial()
    {
        if (!_tutorialActive) return;

        _tutorialActive = false;
        _pendingTutorial = false;
        if (_spotlightOverlay != null) _spotlightOverlay.Visible = false;
        RunData.Instance?.MarkTutorialSeen();

        // The bot has been held for the whole walkthrough (Bot.ProcessTurn refuses to run while
        // the tutorial is up) so the lesson could not desync from a table moving underneath it.
        // Let it think now, and the turn resolves normally from here.
        if (_isVsBot && _isGameStarted && !_gameState.IsGameOver) _bot.ProcessTurn();

        // Deferred: FinishTutorial can be reached from inside UpdateUI (a DO step completing on
        // the last one), and a re-entrant refresh is the kind of thing that works until it doesn't.
        CallDeferred(MethodName.UpdateUI);
    }

    // ------------------------------------------------------------------
    // Coach-marks: one line, the first time you meet a card
    //
    // This is what actually delivers the ladder's promise. The ladder introduces one new card per
    // rung and the market sells it to you straight afterwards - but until now the card simply
    // appeared and something happened to your score. One highlight and one sentence, once ever.
    //
    // The sentence is CardEffects.Introduction, which is the market's own Description. Two copies
    // of an explanation drift, and the player would end up being told two different things about
    // one card.
    //
    // "Met" is profile level (RunData.CardsMet), so a lost run does not un-teach it, and it is the
    // same set a collection log will read.
    // ------------------------------------------------------------------

    private readonly struct CoachMark
    {
        public readonly Card Card;
        public readonly bool FromOpponent;

        public CoachMark(Card card, bool fromOpponent)
        {
            Card = card;
            FromOpponent = fromOpponent;
        }
    }

    private readonly Queue<CoachMark> _coachQueue = new Queue<CoachMark>();
    private CoachMark? _coachShowing;

    /// Where the highlight goes: the middle panel's banner when the card was played AT you (the
    /// banner is the thing that just narrated it), your own hand when it is a card you now hold.
    private Control CoachTarget(CoachMark mark) =>
        mark.FromOpponent ? _ui.EffectBanner : _p1ModifierContainer;

    private void QueueCoachMark(Card card, bool fromOpponent)
    {
        string key = CardEffects.MetKey(card);
        if (key == null) return; // a plain +3 explains itself

        RunData run = RunData.Instance;
        if (run == null || run.HasMetCard(key)) return;

        // The same card can arrive twice in one turn - once in the hand and once across the table
        // - and the same explanation twice is worse than none.
        foreach (CoachMark queued in _coachQueue)
            if (CardEffects.MetKey(queued.Card) == key) return;
        if (_coachShowing.HasValue && CardEffects.MetKey(_coachShowing.Value.Card) == key) return;

        _coachQueue.Enqueue(new CoachMark(card, fromOpponent));
    }

    /// Local 2-player is left alone on purpose: there is a person in the room to explain, which is
    /// the same reason that mode's How to Play is short.
    private void QueueCoachMarksForModifiers()
    {
        if (!_isVsBot) return;
        foreach (Card card in _player1.Modifiers) QueueCoachMark(card, fromOpponent: false);
    }

    /// Runs from UpdateUI. Shows at most one at a time, and only when nothing else owns the
    /// screen - a card explained over the top of a set-end panel teaches nobody anything.
    private void DrainCoachMarks()
    {
        if (_coachShowing.HasValue)
        {
            CallDeferred(MethodName.RefreshSpotlight); // the highlighted control may have moved
            return;
        }

        if (_tutorialActive || _coachQueue.Count == 0) return;
        if (!_isGameStarted || _gameState.IsGameOver || _setOverPending) return;
        if (_howToPlayOverlay != null && _howToPlayOverlay.Visible) return;
        if (_tableMenuOverlay != null && _tableMenuOverlay.Visible) return;
        if (_recallOverlay != null && _recallOverlay.Visible) return;
        if (RescueShowing) return;

        ShowCoachMark(_coachQueue.Dequeue());
    }

    private void ShowCoachMark(CoachMark mark)
    {
        _coachShowing = mark;

        _spotlightLabel.Text = CardEffects.Introduction(mark.Card);
        _spotlightNext.Visible = true;
        _spotlightSkip.Visible = false; // there is nothing to skip: it is one line, once ever

        MoveChild(_spotlightOverlay, GetChildCount() - 1);
        _spotlightOverlay.Visible = true;
        PlaceSpotlight(CoachTarget(mark), blockHole: true);
    }

    private void DismissCoachMark()
    {
        if (!_coachShowing.HasValue) return;

        if (RunData.Instance != null && RunData.Instance.MarkCardMet(CardEffects.MetKey(_coachShowing.Value.Card)))
            AnnounceCollectionComplete();
        _coachShowing = null;

        if (_spotlightOverlay != null) _spotlightOverlay.Visible = false;
        if (_spotlightSkip != null) _spotlightSkip.Visible = true;

        CallDeferred(MethodName.UpdateUI); // which drains the next one, if there is one
    }

    /// One button, two owners. The tutorial advances; a coach-mark is simply done.
    private void OnSpotlightNextPressed()
    {
        if (_coachShowing.HasValue) DismissCoachMark();
        else AdvanceTutorial();
    }

    // ------------------------------------------------------------------
    // How to Play
    //
    // A "How to Play" button is added in code to the middle panel's ButtonColumn (just above the
    // Restart / Exit row) so both scenes get it without NodePath wiring. It opens a full-screen
    // overlay with the rules; it can be opened at any time and changes no game state. In mirrored
    // 2-player mode a "Flip for other player" button turns the panel upside down for Player 2.
    // ------------------------------------------------------------------
    // TWO texts, because the two modes have different learners (Alexander, 2026-09-15).
    //
    // In local 2-player somebody who already knows the game is sitting next to somebody who does
    // not, and a person explains it far better than a panel does. That screen only has to carry
    // the handful of rules the explainer might forget - so it is short on purpose, and making it
    // longer would make it worse.
    //
    // Against the bot nobody is there to explain, so the game has to teach. The first-launch
    // tutorial does that (claude/tutorial-and-how-to-play-spec.md); this text is the reference
    // you come back to, and it deliberately does NOT enumerate the six effect cards - the ladder
    // introduces them one rung at a time and explains each one where you meet it. A list of all
    // six here would undo that, and it is exactly the "text-heavy wall" the tenets rule out.
    private static readonly string HowToPlayShort =
        "GOAL\n" +
        "Get as close to the target without going over. The target is on your score line - " +
        $"\"You  17/20\". Win {GameState.SetsToWinMatch} sets to win the match.\n\n" +
        "EACH TURN\n" +
        "Both players are dealt a card at the same time. You both decide at the same time too - " +
        "nobody waits for anyone.\n\n" +
        "MODIFIERS\n" +
        "Tap a Modifier to see its effect, then tap it again to play it. It adds its value to your " +
        "score. You get four, and they have to last the whole match.\n\n" +
        "DRAW CARD or HOLD\n" +
        "Draw Card: you are done for this turn, and you take another card on the next one.\n" +
        "Hold: you stop taking cards, and your score is locked for the rest of the set.\n\n" +
        "GOING OVER\n" +
        "Over the target is only a warning until you press Draw Card or Hold - a minus Modifier can " +
        "still save you. Draw while over, and you bust.\n\n" +
        "That is the whole game. Everything else is a Modifier that explains itself when you meet it.";

    private static readonly string HowToPlayFull =
        "GOAL\n" +
        "Get as close to the target as you can without going over. The target is on your own score " +
        "line - \"You  17/20\" - and it CHANGES as you climb: 20 at first, then 23, then 18, and on " +
        $"up. Win {GameState.SetsToWinMatch} sets to win the match.\n\n" +
        "MATCH, SET, TURN\n" +
        "A match is played in sets, and a set is played in turns. Win a set by finishing closer to " +
        "the target than your opponent.\n\n" +
        "THE DECK\n" +
        "One deck of 40 cards, shared by both players: four each of 1 to 10. It is shuffled fresh " +
        "every set, and the number on it is how many cards are left - so it can be counted.\n\n" +
        "A TURN\n" +
        "Each turn, every player who isn't holding is dealt one card at the same time. Both players " +
        "then decide - at the same time, without waiting for each other - whether to play a " +
        "Modifier, and then press Draw Card or Hold.\n" +
        "When the target is 20 or more, the FIRST turn of a set gives everyone two cards. Two " +
        "cards can never total more than 20, so that opening can never bust you.\n\n" +
        "MODIFIERS\n" +
        "You get 4 Modifiers at the start of a match, and they have to last every set of it - " +
        "a Modifier spent in the first set is gone for the rest. Plain ones are worth -4 to +4 and " +
        "add their value to your score.\n\n" +
        "PLAYING A MODIFIER\n" +
        "Tap a Modifier to pick it up. It lifts, and your score changes to what it would " +
        "become - for example 17/20 turns into 20/20. Blue means you would still be at or under the " +
        "target, orange means it would take you over. Nothing is spent yet: tap Play (or tap it again) to " +
        "commit it, or Put back to change your mind.\n\n" +
        "+/- MODIFIERS\n" +
        "A Modifier marked +/- can be played either way round. Pick it up and press Flip Value " +
        "to swap it between plus and minus - as often as you like - before playing it. " +
        "A +3 becomes a -3, and back again.\n\n" +
        "DRAW CARD\n" +
        "You are done for this turn, and you take another card on the next one.\n\n" +
        "HOLD\n" +
        "You stop taking cards for the rest of the set. Your score is locked in.\n\n" +
        "GOING OVER\n" +
        "Going over the target is only a warning (\"Over target!\") - you can still play a " +
        "minus Modifier to get back under. If you press Draw Card or Hold while still over the " +
        "target, you bust and lose the set when the turn resolves.\n\n" +
        "HOW A SET ENDS\n" +
        "Once both players have pressed Draw Card or Hold, the turn resolves:\n" +
        "- Anyone over the target busts. If both bust, the set is a tie and is replayed.\n" +
        "- If both players are holding, the higher score wins the set. Equal scores tie and the set " +
        "is replayed.\n" +
        "- Otherwise the next turn is dealt to everyone who isn't holding.\n\n" +
        "Filling all 9 board slots without busting is still a good place to be - hold!\n\n" +
        "THE BOT\n" +
        "It plays the same rules as you and decides at the same time as you do - you never wait for " +
        "it. It gets sharper as you climb: the early opponents play their own Modifiers, " +
        "the last ones play yours.\n\n" +
        "THE CLIMB\n" +
        "Ten matches, each against a tougher opponent at a different target. Winning pays medals; " +
        "medals buy Modifiers in the market between matches; the Modifiers you own are slotted into " +
        "a deck of 12, and 4 of those 12 are dealt to you each match. Losing a match ends the run - " +
        "but nothing you own is ever taken away.\n\n" +
        "SPECIAL MODIFIERS\n" +
        "Most rungs of the climb introduce one new Modifier that does something other than add a " +
        "number - changing a card, taking a score, undoing a play. Each one is explained the first " +
        "time you meet it, and the market sells it to you straight afterwards. There is nothing to " +
        "memorise here: you will always have met a Modifier before you can buy it.";

    private Control _howToPlayOverlay;
    private Label _howToPlayRules;
    private PanelContainer _howToPlayPanel;
    private Button _howToPlayFlipButton;
    private bool _howToPlayFlipped = false;

    private void BuildHowToPlay()
    {
        // The button that opens this lives in the table menu now (CompactControlPanel) - it was
        // one of four things permanently on screen in a middle column the playtest called
        // cluttered, and it is pressed once a session.
        //
        // The overlay: dim + centred panel + scrolling rules + Close (and Flip when mirrored).
        _howToPlayOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(_howToPlayOverlay);
        _howToPlayOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        ColorRect dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _howToPlayOverlay.AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _howToPlayPanel = new PanelContainer();
        StyleBoxFlat style = new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.14f, 0.2f, 0.98f),
            BorderColor = new Color(0.55f, 0.65f, 0.8f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(12);
        style.SetContentMarginAll(20);
        _howToPlayPanel.AddThemeStyleboxOverride("panel", style);
        _howToPlayOverlay.AddChild(_howToPlayPanel);
        // Fill the screen with a margin so the rules get as much room as the device has.
        _howToPlayPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _howToPlayPanel.OffsetLeft = 24;
        _howToPlayPanel.OffsetTop = 24;
        _howToPlayPanel.OffsetRight = -24;
        _howToPlayPanel.OffsetBottom = -24;
        _howToPlayPanel.Resized += () =>
        {
            _howToPlayPanel.PivotOffset = _howToPlayPanel.Size / 2f;
            _howToPlayPanel.RotationDegrees = _howToPlayFlipped ? 180f : 0f;
        };

        VBoxContainer box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        _howToPlayPanel.AddChild(box);

        Label title = MakeOverlayLabel(30);
        title.Text = "How to Play";
        box.AddChild(title);

        ScrollContainer scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        box.AddChild(scroll);

        _howToPlayRules = new Label
        {
            Text = HowToPlayShort,
            AutowrapMode = TextServer.AutowrapMode.Word,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _howToPlayRules.AddThemeFontSizeOverride("font_size", 20);
        scroll.AddChild(_howToPlayRules);

        HBoxContainer buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 16);
        box.AddChild(buttons);

        _howToPlayFlipButton = new Button { Text = "Flip for other player", Visible = false };
        _howToPlayFlipButton.Pressed += () =>
        {
            _howToPlayFlipped = !_howToPlayFlipped;
            _howToPlayPanel.PivotOffset = _howToPlayPanel.Size / 2f;
            _howToPlayPanel.RotationDegrees = _howToPlayFlipped ? 180f : 0f;
        };
        buttons.AddChild(_howToPlayFlipButton);

        Button close = new Button { Text = "Close" };
        close.Pressed += HideHowToPlay;
        buttons.AddChild(close);
    }

    /// Which of the two texts this screen is showing. Against the bot, the long one; with two
    /// people at one device, the short one. From the start menu - before a mode has been picked -
    /// the short one too: somebody who has not started yet wants to know what the game IS, and the
    /// tutorial will teach them the rest at the table.
    private string HowToPlayForThisMode() =>
        (_isGameStarted && _isVsBot) ? HowToPlayFull : HowToPlayShort;

    private void ShowHowToPlay()
    {
        if (_howToPlayOverlay == null) return;
        if (_howToPlayRules != null) _howToPlayRules.Text = HowToPlayForThisMode();
        _howToPlayFlipped = false;
        _howToPlayPanel.RotationDegrees = 0f;
        _howToPlayFlipButton.Visible = _ui.IsMirrored;
        MoveChild(_howToPlayOverlay, GetChildCount() - 1); // above any stray animation card
        _howToPlayOverlay.Visible = true;
    }

    private void HideHowToPlay()
    {
        if (_howToPlayOverlay == null) return;
        _howToPlayOverlay.Visible = false;
    }

}
