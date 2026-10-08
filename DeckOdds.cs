/// The odds behind "tap your deck" (2026-10-08): what the next card off your own deck does to your
/// score. Plain arithmetic on what is left in the deck - it is what a player counting the board
/// would work out, done for them.
///
/// It reads the deck's make-up, not its order. The Bronze rung's helping hand (Table.SteerTopCard)
/// can reorder the top card, so on that rung the real chance of a bust is a little lower than this
/// says - the honest direction to be wrong in.
public static class DeckOdds
{
    public readonly struct Odds
    {
        public readonly int Cards;
        public readonly float Under;
        public readonly float Hit;
        public readonly float Over;

        public Odds(int cards, float under, float hit, float over)
        {
            Cards = cards;
            Under = under;
            Hit = hit;
            Over = over;
        }
    }

    /// `counts[v - 1]` copies of each value v (Table.DeckCounts).
    public static Odds NextDraw(int[] counts, int score, int target)
    {
        int total = 0, under = 0, hit = 0, over = 0;
        for (int value = 1; value <= 10 && value <= counts.Length; value++)
        {
            int n = counts[value - 1];
            total += n;
            int after = score + value;
            if (after < target) under += n;
            else if (after == target) hit += n;
            else over += n;
        }
        if (total == 0) return new Odds(0, 0f, 0f, 0f);
        return new Odds(total, under / (float)total, hit / (float)total, over / (float)total);
    }

    /// Whole percentages that add up to exactly 100 (largest remainder), so the three lines never
    /// read 33 + 33 + 33.
    public static int[] Percentages(Odds odds)
    {
        float[] raw = { odds.Under * 100f, odds.Hit * 100f, odds.Over * 100f };
        int[] whole = new int[3];
        int sum = 0;
        for (int i = 0; i < 3; i++) { whole[i] = (int)raw[i]; sum += whole[i]; }
        if (odds.Cards == 0) return whole;
        while (sum < 100)
        {
            int best = 0;
            for (int i = 1; i < 3; i++)
                if (raw[i] - whole[i] > raw[best] - whole[best]) best = i;
            whole[best]++;
            raw[best] = whole[best]; // its remainder is used up
            sum++;
        }
        return whole;
    }
}
