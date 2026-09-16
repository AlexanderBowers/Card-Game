using Godot;
using System;

public partial class GameState : Node
{
	/// How many sets a player must win to take the match. Best of five (Alexander, 2026-09-12 -
	/// raised from two, for every mode: ladder, vs-bot and local 2-player alike).
	///
	/// GameManager's win-chip row draws one slot per win needed and reads this constant, so the
	/// two can no longer drift - they used to be two separate literals held together by a comment.
	public const int SetsToWinMatch = 3;

	public int TargetScore { get; set; } = 20;
	public int SetsWonPlayer1 { get; set; } = 0;
	public int SetsWonPlayer2 { get; set; } = 0;

	/// True until someone has WON a set this match. The set number itself is insignificant
	/// (Alexander, 2026-09-16) - it is never shown and never stored; the only thing the game ever
	/// needed from it was "is this the first set", and the win counts already answer that. A tied
	/// set is replayed, so a tie leaves this true, exactly as the old counter did.
	public bool IsFirstSet => SetsWonPlayer1 + SetsWonPlayer2 == 0;

	public bool IsGameOver { get; set; } = false;

	public void RecordSetWinner(int winningPlayer)
	{
		if (winningPlayer == 1) SetsWonPlayer1++;
		else if (winningPlayer == 2) SetsWonPlayer2++;
	}

	public bool CheckMatchWinner(out int matchWinner)
	{
		matchWinner = 0;
		if (SetsWonPlayer1 >= SetsToWinMatch)
		{
			matchWinner = 1;
			IsGameOver = true;
			return true;
		}
		if (SetsWonPlayer2 >= SetsToWinMatch)
		{
			matchWinner = 2;
			IsGameOver = true;
			return true;
		}
		return false;
	}	
}
