namespace CriticalCount.Server;

/// Where a match's messages go. The lobby implements it over WebSockets; the tests over a list.
public interface IMatchSink
{
    void Send(string accountId, object message);
    void MatchOver(OnlineMatch match);
}

/// One online match, run entirely on the server with the game's own rules (Table, Player,
/// CardEffects, SetRules, GameState - the same files the phone runs offline).
///
/// It is GameManager's turn loop with the screen taken out: there are no animations to wait for
/// and no prompts to acknowledge, so where GameManager waits on a button the match waits on a
/// message or the clock instead. Everything is driven from outside - Act() for a message from a
/// phone and Tick() from the server's clock - and every entry point takes the match's lock, so
/// two phones pressing at once is the same as one after the other.
///
/// What each phone is told is built per seat (ViewFor): your own hand, your opponent's hand
/// COUNT, never the order of either deck.
public sealed class OnlineMatch : ITableHost
{
    public const int TurnSeconds = 30;
    /// A turn re-opened by a card played at you always leaves at least this long to answer it.
    public const int ReopenGraceSeconds = 10;
    /// Missing this many turns in a row in one match forfeits it.
    public const int TimeoutsToForfeit = 3;
    /// How long a dropped phone has to come back before the match is given to the other player.
    public const int DisconnectGraceSeconds = 45;
    /// The pause between a set ending and the next one being dealt, so both phones can show it.
    public const double SetEndPauseSeconds = 4.0;
    public const int Target = 20;

    public enum Phase { Playing, SetEnd, Over }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public Phase Current { get; private set; } = Phase.Playing;
    public string[] Seats { get; }
    public int Winner { get; private set; } = -1; // seat index, once Over
    public string EndReason { get; private set; }

    private readonly object _gate = new();
    private readonly IMatchSink _sink;
    private readonly Func<DateTime> _clock;
    private readonly Player _p1;
    private readonly Player _p2;
    private readonly GameState _state = new() { TargetScore = Target };
    private readonly Table _table;
    private readonly bool _specials;

    private DateTime _turnDeadline;
    private DateTime _setResumeAt;
    private readonly int[] _timeouts = new int[2];
    private readonly DateTime?[] _disconnectedAt = new DateTime?[2];
    private EventView _lastEvent;
    private int _lastEventBySeat = -1;

    public Random Rng { get; }

    public OnlineMatch(string seat0, string name0, string seat1, string name1, IMatchSink sink,
                       bool specials = false, Random rng = null, Func<DateTime> clock = null)
    {
        Seats = new[] { seat0, seat1 };
        _sink = sink;
        _specials = specials;
        Rng = rng ?? new Random();
        _clock = clock ?? (() => DateTime.UtcNow);
        _p1 = new Player(name0);
        _p2 = new Player(name1);
        _table = new Table(this);
    }

    public void Start()
    {
        lock (_gate)
        {
            for (int s = 0; s < 2; s++)
            {
                _sink.Send(Seats[s], new
                {
                    type = "matchStart",
                    matchId = Id,
                    opponent = new PlayerRef(Seats[1 - s], PlayerOf(1 - s).PlayerName),
                    target = Target,
                    setsToWin = GameState.SetsToWinMatch,
                    turnSeconds = TurnSeconds,
                });
            }
            _table.DealMatchHands();
            StartNewSet();
        }
    }

    public int SeatOf(string accountId) => accountId == Seats[0] ? 0 : accountId == Seats[1] ? 1 : -1;

    private Player PlayerOf(int seat) => seat == 0 ? _p1 : _p2;

    // ------------------------------------------------------------------
    // The turn loop (GameManager's StartNewSet / DealCards / ResolveTurn / EndSet)
    // ------------------------------------------------------------------

    private void StartNewSet()
    {
        _p1.ResetForNewSet();
        _p2.ResetForNewSet();
        _table.StartSet();
        Current = Phase.Playing;
        DealTurn();
    }

    private void DealTurn()
    {
        _table.DealTurn();
        _turnDeadline = _clock().AddSeconds(TurnSeconds);
        BroadcastState();
    }

    /// Nothing happens until BOTH players are done with the turn; then the set ends or the next
    /// turn is dealt.
    private void Resolve()
    {
        if (Current != Phase.Playing) return;
        if (_p1.CanAct || _p2.CanAct)
        {
            BroadcastState();
            return;
        }

        if (SetRules.IsSetOver(_p1, _p2, _state.TargetScore))
        {
            EndSet();
            return;
        }
        DealTurn();
    }

    private void EndSet()
    {
        int winner = SetRules.Winner(_p1.CurrentScore, _p2.CurrentScore, _state.TargetScore);
        if (winner != 0) _state.RecordSetWinner(winner);

        for (int s = 0; s < 2; s++)
        {
            string w = winner == 0 ? "tie" : (winner - 1 == s ? "you" : "them");
            _sink.Send(Seats[s], new
            {
                type = "setEnd",
                winner = w,
                yourScore = PlayerOf(s).CurrentScore,
                theirScore = PlayerOf(1 - s).CurrentScore,
            });
        }

        if (_state.CheckMatchWinner(out int matchWinner))
        {
            Finish(matchWinner - 1, "sets");
            return;
        }

        Current = Phase.SetEnd;
        _setResumeAt = _clock().AddSeconds(SetEndPauseSeconds);
        BroadcastState();
    }

    private void Finish(int winningSeat, string reason)
    {
        if (Current == Phase.Over) return;
        Current = Phase.Over;
        Winner = winningSeat;
        EndReason = reason;
        _state.IsGameOver = true;
        BroadcastState();
        for (int s = 0; s < 2; s++)
            _sink.Send(Seats[s], new { type = "matchEnd", winner = s == winningSeat ? "you" : "them", reason });
        _sink.MatchOver(this);
    }

    // ------------------------------------------------------------------
    // What the phones may do
    // ------------------------------------------------------------------

    /// One message from one phone. Returns an error code for the sender, or null when it was
    /// applied. A refused play changes nothing.
    public string Act(string accountId, Inbound msg)
    {
        lock (_gate)
        {
            int seat = SeatOf(accountId);
            if (seat < 0) return "not_in_match";
            if (msg.Type == "forfeit")
            {
                Finish(1 - seat, "forfeit");
                return null;
            }
            if (Current != Phase.Playing) return "not_now";

            Player me = PlayerOf(seat);
            if (!me.CanAct) return "not_your_move";

            string error = msg.Type switch
            {
                "draw" => EndTurn(me, hold: false),
                "hold" => EndTurn(me, hold: true),
                "play" => Play(seat, me, msg),
                _ => "unknown_message",
            };
            if (error != null) return error;

            _timeouts[seat] = 0; // they are here and playing
            Resolve();
            return null;
        }
    }

    private static string EndTurn(Player me, bool hold)
    {
        if (hold) me.IsHolding = true;
        else me.HasEndedTurn = true;
        return null;
    }

    private string Play(int seat, Player me, Inbound msg)
    {
        Card card = me.Modifiers.Find(c => c.Id == msg.CardId);
        if (card == null) return "no_such_card";

        if (card.Effect != CardEffect.None)
        {
            Card chosen = null;
            if (card.Effect == CardEffect.Recall)
            {
                chosen = me.SpentCards.Find(c => c.Id == msg.ChosenId);
                if (chosen == null) return "recall_needs_choice";
            }

            if (!_table.TryPlayEffect(me, card, chosen, out Table.EffectPlay play))
                return "effect_refused";

            _lastEvent = new EventView(card.Effect.ToString(), true, play.Result.Narration, play.Destroyed?.Id);
            _lastEventBySeat = seat;

            // The answering rule re-opened their turn: make sure they have time to answer.
            if (play.Reopened)
            {
                DateTime floor = _clock().AddSeconds(ReopenGraceSeconds);
                if (_turnDeadline < floor) _turnDeadline = floor;
            }
            return null;
        }

        if (_table.IsRecallLocked(me, card)) return "recall_locked";

        // A +/- card is played at the sign the phone chose. Any other value is refused rather
        // than guessed at - the server never plays a card the player did not mean.
        if (msg.Value.HasValue && msg.Value.Value != card.Value)
        {
            if (!card.CanFlipValue || msg.Value.Value != -card.Value) return "bad_value";
            card.FlipValue();
        }

        return me.PlayModifierCard(card, _state) ? null : "no_such_card";
    }

    // ------------------------------------------------------------------
    // The clock
    // ------------------------------------------------------------------

    /// Called a few times a second by the server. Runs out turns, resumes after a set, and gives
    /// the match away when a dropped phone does not come back.
    public void Tick()
    {
        lock (_gate)
        {
            if (Current == Phase.Over) return;
            DateTime now = _clock();

            for (int s = 0; s < 2; s++)
            {
                if (_disconnectedAt[s] is DateTime gone && (now - gone).TotalSeconds >= DisconnectGraceSeconds)
                {
                    Finish(1 - s, "disconnect");
                    return;
                }
            }

            if (Current == Phase.SetEnd)
            {
                if (now >= _setResumeAt) StartNewSet();
                return;
            }

            if (now < _turnDeadline) return;

            // Time is up: anyone still deciding ends their turn as if they had pressed Draw Card.
            // That is the gentlest reading of silence - it never commits them to a Hold.
            for (int s = 0; s < 2; s++)
            {
                Player p = PlayerOf(s);
                if (!p.CanAct) continue;
                p.HasEndedTurn = true;
                if (++_timeouts[s] >= TimeoutsToForfeit)
                {
                    Finish(1 - s, "timeouts");
                    return;
                }
            }
            Resolve();
        }
    }

    public void Disconnected(string accountId)
    {
        lock (_gate)
        {
            int seat = SeatOf(accountId);
            if (seat < 0 || Current == Phase.Over) return;
            _disconnectedAt[seat] ??= _clock();
            BroadcastState();
        }
    }

    /// The phone is back: it gets the table as it stands now.
    public void Reconnected(string accountId)
    {
        lock (_gate)
        {
            int seat = SeatOf(accountId);
            if (seat < 0) return;
            _disconnectedAt[seat] = null;
            _sink.Send(Seats[seat], new
            {
                type = "matchStart",
                matchId = Id,
                opponent = new PlayerRef(Seats[1 - seat], PlayerOf(1 - seat).PlayerName),
                target = Target,
                setsToWin = GameState.SetsToWinMatch,
                turnSeconds = TurnSeconds,
                resumed = true,
            });
            BroadcastState();
        }
    }

    // ------------------------------------------------------------------
    // What each phone sees
    // ------------------------------------------------------------------

    private void BroadcastState()
    {
        for (int s = 0; s < 2; s++) _sink.Send(Seats[s], ViewFor(s));
        _lastEvent = null; // an event is news once
        _lastEventBySeat = -1;
    }

    public StateMessage ViewFor(int seat)
    {
        Player me = PlayerOf(seat);
        Player them = PlayerOf(1 - seat);
        long msLeft = Current == Phase.Playing ? Math.Max(0, (long)(_turnDeadline - _clock()).TotalMilliseconds) : 0;
        EventView ev = _lastEvent == null ? null : _lastEvent with { ByYou = _lastEventBySeat == seat };

        return new StateMessage(
            "state", Id, _state.TargetScore, GameState.SetsToWinMatch, msLeft,
            Current switch { Phase.Playing => "playing", Phase.SetEnd => "setEnd", _ => "over" },
            Side(seat, me, mine: true),
            Side(1 - seat, them, mine: false),
            ev);
    }

    private SideView Side(int seat, Player p, bool mine)
    {
        Card locked = null;
        if (mine) locked = p.Modifiers.Find(c => _table.IsRecallLocked(p, c));
        return new SideView(
            p.PlayerName,
            p.CurrentScore,
            p.IsHolding,
            p.HasEndedTurn,
            Current == Phase.Playing && p.CanAct,
            seat == 0 ? _state.SetsWonPlayer1 : _state.SetsWonPlayer2,
            p.ActiveCardsOnBoard.Select(ToView).ToList(),
            p.Modifiers.Count,
            mine ? p.Modifiers.Select(ToView).ToList() : null,
            mine ? p.SpentCards.Select(ToView).ToList() : null,
            locked?.Id,
            _table.HasPlayedEffect(p),
            _disconnectedAt[seat] == null,
            _table.Remaining(p));
    }

    private static CardView ToView(Card c) =>
        new(c.Id, c.Value, c.CanFlipValue, c.Effect == CardEffect.None ? null : c.Effect.ToString(), c.DisplayText);

    // ------------------------------------------------------------------
    // ITableHost: an online match is local 2-player's table with a server for a room
    // ------------------------------------------------------------------
    Player ITableHost.Player1 => _p1;
    Player ITableHost.Player2 => _p2;
    GameState ITableHost.State => _state;
    Random ITableHost.Rng => Rng;
    RunData ITableHost.Run => null;
    bool ITableHost.VsBot => false;
    bool ITableHost.LocalSpecials => _specials;
    bool ITableHost.TutorialStaged => false;
    IReadOnlyList<int> ITableHost.TutorialOpening => Array.Empty<int>();
    IReadOnlyList<int> ITableHost.TutorialModifiers => Array.Empty<int>();
    bool ITableHost.TutorialOverLesson => false;
    IReadOnlyList<int> ITableHost.TutorialOverDraws => Array.Empty<int>();
    bool ITableHost.TutorialFlipLesson => false;
    IReadOnlyList<int> ITableHost.TutorialFlipDraws => Array.Empty<int>();
    List<CardEffect> ITableHost.UnlockedLocalSpecials() => CardEffects.WiredEffects();

    /// Player 2 is a person here, so they get exactly what Player 1 got: a plain hand, plus one
    /// special when specials are on.
    void ITableHost.DealBotHand()
    {
        _table.DealPlainHand(_p2);
        if (_specials) _table.AddLocalSpecial(_p2);
    }

    void ITableHost.ShowDrawnCard(Player player, Card card, float delay) { }
    void ITableHost.HandsDealt(bool introduceCards) { }
}
