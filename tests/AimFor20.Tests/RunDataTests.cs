using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// The profile and the run down the ladder: what a win, a loss and a new run each cost.
public class RunDataTests
{
    private static RunData NewRun(int seed = 1)
    {
        RunData run = new RunData(new Random(seed)) { Clock = () => 1_000 };
        run.StartNewRun();
        return run;
    }

    [Fact]
    public void A_new_run_starts_at_stage_1_with_the_starter_collection_and_a_full_deck()
    {
        RunData run = NewRun();
        Assert.True(run.RunActive);
        Assert.Equal(0, run.StepIndex);
        Assert.Equal(14, run.Inventory.Count);
        Assert.Equal(RunData.SideDeckSize, run.SideDeck.Count);
        Assert.All(run.Inventory, def => Assert.False(def.CanFlipValue)); // +/- is a store card
    }

    [Fact]
    public void Every_change_raises_Changed_so_the_store_can_save_it()
    {
        RunData run = new RunData(new Random(1));
        int saves = 0;
        run.Changed += () => saves++;
        run.StartNewRun();
        run.CompleteMatch(3, won: true);
        Assert.Equal(2, saves);
    }

    [Fact]
    public void Winning_banks_a_medal_per_set_plus_the_purse_and_climbs_a_rung()
    {
        RunData run = NewRun();
        run.CompleteMatch(setsWon: 3, won: true);
        Assert.Equal(3 + Ladder.At(0).MedalReward, run.Medals);
        Assert.Equal(1, run.StepIndex);
        Assert.Equal(1, run.FurthestStep);
    }

    [Fact]
    public void Losing_ends_the_run_but_keeps_cards_deck_and_medals()
    {
        RunData run = NewRun();
        run.CompleteMatch(3, won: true);
        run.AddToInventory(new ModifierDef(5));
        int medals = run.Medals;
        List<int> deck = run.SideDeck.ToList();

        run.CompleteMatch(1, won: false);

        Assert.False(run.RunActive);
        Assert.Equal(15, run.Inventory.Count);
        Assert.Equal(medals, run.Medals);
        Assert.Equal(deck, run.SideDeck);

        run.StartNewRun();
        Assert.Equal(0, run.StepIndex);
        Assert.Equal(15, run.Inventory.Count);
        Assert.Equal(1, run.FurthestStep); // the record survives
    }

    [Fact]
    public void A_new_run_never_replays_the_tutorial()
    {
        RunData run = NewRun();
        run.MarkTutorialSeen();
        run.StartNewRun();
        Assert.True(run.TutorialSeen);
    }

    [Fact]
    public void The_finale_rolls_its_rules_once_on_arrival_and_keeps_them()
    {
        RunData run = NewRun();
        run.DebugJumpToStep(Ladder.Length - 2);
        run.CompleteMatch(3, won: true);

        Assert.True(run.CurrentStep.Randomised);
        Assert.NotNull(run.CurrentRolledEffects);
        int target = run.CurrentTarget;
        List<CardEffect> rules = run.CurrentRolledEffects.ToList();

        run.EnsureRuleset();
        Assert.Equal(target, run.CurrentTarget);
        Assert.Equal(rules, run.CurrentRolledEffects);
    }

    [Fact]
    public void Clearing_the_ladder_unlocks_endless()
    {
        RunData run = NewRun();
        Assert.False(run.EndlessUnlocked);
        run.DebugJumpToStep(Ladder.Length - 1);
        run.CompleteMatch(3, won: true);
        Assert.True(run.RunComplete);
        Assert.True(run.EndlessUnlocked);
    }

    [Fact]
    public void An_endless_run_is_banked_once_when_it_is_lost()
    {
        RunData run = NewRun();
        run.StartEndless();
        run.CompleteMatch(3, won: true);
        run.CompleteMatch(3, won: true);
        run.CompleteMatch(0, won: false);

        Assert.Equal(2, run.EndlessStreak); // left standing for the run-end screen
        Assert.Equal(2, run.EndlessBest);
        Assert.Single(run.EndlessScores);
        Assert.Equal(new EndlessScore(2, 1_000), run.EndlessScores[0]);

        run.StartNewRun(); // giving up an already-banked run must not bank it twice
        Assert.Single(run.EndlessScores);
    }

    [Fact]
    public void Abandoning_an_endless_run_for_a_new_one_still_banks_it()
    {
        RunData run = NewRun();
        run.StartEndless();
        run.CompleteMatch(3, won: true);
        run.StartEndless();
        Assert.Single(run.EndlessScores);
        Assert.Equal(0, run.EndlessStreak);
    }

    [Fact]
    public void Rescue_is_offered_from_stage_4_and_once_per_match()
    {
        RunData run = NewRun();
        run.DebugJumpToStep(RunData.RescueFirstStage - 2);
        Assert.False(run.RescueEligible);

        run.DebugJumpToStep(RunData.RescueFirstStage - 1);
        Assert.True(run.RescueEligible);
        run.UseMatchRescue();
        Assert.False(run.RescueEligible);

        run.CompleteMatch(3, won: true);
        Assert.True(run.RescueEligible);
    }

    [Fact]
    public void The_market_only_sells_an_effect_once_its_stage_is_cleared_this_run()
    {
        RunData run = NewRun();
        int copyStage = Ladder.StageThatIntroduces(CardEffect.Copy);
        run.DebugJumpToStep(copyStage - 1);
        Assert.False(run.EffectUnlocked(CardEffect.Copy));
        run.CompleteMatch(3, won: true);
        Assert.True(run.EffectUnlocked(CardEffect.Copy));

        run.CompleteMatch(0, won: false);
        run.StartNewRun();
        Assert.False(run.EffectUnlocked(CardEffect.Copy)); // gated on this run, not the record
        Assert.Contains(CardEffect.Copy, run.MetEffects()); // ...but local 2-player has seen it
    }

    [Fact]
    public void Side_deck_drops_bad_and_repeated_indices()
    {
        RunData run = NewRun();
        run.SetSideDeck(new[] { 0, 0, 1, -1, 99, 2 });
        Assert.Equal(new List<int> { 0, 1, 2 }, run.SideDeck);
    }

    [Fact]
    public void A_match_hand_is_four_different_deck_cards()
    {
        RunData run = NewRun();
        run.SetSideDeck(Enumerable.Range(0, RunData.SideDeckSize));
        List<Card> hand = run.DrawMatchModifiers();
        Assert.Equal(RunData.MatchModifierCount, hand.Count);
        Assert.All(hand, c => Assert.Equal(CardType.Modifier, c.Type));
    }

    [Fact]
    public void Completing_the_collection_log_switches_the_gilded_back_on_exactly_once()
    {
        RunData run = NewRun();
        bool completedOnce = false;
        foreach (string key in CollectionLog.Keys)
        {
            bool completed = run.MarkCardMet(key);
            Assert.False(completed && completedOnce);
            completedOnce |= completed;
        }
        Assert.True(completedOnce);
        Assert.True(run.CollectionComplete);
        Assert.True(run.UseCollectorBack);
    }

    [Fact]
    public void Cosmetics_need_the_rank_beaten_and_the_medals()
    {
        RunData run = NewRun();
        Assert.False(run.CosmeticUnlocked("bronze"));
        Assert.False(run.BuyCosmetic("bronze", board: false));

        run.CompleteMatch(3, won: true); // beat the Bronze Challenger
        Assert.True(run.CosmeticUnlocked("bronze"));
        run.CompleteMatch(3, won: true); // and the Champion, for the medals
        Assert.False(run.CosmeticUnlocked("silver"));

        Assert.True(run.Medals >= Cosmetics.Price("bronze"));
        int before = run.Medals;
        Assert.True(run.BuyCosmetic("bronze", board: false));
        Assert.Equal(before - Cosmetics.Price("bronze"), run.Medals);
        Assert.Equal("bronze", run.SelectedDeck);
        Assert.False(run.BuyCosmetic("bronze", board: false)); // already owned
    }

    [Fact]
    public void Wiping_the_save_returns_to_a_first_launch()
    {
        RunData run = NewRun();
        run.CompleteMatch(3, won: true);
        run.MarkTutorialSeen();
        run.DebugWipeSave();
        Assert.False(run.RunActive);
        Assert.Equal(0, run.Medals);
        Assert.Empty(run.Inventory);
        Assert.False(run.TutorialSeen);
        Assert.Equal(Cosmetics.Default, run.SelectedBoard);
    }
}

/// The fixed tables the run is measured against.
public class LadderAndCatalogueTests
{
    [Fact]
    public void Every_rung_below_the_finale_after_stage_3_introduces_exactly_one_new_effect()
    {
        var seen = new HashSet<CardEffect>();
        for (int i = 3; i < Ladder.Length - 1; i++)
        {
            CardEffect effect = Ladder.At(i).AiEffect;
            Assert.NotEqual(CardEffect.None, effect);
            Assert.True(seen.Add(effect), $"{effect} introduced twice");
            Assert.True(CardEffects.IsWired(effect), $"{effect} is on the ladder but not wired");
        }
        Assert.True(Ladder.At(Ladder.Length - 1).Randomised);
    }

    [Fact]
    public void Ladder_indices_are_clamped()
    {
        Assert.Equal(Ladder.At(0).Opponent, Ladder.At(-5).Opponent);
        Assert.Equal(Ladder.At(Ladder.Length - 1).Opponent, Ladder.At(99).Opponent);
    }

    [Fact]
    public void Collection_log_keys_round_trip_to_the_card_they_name()
    {
        Assert.Equal(CollectionLog.MaxMagnitude * 3 + 6, CollectionLog.Keys.Length);
        foreach (string key in CollectionLog.Keys)
            Assert.Equal(key, CollectionLog.Entry(key).LogKey);
    }

    [Fact]
    public void Collection_log_has_no_key_for_a_main_deck_card_or_a_zero()
    {
        Assert.Null(CollectionLog.Key(new Card(5, CardType.Main)));
        Assert.Null(CollectionLog.Key(0, false, CardEffect.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_rolled_ruleset_never_plays_to_20_and_never_pairs_Copy_with_Trade_Totals(int countOffset)
    {
        var rng = new Random(42);
        for (int i = 0; i < 500; i++)
        {
            Ruleset rules = Ruleset.Roll(rng, count: 1 + countOffset);
            Assert.NotEqual(20, rules.Target);
            Assert.InRange(rules.Target, 18, 25);
            Assert.Equal(rules.Effects.Length, rules.Effects.Distinct().Count());
            Assert.False(rules.Effects.Contains(CardEffect.Copy) && rules.Effects.Contains(CardEffect.TradeTotals));
            int[] stages = rules.Effects.Select(Ladder.StageThatIntroduces).ToArray();
            Assert.Equal(stages.OrderBy(s => s), stages); // named in the order the ladder taught them
        }
    }

    [Fact]
    public void Endless_widens_its_target_range_and_adds_a_third_rule_late()
    {
        Assert.Equal((18, 25), EndlessRules.TargetRange(0));
        Assert.Equal((15, 30), EndlessRules.TargetRange(100));
        Assert.Equal(2, EndlessRules.RuleCount(EndlessRules.ThirdRuleStreak - 1));
        Assert.Equal(3, EndlessRules.RuleCount(EndlessRules.ThirdRuleStreak));
    }

    [Fact]
    public void Scoreboard_sorts_best_then_newest_and_keeps_five()
    {
        var scores = new List<EndlessScore>
        {
            new EndlessScore(3, 10), new EndlessScore(5, 1), new EndlessScore(3, 20),
            new EndlessScore(1, 5), new EndlessScore(2, 5), new EndlessScore(9, 0),
        };
        EndlessRules.SortAndTrim(scores);
        Assert.Equal(EndlessRules.ScoreboardSize, scores.Count);
        Assert.Equal(new[] { 9, 5, 3, 3, 2 }, scores.Select(s => s.Streak));
        Assert.Equal(20, scores[2].UnixTime);
    }

    [Fact]
    public void Each_rank_set_opens_on_its_Challenger()
    {
        Assert.Equal("Bronze Challenger", Cosmetics.UnlockStageName("bronze"));
        Assert.Equal("Obsidian Challenger", Cosmetics.UnlockStageName("obsidian"));
        Assert.Null(Cosmetics.UnlockStageName(Cosmetics.Default));
        Assert.Null(Cosmetics.UnlockStageName(Cosmetics.EndlessKey));
    }

    [Fact]
    public void Scrapped_Push_and_Trade_Draw_load_as_Copy()
    {
        Assert.True(RunData.MigrateModifier(3, false, (int)CardEffect.Push, out ModifierDef push));
        Assert.Equal(new ModifierDef(0, false, CardEffect.Copy), push);
        Assert.True(RunData.MigrateModifier(0, false, (int)CardEffect.TradeDraw, out ModifierDef draw));
        Assert.Equal(CardEffect.Copy, draw.Effect);
    }

    [Fact]
    public void Unknown_effects_load_as_plain_cards_and_empty_rows_are_dropped()
    {
        Assert.True(RunData.MigrateModifier(4, false, 999, out ModifierDef def));
        Assert.Equal(new ModifierDef(4), def);
        Assert.False(RunData.MigrateModifier(0, false, 0, out _));
        Assert.True(RunData.MigrateModifier(0, false, (int)CardEffect.Veto, out ModifierDef veto));
        Assert.Equal(CardEffect.Veto, veto.Effect);
    }
}
