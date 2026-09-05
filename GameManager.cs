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

    [ExportGroup("Player 2 UI (AI)")]
    [Export] private Label _p2ScoreLabel;
    [Export] private Label _p2StatusLabel;
    [Export] private Label _p2WinsLabel;
    [Export] private Control _p2BoardContainer;
    [Export] private Control _p2HandContainer;
    [Export] private Control _p2Rotator;

    [ExportGroup("Shared UI")]
    [Export] private Label _roundInfoLabel;
    [Export] private OptionButton _gameModeButton;
    [Export] private CheckButton _mirrorToggle; // 2-player scene only: rotate P2's side 180 degrees
    [Export] private Button _startButton;
    [Export] private Button _endTurnButton;
    [Export] private Button _holdButton;
    [Export] private Control _mainDeckPosition;

    [ExportGroup("System UI")]
    [Export] private Button _restartButton;
    [Export] private Button _exitButton;

    private int _currentActivePlayer = 1;
    private Random _random = new Random();
    private PackedScene _cardViewScene = GD.Load<PackedScene>("res://CardView.tscn");
    private bool _isGameStarted = false;
    private bool _isVsBot = false;
    private bool _aiTurnInProgress = false; // blocks human input while the bot is "thinking"

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

        // Connect button signals
        _startButton.Pressed += OnStartButtonPressed;
        _endTurnButton.Pressed += OnEndTurnPressed;
        _holdButton.Pressed += OnHoldPressed;
        if (_restartButton != null) _restartButton.Pressed += OnRestartPressed;
        if (_exitButton != null) _exitButton.Pressed += OnExitPressed;

        // Set initial waiting message
        _roundInfoLabel.Text = "Press Start Game to Begin";
        _endTurnButton.Disabled = true;
        _holdButton.Disabled = true;

        // Show the empty 3x3 boards and the win chips before the game starts.
        FillBoardWithSlots(_p1BoardContainer);
        FillBoardWithSlots(_p2BoardContainer);
        _p1WinChips = EnsureWinChips(_p1WinsLabel);
        _p2WinChips = EnsureWinChips(_p2WinsLabel);

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
    private const float BaseSide = 720f;
    private static readonly Vector2 NeedPortrait = new Vector2(420, 1350);
    private static readonly Vector2 NeedLandscape = new Vector2(1000, 620);

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

        _endTurnButton.Disabled = false;
        _holdButton.Disabled = false;

        StartNewRound();
    }

    private void StartNewRound()
    {
        _player1.ResetForNewRound();
        _player2.ResetForNewRound();

        // Clear old cards and lay out fresh empty 3x3 boards
        FillBoardWithSlots(_p1BoardContainer);
        FillBoardWithSlots(_p2BoardContainer);

        _currentActivePlayer = 1;
        _player1.IsActiveTurn = true;
        _player2.IsActiveTurn = false;

        DrawCardForActivePlayer();
        UpdateUI();
    }

    private void DrawCardForActivePlayer()
    {
        Player activePlayer = (_currentActivePlayer == 1) ? _player1 : _player2;

        if (activePlayer.IsHolding) return;

        int cardValue = _random.Next(1, 11);
        activePlayer.CurrentScore += cardValue;

        Card drawnMainCard = new Card(cardValue, CardType.Main, cardValue.ToString());
        activePlayer.ActiveCardsOnBoard.Add(drawnMainCard);

        GD.Print($"{activePlayer.PlayerName} drew a {cardValue}. Score: {activePlayer.CurrentScore}");

        Control boardContainer = (_currentActivePlayer == 1) ? _p1BoardContainer : _p2BoardContainer;

        InstantiateCardView(drawnMainCard, boardContainer);

        // A draw that busts with no way back (no minus card that would bring the score under the
        // target) ends the round immediately - no point letting End Turn deal more cards.
        if (activePlayer.CurrentScore > _gameState.TargetScore && !CanRecoverFromBust(activePlayer))
        {
            HandlePlayerBust(activePlayer);
            return;
        }

        if (_isVsBot && _currentActivePlayer == 2 && !activePlayer.IsHolding)
        {
            ProcessAiTurn();
        }
    }

    private bool CanRecoverFromBust(Player player)
    {
        foreach (Card card in player.ModifierHand)
        {
            if (card.Value < 0 && player.CurrentScore + card.Value <= _gameState.TargetScore) return true;
        }
        return false;
    }

    private async void ProcessAiTurn()
    {
        if (_aiTurnInProgress) return; // never run two AI turns at once
        _aiTurnInProgress = true;
        UpdateUI(); // locks End Turn / Hold / hand while the bot thinks

        //1. Wait a moment to let the player see the AI's drawn card
        await ToSignal(GetTree().CreateTimer(1.0f), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return; // scene was restarted/exited mid-turn

        //2. The AI will decide if it wants to play a modifier.
        bool playedModifier = TryAiPlayModifierCard();

        if (playedModifier)
        {
            //If the AI played a modifier, wait 1.5 seconds to let the player see it
            await ToSignal(GetTree().CreateTimer(1.5f), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return;
        }

        _aiTurnInProgress = false;

        //3. Evaluate the score at the end of the turn.
        if (_player2.CurrentScore > _gameState.TargetScore)
        {
            HandlePlayerBust(_player2);
        }
        else
        {
            int target = _gameState.TargetScore;
            int holdThreshold = Math.Max(10, target - 2);

            if (_player1.IsHolding && _player1.CurrentScore <= target)
            {
                holdThreshold = _player1.CurrentScore;
            }

            if (_player2.CurrentScore >= holdThreshold || _player2.CurrentScore == target)
            {
                GD.Print($"AI decides to HOLD at {_player2.CurrentScore} (Target: {target})");
                _player2.IsHolding = true;
                _player2.IsActiveTurn = false;

                if (_player1.IsHolding)
                {
                    EvaluateRoundWinner();
                }
                else
                {
                    SwitchTurn();
                }
            }
            else
            {
                SwitchTurn();
            }
        }
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
    private bool HumanCanAct()
    {
        if (!_isGameStarted || _gameState.IsGameOver || _aiTurnInProgress) return false;
        if (_isVsBot && _currentActivePlayer == 2) return false; // it's the bot's turn
        return true;
    }

    private void OnEndTurnPressed()
    {
        if (!HumanCanAct()) return;

        Player activePlayer = (_currentActivePlayer == 1) ? _player1 : _player2;
        if (activePlayer.IsHolding) return;

        if (activePlayer.CurrentScore > _gameState.TargetScore)
        {
            HandlePlayerBust(activePlayer);
        }
        else
        {
            SwitchTurn();
        }
    }

    private void OnHoldPressed()
    {
        if (!HumanCanAct()) return;

        Player activePlayer = (_currentActivePlayer == 1) ? _player1 : _player2;
        if (activePlayer.IsHolding) return;

        activePlayer.IsHolding = true;
        activePlayer.IsActiveTurn = false;

        GD.Print($"{activePlayer.PlayerName} chose to HOLD at {activePlayer.CurrentScore}");

        if (_player1.IsHolding && _player2.IsHolding)
        {
            EvaluateRoundWinner();
        }
        else
        {
            SwitchTurn();
        }
    }

    private void SwitchTurn()
    {
        if (_player1.IsHolding && _player2.IsHolding)
        {
            EvaluateRoundWinner();
            return;
        }

        if (_currentActivePlayer == 1)
        {
            if (!_player2.IsHolding)
            {
                _currentActivePlayer = 2;
                _player1.IsActiveTurn = false;
                _player2.IsActiveTurn = !_player2.IsHolding;
            }
        }
        else
        {
            if (!_player1.IsHolding)
            {
                _currentActivePlayer = 1;
                _player2.IsActiveTurn = false;
                _player1.IsActiveTurn = !_player1.IsHolding;
            }
        }

        DrawCardForActivePlayer();
        UpdateUI();
    }

    private void HandlePlayerBust(Player bustingPlayer)
    {
        bustingPlayer.IsHolding = true;
        bustingPlayer.IsActiveTurn = false;
        GD.Print($"{bustingPlayer.PlayerName} is forced to hold due to busting.");
        EvaluateRoundWinner();
    }

    private void CheckRoundConclusion()
    {
        if (_player1.IsHolding && _player2.IsHolding)
        {
            EvaluateRoundWinner();
        }
    }

    private void EvaluateRoundWinner()
    {
        int p1Score = _player1.CurrentScore;
        int p2Score = _player2.CurrentScore;
        int target = _gameState.TargetScore;

        bool p1Bust = p1Score > target;
        bool p2Bust = p2Score > target;

        int roundWinner = 0;

        if (p1Bust && p2Bust) roundWinner = 0;
        else if (p1Bust) roundWinner = 2;
        else if (p2Bust) roundWinner = 1;
        else
        {
            int p1Diff = target - p1Score;
            int p2Diff = target - p2Score;

            if (p1Diff < p2Diff) roundWinner = 1;
            else if (p2Diff < p1Diff) roundWinner = 2;
        }

        if (roundWinner == 0)
        {
            GD.Print("Tie. Replaying Round...");
            _roundInfoLabel.Text = "Tie. Replaying Round...";
        }
        else
        {
            _gameState.RecordRoundWinner(roundWinner);
            GD.Print($"Player {roundWinner} won Round {_gameState.CurrentRound}");
            _roundInfoLabel.Text = $"Player {roundWinner} won this round!";
            _gameState.CurrentRound++;
        }

        if (_gameState.CheckMatchWinner(out int matchWinner))
        {
            GD.Print($"Player {matchWinner} wins the match");
            _roundInfoLabel.Text = $"Player {matchWinner} wins the match!";
            UpdateUI(); // IsGameOver is now set, so this disables the buttons and hand cards
        }
        else
        {
            StartNewRound();
        }
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

        if (_p1StatusLabel != null) _p1StatusLabel.Text = _player1.IsHolding ? "Holding" : (_player1.IsActiveTurn ? "Active Turn" : "Waiting");
        if (_p2StatusLabel != null) _p2StatusLabel.Text = _player2.IsHolding ? "Holding" : (_player2.IsActiveTurn ? "Active Turn" : "Waiting");

        // Round wins are shown as chips next to the label (see UpdateWinChips).
        if (_p1WinsLabel != null) _p1WinsLabel.Text = "Wins:";
        if (_p2WinsLabel != null) _p2WinsLabel.Text = "Wins:";
        UpdateWinChips();

        if (_isGameStarted && _roundInfoLabel != null && !_gameState.IsGameOver)
        {
            string turn = (_isVsBot && _currentActivePlayer == 2) ? "Bot thinking..." : $"Turn: P{_currentActivePlayer}";
            _roundInfoLabel.Text = $"Round {_gameState.CurrentRound} - Target: {_gameState.TargetScore} | {turn}";
        }

        // Only the person whose turn it is can act; everything is locked during the bot's turn.
        bool canAct = HumanCanAct();
        if (_endTurnButton != null) _endTurnButton.Disabled = !canAct;
        if (_holdButton != null) _holdButton.Disabled = !canAct;

        RefreshHandUI();
        CallDeferred(MethodName.UpdateRotatorSize);
    }

    private void RefreshHandUI()
    {
        if (_p1HandContainer == null || _p2HandContainer == null || _player1 == null) return;

        // Detach immediately, not just QueueFree: queued nodes stay in the tree until the end of
        // the frame and would still count towards the hand's minimum size when the deferred
        // UpdateRotatorSize runs (P2's side then reserved room for 8-12 cards and pushed P1 off-screen).
        ClearChildren(_p1HandContainer);
        ClearChildren(_p2HandContainer);

        bool canAct = HumanCanAct();
        foreach (Card card in _player1.ModifierHand)
        {
            bool disabled = (!canAct || _currentActivePlayer != 1 || _player1.IsHolding);
            _p1HandContainer.AddChild(CreateHandCardButton(card, disabled, () => OnModifierCardPressed(_player1, card)));
        }

        foreach (Card card in _player2.ModifierHand)
        {
            bool disabled = (!canAct || _currentActivePlayer != 2 || _player2.IsHolding);
            _p2HandContainer.AddChild(CreateHandCardButton(card, disabled, () => OnModifierCardPressed(_player2, card)));
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
        if (!HumanCanAct() || !player.IsActiveTurn || player.IsHolding) return;

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
