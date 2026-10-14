using System;
using System.Collections.Generic;

/// What is in a 40-card main deck (stages 21-50, 2026-10-09). Both players' decks share the shape,
/// so it changes the counting, never the fairness.
public enum DeckShape
{
    Standard,   // four each of 1-10 (forty)
    High,       // four each of 4-10 (twenty-eight)
    Low,        // four each of 1-7 (twenty-eight)
    NoMiddle,   // no 5s or 6s (thirty-two)
    ExtraTens,  // the standard forty and four more 10s (forty-four)
}

/// The deck shapes as data: what each holds and what the table calls it.
public static class DeckShapes
{
    /// The shapes a stage can be dealt, in the order the tiers cycle them.
    public static readonly DeckShape[] Shaped = { DeckShape.High, DeckShape.Low, DeckShape.NoMiddle, DeckShape.ExtraTens };

    /// Every card in a fresh deck of this shape, by value.
    public static List<int> Values(DeckShape shape)
    {
        List<int> deck = new List<int>();
        for (int value = 1; value <= 10; value++)
        {
            bool inShape = shape switch
            {
                DeckShape.High => value >= 4,
                DeckShape.Low => value <= 7,
                DeckShape.NoMiddle => value != 5 && value != 6,
                _ => true,
            };
            if (!inShape) continue;
            for (int copy = 0; copy < 4; copy++) deck.Add(value);
        }
        if (shape == DeckShape.ExtraTens)
            for (int copy = 0; copy < 4; copy++) deck.Add(10);
        return deck;
    }

    /// What the stage banner and the set's toast call it. Empty for the standard deck.
    public static string Label(DeckShape shape) => shape switch
    {
        DeckShape.High => "High deck: 4 to 10 only",
        DeckShape.Low => "Low deck: 1 to 7 only",
        DeckShape.NoMiddle => "No 5s or 6s in the deck",
        DeckShape.ExtraTens => "Extra 10s in the deck",
        _ => string.Empty,
    };
}

/// One rung of the linear ladder.
///
/// There are no invented venue names (Alexander's call, 2026-09-07): a place called the "Neon
/// Underground" tells the player nothing they can see. Progress is shown instead - each RANK
/// repaints the table and the standard cards, so two rungs in you are looking at a different
/// board. Rank is also the whole difficulty story: it sets the target score.
public readonly struct LadderStep
{
    public readonly int Rank;            // index into Ladder.RankNames (and RankTheme)
    public readonly string Opponent;
    public readonly int TargetScore;
    public readonly int MedalReward;

    /// Stage 1 is the standard game and the AI's hand holds no "+/-" cards; every stage above
    /// it guarantees the AI exactly one.
    public readonly bool AiHasFlipValueCards;

    /// The one effect card in the AI's four-card hand at this rung, or None. The AI's hand
    /// lasts the whole match, so one effect card is about one dramatic moment per match.
    public readonly CardEffect AiEffect;

    /// The finale and every tier boss: the target is rolled when the player arrives on this rung
    /// (RunData.EnsureRuleset). TargetScore is then only a fallback that nothing should reach.
    public readonly bool Randomised;

    /// How many effect cards the opponent carries, picked at random when the player arrives on the
    /// rung (RunData.EnsureRuleset) - the finale's two, and every stage above it. 0 = AiEffect.
    public readonly int RolledSpecials;

    /// The opponent's cards past its first FaceUpCards each set are face down (GameState.HiddenOpponent).
    public readonly bool HidesOpponentCards;
    public readonly int FaceUpCards;

    /// What both main decks hold (stages 21+), or a new shape every set (the stage 30 and 50 bosses).
    public readonly DeckShape Deck;
    public readonly bool DeckChangesEachSet;

    /// A played Modifier is replaced from the rest of its owner's deck (stages 31+). The bosses at
    /// 40 and 50 refill the opponent from a pile of specials.
    public readonly bool Refill;
    public readonly bool BotRefillsSpecials;

    /// A new target every set (stages 41+).
    public readonly bool TargetMovesEachSet;

    public LadderStep(int rank, string opponent, int targetScore, int medalReward,
                      bool aiHasFlipValueCards = true, CardEffect aiEffect = CardEffect.None,
                      bool randomised = false, bool hidesOpponentCards = false,
                      int rolledSpecials = 0, int faceUpCards = 2,
                      DeckShape deck = DeckShape.Standard, bool deckChangesEachSet = false,
                      bool refill = false, bool botRefillsSpecials = false, bool targetMovesEachSet = false)
    {
        Randomised = randomised;
        HidesOpponentCards = hidesOpponentCards;
        Rank = rank;
        Opponent = opponent;
        TargetScore = targetScore;
        MedalReward = medalReward;
        AiHasFlipValueCards = aiHasFlipValueCards;
        AiEffect = aiEffect;
        RolledSpecials = rolledSpecials;
        FaceUpCards = faceUpCards;
        Deck = deck;
        DeckChangesEachSet = deckChangesEachSet;
        Refill = refill;
        BotRefillsSpecials = botRefillsSpecials;
        TargetMovesEachSet = targetMovesEachSet;
    }
}

/// The gauntlet, as fixed data: no state, no Godot. What the player has DONE on it lives in
/// RunData; what each rank looks like lives in RankTheme.
public static class Ladder
{
    /// Two rungs per rank, so the board changes every other match and the player can SEE how far
    /// up they are. The colours that go with each name are RankTheme's business.
    public static readonly string[] RankNames = { "Bronze", "Silver", "Gold", "Ruby", "Obsidian" };

    // ------------------------------------------------------------------
    // Tiers (2026-10-09)
    //
    // Fifty stages in five tiers of ten. Tier 1 is the ladder as it was: nine rungs that each bring
    // one new card, and a boss. Every tier above it adds ONE new rule that runs through all ten of
    // its stages, on top of everything before it, and ends on a boss with its own twist:
    //
    //   11-20 Face down      the opponent always plays face down    boss: only its first card face up
    //   21-30 Shaped decks   each stage changes what the decks hold boss: a new shape every set
    //   31-40 Refill         a played Modifier is replaced from     boss: the opponent refills
    //                        your 12-card deck                            with specials
    //   41-50 Moving target  a new target every set                 boss: everything at once
    //
    // Inside a tier the rhythm of tier 1 holds - Bronze to Obsidian, two stages a rank, the board
    // changing every other match - so a tier reads as climbing the same ladder again, harder. The
    // difficulty is information and counting, never bigger arithmetic (the 5-to-85 tenet; the
    // multiplying cards are the sequel's idea). A loss sends the player back to the start of the
    // tier, not to stage 1 (RunData.CheckpointStep).
    // ------------------------------------------------------------------
    public const int TierSize = 10;
    public const int TierCount = 5;

    /// The tier (0-based) a rung belongs to.
    public static int TierOf(int index) => ClampIndex(index) / TierSize;

    /// The first rung (0-based) of the tier a rung belongs to.
    public static int TierStart(int index) => TierOf(index) * TierSize;

    /// The tier's last rung - its boss.
    public static bool IsBoss(int index) => ClampIndex(index) % TierSize == TierSize - 1;

    /// Endless is unlocked by beating stage 10, and plays on stage 10's rules - unchanged by the
    /// tiers above it, so a player already in Endless keeps the mode they had.
    public const int EndlessStepIndex = 9;
    public const int EndlessUnlockSteps = 10;

    /// Difficulty escalates by moving the target away from the comfortable 20 - never by
    /// inflating the arithmetic (the 5-to-85 accessibility tenet rules out multiplier math).
    ///
    /// EVERY CARD MOVED UP A RUNG on 2026-09-11 (Alexander's call), closing the hole Trade Draw
    /// left at stage 5. Trade Totals 6 to 5, Shave 7 to 6, Trade Hands 8 to 7 - so every rung from
    /// 4 to 7 still introduces exactly one new idea, which is the rule the whole ladder is built
    /// on. Stage 8 is Recall and stage 9 is Veto (2026-09-13), so every rung from 1 to 9 now
    /// introduces exactly one new thing and stage 10 is the single randomised finale. The
    /// randomiser held two rungs only because that is what the table happened to have; after nine
    /// rungs of learning, one boss rung that tests all of it is the better shape.
    ///
    /// Note what moved WITH the cards and what did not: the targets belong to the RANKS, not to
    /// the cards, so Shave is now met at a target of 18 rather than 24. Worth watching at the
    /// table - Shave punishes holding below the target, and there is less room to hold below 18.
    private static readonly LadderStep[] TierOne =
    {
        //             rank  opponent               target medals  +/-    effect introduced here
        new LadderStep(0, "Bronze Challenger",   20,  3, false),                            // 1 standard rules
        new LadderStep(0, "Bronze Champion",     20,  3, true),                             // 2 the AI gets +/-
        new LadderStep(1, "Silver Challenger",   23,  4, true),                             // 3 the target moves
        new LadderStep(1, "Silver Champion",     23,  5, true, CardEffect.Copy),            // 4
        new LadderStep(2, "Gold Challenger",     18,  5, true, CardEffect.TradeTotals),     // 5
        new LadderStep(2, "Gold Champion",       18,  6, true, CardEffect.Shave),           // 6
        new LadderStep(3, "Ruby Challenger",     24,  6, true, CardEffect.TradeHands),      // 7
        new LadderStep(3, "Ruby Champion",       24,  8, true, CardEffect.Recall),          // 8
        new LadderStep(4, "Obsidian Challenger", 22,  8, true, CardEffect.Veto),            // 9
        new LadderStep(4, "Obsidian Champion",   25, 10, true, randomised: true,            // 10 ruleset rolled,
                       rolledSpecials: 2, hidesOpponentCards: true),                        //    cards face down
    };

    // The boss plays face down (2026-10-09). Hidden cards are the finale's twist rather than a
    // rung of their own, so stages 1-9 still bring exactly one new idea each - and Endless, parked
    // on this rung (RunData.StartEndless), inherits it: the mode for the players who have learned
    // everything else is the one where you cannot see what you are up against.

    private static readonly string[] TierNumerals = { "", " II", " III", " IV", " V" };

    private static readonly LadderStep[] Steps = Build();

    /// Tiers 2-5, made from tier 1's rhythm: each rung keeps tier 1's rank, target and purse (the
    /// purse two medals richer per tier), takes the tier's rules, and carries specials picked at
    /// random - the whole table has been taught by now, so a rung no longer brings a card of its own.
    private static LadderStep[] Build()
    {
        List<LadderStep> steps = new List<LadderStep>(TierOne);
        for (int tier = 1; tier < TierCount; tier++)
        {
            for (int i = 0; i < TierSize; i++)
            {
                LadderStep shape = TierOne[i];
                bool boss = i == TierSize - 1;
                int stage = tier * TierSize + i + 1;

                // Specials: tier 2 eases in (one, then two), every tier after carries two, and the
                // bosses above stage 20 carry three - a whole hand of tricks beside the "+/-".
                int specials = tier == 1 ? (i < 4 ? 1 : 2) : 2;
                if (boss && tier >= 2) specials = 3;

                DeckShape deck = tier >= 2 ? DeckShapes.Shaped[i % DeckShapes.Shaped.Length] : DeckShape.Standard;

                steps.Add(new LadderStep(
                    shape.Rank,
                    shape.Opponent + TierNumerals[tier],
                    shape.TargetScore,
                    shape.MedalReward + 2 * tier,
                    aiHasFlipValueCards: true,
                    randomised: boss,
                    rolledSpecials: specials,
                    hidesOpponentCards: true,
                    faceUpCards: boss && (stage == 20 || stage == 50) ? 1 : 2,
                    deck: deck,
                    deckChangesEachSet: boss && tier >= 2 && (stage == 30 || stage == 50),
                    refill: tier >= 3,
                    botRefillsSpecials: boss && tier >= 3,
                    targetMovesEachSet: tier >= 4));
            }
        }
        return steps.ToArray();
    }

    public static int Length => Steps.Length;

    /// The rung at this index, clamped onto the ladder.
    public static LadderStep At(int index) => Steps[ClampIndex(index)];

    public static int ClampIndex(int index) => Math.Clamp(index, 0, Steps.Length - 1);

    public static string RankName(int rank) => RankNames[Math.Clamp(rank, 0, RankNames.Length - 1)];

    /// The stage number (1-based) that introduces this effect, or 0 if no stage does.
    public static int StageThatIntroduces(CardEffect effect)
    {
        for (int i = 0; i < Steps.Length; i++)
        {
            if (Steps[i].AiEffect == effect) return i + 1;
        }
        return 0;
    }

    /// The lines the stage banner adds for this rung's rules, each on its own line - empty for a
    /// rung with none. (No full stops: the tutorial's style.)
    public static string RulesLines(int index)
    {
        LadderStep step = At(index);
        List<string> lines = new List<string>();
        if (step.HidesOpponentCards)
            lines.Add(step.FaceUpCards == 1 ? "Face down after one card"
                                             : "Face down after two cards");
        if (step.DeckChangesEachSet) lines.Add("Decks change every set");
        else if (step.Deck != DeckShape.Standard) lines.Add(DeckShapes.Label(step.Deck));
        if (step.Refill) lines.Add("Modifiers refill from your deck");
        if (step.TargetMovesEachSet) lines.Add("Target changes every set");
        return lines.Count == 0 ? string.Empty : string.Join("\n", lines);
    }
}
