using Godot;
using System;
using System.Collections.Generic;

public partial class GameManager : Node
{
    private GameState _gameState;
    private Player _player1;
    private Player _player2;

    [ExportGroup("Player 1 UI")]
    [Export] private Label _p1ScoreLabel;
    [Export] private Label _p1StatusLabel;
    [Export] private Label _p1WinsLabel;
    [Export] private Control _p1BoardContainer;
    [Export] private Control _p1HandContainer;
    [Export] private Button _p1EndTurnButton; // 2-player scene: P1's own End Turn / Hold row under their hand
    [Export] private Button _p1HoldButton;

    [ExportGroup("Player 2 UI (AI)")]
    [Export] private Label _p2ScoreLabel;
    [Export] private Label _p2StatusLabel;
    [Export] private Label _p2WinsLabel;
    [Export] private Control _p2BoardContainer;
    [Export] private Control _p2HandContainer;
    [Export] private Control _p2Rotator;
    [Export] private Button _p2EndTurnButton; // 2-player scene: inside P2Rotator, so it flips with P2's side
    [Export] private Button _p2HoldButton;

    [ExportGroup("Shared UI")]
    [Export] private Label _roundInfoLabel;
    [Export] private OptionButton _gameModeButton;
    [Export] private CheckButton _mirrorToggle; // 2-player scene only: rotate P2's side 180 degrees
    [Export] private Button _startButton;
    [Export] private Button _endTurnButton; // solo scene: one shared pair in the middle panel (Player 1, the human)
    [Export] private Button _holdButton;
    [Export] private Control _mainDeckPosition;

    [ExportGroup("System UI")]
    [Export] private Button _restartButton;
    [Export] private Button _exitButton;

    private Random _random = new Random();
    private PackedScene _cardViewScene = GD.Load<PackedScene>("res://CardView.tscn");
    private bool _isGameStarted = false;
    private bool _isVsBot = false;
    private bool _aiTurnInProgress = false; // the bot is "thinking" for this deal (the human is NOT locked meanwhile)
    private bool _roundOverPending = false; // the round-end explanation is up; nothing moves until it's acknowledged

    // One opponent-facing card per side per deal. Two in one deal cannot be read, however well
    // they animate - see claude/stage-ladder-spec.md.
    private bool _p1PlayedEffectThisDeal = false;
    private bool _p2PlayedEffectThisDeal = false;

    /// Set when the bot's turn is re-opened WHILE that turn is still running (the player answers
    /// a card during one of its animation pauses). Calling ProcessAiTurn there would be swallowed
    /// by its own guard, and the tail of the in-flight turn would then end the bot's turn anyway -
    /// so the flag makes it go round again instead. Without this, whether the bot gets its answer
    /// depends on which pause the player happened to interrupt.
    private bool _p2ReopenedMidTurn = false;

    // ------------------------------------------------------------------
    // Modifier hands
    //
    // Each match deals every player a fresh random hand: non-zero values in -4..+4, and a 1-in-10
    // chance for any of them to be a "+/-" (flip) card the player can swap between plus and minus
    // before committing it. Cards are spent for the whole match, not the round.
    // ------------------------------------------------------------------
    private const int ModifierHandSize = 4;
    private const int MaxModifierMagnitude = 4;
    private const double FlipCardChance = 0.10;

    // ------------------------------------------------------------------
    // Tap to pick up, tap again to play
    //
    // Nothing is spent by a single tap. Tapping a hand card picks it up (it lifts, the others dim,
    // and the player's status line spells out the sum it would make); the End Turn / Hold row is
    // then replaced by big Play / +- / Put back buttons, and tapping the same card again plays it.
    // Chosen over long-press or drag-and-drop: both need sustained precision, which is exactly what
    // small children and older hands struggle with.
    // ------------------------------------------------------------------
    // The single-player ladder run, when there is one (RunData autoload). Local 2-player ignores
    // it entirely and keeps its self-contained randomized hands.
    private bool _inRun = false;

    private Card _p1SelectedCard;
    private Card _p2SelectedCard;
    private HBoxContainer _p1ConfirmRow;
    private HBoxContainer _p2ConfirmRow;
    private Button _p1PlayButton;
    private Button _p2PlayButton;
    private Button _p1FlipButton;
    private Button _p2FlipButton;
    private Control _p1ActionRow;
    private Control _p2ActionRow;

    // ------------------------------------------------------------------
    // UI scaling
    //
    // project.godot uses a 720x720 base viewport with stretch aspect "expand", so the SHORT
    // side of whatever screen we're on is always 720 design-pixels and the long side grows.
    // Everything below is sized in those design pixels. ApplyResponsiveLayout enlarges that
    // base (shrinking the whole UI uniformly) when a screen is too short for the full layout.
    // ------------------------------------------------------------------
    private const int BoardSlots = 9;                       // 3x3 board
    private const int WinsToTakeMatch = GameState.RoundsToWinMatch;  // one chip slot per win needed
    private static readonly Vector2 BaseCardSize = new Vector2(84, 114); // Kenney cards are 140x190
    private const float HandCardScale = 0.8f;
    private const float MinCardScale = 0.6f;
    private float _cardScale = 1f;
    private BoxContainer _mainLayout;

    // The middle panel's two added lines: the target, big, above the round line; and a banner
    // under it that says what an effect card just did. Both are built in code (BuildTableBanners)
    // so the two .tscn scenes stay as they are.
    private Label _targetLabel;
    private Label _effectBanner;
    private uint _effectBannerToken;   // so a stale timer never wipes a newer message

    private Vector2 CardSize => BaseCardSize * _cardScale;
    private Vector2 HandCardSize => BaseCardSize * _cardScale * HandCardScale;

    // ------------------------------------------------------------------
    // Art (Kenney Boardgame Pack, CC0 - see assets/kenney/LICENSE.txt)
    // Regions are taken from the pack's playingCardBacks.xml / chips.xml atlases.
    // ------------------------------------------------------------------
    private Texture2D _cardSheet;
    private Texture2D _chipSheet;
    private static readonly Rect2 RegionMain = new Rect2(280, 760, 140, 190);   // cardBack_green3 - main deck cards
    private static readonly Rect2 RegionPlus = new Rect2(280, 380, 140, 190);   // cardBack_blue3  - positive modifiers
    private static readonly Rect2 RegionMinus = new Rect2(0, 380, 140, 190);    // cardBack_red3   - negative modifiers
    private static readonly Rect2 RegionDeckBack = new Rect2(140, 190, 140, 190); // cardBack_green4 - face-down deck
    // The Kenney sheet has three hues and red/green/blue are already minus/main/plus, so there is
    // no fourth back to give an effect card: it takes a plain green one and wears EffectTint, or
    // a Shave landing in the opponent's grid would read as a card they just drew. A later pass
    // draws the real effect face in code, the way BuildFlipFace draws the blue-over-red one.
    private static readonly Rect2 RegionEffect = new Rect2(140, 0, 140, 190);     // cardBack_green1
    private static readonly Color EffectTint = new Color(1.15f, 0.85f, 1.35f);    // violet wash
    private static readonly Rect2 RegionChipWon = new Rect2(0, 194, 68, 68);    // chipGreen_border
    private static readonly Rect2 RegionChipEmpty = new Rect2(68, 0, 68, 68);   // chipWhite_border
    private const float ChipSize = 28f;
    private HBoxContainer _p1WinChips;
    private HBoxContainer _p2WinChips;

    /// The felt colour of the 2-player table and of stage 1 - project.godot's clear colour.
    private static readonly Color DefaultTableColor = new Color(0.07f, 0.24f, 0.13f);

    /// Tint applied to the standard (main deck) card art for the rank in play. See ApplyRankTheme.
    private Color _rankCardTint = Colors.White;

    private AudioStreamPlayer _sfxSlide;
    private AudioStreamPlayer _sfxPlace;

    // The intermission between two rungs: the market, then the deck. Overlays over this same
    // table rather than scenes of their own, so the run never leaves the table it is playing on.
    private ShopOverlay _shopOverlay;
    private DeckOverlay _deckOverlay;

    public override void _Ready()
    {
        _gameState = new GameState();
        _player1 = new Player("Player 1");
        _player2 = new Player("Player 2");
        DealMatchHands();

        _cardSheet = GD.Load<Texture2D>("res://assets/kenney/cards.png");
        _chipSheet = GD.Load<Texture2D>("res://assets/kenney/chips.png");
        _sfxSlide = CreateSfx("res://assets/kenney/sfx/cardSlide1.ogg");
        _sfxPlace = CreateSfx("res://assets/kenney/sfx/cardPlace1.ogg");

        _mainLayout = GetNodeOrNull<BoxContainer>("GameUI/MainLayout");

        // P2Rotator spins around its own centre, so keep the pivot there whatever size it ends up.
        if (_p2Rotator != null) _p2Rotator.Resized += () => _p2Rotator.PivotOffset = _p2Rotator.Size / 2f;

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
            _mirrorToggle.Toggled += _ => { ApplyResponsiveLayout(); UpdateUI(); };
        }

        // Connect button signals. The solo scene has one shared End Turn / Hold pair in the middle
        // panel (it belongs to Player 1, the human); the 2-player scene gives each player their own
        // pair under their hand instead.
        _startButton.Pressed += OnStartButtonPressed;
        if (_endTurnButton != null) _endTurnButton.Pressed += () => OnEndTurnPressed(_player1);
        if (_holdButton != null) _holdButton.Pressed += () => OnHoldPressed(_player1);
        if (_p1EndTurnButton != null) _p1EndTurnButton.Pressed += () => OnEndTurnPressed(_player1);
        if (_p1HoldButton != null) _p1HoldButton.Pressed += () => OnHoldPressed(_player1);
        if (_p2EndTurnButton != null) _p2EndTurnButton.Pressed += () => OnEndTurnPressed(_player2);
        if (_p2HoldButton != null) _p2HoldButton.Pressed += () => OnHoldPressed(_player2);
        if (_restartButton != null) _restartButton.Pressed += OnRestartPressed;
        if (_exitButton != null) _exitButton.Pressed += OnExitPressed;

        // Set initial waiting message (UpdateUI below disables every action button until Start).
        _roundInfoLabel.Text = "Press Start Game to Begin";

        // Show the empty 3x3 boards and the win chips before the game starts.
        FillBoardWithSlots(_p1BoardContainer);
        FillBoardWithSlots(_p2BoardContainer);
        _p1WinChips = EnsureWinChips(_p1WinsLabel);
        _p2WinChips = EnsureWinChips(_p2WinsLabel);

        BuildRoundEndOverlay();
        BuildHowToPlay();
        BuildConfirmRows();
        BuildIntermissionOverlays();
        BuildTableBanners();
        BuildDebugRow();
        ConfigureStatusLabel(_p1StatusLabel);
        ConfigureStatusLabel(_p2StatusLabel);

        GetTree().Root.SizeChanged += ApplyResponsiveLayout;
        ApplyResponsiveLayout();
        UpdateUI();

        // Coming back from the deck screen: the player already pressed a button to get here, so deal
        // the next match instead of showing them a Start button. (Solo scene only.)
        if (_gameModeButton == null && RunData.Instance != null && RunData.Instance.AutoStartNextMatch)
        {
            RunData.Instance.AutoStartNextMatch = false;
            CallDeferred(MethodName.OnStartButtonPressed);
        }
    }

    public override void _ExitTree()
    {
        if (GetTree() != null) GetTree().Root.SizeChanged -= ApplyResponsiveLayout;
    }

    // ------------------------------------------------------------------
    // Responsive layout
    // ------------------------------------------------------------------
    // Approximate design-pixel footprint of the whole UI (both sides + control panel) in each
    // orientation. If the screen can't show that much at the 720px base, the base is enlarged so
    // the entire UI scales down uniformly instead of cropping. (Phones in portrait are ~720x1560,
    // desktop landscape is 1280x720 - both fit as-is; a short portrait desktop window doesn't.)
    // (Each side is stats + board + hand + its own End Turn / Hold row in the 2-player scene; the
    // middle panel also carries the How to Play button.)
    private const float BaseSide = 720f;
    private static readonly Vector2 NeedPortrait = new Vector2(470, 1520);
    private static readonly Vector2 NeedLandscape = new Vector2(1040, 690);

    private void ApplyResponsiveLayout()
    {
        Window root = GetTree().Root;
        Vector2 win = root.Size;
        bool portrait = win.Y > win.X;

        // What the viewport would be at the plain 720px base, and how much bigger it must be.
        float baseScale = Mathf.Min(win.X / BaseSide, win.Y / BaseSide);
        Vector2 baseViewport = win / Mathf.Max(baseScale, 0.001f);
        Vector2 need = portrait ? NeedPortrait : NeedLandscape;
        float k = Mathf.Max(1f, Mathf.Max(need.X / baseViewport.X, need.Y / baseViewport.Y));
        Vector2I contentSize = (Vector2I)(new Vector2(BaseSide, BaseSide) * k).Round();
        if (root.ContentScaleSize != contentSize) root.ContentScaleSize = contentSize; // re-fires SizeChanged once

        Vector2 vp = GetViewport().GetVisibleRect().Size;

        // Stack the two player sides vertically in portrait, side by side in landscape.
        if (_mainLayout != null)
        {
            _mainLayout.Vertical = portrait;

            // Portrait: P2 on top, P1 at the bottom (near the thumbs). Landscape: P1 left, P2 right.
            Control p1Side = _mainLayout.GetNodeOrNull<Control>("Player1Side");
            Control p2Side = _mainLayout.GetNodeOrNull<Control>("Player2Side");
            Control panel = _mainLayout.GetNodeOrNull<Control>("SharedControlPanel");
            if (p1Side != null && p2Side != null && panel != null)
            {
                _mainLayout.MoveChild(portrait ? p2Side : p1Side, 0);
                _mainLayout.MoveChild(panel, 1);
                _mainLayout.MoveChild(portrait ? p1Side : p2Side, 2);
            }
        }

        // Player 2's side faces the other way when the "Mirror" toggle is on (face-to-face play).
        if (_p2Rotator != null) _p2Rotator.RotationDegrees = IsMirrored ? 180f : 0f;

        // The base-size adjustment above guarantees the viewport is at least `need`, so this only
        // trims the cards in the rare case the estimate is a little short.
        _cardScale = Mathf.Clamp(Mathf.Min(vp.Y / need.Y, vp.X / need.X), MinCardScale, 1f);

        ResizeBoard(_p1BoardContainer);
        ResizeBoard(_p2BoardContainer);
        if (_mainDeckPosition != null) _mainDeckPosition.CustomMinimumSize = CardSize;
        RefreshHandUI();
        CallDeferred(MethodName.UpdateRotatorSize);
    }

    private bool IsMirrored => _mirrorToggle != null && _mirrorToggle.ButtonPressed;

    /// Player2Side (CenterContainer) > P2Holder (plain Control) > P2Rotator (full-rect, rotated) > Layout.
    /// Containers reset their children's rotation every time they re-lay them out, which is why
    /// the rotated node must NOT sit directly in a container - the holder takes the hit instead.
    /// The holder reports 0x0 on its own (its content is anchored, not laid out), so copy the
    /// Layout's minimum size onto it whenever the content changes.
    private void UpdateRotatorSize()
    {
        if (_p2Rotator == null || _p2Rotator.GetChildCount() == 0) return;
        if (_p2Rotator.GetChild(0) is Control layout && _p2Rotator.GetParent() is Control holder)
        {
            holder.CustomMinimumSize = layout.GetCombinedMinimumSize();
            _p2Rotator.RotationDegrees = IsMirrored ? 180f : 0f;
        }
    }

    private void ResizeBoard(Control board)
    {
        if (board == null) return;
        foreach (Node child in board.GetChildren())
        {
            if (child is Control slot)
            {
                slot.CustomMinimumSize = CardSize;
                foreach (Node inner in slot.GetChildren())
                {
                    if (inner is TextureRect view) ApplyCardSize(view, CardSize);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Buttons
    // ------------------------------------------------------------------
    private void OnRestartPressed()
    {
        // Reloading the scene rebuilds GameManager, GameState and both Players from scratch,
        // so this fully resets the match (round wins, scores, hands) for the current mode.
        GD.Print("Restarting game...");
        GetTree().ReloadCurrentScene();
    }

    private void OnExitPressed()
    {
        GD.Print("Exiting game...");
        GetTree().Quit();
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
        DealMatchHands(); // the hand has to last all three rounds of the match

        StartNewRound(); // UpdateUI enables the End Turn / Hold buttons
    }

    /// Puts the solo scene onto the ladder: picks up the run in progress (or starts one), and takes
    /// this venue's target score. Local 2-player is never part of a run.
    private void BeginRunMatch()
    {
        _inRun = false;
        if (!_isVsBot)
        {
            ApplyRankTheme(); // the clear colour is global: put the plain felt back for 2-player
            return;
        }

        RunData run = RunData.Instance;
        if (run == null)
        {
            ApplyRankTheme();
            return; // autoload missing (e.g. the scene opened on its own) - play a one-off
        }

        if (!run.RunActive || run.RunComplete) run.StartNewRun();

        _inRun = true;
        _gameState.TargetScore = run.CurrentTarget;
        _player2.PlayerName = run.CurrentStep.Opponent;
        ApplyRankTheme();
    }

    /// A fresh modifier hand for both players. Cards are spent for the whole match, so this runs
    /// once per match - not per round.
    ///
    /// In a run, Player 1's hand is drawn at random from the 12-card deck they built on the deck
    /// screen: the deck is chosen, the hand is not. Everywhere else (local 2-player, and the bot)
    /// the hand is dealt at random.
    private void DealMatchHands()
    {
        List<Card> runHand = _inRun ? RunData.Instance?.DrawMatchHand() : null;
        if (runHand != null && runHand.Count > 0)
        {
            _player1.ModifierHand.Clear();
            _player1.ModifierHand.AddRange(runHand);
        }
        else
        {
            _player1.DealRandomModifierHand(_random, ModifierHandSize, FlipCardChance, MaxModifierMagnitude);
        }

        DealAiHand();
        ClearSelections();
    }

    /// The AI's hand for this match, built to the rung's recipe rather than rolled flat: stage 1
    /// is the standard game with no "+/-" cards at all, every stage above it guarantees exactly
    /// one, and stages 4-8 spend one of the four slots on that stage's effect card.
    ///
    /// Local 2-player and a runless solo scene keep the old flat roll.
    private void DealAiHand()
    {
        RunData run = _inRun ? RunData.Instance : null;
        if (run == null)
        {
            _player2.DealRandomModifierHand(_random, ModifierHandSize, FlipCardChance, MaxModifierMagnitude);
            return;
        }

        RunData.LadderStep step = run.CurrentStep;
        List<Card> hand = new List<Card>();

        // Plain cards first: no flip chance here, because whether this stage has a "+/-" card is
        // the stage's decision, not a dice roll.
        for (int i = 0; i < ModifierHandSize; i++)
        {
            hand.Add(Player.CreateRandomModifier(_random, 0.0, MaxModifierMagnitude));
        }

        int flipIndex = -1;
        if (step.AiHasFlipCards)
        {
            flipIndex = _random.Next(hand.Count);
            Card card = hand[flipIndex];
            hand[flipIndex] = new Card(Math.Abs(card.Value), CardType.Modifier, "", isFlip: true);
        }

        // The stage's effect card takes one of the four slots.
        //
        // From stage 4 up, if the card this rung is NAMED for is not built yet, the bot carries a
        // finished effect instead of nothing. That is what Alexander was seeing as "the AI is
        // sometimes starting a match without their new modifier card": a rung naming an unwired
        // Trade was dealt four ordinary cards and played exactly like the rung below it.
        //
        // Pass 5 wired both Trades, so stages 4-7 each deal the card they are named for now. What
        // still falls back is stage 8 (no card designed yet) and stages 9-10 (which name nothing
        // at all until the randomizer rolls their ruleset).
        //
        // Still ONE card, dealt once for the whole match and spent when it is played. Hands are
        // not topped up between rounds: the drama of a stage card is that there is one of it.
        CardEffect aiEffect = step.AiEffect;
        if (!CardEffects.IsWired(aiEffect) && run.MatchNumber >= 4)
        {
            List<CardEffect> wired = CardEffects.WiredEffects();

            // Only cards this rung has already EARNED. The ladder's promise is that you meet a
            // card across the table at its own stage and can buy it one visit later; a fallback
            // that reached for anything wired would have handed the player a stage 7 Shave at
            // stage 5, two rungs before the game introduces it and two before the market will
            // sell it. It became a live risk the moment stage 5 lost its own card.
            wired.RemoveAll(effect => RunData.StageThatIntroduces(effect) > run.MatchNumber);

            if (wired.Count > 0) aiEffect = wired[_random.Next(wired.Count)];
        }

        // Never the slot the "+/-" card just took: the recipe is three plain cards (one of them
        // a "+/-") plus the effect, and eating the flip card would quietly undo stage 2.
        if (CardEffects.IsWired(aiEffect))
        {
            int effectIndex = _random.Next(hand.Count);
            if (effectIndex == flipIndex) effectIndex = (effectIndex + 1) % hand.Count;
            hand[effectIndex] = CardEffects.Create(aiEffect, _random);
        }

        _player2.ModifierHand = hand;
        _player2.EnsureBothSigns(_random);
    }

    private void StartNewRound()
    {
        _player1.ResetForNewRound();
        _player2.ResetForNewRound();
        ClearSelections();
        ClearEffectBanner();

        // Clear old cards and lay out fresh empty 3x3 boards
        FillBoardWithSlots(_p1BoardContainer);
        FillBoardWithSlots(_p2BoardContainer);

        DealCards();
    }

    // ------------------------------------------------------------------
    // Deals
    //
    // There is no turn order. A round is a series of deals: every player who isn't holding draws
    // a card at the same time, then both play modifiers and press End Turn / Hold blind. Once
    // neither player can act any more the deal is resolved (ResolveDeal) - busts, both holding,
    // or simply the next deal.
    // ------------------------------------------------------------------
    private void DealCards()
    {
        _player1.HasEndedTurn = false;
        _player2.HasEndedTurn = false;

        // Both are about this deal only: who has drawn what, and who has already reached across
        // the table once.
        _player1.LastDrawnCard = null;
        _player2.LastDrawnCard = null;
        _p1PlayedEffectThisDeal = false;
        _p2PlayedEffectThisDeal = false;
        _p2ReopenedMidTurn = false;

        DrawCardFor(_player1, _p1BoardContainer);
        DrawCardFor(_player2, _p2BoardContainer);

        UpdateUI();

        if (_isVsBot) ProcessAiTurn();
    }

    private void DrawCardFor(Player player, Control boardContainer)
    {
        if (player.IsHolding) return;

        int cardValue = _random.Next(1, 11);
        player.CurrentScore += cardValue;

        Card drawnMainCard = new Card(cardValue, CardType.Main, cardValue.ToString());
        player.ActiveCardsOnBoard.Add(drawnMainCard);
        player.LastDrawnCard = drawnMainCard; // Copy needs to name this exact card

        GD.Print($"{player.PlayerName} drew a {cardValue}. Score: {player.CurrentScore}");

        InstantiateCardView(drawnMainCard, boardContainer);

        // Going over the target here is NOT a bust yet - the player may still play a minus card
        // before ending the turn. Busts are only decided in ResolveDeal.
    }

    /// Called whenever someone finishes their part of the deal (End Turn, Hold, or the bot).
    /// Does nothing until BOTH players are done; then either ends the round or deals again.
    private void ResolveDeal()
    {
        if (!_isGameStarted || _gameState.IsGameOver || _roundOverPending) return;

        if (_player1.CanAct || _player2.CanAct)
        {
            UpdateUI(); // one side is still deciding
            return;
        }

        int target = _gameState.TargetScore;
        bool anyBust = _player1.CurrentScore > target || _player2.CurrentScore > target;
        bool bothHolding = _player1.IsHolding && _player2.IsHolding;

        if (anyBust || bothHolding)
        {
            EndRound();
            return;
        }

        DealCards();
    }

    // ------------------------------------------------------------------
    // How good the bot is
    //
    // Skill rides on the RANK, so it escalates on the same rhythm as the board colour and the
    // player feels the opponent change every second rung. See claude/ai-skill-tiers.md.
    // ------------------------------------------------------------------
    private enum AiSkill
    {
        /// Bronze, Silver (stages 1-4). One card per deal, blind to your hand. The opponent that
        /// teaches the game: it never surprises you while you are still learning what a +/- does.
        Basic,

        /// Gold, Ruby (stages 5-8). It plays the BOARD: chains cards while each one improves its
        /// position, which is what lets it play two minus cards to climb back under a bust.
        Chains,

        /// Obsidian (stages 9-10). It plays YOU: reads your hand to decide whether its own score
        /// is actually safe, and weighs the match score when taking a risk.
        Reads,
    }

    /// At most this many ordinary cards in one deal, once the bot chains. The cap is the point:
    /// an unbounded loop empties the hand in a single deal and reads as a machine having a fit.
    private const int MaxAiChainedCards = 3;

    /// Trade Hands is a bet on the rounds still to come, so the bot only makes it once its OWN
    /// hand is spent - it must be left holding at most this many cards after the trade card goes.
    /// Without this floor it fires on the first deal of the match, when both hands are full and
    /// spending a card to gain one is a swap for its own sake.
    private const int MaxHandToTradeAway = 1;

    private AiSkill CurrentAiSkill()
    {
        RunData run = _inRun ? RunData.Instance : null;
        if (run == null) return AiSkill.Basic; // a one-off solo match is never a boss fight

        switch (run.CurrentStep.Rank)
        {
            case 4: return AiSkill.Reads;
            case 3:
            case 2: return AiSkill.Chains;
            default: return AiSkill.Basic;
        }
    }

    private async void ProcessAiTurn()
    {
        if (_aiTurnInProgress || !_player2.CanAct) return; // never run two AI turns at once
        _aiTurnInProgress = true;
        UpdateUI(); // shows "Thinking..." on the bot's side

        //1. Wait a moment to let the player see the AI's drawn card
        await ToSignal(GetTree().CreateTimer(1.0f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return; // scene was restarted/exited mid-deal

        //2. First, whether to reach across the table at all. At most one such card per deal, and
        //   only when it decides the round - see TryAiPlayEffectCard.
        if (TryAiPlayEffectCard())
        {
            await ToSignal(GetTree().CreateTimer(1.5f), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return;
        }

        //3. Then its own arithmetic. From Gold up it keeps going while each card strictly improves
        //   its position - capped, and with the same pause between each, so the player can follow
        //   a chain rather than watch a hand evaporate.
        AiSkill skill = CurrentAiSkill();
        int maxCards = (skill == AiSkill.Basic) ? 1 : MaxAiChainedCards;

        for (int played = 0; played < maxCards; played++)
        {
            if (!TryAiPlayModifierCard(mayChain: skill != AiSkill.Basic)) break;

            //Wait 1.5 seconds to let the player see each card land
            await ToSignal(GetTree().CreateTimer(1.5f), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return;
        }

        _aiTurnInProgress = false;

        // Answered while it was thinking: start the turn again against the board as it stands now,
        // rather than closing a turn that was re-opened halfway through.
        if (_p2ReopenedMidTurn)
        {
            _p2ReopenedMidTurn = false;
            ProcessAiTurn();
            return;
        }

        int target = _gameState.TargetScore;

        //4. Still over the target now = the bot ends its turn and busts when the deal resolves.
        if (_player2.CurrentScore > target)
        {
            GD.Print($"AI ends its turn over the target at {_player2.CurrentScore}");
            _player2.HasEndedTurn = true;
            ResolveDeal();
            return;
        }

        int holdThreshold = Math.Max(10, target - 2);

        // Chasing a score the player has already locked in: play to BEAT it, not to match it.
        // The first build set the threshold to Player 1's score itself, so against a locked 19 the
        // bot held at 19 - a tie, which is replayed rather than won. That is not a difficulty
        // setting, it is the bot declining a win it could take, so it is fixed at every tier.
        if (_player1.IsHolding && _player1.CurrentScore <= target)
        {
            holdThreshold = Math.Min(target, _player1.CurrentScore + 1);
        }
        else if (skill == AiSkill.Reads)
        {
            // Level 3 weighs the MATCH, not just the round: behind, there is nothing left to
            // protect and it pushes; level on the DECIDER, a bust loses everything and it plays
            // safe.
            //
            // "The decider" was written as `mine == yours && mine > 0`, which was only ever
            // correct because the match was best of three - 1-1 was the only level score that
            // could end it. At best of five that test fires at 1-1 and at 2-2, and 1-1 is an
            // ordinary mid-match round where playing safe just loses ground. The rule it was
            // always trying to state is: level, with either side one win from the match.
            int mine = _gameState.RoundsWonPlayer2;
            int yours = _gameState.RoundsWonPlayer1;
            if (mine < yours) holdThreshold += 1;
            else if (mine == yours && mine == GameState.RoundsToWinMatch - 1) holdThreshold -= 1;

            // ...and it READS PLAYER 1'S HAND, for the one decision it otherwise gets wrong: is my
            // score actually safe? Against a player sitting on 15 with a +4 in hand, holding on 18
            // is not safe, so it keeps pushing for the target.
            //
            // This is hidden information, on purpose, and only from Obsidian: the early ladder is
            // honest and the top of it is meant to feel like the opponent knows what you are
            // holding. Do not "fix" this - if it reads as cheating in playtesting, delete it.
            if (!_player1.IsHolding && CanBeatWithOrdinary(_player1, _player2.CurrentScore, target))
            {
                holdThreshold = target;
            }

            holdThreshold = Math.Clamp(holdThreshold, 1, target);
        }

        if (_player2.CurrentScore >= holdThreshold || _player2.CurrentScore == target)
        {
            GD.Print($"AI decides to HOLD at {_player2.CurrentScore} (Target: {target})");
            _player2.IsHolding = true;
        }
        else
        {
            _player2.HasEndedTurn = true;
        }

        ResolveDeal();
    }

    /// Whether the bot reaches across the table this deal, and with what.
    ///
    /// The rule behind every branch: an effect card is only spent when it DECIDES something. A
    /// Copy spent to move two points, or a Shave on a score the bot is already beating, is the
    /// difference between a boss that feels hard and one that feels cheap.
    private bool TryAiPlayEffectCard()
    {
        if (_p2PlayedEffectThisDeal) return false;

        int target = _gameState.TargetScore;
        Player me = _player2;
        Player you = _player1;

        // Trade Totals - I take their score, they take mine. The biggest reach in the game, so it
        // is asked first: when this and a Copy would both rescue the same deal, taking a whole
        // legal total off them beats trimming my own draw.
        //
        // It is never a free round. CanPlay refuses a holding opponent, so the player it lands on
        // can always still act - and the answering rule re-opens their turn, handing them my wreck
        // and a chance to climb out of it. What the card buys is the total, and the total has to be
        // worth it on its own.
        foreach (Card card in me.ModifierHand)
        {
            if (card.Effect != CardEffect.TradeTotals || !CanPlayEffect(me, card)) continue;

            int theirs = you.CurrentScore;
            if (theirs > target) continue;           // never take a bust off them
            if (theirs <= me.CurrentScore) continue; // and never trade down

            if (me.CurrentScore > target)
            {
                // Busted, and their legal total ends the problem outright - unless my own hand was
                // going to get me under anyway, in which case keep this for a deal where nothing
                // else will. Asked against my own skill, since from Gold up I can chain my way back.
                if (CanGetUnder(me, me.CurrentScore, target, mayChain: CurrentAiSkill() != AiSkill.Basic)) continue;
                return PlayEffectCard(me, card);
            }

            // Not busted. Same bar as Copy below: an effect card is not worth a point or two, so
            // only spend it when it carries me from "not good enough" to "good enough" in one move.
            int wantTotalAtLeast = Math.Max(10, target - 2);
            if (me.CurrentScore >= wantTotalAtLeast) continue;   // already where I need to be
            if (theirs < wantTotalAtLeast) continue;             // their total does not get me there
            if (CanBeatWithOrdinary(me, wantTotalAtLeast - 1, target)) continue; // a plain card does

            return PlayEffectCard(me, card);
        }

        // Copy - my drawn card becomes theirs. Entirely my own business: it never touches their
        // card, their score or their turn, so the only question is whether it changes MY result.
        foreach (Card card in me.ModifierHand)
        {
            if (card.Effect != CardEffect.Copy || !CanPlayEffect(me, card)) continue;

            // CanPlayEffect has already guaranteed both drawn cards exist and differ.
            int mine = me.LastDrawnCard.Value;
            int theirs = you.LastDrawnCard.Value;
            int newScore = me.CurrentScore - mine + theirs;

            if (newScore > target) continue; // never copy myself into a bust

            if (me.CurrentScore > target)
            {
                // Busted, and this card takes the bust away. Spend it - unless an ordinary card
                // would already have done the job, in which case keep the Copy for a deal where
                // nothing else will. Asked against my OWN skill, since from Gold up I can chain
                // two cards to climb back under.
                if (CanGetUnder(me, me.CurrentScore, target, mayChain: CurrentAiSkill() != AiSkill.Basic)) continue;
                return PlayEffectCard(me, card);
            }

            // Not busted. An effect card is not worth a point or two, so only spend it when it
            // carries me from "not good enough" to "good enough" in one move.
            if (newScore <= me.CurrentScore) continue;

            int wantAtLeast = Math.Max(10, target - 2);
            if (_player1.IsHolding && _player1.CurrentScore <= target)
            {
                wantAtLeast = Math.Min(target, _player1.CurrentScore + 1);
            }

            if (me.CurrentScore >= wantAtLeast) continue;   // already where I need to be
            if (newScore < wantAtLeast) continue;           // and this does not get me there
            if (CanBeatWithOrdinary(me, wantAtLeast - 1, target)) continue; // a plain card does it

            return PlayEffectCard(me, card);
        }

        // Shave - their score is locked below the target, so it can never move again and there is
        // nothing to wait for. It only ever matters where one point changes the result, which is
        // exactly when I am level with them or behind: ahead of them it is a wasted card.
        foreach (Card card in me.ModifierHand)
        {
            if (card.Effect != CardEffect.Shave || !CanPlayEffect(me, card)) continue;
            if (me.CurrentScore > target) continue;               // fix my own bust first
            if (me.CurrentScore > you.CurrentScore) continue;      // already winning
            if (me.CurrentScore < you.CurrentScore - 1) continue;  // one point cannot bridge more than one

            // And never instead of simply winning: if an ordinary card already beats their locked
            // score, play that and keep the Shave (priority 1 in the spec's decision order).
            if (CanBeatWithOrdinary(me, you.CurrentScore, target)) continue;

            return PlayEffectCard(me, card);
        }

        // Trade Hands - I take everything they are still holding, they take what I have left. Last,
        // because it decides nothing about THIS deal: it is a bet on the rounds to come, while
        // every card above it is a bet on the one being played.
        //
        // The signal is my own hand being spent, not theirs being good. How many cards someone
        // holds is visible across any real table, so every tier may count them; what is IN a hand
        // is hidden information, and pass 3 licensed reading that at Obsidian only. So the Ruby bot
        // that first carries this card trades on the honest signal - "I have nothing left and they
        // do" - and the Obsidian bot additionally refuses a trade that would not gain it anything.
        foreach (Card card in me.ModifierHand)
        {
            if (card.Effect != CardEffect.TradeHands || !CanPlayEffect(me, card)) continue;
            if (me.CurrentScore > target) continue; // fix my own bust before playing for next round

            // Priority 1 in the spec's decision order: if a plain card already takes the round off
            // a score they have locked in, take the round and keep this.
            if (you.IsHolding && CanBeatWithOrdinary(me, you.CurrentScore, target)) continue;

            // This card is what empties my hand, so count what is left AFTER it goes.
            int myRemaining = me.ModifierHand.Count - 1;
            if (myRemaining > MaxHandToTradeAway) continue;      // my hand is not spent yet
            if (you.ModifierHand.Count <= myRemaining) continue; // and theirs has to be bigger

            if (CurrentAiSkill() == AiSkill.Reads
                && HandStrength(you) <= HandStrength(me, ignore: card)) continue;

            return PlayEffectCard(me, card);
        }

        return false;
    }

    /// What a hand is worth, for the one decision that needs to compare two of them.
    ///
    /// Deliberately NOT "cards that could be played legally this round": hands last the whole
    /// match and are never topped up, so a +5 that is dead against 19 is the best card in the hand
    /// next round. Magnitude is the measure that survives the round. A "+/-" card is worth more
    /// than its number because it can be played either way up, and an effect card is worth taking
    /// whatever it happens to be.
    ///
    /// `ignore` leaves out the card being spent to make the trade.
    private static int HandStrength(Player player, Card ignore = null)
    {
        int strength = 0;
        foreach (Card card in player.ModifierHand)
        {
            if (card == ignore) continue;
            if (card.Effect != CardEffect.None)
            {
                strength += EffectCardWorth;
                continue;
            }
            strength += Math.Abs(card.Value) + (card.IsFlip ? FlipCardBonus : 0);
        }
        return strength;
    }

    private const int FlipCardBonus = 2;
    private const int EffectCardWorth = 5;

    /// Could this player still get to or under the target with the ordinary cards in their hand?
    /// Counts a "+/-" card at its minus face, since that is the orientation that saves a bust.
    ///
    /// mayChain says whether they get to play more than one: a person can chain hand cards for as
    /// long as they like before ending the turn, while the bot plays at most one per deal - so the
    /// same question has two different answers depending on who is being asked about. (From Gold
    /// up the bot chains too, and asks this about itself with mayChain: true.)
    ///
    /// `ignore` leaves one card out of the count - the card the caller is about to spend, which is
    /// no longer available to finish the job it starts.
    private static bool CanGetUnder(Player player, int score, int target, bool mayChain, Card ignore = null)
    {
        if (score <= target) return true;

        int everything = 0;
        foreach (Card card in player.ModifierHand)
        {
            if (card.Effect != CardEffect.None) continue;
            if (card == ignore) continue;

            int best = card.IsFlip ? -Math.Abs(card.Value) : card.Value;
            if (!mayChain && score + best <= target) return true;
            if (best < 0) everything += best;
        }

        return mayChain && score + everything <= target;
    }

    /// Is there an ordinary card that would put the bot past a score the player has locked in,
    /// without busting? If so it does not need an effect card to win this round.
    private static bool CanBeatWithOrdinary(Player me, int scoreToBeat, int target)
    {
        foreach (Card card in me.ModifierHand)
        {
            if (card.Effect != CardEffect.None) continue;

            int magnitude = Math.Abs(card.Value);
            int[] orientations = card.IsFlip ? new[] { magnitude, -magnitude } : new[] { card.Value };
            foreach (int value in orientations)
            {
                int result = me.CurrentScore + value;
                if (result <= target && result > scoreToBeat) return true;
            }
        }
        return false;
    }

    /// The bot looks at every card in its hand - and, for a "+/-" card, at BOTH orientations -
    /// and takes the play that leaves it as high as possible without going over the target.
    ///
    /// `mayChain` says whether another card may follow this one in the same deal (Gold and up). It
    /// changes exactly one thing, and it is the thing Alexander caught at the table: a bot on 26
    /// against a target of 20, holding a -3 and a -4, plays NEITHER, because neither card alone
    /// gets it under. Allowed to chain it plays the -4, then the -3, and takes the round.
    private bool TryAiPlayModifierCard(bool mayChain = false)
    {
        int target = _gameState.TargetScore;
        int score = _player2.CurrentScore;

        // How high it wants to be before it stops improving: near the target normally, or one PAST
        // Player 1 when it is chasing a score Player 1 has already locked in - drawing level with
        // a locked score is a tie, which is replayed rather than won.
        int wantAtLeast = Math.Max(10, target - 2);
        if (_player1.IsHolding && _player1.CurrentScore <= target)
        {
            wantAtLeast = Math.Min(target, _player1.CurrentScore + 1);
        }

        Card bestCard = null;
        int bestValue = 0;
        int bestResult = int.MinValue;

        // A partial climb down: still over the target, but closer, and only ever considered when
        // what is LEFT in the hand can finish the job.
        Card salvageCard = null;
        int salvageValue = 0;
        int salvageResult = int.MaxValue;

        foreach (Card card in _player2.ModifierHand)
        {
            // Effect cards are chosen by their own logic (pass 2), never scored as a gain to the
            // bot's own total: Copy takes its number from the table, and a Shave carries Value 1
            // while subtracting.
            if (card.Effect != CardEffect.None) continue;

            int[] orientations = card.IsFlip ? new[] { card.Value, -card.Value } : new[] { card.Value };
            foreach (int value in orientations)
            {
                int result = score + value;

                if (result > target)
                {
                    // Never play INTO a bust. Already busted, chaining, and this card leaves it
                    // strictly closer to legal - that is the one case worth a card, and only if
                    // the rest of the hand can actually finish the climb down.
                    if (!mayChain || score <= target || result >= score) continue;
                    if (!CanGetUnder(_player2, result, target, mayChain: true, ignore: card)) continue;
                    if (result >= salvageResult) continue;

                    salvageCard = card;
                    salvageValue = value;
                    salvageResult = result;
                    continue;
                }

                if (score <= target)
                {
                    if (result <= score) continue;                    // already safe: only play to improve
                    if (result < wantAtLeast) continue;               // not worth burning a card for
                }
                if (result <= bestResult) continue;

                bestCard = card;
                bestValue = value;
                bestResult = result;
            }
        }

        // Landing legal always beats getting closer, so the salvage is only ever the fallback.
        if (bestCard == null && salvageCard != null)
        {
            bestCard = salvageCard;
            bestValue = salvageValue;
            bestResult = salvageResult;
        }

        if (bestCard == null) return false;

        if (bestCard.Value != bestValue) bestCard.Flip(); // play the +/- card the other way round

        GD.Print($"AI Bot plays modifier {bestCard.CardName}. New Score: {bestResult} (Target: {target})");
        _player2.PlayModifierCard(bestCard, _gameState);
        InstantiateCardView(bestCard, _p2BoardContainer);

        UpdateUI();

        return true;
    }

    // ------------------------------------------------------------------
    // Effect cards
    //
    // One path for both sides. The AI reaches this from ProcessAiTurn (pass 2) and the player
    // from their hand buttons once the market sells them one; neither gets its own rules.
    // ------------------------------------------------------------------

    /// Can this player reach across the table with this card right now? Legality is the card's
    /// own business (CardEffects.CanPlay); the once-per-deal limit is the deal's.
    private bool CanPlayEffect(Player owner, Card card)
    {
        if (card == null || card.Effect == CardEffect.None) return false;
        if (!CardEffects.Implemented(card.Effect)) return false;
        if (owner == _player1 ? _p1PlayedEffectThisDeal : _p2PlayedEffectThisDeal) return false;

        Player target = (owner == _player1) ? _player2 : _player1;
        return CardEffects.CanPlay(card, owner, target, _gameState.TargetScore);
    }

    /// WHY this card cannot be played right now, as a sentence, or null when it can be. The
    /// once-per-deal limit belongs to the DEAL, so it is answered here; every other rule is the
    /// card's own and is answered by CardEffects.
    ///
    /// The same sentence is what the status line shows and what explains the greyed-out Play
    /// button - a rule the player cannot see is a rule they cannot learn.
    private string EffectRefusal(Player owner, Card card)
    {
        if (card == null || card.Effect == CardEffect.None) return null;

        if (owner == _player1 ? _p1PlayedEffectThisDeal : _p2PlayedEffectThisDeal)
            return "one card across the table per deal, and you have played yours.";

        Player target = (owner == _player1) ? _player2 : _player1;
        return CardEffects.RefusalReason(card, owner, target, _gameState.TargetScore);
    }

    /// Spends an effect card and applies it. Returns false without touching anything if the play
    /// was not legal, so a card is never silently eaten.
    private bool PlayEffectCard(Player owner, Card card)
    {
        if (!CanPlayEffect(owner, card)) return false;
        if (!owner.ModifierHand.Remove(card)) return false;

        Player target = (owner == _player1) ? _player2 : _player1;
        CardEffects.EffectResult result = CardEffects.Resolve(card, owner, target, _gameState.TargetScore);

        if (!result.Applied)
        {
            owner.ModifierHand.Add(card); // put it back rather than lose it to a rule we misread
            return false;
        }

        if (owner == _player1) _p1PlayedEffectThisDeal = true;
        else _p2PlayedEffectThisDeal = true;

        // THE ANSWERING RULE. A card played at you re-opens your turn for this deal, so you always
        // get a say - unless you are holding, which is the locked state Shave exists to punish.
        bool reopened = result.ReopensTarget && !target.IsHolding;
        if (reopened) target.HasEndedTurn = false;

        // The two effects that change the other player's score sit in THEIR board, so the number
        // that moved and the card that moved it are in the same place.
        bool onTarget = CardEffects.LandsOnTarget(card.Effect);
        Player boardOwner = onTarget ? target : owner;
        Control board = (boardOwner == _player1) ? _p1BoardContainer : _p2BoardContainer;
        boardOwner.ActiveCardsOnBoard.Add(card);
        InstantiateCardView(card, board);

        // A card that rewrote a drawn card mutated a Card object that is already face-up on a
        // board. Without this the board still reads 10 while the score has been paid at 2, which
        // is the one thing a card called Copy cannot afford to get wrong.
        if (CardEffects.RewritesDrawnCards(card.Effect))
        {
            RefreshCardFace(owner.LastDrawnCard, (owner == _player1) ? _p1BoardContainer : _p2BoardContainer);
            RefreshCardFace(target.LastDrawnCard, (target == _player1) ? _p1BoardContainer : _p2BoardContainer);
        }

        GD.Print(result.Narration);
        ShowEffectBanner(result.Narration); // the log is not on the table - the player has to SEE it
        UpdateUI();

        // A re-opened BOT has to be sent round again: ResolveDeal refuses to move while either
        // side can act, and nothing else would ever call ProcessAiTurn back.
        if (reopened && _isVsBot && target == _player2)
        {
            // Mid-turn the call would hit ProcessAiTurn's own guard and vanish, and its tail would
            // then close the turn we just re-opened. Leave a note for it to read instead.
            if (_aiTurnInProgress) _p2ReopenedMidTurn = true;
            else ProcessAiTurn();
        }

        return true;
    }

    /// True when a person is allowed to press End Turn / Hold / a hand card right now.
    /// (The bot thinking does NOT lock the human - both sides act at the same time.)
    private bool HumanCanAct()
    {
        if (!_isGameStarted || _gameState.IsGameOver || _roundOverPending) return false;
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
    private void OnEndTurnPressed(Player player) => FinishTurn(player, hold: false);
    private void OnHoldPressed(Player player) => FinishTurn(player, hold: true);

    /// A player is done with this deal: End Turn keeps them in for the next deal, Hold takes them
    /// out for the rest of the round. Nothing is decided until the other player is done too.
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

        ResolveDeal();
    }

    /// The round is over: at least one player finished the deal over the target (a bust), or
    /// both players are holding. Who busted is derived from the scores. Records the result, then
    /// shows an explanation that has to be acknowledged - the next round (or a restart, after the
    /// match) starts from that button.
    private void EndRound()
    {
        int target = _gameState.TargetScore;
        int p1 = _player1.CurrentScore;
        int p2 = _player2.CurrentScore;
        bool p1Bust = p1 > target;
        bool p2Bust = p2 > target;

        int roundWinner;
        if (p1Bust && p2Bust) roundWinner = 0;
        else if (p1Bust) roundWinner = 2;
        else if (p2Bust) roundWinner = 1;
        else if (p1 == p2) roundWinner = 0;
        else roundWinner = (p1 > p2) ? 1 : 2; // both under the target: the higher score is closer

        int roundNumber = _gameState.CurrentRound;

        // Why the round ended.
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
        if (roundWinner == 0)
        {
            title = $"Round {roundNumber} is a tie";
            why += "\nSame score, so the round is replayed.";
            buttonText = "Replay Round";
        }
        else
        {
            Player winner = (roundWinner == 1) ? _player1 : _player2;
            _gameState.RecordRoundWinner(roundWinner);
            _gameState.CurrentRound++;
            if (!anyBust) why += $"\n{winner.PlayerName} is closest to {target}.";
            title = $"{winner.PlayerName} wins round {roundNumber}!";
            buttonText = "Next Round";
        }

        Action next;
        if (_gameState.CheckMatchWinner(out int matchWinner))
        {
            Player champion = (matchWinner == 1) ? _player1 : _player2;
            int champWins = (matchWinner == 1) ? _gameState.RoundsWonPlayer1 : _gameState.RoundsWonPlayer2;
            int otherWins = (matchWinner == 1) ? _gameState.RoundsWonPlayer2 : _gameState.RoundsWonPlayer1;
            title = $"{champion.PlayerName} wins the match!";
            why += $"\n{champion.PlayerName} took the match {champWins} rounds to {otherWins}.";
            why += ReportRunResult(matchWinner == 1, _gameState.RoundsWonPlayer1);
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
                buttonText = "Play Again";
                next = OnRestartPressed;
            }
        }
        else
        {
            next = () =>
            {
                _roundOverPending = false;
                StartNewRound();
            };
        }

        string whyOneLine = why.Replace('\n', ' ');
        GD.Print($"Round {roundNumber} over: {title} ({whyOneLine})");

        _roundOverPending = true;
        UpdateUI(); // locks every button and hand card; sides show Bust! / Holding
        if (_roundInfoLabel != null) _roundInfoLabel.Text = title;
        ShowRoundEnd(title, why, buttonText, next);
    }

    /// Banks the match result against the run and says what it was worth. The market and the deck
    /// open next (OpenIntermission), which is where those medals get spent.
    private string ReportRunResult(bool playerWon, int playerRoundsWon)
    {
        RunData run = _inRun ? RunData.Instance : null;
        if (run == null) return string.Empty;

        int before = run.Medals;
        run.CompleteMatch(playerRoundsWon, playerWon);

        if (!playerWon)
        {
            // The cards are the progress, and they survive - say so plainly, on the screen where
            // losing is most likely to make someone put the game down.
            return $"\n\nThe run ends here. Every card you have unlocked ({run.Inventory.Count}) is " +
                   $"still yours, along with {run.Medals} medals.\nPlay Again starts a fresh climb " +
                   $"from stage 1 with your deck intact.";
        }

        int earned = run.Medals - before;
        if (run.RunComplete) return $"\n\nYou earned {earned} medals - and you have cleared the whole ladder.";

        RunData.LadderStep next = run.CurrentStep;
        return $"\n\nYou earned {earned} medals ({run.Medals} banked)." +
               $"\nNext, stage {run.MatchNumber}: {next.Opponent}, target {next.TargetScore}." +
               "\nThe market is open first.";
    }

    /// "Stage 3/10 - Silver" while a run is on; nothing otherwise. The rank is named here because
    /// the table is already wearing its colour - the words label what the player can see.
    private string RunHeader()
    {
        RunData run = _inRun ? RunData.Instance : null;
        if (run == null) return string.Empty;
        return $"Stage {run.MatchNumber}/{RunData.LadderLength} - {run.CurrentRank.Name} - ";
    }

    // ------------------------------------------------------------------
    // The middle panel's two added lines
    // ------------------------------------------------------------------

    /// The target, and the banner that narrates effect cards. Added around the existing round
    /// line in code rather than in the two scene files, so both scenes get them from one place.
    private void BuildTableBanners()
    {
        Control column = _roundInfoLabel?.GetParent() as Control;
        if (column == null) return;
        int at = _roundInfoLabel.GetIndex();

        // The target is the whole difficulty curve on this ladder - it moves from 20 to 23 to 18
        // and back up - so it is the one number that cannot be a fragment of a status string.
        _targetLabel = OverlayUi.MakeLabel(string.Empty, 30);
        column.AddChild(_targetLabel);
        column.MoveChild(_targetLabel, at);

        // Kept VISIBLE and empty rather than hidden, so the panel does not jump by a line every
        // time an effect card resolves.
        _effectBanner = OverlayUi.MakeLabel(string.Empty, 17, new Color(0.86f, 0.74f, 1.0f));
        _effectBanner.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _effectBanner.CustomMinimumSize = new Vector2(0, 46);
        column.AddChild(_effectBanner);
        column.MoveChild(_effectBanner, at + 2);
    }

    /// A refusal is a sentence now, so the status line has to be able to hold one. The height for
    /// two lines is reserved up front - a label that grows when a card is picked up would shove
    /// the board down the screen mid-deal.
    private void ConfigureStatusLabel(Label label)
    {
        if (label == null) return;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.CustomMinimumSize = new Vector2(0, label.GetThemeFontSize("font_size") * 2.6f);
    }

    private void UpdateTargetLabel()
    {
        if (_targetLabel == null) return;

        if (!_isGameStarted)
        {
            _targetLabel.Text = string.Empty;
            _targetLabel.RemoveThemeColorOverride("font_color");
            return;
        }

        _targetLabel.Text = $"TARGET  {_gameState.TargetScore}";
        _targetLabel.RemoveThemeColorOverride("font_color");

        // And when stepping onto this rung MOVED it, say so on the rung where it happens. A target
        // that changes quietly is the game changing its own rules behind the player's back.
        RunData run = _inRun ? RunData.Instance : null;
        if (run != null && run.TargetMovedThisStage)
        {
            string direction = run.CurrentTarget > run.PreviousTarget ? "up" : "down";
            _targetLabel.Text += $"   ({direction} from {run.PreviousTarget})";
            _targetLabel.AddThemeColorOverride("font_color", OverlayUi.MedalGold);
        }
    }

    /// What an effect card just did, on the table. The explanation already existed - CardEffects
    /// writes one for every card it resolves - but it only ever went to the log, so at the table
    /// a score simply changed and nothing said why.
    private async void ShowEffectBanner(string text)
    {
        if (_effectBanner == null || string.IsNullOrEmpty(text)) return;

        uint token = ++_effectBannerToken;
        _effectBanner.Text = text;

        await ToSignal(GetTree().CreateTimer(5.0f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree() || token != _effectBannerToken) return; // a newer card owns the line

        _effectBanner.Text = string.Empty;
    }

    private void ClearEffectBanner()
    {
        _effectBannerToken++;
        if (_effectBanner != null) _effectBanner.Text = string.Empty;
    }

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

        Control column = _roundInfoLabel?.GetParent() as Control;
        if (column == null) return;

        HBoxContainer row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 6);
        column.AddChild(row);

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
            OnRestartPressed();
        };
        row.AddChild(wipe);
    }

    /// Drops the run one rung either way and walks straight into that match, so a stage can be
    /// tested without climbing to it.
    private void DebugJumpStage(int delta)
    {
        RunData run = RunData.Instance;
        if (run == null) return;

        run.DebugJumpToStep(run.StepIndex + delta);
        run.AutoStartNextMatch = true;
        GD.Print($"DEBUG: jumped to stage {run.MatchNumber} ({run.CurrentStep.Opponent}, target {run.CurrentTarget})");
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
        _shopOverlay.Setup(CreateCardView);

        _deckOverlay = new DeckOverlay();
        AddChild(_deckOverlay);
        _deckOverlay.Setup(CreateCardView);
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
        Vector2 deckCardSize = CardSize * 0.7f;
        _shopOverlay.Open(CardSize, () => _deckOverlay.Open(deckCardSize, StartNextMatch));
    }

    /// Reloading the scene is what resets the board, the scores and the round wins (the same path
    /// Restart takes); RunData is an autoload, so the run itself survives it.
    private void StartNextMatch()
    {
        if (RunData.Instance != null) RunData.Instance.AutoStartNextMatch = true;
        GetTree().ReloadCurrentScene();
    }

    // ------------------------------------------------------------------
    // UI refresh
    // ------------------------------------------------------------------
    private void UpdateUI()
    {
        // A picked-up card that can no longer be played (spent, or the player just held) is
        // dropped before anything is drawn, so the status line and the buttons agree.
        ValidateSelections();

        // When P2's side is flipped, each player sees both scores on their own (readable) row.
        // Otherwise everyone can read both rows, so each side just shows its own score.
        if (IsMirrored)
        {
            if (_p1ScoreLabel != null)
                _p1ScoreLabel.Text = $"P1: {_player1.CurrentScore} | P2: {_player2.CurrentScore}";

            if (_p2ScoreLabel != null)
                _p2ScoreLabel.Text = $"P2: {_player2.CurrentScore} | P1: {_player1.CurrentScore}";
        }
        else
        {
            if (_p1ScoreLabel != null) _p1ScoreLabel.Text = $"Score: {_player1.CurrentScore}";
            if (_p2ScoreLabel != null) _p2ScoreLabel.Text = $"Score: {_player2.CurrentScore}";
        }

        UpdateTargetLabel();

        if (_p1StatusLabel != null) _p1StatusLabel.Text = StatusFor(_player1);
        if (_p2StatusLabel != null) _p2StatusLabel.Text = StatusFor(_player2);
        ApplyStatusColor(_p1StatusLabel, _player1);
        ApplyStatusColor(_p2StatusLabel, _player2);

        // Round wins are shown as chips next to the label (see UpdateWinChips).
        if (_p1WinsLabel != null) _p1WinsLabel.Text = "Wins:";
        if (_p2WinsLabel != null) _p2WinsLabel.Text = "Wins:";
        UpdateWinChips();

        // While the round-end explanation is up, EndRound owns this label.
        if (_isGameStarted && _roundInfoLabel != null && !_gameState.IsGameOver && !_roundOverPending)
        {
            _roundInfoLabel.Text = $"{RunHeader()}Round {_gameState.CurrentRound} | {DealStatusText()}";
        }

        // Both sides act at once: each player's row stays live until THAT player has ended the
        // deal or is holding. Everything is locked while a round-end explanation is waiting to be
        // acknowledged. The solo scene's shared pair is Player 1's.
        bool p1Can = HumanCanActFor(_player1);
        bool p2Can = HumanCanActFor(_player2);
        SetEnabled(_endTurnButton, p1Can);
        SetEnabled(_holdButton, p1Can);
        SetEnabled(_p1EndTurnButton, p1Can);
        SetEnabled(_p1HoldButton, p1Can);
        SetEnabled(_p2EndTurnButton, p2Can);
        SetEnabled(_p2HoldButton, p2Can);

        // While a card is picked up, that player's End Turn / Hold row is swapped for the
        // Play / +- / Put back row (same slot in the layout, so nothing moves).
        UpdateConfirmRow(_player1, _p1ConfirmRow, _p1PlayButton, _p1FlipButton, _p1ActionRow);
        UpdateConfirmRow(_player2, _p2ConfirmRow, _p2PlayButton, _p2FlipButton, _p2ActionRow);

        RefreshHandUI();
        CallDeferred(MethodName.UpdateRotatorSize);
    }

    /// What the middle panel says about the current deal.
    private string DealStatusText()
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
        if (p1 && p2) return "Both players: play or end turn";
        if (p1) return "Waiting for P1";
        if (p2) return "Waiting for P2";
        return "Dealing...";
    }

    private string StatusFor(Player player)
    {
        bool over = player.CurrentScore > _gameState.TargetScore;

        if (_roundOverPending && over) return "Bust!";
        if (_roundOverPending) return player.IsHolding ? "Holding" : "Done";

        if (player.IsHolding) return "Holding";
        if (player.HasEndedTurn) return "Done - waiting";

        // A card is picked up: spell the arithmetic out. This is the game's teaching moment, so
        // it is shown as a full sum rather than just the answer.
        Card picked = SelectedFor(player);
        if (picked != null)
        {
            if (picked.Effect != CardEffect.None) return EffectPreview(player, picked);

            string sign = picked.Value < 0 ? "-" : "+";
            return $"{player.CurrentScore} {sign} {Math.Abs(picked.Value)} = {player.CurrentScore + picked.Value}";
        }

        // Still acting this deal.
        if (over) return "Over target!"; // a warning, not a bust yet: play a minus card before ending the turn
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
                return $"Trade Hands: your {player.ModifierHand.Count - 1} for their {other.ModifierHand.Count}";
        }

        return name;
    }

    private static void SetEnabled(Button button, bool enabled)
    {
        if (button != null) button.Disabled = !enabled;
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
        if (_p1SelectedCard != null && (!_player1.ModifierHand.Contains(_p1SelectedCard) || !HumanCanActFor(_player1)))
            _p1SelectedCard = null;
        if (_p2SelectedCard != null && (!_player2.ModifierHand.Contains(_p2SelectedCard) || !HumanCanActFor(_player2)))
            _p2SelectedCard = null;
    }

    /// Green when the picked-up card keeps the player at or under the target, red when it would
    /// take them over - the colour reads long before the numbers do.
    private void ApplyStatusColor(Label label, Player player)
    {
        if (label == null) return;

        Card picked = SelectedFor(player);
        if (picked == null)
        {
            label.RemoveThemeColorOverride("font_color");
            return;
        }

        // An effect card is not added to this player's score, so "would this bust me" is the wrong
        // question: colour it by whether it can be played at all.
        if (picked.Effect != CardEffect.None)
        {
            label.AddThemeColorOverride("font_color", CanPlayEffect(player, picked)
                ? new Color(0.55f, 0.95f, 0.60f)
                : new Color(1f, 0.45f, 0.42f));
            return;
        }

        bool over = player.CurrentScore + picked.Value > _gameState.TargetScore;
        label.AddThemeColorOverride("font_color", over
            ? new Color(1f, 0.45f, 0.42f)
            : new Color(0.55f, 0.95f, 0.60f));
    }

    /// Builds each player's Play / +- / Put back row, directly under the End Turn / Hold row it
    /// stands in for. Found from the exported buttons rather than a NodePath, so it works in both
    /// scenes (and inside P2's rotator, so it flips with the rest of P2's side).
    private void BuildConfirmRows()
    {
        _p1ConfirmRow = BuildConfirmRow(_player1, _p1EndTurnButton ?? _endTurnButton, out _p1PlayButton, out _p1FlipButton, out _p1ActionRow);
        _p2ConfirmRow = BuildConfirmRow(_player2, _p2EndTurnButton, out _p2PlayButton, out _p2FlipButton, out _p2ActionRow);
    }

    private HBoxContainer BuildConfirmRow(Player player, Button anchorButton, out Button playButton,
                                          out Button flipButton, out Control actionRow)
    {
        playButton = null;
        flipButton = null;
        actionRow = null;
        if (anchorButton == null) return null; // that side has no buttons in this scene (the bot's)

        actionRow = anchorButton.GetParent() as Control; // the End Turn / Hold row
        Node host = actionRow?.GetParent();              // the column that row lives in
        if (actionRow == null || host == null) return null;

        HBoxContainer row = new HBoxContainer
        {
            Visible = false,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        row.AddThemeConstantOverride("separation", 12);

        Button play = MakeConfirmButton("Play", new Color(0.24f, 0.62f, 0.31f));
        play.Pressed += () => PlaySelectedCard(player);
        row.AddChild(play);
        playButton = play;

        flipButton = MakeConfirmButton("+ / -", new Color(0.22f, 0.44f, 0.78f));
        flipButton.Pressed += () => FlipSelectedCard(player);
        row.AddChild(flipButton);

        Button cancel = MakeConfirmButton("Put back", new Color(0.38f, 0.38f, 0.42f));
        cancel.Pressed += () => { SetSelection(player, null); UpdateUI(); };
        row.AddChild(cancel);

        host.AddChild(row);
        host.MoveChild(row, actionRow.GetIndex() + 1);
        return row;
    }

    private static Button MakeConfirmButton(string text, Color tint)
    {
        Button button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeColorOverride("font_color", Colors.White);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);

        StyleBoxFlat box = new StyleBoxFlat
        {
            BgColor = tint,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 14,
            ContentMarginBottom = 14,
        };
        StyleBoxFlat pressed = (StyleBoxFlat)box.Duplicate();
        pressed.BgColor = tint.Lightened(0.15f);

        button.AddThemeStyleboxOverride("normal", box);
        button.AddThemeStyleboxOverride("hover", pressed);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("focus", box);
        return button;
    }

    private void UpdateConfirmRow(Player player, HBoxContainer row, Button playButton,
                                  Button flipButton, Control actionRow)
    {
        if (row == null) return;

        Card picked = SelectedFor(player);
        row.Visible = picked != null;
        if (actionRow != null) actionRow.Visible = picked == null;
        if (flipButton != null) flipButton.Visible = picked != null && picked.IsFlip;

        // An effect card that is not legal right now cannot be played at all, so the button says
        // so rather than the card bouncing back with a message. The status line above it is
        // already explaining why (see EffectPreview).
        if (playButton != null)
        {
            playButton.Disabled = picked != null && picked.Effect != CardEffect.None
                && !CanPlayEffect(player, picked);
        }
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
            // A refused play puts the card back in the player's hand AND back under their finger,
            // so the status line keeps explaining why it would not go.
            if (!PlayEffectCard(player, card)) SetSelection(player, card);
            UpdateUI();
            return;
        }

        if (player.PlayModifierCard(card, _gameState))
        {
            InstantiateCardView(card, (player == _player1) ? _p1BoardContainer : _p2BoardContainer);
        }

        UpdateUI();
    }

    /// Swaps a picked-up "+/-" card between plus and minus. Nothing is spent - the sum in the
    /// status line just changes, so it can be flipped back and forth as often as the player likes.
    private void FlipSelectedCard(Player player)
    {
        Card card = SelectedFor(player);
        if (card == null || !HumanCanActFor(player) || !card.Flip()) return;

        _sfxPlace?.Play();
        UpdateUI();
    }

    private void RefreshHandUI()
    {
        if (_p1HandContainer == null || _p2HandContainer == null || _player1 == null) return;

        // Detach immediately, not just QueueFree: queued nodes stay in the tree until the end of
        // the frame and would still count towards the hand's minimum size when the deferred
        // UpdateRotatorSize runs (P2's side then reserved room for 8-12 cards and pushed P1 off-screen).
        ClearChildren(_p1HandContainer);
        ClearChildren(_p2HandContainer);

        bool p1Can = HumanCanActFor(_player1);
        bool p2Can = HumanCanActFor(_player2);
        foreach (Card card in _player1.ModifierHand)
        {
            _p1HandContainer.AddChild(CreateHandCardButton(
                card, !p1Can, card == _p1SelectedCard, _p1SelectedCard != null,
                () => OnModifierCardPressed(_player1, card)));
        }

        foreach (Card card in _player2.ModifierHand)
        {
            _p2HandContainer.AddChild(CreateHandCardButton(
                card, !p2Can, card == _p2SelectedCard, _p2SelectedCard != null,
                () => OnModifierCardPressed(_player2, card)));
        }
    }

    /// A tappable modifier card: an invisible Button (so the theme's touch-friendly hit area
    /// and focus handling still apply) with the card art drawn on top. A picked-up card is lifted
    /// and the rest of the hand dims, so which card is in play is obvious without reading anything.
    private Button CreateHandCardButton(Card card, bool disabled, bool selected, bool anySelected, Action onPressed)
    {
        Button button = new Button
        {
            Flat = true,
            CustomMinimumSize = HandCardSize,
            Disabled = disabled,
            FocusMode = Control.FocusModeEnum.None,
        };
        StyleBoxEmpty empty = new StyleBoxEmpty();
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
            button.AddThemeStyleboxOverride(state, empty);
        button.Pressed += onPressed;

        TextureRect view = CreateCardView(card, HandCardSize);
        view.MouseFilter = Control.MouseFilterEnum.Ignore;

        if (disabled) view.Modulate = new Color(0.55f, 0.55f, 0.55f);
        else if (anySelected && !selected) view.Modulate = new Color(0.5f, 0.5f, 0.55f); // the rest of the hand steps back
        else view.Modulate = Colors.White;

        button.AddChild(view);
        view.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        if (selected)
        {
            // Scale the art, not the Button: the Button is a container child and would have its
            // scale reset on the next layout pass, and growing it would shove the whole hand about.
            view.PivotOffset = HandCardSize / 2f;
            view.Scale = new Vector2(1.18f, 1.18f);

            StyleBoxFlat outline = new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0),
                BorderColor = new Color(1f, 0.92f, 0.4f),
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
            };
            outline.SetBorderWidthAll(4);

            Panel highlight = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            highlight.AddThemeStyleboxOverride("panel", outline);
            view.AddChild(highlight);
            highlight.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }

        return button;
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
        _sfxSlide?.Play();
        UpdateUI();
    }

    // ------------------------------------------------------------------
    // Board slots
    // ------------------------------------------------------------------
    /// Clears the board and fills it with 9 empty, faintly outlined slots. Cards are placed
    /// INTO these slots (see InstantiateCardView) so the grid never grows or shifts.
    private static void ClearChildren(Node parent)
    {
        if (parent == null) return;
        foreach (Node child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void FillBoardWithSlots(Control board)
    {
        if (board == null) return;
        ClearChildren(board);

        for (int i = 0; i < BoardSlots; i++)
        {
            Panel slot = new Panel { CustomMinimumSize = CardSize, MouseFilter = Control.MouseFilterEnum.Ignore };
            StyleBoxFlat style = new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0.18f),
                BorderColor = new Color(1, 1, 1, 0.12f),
            };
            style.SetBorderWidthAll(2);
            style.SetCornerRadiusAll(8);
            slot.AddThemeStyleboxOverride("panel", style);
            board.AddChild(slot);
        }
    }

    private Control FindFreeSlot(Control board)
    {
        foreach (Node child in board.GetChildren())
        {
            if (child is Control slot && slot.GetChildCount() == 0) return slot;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Card views
    // ------------------------------------------------------------------
    private Rect2 RegionFor(Card card)
    {
        if (card.Type == CardType.Main) return RegionMain;
        // Before the sign test: a Shave carries Value 1 and would otherwise wear the blue "plus"
        // back, which is the opposite of what it does.
        if (card.Effect != CardEffect.None) return RegionEffect;
        if (card.Value < 0) return RegionMinus;
        return RegionPlus;
    }

    private AtlasTexture MakeAtlas(Texture2D sheet, Rect2 region)
    {
        return new AtlasTexture { Atlas = sheet, Region = region };
    }

    // When P2's side is mirrored, every card shows its value twice - like the corner indices on
    // a real playing card: once in the top half and once upside down in the bottom half - so
    // both players can read every card. Otherwise a single centred value is used.
    private static readonly string[] CardLabelNames = { "Label", "LabelFlipped" };

    private void ApplyCardSize(TextureRect view, Vector2 size)
    {
        view.CustomMinimumSize = size;

        // A "+/-" card is always drawn two-way - blue +n above, red -n below - whether or not the
        // table is mirrored, because that split IS how you tell it from an ordinary modifier.
        bool twoWay = IsMirrored || view.HasNode("FlipBottom");

        // An effect card's face is a mark rather than one number ("->+4", "-1"), so it needs a
        // smaller font than a card showing a single digit or two.
        float fontScale = view.HasMeta("effectCard") ? 0.22f : (twoWay ? 0.33f : 0.36f);
        int fontSize = Mathf.RoundToInt(size.Y * fontScale);

        Label label = view.GetNodeOrNull<Label>("Label");
        if (label != null)
        {
            label.AddThemeFontSizeOverride("font_size", fontSize);
            label.AnchorBottom = twoWay ? 0.5f : 1f; // top half, or the whole card
        }

        Label flipped = view.GetNodeOrNull<Label>("LabelFlipped");
        if (flipped != null)
        {
            flipped.AddThemeFontSizeOverride("font_size", fontSize);
            flipped.Visible = twoWay;
            flipped.RotationDegrees = IsMirrored ? 180f : 0f; // upright unless P2 is reading it
        }
    }

    /// Half the card's colour when that orientation is not the one currently chosen.
    private static readonly Color DimmedHalf = new Color(0.58f, 0.58f, 0.62f);

    /// Draws a "+/-" card as two halves: a blue top reading +n and a red bottom reading -n
    /// (Alexander, 2026-09-07). The bright half is the orientation the card is currently set to,
    /// so tapping +/- visibly moves the card from one half to the other.
    ///
    /// The red half is the same Kenney card art clipped to the bottom of the frame rather than a
    /// flat rectangle, so the border and the rounded corners still line up.
    private void BuildFlipFace(TextureRect view, Card card, Vector2 size)
    {
        view.Texture = MakeAtlas(_cardSheet, RegionPlus); // blue, and the top half is what shows

        Control bottom = new Control
        {
            Name = "FlipBottom",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        view.AddChild(bottom);
        view.MoveChild(bottom, 0); // behind the two numbers, in front of the blue art
        bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        bottom.AnchorTop = 0.5f;
        bottom.OffsetTop = 0f;

        TextureRect red = new TextureRect
        {
            Name = "FlipBottomArt",
            Texture = MakeAtlas(_cardSheet, RegionMinus),
            ExpandMode = view.ExpandMode,
            StretchMode = view.StretchMode,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bottom.AddChild(red);
        red.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Grow the red art back up over the hidden half so it is a whole card, clipped to this
        // one. Re-run on every resize, because the card is re-sized on rotation.
        red.OffsetTop = -size.Y * 0.5f;
        bottom.Resized += () => red.OffsetTop = -bottom.Size.Y;

        int magnitude = Mathf.Abs(card.Value);
        bool plusChosen = card.Value >= 0;
        int fontSize = Mathf.RoundToInt(size.Y * 0.33f); // two numbers, half a card each

        Label top = view.GetNodeOrNull<Label>("Label");
        if (top != null)
        {
            top.Text = "+" + magnitude;
            top.AnchorBottom = 0.5f;
            top.AddThemeFontSizeOverride("font_size", fontSize);
            top.Modulate = plusChosen ? Colors.White : DimmedHalf;
        }

        Label under = view.GetNodeOrNull<Label>("LabelFlipped");
        if (under != null)
        {
            under.Text = "-" + magnitude;
            under.Visible = true;
            under.AddThemeFontSizeOverride("font_size", fontSize);
            under.Modulate = plusChosen ? DimmedHalf : Colors.White;
        }

        // Dim the art of the half that is not in play, so the choice reads from across the table.
        view.SelfModulate = plusChosen ? Colors.White : DimmedHalf;
        bottom.Modulate = plusChosen ? DimmedHalf : Colors.White;
    }

    /// Repaints the table and the standard cards for the rank the player is on. This is the whole
    /// progression display: no invented venue names, just a board that looks different every two
    /// rungs (Alexander, 2026-09-07).
    private void ApplyRankTheme()
    {
        RunData run = _inRun ? RunData.Instance : null;

        // No run (local 2-player, or the solo scene opened on its own) means the plain felt: the
        // clear colour is global and would otherwise follow us out of the run.
        Color table = (run == null) ? DefaultTableColor : run.CurrentRank.Table;
        _rankCardTint = (run == null) ? Colors.White : run.CurrentRank.CardTint;

        RenderingServer.SetDefaultClearColor(table);
        if (_mainDeckPosition is TextureRect deck) deck.SelfModulate = _rankCardTint;
    }

    private TextureRect CreateCardView(Card card, Vector2 size)
    {
        TextureRect view = (TextureRect)_cardViewScene.Instantiate();
        view.Texture = MakeAtlas(_cardSheet, RegionFor(card));
        view.SetMeta("cardId", card.Id); // so RefreshCardFace can find this view again
        ApplyCardSize(view, size);

        string text = card.DisplayText; // read from Value, so a flipped card shows its new sign
        foreach (string name in CardLabelNames)
        {
            Label label = view.GetNodeOrNull<Label>(name);
            if (label != null) label.Text = text;
        }

        // Standard cards carry the rank's tint, so the deck you are playing with visibly changes
        // as you climb. SelfModulate, not Modulate: the number on top stays white.
        if (card.Type == CardType.Main) view.SelfModulate = _rankCardTint;

        // An effect card is a Modifier, so this never fights the rank tint above.
        if (card.Effect != CardEffect.None)
        {
            view.SelfModulate = EffectTint;
            view.SetMeta("effectCard", true); // ApplyCardSize gives its longer mark a smaller font
            ApplyCardSize(view, size);        // re-run now that the meta is set
        }

        // The +/- face would rewrite both labels and hide the effect entirely, so only an
        // ordinary card gets it - a hand-edited save carrying both flags cannot lie about itself.
        if (card.IsFlip && card.Effect == CardEffect.None) BuildFlipFace(view, card, size);

        // The bottom label is rotated about its own centre once the layout has given it a size -
        // but ONLY when someone is sitting on that side of the table. On a mirrored 2-player board
        // it is P2's corner index; in solo (and in the market and the deck screen) there is nobody
        // down there, and an upside-down "-1" on a +/- card just reads as broken art.
        Label flipped = view.GetNodeOrNull<Label>("LabelFlipped");
        if (flipped != null)
        {
            flipped.Resized += () =>
            {
                flipped.PivotOffset = flipped.Size / 2f;
                flipped.RotationDegrees = IsMirrored ? 180f : 0f;
            };
        }
        return view;
    }

    /// Redraws the face of a card that is already on a board, after something changed its Value.
    /// Silent when the card is not on this board - a hand card has no view to redraw, and that is
    /// not an error.
    private void RefreshCardFace(Card card, Control board)
    {
        TextureRect view = FindCardView(card, board);
        if (view == null) return;

        view.Texture = MakeAtlas(_cardSheet, RegionFor(card));

        string text = card.DisplayText; // computed from Value, so the new number is already in it
        foreach (string name in CardLabelNames)
        {
            Label label = view.GetNodeOrNull<Label>(name);
            if (label != null) label.Text = text;
        }
    }

    /// The view showing this exact card, or null. Cards live one-per-slot (FillBoardWithSlots),
    /// so this is a walk of nine slots rather than a search.
    private TextureRect FindCardView(Card card, Control board)
    {
        if (card == null || board == null) return null;

        foreach (Node slot in board.GetChildren())
        {
            foreach (Node child in slot.GetChildren())
            {
                if (child is TextureRect view && view.HasMeta("cardId")
                    && view.GetMeta("cardId").AsInt32() == card.Id)
                {
                    return view;
                }
            }
        }
        return null;
    }

    private void InstantiateCardView(Card card, Control parentContainer)
    {
        if (_cardViewScene == null) return;

        TextureRect cardNode = CreateCardView(card, CardSize);

        // Drop the card into the next empty slot; if the board is somehow full, let the grid grow.
        Control slot = FindFreeSlot(parentContainer);
        cardNode.Modulate = new Color(1, 1, 1, 0); // invisible until the deal animation lands
        if (slot != null)
        {
            slot.AddChild(cardNode);
            cardNode.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        else
        {
            parentContainer.AddChild(cardNode);
        }

        // Defer the animation by one frame so Godot has time to calculate its final Grid position
        CallDeferred(MethodName.AnimateCardDrop, cardNode);
    }

    private void AnimateCardDrop(Control realCard)
    {
        // Fallback in case the deck isn't assigned in the inspector
        if (_mainDeckPosition == null || !IsInstanceValid(realCard))
        {
            if (IsInstanceValid(realCard)) realCard.Modulate = Colors.White;
            return;
        }

        // 1. A face-down card that flies from the deck to the slot
        TextureRect fakeCard = new TextureRect
        {
            Texture = MakeAtlas(_cardSheet, RegionDeckBack),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Size = CardSize,
            PivotOffset = CardSize / 2f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SelfModulate = _rankCardTint,
        };
        AddChild(fakeCard); // on the scene root so it draws above everything

        // 2. Start on the deck, small and upside down
        Vector2 deckCenter = _mainDeckPosition.GetGlobalTransform() * (_mainDeckPosition.Size / 2f);
        fakeCard.GlobalPosition = deckCenter - CardSize / 2f;
        fakeCard.RotationDegrees = -180f;
        fakeCard.Scale = new Vector2(0.5f, 0.5f);

        // Target the slot's visual centre. Player 2's side may be rotated 180 degrees, so
        // go through the full global transform instead of GlobalPosition.
        Vector2 targetCenter = realCard.GetGlobalTransform() * (realCard.Size / 2f);
        float targetRotation = Mathf.RadToDeg(realCard.GetGlobalTransform().Rotation);

        if (_sfxSlide != null) _sfxSlide.Play();

        // 3. Fly, spin and grow at the same time, then reveal the real card
        Tween tween = GetTree().CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(fakeCard, "global_position", targetCenter - CardSize / 2f, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(fakeCard, "rotation_degrees", targetRotation, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(fakeCard, "scale", Vector2.One, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            fakeCard.QueueFree();
            if (IsInstanceValid(realCard)) realCard.Modulate = Colors.White;
            if (_sfxPlace != null) _sfxPlace.Play();
        }));
    }

    // ------------------------------------------------------------------
    // Round-win chips
    // ------------------------------------------------------------------
    /// Adds a row of poker chips right after the "Wins" label (one per round needed to win the match).
    private HBoxContainer EnsureWinChips(Label winsLabel)
    {
        if (winsLabel == null || _chipSheet == null) return null;
        Node parent = winsLabel.GetParent();
        if (parent == null) return null;

        HBoxContainer chips = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        chips.AddThemeConstantOverride("separation", 4);
        for (int i = 0; i < WinsToTakeMatch; i++)
        {
            chips.AddChild(new TextureRect
            {
                Texture = MakeAtlas(_chipSheet, RegionChipEmpty),
                CustomMinimumSize = new Vector2(ChipSize, ChipSize),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            });
        }
        parent.AddChild(chips);
        parent.MoveChild(chips, winsLabel.GetIndex() + 1);
        return chips;
    }

    private void UpdateWinChips()
    {
        SetChips(_p1WinChips, _gameState.RoundsWonPlayer1);
        SetChips(_p2WinChips, _gameState.RoundsWonPlayer2);
    }

    private void SetChips(HBoxContainer chips, int wins)
    {
        if (chips == null) return;
        int i = 0;
        foreach (Node child in chips.GetChildren())
        {
            if (child is TextureRect chip && chip.Texture is AtlasTexture atlas)
            {
                atlas.Region = i < wins ? RegionChipWon : RegionChipEmpty;
            }
            i++;
        }
    }

    // ------------------------------------------------------------------
    // Round-end overlay
    //
    // A full-screen layer over the table (blocks every tap underneath) with a centred panel:
    // title, why the round ended, and one button. In mirrored 2-player there's also an
    // upside-down copy of the text at the top of the panel, nearest Player 2. Built in code so
    // both scenes get it without any NodePath wiring.
    // ------------------------------------------------------------------
    private Control _roundEndOverlay;
    private Label _roundEndTitle;
    private Label _roundEndBody;
    private Button _roundEndButton;
    private Control _roundEndFlippedHolder;   // plain Control: containers reset a child's rotation, holders don't
    private VBoxContainer _roundEndFlippedBox; // the node that is rotated 180 degrees
    private Label _roundEndFlippedTitle;
    private Label _roundEndFlippedBody;
    private HSeparator _roundEndDivider;
    private Action _roundEndAction;

    private void BuildRoundEndOverlay()
    {
        _roundEndOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(_roundEndOverlay); // on the scene root, after GameUI, so it draws (and gets input) on top
        _roundEndOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        ColorRect dim = new ColorRect { Color = new Color(0, 0, 0, 0.5f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _roundEndOverlay.AddChild(dim);
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
        _roundEndOverlay.AddChild(panel);
        // Anchored to the centre with zero offsets: a Control grows to its minimum size, and with
        // grow "both" it stays centred, so the panel always hugs its content.
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;

        VBoxContainer box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 14);
        panel.AddChild(box);

        // Player 2's upside-down copy (mirrored 2-player only). Same pattern as P2Holder/P2Rotator.
        _roundEndFlippedHolder = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_roundEndFlippedHolder);
        _roundEndFlippedBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _roundEndFlippedBox.AddThemeConstantOverride("separation", 6);
        _roundEndFlippedHolder.AddChild(_roundEndFlippedBox);
        _roundEndFlippedBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _roundEndFlippedBox.Resized += () =>
        {
            _roundEndFlippedBox.PivotOffset = _roundEndFlippedBox.Size / 2f;
            _roundEndFlippedBox.RotationDegrees = 180f;
        };
        _roundEndFlippedTitle = MakeOverlayLabel(30);
        _roundEndFlippedBody = MakeOverlayLabel(22);
        _roundEndFlippedBox.AddChild(_roundEndFlippedTitle);
        _roundEndFlippedBox.AddChild(_roundEndFlippedBody);
        _roundEndDivider = new HSeparator { Visible = false };
        box.AddChild(_roundEndDivider);

        _roundEndTitle = MakeOverlayLabel(30);
        _roundEndBody = MakeOverlayLabel(22);
        box.AddChild(_roundEndTitle);
        box.AddChild(_roundEndBody);

        _roundEndButton = new Button { Text = "Next Round" };
        _roundEndButton.Pressed += OnRoundEndButtonPressed;
        box.AddChild(_roundEndButton);
    }

    private static Label MakeOverlayLabel(int fontSize)
    {
        Label label = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }

    private void ShowRoundEnd(string title, string why, string buttonText, Action onAcknowledged)
    {
        _roundEndAction = onAcknowledged;
        if (_roundEndOverlay == null)
        {
            onAcknowledged?.Invoke(); // overlay failed to build: don't strand the game
            return;
        }

        // Make it visible first: minimum sizes are only reliable for nodes visible in the tree,
        // and everything below resolves in the same frame before it is drawn.
        MoveChild(_roundEndOverlay, GetChildCount() - 1); // above any stray animation card
        _roundEndOverlay.Visible = true;

        _roundEndTitle.Text = title;
        _roundEndBody.Text = why;
        _roundEndButton.Text = buttonText;

        bool mirrored = IsMirrored;
        _roundEndFlippedHolder.Visible = mirrored;
        _roundEndDivider.Visible = mirrored;
        if (mirrored)
        {
            _roundEndFlippedTitle.Text = title;
            _roundEndFlippedBody.Text = why;
            // The holder reports 0x0 on its own; give it the rotated block's footprint.
            _roundEndFlippedHolder.CustomMinimumSize = _roundEndFlippedBox.GetCombinedMinimumSize();
        }
        CallDeferred(MethodName.UpdateRoundEndFlippedSize); // re-measure once the first layout pass has run
    }

    private void UpdateRoundEndFlippedSize()
    {
        if (_roundEndFlippedHolder == null || !_roundEndFlippedHolder.Visible) return;
        _roundEndFlippedHolder.CustomMinimumSize = _roundEndFlippedBox.GetCombinedMinimumSize();
        _roundEndFlippedBox.PivotOffset = _roundEndFlippedBox.Size / 2f;
        _roundEndFlippedBox.RotationDegrees = 180f;
    }

    private void OnRoundEndButtonPressed()
    {
        _roundEndOverlay.Visible = false;
        Action action = _roundEndAction;
        _roundEndAction = null;
        action?.Invoke();
    }

    // ------------------------------------------------------------------
    // How to Play
    //
    // A "How to Play" button is added in code to the middle panel's ButtonColumn (just above the
    // Restart / Exit row) so both scenes get it without NodePath wiring. It opens a full-screen
    // overlay with the rules; it can be opened at any time and changes no game state. In mirrored
    // 2-player mode a "Flip for other player" button turns the panel upside down for Player 2.
    // ------------------------------------------------------------------
    private static readonly string HowToPlayText =
        "GOAL\n" +
        "Get as close to the target score (20) as you can without going over. " +
        $"Win {GameState.RoundsToWinMatch} rounds to win the match.\n\n" +
        "A ROUND\n" +
        "A round is a series of deals. Each deal, every player who isn't holding is dealt one card " +
        "(worth 1 to 10) at the same time. Both players then decide - at the same time, without waiting " +
        "for each other - whether to play a modifier card, and then press End Turn or Hold.\n\n" +
        "MODIFIER CARDS\n" +
        "Each player is dealt a hand of 4 random modifier cards at the start of the match. They are " +
        "worth anywhere from -4 to +4, and playing one adds its value to your score. Each card can " +
        "only be used once per match, so spend them wisely.\n\n" +
        "PLAYING A CARD\n" +
        "Tap a card in your hand to pick it up. It lifts, and your status line shows the sum it would " +
        "make - for example \"17 + 3 = 20\". Green means you would still be at or under the target, " +
        "red means it would take you over. Nothing is spent yet: tap Play (or tap the card again) to " +
        "commit it, or Put back to change your mind.\n\n" +
        "+/- CARDS\n" +
        "A card marked with a small yellow +/- can be played either way round. Pick it up and press " +
        "the + / - button to swap it between plus and minus - as often as you like - before playing it. " +
        "A +3 becomes a -3, and back again.\n\n" +
        "END TURN\n" +
        "You're done for this deal and will be dealt another card next deal.\n\n" +
        "HOLD\n" +
        "You stop taking cards for the rest of the round. Your score is locked in.\n\n" +
        "GOING OVER\n" +
        "Going over the target after a deal is only a warning (\"Over target!\") - you can still play a " +
        "minus card to get back under. If you end your turn or hold while still over the target, you " +
        "bust and lose the round when the deal resolves.\n\n" +
        "HOW A ROUND ENDS\n" +
        "Once both players have pressed End Turn or Hold, the deal resolves:\n" +
        "- Anyone over the target busts. If both bust, the round is a tie and is replayed.\n" +
        "- If both players are holding, the higher score wins the round. Equal scores tie and the round " +
        "is replayed.\n" +
        "- Otherwise the next deal is dealt to everyone who isn't holding.\n\n" +
        "Filling all 9 board slots without busting is still a good place to be - hold!\n\n" +
        "VS. BOT\n" +
        "The bot plays the same rules as you and decides at the same time as you do - you never have " +
        "to wait for it to take your turn.";

    private Control _howToPlayOverlay;
    private PanelContainer _howToPlayPanel;
    private Button _howToPlayFlipButton;
    private bool _howToPlayFlipped = false;

    private void BuildHowToPlay()
    {
        // 1. The button, in the middle panel's ButtonColumn just above the Restart / Exit row.
        Control column = GetNodeOrNull<Control>("GameUI/MainLayout/SharedControlPanel/VBoxContainer/TableRow/ButtonColumn");
        Node systemButtons = _restartButton?.GetParent();
        if (column == null && systemButtons?.GetParent() is Control fallback) column = fallback;
        if (column == null) return;

        Button open = new Button { Text = "How to Play" };
        open.Pressed += ShowHowToPlay;
        column.AddChild(open);
        if (systemButtons != null && systemButtons.GetParent() == column)
        {
            column.MoveChild(open, systemButtons.GetIndex());
        }

        // 2. The overlay: dim + centred panel + scrolling rules + Close (and Flip when mirrored).
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

        Label rules = new Label
        {
            Text = HowToPlayText,
            AutowrapMode = TextServer.AutowrapMode.Word,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        rules.AddThemeFontSizeOverride("font_size", 20);
        scroll.AddChild(rules);

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

    private void ShowHowToPlay()
    {
        if (_howToPlayOverlay == null) return;
        _howToPlayFlipped = false;
        _howToPlayPanel.RotationDegrees = 0f;
        _howToPlayFlipButton.Visible = IsMirrored;
        MoveChild(_howToPlayOverlay, GetChildCount() - 1); // above any stray animation card
        _howToPlayOverlay.Visible = true;
    }

    private void HideHowToPlay()
    {
        if (_howToPlayOverlay == null) return;
        _howToPlayOverlay.Visible = false;
    }

    // ------------------------------------------------------------------
    // Audio
    // ------------------------------------------------------------------
    private AudioStreamPlayer CreateSfx(string path)
    {
        AudioStream stream = GD.Load<AudioStream>(path);
        if (stream == null) return null;
        AudioStreamPlayer player = new AudioStreamPlayer { Stream = stream, VolumeDb = -4f };
        AddChild(player);
        return player;
    }
}
