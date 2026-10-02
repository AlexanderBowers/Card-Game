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
}
