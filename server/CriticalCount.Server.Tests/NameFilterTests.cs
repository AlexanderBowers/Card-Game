using CriticalCount.Server;
using Xunit;

public class NameFilterTests
{
    private static NameFilter F => NameFilter.Default;

    [Theory]
    [InlineData("Alex")]
    [InlineData("Cassandra")]      // contains a short blocked word, inside a name: allowed
    [InlineData("Grandma Jo")]
    [InlineData("Ace_of_20")]
    [InlineData("B.J. Lane")]
    [InlineData("Pazaak Fan 99")]
    [InlineData("Glass Bass")]
    [InlineData("Opponent")]       // a doubled-letter entry collapsed to three letters used to match inside it
    [InlineData("Test Opponent")]
    public void OrdinaryNamesPass(string name) => Assert.Equal(NameFilter.Verdict.Ok, F.Check(name));

    [Theory]
    [InlineData("fuck")]
    [InlineData("FuCk_you")]
    [InlineData("f.u.c.k")]
    [InlineData("fuuuuck")]
    [InlineData("phuck shit")]
    [InlineData("sh1t lord")]
    [InlineData("b1tch")]
    [InlineData("a55hole")]
    [InlineData("big ass")]
    [InlineData("a s s")]
    public void ProfanityIsRejected(string name) => Assert.Equal(NameFilter.Verdict.Rejected, F.Check(name));

    [Theory]
    [InlineData("Admin")]
    [InlineData("4dm1n_Bob")]
    [InlineData("Moderator")]
    [InlineData("CriticalCount")]
    [InlineData("Cloudy Day Games")]
    [InlineData("mod")]
    [InlineData("Dev Team")]
    public void ReservedNamesAreRejected(string name) => Assert.Equal(NameFilter.Verdict.Rejected, F.Check(name));

    [Theory]
    [InlineData("ab", NameFilter.Verdict.TooShort)]
    [InlineData("   a   ", NameFilter.Verdict.TooShort)]
    [InlineData("abcdefghijklmnopq", NameFilter.Verdict.TooLong)]
    [InlineData("Ålex", NameFilter.Verdict.BadCharacters)]   // non-ASCII look-alikes are shut out
    [InlineData("Аlex", NameFilter.Verdict.BadCharacters)]   // (that A is Cyrillic)
    [InlineData("al<b>", NameFilter.Verdict.BadCharacters)]
    [InlineData("12345", NameFilter.Verdict.NeedsLetters)]
    [InlineData(null, NameFilter.Verdict.TooShort)]
    public void ShapeRules(string name, NameFilter.Verdict expected) => Assert.Equal(expected, F.Check(name));

    [Fact]
    public void CleanCollapsesSpaces() => Assert.Equal("Grandma Jo", NameFilter.Clean("  Grandma    Jo "));

    [Fact]
    public void TheWholeListLoaded()
    {
        // Every entry in the list must reject itself, or the loader dropped something.
        string path = Path.Combine(AppContext.BaseDirectory, "blocked_words_en.txt");
        using Stream s = typeof(NameFilter).Assembly.GetManifestResourceStream("blocked_words_en.txt");
        using var reader = new StreamReader(s);
        string line;
        int checkedCount = 0;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (NameFilter.Squash(line.ToLowerInvariant(), false).Length == 0) continue;
            Assert.True(F.IsOffensive(line), $"list entry #{checkedCount} not caught");
            checkedCount++;
        }
        Assert.True(checkedCount > 300);
    }
}
