using System;
using System.Collections.Generic;

/// What the table needs from the game around it, and nothing more.
///
/// The same shape as IBotTable (see Bot.cs): GameManager implements it explicitly, so none of
/// these can be called from inside GameManager by accident and the table's reach stays exactly as
/// wide as this list reads. The few members the two contracts share are deliberately repeated
/// rather than hoisted into a common base - a role's surface is easier to judge when the whole of
/// it is in one place.
public interface ITableHost
{
    Player Player1 { get; }
    Player Player2 { get; }
    GameState State { get; }
    Random Rng { get; }

    /// The ladder run this match belongs to, or null (local 2-player, or a one-off solo match).
    RunData Run { get; }

    bool VsBot { get; }
    bool LocalSpecials { get; }

    /// The tutorial's staged first set: a fixed opening off the top of the deck and a fixed hand,
    /// so the lesson can promise what the next card will be.
    bool TutorialStaged { get; }
    IReadOnlyList<int> TutorialOpening { get; }
    IReadOnlyList<int> TutorialModifiers { get; }

    /// The tutorial's second lesson (2026-10-06): going over is not a bust while you can still play
    /// a minus card. While it is waiting to be shown, every set after the first stacks Player 1's
    /// first draws (TutorialOverDraws, in draw order: 10 and 1 on the opening deal, then 10 = 21).
    bool TutorialOverLesson { get; }
    IReadOnlyList<int> TutorialOverDraws { get; }

    /// The flip lesson's match (after the first Market visit put a +/-1 in the deck): the hand is
    /// sure to hold a +/-1, and the first set is stacked so Player 1 reaches one over the target
    /// (TutorialFlipDraws, in draw order; empty when the target leaves no clean way to stack it).
    bool TutorialFlipLesson { get; }
    IReadOnlyList<int> TutorialFlipDraws { get; }

    /// The specials a local 2-player hand may be dealt: only the ones met in single player.
    List<CardEffect> UnlockedLocalSpecials();

    /// The bot builds its own hand. The ladder's recipes are the opponent's business, not the
    /// deck's - see Bot.DealHand.
    void DealBotHand();

    /// A card has landed on a player's board. WHERE it lands and how it flies there is the UI's
    /// business; the table only says that it did.
    void ShowDrawnCard(Player player, Card card, float delay);

    /// Fresh hands are on the table: drop any card that was picked up, and - except for the
    /// tutorial's staged hand, which is itself the lesson - introduce the cards the player has
    /// not met before.
    void HandsDealt(bool introduceCards);
}

/// The cards: the shared main deck, the hands dealt from it, what each player has drawn this
/// turn, and the two pieces of per-turn bookkeeping that hang off a card being played.
///
/// The line against TableUi (and it is the line worth holding): this class knows WHERE the cards
/// are, never what they look like. Nothing here touches a node, a texture or an animation - the
/// one moment a card becomes visible goes out through ITableHost.ShowDrawnCard. It does not use
/// Godot at all, which is what lets tests/AimFor20.Tests drive it through a fake host.
public sealed class Table
{
    private readonly ITableHost _host;

    public Table(ITableHost host) => _host = host;

    private Player P1 => _host.Player1;
    private Player P2 => _host.Player2;

    // ------------------------------------------------------------------
    // Modifier hands
    //
    // Each match deals every player a fresh random hand: non-zero values in -4..+4, and a 1-in-10
    // chance for any of them to be a "+/-" (flip) card the player can swap between plus and minus
    // before committing it. Cards are spent for the whole match, not the set.
    // ------------------------------------------------------------------
    public const int HandSize = 4;
    public const int MaxModifierMagnitude = 4;
    public const double FlipValueChance = 0.10;

    /// At or above this target the opening deal is two cards, because two cards cannot exceed 20.
    private const int OpeningDoubleDealTarget = 20;

    /// How long the second opening card waits behind the first, so the pair reads as two cards.
    private const float OpeningDealStagger = 0.18f;

    /// The next turn is this set's opening one - see DealTurn.
    private bool _firstTurnOfSet;

    // One opponent-facing card per side per turn. Two in one turn cannot be read, however well
    // they animate - see claude/stage-ladder-spec.md.
    private bool _p1PlayedEffect;
    private bool _p2PlayedEffect;

    /// A card just brought back by Recall cannot be played until the NEXT turn. This is the whole
    /// of Recall's design: without it the card is "an extra modifier exactly when I need one",
    /// which is a rescue, and stage 8 is meant to be the rung that stops being about rescues.
    ///
    /// It needs its own store because the played-an-effect flags do NOT cover it - those gate a
    /// second EFFECT card, and a recalled +4 is a plain modifier.
    private Card _p1RecallLock;
    private Card _p2RecallLock;

    /// How many cards are left in this player's own deck (pass 43: one deck each). The draw log
    /// reads it; so would a counting aid.
    public int Remaining(Player owner) => DeckOf(owner).Count;

    /// How many of each value are still in this player's deck: [0] is the ones, [9] the tens. The
    /// deck odds read it (tap your deck). Nothing here is secret - every card drawn lands face up
    /// on the board, so a player counting carefully knows exactly this.
    public int[] DeckCounts(Player owner)
    {
        int[] counts = new int[10];
        foreach (int value in DeckOf(owner))
            if (value >= 1 && value <= 10) counts[value - 1]++;
        return counts;
    }

    // ------------------------------------------------------------------
    // Per-turn bookkeeping
    //
    // Both of these used to be a pair of _p1X / _p2X fields read through a ternary at every use
    // site. They are card facts about a turn, so they live with the cards, and asking for them by
    // player is one thing to get right instead of six.
    // ------------------------------------------------------------------

    /// Has this player already reached across the table this turn?
    public bool HasPlayedEffect(Player owner) => owner == P1 ? _p1PlayedEffect : _p2PlayedEffect;

    public void NoteEffectPlayed(Player owner)
    {
        if (owner == P1) _p1PlayedEffect = true;
        else _p2PlayedEffect = true;
    }

    /// True when this card came back through a Recall during THIS turn and so cannot be played
    /// yet. Applies to both sides; the bot is bound by it exactly as the player is.
    public bool IsRecallLocked(Player owner, Card card) =>
        card != null && card == (owner == P1 ? _p1RecallLock : _p2RecallLock);

    public void LockRecall(Player owner, Card card)
    {
        if (owner == P1) _p1RecallLock = card;
        else _p2RecallLock = card;
    }

    /// The other side of the table.
    public Player OpponentOf(Player player) => player == P1 ? P2 : P1;

    // ------------------------------------------------------------------
    // Face-down cards (the hidden-card rule, 2026-10-09)
    //
    // After Chuck's playtest: "I can see what the other player has ... so I know exactly what I
    // have to do." On a rung that hides (GameState.HiddenOpponent - the boss and Endless) the bot's
    // first two cards each set land face up, so there is something to read, and everything after
    // them - its draws and the plain Modifiers it plays - lands face down, as does its hand. The
    // set's end turns the board over.
    //
    // What a face-down card is NOT is a secret from the rules: it is a Card.IsHidden flag the
    // views draw as a back, so the bot, the scoring and every effect read the real values. The
    // only rule that bends is what the PLAYER is told - the legality and wording of effects aimed
    // at a face-down board (CardEffects.CanPlay, EffectPreview, TryPlayEffect's narration).
    // ------------------------------------------------------------------

    /// How many of the bot's cards each set are face up before the rest start landing face down.
    public const int FaceUpOpeningCards = 2;

    /// This match hides the bot's cards. Never in local 2-player, whatever the state says.
    public bool HidesOpponent => _host.VsBot && _host.State.HiddenOpponent;

    /// The set is over: the bot's board turns face up so the result can be read. Its hand stays
    /// face down - those cards are still to be played this match. Returns the cards that turned.
    public List<Card> RevealOpponentBoard() => HidesOpponent ? P2.RevealBoard() : new List<Card>();

    private static void Reveal(Card card, List<Card> turned)
    {
        if (card == null || !card.IsHidden) return;
        card.IsHidden = false;
        turned.Add(card);
    }

    // ------------------------------------------------------------------
    // Effect cards: one set of rules for both sides
    //
    // The bot reaches these through IBotTable, the player through their hand. Neither gets its
    // own rules. What a play LOOKS like (the card landing, a burned Veto, the banner) is the
    // caller's business; what it DOES to the cards and the turn is decided here.
    // ------------------------------------------------------------------

    /// Can this player reach across the table with this card right now? Legality is the card's
    /// own business (CardEffects.CanPlay); the once-per-turn limit is the turn's.
    public bool CanPlayEffect(Player owner, Card card)
    {
        if (card == null || card.Effect == CardEffect.None) return false;
        if (!CardEffects.Implemented(card.Effect)) return false;
        if (HasPlayedEffect(owner)) return false;
        return CardEffects.CanPlay(card, owner, OpponentOf(owner), _host.State.TargetScore);
    }

    /// WHY this card cannot be played right now, as a sentence, or null when it can be. The
    /// once-per-turn limit belongs to the TURN, so it is answered here; every other rule is the
    /// card's own and is answered by CardEffects.
    ///
    /// The same sentence is what the status line shows and what explains the greyed-out Play
    /// button - a rule the player cannot see is a rule they cannot learn.
    public string EffectRefusal(Player owner, Card card)
    {
        if (card == null || card.Effect == CardEffect.None) return null;
        if (HasPlayedEffect(owner)) return "one card across the table per turn, and you have played yours.";
        return CardEffects.RefusalReason(card, owner, OpponentOf(owner), _host.State.TargetScore);
    }

    /// What an effect play did, for the caller to show.
    public readonly struct EffectPlay
    {
        public readonly CardEffects.EffectResult Result;
        public readonly Player Target;
        /// Whose board the effect card itself lands on (CardEffects.LandsOnTarget).
        public readonly Player BoardOwner;
        /// The card a Veto destroyed, or null.
        public readonly Card Destroyed;
        /// The target's turn was re-opened (the answering rule).
        public readonly bool Reopened;
        /// Face-down cards this play turned face up (the hidden-card rule), for the table to flip.
        public readonly List<Card> Revealed;
        /// What to say about it - the result's narration, unless that would read out a face-down
        /// number (see TryPlayEffect).
        public readonly string Narration;

        public EffectPlay(CardEffects.EffectResult result, Player target, Player boardOwner, Card destroyed, bool reopened,
                          List<Card> revealed, string narration)
        {
            Result = result;
            Target = target;
            BoardOwner = boardOwner;
            Destroyed = destroyed;
            Reopened = reopened;
            Revealed = revealed ?? new List<Card>();
            Narration = narration ?? result.Narration;
        }
    }

    /// Spends an effect card and applies it. Returns false without touching anything if the play
    /// was not legal, so a card is never silently eaten.
    /// `chosen` is Recall's only: which spent card comes back. The player picks it in the Recall
    /// overlay, the bot in its PickRecallTarget; every other effect ignores it.
    public bool TryPlayEffect(Player owner, Card card, Card chosen, out EffectPlay play)
    {
        play = default;
        if (!CanPlayEffect(owner, card)) return false;
        if (!owner.Modifiers.Remove(card)) return false;

        Player target = OpponentOf(owner);

        // Veto destroys a card that is already face-up on the target's board, and Resolve clears
        // LastPlayedModifier - so the card is grabbed here, or the caller has nothing to burn.
        Card destroyed = (card.Effect == CardEffect.Veto) ? target.LastPlayedModifier : null;

        // Taken before Resolve moves anything: which card Copy reads, the drawn card it rewrites,
        // and whether the card Recall brings back was one played face down.
        Card copySource = (card.Effect == CardEffect.Copy) ? CardEffects.CopySource(target) : null;
        bool recalledHidden = chosen != null && chosen.IsHidden;
        bool targetHidden = target.HasHiddenCards;

        CardEffects.EffectResult result = CardEffects.Resolve(card, owner, target, _host.State.TargetScore, chosen);
        if (!result.Applied)
        {
            owner.Modifiers.Add(card); // put it back rather than lose it to a rule we misread
            return false;
        }

        NoteEffectPlayed(owner);

        // The hidden-card rule. An effect card is a public act - it reaches across the table, so it
        // is played face up even out of a face-down hand - and some of them name a face-down card
        // outright. Those turn over; everything else stays down and is narrated without numbers.
        List<Card> revealed = new List<Card>();
        string narration = null;
        if (HidesOpponent)
        {
            card.IsHidden = false;
            switch (card.Effect)
            {
                case CardEffect.Copy:
                    // Yours copies theirs: the card it read is named. Theirs copies yours: their
                    // drawn card now shows your number, so there is nothing left to hide on it.
                    Reveal(owner == P1 ? copySource : owner.LastDrawnCard, revealed);
                    if (owner == P2)
                        narration = $"{Speech.Does(owner.PlayerName, "plays", "play")} Copy - their drawn card becomes a copy of your {owner.LastDrawnCard?.Value}";
                    break;

                case CardEffect.TradeTotals:
                    // Your score is now theirs, so their total is no secret: the whole board turns.
                    foreach (Card c in P2.ActiveCardsOnBoard.ToArray()) Reveal(c, revealed);
                    break;

                case CardEffect.TradeHands:
                    // Their hand is yours now. (Yours, now theirs, was never face down.)
                    foreach (Card c in P1.Modifiers) Reveal(c, revealed);
                    break;

                case CardEffect.Veto:
                    // The burned card is shown as it goes, so the player sees what they destroyed -
                    // but not the score it leaves, which the rest of their board still hides.
                    Reveal(destroyed, revealed);
                    if (owner == P1 && targetHidden && destroyed != null)
                        narration = $"{Speech.Does(owner.PlayerName, "plays", "play")} Veto - destroys {Speech.Possessive(target.PlayerName)} "
                                  + $"{(destroyed.Value > 0 ? "+" : "")}{destroyed.Value}"
                                  // Still the old hold here: the release is applied just below.
                                  + (target.IsHolding ? " - and they are no longer holding" : string.Empty);
                    break;

                case CardEffect.Shave:
                    // (On the target it changed nothing, and Resolve already says so in words.)
                    if (owner == P1 && targetHidden && target.CurrentScore < _host.State.TargetScore)
                        narration = $"{Speech.Does(owner.PlayerName, "plays", "play")} Shave - {Speech.Possessive(target.PlayerName)} score goes down by 1";
                    break;

                case CardEffect.Recall:
                    if (owner == P2 && recalledHidden)
                        narration = $"{Speech.Does(owner.PlayerName, "plays", "play")} Recall - takes back a Modifier";
                    break;
            }
        }

        // Recall's card is back in hand but dead until the next turn. Set AFTER Resolve, because
        // Resolve is what moved it out of the spent pile.
        if (card.Effect == CardEffect.Recall) LockRecall(owner, chosen);

        // THE ANSWERING RULE. A card played at you re-opens your turn for this turn, so you always
        // get a say - unless you are holding, which is the locked state Shave exists to punish.
        //
        // ReleasesHold is the one exception to that exception (Veto, pass 7): it un-locks a score
        // that was already committed, so the target is re-opened even from a hold. Neither flag
        // ever deals a card - a re-opened player plays a Modifier, holds, or ends the turn.
        if (result.ReleasesHold) target.IsHolding = false;
        bool reopened = result.ReopensTarget && !target.IsHolding;
        if (reopened) target.HasEndedTurn = false;

        // The two effects that change the other player's score sit in THEIR board, so the number
        // that moved and the card that moved it are in the same place.
        Player boardOwner = CardEffects.LandsOnTarget(card.Effect) ? target : owner;
        boardOwner.ActiveCardsOnBoard.Add(card);

        play = new EffectPlay(result, target, boardOwner, destroyed, reopened, revealed, narration);
        return true;
    }

    /// The status line for a picked-up effect card: the teaching moment a plain Modifier gets from
    /// its score preview, for a card whose arithmetic happens on the OTHER side of the table. Says what it would do, or says it cannot be played right now - never a
    /// sum of this player's score and a number that is not going to be added to it.
    public string EffectPreview(Player player, Card picked)
    {
        Player other = OpponentOf(player);
        string name = CardEffects.Label(picked.Effect);

        string refusal = EffectRefusal(player, picked);
        if (refusal != null) return $"{name}: {refusal}";

        // Face-down cards are previewed without their numbers (the hidden-card rule).
        bool hidden = other.HasHiddenCards;

        switch (picked.Effect)
        {
            case CardEffect.Copy:
            {
                int mine = player.LastDrawnCard?.Value ?? 0;
                Card source = CardEffects.CopySource(other);
                if (source != null && source.IsHidden) return $"Your {mine} becomes a copy of their face-down card";
                int theirs = source?.Value ?? 0;
                int after = player.CurrentScore - mine + theirs;
                return $"Your {mine} becomes a {theirs}: {player.CurrentScore} to {after}";
            }
            case CardEffect.Shave:
                if (hidden) return $"{other.PlayerName}: their score goes down by 1, unless they are on the target";
                return $"{other.PlayerName}: {other.CurrentScore} - 1 = {other.CurrentScore - 1}";
            case CardEffect.TradeTotals:
                if (hidden) return $"Trade Totals: your {player.CurrentScore} for their hidden total";
                return $"Trade Totals: {player.CurrentScore} and {other.CurrentScore} change places";
            case CardEffect.TradeHands:
                return $"Trade Hands: your {player.Modifiers.Count - 1} Modifiers for their {other.Modifiers.Count}";
            case CardEffect.Recall:
                return "Take a Modifier back - you can play it from your next turn";
            case CardEffect.Veto:
            {
                // EffectRefusal returned null above, so CanPlay said yes, so LastPlayedModifier is
                // a plain modifier they played this turn. Named with its sign, because vetoing a
                // minus card sends their score UP and the preview has to show that honestly.
                Card theirs = other.LastPlayedModifier;
                if (theirs.IsHidden) return "Destroy the face-down Modifier they just played";
                string theirSign = theirs.Value < 0 ? "-" : "+";
                return $"Destroy their {theirSign}{Math.Abs(theirs.Value)}: "
                     + $"{other.CurrentScore} back to {other.CurrentScore - theirs.Value}";
            }
        }

        return name;
    }

    /// A fresh set: a fresh forty for each player, and the next turn is this set's opening one.
    public void StartSet()
    {
        Shuffle();
        _firstTurnOfSet = true;
    }

    /// The flat random hand local 2-player and a runless solo match still deal, and the shape the
    /// bot measures its own recipes in.
    public void DealPlainHand(Player player) =>
        player.DealRandomModifiers(_host.Rng, HandSize, FlipValueChance, MaxModifierMagnitude);

    // ------------------------------------------------------------------
    // The main decks (playtest feedback, 2026-09-15; one deck EACH since pass 43)
    //
    // It used to be a bare Next(1, 11) on every draw: an infinite stream with no memory, where
    // four 10s in a row is possible and the player has no way to tell bad luck from the game
    // cheating. It is now a real object - four copies each of 1 to 10, forty cards, shuffled - and
    // that is Pazaak's own main deck.
    //
    // A finite deck can be COUNTED, which is a real skill the 55+ group already owns and costs a
    // five-year-old nothing.
    //
    // Pass 43: each player has their OWN forty, on their own side of the table. The reason is
    // online play: a deck of your own is where your customisation shows (its back, later perhaps
    // more), and two players' decks cannot be one pile. Counting survives: every card you draw
    // lands face up on your board, so your own deck is exactly as countable as the shared one was
    // - you just count one deck instead of both players' draws out of one.
    //
    // SHUFFLED EVERY SET, not every match: Pazaak's rule, and it keeps each set a clean
    // counting problem rather than a match-long bookkeeping chore.
    // ------------------------------------------------------------------

    private const int MainDeckCopies = 4; // of each value 1-10, so forty cards

    private readonly List<int> _p1Deck = new List<int>();
    private readonly List<int> _p2Deck = new List<int>();

    private List<int> DeckOf(Player owner) => owner == P2 ? _p2Deck : _p1Deck;

    private void Shuffle()
    {
        ShuffleDeck(_p1Deck);
        ShuffleDeck(_p2Deck);

        // The staged opening for the tutorial's first set. Teaching.Opening is written for the
        // turn order P1, P2, P1, P2 drawing off the END of one pile, so its entries alternate
        // between the two players: from the end, Player 1, Player 2, Player 1, Player 2. Each
        // value is REMOVED from the shuffled remainder before being appended, so each deck still
        // holds exactly four of every value and the rest of the set is as random as any other.
        if (_host.TutorialStaged && _host.State.IsFirstSet)
        {
            IReadOnlyList<int> opening = _host.TutorialOpening;
            for (int i = 0; i < opening.Count; i++)
            {
                bool forP1 = (opening.Count - 1 - i) % 2 == 0;
                List<int> deck = forP1 ? _p1Deck : _p2Deck;
                deck.Remove(opening[i]);
                deck.Add(opening[i]);
            }
        }
        else if (_host.TutorialOverLesson && !_host.State.IsFirstSet)
        {
            // The second lesson's set: Player 1 draws off the END of their own pile, so the first
            // draw is appended last. Removed first, so the deck still holds four of every value.
            IReadOnlyList<int> draws = _host.TutorialOverDraws;
            for (int i = draws.Count - 1; i >= 0; i--)
            {
                _p1Deck.Remove(draws[i]);
                _p1Deck.Add(draws[i]);
            }
        }
        else if (_host.TutorialFlipLesson && _host.State.IsFirstSet)
        {
            IReadOnlyList<int> draws = _host.TutorialFlipDraws;
            for (int i = draws.Count - 1; i >= 0; i--)
            {
                _p1Deck.Remove(draws[i]);
                _p1Deck.Add(draws[i]);
            }
        }
    }

    private void ShuffleDeck(List<int> deck)
    {
        deck.Clear();
        for (int value = 1; value <= 10; value++)
            for (int copy = 0; copy < MainDeckCopies; copy++)
                deck.Add(value);

        // Fisher-Yates, off the table's one Random, the same stream every other deal uses.
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = _host.Rng.Next(i + 1);
            int swap = deck[i];
            deck[i] = deck[j];
            deck[j] = swap;
        }
    }

    /// The top card of this player's own deck. A deck cannot actually run out: one player drawing
    /// until they bust takes at most a handful of cards from forty. The reshuffle is here anyway,
    /// so a future rule change cannot turn that arithmetic into a crash.
    private int DrawValue(Player owner, bool opening = false)
    {
        List<int> deck = DeckOf(owner);
        if (deck.Count == 0) ShuffleDeck(deck);

        SteerTopCard(owner, deck, opening);

        int last = deck.Count - 1;
        int value = deck[last];
        deck.RemoveAt(last);
        return value;
    }
    /// The Bronze rung's helping hand - see DrawAssist. Only ever reorders the deck.
    private void SteerTopCard(Player owner, List<int> deck, bool opening)
    {
        if (_host.TutorialStaged && _host.State.IsFirstSet) return; // the staged opening is the lesson
        if (owner == P1 && _host.TutorialOverLesson) return;        // ...and so is the second lesson's
        if (owner == P1 && _host.TutorialFlipLesson && _host.State.IsFirstSet
            && _host.TutorialFlipDraws.Count > 0) return;          // ...and the flip lesson's

        int target = _host.State.TargetScore;
        int? pick = null;

        if (owner == P1 && DrawAssist.GuidesPlayer(_host.Run, _host.VsBot))
            pick = DrawAssist.PlayerPick(deck, P1, target, opening, card => IsRecallLocked(P1, card), _host.Rng);
        else if (owner == P2 && DrawAssist.BotAvoidsTarget(_host.Run, _host.VsBot))
            pick = DrawAssist.BotPick(deck, P2, target, _host.Rng);

        if (pick.HasValue) DrawAssist.StackTop(deck, pick.Value);
    }

    /// A fresh modifier hand for both players. Cards are spent for the whole match, so this runs
    /// once per match - not per set.
    ///
    /// In a run, Player 1's hand is drawn at random from the 12-card deck they built on the deck
    /// screen: the deck is chosen, the hand is not. Everywhere else (local 2-player, and the bot)
    /// the hand is dealt at random.
    public void DealMatchHands()
    {
        // The staged hand for the tutorial. The +4 is the lesson - it takes the staged opening of
        // 16 to exactly 20 - and the other three are there so the hand looks like a normal one.
        if (_host.TutorialStaged)
        {
            P1.Modifiers.Clear();
            foreach (int value in _host.TutorialModifiers)
                P1.Modifiers.Add(new Card(value, CardType.Modifier));

            P1.ResetForNewMatch();
            P2.ResetForNewMatch();
            _host.DealBotHand();
            _host.HandsDealt(introduceCards: false); // the staged hand is the lesson; it introduces itself
            return;
        }

        List<Card> runModifiers = _host.Run?.DrawMatchModifiers();
        if (runModifiers != null && runModifiers.Count > 0)
        {
            P1.Modifiers.Clear();
            P1.Modifiers.AddRange(runModifiers);
            if (_host.TutorialFlipLesson) EnsureFlipCard(P1);
        }
        else
        {
            DealPlainHand(P1);
            if (!_host.VsBot && _host.LocalSpecials) AddLocalSpecial(P1);
        }

        // The spent pile is per-MATCH, which is what makes Recall a card about a hand that has to
        // last every set rather than a card about this set. This is the only place it clears.
        P1.ResetForNewMatch();
        P2.ResetForNewMatch();

        _host.DealBotHand();

        // The hidden-card rule: the bot's hand is face down for the whole match, so the Modifiers
        // it plays from it land face down too. (An effect card turns up as it is played.)
        if (HidesOpponent)
            foreach (Card card in P2.Modifiers) card.IsHidden = true;

        _host.HandsDealt(introduceCards: true);
    }

    /// Player 1's first draws in the flip lesson's first set: the opening pair, then one more,
    /// landing exactly one over the target. Needs the two-card opening (target 20 or more).
    public static IReadOnlyList<int> FlipLessonDraws(int target) =>
        target >= OpeningDoubleDealTarget && target <= 29 ? new[] { 10, target - 19, 10 } : Array.Empty<int>();

    /// The flip lesson needs a +/-1 in the hand. Four of twelve are dealt at random, so when the
    /// draw missed it, the smallest plain plus card makes way for it.
    private static void EnsureFlipCard(Player player)
    {
        if (player.Modifiers.Count == 0) return;
        if (player.Modifiers.Exists(c => c.CanFlipValue && c.Effect == CardEffect.None)) return;
        int at = 0;
        for (int i = 0; i < player.Modifiers.Count; i++)
        {
            Card c = player.Modifiers[i];
            if (c.Effect != CardEffect.None || c.Value <= 0) continue;
            Card best = player.Modifiers[at];
            if (best.Effect != CardEffect.None || best.Value <= 0 || c.Value < best.Value) at = i;
        }
        player.Modifiers[at] = new ModifierDef(1, canFlipValue: true).ToCard();
    }

    /// Local 2-player with specials on: one of the four cards becomes a random finished special
    /// Modifier - the ladder's own recipe (three plain cards plus one effect), so a hand is never
    /// all tricks and no arithmetic. Each player rolls their own, so the two may differ.
    public void AddLocalSpecial(Player player)
    {
        // Only specials the player has met in single player (pass 21).
        List<CardEffect> wired = _host.UnlockedLocalSpecials();
        if (wired.Count == 0 || player.Modifiers.Count == 0) return;

        CardEffect effect = wired[_host.Rng.Next(wired.Count)];
        player.Modifiers[_host.Rng.Next(player.Modifiers.Count)] = CardEffects.Create(effect, _host.Rng);
        player.EnsureBothSigns(_host.Rng); // the replaced card may have been the only plus or minus
    }
    public void DealTurn()
    {
        P1.HasEndedTurn = false;
        P2.HasEndedTurn = false;

        // Both are about this turn only: who has drawn what, and who has already reached across
        // the table once.
        P1.LastDrawnCard = null;
        P2.LastDrawnCard = null;
        P1.LastPlayedModifier = null;
        P2.LastPlayedModifier = null;
        _p1PlayedEffect = false;
        _p2PlayedEffect = false;

        // A card recalled during the last turn becomes playable now. This is the ONLY place the
        // lock is lifted, so a recalled card is always dead for exactly one turn.
        _p1RecallLock = null;
        _p2RecallLock = null;

        // Two cards on the opening deal when the target is 20 or more (playtest, 2026-09-15).
        //
        // The "20 or greater" clause is not a guess: two main-deck cards are at most 10 + 10 = 20,
        // so at a target of 20 or above the opening deal provably CANNOT bust anyone, and at 20
        // exactly the best it can do is a perfect score. Below 20 - stages 5 and 6, target 18 - it
        // could, so those rungs keep the single opening card. That is not a wart; it is free
        // variety, and it lands on the two rungs that already feel different because the target
        // dropped.
        bool opening = _firstTurnOfSet;
        _firstTurnOfSet = false;
        int cards = (opening && _host.State.TargetScore >= OpeningDoubleDealTarget) ? 2 : 1;

        for (int i = 0; i < cards; i++)
        {
            // Both players' cards fly at once, as they always have; the second pair is staggered so
            // an opening deal reads as two cards rather than one thick one.
            float delay = i * OpeningDealStagger;
            DrawFor(P1, delay, opening);
            DrawFor(P2, delay, opening);
        }
    }

    private void DrawFor(Player player, float delay = 0f, bool opening = false)
    {
        if (player.IsHolding) return;

        int cardValue = DrawValue(player, opening);
        player.CurrentScore += cardValue;

        Card drawnMainCard = new Card(cardValue, CardType.Main, cardValue.ToString());
        player.ActiveCardsOnBoard.Add(drawnMainCard);

        // The hidden-card rule: past the bot's first two cards this set, its draws land face down.
        if (HidesOpponent && player == P2 && player.ActiveCardsOnBoard.Count > FaceUpOpeningCards)
            drawnMainCard.IsHidden = true;

        // Copy names this exact card. On a two-card opening deal that is the SECOND one, because
        // this runs once per card and the last write wins - which is the right answer (it is the
        // most recent draw), but it is the kind of thing that should be written down rather than
        // discovered.
        player.LastDrawnCard = drawnMainCard;

        _host.ShowDrawnCard(player, drawnMainCard, delay);

        // Going over the target here is NOT a bust yet - the player may still play a minus card
        // before ending the turn. Busts are only decided in ResolveTurn.
    }
}
