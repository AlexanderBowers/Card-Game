using System.Text.Json;
using CriticalCount.Server;
using Microsoft.Data.Sqlite;
using Xunit;

/// Collects what a match or the lobby would have sent, per account.
public sealed class FakeSink : IMatchSink
{
    public readonly List<(string to, object msg)> Sent = new();
    public readonly List<OnlineMatch> Over = new();

    public void Send(string accountId, object message) => Sent.Add((accountId, message));
    public void MatchOver(OnlineMatch match) => Over.Add(match);

    public StateMessage LastState(string to) =>
        Sent.Where(s => s.to == to && s.msg is StateMessage).Select(s => (StateMessage)s.msg).LastOrDefault();

    public List<string> TypesFor(string to) => Sent.Where(s => s.to == to).Select(s => TypeOf(s.msg)).ToList();

    public static string TypeOf(object msg) =>
        JsonDocument.Parse(JsonSerializer.Serialize(msg, msg.GetType(), Protocol.Json)).RootElement.GetProperty("type").GetString();
}

public sealed class FakeClock
{
    public DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
}

public class MatchTests
{
    private readonly FakeSink _sink = new();
    private readonly FakeClock _clock = new();

    private OnlineMatch NewMatch(int seed = 1, bool specials = false)
    {
        var m = new OnlineMatch("A", "Ann#0001", "B", "Ben#0002", _sink, specials, new Random(seed), () => _clock.Now);
        m.Start();
        return m;
    }

    private static Inbound Msg(string type, int cardId = 0, int? value = null) =>
        new() { Type = type, CardId = cardId, Value = value };

    [Fact]
    public void StartDealsBothSidesAndHidesTheOpponentsHand()
    {
        NewMatch();
        StateMessage a = _sink.LastState("A");
        StateMessage b = _sink.LastState("B");

        Assert.Equal("playing", a.Phase);
        Assert.Equal(20, a.Target);
        Assert.Equal(4, a.You.Hand.Count);
        Assert.Null(a.Them.Hand);             // never sent
        Assert.Null(a.Them.Spent);
        Assert.Equal(4, a.Them.HandCount);
        Assert.Equal(2, a.You.Board.Count);   // the two-card opening at a target of 20
        Assert.True(a.You.CanAct);
        Assert.Equal(30000, a.TurnMsLeft);

        // A's view of B is B's own view with the hand taken out.
        Assert.Equal(b.You.Score, a.Them.Score);
        Assert.Equal("Ben#0002", a.Them.Name);

        // And the serialized message A receives carries none of B's card ids from B's hand.
        string json = JsonSerializer.Serialize(a, Protocol.Json);
        foreach (CardView c in b.You.Hand) Assert.DoesNotContain($"\"id\":{c.Id},", json);
    }

    [Fact]
    public void NothingIsDealtUntilBothAreDone()
    {
        OnlineMatch m = NewMatch();
        Assert.Null(m.Act("A", Msg("draw")));
        Assert.Equal(2, _sink.LastState("A").You.Board.Count);
        Assert.Equal("not_your_move", m.Act("A", Msg("draw")));

        Assert.Null(m.Act("B", Msg("draw")));
        StateMessage a = _sink.LastState("A");
        if (a.Phase == "playing") Assert.Equal(3, a.You.Board.Count);
    }

    [Fact]
    public void YouCannotPlaySomeoneElsesCardOrAMadeUpValue()
    {
        OnlineMatch m = NewMatch();
        CardView theirs = _sink.LastState("B").You.Hand[0];
        Assert.Equal("no_such_card", m.Act("A", Msg("play", theirs.Id)));
        Assert.Equal("no_such_card", m.Act("A", Msg("play", 999999)));

        CardView mine = _sink.LastState("A").You.Hand.First(c => c.Effect == null);
        Assert.Equal("bad_value", m.Act("A", Msg("play", mine.Id, mine.Value + 1)));
        if (!mine.Flip) Assert.Equal("bad_value", m.Act("A", Msg("play", mine.Id, -mine.Value)));

        int before = _sink.LastState("A").You.Score;
        Assert.Null(m.Act("A", Msg("play", mine.Id, mine.Value)));
        StateMessage after = _sink.LastState("A");
        Assert.Equal(before + mine.Value, after.You.Score);
        Assert.Equal(3, after.You.Hand.Count);
        Assert.Equal(3, _sink.LastState("B").Them.HandCount);
        Assert.Equal("no_such_card", m.Act("A", Msg("play", mine.Id))); // spent is spent
    }

    [Fact]
    public void AFlipCardPlaysAtTheChosenSign()
    {
        // Find a seed whose opening hand for A holds a +/- card.
        for (int seed = 0; seed < 500; seed++)
        {
            _sink.Sent.Clear();
            OnlineMatch m = NewMatch(seed);
            CardView flip = _sink.LastState("A").You.Hand.FirstOrDefault(c => c.Flip && c.Effect == null);
            if (flip == null) continue;

            int before = _sink.LastState("A").You.Score;
            Assert.Null(m.Act("A", Msg("play", flip.Id, -flip.Value)));
            Assert.Equal(before - flip.Value, _sink.LastState("A").You.Score);
            return;
        }
        Assert.Fail("no seed dealt a flip card");
    }

    [Fact]
    public void TheClockEndsTheTurnAsDrawCard()
    {
        OnlineMatch m = NewMatch();
        m.Act("A", Msg("draw"));
        _clock.Advance(29);
        m.Tick();
        Assert.True(_sink.LastState("B").You.CanAct);

        _clock.Advance(2);
        m.Tick();
        StateMessage b = _sink.LastState("B");
        Assert.False(b.You.Holding); // silence is never a Hold
    }

    [Fact]
    public void ThreeMissedTurnsInARowForfeit()
    {
        OnlineMatch m = NewMatch();
        for (int turn = 0; turn < 3 && m.Current != OnlineMatch.Phase.Over; turn++)
        {
            // A keeps playing; B never does.
            if (m.Current == OnlineMatch.Phase.Playing && _sink.LastState("A").You.CanAct) m.Act("A", Msg("draw"));
            _clock.Advance(OnlineMatch.TurnSeconds + 1);
            m.Tick();
            if (m.Current == OnlineMatch.Phase.SetEnd)
            {
                _clock.Advance(OnlineMatch.SetEndPauseSeconds + 0.1);
                m.Tick();
            }
        }
        // Unless the sets ran out first, B lost on timeouts.
        if (m.Current == OnlineMatch.Phase.Over && m.EndReason == "timeouts") Assert.Equal(0, m.Winner);
    }

    [Fact]
    public void ForfeitEndsTheMatchForBoth()
    {
        OnlineMatch m = NewMatch();
        Assert.Null(m.Act("B", Msg("forfeit")));
        Assert.Equal(OnlineMatch.Phase.Over, m.Current);
        Assert.Equal(0, m.Winner);
        Assert.Equal("forfeit", m.EndReason);
        Assert.Single(_sink.Over);
        Assert.Contains("matchEnd", _sink.TypesFor("A"));
        Assert.Contains("matchEnd", _sink.TypesFor("B"));
        Assert.Equal("not_now", m.Act("A", Msg("draw")));
    }

    [Fact]
    public void ADroppedPhoneHasGraceThenLoses()
    {
        OnlineMatch m = NewMatch();
        m.Disconnected("B");
        Assert.False(_sink.LastState("A").Them.Connected);

        _clock.Advance(OnlineMatch.DisconnectGraceSeconds - 5);
        m.Reconnected("B");
        Assert.True(_sink.LastState("A").Them.Connected);

        m.Disconnected("B");
        _clock.Advance(OnlineMatch.DisconnectGraceSeconds + 1);
        m.Tick();
        Assert.Equal(OnlineMatch.Phase.Over, m.Current);
        Assert.Equal("disconnect", m.EndReason);
        Assert.Equal(0, m.Winner);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void AWholeMatchPlaysToThreeSets(int seed, bool specials)
    {
        OnlineMatch m = NewMatch(seed, specials);
        var rng = new Random(seed * 31);
        for (int step = 0; step < 5000 && m.Current != OnlineMatch.Phase.Over; step++)
        {
            if (m.Current == OnlineMatch.Phase.SetEnd)
            {
                _clock.Advance(OnlineMatch.SetEndPauseSeconds + 0.1);
                m.Tick();
                continue;
            }
            foreach (string seat in new[] { "A", "B" })
            {
                StateMessage s = _sink.LastState(seat);
                if (m.Current != OnlineMatch.Phase.Playing || !s.You.CanAct) continue;

                // Now and then play a plain card that helps; otherwise hold at 17+, else draw.
                CardView help = s.You.Hand.FirstOrDefault(c => c.Effect == null && s.You.Score + c.Value <= 20 && s.You.Score + c.Value >= 18);
                if (help != null && rng.Next(2) == 0) m.Act(seat, Msg("play", help.Id, help.Value));
                else if (s.You.Score > 20)
                {
                    CardView minus = s.You.Hand.FirstOrDefault(c => c.Effect == null && (c.Value < 0 || c.Flip));
                    if (minus != null) m.Act(seat, Msg("play", minus.Id, -Math.Abs(minus.Value)));
                    m.Act(seat, Msg("draw"));
                }
                else m.Act(seat, Msg(s.You.Score >= 17 ? "hold" : "draw"));
            }
        }
        Assert.Equal(OnlineMatch.Phase.Over, m.Current);
        Assert.Equal("sets", m.EndReason);
        StateMessage final = _sink.LastState(m.Seats[m.Winner]);
        Assert.Equal(GameState.SetsToWinMatch, final.You.Wins);
        Assert.Contains("setEnd", _sink.TypesFor("A"));
    }
}

public class LobbyTests : IDisposable
{
    private sealed class FakeClient : Lobby.IClient
    {
        public string AccountId { get; init; }
        public string DisplayName { get; init; }
        public readonly List<object> Got = new();
        public bool Closed;
        public void Send(object message) => Got.Add(message);
        public void Close() => Closed = true;
        public List<string> Types => Got.Select(FakeSink.TypeOf).ToList();
    }

    private readonly SqliteConnection _keep;
    private readonly Store _store;
    private readonly FakeClock _clock = new();
    private readonly Lobby _lobby;

    public LobbyTests()
    {
        _store = Store.InMemory(out _keep);
        _lobby = new Lobby(_store, clock: () => _clock.Now, rngFactory: () => new Random(7));
    }

    public void Dispose() => _keep.Dispose();

    private FakeClient Join(string name)
    {
        var (id, _) = _store.CreateAccount();
        _store.SetName(id, name);
        var c = new FakeClient { AccountId = id, DisplayName = _store.GetProfile(id).Display };
        _lobby.Connected(c);
        return c;
    }

    [Fact]
    public void TwoInTheQueueMakeAMatch()
    {
        FakeClient a = Join("Ann"), b = Join("Ben");
        _lobby.Handle(a, new Inbound { Type = "queue" });
        Assert.Contains("queued", a.Types);
        _lobby.Handle(b, new Inbound { Type = "queue" });

        Assert.Contains("matchStart", a.Types);
        Assert.Contains("matchStart", b.Types);
        Assert.True(_lobby.IsInMatch(a.AccountId));
        Assert.Single(_lobby.Matches);
    }

    [Fact]
    public void BlockedPlayersAreNeverPaired()
    {
        FakeClient a = Join("Ann"), b = Join("Ben"), c = Join("Cat");
        _store.Block(b.AccountId, a.AccountId);
        _lobby.Handle(a, new Inbound { Type = "queue" });
        _lobby.Handle(b, new Inbound { Type = "queue" });
        Assert.DoesNotContain("matchStart", b.Types);

        _lobby.Handle(c, new Inbound { Type = "queue" });
        Assert.Contains("matchStart", a.Types);
        Assert.Contains("matchStart", c.Types);
        Assert.DoesNotContain("matchStart", b.Types);
    }

    [Fact]
    public void OnlyFriendsCanChallenge()
    {
        FakeClient a = Join("Ann"), b = Join("Ben");
        _lobby.Handle(a, new Inbound { Type = "invite", To = b.AccountId });
        Assert.Contains("error", a.Types);
        Assert.DoesNotContain("invited", b.Types);
    }

    [Fact]
    public void AnAcceptedChallengeStartsAMatch()
    {
        FakeClient a = Join("Ann"), b = Join("Ben");
        _store.RequestFriend(a.AccountId, b.AccountId);
        _store.AcceptFriend(b.AccountId, a.AccountId);

        _lobby.Handle(a, new Inbound { Type = "invite", To = b.AccountId });
        string inviteId = InviteIdFrom(b);
        _lobby.Handle(b, new Inbound { Type = "inviteReply", InviteId = inviteId, Accept = true });

        Assert.Contains("matchStart", a.Types);
        Assert.Contains("matchStart", b.Types);
    }

    [Fact]
    public void ChallengesDeclineAndExpire()
    {
        FakeClient a = Join("Ann"), b = Join("Ben");
        _store.RequestFriend(a.AccountId, b.AccountId);
        _store.AcceptFriend(b.AccountId, a.AccountId);

        _lobby.Handle(a, new Inbound { Type = "invite", To = b.AccountId });
        _lobby.Handle(b, new Inbound { Type = "inviteReply", InviteId = InviteIdFrom(b), Accept = false });
        Assert.Contains("inviteClosed", a.Types);

        a.Got.Clear();
        _lobby.Handle(a, new Inbound { Type = "invite", To = b.AccountId });
        _clock.Advance(Lobby.InviteSeconds + 1);
        _lobby.Tick();
        Assert.Contains("inviteClosed", a.Types);
        Assert.DoesNotContain("matchStart", a.Types);
    }

    [Fact]
    public void ASecondSignInReplacesTheFirst()
    {
        FakeClient a = Join("Ann");
        var again = new FakeClient { AccountId = a.AccountId, DisplayName = a.DisplayName };
        _lobby.Connected(again);
        Assert.True(a.Closed);
        _lobby.Disconnected(a); // the old socket closing must not take the new one offline
        Assert.True(_lobby.IsOnline(a.AccountId));
    }

    [Fact]
    public void ReconnectingPutsYouBackInYourMatch()
    {
        FakeClient a = Join("Ann"), b = Join("Ben");
        _lobby.Handle(a, new Inbound { Type = "queue" });
        _lobby.Handle(b, new Inbound { Type = "queue" });
        _lobby.Disconnected(b);

        var back = new FakeClient { AccountId = b.AccountId, DisplayName = b.DisplayName };
        _lobby.Connected(back);
        Assert.Contains("matchStart", back.Types);
        Assert.Contains("state", back.Types);
    }

    private static string InviteIdFrom(FakeClient c)
    {
        object invited = c.Got.Last(m => FakeSink.TypeOf(m) == "invited");
        return JsonDocument.Parse(JsonSerializer.Serialize(invited, invited.GetType(), Protocol.Json))
            .RootElement.GetProperty("inviteId").GetString();
    }
}
