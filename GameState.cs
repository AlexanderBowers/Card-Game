using System;

public class GameState
{
	/// How many sets a player must win to take the match. Best of five (Alexander, 2026-09-12 -
	/// raised from two, for every mode: ladder, vs-bot and local 2-player alike).
	///
	/// GameManager's win-chip row draws one slot per win needed and reads this constant, so the
	/// two can no longer drift - they used to be two separate literals held together by a comment.
	public const int SetsToWinMatch = 3;

	public int TargetScore { get; set; } = 20;

	/// The hidden-card rule (2026-10-09, after Chuck's playtest: "I need more tension"): the bot's
	/// first two cards each set are face up, everything after them - its draws and the Modifiers it
	/// plays - lands face down, and its hand is face down all match. Set per match from the rung
	/// (LadderStep.HidesOpponentCards). Only ever on against the bot: a phone shared across a table
	/// cannot hide anything, and online has its own rules.
	public bool HiddenOpponent { get; set; }

	/// How many of the bot's cards each set land face up before the rest go face down: two, or
	/// one on the stage 20 and 50 bosses.
	public int FaceUpCards { get; set; } = 2;

	// The tier rules (stages 11-50, 2026-10-09; LadderStep has the full story). Set per match from
	// the rung, and left at their defaults everywhere else - local 2-player, Endless, online.

	/// What both main decks hold this set. Changes between sets when DeckChangesEachSet.
	public DeckShape Deck { get; set; } = DeckShape.Standard;
	public bool DeckChangesEachSet { get; set; }

	/// A played Modifier is replaced from the rest of its owner's deck (Player.RefillPile).
	public bool RefillHands { get; set; }

	/// A new target every set after the first (Table.StartSet).
	public bool TargetMovesEachSet { get; set; }
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
