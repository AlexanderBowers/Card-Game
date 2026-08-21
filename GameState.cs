using Godot;
using System;

public partial class GameState : Node
{
	public int TargetScore { get; set; } = 20;
	public int RoundsWonPlayer1 { get; set; } = 0;
	public int RoundsWonPlayer2 { get; set; } = 0;
	public int CurrentRound { get; set; } = 1;

	public bool IsgameOver { get; set; } = false;

	public void RecordRoundWinner(int winningPlayer)
	{
		if (winningPlayer == 1) RoundsWonPlayer1++;
		else if (winningPlayer == 2) RoundsWonPlayer2++;
	}

	public bool CheckMatchWinner(out int matchWinner)
	{
		matchWinner = 0;
		if (RoundsWonPlayer1 >= 2)
		{
			matchWinner = 1;
			IsgameOver = true;
			return true;
		}
		if (RoundsWonPlayer1 >= 2)
		{
			matchWinner = 2;
			IsgameOver = true;
			return true;
		}
		return false;
	}	
}
