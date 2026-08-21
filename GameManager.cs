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
	private Label _p2ScoreLabel;
	private Label _p2StatusLabel;
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
		_p2ScoreLabel = GetNode<Label>("GameUI/Player2Side/ScoreLabel");
		_p2StatusLabel = GetNode<Label>("GameUI/Player2Side/StatusLabel");
		_roundInfoLabel = GetNode<Label>("GameUI/SharedControlPanel/RoundInfoLabel");

		_endTurnButton = GetNode<Button>("GameUI/SharedControlPanel/EndTurnButton");
		_holdButton = GetNode<Button>("GameUI/SharedControlPanel/HoldButton");

		//Connect Button signals
		_endTurnButton.KeepPressedOutside += OnEndTurnPressed;
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

	
}
