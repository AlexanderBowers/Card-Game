using Godot;

/// What a ladder rank looks like: the table felt and how the standard (main deck) cards are tinted
/// while you are in it. The rank NAMES and the ladder itself are plain data in Ladder.cs; only the
/// colours need Godot, so only they live here.
public readonly struct RankTheme
{
    public readonly string Name;
    public readonly Color Table;      // the felt behind everything
    public readonly Color CardTint;   // multiplied into the standard card art

    private RankTheme(string name, Color table, Color cardTint)
    {
        Name = name;
        Table = table;
        CardTint = cardTint;
    }

    // Same order as Ladder.RankNames.
    private static readonly RankTheme[] Themes =
    {
        new RankTheme(Ladder.RankNames[0], new Color(0.07f, 0.24f, 0.13f), new Color(1.00f, 1.00f, 1.00f)),
        new RankTheme(Ladder.RankNames[1], new Color(0.10f, 0.20f, 0.26f), new Color(0.86f, 1.00f, 1.12f)),
        new RankTheme(Ladder.RankNames[2], new Color(0.18f, 0.16f, 0.06f), new Color(1.28f, 1.08f, 0.55f)),
        new RankTheme(Ladder.RankNames[3], new Color(0.22f, 0.07f, 0.10f), new Color(1.30f, 0.74f, 0.74f)),
        new RankTheme(Ladder.RankNames[4], new Color(0.10f, 0.07f, 0.16f), new Color(0.86f, 0.72f, 1.20f)),
    };

    public static RankTheme For(int rank) => Themes[Mathf.Clamp(rank, 0, Themes.Length - 1)];
}

/// The one Godot-flavoured member of RunData. Kept in its own file so the rest of RunData
/// compiles without Godot (the test project leaves this file out).
public partial class RunData
{
    public RankTheme CurrentRank => RankTheme.For(CurrentStep.Rank);
}
