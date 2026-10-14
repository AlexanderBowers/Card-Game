using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CriticalCount.Server;

/// Everything the server keeps on disk, in one SQLite file.
///
/// WHAT IS STORED, and this list is the privacy policy's list - change one, change the other:
///   accounts : a random id, a HASH of the phone's random secret, the display name and its #tag,
///              the friend code, the best endless streak, and when the account was made.
///   friends  : who is friends with whom, and pending requests.
///   blocks   : who blocked whom.
///   reports  : who reported whose name, and the one-word reason picked from a list.
/// What is NOT stored, anywhere: IP addresses, device ids, emails, real names, locations,
/// match histories or anything typed other than the display name.
public sealed class Store
{
    private readonly string _connectionString;

    public Store(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        Migrate();
    }

    /// An in-memory database for the tests. The keep-alive connection holds it open.
    public static Store InMemory(out SqliteConnection keepAlive)
    {
        string name = "file:cc_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared";
        keepAlive = new SqliteConnection("Data Source=" + name);
        keepAlive.Open();
        return new Store(name, raw: true);
    }

    private Store(string connectionString, bool raw)
    {
        _connectionString = "Data Source=" + connectionString;
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, @"
CREATE TABLE IF NOT EXISTS accounts (
    id            TEXT PRIMARY KEY,
    secret_hash   BLOB NOT NULL,
    name          TEXT NOT NULL,
    name_lc       TEXT NOT NULL,
    tag           INTEGER NOT NULL,
    friend_code   TEXT NOT NULL UNIQUE,
    endless_best  INTEGER NOT NULL DEFAULT 0,
    endless_at    INTEGER NOT NULL DEFAULT 0,
    created_at    INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_accounts_name_tag ON accounts(name_lc, tag);
CREATE INDEX IF NOT EXISTS ix_accounts_endless ON accounts(endless_best DESC, endless_at ASC);
CREATE TABLE IF NOT EXISTS friends (
    a       TEXT NOT NULL,  -- for 'pending', a sent the request to b
    b       TEXT NOT NULL,
    status  TEXT NOT NULL,  -- 'pending' | 'accepted'
    PRIMARY KEY (a, b)
);
CREATE INDEX IF NOT EXISTS ix_friends_b ON friends(b);
CREATE TABLE IF NOT EXISTS blocks (
    blocker TEXT NOT NULL,
    blocked TEXT NOT NULL,
    PRIMARY KEY (blocker, blocked)
);
CREATE TABLE IF NOT EXISTS reports (
    reporter TEXT NOT NULL,
    reported TEXT NOT NULL,
    reason   TEXT NOT NULL,
    at       INTEGER NOT NULL,
    PRIMARY KEY (reporter, reported)
);");
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        object o = cmd.ExecuteScalar();
        if (o == null || o is DBNull) return default;
        return (T)Convert.ChangeType(o, typeof(T));
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ------------------------------------------------------------------
    // Accounts
    // ------------------------------------------------------------------

    public const string DefaultName = "Player";

    public sealed record Profile(string Id, string Name, int Tag, string FriendCode, int EndlessBest)
    {
        public string Display => $"{Name}#{Tag:D4}";
    }

    /// A new anonymous account. Returns the id and the secret; the secret is shown to the phone
    /// once and only its hash is kept.
    public (string id, string secret) CreateAccount()
    {
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        string secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var c = Open();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Exec(c, @"INSERT INTO accounts (id, secret_hash, name, name_lc, tag, friend_code, created_at)
                          VALUES ($id, $h, $n, $nl, $t, $fc, $now)",
                    ("$id", id), ("$h", Hash(secret)), ("$n", DefaultName), ("$nl", DefaultName.ToLowerInvariant()),
                    ("$t", RandomTag()), ("$fc", NewFriendCode()), ("$now", Now));
                return (id, secret);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19 && attempt < 20) { } // a tag or code collided: roll again
        }
    }

    public bool VerifySecret(string id, string secret)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret) || id.Length > 64 || secret.Length > 128) return false;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT secret_hash FROM accounts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteScalar() is not byte[] stored) return false;
        return CryptographicOperations.FixedTimeEquals(stored, Hash(secret));
    }

    public Profile GetProfile(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, tag, friend_code, endless_best FROM accounts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new Profile(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4)) : null;
    }

    public string FindByFriendCode(string code)
    {
        string normalized = NormalizeFriendCode(code);
        if (normalized == null) return null;
        using var c = Open();
        return Scalar<string>(c, "SELECT id FROM accounts WHERE friend_code = $c", ("$c", normalized));
    }

    /// Sets the display name. The name must already have passed NameFilter. The #tag is kept when
    /// it is free under the new name, and re-rolled when it is not.
    public Profile SetName(string id, string name)
    {
        using var c = Open();
        int tag = Scalar<int>(c, "SELECT tag FROM accounts WHERE id = $id", ("$id", id));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Exec(c, "UPDATE accounts SET name = $n, name_lc = $nl, tag = $t WHERE id = $id",
                    ("$n", name), ("$nl", name.ToLowerInvariant()), ("$t", tag), ("$id", id));
                return GetProfile(id);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19 && attempt < 50)
            {
                tag = RandomTag();
            }
        }
    }

    /// Deletes the account and every row that mentions it. Irreversible by design.
    public void DeleteAccount(string id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM friends WHERE a = $id OR b = $id", ("$id", id));
        Exec(c, "DELETE FROM blocks WHERE blocker = $id OR blocked = $id", ("$id", id));
        Exec(c, "DELETE FROM reports WHERE reporter = $id OR reported = $id", ("$id", id));
        Exec(c, "DELETE FROM accounts WHERE id = $id", ("$id", id));
        tx.Commit();
    }

    /// Up to `limit` players whose name starts with `prefix`, best match first. Leaves out the
    /// searcher, anyone either side has blocked, and unnamed accounts (still "Player").
    public List<Profile> SearchByName(string searcher, string prefix, int limit = 10)
    {
        var results = new List<Profile>();
        string p = NameFilter.Clean(prefix).ToLowerInvariant();
        int hash = p.IndexOf('#');
        int? tag = null;
        if (hash >= 0)
        {
            if (int.TryParse(p[(hash + 1)..], out int t)) tag = t;
            p = p[..hash].TrimEnd();
        }
        if (p.Length < 2) return results;
        // LIKE wildcards in what was typed are matched literally.
        string like = p.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, tag, friend_code, endless_best FROM accounts a
WHERE name_lc LIKE $like ESCAPE '\' AND id <> $me AND name_lc <> $default
  AND ($tag IS NULL OR tag = $tag)
  AND NOT EXISTS (SELECT 1 FROM blocks WHERE (blocker = $me AND blocked = a.id) OR (blocker = a.id AND blocked = $me))
ORDER BY (name_lc = $exact) DESC, length(name_lc), name_lc, tag
LIMIT $limit";
        cmd.Parameters.AddWithValue("$like", like);
        cmd.Parameters.AddWithValue("$me", searcher);
        cmd.Parameters.AddWithValue("$default", DefaultName.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$tag", (object)tag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exact", p);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(new Profile(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4)));
        return results;
    }

    // ------------------------------------------------------------------
    // Friends
    // ------------------------------------------------------------------

    public enum FriendResult { Requested, Accepted, AlreadyFriends, AlreadyRequested, Blocked, NotFound, Self, TooMany }

    public const int MaxFriends = 200;

    /// `from` asks `to` to be friends. If `to` had already asked `from`, this accepts instead.
    public FriendResult RequestFriend(string from, string to)
    {
        if (from == to) return FriendResult.Self;
        using var c = Open();
        if (Scalar<long>(c, "SELECT COUNT(*) FROM accounts WHERE id = $id", ("$id", to)) == 0) return FriendResult.NotFound;
        if (IsBlockedEitherWay(c, from, to)) return FriendResult.Blocked;

        string mine = Scalar<string>(c, "SELECT status FROM friends WHERE a = $a AND b = $b", ("$a", from), ("$b", to));
        string theirs = Scalar<string>(c, "SELECT status FROM friends WHERE a = $a AND b = $b", ("$a", to), ("$b", from));
        if (mine == "accepted" || theirs == "accepted") return FriendResult.AlreadyFriends;
        if (theirs == "pending")
        {
            Exec(c, "UPDATE friends SET status = 'accepted' WHERE a = $a AND b = $b", ("$a", to), ("$b", from));
            return FriendResult.Accepted;
        }
        if (mine == "pending") return FriendResult.AlreadyRequested;
        if (Scalar<long>(c, "SELECT COUNT(*) FROM friends WHERE a = $id OR b = $id", ("$id", from)) >= MaxFriends)
            return FriendResult.TooMany;

        Exec(c, "INSERT INTO friends (a, b, status) VALUES ($a, $b, 'pending')", ("$a", from), ("$b", to));
        return FriendResult.Requested;
    }

    /// `me` accepts a request `other` sent. False if there was no such request.
    public bool AcceptFriend(string me, string other)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE friends SET status = 'accepted' WHERE a = $other AND b = $me AND status = 'pending'";
        cmd.Parameters.AddWithValue("$other", other);
        cmd.Parameters.AddWithValue("$me", me);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// Removes a friendship, declines their request, or cancels ours - whichever exists.
    public void RemoveFriend(string me, string other)
    {
        using var c = Open();
        Exec(c, "DELETE FROM friends WHERE (a = $me AND b = $o) OR (a = $o AND b = $me)", ("$me", me), ("$o", other));
    }

    public bool AreFriends(string x, string y)
    {
        using var c = Open();
        return Scalar<long>(c, @"SELECT COUNT(*) FROM friends
            WHERE status = 'accepted' AND ((a = $x AND b = $y) OR (a = $y AND b = $x))", ("$x", x), ("$y", y)) > 0;
    }

    public sealed record FriendLists(List<Profile> Friends, List<Profile> Incoming, List<Profile> Outgoing);

    public FriendLists ListFriends(string me)
    {
        var friends = new List<Profile>();
        var incoming = new List<Profile>();
        var outgoing = new List<Profile>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT f.a, f.b, f.status, p.id, p.name, p.tag, p.friend_code, p.endless_best
FROM friends f JOIN accounts p ON p.id = CASE WHEN f.a = $me THEN f.b ELSE f.a END
WHERE f.a = $me OR f.b = $me
ORDER BY p.name_lc, p.tag";
        cmd.Parameters.AddWithValue("$me", me);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // Friend codes are the OWNER's to hand out; a list of other people never carries them.
            var profile = new Profile(r.GetString(3), r.GetString(4), r.GetInt32(5), string.Empty, r.GetInt32(7));
            if (r.GetString(2) == "accepted") friends.Add(profile);
            else if (r.GetString(0) == me) outgoing.Add(profile);
            else incoming.Add(profile);
        }
        return new FriendLists(friends, incoming, outgoing);
    }

    public List<string> FriendIds(string me)
    {
        var ids = new List<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT CASE WHEN a = $me THEN b ELSE a END FROM friends
                            WHERE status = 'accepted' AND (a = $me OR b = $me)";
        cmd.Parameters.AddWithValue("$me", me);
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    // ------------------------------------------------------------------
    // Blocks and reports
    // ------------------------------------------------------------------

    /// Blocking also ends any friendship or request between the two, both ways.
    public void Block(string me, string other)
    {
        if (me == other) return;
        using var c = Open();
        Exec(c, "INSERT OR IGNORE INTO blocks (blocker, blocked) VALUES ($me, $o)", ("$me", me), ("$o", other));
        Exec(c, "DELETE FROM friends WHERE (a = $me AND b = $o) OR (a = $o AND b = $me)", ("$me", me), ("$o", other));
    }

    public void Unblock(string me, string other)
    {
        using var c = Open();
        Exec(c, "DELETE FROM blocks WHERE blocker = $me AND blocked = $o", ("$me", me), ("$o", other));
    }

    public List<Profile> ListBlocked(string me)
    {
        var list = new List<Profile>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT p.id, p.name, p.tag, p.endless_best FROM blocks b JOIN accounts p ON p.id = b.blocked
                            WHERE b.blocker = $me ORDER BY p.name_lc";
        cmd.Parameters.AddWithValue("$me", me);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new Profile(r.GetString(0), r.GetString(1), r.GetInt32(2), string.Empty, r.GetInt32(3)));
        return list;
    }

    public bool IsBlockedEitherWay(string x, string y)
    {
        using var c = Open();
        return IsBlockedEitherWay(c, x, y);
    }

    private static bool IsBlockedEitherWay(SqliteConnection c, string x, string y) =>
        Scalar<long>(c, @"SELECT COUNT(*) FROM blocks
            WHERE (blocker = $x AND blocked = $y) OR (blocker = $y AND blocked = $x)", ("$x", x), ("$y", y)) > 0;

    public static readonly string[] ReportReasons = { "name", "cheating", "other" };

    /// Distinct players who must report a NAME before it is reset to "Player" automatically.
    public const int NameReportsToReset = 3;

    /// Records a report (one per reporter per player; a second just updates the reason). When
    /// enough different players have reported someone's name, the name goes back to the default -
    /// the person keeps their account, friends and #tag, and can pick a new name that passes.
    /// Returns true when this report caused that reset.
    public bool Report(string reporter, string reported, string reason)
    {
        if (reporter == reported || Array.IndexOf(ReportReasons, reason) < 0) return false;
        using var c = Open();
        if (Scalar<long>(c, "SELECT COUNT(*) FROM accounts WHERE id = $id", ("$id", reported)) == 0) return false;
        Exec(c, "INSERT OR REPLACE INTO reports (reporter, reported, reason, at) VALUES ($r, $d, $why, $now)",
            ("$r", reporter), ("$d", reported), ("$why", reason), ("$now", Now));

        if (reason != "name") return false;
        long count = Scalar<long>(c, "SELECT COUNT(*) FROM reports WHERE reported = $d AND reason = 'name'", ("$d", reported));
        if (count < NameReportsToReset) return false;

        string current = Scalar<string>(c, "SELECT name FROM accounts WHERE id = $d", ("$d", reported));
        if (current == DefaultName) return false;
        SetName(reported, DefaultName);
        // The reports were about the OLD name; the new one starts clean.
        Exec(c, "DELETE FROM reports WHERE reported = $d AND reason = 'name'", ("$d", reported));
        return true;
    }

    // ------------------------------------------------------------------
    // Endless leaderboard
    // ------------------------------------------------------------------

    /// The furthest anyone could plausibly get. Anything above is refused rather than stored.
    public const int MaxPlausibleStreak = 500;

    /// Keeps the best streak. Returns the (possibly unchanged) best.
    public int SubmitEndless(string id, int streak)
    {
        if (streak < 0 || streak > MaxPlausibleStreak) return GetProfile(id)?.EndlessBest ?? 0;
        using var c = Open();
        Exec(c, "UPDATE accounts SET endless_best = $s, endless_at = $now WHERE id = $id AND endless_best < $s",
            ("$s", streak), ("$now", Now), ("$id", id));
        return Scalar<int>(c, "SELECT endless_best FROM accounts WHERE id = $id", ("$id", id));
    }

    public sealed record LeaderRow(int Rank, string Name, int Tag, int Streak, bool You);

    public List<LeaderRow> TopEndless(string me, int limit = 50)
    {
        var rows = new List<LeaderRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT id, name, tag, endless_best FROM accounts WHERE endless_best > 0
                            ORDER BY endless_best DESC, endless_at ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        int rank = 0;
        while (r.Read()) rows.Add(new LeaderRow(++rank, r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(0) == me));
        return rows;
    }

    /// This player's own rank (1-based), or 0 with no streak on the board.
    public int RankOf(string me)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT endless_best, endless_at FROM accounts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", me);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.GetInt32(0) <= 0) return 0;
        int best = r.GetInt32(0);
        long at = r.GetInt64(1);
        return 1 + (int)Scalar<long>(c, @"SELECT COUNT(*) FROM accounts
            WHERE endless_best > $b OR (endless_best = $b AND endless_at < $at)", ("$b", best), ("$at", at));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static byte[] Hash(string secret) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

    private static int RandomTag() => RandomNumberGenerator.GetInt32(1, 10000);

    // Crockford-style: no I, L, O or U, so a code read aloud or off a screen is never ambiguous.
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static string NewFriendCode()
    {
        Span<char> chars = stackalloc char[8];
        for (int i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }

    /// "kx7q-42pm", "KX7Q42PM", "kx7q 42pm" -> "KX7Q42PM"; also reads O as 0 and I/L as 1, the
    /// way Crockford intends. Null when it cannot be a code.
    public static string NormalizeFriendCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var sb = new System.Text.StringBuilder(8);
        foreach (char raw in code.ToUpperInvariant())
        {
            if (raw == '-' || raw == ' ') continue;
            char ch = raw switch { 'O' => '0', 'I' => '1', 'L' => '1', _ => raw };
            if (CodeAlphabet.IndexOf(ch) < 0) return null;
            sb.Append(ch);
        }
        return sb.Length == 8 ? sb.ToString() : null;
    }

    /// The code as shown to people: KX7Q-42PM.
    public static string FormatFriendCode(string code) =>
        string.IsNullOrEmpty(code) || code.Length != 8 ? code : code[..4] + "-" + code[4..];

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
