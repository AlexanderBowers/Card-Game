/// When a set ends and who takes it - the two rules GameManager applies at the end of every turn,
/// kept apart from the screens around them so the tests can hold them.
public static class SetRules
{
    /// Both players are done with the turn: the set is over if anyone finished it over the target
    /// (a bust - going over mid-turn is not one, a minus card can still save it) or both are holding.
    public static bool IsSetOver(Player p1, Player p2, int target)
    {
        bool anyBust = p1.CurrentScore > target || p2.CurrentScore > target;
        bool bothHolding = p1.IsHolding && p2.IsHolding;
        return anyBust || bothHolding;
    }

    /// 1 or 2 for the player who takes the set, or 0 for a tie (the set is replayed). A bust loses
    /// outright; two busts, or two equal scores, tie; otherwise the higher score is the closer one.
    public static int Winner(int p1, int p2, int target)
    {
        bool p1Bust = p1 > target;
        bool p2Bust = p2 > target;
        if (p1Bust && p2Bust) return 0;
        if (p1Bust) return 2;
        if (p2Bust) return 1;
        if (p1 == p2) return 0;
        return p1 > p2 ? 1 : 2;
    }

    /// What the set-end panel says about a set that has just ended.
    public readonly struct Outcome
    {
        public readonly int Winner;        // 1, 2, or 0 for a tie
        public readonly string Title;      // "<name> wins the set!" / "The set is a tie"
        public readonly string Why;        // why it ended, and what that means
        public readonly string ButtonText; // "Next Set" / "Replay Set"

        public Outcome(int winner, string title, string why, string buttonText)
        {
            Winner = winner;
            Title = title;
            Why = why;
            ButtonText = buttonText;
        }
    }

    /// The set-end explanation, worded from the two scores. A rule the player cannot see is a rule
    /// they cannot learn, so it always says WHY: who busted, or who held closer.
    public static Outcome Describe(string p1Name, int p1, string p2Name, int p2, int target)
    {
        bool p1Bust = p1 > target;
        bool p2Bust = p2 > target;
        int winner = Winner(p1, p2, target);

        string why;
        if (p1Bust && p2Bust)
            why = $"Both players busted: {p1} and {p2} are over the target of {target}.";
        else if (p1Bust)
            why = $"{p1Name} busted: {p1} is over the target of {target}.";
        else if (p2Bust)
            why = $"{p2Name} busted: {p2} is over the target of {target}.";
        else
            why = $"Both players held.\n{p1Name}: {p1}      {p2Name}: {p2}";

        if (winner == 0)
            return new Outcome(0, "The set is a tie", why + "\nSame score, so the set is replayed.", "Replay Set");

        string winnerName = winner == 1 ? p1Name : p2Name;
        if (!p1Bust && !p2Bust) why += $"\n{Speech.Is(winnerName)} closest to {target}.";
        return new Outcome(winner, $"{Speech.Does(winnerName, "wins", "win")} the set!", why, "Next Set");
    }
}
