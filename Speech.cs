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
}
