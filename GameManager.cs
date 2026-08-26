using Godot;
using System;

public partial class GameManager : Node
{
    private GameState _gameState;
    private Player _player1;
    private Player _player2;

    private Label _p1ScoreLabel;
    private Label _p1StatusLabel;
    private Label _p1WinsLabel;
    private Label _p2ScoreLabel;
    private Label _p2StatusLabel;
    private Label _p2WinsLabel;
    private Label _roundInfoLabel;

    private Button _startButton;
    private Button _endTurnButton;
    private Button _holdButton;

    private int _currentActivePlayer = 1;
    private Random _random = new Random();
    private PackedScene _cardViewScene = GD.Load<PackedScene>("res://CardView.tscn");
    private bool _isGameStarted = false;

    public override void _Ready()
    {
        _gameState = new GameState();
        _player1 = new Player("Player 1");
        _player2 = new Player("Player 2");

        // Fetch UI node references
        _p1ScoreLabel = GetNode<Label>("GameUI/Player1Side/ScoreLabel");
        _p1StatusLabel = GetNode<Label>("GameUI/Player1Side/StatusLabel");
        _p1WinsLabel = GetNodeOrNull<Label>("GameUI/Player1Side/WinsLabel");
        _p2ScoreLabel = GetNode<Label>("GameUI/Player2Side/ScoreLabel");
        _p2StatusLabel = GetNode<Label>("GameUI/Player2Side/StatusLabel");
        _p2WinsLabel = GetNodeOrNull<Label>("GameUI/Player2Side/WinsLabel");
        _roundInfoLabel = GetNode<Label>("GameUI/SharedControlPanel/RoundInfoLabel");

        _startButton = GetNode<Button>("GameUI/SharedControlPanel/StartButton");
        _endTurnButton = GetNode<Button>("GameUI/SharedControlPanel/EndTurnButton");
        _holdButton = GetNode<Button>("GameUI/SharedControlPanel/HoldButton");

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
        _isGameStarted = true;
        _startButton.Visible = false; 
        _endTurnButton.Disabled = false;
        _holdButton.Disabled = false;

        StartNewRound();
    }

    private void StartNewRound()
    {
        _player1.ResetForNewRound();
        _player2.ResetForNewRound();

        Control p1BoardContainer = GetNode<Control>("GameUI/Player1Side/BoardSlotsContainer");
        Control p2BoardContainer = GetNode<Control>("GameUI/Player2Side/BoardSlotsContainer");
        foreach (Node child in p1BoardContainer.GetChildren()) child.QueueFree();
        foreach (Node child in p2BoardContainer.GetChildren()) child.QueueFree();

        _currentActivePlayer = 1;
        _player1.IsActiveTurn = true;
        _player2.IsActiveTurn = false;

        // Draw Player 1's opening card immediately and update UI in correct order
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

        Control boardContainer = (_currentActivePlayer == 1)
            ? GetNode<Control>("GameUI/Player1Side/BoardSlotsContainer")
            : GetNode<Control>("GameUI/Player2Side/BoardSlotsContainer");
            
        InstantiateCardView(drawnMainCard, boardContainer);
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
        
        //Toggle active player if the other player is not holding.
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
        _p1ScoreLabel.Text = $"Score: {_player1.CurrentScore}";
        _p2ScoreLabel.Text = $"Score: {_player2.CurrentScore}";

        _p1StatusLabel.Text = _player1.IsHolding ? "Holding" : (_player1.IsActiveTurn ? "Active Turn" : "Waiting");
        _p2StatusLabel.Text = _player2.IsHolding ? "Holding" : (_player2.IsActiveTurn ? "Active Turn" : "Waiting");

        if (_p1WinsLabel != null) _p1WinsLabel.Text = $"Round Wins: {_gameState.RoundsWonPlayer1}";
        if (_p2WinsLabel != null) _p2WinsLabel.Text = $"Round Wins: {_gameState.RoundsWonPlayer2}";

        if (_isGameStarted)
        {
            _roundInfoLabel.Text = $"Round {_gameState.CurrentRound} - Target: {_gameState.TargetScore} | Turn: P{_currentActivePlayer}";
        }

        RefreshHandUI();
    }
    
    private void RefreshHandUI()
    {
        Control p1HandContainer = GetNode<Control>("GameUI/Player1Side/HandContainer");
        Control p2HandContainer = GetNode<Control>("GameUI/Player2Side/HandContainer");

        foreach(Node child in p1HandContainer.GetChildren()) child.QueueFree();
        foreach (Node child in p2HandContainer.GetChildren()) child.QueueFree();

        foreach (Card card in _player1.ModifierHand)
        {
            Button cardButton = new Button();
            cardButton.Text = card.CardName;
            cardButton.CustomMinimumSize = new Vector2(60, 50);
            cardButton.Disabled = (!_isGameStarted || _currentActivePlayer != 1 || _player1.IsHolding);
            cardButton.Pressed += () => OnModifierCardPressed(_player1, card);
            p1HandContainer.AddChild(cardButton);
        }

        foreach (Card card in _player2.ModifierHand)
        {
            Button cardButton = new Button();
            cardButton.Text = card.CardName;
            cardButton.CustomMinimumSize = new Vector2(60, 50);
            cardButton.Disabled = (!_isGameStarted || _currentActivePlayer != 2 || _player2.IsHolding);
            cardButton.Pressed += () => OnModifierCardPressed(_player2, card);
            p2HandContainer.AddChild(cardButton);
        }
    }

    private void OnModifierCardPressed(Player player, Card card)
    {
        if (!player.IsActiveTurn || player.IsHolding) return;

        if (player.PlayModifierCard(card, _gameState))
        {
            Control boardContainer = (player == _player1)
                ? GetNode<Control>("GameUI/Player1Side/BoardSlotsContainer")
                : GetNode<Control>("GameUI/Player2Side/BoardSlotsContainer");

            InstantiateCardView(card, boardContainer);

            UpdateUI();
        }
    }
    
    private void InstantiateCardView(Card card, Control parentContainer)
    {
        if (_cardViewScene != null)
        {
            Control cardNode = (Control)_cardViewScene.Instantiate();
            Label label = cardNode.GetNodeOrNull<Label>("Label");
            if (label != null)
            {
                label.Text = card.CardName;
            }
            parentContainer.AddChild(cardNode);
        }
    }
}