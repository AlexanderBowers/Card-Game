using System;
using System.Collections.Generic;

/// A rolled set of rules: the randomised finale's, and every endless match's
/// (stage-ladder-spec.md, "Stage 9+").
public readonly struct Ruleset
{
    public readonly int Target;
    public readonly CardEffect[] Effects;
    public Ruleset(int target, CardEffect[] effects) { Target = target; Effects = effects; }

    /// A target away from the familiar 20, and `count` different effect cards - never Copy with
    /// Trade Totals, which are both "the AI undoes the draw that ruined it" and together read as
    /// the game cheating rather than as two rules.
    ///
    /// `count` is 2 everywhere except deep in an endless streak (EndlessRules.RuleCount). It is
    /// clamped to what the wired pool can actually supply, so adding or removing a card never
    /// rolls a ruleset with a hole in it.
    public static Ruleset Roll(Random rng, int minTarget = 18, int maxTarget = 25, int count = 2)
    {
        int target;
        do target = rng.Next(minTarget, maxTarget + 1); while (target == 20 && minTarget < maxTarget);

        List<CardEffect> pool = CardEffects.WiredEffects();
        List<CardEffect> picked = new List<CardEffect>();
        int wanted = Math.Max(1, count);

        while (picked.Count < wanted && pool.Count > 0)
        {
            CardEffect next = pool[rng.Next(pool.Count)];
            picked.Add(next);
            pool.Remove(next);
            // The exclusion is between these two specifically, and it applies however many are
            // rolled: whichever of the pair comes out first, the other stops being available.
            if (next == CardEffect.Copy) pool.Remove(CardEffect.TradeTotals);
            if (next == CardEffect.TradeTotals) pool.Remove(CardEffect.Copy);
        }

        // Stage order, so the finale names them the way the ladder taught them.
        CardEffect[] effects = picked.ToArray();
        Array.Sort(effects, (a, b) => Ladder.StageThatIntroduces(a).CompareTo(Ladder.StageThatIntroduces(b)));
        return new Ruleset(target, effects);
    }
}

/// One row of the endless scoreboard: the streak IS the score; the target it died on was noise
/// on the row (Alexander, 2026-09-17).
public readonly struct EndlessScore
{
    public readonly int Streak;
    public readonly long UnixTime;

    public EndlessScore(int streak, long unixTime)
    {
        Streak = streak;
        UnixTime = unixTime;
    }
}

/// Endless mode's fixed rules (playtest-feedback-family.md §5.1): how a streak escalates and how
/// the scoreboard is kept. The streak itself is run state in RunData.
public static class EndlessRules
{
    public const int ScoreboardSize = 5;

    /// The target range widens as the streak grows - one step further from 20 on each side every
    /// two wins - so a long streak is harder arithmetic, never bigger multipliers. Capped where
    /// a 9-slot board and a 40-card deck still comfortably reach it.
    public static (int min, int max) TargetRange(int streak)
    {
        int widen = streak / 2;
        return (Math.Max(15, 18 - widen), Math.Min(30, 25 + widen));
    }

    /// The range above stops widening at a streak of 10, and after that endless stopped getting
    /// harder at all - every match past it was the same match (pass 24). Past this streak the
    /// opponent carries a THIRD rolled effect card instead of two, which fills its whole hand:
    /// one "+/-" and three specials, no ordinary cards. That is the last escalation there is, and
    /// it is deliberately the last one - it is bounded (each effect is spent when it is played)
    /// and it is announced, because the rolled rules are printed over the table before the deal.
    public const int ThirdRuleStreak = 12;

    public static int RuleCount(int streak) => streak >= ThirdRuleStreak ? 3 : 2;

    /// Best first, and for a tie the more recent run first; trimmed to ScoreboardSize.
    public static void SortAndTrim(List<EndlessScore> scores)
    {
        scores.Sort((a, b) => a.Streak != b.Streak
            ? b.Streak.CompareTo(a.Streak)
            : b.UnixTime.CompareTo(a.UnixTime));
        if (scores.Count > ScoreboardSize)
            scores.RemoveRange(ScoreboardSize, scores.Count - ScoreboardSize);
    }
}
