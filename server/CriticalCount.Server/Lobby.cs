using System.Collections.Concurrent;

namespace CriticalCount.Server;

/// Who is connected, who is waiting for a quick match, open challenges between friends, and
/// which match each player is in.
///
/// Lock order: the lobby's lock is never held while calling into a match (matches have their
/// own lock and call back into the lobby through IMatchSink). Every method below collects what
/// it needs under the lobby lock, lets go, and only then touches a match.
public sealed class Lobby : IMatchSink
{
    public const int InviteSeconds = 30;

    /// One phone's connection, as the lobby sees it: somewhere to send, nothing more.
    public interface IClient
    {
        string AccountId { get; }
        string DisplayName { get; }
        void Send(object message);
        void Close();
    }

    private sealed record Invite(string Id, string From, string To, DateTime Expires);

    private readonly object _gate = new();
    private readonly Store _store;
    private readonly Func<DateTime> _clock;
    private readonly bool _specials;
    private readonly Func<Random> _rngFactory;

    private readonly Dictionary<string, IClient> _clients = new();
    private readonly List<string> _queue = new();
    private readonly Dictionary<string, Invite> _invites = new();       // by invite id
    private readonly Dictionary<string, OnlineMatch> _matchOf = new();  // by account id
    private readonly ConcurrentDictionary<string, OnlineMatch> _matches = new();

    public Lobby(Store store, bool specials = false, Func<DateTime> clock = null, Func<Random> rngFactory = null)
    {
        _store = store;
        _specials = specials;
        _clock = clock ?? (() => DateTime.UtcNow);
        _rngFactory = rngFactory ?? (() => new Random());
    }

    public IEnumerable<OnlineMatch> Matches => _matches.Values;

    // ------------------------------------------------------------------
    // Presence
    // ------------------------------------------------------------------

    public bool IsOnline(string accountId)
    {
        lock (_gate) return _clients.ContainsKey(accountId);
    }

    public bool IsInMatch(string accountId)
    {
        lock (_gate) return _matchOf.ContainsKey(accountId);
    }

    /// A phone has signed in on the socket. A second sign-in for the same account replaces the
    /// first (the newest phone wins), and a player with a match in progress is put back in it.
    public void Connected(IClient client)
    {
        IClient replaced;
        OnlineMatch match;
        lock (_gate)
        {
            _clients.TryGetValue(client.AccountId, out replaced);
            _clients[client.AccountId] = client;
            _matchOf.TryGetValue(client.AccountId, out match);
        }
        if (replaced != null && !ReferenceEquals(replaced, client))
        {
            replaced.Send(new { type = "error", code = "signed_in_elsewhere", message = "Signed in on another device." });
            replaced.Close();
        }
        client.Send(new { type = "authed", you = new PlayerRef(client.AccountId, client.DisplayName) });
        match?.Reconnected(client.AccountId);
    }

    public void Disconnected(IClient client)
    {
        OnlineMatch match = null;
        var closedInvites = new List<Invite>();
        lock (_gate)
        {
            if (!_clients.TryGetValue(client.AccountId, out IClient current) || !ReferenceEquals(current, client)) return;
            _clients.Remove(client.AccountId);
            _queue.Remove(client.AccountId);
            foreach (Invite inv in _invites.Values.Where(i => i.From == client.AccountId || i.To == client.AccountId).ToList())
            {
                _invites.Remove(inv.Id);
                closedInvites.Add(inv);
            }
            _matchOf.TryGetValue(client.AccountId, out match);
        }
        foreach (Invite inv in closedInvites) NotifyInviteClosed(inv, "unavailable", skip: client.AccountId);
        match?.Disconnected(client.AccountId);
    }

    // ------------------------------------------------------------------
    // Messages
    // ------------------------------------------------------------------

    public void Handle(IClient client, Inbound msg)
    {
        string me = client.AccountId;
        switch (msg.Type)
        {
            case "ping":
                client.Send(new { type = "pong" });
                return;
            case "queue":
                JoinQueue(client);
                return;
            case "leaveQueue":
                lock (_gate) _queue.Remove(me);
                client.Send(new { type = "queueLeft" });
                return;
            case "invite":
                SendInvite(client, msg.To);
                return;
            case "cancelInvite":
                CancelInvitesFrom(me);
                return;
            case "inviteReply":
                ReplyToInvite(client, msg.InviteId, msg.Accept);
                return;
            case "play":
            case "draw":
            case "hold":
            case "forfeit":
            {
                OnlineMatch match;
                lock (_gate) _matchOf.TryGetValue(me, out match);
                if (match == null)
                {
                    client.Send(Error("not_in_match"));
                    return;
                }
                string error = match.Act(me, msg);
                if (error != null) client.Send(Error(error));
                return;
            }
            default:
                client.Send(Error("unknown_message"));
                return;
        }
    }

    private static object Error(string code, string message = null) => new { type = "error", code, message };

    // ------------------------------------------------------------------
    // Quick match
    // ------------------------------------------------------------------

    private void JoinQueue(IClient client)
    {
        string me = client.AccountId;
        string opponent = null;
        lock (_gate)
        {
            if (_matchOf.ContainsKey(me))
            {
                client.Send(Error("already_in_match"));
                return;
            }
            if (_queue.Contains(me)) return;

            // First come, first served - skipping anyone either side has blocked.
            foreach (string waiting in _queue)
            {
                if (_store.IsBlockedEitherWay(me, waiting)) continue;
                opponent = waiting;
                break;
            }
            if (opponent == null)
            {
                _queue.Add(me);
                client.Send(new { type = "queued" });
                return;
            }
            _queue.Remove(opponent);
        }
        StartMatch(opponent, me);
    }

    // ------------------------------------------------------------------
    // Challenges between friends
    // ------------------------------------------------------------------

    private void SendInvite(IClient client, string to)
    {
        string me = client.AccountId;
        if (string.IsNullOrEmpty(to) || to == me || !_store.AreFriends(me, to))
        {
            client.Send(Error("not_friends"));
            return;
        }

        Invite invite;
        IClient target;
        lock (_gate)
        {
            if (_matchOf.ContainsKey(me))
            {
                client.Send(Error("already_in_match"));
                return;
            }
            if (!_clients.TryGetValue(to, out target) || _matchOf.ContainsKey(to))
            {
                client.Send(Error("friend_unavailable"));
                return;
            }
            // One open challenge per player: a new one replaces the old.
            foreach (Invite old in _invites.Values.Where(i => i.From == me).ToList()) _invites.Remove(old.Id);
            _queue.Remove(me);
            invite = new Invite(Guid.NewGuid().ToString("N")[..12], me, to, _clock().AddSeconds(InviteSeconds));
            _invites[invite.Id] = invite;
        }
        client.Send(new { type = "inviteSent", inviteId = invite.Id, to });
        target.Send(new { type = "invited", inviteId = invite.Id, from = new PlayerRef(me, client.DisplayName), seconds = InviteSeconds });
    }

    private void CancelInvitesFrom(string me)
    {
        List<Invite> cancelled;
        lock (_gate)
        {
            cancelled = _invites.Values.Where(i => i.From == me).ToList();
            foreach (Invite inv in cancelled) _invites.Remove(inv.Id);
        }
        foreach (Invite inv in cancelled) NotifyInviteClosed(inv, "cancelled");
    }

    private void ReplyToInvite(IClient client, string inviteId, bool accept)
    {
        Invite invite;
        bool unavailable = false;
        lock (_gate)
        {
            if (inviteId == null || !_invites.TryGetValue(inviteId, out invite) || invite.To != client.AccountId)
            {
                client.Send(Error("no_such_invite"));
                return;
            }
            _invites.Remove(inviteId);
            if (accept)
            {
                unavailable = !_clients.ContainsKey(invite.From) || _matchOf.ContainsKey(invite.From) || _matchOf.ContainsKey(invite.To);
                if (!unavailable)
                {
                    _queue.Remove(invite.From);
                    _queue.Remove(invite.To);
                }
            }
        }
        if (!accept || unavailable)
        {
            NotifyInviteClosed(invite, unavailable ? "unavailable" : "declined");
            return;
        }
        StartMatch(invite.From, invite.To);
    }

    private void NotifyInviteClosed(Invite invite, string reason, string skip = null)
    {
        foreach (string who in new[] { invite.From, invite.To })
        {
            if (who == skip) continue;
            IClient c;
            lock (_gate) _clients.TryGetValue(who, out c);
            c?.Send(new { type = "inviteClosed", inviteId = invite.Id, reason });
        }
    }

    // ------------------------------------------------------------------
    // Matches
    // ------------------------------------------------------------------

    private void StartMatch(string seat0, string seat1)
    {
        string name0 = _store.GetProfile(seat0)?.Display ?? "Player";
        string name1 = _store.GetProfile(seat1)?.Display ?? "Player";
        var match = new OnlineMatch(seat0, name0, seat1, name1, this, _specials, _rngFactory(), _clock);
        lock (_gate)
        {
            _matchOf[seat0] = match;
            _matchOf[seat1] = match;
        }
        _matches[match.Id] = match;
        match.Start();
    }

    void IMatchSink.Send(string accountId, object message)
    {
        IClient c;
        lock (_gate) _clients.TryGetValue(accountId, out c);
        c?.Send(message);
    }

    void IMatchSink.MatchOver(OnlineMatch match)
    {
        lock (_gate)
        {
            foreach (string seat in match.Seats)
                if (_matchOf.TryGetValue(seat, out OnlineMatch m) && ReferenceEquals(m, match)) _matchOf.Remove(seat);
        }
        _matches.TryRemove(match.Id, out _);
    }

    /// The server clock: every match's Tick, and expired challenges.
    public void Tick()
    {
        foreach (OnlineMatch match in _matches.Values) match.Tick();

        List<Invite> expired;
        DateTime now = _clock();
        lock (_gate)
        {
            expired = _invites.Values.Where(i => i.Expires <= now).ToList();
            foreach (Invite inv in expired) _invites.Remove(inv.Id);
        }
        foreach (Invite inv in expired) NotifyInviteClosed(inv, "expired");
    }

    /// The account is being deleted: out of the queue, out of any match (as a forfeit), offline.
    public void Remove(string accountId)
    {
        IClient client;
        OnlineMatch match;
        lock (_gate)
        {
            _clients.TryGetValue(accountId, out client);
            _matchOf.TryGetValue(accountId, out match);
            _queue.Remove(accountId);
        }
        match?.Act(accountId, new Inbound { Type = "forfeit" });
        client?.Close();
    }
}
