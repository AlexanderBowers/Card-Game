using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public partial class GameManager : Node, IBotTable, ITableHost, ITableUiHost, IMenusHost, ITeachingHost, IPromptsHost
{
    private GameState _gameState;
    private Player _player1;
    private Player _player2;

    // Pass 34: no scene nodes are exported here any more. The table is a LAYOUT scene
    // (table_landscape.tscn / table_portrait.tscn) that TableUi instances under LayoutHost and
    // swaps on a rotation, so any node held here would go stale the first time the phone turned.
    // What the game needs from the table it asks TableUi for, at the moment it needs it.
    private Control _p1BoardContainer => _ui?.P1Board;
    private Control _p2BoardContainer => _ui?.P2Board;

    // The three controls that live in the table MENU rather than on the table. Built here because
    // their handlers are here; Menus moves them into its overlay.
    private CheckButton _mirrorToggle;  // local 2-player: turn Player 2's side round
    private Button _restartButton;
    private Button _exitButton;

    private Random _random = new Random();
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
        // First, because it is the slowest: Play takes a second or two to say what this account
        // owns, and AdMob wants its first ad preloaded long before a match can end.
        Monetization.Initialize(this);

        _gameState = new GameState();
        _player1 = new Player("Player 1");
        _player2 = new Player("Player 2");
        _mirrorToggle = new CheckButton { Text = "Mirror for Player 2", FocusMode = Control.FocusModeEnum.None };
        // Face-to-face on a phone wants P2 flipped by default; on a desktop it doesn't.
        _mirrorToggle.SetPressedNoSignal(OS.HasFeature("mobile"));
        // Refresh FIRST: it decides whether the score is one line or two, and the fit measures
        // the side around it (Alexander, 2026-09-18: toggling the mirror shifted Player 1's side).
        _mirrorToggle.Toggled += _ => { _ui.Refresh(); _ui.ApplyResponsiveLayout(); };

        _restartButton = new Button { Text = "Restart" };
        _restartButton.Pressed += OnRestartPressed;
        _exitButton = new Button { Text = "Exit" };
        _exitButton.Pressed += OnExitPressed;

        // The layout scene goes under LayoutHost; every overlay is added to this node AFTER it,
        // so they all draw on top of whichever layout is up.
        Control layoutHost = GetNodeOrNull<Control>("LayoutHost");
        if (layoutHost == null)
        {
            layoutHost = new Control { Name = "LayoutHost", MouseFilter = Control.MouseFilterEnum.Pass };
            AddChild(layoutHost);
            MoveChild(layoutHost, 0);
            layoutHost.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        _ui = new TableUi(this, this, layoutHost, _mirrorToggle);
        // A big moment held the turn back (ResolveTurn waits while one plays): pick it up again.
        _ui.Moments.Idle += ResolveTurn;

        _menus = new Menus(this, this, _ui, new Menus.Nodes
        {
            ExitButton = _exitButton, RestartButton = _restartButton, MirrorToggle = _mirrorToggle,
        });

        _prompts = new Prompts(this, this, _ui);
        _teaching = new Teaching(this, this, _ui, _menus);

        _table = new Table(this);
        _bot = new Bot(this); // before the first deal: the table asks it for the bot's hand
        _table.DealMatchHands();

        _teaching.BuildSpotlight();
        _prompts.BuildSetEndOverlay();
        _menus.BuildHowToPlay();
        _menus.BuildTableMenu();
        _debugRows = DebugRows.Build(_menus.DebugSlot, DebugJumpStage, RestartToMenu); // Options > Debug shows them
        BuildIntermissionOverlays();
        GameSettings.EnsureLoaded();
        GameSettings.Changed += OnSettingsChanged;
        BuildOptions();
        _menus.BuildStartMenu();

        // Puts up the layout for this screen (and repaints it); again on every resize/rotation.
        GetTree().Root.SizeChanged += _ui.ApplyResponsiveLayout;
        _ui.ApplyResponsiveLayout();

        // Two ways to arrive already holding a decision, and in both the player has pressed a
        // button to get here - so deal the match rather than showing them a second front door.
        // The run's note is RunData's (the deck screen, a debug stage jump, Restart); the local
        // 2-player note is Menus' static. Both survive the reload; neither is saved to disk.
        bool autoRun = RunData.Instance != null && RunData.Instance.AutoStartNextMatch;
        if (autoRun) RunData.Instance.AutoStartNextMatch = false;

        bool autoLocal2P = !autoRun && Menus.PendingLocal2Player;
        Menus.PendingLocal2Player = false;

        // Debug builds only: "-- --autostart=solo" or "-- --autostart=local" (add "--mirror" to
        // turn Player 2's side round) on the command line
        // deals straight into a match, skipping the start menu - for looking at the table layouts
        // (Movie Maker screenshots, a quick check after editing a layout scene).
        if (OS.IsDebugBuild() && !autoRun && !autoLocal2P)
        {
            foreach (string arg in OS.GetCmdlineUserArgs())
            {
                if (arg == "--autostart=solo") autoRun = true;
                if (arg == "--autostart=local") autoLocal2P = true;
                if (arg == "--mirror") _mirrorToggle.SetPressedNoSignal(true);
            }
        }

        if (autoRun || autoLocal2P) Callable.From(() => StartMatch(local2Player: autoLocal2P)).CallDeferred();
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
    private List<Control> _debugRows = new List<Control>();

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
        _ui.ApplyResponsiveLayout(); // also re-fits (the debug rows change the height)
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
            if (_isVsBot)
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

    /// Deals a match in the mode the start menu chose. Solo and local 2-player share one table
    /// scene since pass 34, so this is a flag rather than a scene change.
    private void StartMatch(bool local2Player)
    {
        _isGameStarted = true;
        _isVsBot = !local2Player;
        _player2.PlayerName = _isVsBot ? "AI Bot" : "Player 2";

        // The mode decides what the layout shows (P2's buttons, the mirror), so re-apply it.
        _ui.ApplyResponsiveLayout();

        BeginRunMatch();

        // Decided BEFORE the hand is dealt and before the first shuffle, because staging is a
        // change to both of them.
        bool tutorial = _teaching.PrepareForMatch();

        // Until the camera has swung in and the stage banner has crossed, the table is still
        // arriving: the first deal's refresh must not put a coach mark up under the swing.
        _arrivalPending = !tutorial;

        _table.DealMatchHands(); // the hand has to last all three sets of the match

        StartNewSet(); // UpdateUI enables the Draw Card / Hold buttons

        if (tutorial) _teaching.StartTutorial();
        else if (_ui.World3D != null && _ui.World3D.CanSwing) _ui.World3D.SwingIn(ShowStageIntro); // Pocket-style camera swing, then the banner
        else ShowStageIntro();
    }

    /// The camera is still swinging in, or the stage banner is still crossing: the table has not
    /// arrived yet, so nothing that wants the player's attention (a coach mark) should start.
    private bool TableArriving => _arrivalPending || (_ui.World3D?.Swinging ?? false) || StageIntro.Playing > 0;
    private bool _arrivalPending;

    /// "Stage 2 / Target: 20" sliding across at the start of a ladder match (StageIntro).
    /// Once it has gone, a refresh lets any coach mark that waited for it appear.
    private void ShowStageIntro()
    {
        RunData run = _inRun ? RunData.Instance : null;
        if (run == null)
        {
            _arrivalPending = false;
            _ui.DeferRefresh();
            return;
        }

        string title = run.Endless ? $"Endless Match {run.EndlessStreak + 1}" : $"Stage {run.MatchNumber}";
        StageIntro.Play(this, title, $"Target: {_gameState.TargetScore}", () =>
        {
            _arrivalPending = false;
            _ui.DeferRefresh();
        });
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
        _ui.Toasts.ClearEffectBanner();

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

        // Paint first: a Hold that has just happened starts its padlock from this refresh. Then,
        // while any big moment is playing, nothing is resolved - no deal, no set end - until it
        // finishes and TableMoments.Idle calls back here.
        _ui.Refresh();
        if (_ui.Moments.Busy) return;

        if (_player1.CanAct || _player2.CanAct) return; // one side is still deciding

        if (SetRules.IsSetOver(_player1, _player2, _gameState.TargetScore))
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
    Card ITeachingHost.SelectedFor(Player player) => SelectedFor(player);
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
    void IMenusHost.StartMatch(bool local2Player) => StartMatch(local2Player);
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
    // Pass 33: the stage line only. Whose move it is was said three times over ("Both players: play
    // or draw" here, "Your move" on each side) - the buttons say it now, by reading "Waiting...".
    string ITableUiHost.SetInfoLine() => RunHeader();
    void ITableUiHost.ValidateSelections() => ValidateSelections();
    void ITableUiHost.ModifierPressed(Player player, Card card) => OnModifierCardPressed(player, card);
    void ITableUiHost.PlayPressed(Player player) => PlaySelectedCard(player);
    void ITableUiHost.PutBackPressed(Player player) => SetSelection(player, null);
    void ITableUiHost.FlipValuePressed(Player player) => FlipSelectedValue(player);
    void ITableUiHost.DrawCardPressed(Player player) => OnDrawCardPressed(player);
    void ITableUiHost.HoldPressed(Player player) => OnHoldPressed(player);
    void ITableUiHost.MenuPressed() => _menus.ShowTableMenu();
    void ITableUiHost.LayoutBound(TableLayout layout) { } // nothing of the game's hangs on the layout now
    void ITableUiHost.LayoutChanged() => _teaching.RefreshSpotlight();

    /// The table has just been repainted. The tutorial is watching the screen for the step it set,
    /// and a coach mark waits for a quiet moment to appear - both get their look here, after the
    /// paint and before anything else happens.
    void ITableUiHost.AfterRefresh()
    {
        _teaching.CheckTutorialProgress();
        // A coach mark waits for a big moment to finish rather than landing on top of it - and
        // for the table to arrive: the camera's swing-in and the stage banner (playtest,
        // 2026-10-05: the +/- Modifier's mark appeared while the camera was still panning).
        if (!_ui.Moments.Busy && !TableArriving) _teaching.DrainCoachMarks();
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
        _ui.InstantiateCardView(card, BoardOf(player), delay);

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
        // The bot does not act half way through a big moment (yours or its own): it waits for
        // the picture to catch up, as you have to.
        while (IsInsideTree() && _ui.Moments.Busy)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        return IsInsideTree(); // false: the scene was restarted or exited while it waited
    }

    // ------------------------------------------------------------------
    // Effect cards
    //
    // One path for both sides, and the rules of it live in Table (CanPlayEffect, EffectRefusal,
    // TryPlayEffect). What is left here is how a play is SHOWN.
    // ------------------------------------------------------------------

    /// Can this player play this ORDINARY modifier right now? The only rule is the Recall lock -
    /// everything else about a plain card is decided by the player's own arithmetic.
    private bool CanPlayModifierNow(Player owner, Card card) =>
        card != null && card.Effect == CardEffect.None && !_table.IsRecallLocked(owner, card);

    private bool CanPlayEffect(Player owner, Card card) => _table.CanPlayEffect(owner, card);
    private string EffectRefusal(Player owner, Card card) => _table.EffectRefusal(owner, card);

    /// Plays an effect card (Table.TryPlayEffect decides what it does) and shows it: the vetoed
    /// card burning, the effect card landing, any redrawn faces, the banner and the coach mark.
    private bool PlayEffectCard(Player owner, Card card, Card chosen = null)
    {
        // The numbers as they were, for the moments that tick a score over on impact.
        Player opponent = _table.OpponentOf(owner);
        int ownerBefore = owner.CurrentScore;
        int opponentBefore = opponent.CurrentScore;

        if (!_table.TryPlayEffect(owner, card, chosen, out Table.EffectPlay play)) return false;
        Player target = play.Target;

        // The vetoed card leaves the table. Burn it BEFORE the Veto card drops, so the eye follows
        // the card being destroyed rather than the one arriving - a score that ticks down on its
        // own tells the target nothing about WHICH card they just lost.
        if (play.Destroyed != null) _ui.BurnCardView(play.Destroyed, BoardOf(target));

        _ui.InstantiateCardView(card, BoardOf(play.BoardOwner));

        // The big moments (2026-10-05). The model has already changed; these catch the picture up
        // - and until they finish, ResolveTurn waits and the bot pauses (TableMoments.Busy).
        switch (card.Effect)
        {
            case CardEffect.Shave:
                _ui.PlayShave(target, opponentBefore);
                break;

            case CardEffect.TradeTotals:
                _ui.PlayTradeTotals(owner, ownerBefore, target, opponentBefore);
                break;

            case CardEffect.TradeHands:
                // Taken now: the hands on screen still hold the old cards (the played one already
                // lifted out); the refresh below rebuilds them traded, hidden until the fans land.
                _ui.PlayTradeHands(_ui.SnapshotHands());
                break;
        }

        // A card that rewrote a drawn card mutated a Card object that is already face-up on a
        // board. Without a redraw the board still reads 10 while the score has been paid at 2,
        // which is the one thing a card called Copy cannot afford to get wrong - so the card flips
        // into its new face, and the score ticks over while it is edge-on.
        if (CardEffects.RewritesDrawnCards(card.Effect))
        {
            _ui.PlayCopy(owner, ownerBefore, target, BoardOf(owner), BoardOf(target));
            _ui.RefreshCardFace(target.LastDrawnCard, BoardOf(target));
        }

        _ui.Toasts.ShowEffectBanner(play.Result.Narration); // the player has to SEE it

        // The ladder's promise, kept: you meet a card when it is used on you, and the game says
        // once what it was. Only the bot's cards - your own were introduced when you were dealt them.
        // Against the bot only: local 2-player has a person in the room to explain (and the
        // harness pass of 2026-10-05 caught Player 2's cards raising coach marks there).
        if (owner == _player2 && _isVsBot) _teaching.QueueCoachMark(card, fromOpponent: true);
        _ui.Refresh();

        // A re-opened BOT has to be sent round again: ResolveTurn refuses to move while either
        // side can act, and nothing else would ever call the bot back. How it goes round - now, or
        // as a note for the turn already in flight - is the bot's own business (Bot.TurnReopened).
        if (play.Reopened && _isVsBot && target == _player2) _bot.TurnReopened();

        return true;
    }

    private Control BoardOf(Player player) => (player == _player1) ? _p1BoardContainer : _p2BoardContainer;

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

        SetRules.Outcome outcome = SetRules.Describe(_player1.PlayerName, _player1.CurrentScore,
                                                     _player2.PlayerName, _player2.CurrentScore,
                                                     _gameState.TargetScore);
        if (outcome.Winner != 0) _gameState.RecordSetWinner(outcome.Winner);
        string title = outcome.Title;
        string why = outcome.Why;
        string buttonText = outcome.ButtonText;

        Action next;
        string secondText = null;
        Action second = null;
        bool matchOver = false;
        if (_gameState.CheckMatchWinner(out int matchWinner))
        {
            // The match-end screen (Alexander, 2026-09-30): "X Wins!", then the buttons - "Proceed
            // to Modifier Shop" after a win, "Start New Run" after a loss, and "Return to Main
            // Menu" under either. The result is banked here; the medals it earned are shown on the
            // shop screen that follows.
            matchOver = true;
            Player champion = (matchWinner == 1) ? _player1 : _player2;
            bool playerWon = matchWinner == 1;
            title = (_isVsBot && playerWon) ? "You Win!" : $"{champion.PlayerName} Wins!";
            RunData run = _inRun ? RunData.Instance : null;
            run?.CompleteMatch(_gameState.SetsWonPlayer1, playerWon);
            secondText = "Return to Main Menu";
            second = RestartToMenu;

            if (run != null && run.RunActive && !run.RunComplete)
            {
                buttonText = "Proceed to Modifier Shop";
                next = OpenIntermission;
            }
            else if (run != null && playerWon && run.RunComplete)
            {
                // The whole ladder is cleared: the next thing worth offering is Endless.
                buttonText = "Start Endless";
                next = () =>
                {
                    RunData.Instance.StartEndless();
                    StartNextMatch();
                };
            }
            else if (run != null)
            {
                bool wasEndless = run.Endless;
                buttonText = "Start New Run";
                next = () =>
                {
                    if (wasEndless) RunData.Instance.StartEndless();
                    else RunData.Instance.StartNewRun();
                    StartNextMatch();
                };
            }
            else
            {
                // Local 2-player, or a one-off solo table: the same match again.
                buttonText = "Play Again";
                next = () => RestartScene(sameMatch: true);

                // Local co-op: a short ad the players can close, after every 2nd finished match
                // (monetization-spec.md §2). On the match-end screen, before anything else starts.
                if (!_isVsBot)
                {
                    _coopMatchesFinished++;
                    if (_coopMatchesFinished % CoopMatchesPerAd == 0)
                    {
                        Action again = next, toMenu = second;
                        next = () => AdService.ShowInterstitial(this, again);
                        second = () => AdService.ShowInterstitial(this, toMenu);
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
        Label setInfo = _ui.Layout?.SetInfoLabel;
        if (setInfo != null)
        {
            setInfo.Text = title;
            setInfo.AddThemeFontSizeOverride("font_size", TableUi.SetInfoFont); // back from the big target
            setInfo.Visible = true; // hidden while it had nothing to say (TableUi.Refresh)
        }
        _prompts.ShowSetEnd(title, matchOver ? string.Empty : why, buttonText, next, secondText, second);
    }

    private string RunHeader()
    {
        RunData run = _inRun ? RunData.Instance : null;
        if (run == null) return string.Empty;
        // Alexander, 2026-09-30: the middle of the table shows the TARGET, not the stage - "if it's
        // 20, just say 20". The stage is announced once, by the slide-in at the start of the match.
        int target = _gameState.TargetScore;
        return target == 20 ? "20" : $"Target {target}";
    }

    /// Drops the run one rung either way and walks straight into that match (the debug row).
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
        // Only the single-player ladder has a run to spend medals on; local 2-player never does -
        // but it is the same scene now, so they are built either way and simply never opened.

        _shopOverlay = new ShopOverlay();
        AddChild(_shopOverlay);
        _shopOverlay.Setup(_ui.Cards.CreateCardView);

        _deckOverlay = new DeckOverlay();
        AddChild(_deckOverlay);
        _deckOverlay.Setup(_ui.Cards.CreateCardView);
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

        // The deck screen is full screen and sizes its own cards to the room it has; the table's
        // card size only gives it the aspect and the limits.
        _shopOverlay.Open(_ui.CardSize, () => _deckOverlay.Open(_ui.CardSize, StartNextMatch));
    }

    /// Reloading the scene is what resets the board, the scores and the set wins (the same path
    /// Restart takes); RunData is an autoload, so the run itself survives it.
    private void StartNextMatch()
    {
        if (RunData.Instance != null) RunData.Instance.AutoStartNextMatch = true;
        GetTree().ReloadCurrentScene();
    }


    /// A message about this player's side, shown in a floating toast (pass 36) - or empty.
    ///
    /// Over target and Bust are NOT words any more: the score box itself turns red (TableUi). The
    /// set-end panel says the rest once a set is over, so nothing is said while it is up.
    private string StatusFor(Player player)
    {
        if (_setOverPending) return string.Empty;

        // Holding is shown by the padlock on the score box (ScoreDisplay.UpdateHoldLock), not in words.
        if (player.IsHolding) return string.Empty;
        if (player.HasEndedTurn) return string.Empty;

        // A card is picked up: spell the arithmetic out. This is the game's teaching moment, so
        // it is shown as a full sum rather than just the answer.
        Card picked = SelectedFor(player);
        if (picked != null)
        {
            if (picked.Effect != CardEffect.None) return _table.EffectPreview(player, picked);
            if (_table.IsRecallLocked(player, picked)) return "Just recalled - playable from your next turn";
            // A plain Modifier says nothing here: its result is shown on the score itself.
        }

        return string.Empty;
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
            _ui.InstantiateCardView(card, BoardOf(player));
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


    /// Plain and flip-value Modifiers only; effect cards are marked when a coach-mark explains
    /// them. Ladder only - local 2-player deals random hands and has no profile to write to.
    private void NoteModifierMet(Card card)
    {
        if (!_inRun || RunData.Instance == null) return;
        if (RunData.Instance.MarkPlainModifierMet(card)) AnnounceCollectionComplete();
    }

    private void AnnounceCollectionComplete()
    {
        _ui.Toasts.ShowEffectBanner("Collection complete! Your deck now has a gilded back.");
        _ui.ApplyRankTheme();
    }
}
