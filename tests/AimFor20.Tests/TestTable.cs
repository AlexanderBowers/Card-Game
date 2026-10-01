using System.Collections.Generic;

/// Small builders so each test reads as the table it sets up.
internal static class TestTable
{
    public const int Target = 20;

    public static Card MainCard(int value) => new Card(value, CardType.Main);

    public static Card Mod(int value, bool flip = false) => new Card(value, CardType.Modifier, "", flip);

    public static Card Effect(CardEffect effect) => CardEffects.Create(effect, new System.Random(1));

    public static Card Rescue(int value)
    {
        Card card = Mod(value);
        card.IsRescue = true;
        return card;
    }

    public static Player Player(string name, int score = 0, bool holding = false, params Card[] hand)
    {
        Player p = new Player(name) { CurrentScore = score, IsHolding = holding };
        p.Modifiers.AddRange(hand);
        return p;
    }

    /// Puts a main-deck card on the player's board the way a draw does.
    public static Card Draw(Player p, int value)
    {
        Card card = MainCard(value);
        p.ActiveCardsOnBoard.Add(card);
        p.LastDrawnCard = card;
        p.CurrentScore += value;
        return card;
    }

    public static List<int> Values(IEnumerable<Card> cards)
    {
        List<int> values = new List<int>();
        foreach (Card c in cards) values.Add(c.Value);
        return values;
    }
}
