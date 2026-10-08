using CriticalCount.Server;
using Microsoft.Data.Sqlite;
using Xunit;

public class StoreTests : IDisposable
{
    private readonly SqliteConnection _keep;
    private readonly Store _store;

    public StoreTests() => _store = Store.InMemory(out _keep);
    public void Dispose() => _keep.Dispose();

    private string NewAccount(string name = null)
    {
        var (id, _) = _store.CreateAccount();
        if (name != null) _store.SetName(id, name);
        return id;
    }

    [Fact]
    public void SecretVerifiesAndOnlyItsHashIsKept()
    {
        var (id, secret) = _store.CreateAccount();
        Assert.True(_store.VerifySecret(id, secret));
        Assert.False(_store.VerifySecret(id, secret + "x"));
        Assert.False(_store.VerifySecret("nope", secret));

        using var cmd = _keep.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM accounts WHERE CAST(secret_hash AS TEXT) = $s";
        cmd.Parameters.AddWithValue("$s", secret);
        Assert.Equal(0L, (long)cmd.ExecuteScalar());
    }

    [Fact]
    public void NewAccountsAreUnnamedPlayersWithATagAndCode()
    {
        Store.Profile p = _store.GetProfile(NewAccount());
        Assert.Equal(Store.DefaultName, p.Name);
        Assert.InRange(p.Tag, 1, 9999);
        Assert.Equal(8, p.FriendCode.Length);
        Assert.Matches(@"^Player#\d{4}$", p.Display);
    }

    [Fact]
    public void SameNameGetsDifferentTags()
    {
        var tags = new HashSet<int>();
        for (int i = 0; i < 30; i++) tags.Add(_store.GetProfile(NewAccount("Alex")).Tag);
        Assert.Equal(30, tags.Count);
    }

    [Fact]
    public void FriendCodesAreForgivingToType()
    {
        string id = NewAccount("Alex");
        string code = _store.GetProfile(id).FriendCode;
        string typed = (code[..4] + "-" + code[4..]).ToLowerInvariant().Replace('0', 'o').Replace('1', 'l');
        Assert.Equal(id, _store.FindByFriendCode(typed));
        Assert.Null(_store.FindByFriendCode("short"));
    }

    [Fact]
    public void RequestThenAccept()
    {
        string a = NewAccount("Ann"), b = NewAccount("Ben");
        Assert.Equal(Store.FriendResult.Requested, _store.RequestFriend(a, b));
        Assert.Equal(Store.FriendResult.AlreadyRequested, _store.RequestFriend(a, b));

        var bLists = _store.ListFriends(b);
        Assert.Single(bLists.Incoming);
        Assert.Empty(bLists.Friends);
        Assert.Equal("", bLists.Incoming[0].FriendCode); // someone else's code never travels

        Assert.True(_store.AcceptFriend(b, a));
        Assert.True(_store.AreFriends(a, b));
        Assert.Equal(new[] { b }, _store.FriendIds(a));
    }

    [Fact]
    public void CrossedRequestsBecomeFriends()
    {
        string a = NewAccount("Ann"), b = NewAccount("Ben");
        _store.RequestFriend(a, b);
        Assert.Equal(Store.FriendResult.Accepted, _store.RequestFriend(b, a));
        Assert.True(_store.AreFriends(a, b));
    }

    [Fact]
    public void BlockingEndsFriendshipAndHidesBothWays()
    {
        string a = NewAccount("Ann"), b = NewAccount("Annie");
        _store.RequestFriend(a, b);
        _store.AcceptFriend(b, a);

        _store.Block(b, a);
        Assert.False(_store.AreFriends(a, b));
        Assert.Equal(Store.FriendResult.Blocked, _store.RequestFriend(a, b));
        Assert.Empty(_store.SearchByName(a, "Ann"));       // a cannot find b
        Assert.Empty(_store.SearchByName(b, "Ann"));       // nor b find a
        Assert.True(_store.IsBlockedEitherWay(a, b));

        _store.Unblock(b, a);
        Assert.Single(_store.SearchByName(a, "Ann"));
    }

    [Fact]
    public void SearchFindsByPrefixAndTagAndSkipsUnnamed()
    {
        string me = NewAccount("Me Myself");
        string alex = NewAccount("Alex");
        NewAccount("Alexandra");
        NewAccount("Bob");
        NewAccount(); // still "Player": never listed

        var results = _store.SearchByName(me, "ale");
        Assert.Equal(2, results.Count);
        Assert.Equal("Alex", results[0].Name); // the shorter, closer name first

        int tag = _store.GetProfile(alex).Tag;
        Assert.Single(_store.SearchByName(me, $"alex#{tag:D4}"));
        Assert.Empty(_store.SearchByName(me, "Player"));
        Assert.Empty(_store.SearchByName(me, "a"));     // two characters minimum
        Assert.Empty(_store.SearchByName(me, "%"));     // wildcards are literal
    }

    [Fact]
    public void ThreeNameReportsResetTheName()
    {
        string target = NewAccount("Rude Name");
        int tag = _store.GetProfile(target).Tag;
        string r1 = NewAccount(), r2 = NewAccount(), r3 = NewAccount();

        Assert.False(_store.Report(r1, target, "name"));
        Assert.False(_store.Report(r1, target, "name")); // the same reporter twice counts once
        Assert.False(_store.Report(r2, target, "name"));
        Assert.True(_store.Report(r3, target, "name"));

        Store.Profile p = _store.GetProfile(target);
        Assert.Equal(Store.DefaultName, p.Name);
        Assert.False(_store.Report(r1, target, "made-up reason"));
        _ = tag;
    }

    [Fact]
    public void LeaderboardKeepsBestAndRanks()
    {
        string a = NewAccount("Ann"), b = NewAccount("Ben"), c = NewAccount("Cat");
        _store.SubmitEndless(a, 5);
        _store.SubmitEndless(b, 9);
        _store.SubmitEndless(a, 3);                       // lower: ignored
        Assert.Equal(5, _store.GetProfile(a).EndlessBest);
        Assert.Equal(0, _store.SubmitEndless(c, 100000)); // implausible: refused

        var top = _store.TopEndless(a);
        Assert.Equal(2, top.Count);
        Assert.Equal("Ben", top[0].Name);
        Assert.True(top[1].You);
        Assert.Equal(2, _store.RankOf(a));
        Assert.Equal(0, _store.RankOf(c));
    }

    [Fact]
    public void DeletingAnAccountRemovesEveryTrace()
    {
        string a = NewAccount("Ann"), b = NewAccount("Ben");
        _store.RequestFriend(a, b);
        _store.Block(b, a);
        _store.Report(b, a, "name");
        _store.DeleteAccount(a);

        Assert.Null(_store.GetProfile(a));
        foreach (string table in new[] { "friends", "blocks", "reports" })
        {
            using var cmd = _keep.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            Assert.Equal(0L, (long)cmd.ExecuteScalar());
        }
    }
}
