using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public partial class GameManager : Node, IBotTable, ITableHost, ITableUiHost, IMenusHost, ITeachingHost, IPromptsHost
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

        _menus = new Menus(this, this, _ui, new Menus.Nodes
        {
            StartButton = _startButton,   ExitButton = _exitButton,
            RestartButton = _restartButton, GameModeButton = _gameModeButton,
            MirrorToggle = _mirrorToggle,
        });

        _prompts = new Prompts(this, this, _ui);

        _teaching = new Teaching(this, this, _ui, _menus, new Teaching.Nodes
        {
            MainDeckPosition = _mainDeckPosition, P1ModifierContainer = _p1ModifierContainer,
            P1ScoreLabel = _p1ScoreLabel,         P1WinsLabel = _p1WinsLabel,
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
        _teaching.BuildSpotlight();
        _prompts.BuildSetEndOverlay();
        _menus.BuildHowToPlay();
        _ui.CompactControlPanel(); // must precede BuildConfirmRows - see the method
        _ui.BuildConfirmRows();
        BuildIntermissionOverlays();
        _ui.BuildTableBanners();
        GameSettings.EnsureLoaded();
        GameSettings.Changed += OnSettingsChanged;
        BuildDebugRow();
        BuildOptions();
        _menus.BuildStartMenu();
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

        bool autoLocal2P = _gameModeButton != null && Menus.PendingLocal2Player;
        if (autoLocal2P)
        {
            Menus.PendingLocal2Player = false;
            _gameModeButton.Select(0); // or OnStartButtonPressed routes straight back to the solo scene
            if (_mirrorToggle != null) _mirrorToggle.Visible = true;
        }

        if (autoRun || autoLocal2P) CallDeferred(MethodName.OnStartButtonPressed);
        else _menus.ShowStartMenu();
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
            else Menus.PendingLocal2Player = true;
        }
        else
        {
            // Land on the menu, and leave nothing behind that would skip past it.
            if (RunData.Instance != null) RunData.Instance.AutoStartNextMatch = false;
            Menus.PendingLocal2Player = false;
        }

        GD.Print(sameMatch ? "Restarting match..." : "Returning to the start menu...");
        GetTree().ReloadCurrentScene();
    }

    /// The table's Exit leaves the MATCH, not the app (Alexander, 2026-09-16: "Exit doesn't allow
    /// you to go back to start menu"). Quitting the app lives on the start menu now.
    private void OnExitPressed()
    {
        _menus.HideTableMenu();
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
        bool tutorial = _teaching.PrepareForMatch();

        _table.DealMatchHands(); // the hand has to last all three sets of the match

        StartNewSet(); // UpdateUI enables the Draw Card / Hold buttons

        if (tutorial) _teaching.StartTutorial();
    }

    /// Puts the solo scene onto the ladder: picks up the run in progress (or starts one), and takes
    /// this venue's target score. Local 2-player is never part of a run.
    private void BeginRunMatch()
    {
        _inRun = false;
        if (!_isVsBot)
        {
            _gameState.TargetScore = Menus.Local2PlayerTarget; // the setup page's choice
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
            if (_prompts.OfferRescue()) return;
            EndSet();
            return;
        }

        DealCards();
    }


    // ------------------------------------------------------------------
    // The three that stop the game
    //
    // The set-end explanation, Recall's chooser and the bust rescue offer are in Prompts.cs. A
    // prompt arrives rather than being entered, takes the screen, and hands back one decision.
    // ------------------------------------------------------------------
    private Prompts _prompts;

    Player IPromptsHost.Player1 => _player1;
    Player IPromptsHost.Player2 => _player2;
    GameState IPromptsHost.State => _gameState;
    Random IPromptsHost.Rng => _random;
    bool IPromptsHost.VsBot => _isVsBot;
    bool IPromptsHost.InRun => _inRun;
    bool IPromptsHost.TutorialRunning => _teaching.Running;
    bool IPromptsHost.PlayEffectCard(Player owner, Card card, Card chosen) => PlayEffectCard(owner, card, chosen);
    void IPromptsHost.SetSelection(Player player, Card card) => SetSelection(player, card);

    // ------------------------------------------------------------------
    // The lessons
    //
    // The first-launch walkthrough and the coach marks are in Teaching.cs. Both watch the SCREEN
    // rather than the rules, so what they ask for here is mostly "is anything else up?" - and the
    // one thing they change is letting the bot think again once the lesson is over.
    // ------------------------------------------------------------------
    private Teaching _teaching;

    Player ITeachingHost.Player1 => _player1;
    GameState ITeachingHost.State => _gameState;
    bool ITeachingHost.GameStarted => _isGameStarted;
    bool ITeachingHost.VsBot => _isVsBot;
    bool ITeachingHost.InRun => _inRun;
    bool ITeachingHost.SetOverPending => _setOverPending;
    bool ITeachingHost.PromptShowing => _prompts.Showing;
    void ITeachingHost.ReleaseBot() => _bot.ProcessTurn();
    void ITeachingHost.CollectionComplete() => AnnounceCollectionComplete();

    // ------------------------------------------------------------------
    // The screens that cover the table
    //
    // The start menu and its sub-pages, How to Play, the collection log and the table's own Menu
    // button are in Menus.cs. The contract is four verbs, because that is all a menu ever does:
    // take one decision and hand it over.
    // ------------------------------------------------------------------
    private Menus _menus;

    bool IMenusHost.GameStarted => _isGameStarted;
    bool IMenusHost.VsBot => _isVsBot;
    string IMenusHost.FinaleRulesLine(RunData run, string prefix) => FinaleRulesLine(run, prefix);
    void IMenusHost.StartMatch() => OnStartButtonPressed();
    void IMenusHost.OpenOptions(Action onClosed) => OpenOptions(onClosed);

    /// The walkthrough is staged into the deal, so replaying it is a reload rather than a flag
    /// flipped mid-match - and the flag has to be set before the reload, not after.
    void IMenusHost.ReplayTutorial()
    {
        Teaching.PendingTutorial = true;
        RunData.Instance?.ReplayTutorial();
        OnRestartPressed();
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
    void ITableUiHost.BuildTableMenu() => _menus.BuildTableMenu();
    void ITableUiHost.LayoutChanged() => _teaching.RefreshSpotlight();
    string ITableUiHost.FinaleRulesLine(RunData run, string prefix) => FinaleRulesLine(run, prefix);

    /// The table has just been repainted. The tutorial is watching the screen for the step it set,
    /// and a coach mark waits for a quiet moment to appear - both get their look here, after the
    /// paint and before anything else happens.
    void ITableUiHost.AfterRefresh()
    {
        _teaching.CheckTutorialProgress();
        _teaching.DrainCoachMarks();
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
    bool ITableHost.LocalSpecials => Menus.Local2PlayerSpecials;
    bool ITableHost.TutorialStaged => _teaching.Staged;
    IReadOnlyList<int> ITableHost.TutorialOpening => Teaching.Opening;
    IReadOnlyList<int> ITableHost.TutorialModifiers => Teaching.Modifiers;

    List<CardEffect> ITableHost.UnlockedLocalSpecials() => Menus.UnlockedLocalSpecials();
    void ITableHost.DealBotHand() => _bot.DealHand();

    void ITableHost.ShowDrawnCard(Player player, Card card, float delay) =>
        _ui.InstantiateCardView(card, player == _player1 ? _p1BoardContainer : _p2BoardContainer, delay);

    void ITableHost.HandsDealt(bool introduceCards)
    {
        ClearSelections();
        if (!introduceCards) return;
        _teaching.QueueCoachMarksForModifiers();
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
    bool IBotTable.LocalSpecials => Menus.Local2PlayerSpecials;
    bool IBotTable.BotPlayedEffectThisTurn => _table.HasPlayedEffect(_player2);
    bool IBotTable.TutorialHoldsBot => _teaching.Running;

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
        if (owner == _player2) _teaching.QueueCoachMark(card, fromOpponent: true);
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
        if (_prompts.Showing) return false; // a set-end panel, a Recall choice or a rescue offer
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
        _prompts.ShowSetEnd(title, why, buttonText, next);
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
        void RescueText() => rescue.Text = Prompts.DebugAlwaysRescue ? "Rescue 100%" : "Rescue 15%";
        RescueText();
        rescue.Pressed += () => { Prompts.DebugAlwaysRescue = !Prompts.DebugAlwaysRescue; RescueText(); };
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
                _prompts.ShowRecallOverlay(player, card);
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


    // ---- the overlay -------------------------------------------------


    // ---- the steps ---------------------------------------------------


    // ---- running it --------------------------------------------------


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


}
