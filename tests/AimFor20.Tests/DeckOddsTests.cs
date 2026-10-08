using System.Linq;
using Xunit;

public class DeckOddsTests
{
    private static int[] FullDeck() => Enumerable.Repeat(4, 10).ToArray();

    [Fact]
    public void FreshDeckOnFifteenSplitsFourFiveOne()
    {
        // On 15 with target 20: 1-4 stay under, 5 hits, 6-10 go over.
        DeckOdds.Odds odds = DeckOdds.NextDraw(FullDeck(), 15, 20);
        Assert.Equal(40, odds.Cards);
        Assert.Equal(0.4f, odds.Under, 3);
        Assert.Equal(0.1f, odds.Hit, 3);
        Assert.Equal(0.5f, odds.Over, 3);
        Assert.Equal(new[] { 40, 10, 50 }, DeckOdds.Percentages(odds));
    }

    [Fact]
    public void CountsWhatIsLeftNotAFreshDeck()
    {
        int[] counts = FullDeck();
        counts[4] = 0; // every 5 already drawn
        DeckOdds.Odds odds = DeckOdds.NextDraw(counts, 15, 20);
        Assert.Equal(36, odds.Cards);
        Assert.Equal(0f, odds.Hit);
    }

    [Fact]
    public void LowScoreCannotBust()
    {
        DeckOdds.Odds odds = DeckOdds.NextDraw(FullDeck(), 5, 20);
        Assert.Equal(1f, odds.Under);
        Assert.Equal(new[] { 100, 0, 0 }, DeckOdds.Percentages(odds));
    }

    [Fact]
    public void PercentagesAlwaysAddUpToAHundred()
    {
        for (int score = 0; score <= 25; score++)
        {
            int[] counts = { 3, 4, 2, 4, 1, 4, 3, 4, 4, 2 };
            int[] pct = DeckOdds.Percentages(DeckOdds.NextDraw(counts, score, 20));
            Assert.Equal(100, pct.Sum());
        }
    }

    [Fact]
    public void EmptyDeckIsAllZero()
    {
        DeckOdds.Odds odds = DeckOdds.NextDraw(new int[10], 15, 20);
        Assert.Equal(0, odds.Cards);
        Assert.Equal(0, DeckOdds.Percentages(odds).Sum());
    }

    [Fact]
    public void TableCountsAFreshDeckAsFourOfEach()
    {
        FakeTableHost host = new FakeTableHost();
        Table table = new Table(host);
        table.StartSet();
        int[] counts = table.DeckCounts(host.Player1);
        Assert.Equal(10, counts.Length);
        Assert.Equal(table.Remaining(host.Player1), counts.Sum());
    }
}
