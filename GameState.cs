using Godot;
using System;

public partial class GameState : Node
{
	/// How many rounds a player must win to take the match. Best of five (Alexander, 2026-09-12 -
	/// raised from two, for every mode: ladder, vs-bot and local 2-player alike).
	///
	/// GameManager's win-chip row draws one slot per win needed and reads this constant, so the
	/// two can no longer drift - they used to be two separate literals held together by a comment.
	public const int RoundsToWinMatch = 3;

	public int TargetScore { get; set; } = 20;
	public int RoundsWonPlayer1 { get; set; } = 0;
	public int RoundsWonPlayer2 { get; set; } = 0;
	public int CurrentRound { get; set; } = 1;

	public bool IsGameOver { get; set; } = false;

	public void RecordRoundWinner(int winningPlayer)
	{
		if (winningPlayer == 1) RoundsWonPlayer1++;
		else if (winningPlayer == 2) RoundsWonPlayer2++;
	}

	public bool CheckMatchWinner(out int matchWinner)
	{
		matchWinner = 0;
		if (RoundsWonPlayer1 >= RoundsToWinMatch)
		{
			matchWinner = 1;
			IsGameOver = true;
			return true;
		}
		if (RoundsWonPlayer2 >= RoundsToWinMatch)
		{
			matchWinner = 2;
			IsGameOver = true;
			return true;
		}
		return false;
	}	
}
