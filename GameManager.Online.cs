using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

/// An online match on this table (2026-10-08).
///
/// The server plays the match (server/CriticalCount.Server/OnlineMatch.cs - the same Table and
/// CardEffects this phone runs offline). This phone only SHOWS it: every "state" message rewrites
/// the two Players to what the server says, and the table draws them exactly as it draws a match
/// against the bot. Presses go to the server instead of the rules: Draw Card, Hold and Play send a
/// message and wait for the next state, so a modified phone can ask for anything and get only
/// what the rules allow.
///
/// Local Card objects are kept per server card id, so a card keeps its identity from hand to board
/// - which is what lets a played Modifier fly from its place in the hand, a Copy flip the card that
/// is already on the board, and a Veto burn the exact card it destroyed.
public partial class GameManager
{
    private bool _online;
    private string _onlineMatchId;
    private string _onlinePhase = "playing";
    private bool _onlineYouCanAct;
    private bool _onlineAwaiting;          // a press was sent; nothing more until the next state
    private bool _onlineYouPlayedEffect;
    private Card _onlineRecallLocked;
    private int[] _onlineDeckCounts;       // what is left in your own deck, for the deck odds
    private long _onlineTurnEndsAtMs;      // local clock (Time.GetTicksMsec) when the turn runs out
    private int _onlineShownSeconds = -1;
    private bool _onlineThemConnected = true;
    private bool _onlineOver;

    private readonly Dictionary<int, Card> _onlineCards = new Dictionary<int, Card>();
    private readonly Dictionary<Card, int> _onlineIdOf = new Dictionary<Card, int>();
    private readonly List<Card> _onlineHiddenHand = new List<Card>();

    public bool IsOnlineMatch => _online;

    /// From the Online menu, on "matchStart". The scene is on the start menu; deal nothing - the
    /// server's first "state" (already on its way) puts the cards down.
    public void StartOnlineMatch(JsonElement start)
    {
        OnlineService svc = OnlineService.Instance;
        if (svc == null) return;

        _online = true;
        _onlineOver = false;
        _isGameStarted = true;
        _isVsBot = false;
        _inRun = false;
        _onlineMatchId = OnlineService.Str(start, "matchId");
        _onlinePhase = "playing";
        _onlineAwaiting = false;
        _onlineCards.Clear();
        _onlineIdOf.Clear();
        _onlineHiddenHand.Clear();

        string opponent = start.TryGetProperty("opponent", out JsonElement opp) ? OnlineService.Str(opp, "name") : null;
        _player1.PlayerName = Speech.You;
        _player2.PlayerName = ShortName(opponent);
        _gameState.TargetScore = OnlineService.Int(start, "target", 20);
        _gameState.SetsWonPlayer1 = 0;
        _gameState.SetsWonPlayer2 = 0;
        _gameState.IsGameOver = false;

        _player1.Modifiers.Clear();
        _player2.Modifiers.Clear();
        _player1.ResetForNewMatch();
        _player2.ResetForNewMatch();
        _player1.ResetForNewSet();
        _player2.ResetForNewSet();
        ClearSelections();
        _ui.FillBoardWithSlots(_p1BoardContainer);
        _ui.FillBoardWithSlots(_p2BoardContainer);

        svc.Message -= OnOnlineMessage;
        svc.Message += OnOnlineMessage;
        svc.Closed -= OnOnlineClosed;
        svc.Closed += OnOnlineClosed;

        _ui.ApplyRankTheme();
        _ui.ApplyResponsiveLayout();
        _arrivalPending = true;
        void Arrive() => StageIntro.Play(this, "Online Match", $"vs {_player2.PlayerName}", () =>
        {
            _arrivalPending = false;
            _ui.DeferRefresh();
        });
        if (_ui.World3D != null && _ui.World3D.CanSwing) _ui.World3D.SwingIn(Arrive);
        else Arrive();
        _ui.Refresh();
    }

    /// "Name#1234" on the table reads as "Name": the tag is for finding people, not for playing them.
    private static string ShortName(string display)
    {
        if (string.IsNullOrEmpty(display)) return "Opponent";
        int hash = display.LastIndexOf('#');
        return hash > 0 ? display[..hash] : display;
    }

    private void DetachOnline()
    {
        OnlineService svc = OnlineService.Instance;
        if (svc == null) return;
        svc.Message -= OnOnlineMessage;
        svc.Closed -= OnOnlineClosed;
    }

    // ------------------------------------------------------------------
    // Messages
    // ------------------------------------------------------------------

    private void OnOnlineMessage(string type, JsonElement msg)
    {
        if (!_online || !IsInsideTree()) return;
        switch (type)
        {
            case "state":
                if (OnlineService.Str(msg, "matchId") == _onlineMatchId) ApplyOnlineState(msg);
                break;
            case "setEnd":
                OnlineSetEnd(msg);
                break;
            case "matchEnd":
                OnlineMatchEnd(msg);
                break;
            case "error":
                _onlineAwaiting = false;
                string code = OnlineService.Str(msg, "code");
                if (code == "effect_refused") _ui.Toasts.ShowEffectBanner("That card can't be played right now");
                else if (code == "signed_in_elsewhere") _ui.Toasts.ShowEffectBanner("Signed in on another device");
                _ui.Refresh();
                break;
        }
    }

    private void OnOnlineClosed()
    {
        if (!_online || _onlineOver || !IsInsideTree()) return;
        _ui.Toasts.ShowEffectBanner("Connection lost - reconnecting...");
    }

    private void ApplyOnlineState(JsonElement s)
    {
        _onlineAwaiting = false;
        string phase = OnlineService.Str(s, "phase") ?? "playing";
        JsonElement you = s.GetProperty("you");
        JsonElement them = s.GetProperty("them");

        _gameState.TargetScore = OnlineService.Int(s, "target", _gameState.TargetScore);

        // A new set: the server cleared both boards. Clear ours to match, without ceremony.
        bool newSet = _onlinePhase == "setEnd" && phase == "playing";
        if (newSet || BoardCount(you) < _player1.ActiveCardsOnBoard.Count - 1 && BoardCount(them) < _player2.ActiveCardsOnBoard.Count - 1)
        {
            _player1.ResetForNewSet();
            _player2.ResetForNewSet();
            ClearSelections();
            _ui.Toasts.ClearEffectBanner();
            _ui.FillBoardWithSlots(_p1BoardContainer);
            _ui.FillBoardWithSlots(_p2BoardContainer);
            Prompts_HideSetInfo();
        }
        _onlinePhase = phase;

        // The numbers as they were, for the moments that tick a score over.
        int youBefore = _player1.CurrentScore;
        int themBefore = _player2.CurrentScore;
        var handsBefore = _ui.SnapshotHands();

        // Boards FIRST, while the played card is still in the hand on screen: a Modifier flies
        // from where it sat in the hand.
        bool copied = SyncBoard(_player1, you.GetProperty("board"), _p1BoardContainer);
        copied |= SyncBoard(_player2, them.GetProperty("board"), _p2BoardContainer);

        // Your hand, by id (so a picked-up card stays picked up). A +/- card keeps the sign you
        // flipped it to - the server only learns the sign when you play it.
        List<Card> mine = new List<Card>();
        foreach (JsonElement c in you.GetProperty("hand").EnumerateArray()) mine.Add(CardFor(c, keepFlip: true));
        bool handsTraded = HandIdsChanged(_player1.Modifiers, mine) && _player1.Modifiers.Count > 0 && mine.Count > 0
                           && !SameIdsMinusOne(_player1.Modifiers, mine);
        _player1.Modifiers.Clear();
        _player1.Modifiers.AddRange(mine);

        _player1.SpentCards.Clear();
        if (you.TryGetProperty("spent", out JsonElement spent) && spent.ValueKind == JsonValueKind.Array)
            foreach (JsonElement c in spent.EnumerateArray()) _player1.SpentCards.Add(CardFor(c, keepFlip: false));

        // Their hand: face-down stand-ins, as many as they hold.
        int theirCount = OnlineService.Int(them, "handCount");
        while (_onlineHiddenHand.Count < theirCount) _onlineHiddenHand.Add(new Card(0, CardType.Modifier) { IsHidden = true });
        _player2.Modifiers.Clear();
        for (int i = 0; i < theirCount; i++) _player2.Modifiers.Add(_onlineHiddenHand[i]);

        ApplySide(_player1, you);
        ApplySide(_player2, them);
        _gameState.SetsWonPlayer1 = OnlineService.Int(you, "wins");
        _gameState.SetsWonPlayer2 = OnlineService.Int(them, "wins");

        _onlineYouCanAct = OnlineService.Bool(you, "canAct");
        _onlineYouPlayedEffect = OnlineService.Bool(you, "playedEffect");
        _onlineDeckCounts = null; // a server older than the deck odds sends none: the odds just don't open
        if (you.TryGetProperty("deckCounts", out JsonElement dc) && dc.ValueKind == JsonValueKind.Array
            && dc.GetArrayLength() == 10)
        {
            _onlineDeckCounts = new int[10];
            int i = 0;
            foreach (JsonElement n in dc.EnumerateArray()) _onlineDeckCounts[i++] = n.GetInt32();
        }
        _onlineRecallLocked = you.TryGetProperty("recallLocked", out JsonElement rl) && rl.ValueKind == JsonValueKind.Number
            && _onlineCards.TryGetValue(rl.GetInt32(), out Card locked) ? locked : null;

        long msLeft = s.TryGetProperty("turnMsLeft", out JsonElement t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
        _onlineTurnEndsAtMs = msLeft > 0 ? (long)Time.GetTicksMsec() + msLeft : 0;
        _onlineShownSeconds = -1;

        bool themConnected = them.TryGetProperty("connected", out JsonElement tc) && tc.ValueKind == JsonValueKind.True;
        if (_onlineThemConnected && !themConnected) _ui.Toasts.ShowEffectBanner($"{_player2.PlayerName} lost connection - waiting for them");
        _onlineThemConnected = themConnected;

        // What just happened across the table, shown the way the offline game shows it.
        if (s.TryGetProperty("event", out JsonElement ev) && ev.ValueKind == JsonValueKind.Object)
            ShowOnlineEvent(ev, youBefore, themBefore, handsBefore, handsTraded);

        _ui.Refresh();

        if (DebugAutoplay && _onlineYouCanAct) Callable.From(AutoplayStep).CallDeferred();
    }

    /// Debug builds only ("-- --online-autoplay"): presses this phone's own buttons the way a
    /// player would - tap a Modifier twice to play it, Draw Card, Hold - so a test match exercises
    /// exactly the paths a finger does.
    private static readonly bool DebugAutoplay =
        OS.IsDebugBuild() && Array.IndexOf(OS.GetCmdlineUserArgs(), "--online-autoplay") >= 0;

    private async void AutoplayStep()
    {
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree() || !OnlineCanAct(_player1)) return;
        int score = _player1.CurrentScore, target = _gameState.TargetScore;

        // A +/- card is flipped when its minus is the one that helps: the Flip Value path.
        foreach (Card card in _player1.Modifiers.ToArray())
        {
            if (card.Effect != CardEffect.None) continue;
            int as_is = score + card.Value;
            int flipped = score - card.Value;
            bool good = as_is >= target - 2 && as_is <= target;
            bool goodFlipped = card.CanFlipValue && flipped >= target - 2 && flipped <= target;
            if (!good && !goodFlipped) continue;
            OnModifierCardPressed(_player1, card);           // pick it up
            if (!good) FlipSelectedValue(_player1);          // flip it
            OnModifierCardPressed(_player1, card);           // tap again: play
            return;
        }
        FinishTurn(_player1, hold: score >= target - 3 && score <= target);
    }

    private static int BoardCount(JsonElement side) =>
        side.TryGetProperty("board", out JsonElement b) && b.ValueKind == JsonValueKind.Array ? b.GetArrayLength() : 0;

    private void ApplySide(Player p, JsonElement side)
    {
        p.CurrentScore = OnlineService.Int(side, "score");
        p.IsHolding = OnlineService.Bool(side, "holding");
        p.HasEndedTurn = OnlineService.Bool(side, "ended");
        p.LastDrawnCard = side.TryGetProperty("lastDrawnId", out JsonElement d) && d.ValueKind == JsonValueKind.Number
            && _onlineCards.TryGetValue(d.GetInt32(), out Card drawn) ? drawn : null;
        p.LastPlayedModifier = side.TryGetProperty("lastPlayedId", out JsonElement m) && m.ValueKind == JsonValueKind.Number
            && _onlineCards.TryGetValue(m.GetInt32(), out Card played) ? played : null;
    }

    /// The local Card for a server card, made the first time it is seen.
    private Card CardFor(JsonElement c, bool keepFlip)
    {
        int id = c.GetProperty("id").GetInt32();
        int value = c.GetProperty("value").GetInt32();
        bool flip = OnlineService.Bool(c, "flip");
        string effectName = OnlineService.Str(c, "effect");
        bool main = OnlineService.Bool(c, "main");

        if (_onlineCards.TryGetValue(id, out Card card))
        {
            // Keep a +/- card's local orientation in your hand; anywhere else the server is right.
            if (!(keepFlip && card.CanFlipValue && Math.Abs(card.Value) == Math.Abs(value)) && card.Value != value)
            {
                card.Value = value;
                card.CardName = card.DisplayText;
            }
            card.IsHidden = false;
            return card;
        }

        CardEffect effect = CardEffect.None;
        if (!string.IsNullOrEmpty(effectName)) Enum.TryParse(effectName, out effect);
        card = new Card(value, main ? CardType.Main : CardType.Modifier, "", flip, effect);
        _onlineCards[id] = card;
        _onlineIdOf[card] = id;
        return card;
    }

    /// Makes this player's board match the server's, card by card. Returns true when a card on it
    /// changed face (a Copy).
    private bool SyncBoard(Player player, JsonElement board, Control container)
    {
        List<Card> incoming = new List<Card>();
        List<int> oldValues = new List<int>();
        foreach (JsonElement c in board.EnumerateArray())
        {
            int id = c.GetProperty("id").GetInt32();
            oldValues.Add(_onlineCards.TryGetValue(id, out Card known) ? known.Value : int.MinValue);
            incoming.Add(CardFor(c, keepFlip: false));
        }

        // Gone from the board (a Veto): it burns where it lay.
        foreach (Card card in player.ActiveCardsOnBoard.ToArray())
        {
            if (incoming.Contains(card)) continue;
            player.ActiveCardsOnBoard.Remove(card);
            _ui.BurnCardView(card, container);
        }

        bool faceChanged = false;
        float delay = 0f;
        for (int i = 0; i < incoming.Count; i++)
        {
            Card card = incoming[i];
            if (player.ActiveCardsOnBoard.Contains(card))
            {
                if (oldValues[i] != int.MinValue && oldValues[i] != card.Value)
                {
                    _ui.RefreshCardFace(card, container);
                    faceChanged = true;
                }
                continue;
            }
            player.ActiveCardsOnBoard.Add(card);
            _ui.InstantiateCardView(card, container, delay);
            if (card.Type == CardType.Main) delay += 0.18f; // a two-card opening reads as two cards
        }
        return faceChanged;
    }

    private bool HandIdsChanged(List<Card> before, List<Card> after)
    {
        if (before.Count != after.Count) return true;
        for (int i = 0; i < before.Count; i++) if (before[i] != after[i]) return true;
        return false;
    }

    /// The hand lost one card (played) and nothing else changed - not a trade.
    private static bool SameIdsMinusOne(List<Card> before, List<Card> after)
    {
        if (after.Count > before.Count) return false;
        foreach (Card c in after) if (!before.Contains(c)) return false;
        return true;
    }

    private void ShowOnlineEvent(JsonElement ev, int youBefore, int themBefore,
        (List<TableMoments.HandCard> P1, List<TableMoments.HandCard> P2) handsBefore, bool handsTraded)
    {
        string effectName = OnlineService.Str(ev, "effect");
        bool byYou = OnlineService.Bool(ev, "byYou");
        if (!Enum.TryParse(effectName, out CardEffect effect)) return;

        Player owner = byYou ? _player1 : _player2;
        Player target = byYou ? _player2 : _player1;
        int ownerBefore = byYou ? youBefore : themBefore;
        int targetBefore = byYou ? themBefore : youBefore;

        switch (effect)
        {
            case CardEffect.Shave:
                _ui.PlayShave(target, targetBefore);
                break;
            case CardEffect.TradeTotals:
                _ui.PlayTradeTotals(owner, ownerBefore, target, targetBefore);
                break;
            case CardEffect.TradeHands:
                if (handsTraded) _ui.PlayTradeHands(handsBefore);
                break;
            case CardEffect.Copy:
                _ui.PlayCopy(owner, ownerBefore, target, BoardOf(owner), BoardOf(target));
                break;
        }

        // Said in this phone's words: "You" and the opponent's name, whichever side played it.
        string label = CardEffects.Label(effect);
        _ui.Toasts.ShowEffectBanner(byYou ? $"You played {label}" : $"{_player2.PlayerName} played {label}");
    }

    private void Prompts_HideSetInfo()
    {
        Label setInfo = _ui.Layout?.SetInfoLabel;
        if (setInfo != null) setInfo.AddThemeFontSizeOverride("font_size", TableUi.SetInfoFont);
    }

    private void OnlineSetEnd(JsonElement msg)
    {
        string winner = OnlineService.Str(msg, "winner");
        string title = winner switch
        {
            "you" => "You won the set!",
            "them" => $"{_player2.PlayerName} won the set!",
            _ => "The set is a tie",
        };
        ClearSelections();
        _ui.Toasts.ShowEffectBanner(title);
        Label setInfo = _ui.Layout?.SetInfoLabel;
        if (setInfo != null)
        {
            setInfo.Text = title;
            setInfo.AddThemeFontSizeOverride("font_size", TableUi.SetInfoFont);
            setInfo.Visible = true;
        }
    }

    private void OnlineMatchEnd(JsonElement msg)
    {
        _onlineOver = true;
        _onlinePhase = "over";
        DetachOnline();
        bool won = OnlineService.Str(msg, "winner") == "you";
        string reason = OnlineService.Str(msg, "reason");
        string why = reason switch
        {
            "forfeit" => won ? $"{_player2.PlayerName} left the match." : "You left the match.",
            "timeouts" => won ? $"{_player2.PlayerName} ran out of time." : "You missed three turns in a row.",
            "disconnect" => won ? $"{_player2.PlayerName} lost connection." : "You lost connection.",
            _ => string.Empty,
        };
        string title = won ? "You Win!" : $"{_player2.PlayerName} Wins!";
        _ui.Refresh();
        _prompts.ShowSetEnd(title, why, "Play Again",
            () => { Menus.PendingOnlineQueue = true; RestartToMenu(); },
            "Return to Main Menu", RestartToMenu);
    }

    // ------------------------------------------------------------------
    // Presses, sent to the server
    // ------------------------------------------------------------------

    private bool OnlineCanAct(Player player) =>
        _online && !_onlineOver && player == _player1 && _onlinePhase == "playing"
        && _onlineYouCanAct && !_onlineAwaiting && !_prompts.Showing;

    private void OnlineFinishTurn(bool hold)
    {
        if (!OnlineCanAct(_player1)) return;
        SetSelection(_player1, null);
        if (OnlineService.Instance?.SendMessage(new { type = hold ? "hold" : "draw" }) == true)
            _onlineAwaiting = true;
        _ui.Refresh();
    }

    private void OnlinePlay(Card card, Card chosen = null)
    {
        if (!OnlineCanAct(_player1) || !_onlineIdOf.TryGetValue(card, out int id)) return;
        int? chosenId = chosen != null && _onlineIdOf.TryGetValue(chosen, out int cid) ? cid : null;
        bool sent = OnlineService.Instance?.SendMessage(new
        {
            type = "play",
            cardId = id,
            value = card.Effect == CardEffect.None ? card.Value : (int?)null,
            chosenId,
        }) == true;
        if (sent) _onlineAwaiting = true;
        SetSelection(_player1, null);
        _ui.Refresh();
    }

    private bool OnlineCanPlayEffect(Player owner, Card card) =>
        owner == _player1 && card != null && card.Effect != CardEffect.None
        && CardEffects.Implemented(card.Effect) && !_onlineYouPlayedEffect
        && CardEffects.CanPlay(card, _player1, _player2, _gameState.TargetScore);

    private bool OnlineRecallLocked(Card card) => card != null && card == _onlineRecallLocked;

    /// Leaving the table mid-match is a forfeit, said plainly by the server to both sides.
    private void OnlineLeave()
    {
        if (_online && !_onlineOver) OnlineService.Instance?.SendMessage(new { type = "forfeit" });
        _onlineOver = true;
        DetachOnline();
    }

    /// The middle of the table, online: the seconds left in the turn while it is running.
    private string OnlineHeader()
    {
        if (_onlinePhase != "playing" || _onlineTurnEndsAtMs <= 0) return string.Empty;
        long ms = _onlineTurnEndsAtMs - (long)Time.GetTicksMsec();
        int secs = (int)Math.Ceiling(Math.Max(0, ms) / 1000.0);
        return $"{secs}";
    }

    /// Ticks the turn clock in the middle of the table once a second.
    public override void _Process(double delta)
    {
        if (!_online || _onlineOver || _setOverPending || _onlinePhase != "playing") return;
        if (_onlineTurnEndsAtMs <= 0) return;
        long ms = _onlineTurnEndsAtMs - (long)Time.GetTicksMsec();
        int secs = (int)Math.Ceiling(Math.Max(0, ms) / 1000.0);
        if (secs == _onlineShownSeconds) return;
        _onlineShownSeconds = secs;
        Label setInfo = _ui.Layout?.SetInfoLabel;
        if (setInfo == null) return;
        setInfo.Text = $"{secs}";
        setInfo.AddThemeFontSizeOverride("font_size", TableUi.SetInfoFont);
        setInfo.AddThemeColorOverride("font_color", secs <= 10 ? OverlayUi.Warning : OverlayUi.Ink);
        setInfo.Visible = true;
    }
}
