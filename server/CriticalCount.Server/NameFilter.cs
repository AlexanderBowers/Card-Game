using System.Reflection;
using System.Text;

namespace CriticalCount.Server;

/// Decides whether a display name is allowed. Names are the only thing players write that other
/// players read (there is no chat), so this is the whole of the moderation surface - with
/// Block and Report behind it for whatever gets past.
///
/// Three layers, cheapest first:
///   1. Shape: 3-16 characters, plain ASCII letters/digits and a few separators, at least two
///      letters. ASCII-only is deliberate - it shuts out look-alike letters from other alphabets,
///      which is the oldest way round a word list.
///   2. Reserved: nobody gets to look like staff, the studio or the game.
///   3. The LDNOOBW list (Data/blocked_words_en.txt, CC BY 4.0), checked after undoing the usual
///      disguises: case, leetspeak digits (0=o, 1=i/l, 3=e, 4=a, 5=s, 7=t, 8=b, 9=g), separators
///      ("f.u_c-k") and stretched letters ("fuuuck").
///
/// Long entries (4+ letters) are matched ANYWHERE in the squashed name, so they cannot be hidden
/// inside other text. Short ones (3 letters or fewer) only match a whole word, because as
/// substrings they are inside far too many ordinary names (the Scunthorpe problem). The cost of
/// the substring rule is the occasional innocent name (a "Hancock") being refused; a refused
/// player picks another name, which is a much smaller harm than the alternative in a 13+ game.
public sealed class NameFilter
{
    public const int MinLength = 3;
    public const int MaxLength = 16;

    private static readonly string[] ReservedAnywhere =
    {
        "admin", "moderator", "cloudyday", "criticalcount", "official", "gamemaster", "developer",
    };

    private static readonly string[] ReservedWords = { "mod", "mods", "dev", "devs", "gm", "staff" };

    private readonly List<string> _long = new();      // squashed, 4+ letters: substring match
    private readonly HashSet<string> _short = new();  // squashed, <= 3 letters: whole-word match
    private readonly HashSet<string> _shortDedup = new(); // the same, stretched letters collapsed

    public static NameFilter Default { get; } = new NameFilter(LoadEmbeddedList());

    public NameFilter(IEnumerable<string> blockedEntries)
    {
        foreach (string raw in blockedEntries)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // List entries are already plain words; the same squash makes "two words" one string.
            string squashed = Squash(line.ToLowerInvariant(), oneIsL: false);
            if (squashed.Length == 0) continue;
            if (squashed.Length >= 4) _long.Add(squashed);
            else AddShort(squashed);
        }
        foreach (string word in ReservedWords) AddShort(word);
    }

    private void AddShort(string word)
    {
        _short.Add(word);
        _shortDedup.Add(Dedup(word));
    }

    public enum Verdict { Ok, TooShort, TooLong, BadCharacters, NeedsLetters, Rejected }

    /// The name as it will be stored: trimmed, inner runs of spaces collapsed to one.
    public static string Clean(string name)
    {
        if (name == null) return string.Empty;
        var sb = new StringBuilder(name.Length);
        bool space = false;
        foreach (char c in name.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
                continue;
            }
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public Verdict Check(string name)
    {
        string cleaned = Clean(name);
        if (cleaned.Length < MinLength) return Verdict.TooShort;
        if (cleaned.Length > MaxLength) return Verdict.TooLong;

        int letters = 0;
        foreach (char c in cleaned)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                      || c == ' ' || c == '_' || c == '-' || c == '.';
            if (!ok) return Verdict.BadCharacters;
            if (char.IsLetter(c)) letters++;
        }
        if (letters < 2) return Verdict.NeedsLetters;

        return IsOffensive(cleaned) ? Verdict.Rejected : Verdict.Ok;
    }

    public bool IsAllowed(string name) => Check(name) == Verdict.Ok;

    /// True when the name contains a blocked or reserved word in any of its disguises. A "1" can
    /// stand for an i or an l, so both readings are tried.
    public bool IsOffensive(string name)
    {
        string lower = name.ToLowerInvariant();
        return Hits(lower, oneIsL: false) || Hits(lower, oneIsL: true);
    }

    private bool Hits(string lower, bool oneIsL)
    {
        string squashed = Squash(lower, oneIsL);
        string stretched = Dedup(squashed);

        foreach (string word in ReservedAnywhere)
            if (squashed.Contains(word) || stretched.Contains(Dedup(word))) return true;

        // The stretched-letter reading only for words that are still long once their own doubled
        // letters collapse: a four-letter entry with a double letter shrinks to three, and three
        // letters match inside ordinary names ("Opponent" was refused, 2026-10-08).
        foreach (string word in _long)
        {
            if (squashed.Contains(word)) return true;
            string collapsed = Dedup(word);
            if (collapsed.Length >= 4 && stretched.Contains(collapsed)) return true;
        }

        // Whole words: split on the separators BEFORE squashing them away.
        foreach (string token in lower.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = Squash(token, oneIsL);
            if (t.Length == 0) continue;
            if (_short.Contains(t) || _shortDedup.Contains(Dedup(t))) return true;
        }

        // The whole name with its separators gone, as one word: catches "f.a.g" spelled out.
        return _short.Contains(squashed) || _shortDedup.Contains(stretched);
    }

    /// Lower-case letters only, with leetspeak digits read back as the letters they stand for.
    internal static string Squash(string lower, bool oneIsL)
    {
        var sb = new StringBuilder(lower.Length);
        foreach (char c in lower)
        {
            char mapped = c switch
            {
                '0' => 'o',
                '1' => oneIsL ? 'l' : 'i',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '8' => 'b',
                '9' => 'g',
                '@' => 'a',
                '$' => 's',
                '!' => 'i',
                _ => c,
            };
            if (mapped >= 'a' && mapped <= 'z') sb.Append(mapped);
        }
        return sb.ToString();
    }

    /// "fuuuuck" -> "fuck": runs of one letter collapse to a single letter.
    internal static string Dedup(string s)
    {
        if (s.Length < 2) return s;
        var sb = new StringBuilder(s.Length);
        char prev = '\0';
        foreach (char c in s)
        {
            if (c != prev) sb.Append(c);
            prev = c;
        }
        return sb.ToString();
    }

    private static IEnumerable<string> LoadEmbeddedList()
    {
        Assembly asm = typeof(NameFilter).Assembly;
        using Stream stream = asm.GetManifestResourceStream("blocked_words_en.txt")
            ?? throw new InvalidOperationException("blocked_words_en.txt is not embedded in the server assembly");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        string line;
        while ((line = reader.ReadLine()) != null) lines.Add(line);
        return lines;
    }
}
