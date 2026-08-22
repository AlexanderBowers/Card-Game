using Godot;
using System;
using System.Net.Http.Headers;

public partial class GameManager : Node
{
	//References to our scene nodes
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

	private Button _endTurnButton;
	private Button _holdButton;

	// Track active turn (1 for player1, 2 for player2)
	private int _currentActivePlayer = 1;
	private Random _random = new Random();

    public override void _Ready()
	{
		//Initialize data structures
		_gameState = new GameState();
		_player1 = new Player("Player 1");
		_player2 = new Player("Player 2");

		//Fetch UI node references based on table_scene.tscn paths
		_p1ScoreLabel = GetNode<Label>("GameUI/Player1Side/ScoreLabel");
		_p1StatusLabel = GetNode<Label>("GameUI/Player1Side/StatusLabel");
		_p1WinsLabel = GetNodeOrNull<Label>("GameIU/Player1Side/WinsLabel");
		_p2ScoreLabel = GetNode<Label>("GameUI/Player2Side/ScoreLabel");
		_p2StatusLabel = GetNode<Label>("GameUI/Player2Side/StatusLabel");
		_p2WinsLabel = GetNodeOrNull<Label>("GameUI/Player2Side/WinsLabel");
		_roundInfoLabel = GetNode<Label>("GameUI/SharedControlPanel/RoundInfoLabel");

		_endTurnButton = GetNode<Button>("GameUI/SharedControlPanel/EndTurnButton");
		_holdButton = GetNode<Button>("GameUI/SharedControlPanel/HoldButton");

		//Connect Button signals
		_endTurnButton.Pressed += OnEndTurnPressed;
		_holdButton.Pressed += OnHoldPressed;

		StartNewRound();
	}

	private void StartNewRound()
	{
		_player1.ResetForNewRound();
		_player2.ResetForNewRound();

		_currentActivePlayer = 1;
		_player1.IsActiveTurn = true;

		//Draw initial card for Player 1 to kick off the round
		DrawCardForActivePlayer();
		UpdateUI();
	}
	private void DrawCardForActivePlayer()
	{
		Player activePlayer = (_currentActivePlayer == 1) ? _player1 : _player2;
		
		if (activePlayer.IsHolding) return;

		//Draw random main deck card from 1 to 10
		int cardValue = _random.Next(1, 11);
		activePlayer.CurrentScore += cardValue;
		
	}
	private void OnEndTurnPressed()
	{
		Player activePlayer = (_currentActivePlayer == 1) ? _player1 : _player2;
		if (activePlayer.IsHolding) return;

		//Switch turns if the other player is not holding
		SwitchTurn();
	}

	private void OnHoldPressed()
	{
		Player activePlayer = (_currentActivePlayer == 1) ? _player1 : _player2;
		activePlayer.IsHolding = true;
		activePlayer.IsActiveTurn = false;

		GD.Print($"{activePlayer.PlayerName} chose to HOLD at {activePlayer.CurrentScore}");

		CheckRoundConclusion();
		if (!_gameState.IsGameOver)
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

		//Switch active player index
		if (_currentActivePlayer == 1)
		{
			_currentActivePlayer = 2;
			_player1.IsActiveTurn = false;
			_player2.IsActiveTurn = !_player2.IsHolding;

			if (!_player2.IsHolding) DrawCardForActivePlayer();
		}
		else
		{
			_currentActivePlayer = 1;
			_player2.IsActiveTurn = false;
			_player1.IsActiveTurn = !_player1.IsHolding;

			if (!_player1.IsHolding) DrawCardForActivePlayer();
		}

		UpdateUI();
	}

	private void HandlePlayerBust(Player bustingPlayer)
	{
		bustingPlayer.IsHolding = true;
		//If current player busts, give turn or check round end
		SwitchTurn();
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

		if (p1Bust)
		{
			roundWinner = 2;
		}
		else if (p2Bust)
		{
			roundWinner = 1;
		}
		else
		{
			int p1Diff = target - p1Score;
			int p2Diff = target - p2Score;

			if (p1Diff < p2Diff) roundWinner = 1;
			else if (p2Diff < p1Diff) roundWinner = 2;
		}
		if(roundWinner == 0)
		{
			GD.Print("Tie. Replaying Round...");
			_roundInfoLabel.Text = "Tie. Replaying Round...";
		}
		else
		{
			_gameState.RecordRoundWinner(roundWinner);
			GD.Print($"Player {roundWinner} won Round {_gameState.CurrentRound}");
			_roundInfoLabel.Text = $"Player {roundWinner} has won this round.";
			_gameState.CurrentRound++;
		}

		//Check match victory (Best of 3 Default)
		{
			if (_gameState.CheckMatchWinner(out int matchWinner))
			{
				GD.Print($"Player {matchWinner} wins the match");
				_roundInfoLabel.Text = $"Player {matchWinner} wins the match.";
			}
			else
			{
				StartNewRound();
			}
		}
	}

	private void UpdateUI()
	{
		_p1ScoreLabel.Text = $"Score: {_player1.CurrentScore}";
		_p2ScoreLabel.Text = $"Score: {_player2.CurrentScore}";

		_p1StatusLabel.Text = _player1.IsHolding ? "Holding" : (_player1.IsActiveTurn ? "Active Turn" : "Waiting");
		_p2StatusLabel.Text = _player2.IsHolding ? "Holding" : (_player2.IsActiveTurn ? "Active Turn" : "Waiting");

		//Update Best of 3 Scoreboard counters if the labels exist
		if (_p1WinsLabel != null) _p1WinsLabel.Text = $"Round Wins: {_gameState.RoundsWonPlayer1}";
		if (_p2WinsLabel != null) _p2WinsLabel.Text = $"Round Wins: {_gameState.RoundsWonPlayer2}";

		_roundInfoLabel.Text = $"Round {_gameState.CurrentRound} - Target: {_gameState.TargetScore} | Turn: P{_currentActivePlayer}";
	}
	
}
