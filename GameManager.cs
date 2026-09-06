using Godot;
using System;

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

    // ------------------------------------------------------------------
    // UI scaling
    //
    // project.godot uses a 720x720 base viewport with stretch aspect "expand", so the SHORT
    // side of whatever screen we're on is always 720 design-pixels and the long side grows.
    // Everything below is sized in those design pixels. ApplyResponsiveLayout enlarges that
    // base (shrinking the whole UI uniformly) when a screen is too short for the full layout.
    // ------------------------------------------------------------------
    private const int BoardSlots = 9;                       // 3x3 board
    private const int WinsToTakeMatch = 2;                  // must match GameState.CheckMatchWinner
    private static readonly Vector2 BaseCardSize = new Vector2(84, 114); // Kenney cards are 140x190
    private const float HandCardScale = 0.8f;
    private const float MinCardScale = 0.6f;
    private float _cardScale = 1f;
    private BoxContainer _mainLayout;

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
    private static readonly Rect2 RegionChipWon = new Rect2(0, 194, 68, 68);    // chipGreen_border
    private static readonly Rect2 RegionChipEmpty = new Rect2(68, 0, 68, 68);   // chipWhite_border
    private const float ChipSize = 28f;
    private HBoxContainer _p1WinChips;
    private HBoxContainer _p2WinChips;

    private AudioStreamPlayer _sfxSlide;
    private AudioStreamPlayer _sfxPlace;

    public override void _Ready()
    {
        _gameState = new GameState();
        _player1 = new Player("Player 1");
        _player2 = new Player("Player 2");

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

        GetTree().Root.SizeChanged += ApplyResponsiveLayout;
        ApplyResponsiveLayout();
        UpdateUI();
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
    private static readonly Vector2 NeedPortrait = new Vector2(420, 1520);
    private static readonly Vector2 NeedLandscape = new Vector2(1000, 690);

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

        StartNewRound(); // UpdateUI enables the End Turn / Hold buttons
    }

    private void StartNewRound()
    {
        _player1.ResetForNewRound();
        _player2.ResetForNewRound();

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

    private async void ProcessAiTurn()
    {
        if (_aiTurnInProgress || !_player2.CanAct) return; // never run two AI turns at once
        _aiTurnInProgress = true;
        UpdateUI(); // shows "Thinking..." on the bot's side

        //1. Wait a moment to let the player see the AI's drawn card
        await ToSignal(GetTree().CreateTimer(1.0f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return; // scene was restarted/exited mid-deal

        //2. The AI will decide if it wants to play a modifier.
        bool playedModifier = TryAiPlayModifierCard();

        if (playedModifier)
        {
            //If the AI played a modifier, wait 1.5 seconds to let the player see it
            await ToSignal(GetTree().CreateTimer(1.5f), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return;
        }

        _aiTurnInProgress = false;

        int target = _gameState.TargetScore;

        //3. Still over the target now = the bot ends its turn and busts when the deal resolves.
        if (_player2.CurrentScore > target)
        {
            GD.Print($"AI ends its turn over the target at {_player2.CurrentScore}");
            _player2.HasEndedTurn = true;
            ResolveDeal();
            return;
        }

        int holdThreshold = Math.Max(10, target - 2);

        if (_player1.IsHolding && _player1.CurrentScore <= target)
        {
            holdThreshold = _player1.CurrentScore;
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

    private bool TryAiPlayModifierCard()
    {
        int target = _gameState.TargetScore;
        Card bestCardToPlay = null;
        int highValueThreshold = target - 2;
        int projectedScore = 0;

        if (_player1.IsHolding && _player1.CurrentScore <= target)
        {
            highValueThreshold = _player1.CurrentScore;
        }

        foreach (Card card in _player2.ModifierHand)
        {
            projectedScore = _player2.CurrentScore + card.Value;

            if (_player2.CurrentScore > target && card.Value < 0 && projectedScore <= target)
            {
                bestCardToPlay = card;
                break;
            }
            else if (_player2.CurrentScore <= target && card.Value > 0 && projectedScore <= target)
            {
                if (projectedScore >= highValueThreshold || projectedScore == target)
                {
                    bestCardToPlay = card;
                    break;
                }
            }
        }

        if (bestCardToPlay != null)
        {
            GD.Print($"AI Bot plays modifier {bestCardToPlay.CardName}. New Score: {projectedScore} (Target: {target})");
            _player2.PlayModifierCard(bestCardToPlay, _gameState);
            InstantiateCardView(bestCardToPlay, _p2BoardContainer);

            UpdateUI();

            return true;
        }

        return false;
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
            buttonText = "Play Again";
            next = OnRestartPressed;
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

    // ------------------------------------------------------------------
    // UI refresh
    // ------------------------------------------------------------------
    private void UpdateUI()
    {
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

        if (_p1StatusLabel != null) _p1StatusLabel.Text = StatusFor(_player1);
        if (_p2StatusLabel != null) _p2StatusLabel.Text = StatusFor(_player2);

        // Round wins are shown as chips next to the label (see UpdateWinChips).
        if (_p1WinsLabel != null) _p1WinsLabel.Text = "Wins:";
        if (_p2WinsLabel != null) _p2WinsLabel.Text = "Wins:";
        UpdateWinChips();

        // While the round-end explanation is up, EndRound owns this label.
        if (_isGameStarted && _roundInfoLabel != null && !_gameState.IsGameOver && !_roundOverPending)
        {
            _roundInfoLabel.Text = $"Round {_gameState.CurrentRound} - Target: {_gameState.TargetScore} | {DealStatusText()}";
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

        // Still acting this deal.
        if (over) return "Over target!"; // a warning, not a bust yet: play a minus card before ending the turn
        if (_isVsBot && player == _player2) return "Thinking...";
        return "Your move";
    }

    private static void SetEnabled(Button button, bool enabled)
    {
        if (button != null) button.Disabled = !enabled;
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
            _p1HandContainer.AddChild(CreateHandCardButton(card, !p1Can, () => OnModifierCardPressed(_player1, card)));
        }

        foreach (Card card in _player2.ModifierHand)
        {
            _p2HandContainer.AddChild(CreateHandCardButton(card, !p2Can, () => OnModifierCardPressed(_player2, card)));
        }
    }

    /// A tappable modifier card: an invisible Button (so the theme's touch-friendly hit area
    /// and focus handling still apply) with the card art drawn on top.
    private Button CreateHandCardButton(Card card, bool disabled, Action onPressed)
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
        view.Modulate = disabled ? new Color(0.55f, 0.55f, 0.55f) : Colors.White;
        button.AddChild(view);
        view.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        return button;
    }

    private void OnModifierCardPressed(Player player, Card card)
    {
        if (!HumanCanActFor(player)) return;

        if (player.PlayModifierCard(card, _gameState))
        {
            Control boardContainer = (player == _player1) ? _p1BoardContainer : _p2BoardContainer;

            InstantiateCardView(card, boardContainer);

            UpdateUI();
        }
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
        bool twoWay = IsMirrored;
        int fontSize = Mathf.RoundToInt(size.Y * (twoWay ? 0.33f : 0.36f));

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
        }
    }

    private TextureRect CreateCardView(Card card, Vector2 size)
    {
        TextureRect view = (TextureRect)_cardViewScene.Instantiate();
        view.Texture = MakeAtlas(_cardSheet, RegionFor(card));
        ApplyCardSize(view, size);

        string text = (card.Type != CardType.Main && card.Value > 0) ? "+" + card.Value : card.CardName;
        foreach (string name in CardLabelNames)
        {
            Label label = view.GetNodeOrNull<Label>(name);
            if (label != null) label.Text = text;
        }

        // The bottom label is rotated about its own centre once the layout has given it a size.
        Label flipped = view.GetNodeOrNull<Label>("LabelFlipped");
        if (flipped != null)
        {
            flipped.Resized += () =>
            {
                flipped.PivotOffset = flipped.Size / 2f;
                flipped.RotationDegrees = 180f;
            };
        }
        return view;
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
    private const string HowToPlayText =
        "GOAL\n" +
        "Get as close to the target score (20) as you can without going over. " +
        "Win 2 rounds to win the match.\n\n" +
        "A ROUND\n" +
        "A round is a series of deals. Each deal, every player who isn't holding is dealt one card " +
        "(worth 1 to 10) at the same time. Both players then decide - at the same time, without waiting " +
        "for each other - whether to play a modifier card, and then press End Turn or Hold.\n\n" +
        "MODIFIER CARDS\n" +
        "Each player starts the match with a hand of +1, +2, -1 and -2. Tap one to play it onto your " +
        "board and add it to your score. Each modifier can only be used once per match, so spend them " +
        "wisely.\n\n" +
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
