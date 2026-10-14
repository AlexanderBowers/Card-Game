namespace CriticalCount.Server;

/// The side deck a phone brings to an online match (2026-10-08: "your own deck, but verify
/// they're legitimate cards").
///
/// The phone sends NAMES, never numbers: twelve collection keys like "+3", "flip2" or "Veto".
/// Every one must be a card in the game's own catalogue (CollectionLog.Keys - every card the
/// Market can sell or the starter deck holds), and the server builds each card itself from that
/// name. So a phone cannot invent a card: there is no key for "set my score to the target", and
/// a value, a sign or an effect the phone sent is never read.
///
/// What it cannot check: that the player OWNS those cards. The collection lives on the phone and
/// nowhere else, so a modified phone could bring twelve real cards it never bought.
public static class OnlineDeck
{
    private static readonly HashSet<string> Known = new(CollectionLog.Keys);

    /// Null when the deck is fine (or absent - that means a random hand), else the error code.
    public static string Validate(string[] deck)
    {
        if (deck == null) return null;
        if (deck.Length != RunData.SideDeckSize) return "bad_deck";
        foreach (string key in deck)
        {
            if (key == null || !Known.Contains(key)) return "bad_deck";
            ModifierDef def = CollectionLog.Entry(key);
            if (def.Effect != CardEffect.None && !CardEffects.Implemented(def.Effect)) return "bad_deck";
        }
        return null;
    }

    /// A match hand: RunData.MatchModifierCount cards drawn at random from the deck, the way a
    /// solo match draws from the deck screen's twelve.
    public static List<Card> DrawHand(IReadOnlyList<string> deck, Random rng)
    {
        List<string> pool = new(deck);
        List<Card> hand = new();
        for (int i = 0; i < RunData.MatchModifierCount && pool.Count > 0; i++)
        {
            int pick = rng.Next(pool.Count);
            hand.Add(ToCard(pool[pick], rng));
            pool.RemoveAt(pick);
        }
        return hand;
    }

    /// The card a key names, built here - the same way the Market builds it.
    public static Card ToCard(string key, Random rng)
    {
        ModifierDef def = CollectionLog.Entry(key);
        return def.Effect != CardEffect.None ? CardEffects.Create(def.Effect, rng) : def.ToCard();
    }
}
