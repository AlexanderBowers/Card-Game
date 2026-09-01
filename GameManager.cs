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
    [Export] private Button _startButton;
    [Export] private Button _endTurnButton;
    [Export] private Button _holdButton;

    private int _currentActivePlayer = 1;
    private Random _random = new Random();
    private PackedScene _cardViewScene = GD.Load<PackedScene>("res://CardView.tscn");
    private bool _isGameStarted = false;
    private bool _isVsBot = false;

    public override void _Ready()
    {
        _gameState = new GameState();
        _player1 = new Player("Player 1");
        _player2 = new Player("Player 2");

        // Force the rotation for local co-op, bypassing the Godot Container lock
        if (_p2Rotator != null)
        {
            _p2Rotator.RotationDegrees = 180;
        }

        if (_gameModeButton != null)
        {
            _gameModeButton.Clear();
            _gameModeButton.AddItem("Local 2-Player", 0);
            _gameModeButton.AddItem("vs. Bot", 1);
            _gameModeButton.Select(1);
        }

        // Connect button signals
        _startButton.Pressed += OnStartButtonPressed;
        _endTurnButton.Pressed += OnEndTurnPressed;
        _holdButton.Pressed += OnHoldPressed;

        // Set initial waiting message
        _roundInfoLabel.Text = "Press Start Game to Begin";
        _endTurnButton.Disabled = true;
        _holdButton.Disabled = true;
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

        foreach (Node child in _p1BoardContainer.GetChildren()) child.QueueFree();
        foreach (Node child in _p2BoardContainer.GetChildren()) child.QueueFree();

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
        
        if(_isVsBot && _currentActivePlayer == 2 && !activePlayer.IsHolding)
        {
            ProcessAiTurn();
        }
    }

    private void ProcessAiTurn()
    {   
        TryAiPlayModifierCard();

        if(_player2.CurrentScore > _gameState.TargetScore)
        {
            HandlePlayerBust(_player2);
        }
        else
        {
            int target = _gameState.TargetScore;
            int holdThreshold = Math.Max(10, target - 2);
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

    private void TryAiPlayModifierCard()
    {
        int target = _gameState.TargetScore;
        Card bestCardToPlay = null;
        int highValueThreshold = target - 2;
        int projectedScore = 0;

        foreach(Card card in _player2.ModifierHand)
        {
            projectedScore = _player2.CurrentScore + card.Value;
            
            if(_player2.CurrentScore > target && card.Value < 0 && projectedScore <= target)
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
        }
    }

    private void OnEndTurnPressed()
    {
        if (!_isGameStarted) return;

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
        if (!_isGameStarted) return;

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
            _endTurnButton.Disabled = true;
            _holdButton.Disabled = true;
        }
        else
        {
            StartNewRound();
        }
    }

    private void UpdateUI()
    {
        // If _p2Rotator is linked, we are in the face-to-face scene and need dual scores.
        // Otherwise, we are in the side-by-side solo scene and just need standard scores.
        if (_p2Rotator != null)
        {
            if (_p1ScoreLabel != null) 
                _p1ScoreLabel.Text = $"P1 Score: {_player1.CurrentScore}  |  P2 Score: {_player2.CurrentScore}";
                
            if (_p2ScoreLabel != null) 
                _p2ScoreLabel.Text = $"P2 Score: {_player2.CurrentScore}  |  P1 Score: {_player1.CurrentScore}";
        }
        else
        {
            if (_p1ScoreLabel != null) _p1ScoreLabel.Text = $"Score: {_player1.CurrentScore}";
            if (_p2ScoreLabel != null) _p2ScoreLabel.Text = $"Score: {_player2.CurrentScore}";
        }

        if (_p1StatusLabel != null) _p1StatusLabel.Text = _player1.IsHolding ? "Holding" : (_player1.IsActiveTurn ? "Active Turn" : "Waiting");
        if (_p2StatusLabel != null) _p2StatusLabel.Text = _player2.IsHolding ? "Holding" : (_player2.IsActiveTurn ? "Active Turn" : "Waiting");

        if (_p1WinsLabel != null) _p1WinsLabel.Text = $"Round Wins: {_gameState.RoundsWonPlayer1}";
        if (_p2WinsLabel != null) _p2WinsLabel.Text = $"Round Wins: {_gameState.RoundsWonPlayer2}";

        if (_isGameStarted && _roundInfoLabel != null)
        {
            _roundInfoLabel.Text = $"Round {_gameState.CurrentRound} - Target: {_gameState.TargetScore} | Turn: P{_currentActivePlayer}";
        }

        RefreshHandUI();
    }
    
    private void RefreshHandUI()
    {
        foreach(Node child in _p1HandContainer.GetChildren()) child.QueueFree();
        foreach (Node child in _p2HandContainer.GetChildren()) child.QueueFree();

        foreach (Card card in _player1.ModifierHand)
        {
            Button cardButton = new Button();
            cardButton.Text = card.CardName;
            cardButton.CustomMinimumSize = new Vector2(60, 50);
            cardButton.Disabled = (!_isGameStarted || _currentActivePlayer != 1 || _player1.IsHolding);
            cardButton.Pressed += () => OnModifierCardPressed(_player1, card);
            _p1HandContainer.AddChild(cardButton);
        }

        foreach (Card card in _player2.ModifierHand)
        {
            Button cardButton = new Button();
            cardButton.Text = card.CardName;
            cardButton.CustomMinimumSize = new Vector2(60, 50);
            cardButton.Disabled = (!_isGameStarted || _currentActivePlayer != 2 || _player2.IsHolding);
            cardButton.Pressed += () => OnModifierCardPressed(_player2, card);
            _p2HandContainer.AddChild(cardButton);
        }
    }

    private void OnModifierCardPressed(Player player, Card card)
    {
        if (!player.IsActiveTurn || player.IsHolding) return;

        if (player.PlayModifierCard(card, _gameState))
        {
            Control boardContainer = (player == _player1) ? _p1BoardContainer : _p2BoardContainer;

            InstantiateCardView(card, boardContainer);

            UpdateUI();
        }
    }
    
    private void InstantiateCardView(Card card, Control parentContainer)
    {
        if (_cardViewScene != null)
        {
            Control cardNode = (Control)_cardViewScene.Instantiate();
            Label label = cardNode.GetNodeOrNull<Label>("Value") ?? cardNode.GetNodeOrNull<Label>("Label");
            Panel bgPanel = cardNode as Panel;

            if (label != null)
            {
                if (card.Type != CardType.Main && card.Value > 0)
                    label.Text = "+" + card.Value.ToString();
                else
                    label.Text = card.CardName;
            }

            if (bgPanel != null)
            {
                StyleBoxFlat styleBox = new StyleBoxFlat();
                styleBox.CornerRadiusTopLeft = 6;
                styleBox.CornerRadiusTopRight = 6;
                styleBox.CornerRadiusBottomLeft = 6;
                styleBox.CornerRadiusBottomRight = 6;
                
                styleBox.BorderWidthTop = 2;
                styleBox.BorderWidthBottom = 2;
                styleBox.BorderWidthLeft = 2;
                styleBox.BorderWidthRight = 2;
                styleBox.BorderColor = new Color(0.9f, 0.9f, 0.9f);

                if (card.Type == CardType.Main)
                {
                    styleBox.BgColor = new Color(0.15f, 0.55f, 0.15f); 
                }
                else
                {
                    if (card.Value > 0)
                        styleBox.BgColor = new Color(0.15f, 0.55f, 0.15f); 
                    else if (card.Value < 0)
                        styleBox.BgColor = new Color(0.75f, 0.15f, 0.15f); 
                    else
                        styleBox.BgColor = new Color(0.15f, 0.35f, 0.75f); 
                }

                bgPanel.AddThemeStyleboxOverride("panel", styleBox);
            }

            parentContainer.AddChild(cardNode);
        }
    }
}