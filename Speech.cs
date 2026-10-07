/// Sentences about a player that read right when that player is "You" (playtest, 2026-10-06:
/// "am I Player 1?"). In single player your name IS "You", so "You plays Copy" and "You wins the
/// set!" have to become "You play Copy" and "You win the set!". Local 2-player keeps "Player 1" and
/// "Player 2", and those read as before.
public static class Speech
{
    public const string You = "You";

    public static bool IsYou(string name) => name == You;

    /// "You play" / "Gold Champion plays".
    public static string Does(string name, string verbS, string verbBase) =>
        IsYou(name) ? $"{You} {verbBase}" : $"{name} {verbS}";

    /// "You are" / "Gold Champion is".
    public static string Is(string name) => IsYou(name) ? $"{You} are" : $"{name} is";

    /// "your" / "Gold Champion's".
    public static string Possessive(string name) => IsYou(name) ? "your" : $"{name}'s";

    /// "you" mid-sentence / the name.
    public static string Object(string name) => IsYou(name) ? "you" : name;

    /// How every tutorial line is shown (playtest, 2026-10-07: a full stop "sounds too dry/formal").
    /// Each sentence gets a line of its own, and no line ends in a full stop - "!" and "?" stay.
    /// Line breaks already in the text are folded first, so a sentence wrapped by hand for a
    /// narrow card is not split into two lines here.
    ///   "It may have two sentences. In that case, rewrite them like this."
    ///   -> "It may have two sentences\nIn that case, rewrite them like this"
    public static string Casual(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
        string flat = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        string[] sentences = System.Text.RegularExpressions.Regex.Split(flat, @"(?<=[.!?])\s+");
        for (int i = 0; i < sentences.Length; i++)
            sentences[i] = sentences[i].TrimEnd('.');
        return string.Join("\n", sentences);
    }
}
