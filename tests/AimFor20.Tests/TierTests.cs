using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// Stages 11-50 (2026-10-09): five tiers of ten, a rule per tier on top of the ones before, a boss
/// each, and a checkpoint at each tier's first stage.
public class TierTests
{
    private static RunData NewRun(int seed = 1)
    {
        RunData run = new RunData(new Random(seed)) { Clock = () => 1_000 };
        run.StartNewRun();
        return run;
    }

    /// Wins every match from where the run stands until it reaches `stepIndex`.
    private static void ClimbTo(RunData run, int stepIndex)
    {
        while (run.StepIndex < stepIndex) run.CompleteMatch(3, won: true);
    }

    // ------------------------------------------------------------------
    // The ladder
    // ------------------------------------------------------------------

    [Fact]
    public void The_ladder_is_fifty_stages_in_five_tiers_of_ten()
    {
        Assert.Equal(50, Ladder.Length);
        Assert.Equal(0, Ladder.TierOf(9));
        Assert.Equal(1, Ladder.TierOf(10));
        Assert.Equal(4, Ladder.TierOf(49));
        Assert.Equal(30, Ladder.TierStart(34));
        Assert.Equal(new[] { 9, 19, 29, 39, 49 }, Enumerable.Range(0, 50).Where(Ladder.IsBoss));
    }

    [Fact]
    public void Each_tier_climbs_the_ranks_again_with_its_numeral_and_a_richer_purse()
    {
        LadderStep first = Ladder.At(0), again = Ladder.At(20);
        Assert.Equal(first.Rank, again.Rank);
        Assert.Equal("Bronze Challenger III", again.Opponent);
        Assert.Equal(first.MedalReward + 4, again.MedalReward);
        Assert.Equal("Obsidian Champion V", Ladder.At(49).Opponent);
    }

    [Fact]
    public void Only_tier_one_brings_a_card_of_its_own_and_every_stage_above_rolls_specials()
    {
        for (int i = 10; i < Ladder.Length; i++)
        {
            Assert.Equal(CardEffect.None, Ladder.At(i).AiEffect);
            Assert.InRange(Ladder.At(i).RolledSpecials, 1, 3);
        }
        Assert.Equal(1, Ladder.At(10).RolledSpecials); // tier 2 eases in
        Assert.Equal(2, Ladder.At(14).RolledSpecials);
        Assert.Equal(3, Ladder.At(29).RolledSpecials); // the bosses above 20 carry three
    }

    [Fact]
    public void Each_tier_adds_its_rule_and_keeps_the_ones_below_it()
    {
        LadderStep t2 = Ladder.At(12), t3 = Ladder.At(22), t4 = Ladder.At(32), t5 = Ladder.At(42);

        Assert.True(t2.HidesOpponentCards);
        Assert.Equal(DeckShape.Standard, t2.Deck);
        Assert.False(t2.Refill);

        Assert.True(t3.HidesOpponentCards);
        Assert.NotEqual(DeckShape.Standard, t3.Deck);
        Assert.False(t3.Refill);

        Assert.True(t4.HidesOpponentCards && t4.Refill);
        Assert.NotEqual(DeckShape.Standard, t4.Deck);
        Assert.False(t4.TargetMovesEachSet);

        Assert.True(t5.HidesOpponentCards && t5.Refill && t5.TargetMovesEachSet);
    }

    [Fact]
    public void Each_boss_has_its_own_twist()
    {
        Assert.Equal(1, Ladder.At(19).FaceUpCards);           // 20: only the first card face up
        Assert.True(Ladder.At(29).DeckChangesEachSet);        // 30: the decks change every set
        Assert.True(Ladder.At(39).BotRefillsSpecials);        // 40: it refills with specials
        LadderStep fifty = Ladder.At(49);                     // 50: all of it
        Assert.True(fifty.FaceUpCards == 1 && fifty.DeckChangesEachSet && fifty.BotRefillsSpecials
                    && fifty.Refill && fifty.TargetMovesEachSet);
        Assert.Equal(2, Ladder.At(18).FaceUpCards);           // ...and the stage before a boss has none of it
        Assert.False(Ladder.At(28).DeckChangesEachSet);
    }

    [Fact]
    public void The_stage_banner_names_every_rule_in_play()
    {
        Assert.Equal(string.Empty, Ladder.RulesLines(3));
        Assert.Equal("Face down after two cards", Ladder.RulesLines(9));
        Assert.Contains("after one card", Ladder.RulesLines(19, 30)); // a boss below the head start
        string fifty = Ladder.RulesLines(49);
        Assert.Contains("Face down near the target", fifty);
        Assert.Contains("dealt to within 20", fifty);
        Assert.Contains("Decks change every set", fifty);
        Assert.Contains("refill from your deck", fifty);
        Assert.Contains("Target changes every set", fifty);
    }

    // ------------------------------------------------------------------
    // Climb and swing (2026-10-22)
    // ------------------------------------------------------------------

    [Fact]
    public void Each_tier_above_the_first_climbs_higher_and_swings_low()
    {
        int lastTop = 25;
        for (int tier = 1; tier < Ladder.TierCount; tier++)
        {
            List<int> targets = Enumerable.Range(tier * 10, 9).Select(i => Ladder.At(i).TargetScore).ToList();
            Assert.True(targets.Max() > lastTop, $"tier {tier + 1} climbs past the tier below");
            Assert.True(targets.Count(t => t <= 16) >= 3, $"tier {tier + 1} has low stages");
            lastTop = targets.Max();
        }
        Assert.InRange(lastTop, 70, 80);
        Assert.Equal(20, Ladder.At(0).TargetScore); // tier 1 is untouched
        Assert.Equal(22, Ladder.At(8).TargetScore);
    }

    [Fact]
    public void A_tier_boss_rolls_its_target_in_the_tiers_top_range()
    {
        for (int seed = 1; seed < 8; seed++)
        {
            RunData run = NewRun(seed);
            ClimbTo(run, 49);
            Assert.InRange(run.CurrentTarget, 70, 80);
        }
        RunData twenty = NewRun();
        ClimbTo(twenty, 19);
        Assert.InRange(twenty.CurrentTarget, 36, 42);
    }

    [Fact]
    public void Reveal_distance_grows_with_the_target_and_is_off_at_thirty_or_less()
    {
        Assert.Equal(0, Ladder.RevealDistanceFor(30, false));
        Assert.Equal(10, Ladder.RevealDistanceFor(35, false));
        Assert.Equal(10, Ladder.RevealDistanceFor(40, false));
        Assert.Equal(15, Ladder.RevealDistanceFor(60, false));
        Assert.Equal(20, Ladder.RevealDistanceFor(80, false));
        Assert.Equal(25, Ladder.RevealDistanceFor(80, true));
    }

    [Fact]
    public void Above_thirty_both_players_are_dealt_to_within_twenty_of_the_target()
    {
        for (int seed = 1; seed < 20; seed++)
        {
            var host = new FakeTableHost();
            host.Rng = new Random(seed);
            host.State.TargetScore = 70;
            var table = new Table(host);
            table.StartSet();
            table.DealTurn();
            Assert.InRange(host.Player1.CurrentScore, 50, 59); // never a bust: the last card is 10 at most
            Assert.InRange(host.Player2.CurrentScore, 50, 59);
        }
    }

    [Fact]
    public void At_thirty_or_less_the_opening_is_still_two_cards()
    {
        var (host, table) = NewTable(target: 30);
        table.StartSet();
        table.DealTurn();
        Assert.Equal(2, host.Player1.ActiveCardsOnBoard.Count);
    }

    [Fact]
    public void At_a_high_target_the_bots_cards_stay_face_up_until_it_nears_the_target()
    {
        for (int seed = 1; seed < 20; seed++)
        {
            var host = new FakeTableHost { VsBot = true };
            host.Rng = new Random(seed);
            host.State.TargetScore = 60;        // distance 15: face down from 45
            host.State.HiddenOpponent = true;
            var table = new Table(host);
            table.StartSet();
            table.DealTurn();
            Player bot = host.Player2;
            Assert.DoesNotContain(bot.ActiveCardsOnBoard, c => c.IsHidden); // the head start is face up

            while (bot.CurrentScore < 45) { table.DealTurn(); }
            // Every card drawn once it was at 45 or more is face down, and none before.
            int running = 0;
            foreach (Card card in bot.ActiveCardsOnBoard)
            {
                Assert.Equal(running >= 45, card.IsHidden);
                running += card.Value;
            }
            if (bot.CurrentScore < 60) { table.DealTurn(); Assert.True(bot.ActiveCardsOnBoard[^1].IsHidden); }
        }
    }

    [Fact]
    public void Once_a_card_is_face_down_the_rest_of_the_set_stays_face_down()
    {
        var (host, table) = NewTable(target: 60);
        host.VsBot = true;
        host.State.HiddenOpponent = true;
        Player bot = host.Player2;
        bot.ActiveCardsOnBoard.Add(new Card(5, CardType.Main) { IsHidden = true });
        Assert.True(table.LandsFaceDown(bot, scoreBefore: 10, cardsBefore: 1));
        Assert.False(table.LandsFaceDown(host.Player1, scoreBefore: 55, cardsBefore: 1)); // never yours
    }

    [Fact]
    public void A_moving_target_moves_around_the_stages_own_number()
    {
        Assert.Equal((Table.MovingTargetMin, Table.MovingTargetMax), Table.MovingTargetRange(0));
        Assert.Equal((64, 84), Table.MovingTargetRange(74));
        Assert.Equal((11, 17), Table.MovingTargetRange(14));

        var (host, table) = NewTable(target: 74);
        host.State.BaseTarget = 74;
        host.State.TargetMovesEachSet = true;
        table.StartSet();
        for (int set = 0; set < 8; set++)
        {
            table.StartSet();
            Assert.InRange(host.State.TargetScore, 64, 84);
        }
    }

    // ------------------------------------------------------------------
    // Choosing where a run starts (2026-10-22)
    // ------------------------------------------------------------------

    [Fact]
    public void A_player_past_stage_ten_can_start_again_from_stage_one_and_keep_their_progress()
    {
        RunData run = NewRun();
        ClimbTo(run, 24);
        run.CompleteMatch(1, won: false);
        Assert.Equal(new List<int> { 0, 10, 20 }, run.StartableSteps());

        run.StartNewRun(0);
        Assert.Equal(0, run.StepIndex);
        Assert.Equal(0, run.RunStartStep);
        Assert.Equal(20, run.CheckpointStep);   // the tiers stay open
        Assert.Equal(24, run.FurthestStep);

        // A loss on the replay goes back to where the replay began, not to the checkpoint.
        ClimbTo(run, 4);
        run.CompleteMatch(0, won: false);
        run.StartNewRun(run.RunStartStep);
        Assert.Equal(0, run.StepIndex);
    }

    [Fact]
    public void A_start_beyond_the_checkpoint_or_mid_tier_is_snapped_back()
    {
        RunData run = NewRun();
        ClimbTo(run, 12);
        run.StartNewRun(35);
        Assert.Equal(10, run.StepIndex);
        run.StartNewRun(7);
        Assert.Equal(0, run.StepIndex);
    }

    [Fact]
    public void Where_the_run_began_survives_a_save()
    {
        RunData run = NewRun();
        ClimbTo(run, 21);
        run.StartNewRun(10);
        RunData loaded = new RunData(new Random(2));
        loaded.LoadSaveJson(run.ToSaveJson());
        Assert.Equal(10, loaded.RunStartStep);

        string old = run.ToSaveJson().Replace("\"runStart\":10,", string.Empty);
        RunData migrated = new RunData(new Random(3));
        migrated.LoadSaveJson(old);
        Assert.Equal(20, migrated.RunStartStep); // an old save began its run at the checkpoint
    }

    [Fact]
    public void Deck_shapes_hold_what_they_say()
    {
        Assert.Equal(28, DeckShapes.Values(DeckShape.High).Count);
        Assert.True(DeckShapes.Values(DeckShape.High).All(v => v >= 4));
        Assert.True(DeckShapes.Values(DeckShape.Low).All(v => v <= 7));
        Assert.DoesNotContain(5, DeckShapes.Values(DeckShape.NoMiddle));
        Assert.DoesNotContain(6, DeckShapes.Values(DeckShape.NoMiddle));
        Assert.Equal(8, DeckShapes.Values(DeckShape.ExtraTens).Count(v => v == 10));
        Assert.Equal(40, DeckShapes.Values(DeckShape.Standard).Count);
    }

    // ------------------------------------------------------------------
    // Checkpoints and Endless
    // ------------------------------------------------------------------

    [Fact]
    public void Beating_stage_ten_goes_on_to_stage_eleven_and_opens_endless()
    {
        RunData run = NewRun();
        ClimbTo(run, 10);
        Assert.True(run.RunActive);
        Assert.False(run.RunComplete);
        Assert.Equal(11, run.MatchNumber);
        Assert.True(run.EndlessUnlocked);
        Assert.Equal(10, run.CheckpointStep);
    }

    [Fact]
    public void A_loss_restarts_at_the_tiers_first_stage_not_at_stage_one()
    {
        RunData run = NewRun();
        ClimbTo(run, 33);                 // stage 34
        run.CompleteMatch(1, won: false);
        Assert.False(run.RunActive);

        run.StartNewRun();
        Assert.Equal(30, run.StepIndex);  // stage 31
    }

    [Fact]
    public void A_loss_before_any_boss_is_beaten_still_starts_over_at_stage_one()
    {
        RunData run = NewRun();
        ClimbTo(run, 7);
        run.CompleteMatch(0, won: false);
        run.StartNewRun();
        Assert.Equal(0, run.StepIndex);
        Assert.Equal(0, run.CheckpointStep);
    }

    [Fact]
    public void Clearing_all_fifty_completes_the_run()
    {
        RunData run = NewRun();
        ClimbTo(run, 50);
        Assert.True(run.RunComplete);
        Assert.Equal(40, run.CheckpointStep); // a new run starts the last tier, never past the end
    }

    [Fact]
    public void Endless_still_plays_on_stage_tens_rules()
    {
        RunData run = NewRun();
        ClimbTo(run, 25);
        run.StartEndless();
        Assert.Equal(Ladder.EndlessStepIndex, run.StepIndex);
        Assert.Equal(DeckShape.Standard, run.CurrentStep.Deck);
        Assert.False(run.CurrentStep.Refill);
    }

    [Fact]
    public void A_stage_above_ten_rolls_its_specials_on_arrival_but_keeps_its_ranks_target()
    {
        RunData run = NewRun();
        ClimbTo(run, 12); // Silver Challenger II: target 23, two specials... one in the first four
        Assert.NotNull(run.CurrentRolledEffects);
        Assert.Single(run.CurrentRolledEffects);
        Assert.Equal(Ladder.At(12).TargetScore, run.CurrentTarget);
    }

    [Fact]
    public void The_checkpoint_survives_a_save_and_an_old_save_that_beat_stage_ten_starts_at_eleven()
    {
        RunData run = NewRun();
        ClimbTo(run, 21);
        RunData loaded = new RunData(new Random(2));
        loaded.LoadSaveJson(run.ToSaveJson());
        Assert.Equal(20, loaded.CheckpointStep);

        string old = run.ToSaveJson().Replace("\"checkpoint\":20,", string.Empty).Replace("\"furthest\":21", "\"furthest\":10");
        RunData migrated = new RunData(new Random(3));
        migrated.LoadSaveJson(old);
        Assert.Equal(10, migrated.CheckpointStep);
    }

    // ------------------------------------------------------------------
    // The tier sets
    // ------------------------------------------------------------------

    [Fact]
    public void Each_tier_set_opens_with_its_boss()
    {
        RunData run = NewRun();
        ClimbTo(run, 19);
        Assert.False(run.CosmeticUnlocked("jade"));
        run.CompleteMatch(3, won: true); // stage 20
        Assert.True(run.CosmeticUnlocked("jade"));
        Assert.False(run.CosmeticUnlocked("sapphire"));
        Assert.Equal(49, Cosmetics.UnlockStepIndex("diamond"));
        Assert.Equal(0, Cosmetics.UnlockStepIndex("bronze")); // the rank sets are unchanged
        Assert.Equal(8, Cosmetics.UnlockStepIndex("obsidian"));
    }

    // ------------------------------------------------------------------
    // The rules in play
    // ------------------------------------------------------------------

    private static (FakeTableHost host, Table table) NewTable(int target = 20)
    {
        var host = new FakeTableHost();
        host.State.TargetScore = target;
        var table = new Table(host);
        return (host, table);
    }

    [Fact]
    public void A_shaped_deck_deals_only_its_own_cards()
    {
        var (host, table) = NewTable();
        host.State.Deck = DeckShape.High;
        table.StartSet();
        Assert.Equal(28, table.Remaining(host.Player1));
        int[] counts = table.DeckCounts(host.Player1);
        Assert.Equal(0, counts[0] + counts[1] + counts[2]); // no 1s, 2s or 3s
    }

    [Fact]
    public void A_moving_target_holds_for_the_first_set_then_moves_every_set_after()
    {
        var (host, table) = NewTable(target: 23);
        host.State.TargetMovesEachSet = true;
        table.StartSet();
        Assert.Equal(23, host.State.TargetScore);
        Assert.False(table.TargetMovedThisSet);

        for (int set = 0; set < 6; set++)
        {
            int was = host.State.TargetScore;
            table.StartSet();
            Assert.True(table.TargetMovedThisSet);
            Assert.NotEqual(was, host.State.TargetScore);
            Assert.InRange(host.State.TargetScore, Table.MovingTargetMin, Table.MovingTargetMax);
        }
    }

    [Fact]
    public void A_changing_deck_reshapes_every_set_after_the_first()
    {
        var (host, table) = NewTable();
        host.State.Deck = DeckShape.Low;
        host.State.DeckChangesEachSet = true;
        table.StartSet();
        Assert.Equal(DeckShape.Low, host.State.Deck);
        table.StartSet();
        Assert.True(table.DeckChangedThisSet);
        Assert.NotEqual(DeckShape.Low, host.State.Deck);
        Assert.NotEqual(DeckShape.Standard, host.State.Deck);
    }

    [Fact]
    public void Refill_deals_the_rest_of_your_deck_behind_your_hand_and_replaces_each_card_played()
    {
        var (host, table) = NewTable();
        host.State.RefillHands = true;
        host.Run = NewRun();
        table.StartSet();
        table.DealMatchHands();

        Player you = host.Player1;
        Assert.Equal(4, you.Modifiers.Count);
        Assert.Equal(RunData.SideDeckSize - 4, you.RefillPile.Count);

        Card next = you.RefillPile[0];
        Assert.True(you.PlayModifierCard(you.Modifiers[0], host.State));
        Assert.Equal(4, you.Modifiers.Count);
        Assert.Contains(next, you.Modifiers);
        Assert.Equal(RunData.SideDeckSize - 5, you.RefillPile.Count);
    }

    [Fact]
    public void Without_the_refill_rule_there_is_nothing_waiting_behind_the_hand()
    {
        var (host, table) = NewTable();
        host.Run = NewRun();
        table.StartSet();
        table.DealMatchHands();
        Assert.Empty(host.Player1.RefillPile);
        Assert.True(host.Player1.PlayModifierCard(host.Player1.Modifiers[0], host.State));
        Assert.Equal(3, host.Player1.Modifiers.Count);
    }

    [Fact]
    public void A_rescue_card_is_not_replaced()
    {
        Player you = TestTable.Player("You");
        Card rescue = TestTable.Rescue(3);
        you.Modifiers.Add(rescue);
        you.RefillPile.Add(TestTable.Mod(2));
        Assert.True(you.PlayModifierCard(rescue, new GameState()));
        Assert.Empty(you.Modifiers);
        Assert.Single(you.RefillPile);
    }

    [Fact]
    public void An_effect_card_played_is_replaced_too()
    {
        var (host, table) = NewTable();
        Player you = host.Player1, them = host.Player2;
        TestTable.Draw(you, 9);
        TestTable.Draw(them, 7);
        Card trade = TestTable.Effect(CardEffect.TradeTotals);
        you.Modifiers.Add(trade);
        Card waiting = TestTable.Mod(1);
        you.RefillPile.Add(waiting);

        Assert.True(table.TryPlayEffect(you, trade, null, out _));
        Assert.Contains(waiting, you.Modifiers);
    }
}
