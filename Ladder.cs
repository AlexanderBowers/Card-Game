using System;

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

    /// The finale: target and effect cards are rolled when the player arrives on this rung
    /// (RunData.EnsureRuleset). TargetScore is then only a fallback that nothing should reach.
    public readonly bool Randomised;

    /// The opponent's cards after its first two are face down (GameState.HiddenOpponent).
    public readonly bool HidesOpponentCards;

    public LadderStep(int rank, string opponent, int targetScore, int medalReward,
                      bool aiHasFlipValueCards = true, CardEffect aiEffect = CardEffect.None,
                      bool randomised = false, bool hidesOpponentCards = false)
    {
        Randomised = randomised;
        HidesOpponentCards = hidesOpponentCards;
        Rank = rank;
        Opponent = opponent;
        TargetScore = targetScore;
        MedalReward = medalReward;
        AiHasFlipValueCards = aiHasFlipValueCards;
        AiEffect = aiEffect;
    }
}

/// The gauntlet, as fixed data: no state, no Godot. What the player has DONE on it lives in
/// RunData; what each rank looks like lives in RankTheme.
public static class Ladder
{
    /// Two rungs per rank, so the board changes every other match and the player can SEE how far
    /// up they are. The colours that go with each name are RankTheme's business.
    public static readonly string[] RankNames = { "Bronze", "Silver", "Gold", "Ruby", "Obsidian" };

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
    private static readonly LadderStep[] Steps =
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
                       hidesOpponentCards: true),                                           //    cards face down
    };

    // The boss plays face down (2026-10-09). Hidden cards are the finale's twist rather than a
    // rung of their own, so stages 1-9 still bring exactly one new idea each - and Endless, parked
    // on this rung (RunData.StartEndless), inherits it: the mode for the players who have learned
    // everything else is the one where you cannot see what you are up against.

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
}
