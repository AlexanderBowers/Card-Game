using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// What the bot is allowed to know about the table, and the only things it is allowed to do to it.
///
/// GameManager implements this and hands itself to Bot; Bot holds nothing else and has no
/// reference to the scene, the nodes or the UI. Keeping this list short is the whole point of the
/// split - it is what stops the opponent's reasoning drifting back into the rest of the game a
/// line at a time, which is how it ended up spread through a 5,700-line file in the first place.
public interface IBotTable
{
    /// The two players, named from the bot's side, because that is how its decisions are written.
    Player BotPlayer { get; }
    Player HumanPlayer { get; }

    GameState State { get; }

    /// The ladder run this match belongs to, or null: local 2-player and a one-off solo match
    /// have no run, and a bot with no run is never a boss fight.
    RunData Run { get; }

    /// The table's own Random. One stream for the whole game, so a seeded run stays reproducible.
    Random Rng { get; }

    /// The shape of a dealt hand. The bot builds the ladder's recipes itself (DealHand); these are
    /// the numbers the recipe is measured in.
    int HandSize { get; }
    int MaxModifierMagnitude { get; }

    bool VsBot { get; }
    bool LocalSpecials { get; }

    /// One opponent-facing card per side per turn; this is the bot's half of that.
    bool BotPlayedEffectThisTurn { get; }

    /// True while the first-launch tutorial is walking the player through a staged match. The
    /// bot stands still for the whole of it - see ProcessTurn.
    bool TutorialHoldsBot { get; }

    bool IsRecallLocked(Player owner, Card card);
    bool CanPlayEffect(Player owner, Card card);
    bool PlayEffectCard(Player owner, Card card, Card chosen = null);

    /// A plain modifier onto the table: the play, the card on the board, and the player meeting
    /// it. NOT the refresh - the bot asks for that itself, so the order it plays in stays visible
    /// where it plays.
    void PlayBotModifier(Card card);

    /// A flat random hand, the way local 2-player and a runless solo match still deal.
    void DealPlainHand(Player player);
    void AddLocalSpecial(Player player);

    void Refresh();
    void ResolveTurn();

    /// One beat of the bot's thinking-out-loud pacing. False afterwards means the scene was
    /// restarted or left while it waited, and the turn it was in the middle of no longer exists.
    Task<bool> Pause(double seconds);
}

/// The opponent: every decision it makes, and nothing else.
///
/// It was spread through GameManager until 2026-09-18, which made two things hard that matter for
/// a ladder whose whole promise is that the opponent changes as you climb: reading the decision
/// order end to end, and changing one tier's behaviour without wondering what else touched it.
///
/// The rule the whole file is written to: an effect card is only spent when it DECIDES something.
/// A Copy spent to move two points, or a Shave on a score the bot is already beating, is the
/// difference between a boss that feels hard and one that feels cheap.
public sealed class Bot
{
    private readonly IBotTable _table;

    /// The bot is "thinking" for this turn. The human is NOT locked meanwhile - both sides act at
    /// the same time - so this only ever stops two of the bot's own turns overlapping.
    private bool _thinking;

    /// Set when the bot's turn is re-opened WHILE that turn is still running (the player answers a
    /// card during one of its animation pauses). Calling ProcessTurn there would be swallowed by
    /// its own guard, and the tail of the in-flight turn would then end the bot's turn anyway - so
    /// the flag makes it go round again instead. Without this, whether the bot gets its answer
    /// depends on which pause the player happened to interrupt.
    private bool _reopenedMidTurn;

    public Bot(IBotTable table) => _table = table;

    /// The bot's own side of the table, and the player's. Written this way because every decision
    /// below reads as one player's reasoning about another.
    private Player Me => _table.BotPlayer;
    private Player You => _table.HumanPlayer;
    private GameState State => _table.State;

    /// A fresh turn: the last one's re-opening is spent. Called by the table from DealCards.
    public void ResetForTurn() => _reopenedMidTurn = false;

    /// The bot's turn was re-opened by an effect card the player answered with. Mid-turn the
    /// restart would hit ProcessTurn's own guard and vanish, so leave a note instead; between
    /// turns there is nothing in flight and it can simply go again.
    public void TurnReopened()
    {
        if (_thinking) _reopenedMidTurn = true;
        else ProcessTurn();
    }

    // How good the bot is
    //
    // Skill rides on the RANK, so it escalates on the same rhythm as the board colour and the
    // player feels the opponent change every second rung. See claude/ai-skill-tiers.md.

    public enum Skill
    {
        /// Bronze, Silver (stages 1-4). One card per turn, blind to your hand. The opponent that
        /// teaches the game: it never surprises you while you are still learning what a +/- does.
        Basic,

        /// Gold, Ruby (stages 5-8). It plays the BOARD: chains cards while each one improves its
        /// position, which is what lets it play two minus cards to climb back under a bust.
        Chains,

        /// Obsidian (stages 9-10). It plays YOU: reads your hand to decide whether its own score
        /// is actually safe, and weighs the match score when taking a risk.
        Reads,
    }

    /// At most this many ordinary cards in one turn, once the bot chains. The cap is the point:
    /// an unbounded loop empties the hand in a single turn and reads as a machine having a fit.
    private const int MaxAiChainedCards = 3;

    /// Trade Hands is a bet on the sets still to come, so the bot only makes it once its OWN
    /// hand is spent - it must be left holding at most this many cards after the trade card goes.
    /// Without this floor it fires on the first deal of the match, when both hands are full and
    /// spending a card to gain one is a swap for its own sake.
    private const int MaxModifiersToTradeAway = 1;

    private Skill CurrentSkill()
    {
        RunData run = _table.Run;
        if (run == null) return Skill.Basic; // a one-off solo match is never a boss fight

        switch (run.CurrentStep.Rank)
        {
            case 4: return Skill.Reads;
            case 3:
            case 2: return Skill.Chains;
            default: return Skill.Basic;
        }
    }

    public async void ProcessTurn()
    {
        // Held for the whole walkthrough. The game is simultaneous and the bot acts on a timer, so
        // a tutorial step that waited while the bot played would teach a table that had already
        // moved. FinishTutorial calls this again to release it.
        if (_table.TutorialHoldsBot) return;

        if (_thinking || !Me.CanAct) return; // never run two AI turns at once
        _thinking = true;
        _table.Refresh(); // shows "Thinking..." on the bot's side

        //1. Wait a moment to let the player see the AI's drawn card
        if (!await _table.Pause(1.0)) return; // the scene was restarted or left mid-turn

        //2. First, whether to reach across the table at all. At most one such card per turn, and
        //   only when it decides the set - see TryPlayEffectCard.
        if (TryPlayEffectCard())
        {
            if (!await _table.Pause(1.5)) return;
        }

        //3. Then its own arithmetic. From Gold up it keeps going while each card strictly improves
        //   its position - capped, and with the same pause between each, so the player can follow
        //   a chain rather than watch a hand evaporate.
        Skill skill = CurrentSkill();
        int maxCards = (skill == Skill.Basic) ? 1 : MaxAiChainedCards;

        for (int played = 0; played < maxCards; played++)
        {
            if (!TryPlayModifierCard(mayChain: skill != Skill.Basic)) break;

            //Wait 1.5 seconds to let the player see each card land
            if (!await _table.Pause(1.5)) return;
        }

        _thinking = false;

        // Answered while it was thinking: start the turn again against the board as it stands now,
        // rather than closing a turn that was re-opened halfway through.
        if (_reopenedMidTurn)
        {
            _reopenedMidTurn = false;
            ProcessTurn();
            return;
        }

        int target = State.TargetScore;

        //4. Still over the target now = the bot ends its turn and busts when the turn resolves.
        if (Me.CurrentScore > target)
        {
            GD.Print($"AI ends its turn over the target at {Me.CurrentScore}");
            Me.HasEndedTurn = true;
            _table.ResolveTurn();
            return;
        }

        int holdThreshold = Math.Max(10, target - 2);

        // Chasing a score the player has already locked in: play to BEAT it, not to match it.
        // The first build set the threshold to Player 1's score itself, so against a locked 19 the
        // bot held at 19 - a tie, which is replayed rather than won. That is not a difficulty
        // setting, it is the bot declining a win it could take, so it is fixed at every tier.
        if (You.IsHolding && You.CurrentScore <= target)
        {
            holdThreshold = Math.Min(target, You.CurrentScore + 1);
        }
        else if (skill == Skill.Reads)
        {
            // Level 3 weighs the MATCH, not just the set: behind, there is nothing left to
            // protect and it pushes; level on the DECIDER, a bust loses everything and it plays
            // safe.
            //
            // "The decider" was written as `mine == yours && mine > 0`, which was only ever
            // correct because the match was best of three - 1-1 was the only level score that
            // could end it. At best of five that test fires at 1-1 and at 2-2, and 1-1 is an
            // ordinary mid-match set where playing safe just loses ground. The rule it was
            // always trying to state is: level, with either side one win from the match.
            int mine = State.SetsWonPlayer2;
            int yours = State.SetsWonPlayer1;
            if (mine < yours) holdThreshold += 1;
            else if (mine == yours && mine == GameState.SetsToWinMatch - 1) holdThreshold -= 1;

            // ...and it READS PLAYER 1'S HAND, for the one decision it otherwise gets wrong: is my
            // score actually safe? Against a player sitting on 15 with a +4 in hand, holding on 18
            // is not safe, so it keeps pushing for the target.
            //
            // This is hidden information, on purpose, and only from Obsidian: the early ladder is
            // honest and the top of it is meant to feel like the opponent knows what you are
            // holding. Do not "fix" this - if it reads as cheating in playtesting, delete it.
            if (!You.IsHolding && CanBeatWithOrdinary(You, Me.CurrentScore, target))
            {
                holdThreshold = target;
            }

            holdThreshold = Math.Clamp(holdThreshold, 1, target);
        }

        if (Me.CurrentScore >= holdThreshold || Me.CurrentScore == target)
        {
            GD.Print($"AI decides to HOLD at {Me.CurrentScore} (Target: {target})");
            Me.IsHolding = true;
        }
        else
        {
            Me.HasEndedTurn = true;
        }

        _table.ResolveTurn();
    }

    /// Whether the bot reaches across the table this turn, and with what.
    ///
    /// The rule behind every branch: an effect card is only spent when it DECIDES something. A
    /// Copy spent to move two points, or a Shave on a score the bot is already beating, is the
    /// difference between a boss that feels hard and one that feels cheap.
    private bool TryPlayEffectCard()
    {
        if (_table.BotPlayedEffectThisTurn) return false;

        int target = State.TargetScore;


        // Trade Totals - I take their score, they take mine. The biggest reach in the game, so it
        // is asked first: when this and a Copy would both rescue the same turn, taking a whole
        // legal total off them beats trimming my own draw.
        //
        // It is never a free set. CanPlay refuses a holding opponent, so the player it lands on
        // can always still act - and the answering rule re-opens their turn, handing them my wreck
        // and a chance to climb out of it. What the card buys is the total, and the total has to be
        // worth it on its own.
        foreach (Card card in Me.Modifiers)
        {
            if (card.Effect != CardEffect.TradeTotals || !_table.CanPlayEffect(Me, card)) continue;

            int theirs = You.CurrentScore;
            if (theirs > target) continue;           // never take a bust off them
            if (theirs <= Me.CurrentScore) continue; // and never trade down

            if (Me.CurrentScore > target)
            {
                // Busted, and their legal total ends the problem outright - unless my own hand was
                // going to get me under anyway, in which case keep this for a turn where nothing
                // else will. Asked against my own skill, since from Gold up I can chain my way back.
                if (CanGetUnder(Me, Me.CurrentScore, target, mayChain: CurrentSkill() != Skill.Basic)) continue;
                return _table.PlayEffectCard(Me, card);
            }

            // Not busted. Same bar as Copy below: an effect card is not worth a point or two, so
            // only spend it when it carries me from "not good enough" to "good enough" in one move.
            int wantTotalAtLeast = Math.Max(10, target - 2);
            if (Me.CurrentScore >= wantTotalAtLeast) continue;   // already where I need to be
            if (theirs < wantTotalAtLeast) continue;             // their total does not get me there
            if (CanBeatWithOrdinary(Me, wantTotalAtLeast - 1, target)) continue; // a plain card does

            return _table.PlayEffectCard(Me, card);
        }

        // Copy - my drawn card becomes theirs. Entirely my own business: it never touches their
        // card, their score or their turn, so the only question is whether it changes MY result.
        foreach (Card card in Me.Modifiers)
        {
            if (card.Effect != CardEffect.Copy || !_table.CanPlayEffect(Me, card)) continue;

            // CanPlayEffect has already guaranteed both drawn cards exist and differ.
            int mine = Me.LastDrawnCard.Value;
            int theirs = You.LastDrawnCard.Value;
            int newScore = Me.CurrentScore - mine + theirs;

            if (newScore > target) continue; // never copy myself into a bust

            if (Me.CurrentScore > target)
            {
                // Busted, and this card takes the bust away. Spend it - unless an ordinary card
                // would already have done the job, in which case keep the Copy for a turn where
                // nothing else will. Asked against my OWN skill, since from Gold up I can chain
                // two cards to climb back under.
                if (CanGetUnder(Me, Me.CurrentScore, target, mayChain: CurrentSkill() != Skill.Basic)) continue;
                return _table.PlayEffectCard(Me, card);
            }

            // Not busted. An effect card is not worth a point or two, so only spend it when it
            // carries me from "not good enough" to "good enough" in one move.
            if (newScore <= Me.CurrentScore) continue;

            int wantAtLeast = Math.Max(10, target - 2);
            if (You.IsHolding && You.CurrentScore <= target)
            {
                wantAtLeast = Math.Min(target, You.CurrentScore + 1);
            }

            if (Me.CurrentScore >= wantAtLeast) continue;   // already where I need to be
            if (newScore < wantAtLeast) continue;           // and this does not get me there
            if (CanBeatWithOrdinary(Me, wantAtLeast - 1, target)) continue; // a plain card does it

            return _table.PlayEffectCard(Me, card);
        }

        // Shave - their score is locked below the target, so it can never move again and there is
        // nothing to wait for. It only ever matters where one point changes the result, which is
        // exactly when I am level with them or behind: ahead of them it is a wasted card.
        foreach (Card card in Me.Modifiers)
        {
            if (card.Effect != CardEffect.Shave || !_table.CanPlayEffect(Me, card)) continue;
            if (Me.CurrentScore > target) continue;               // fix my own bust first
            if (Me.CurrentScore > You.CurrentScore) continue;      // already winning
            if (Me.CurrentScore < You.CurrentScore - 1) continue;  // one point cannot bridge more than one

            // And never instead of simply winning: if an ordinary card already beats their locked
            // score, play that and keep the Shave (priority 1 in the spec's decision order).
            if (CanBeatWithOrdinary(Me, You.CurrentScore, target)) continue;

            return _table.PlayEffectCard(Me, card);
        }

        // Veto - destroy the card they just played. Asked AFTER Shave, which looks backwards until
        // you read Shave's gates: Shave only fires when one point settles it, and when one point
        // settles it one point is the cheaper card to spend. Veto is the bigger hammer and the only
        // card in the game that takes something away forever, so it waits for a job worth it.
        //
        // Two things make it decisive, and nothing else does. It can push them OVER the target, or
        // it can take a total that is beating me and drop it below mine. Short of those, they draw
        // the points straight back next turn and I have spent the game's dearest attack on a dent.
        foreach (Card card in Me.Modifiers)
        {
            if (card.Effect != CardEffect.Veto || !_table.CanPlayEffect(Me, card)) continue;
            if (Me.CurrentScore > target) continue; // fix my own bust first - Veto does nothing for it

            // Never repair a bust for them. They are already over; the card they last played was
            // either a plus that put them there (undoing it RESCUES them) or a minus that failed
            // to save them (already losing). Both are reasons to leave them exactly where they are.
            if (You.CurrentScore > target) continue;

            // CanPlayEffect has already guaranteed this is a plain modifier played this turn.
            Card theirs = You.LastPlayedModifier;
            int after = You.CurrentScore - theirs.Value;

            // Vetoing a MINUS card sends them up, which is how this lands as a kill: they are over
            // the target, re-opened, and one modifier short of the hand they were going to fix it
            // with. Vetoing a plus is the ordinary case - it takes a lead away.
            bool bustsThem = after > target;
            bool takesTheLead = You.CurrentScore > Me.CurrentScore && after < Me.CurrentScore;
            if (!bustsThem && !takesTheLead) continue;

            // Priority 1, as everywhere: if a plain card already takes the set off a score they
            // have locked in, take the set and keep this. Doubly so here - Veto un-holds them,
            // so spending it on a hold I was already going to beat hands the set back.
            if (You.IsHolding && CanBeatWithOrdinary(Me, You.CurrentScore, target)) continue;

            return _table.PlayEffectCard(Me, card);
        }

        // Trade Hands - I take everything they are still holding, they take what I have left. Late,
        // because it decides nothing about THIS turn: it is a bet on the sets to come, while
        // every card above it is a bet on the one being played.
        //
        // The signal is my own hand being spent, not theirs being good. How many cards someone
        // holds is visible across any real table, so every tier may count them; what is IN a hand
        // is hidden information, and pass 3 licensed reading that at Obsidian only. So the Ruby bot
        // that first carries this card trades on the honest signal - "I have nothing left and they
        // do" - and the Obsidian bot additionally refuses a trade that would not gain it anything.
        foreach (Card card in Me.Modifiers)
        {
            if (card.Effect != CardEffect.TradeHands || !_table.CanPlayEffect(Me, card)) continue;
            if (Me.CurrentScore > target) continue; // fix my own bust before playing for next set

            // Priority 1 in the spec's decision order: if a plain card already takes the set off
            // a score they have locked in, take the set and keep this.
            if (You.IsHolding && CanBeatWithOrdinary(Me, You.CurrentScore, target)) continue;

            // This card is what empties my hand, so count what is left AFTER it goes.
            int myRemaining = Me.Modifiers.Count - 1;
            if (myRemaining > MaxModifiersToTradeAway) continue;      // my hand is not spent yet
            // A rescue card never changes hands, so it is not part of what the trade would take.
            if (You.Modifiers.FindAll(c => !c.IsRescue).Count <= myRemaining) continue; // and theirs has to be bigger

            if (CurrentSkill() == Skill.Reads
                && ModifierStrength(You) <= ModifierStrength(Me, ignore: card)) continue;

            return _table.PlayEffectCard(Me, card);
        }

        // Recall - I take one of my own spent cards back. Asked LAST, below even Trade Hands: the
        // card it returns cannot be played until the next turn, so it decides nothing about this
        // one, and Trade Hands at least has a window that closes (their hand is fat NOW). Recall's
        // window never closes, so it is always the thing to do when there is nothing better.
        foreach (Card card in Me.Modifiers)
        {
            if (card.Effect != CardEffect.Recall || !_table.CanPlayEffect(Me, card)) continue;
            if (Me.CurrentScore > target) continue; // fix this turn before playing for the next

            // The floor, and the same one Trade Hands uses. Without it the bot burns Recall in the
            // first turn of a match, when its hand is full and the card it gets back is worth less
            // than the one it spends. The moment this card is FOR is "my hand is spent".
            if (Me.Modifiers.Count - 1 > MaxModifiersToTradeAway) continue;

            // A card for next set is worth nothing when there may not be one. Either side one
            // win from the match means this set can end it.
            if (State.SetsWonPlayer1 >= GameState.SetsToWinMatch - 1
                || State.SetsWonPlayer2 >= GameState.SetsToWinMatch - 1) continue;

            Card wanted = PickRecallTarget(Me, ignore: card);
            if (wanted == null) continue;

            return _table.PlayEffectCard(Me, card, wanted);
        }

        return false;
    }

    /// Which spent card the bot brings back: the biggest one it can - unless what is left in hand
    /// has no way DOWN, in which case the biggest minus instead.
    ///
    /// That second clause is Player.EnsureBothSigns' reasoning applied to a hand of one, and it is
    /// the difference between recalling a +4 it cannot use against 23 and recalling the -3 that
    /// saves it. Hands last the whole match and are never topped up, so "playable right now" is
    /// the wrong measure - a card that is dead this set is the best card in the hand next set.
    private Card PickRecallTarget(Player player, Card ignore = null)
    {
        bool hasWayDown = false;
        foreach (Card held in player.Modifiers)
        {
            if (held == ignore || held.Effect != CardEffect.None) continue;
            if (held.CanFlipValue || held.Value < 0) { hasWayDown = true; break; }
        }

        Card best = null;
        int bestScore = int.MinValue;

        foreach (Card spent in player.SpentCards)
        {
            if (!CardEffects.IsPlainModifier(spent)) continue;

            // A "+/-" card counts for more than its number, because it can be played either way up.
            int score = Math.Abs(spent.Value) + (spent.CanFlipValue ? 1 : 0);

            // ...and when the hand has no way down at all, ANY card that can go down outranks any
            // size of plus. Scored rather than branched so the answer does not depend on the order
            // the pile happens to be in.
            if (!hasWayDown && (spent.CanFlipValue || spent.Value < 0)) score += 100;

            if (score > bestScore) { best = spent; bestScore = score; }
        }

        return best;
    }

    /// What a hand is worth, for the one decision that needs to compare two of them.
    ///
    /// Deliberately NOT "cards that could be played legally this set": hands last the whole
    /// match and are never topped up, so a +5 that is dead against 19 is the best card in the hand
    /// next set. Magnitude is the measure that survives the set. A "+/-" card is worth more
    /// than its number because it can be played either way up, and an effect card is worth taking
    /// whatever it happens to be.
    ///
    /// `ignore` leaves out the card being spent to make the trade.
    private static int ModifierStrength(Player player, Card ignore = null)
    {
        int strength = 0;
        foreach (Card card in player.Modifiers)
        {
            if (card == ignore) continue;
            if (card.Effect != CardEffect.None)
            {
                strength += EffectCardWorth;
                continue;
            }
            strength += Math.Abs(card.Value) + (card.CanFlipValue ? FlipValueBonus : 0);
        }
        return strength;
    }

    private const int FlipValueBonus = 2;
    private const int EffectCardWorth = 5;

    /// Could this player still get to or under the target with the ordinary cards in their hand?
    /// Counts a "+/-" card at its minus face, since that is the orientation that saves a bust.
    ///
    /// mayChain says whether they get to play more than one: a person can chain Modifiers for as
    /// long as they like before ending the turn, while the bot plays at most one per turn - so the
    /// same question has two different answers depending on who is being asked about. (From Gold
    /// up the bot chains too, and asks this about itself with mayChain: true.)
    ///
    /// `ignore` leaves one card out of the count - the card the caller is about to spend, which is
    /// no longer available to finish the job it starts.
    private static bool CanGetUnder(Player player, int score, int target, bool mayChain, Card ignore = null)
    {
        if (score <= target) return true;

        int everything = 0;
        foreach (Card card in player.Modifiers)
        {
            if (card.Effect != CardEffect.None) continue;
            if (card == ignore) continue;

            int best = card.CanFlipValue ? -Math.Abs(card.Value) : card.Value;
            if (!mayChain && score + best <= target) return true;
            if (best < 0) everything += best;
        }

        return mayChain && score + everything <= target;
    }

    /// Is there an ordinary card that would put the bot past a score the player has locked in,
    /// without busting? If so it does not need an effect card to win this set.
    private static bool CanBeatWithOrdinary(Player me, int scoreToBeat, int target)
    {
        foreach (Card card in me.Modifiers)
        {
            if (card.Effect != CardEffect.None) continue;

            int magnitude = Math.Abs(card.Value);
            int[] orientations = card.CanFlipValue ? new[] { magnitude, -magnitude } : new[] { card.Value };
            foreach (int value in orientations)
            {
                int result = me.CurrentScore + value;
                if (result <= target && result > scoreToBeat) return true;
            }
        }
        return false;
    }

    /// The bot looks at every card in its hand - and, for a "+/-" card, at BOTH orientations -
    /// and takes the play that leaves it as high as possible without going over the target.
    ///
    /// `mayChain` says whether another card may follow this one in the same turn (Gold and up). It
    /// changes exactly one thing, and it is the thing Alexander caught at the table: a bot on 26
    /// against a target of 20, holding a -3 and a -4, plays NEITHER, because neither card alone
    /// gets it under. Allowed to chain it plays the -4, then the -3, and takes the set.
    private bool TryPlayModifierCard(bool mayChain = false)
    {
        int target = State.TargetScore;
        int score = Me.CurrentScore;

        // How high it wants to be before it stops improving: near the target normally, or one PAST
        // Player 1 when it is chasing a score Player 1 has already locked in - drawing level with
        // a locked score is a tie, which is replayed rather than won.
        int wantAtLeast = Math.Max(10, target - 2);
        if (You.IsHolding && You.CurrentScore <= target)
        {
            wantAtLeast = Math.Min(target, You.CurrentScore + 1);
        }

        Card bestCard = null;
        int bestValue = 0;
        int bestResult = int.MinValue;

        // A partial climb down: still over the target, but closer, and only ever considered when
        // what is LEFT in the hand can finish the job.
        Card salvageCard = null;
        int salvageValue = 0;
        int salvageResult = int.MaxValue;

        foreach (Card card in Me.Modifiers)
        {
            // Effect cards are chosen by their own logic (pass 2), never scored as a gain to the
            // bot's own total: Copy takes its number from the table, and a Shave carries Value 1
            // while subtracting.
            if (card.Effect != CardEffect.None) continue;
            if (_table.IsRecallLocked(Me, card)) continue; // came back this turn, live from the next

            int[] orientations = card.CanFlipValue ? new[] { card.Value, -card.Value } : new[] { card.Value };
            foreach (int value in orientations)
            {
                int result = score + value;

                if (result > target)
                {
                    // Never play INTO a bust. Already busted, chaining, and this card leaves it
                    // strictly closer to legal - that is the one case worth a card, and only if
                    // the rest of the hand can actually finish the climb down.
                    if (!mayChain || score <= target || result >= score) continue;
                    if (!CanGetUnder(Me, result, target, mayChain: true, ignore: card)) continue;
                    if (result >= salvageResult) continue;

                    salvageCard = card;
                    salvageValue = value;
                    salvageResult = result;
                    continue;
                }

                if (score <= target)
                {
                    if (result <= score) continue;                    // already safe: only play to improve
                    if (result < wantAtLeast) continue;               // not worth burning a card for
                }
                if (result <= bestResult) continue;

                bestCard = card;
                bestValue = value;
                bestResult = result;
            }
        }

        // Landing legal always beats getting closer, so the salvage is only ever the fallback.
        if (bestCard == null && salvageCard != null)
        {
            bestCard = salvageCard;
            bestValue = salvageValue;
            bestResult = salvageResult;
        }

        if (bestCard == null) return false;

        if (bestCard.Value != bestValue) bestCard.FlipValue(); // play the +/- card the other way round

        GD.Print($"AI Bot plays modifier {bestCard.CardName}. New Score: {bestResult} (Target: {target})");
        _table.PlayBotModifier(bestCard);

        _table.Refresh();

        return true;
    }

    /// The AI's hand for this match, built to the rung's recipe rather than rolled flat: stage 1
    /// is the standard game with no "+/-" cards at all, every stage above it guarantees exactly
    /// one, and stages 4-8 spend one of the four slots on that stage's effect card.
    ///
    /// Local 2-player and a runless solo scene keep the old flat roll.
    public void DealHand()
    {
        RunData run = _table.Run;
        if (run == null)
        {
            _table.DealPlainHand(Me);
            if (!_table.VsBot && _table.LocalSpecials) _table.AddLocalSpecial(Me);
            return;
        }

        RunData.LadderStep step = run.CurrentStep;
        List<Card> hand = new List<Card>();

        // Plain cards first: no flip chance here, because whether this stage has a "+/-" card is
        // the stage's decision, not a dice roll.
        for (int i = 0; i < _table.HandSize; i++)
        {
            hand.Add(Player.CreateRandomModifier(_table.Rng, 0.0, _table.MaxModifierMagnitude));
        }

        int flipValueIndex = -1;
        if (step.AiHasFlipValueCards)
        {
            flipValueIndex = _table.Rng.Next(hand.Count);
            Card card = hand[flipValueIndex];
            hand[flipValueIndex] = new Card(Math.Abs(card.Value), CardType.Modifier, "", canFlipValue: true);
        }

        // The stage's effect card takes one of the four slots.
        //
        // From stage 4 up, if the card this rung is NAMED for is not built yet, the bot carries a
        // finished effect instead of nothing. That is what Alexander was seeing as "the AI is
        // sometimes starting a match without their new modifier card": a rung naming an unwired
        // Trade was dealt four ordinary cards and played exactly like the rung below it.
        //
        // Pass 5 wired both Trades, so stages 4-7 each deal the card they are named for now. What
        // still falls back is stage 8 (no card designed yet) and stages 9-10 (which name nothing
        // at all until the randomizer rolls their ruleset).
        //
        // Still ONE card, dealt once for the whole match and spent when it is played. Hands are
        // not topped up between sets: the drama of a stage card is that there is one of it.
        // The finale: two plain cards (one of them the "+/-") and one of each rolled effect.
        List<CardEffect> rolled = run.CurrentRolledEffects;
        if (rolled != null && rolled.Count > 0)
        {
            List<int> free = new List<int>();
            for (int i = 0; i < hand.Count; i++) if (i != flipValueIndex) free.Add(i);
            foreach (CardEffect effect in rolled)
            {
                if (free.Count == 0) break;
                int pick = _table.Rng.Next(free.Count);
                hand[free[pick]] = CardEffects.Create(effect, _table.Rng);
                free.RemoveAt(pick);
            }

            Me.Modifiers = hand;
            Me.EnsureBothSigns(_table.Rng);
            return;
        }

        CardEffect aiEffect = step.AiEffect;
        if (!CardEffects.IsWired(aiEffect) && run.MatchNumber >= 4)
        {
            List<CardEffect> wired = CardEffects.WiredEffects();

            // Only cards this rung has already EARNED. The ladder's promise is that you meet a
            // card across the table at its own stage and can buy it one visit later; a fallback
            // that reached for anything wired would have handed the player a stage 7 Shave at
            // stage 5, two rungs before the game introduces it and two before the market will
            // sell it. It became a live risk the moment stage 5 lost its own card.
            wired.RemoveAll(effect => RunData.StageThatIntroduces(effect) > run.MatchNumber);

            if (wired.Count > 0) aiEffect = wired[_table.Rng.Next(wired.Count)];
        }

        // Never the slot the "+/-" card just took: the recipe is three plain cards (one of them
        // a "+/-") plus the effect, and eating the flip card would quietly undo stage 2.
        if (CardEffects.IsWired(aiEffect))
        {
            int effectIndex = _table.Rng.Next(hand.Count);
            if (effectIndex == flipValueIndex) effectIndex = (effectIndex + 1) % hand.Count;
            hand[effectIndex] = CardEffects.Create(aiEffect, _table.Rng);
        }

        Me.Modifiers = hand;
        Me.EnsureBothSigns(_table.Rng);
    }
}
