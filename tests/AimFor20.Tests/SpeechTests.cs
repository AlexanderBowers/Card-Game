using Xunit;

/// How tutorial lines are shown: one sentence per line, no full stop at the end of any of them.
public class SpeechTests
{
    [Theory]
    [InlineData("It may have two sentences. In that case, rewrite them like this.",
                "It may have two sentences\nIn that case, rewrite them like this")]
    [InlineData("Press Flip Value to turn it into -1.", "Press Flip Value to turn it into -1")]
    [InlineData("You won the Set.\nWin three Sets to win the Match.", "You won the Set\nWin three Sets to win the Match")]
    [InlineData("Take 1 off an opponent who is holding\nbelow the target. They cannot answer.",
                "Take 1 off an opponent who is holding below the target\nThey cannot answer")]
    [InlineData("You won the set! Well done", "You won the set!\nWell done")]
    [InlineData("Let's add it to your deck", "Let's add it to your deck")]
    public void Tutorial_lines_drop_full_stops_and_break_between_sentences(string text, string shown)
    {
        Assert.Equal(shown, Speech.Casual(text));
    }
}
