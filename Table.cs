using Godot;
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
/// one moment a card becomes visible goes out through ITableHost.ShowDrawnCard.
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
        _host.HandsDealt(introduceCards: true);
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

        // Copy names this exact card. On a two-card opening deal that is the SECOND one, because
        // this runs once per card and the last write wins - which is the right answer (it is the
        // most recent draw), but it is the kind of thing that should be written down rather than
        // discovered.
        player.LastDrawnCard = drawnMainCard;

        GD.Print($"{player.PlayerName} drew a {cardValue}. Score: {player.CurrentScore} "
               + $"({DeckOf(player).Count} left in their deck)");

        _host.ShowDrawnCard(player, drawnMainCard, delay);

        // Going over the target here is NOT a bust yet - the player may still play a minus card
        // before ending the turn. Busts are only decided in ResolveTurn.
    }
}
