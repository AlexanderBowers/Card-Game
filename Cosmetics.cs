using System;

/// Decks and boards as fixed data (playtest, 2026-09-30): which exist, what they cost, and which
/// stage opens each one. What the player OWNS and has selected is profile state in RunData.
///
/// The player's deck (card back + the look of their 1-10 cards) and board (the table outside
/// the ladder) are theirs: chosen on the main menu and never changed by a stage. Each stage
/// plays on its own table, and the opponent uses the stage's deck. One of each per rank; Classic
/// is owned from the start, the rest are bought with medals in the Shop - and only once the
/// player has beaten a stage of that rank.
public static class Cosmetics
{
    /// Everyone starts with Classic (playtest, 2026-09-30: "I really don't like the Bronze set being
    /// the default"): the original mint deck on the navy-and-gold back, and a bright sky-blue board.
    public const string Default = "classic";

    public const string EndlessKey = "endless";

    /// Shop order. The five rank sets in ladder order, then Endless's own set.
    public static readonly string[] Keys = { "classic", "bronze", "silver", "gold", "ruby", "obsidian", "endless" };
    private static readonly string[] RankKeys = { "bronze", "silver", "gold", "ruby", "obsidian" };

    /// Medals to buy a deck or board. Classic is free and owned from the start.
    public static int Price(string key) => key switch
    {
        "bronze" => 10,
        "silver" => 15,
        "gold" => 25,
        "ruby" => 40,
        "obsidian" => 60,
        "endless" => 100,
        _ => 0,
    };

    public static bool IsCosmetic(string key) => Array.IndexOf(Keys, key) >= 0;

    /// The ladder rank whose set this is, or -1 for Classic, Endless and anything unknown.
    public static int RankOf(string key) => Array.IndexOf(RankKeys, key);

    /// The rung (0-based) whose win opens this rank's set: the rank's first stage, its Challenger.
    /// -1 for a set no stage opens.
    public static int UnlockStepIndex(string key)
    {
        int rank = RankOf(key);
        return rank < 0 ? -1 : Math.Min(rank * 2, Ladder.Length - 1);
    }

    /// The name of the stage that has to be beaten before this set can be bought. Null for
    /// Classic and Endless.
    public static string UnlockStageName(string key)
    {
        int step = UnlockStepIndex(key);
        return step < 0 ? null : Ladder.At(step).Opponent;
    }
}
