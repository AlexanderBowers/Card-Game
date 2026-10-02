using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static TestTable;

/// A game around the bot: a real Table (so effect cards play by the real rules) behind a fake
/// host, with no pauses - so ProcessTurn runs start to finish inside the call.
internal sealed class BotHarness : IBotTable
{
    public readonly FakeTableHost Host = new FakeTableHost();
    public readonly Table Table;
    public readonly Bot Bot;
    public int Resolves;
    public bool TutorialRunning;

    public BotHarness(RunData run = null, int target = 20)
    {
        Host.Run = run;
        Host.State.TargetScore = run?.CurrentTarget ?? target;
        Table = new Table(Host);
        Bot = new Bot(this);
    }

    public Player Me => Host.Player2;
    public Player You => Host.Player1;

    public Player BotPlayer => Host.Player2;
    public Player HumanPlayer => Host.Player1;
    public GameState State => Host.State;
    public RunData Run => Host.Run;
    public Random Rng => Host.Rng;
    public int HandSize => Table.HandSize;
    public int MaxModifierMagnitude => Table.MaxModifierMagnitude;
    public bool VsBot => Host.VsBot;
    public bool LocalSpecials => Host.LocalSpecials;
    public bool BotPlayedEffectThisTurn => Table.HasPlayedEffect(Me);
    public bool TutorialHoldsBot => TutorialRunning;

    public bool IsRecallLocked(Player owner, Card card) => Table.IsRecallLocked(owner, card);
    public bool CanPlayEffect(Player owner, Card card) => Table.CanPlayEffect(owner, card);
    public bool PlayEffectCard(Player owner, Card card, Card chosen = null) => Table.TryPlayEffect(owner, card, chosen, out _);
    public void PlayBotModifier(Card card) => Me.PlayModifierCard(card, State);
    public void DealPlainHand(Player player) => Table.DealPlainHand(player);
    public void AddLocalSpecial(Player player) => Table.AddLocalSpecial(player);
    public void Refresh() { }
    public void ResolveTurn() => Resolves++;
    public Task<bool> Pause(double seconds) => Task.FromResult(true);

    /// Sets the bot's side up and lets it take one turn.
    public void Turn(int score, params Card[] hand)
    {
        Me.CurrentScore = score;
        Me.Modifiers.Clear();
        Me.Modifiers.AddRange(hand);
        Bot.ProcessTurn();
    }
}

/// The bot's hands, ladder rung by ladder rung.
public class BotHandTests
{
    private static RunData RunAt(int stepIndex, int seed)
    {
        var run = new RunData(new Random(seed));
        run.StartNewRun();
        run.DebugJumpToStep(stepIndex);
        return run;
    }

    private static List<Card> DealtAt(int stepIndex, int seed)
    {
        var bot = new BotHarness(RunAt(stepIndex, seed));
        bot.Host.Rng = new Random(seed);
        bot.Bot.DealHand();
        return bot.Me.Modifiers;
    }

    private static bool HasWayUp(List<Card> hand) =>
        hand.Any(c => c.Effect == CardEffect.None && (c.CanFlipValue || c.Value > 0));
    private static bool HasWayDown(List<Card> hand) =>
        hand.Any(c => c.Effect == CardEffect.None && (c.CanFlipValue || c.Value < 0));

    [Fact]
    public void Stage_1_is_four_plain_cards_with_no_flip_and_no_effect()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            List<Card> hand = DealtAt(0, seed);
            Assert.Equal(4, hand.Count);
            Assert.All(hand, c => { Assert.False(c.CanFlipValue); Assert.Equal(CardEffect.None, c.Effect); });
        }
    }

    [Theory]
    [InlineData(1, CardEffect.None)]
    [InlineData(3, CardEffect.Copy)]
    [InlineData(4, CardEffect.TradeTotals)]
    [InlineData(5, CardEffect.Shave)]
    [InlineData(6, CardEffect.TradeHands)]
    [InlineData(7, CardEffect.Recall)]
    [InlineData(8, CardEffect.Veto)]
    public void Each_rung_deals_one_flip_card_and_its_own_effect_card(int stepIndex, CardEffect effect)
    {
        for (int seed = 0; seed < 50; seed++)
        {
            List<Card> hand = DealtAt(stepIndex, seed);
            Assert.Equal(4, hand.Count);
            Assert.True(hand.Count(c => c.CanFlipValue) >= 1, "the rung's +/- card");
            if (effect == CardEffect.None) Assert.All(hand, c => Assert.Equal(CardEffect.None, c.Effect));
            else Assert.Single(hand, c => c.Effect == effect);
            Assert.True(HasWayUp(hand) && HasWayDown(hand), "a hand always has a way up and a way down");
        }
    }

    [Fact]
    public void The_finale_deals_every_rolled_effect()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            RunData run = RunAt(Ladder.Length - 1, seed);
            var bot = new BotHarness(run);
            bot.Bot.DealHand();
            foreach (CardEffect rolled in run.CurrentRolledEffects)
                Assert.Contains(bot.Me.Modifiers, c => c.Effect == rolled);
            Assert.Contains(bot.Me.Modifiers, c => c.CanFlipValue);
        }
    }

    [Fact]
    public void Without_a_run_the_bot_gets_the_plain_random_hand()
    {
        var bot = new BotHarness();
        bot.Bot.DealHand();
        Assert.Equal(Table.HandSize, bot.Me.Modifiers.Count);
        Assert.All(bot.Me.Modifiers, c => Assert.Equal(CardEffect.None, c.Effect));
    }
}

/// What the bot does with a turn.
public class BotTurnTests
{
    private static RunData RunAt(int stepIndex)
    {
        var run = new RunData(new Random(1));
        run.StartNewRun();
        run.DebugJumpToStep(stepIndex);
        return run;
    }

    [Fact]
    public void It_plays_a_Modifier_onto_the_target_and_holds()
    {
        var bot = new BotHarness(); // no run: the Basic bot, target 20
        bot.Turn(18, Mod(2));
        Assert.Equal(20, bot.Me.CurrentScore);
        Assert.True(bot.Me.IsHolding);
        Assert.Equal(1, bot.Resolves);
    }

    [Fact]
    public void It_never_plays_itself_into_a_bust()
    {
        var bot = new BotHarness();
        bot.Turn(18, Mod(4));
        Assert.Equal(18, bot.Me.CurrentScore);
        Assert.Single(bot.Me.Modifiers);
        Assert.True(bot.Me.IsHolding);
    }

    [Fact]
    public void Busted_it_plays_a_minus_card_to_get_back_under()
    {
        var bot = new BotHarness();
        bot.Turn(23, Mod(-4));
        Assert.Equal(19, bot.Me.CurrentScore);
        Assert.True(bot.Me.IsHolding);
    }

    [Fact]
    public void A_flip_card_is_played_whichever_way_up_saves_it()
    {
        var bot = new BotHarness();
        bot.Turn(23, Mod(3, flip: true));
        Assert.Equal(20, bot.Me.CurrentScore);
    }

    [Fact]
    public void Below_its_hold_line_with_nothing_to_play_it_ends_the_turn()
    {
        var bot = new BotHarness();
        bot.Turn(12, Mod(2));
        Assert.Equal(12, bot.Me.CurrentScore);
        Assert.False(bot.Me.IsHolding);
        Assert.True(bot.Me.HasEndedTurn);
    }

    [Fact]
    public void Against_a_locked_score_it_plays_to_beat_it_never_to_tie()
    {
        var bot = new BotHarness();
        bot.You.CurrentScore = 19;
        bot.You.IsHolding = true;
        bot.Turn(18, Mod(1));           // 19 would only tie
        Assert.Equal(18, bot.Me.CurrentScore);
        Assert.False(bot.Me.IsHolding); // and it does not hold short of beating them

        var again = new BotHarness();
        again.You.CurrentScore = 19;
        again.You.IsHolding = true;
        again.Turn(18, Mod(2));
        Assert.Equal(20, again.Me.CurrentScore);
        Assert.True(again.Me.IsHolding);
    }

    [Fact]
    public void Silver_plays_one_card_a_turn_so_two_minus_cards_cannot_save_a_big_bust()
    {
        RunData run = RunAt(2);               // Silver Challenger, target 23
        var bot = new BotHarness(run);
        bot.Turn(30, Mod(-4), Mod(-3));       // neither alone gets under
        Assert.Equal(30, bot.Me.CurrentScore);
        Assert.True(bot.Me.HasEndedTurn);
    }

    [Fact]
    public void From_Gold_up_it_chains_two_minus_cards_back_under()
    {
        RunData run = RunAt(4);               // Gold Challenger, target 18
        var bot = new BotHarness(run);
        bot.Turn(25, Mod(-4), Mod(-3));
        Assert.Equal(18, bot.Me.CurrentScore);
        Assert.Empty(bot.Me.Modifiers);
        Assert.True(bot.Me.IsHolding);
    }

    [Fact]
    public void At_stage_1_it_never_plays_onto_the_target_and_holds_only_on_target_minus_1()
    {
        RunData run = RunAt(0);
        var bot = new BotHarness(run);
        bot.Turn(16, Mod(4));
        Assert.Equal(16, bot.Me.CurrentScore);
        Assert.True(bot.Me.HasEndedTurn);

        var held = new BotHarness(RunAt(0));
        held.Turn(19, Mod(-2));
        Assert.True(held.Me.IsHolding);
        Assert.Equal(19, held.Me.CurrentScore);
    }

    [Fact]
    public void Shave_is_spent_only_when_one_point_settles_a_locked_score()
    {
        var bot = new BotHarness();
        bot.You.CurrentScore = 18;
        bot.You.IsHolding = true;
        bot.Turn(18, Effect(CardEffect.Shave), Mod(5));
        Assert.Equal(17, bot.You.CurrentScore);
        Assert.DoesNotContain(bot.Me.Modifiers, c => c.Effect == CardEffect.Shave);
        Assert.True(bot.Me.IsHolding);

        var ahead = new BotHarness();
        ahead.You.CurrentScore = 18;
        ahead.You.IsHolding = true;
        ahead.Turn(19, Effect(CardEffect.Shave), Mod(5)); // already winning: keep it
        Assert.Equal(18, ahead.You.CurrentScore);
        Assert.Contains(ahead.Me.Modifiers, c => c.Effect == CardEffect.Shave);
    }

    [Fact]
    public void Busted_with_no_plain_way_back_it_spends_Copy_to_take_the_bust_away()
    {
        var bot = new BotHarness();
        bot.You.CurrentScore = 9;
        Draw(bot.You, 3);
        bot.Me.CurrentScore = 14;
        Draw(bot.Me, 10);                 // 24: bust
        bot.Bot.ResetForTurn();
        bot.Me.Modifiers.AddRange(new[] { Effect(CardEffect.Copy), Mod(2) });
        bot.Bot.ProcessTurn();

        Assert.DoesNotContain(bot.Me.Modifiers, c => c.Effect == CardEffect.Copy);
        Assert.InRange(bot.Me.CurrentScore, 17, 20);
    }

    [Fact]
    public void With_no_way_down_left_Recall_brings_back_a_minus_card()
    {
        var bot = new BotHarness();
        Card bigPlus = Mod(4), minus = Mod(-2);
        bot.Me.SpentCards.AddRange(new[] { bigPlus, minus });
        bot.Turn(12, Effect(CardEffect.Recall), Mod(3));

        Assert.Contains(minus, bot.Me.Modifiers);
        Assert.DoesNotContain(bigPlus, bot.Me.Modifiers);
        Assert.True(bot.Table.IsRecallLocked(bot.Me, minus)); // and it cannot be played this turn
    }

    [Fact]
    public void The_tutorial_holds_the_bot_still()
    {
        var bot = new BotHarness { TutorialRunning = true };
        bot.Turn(18, Mod(2));
        Assert.Equal(18, bot.Me.CurrentScore);
        Assert.Equal(0, bot.Resolves);
    }

    [Fact]
    public void A_bot_that_has_finished_its_turn_does_not_act_again()
    {
        var bot = new BotHarness();
        bot.Me.IsHolding = true;
        bot.Turn(15, Mod(5));
        Assert.Equal(15, bot.Me.CurrentScore);
        Assert.Equal(0, bot.Resolves);
    }
}

/// Table.TryPlayEffect: what an effect card does to the cards and the turn, for either side.
public class EffectPlayTests
{
    [Fact]
    public void An_effect_card_is_spent_lands_on_a_board_and_reopens_the_target()
    {
        var host = new FakeTableHost();
        var table = new Table(host);
        Player you = host.Player1, them = host.Player2;
        you.CurrentScore = 18; you.IsHolding = true;
        Card shave = Effect(CardEffect.Shave);
        them.Modifiers.Add(shave);

        Assert.True(table.TryPlayEffect(them, shave, null, out Table.EffectPlay play));
        Assert.Equal(17, you.CurrentScore);
        Assert.DoesNotContain(shave, them.Modifiers);
        Assert.Same(you, play.Target);
        Assert.Contains(shave, play.BoardOwner.ActiveCardsOnBoard);
        Assert.True(table.HasPlayedEffect(them));
        Assert.False(play.Reopened); // a holding player is not re-opened by Shave
    }

    [Fact]
    public void Only_one_card_across_the_table_per_turn()
    {
        var host = new FakeTableHost();
        var table = new Table(host);
        Player you = host.Player1, them = host.Player2;
        you.CurrentScore = 18; you.IsHolding = true;
        Card first = Effect(CardEffect.Shave), second = Effect(CardEffect.Shave);
        them.Modifiers.AddRange(new[] { first, second });

        Assert.True(table.TryPlayEffect(them, first, null, out _));
        Assert.False(table.CanPlayEffect(them, second));
        Assert.NotNull(table.EffectRefusal(them, second));
        Assert.False(table.TryPlayEffect(them, second, null, out _));
        Assert.Contains(second, them.Modifiers); // a refused card is never eaten
    }

    [Fact]
    public void Veto_destroys_the_card_and_unlocks_a_holder()
    {
        var host = new FakeTableHost();
        var table = new Table(host);
        Player you = host.Player1, them = host.Player2;
        Card plus = Mod(3);
        you.CurrentScore = 15;
        you.Modifiers.Add(plus);
        you.PlayModifierCard(plus, host.State); // 18
        you.IsHolding = true;
        Card veto = Effect(CardEffect.Veto);
        them.Modifiers.Add(veto);

        Assert.True(table.TryPlayEffect(them, veto, null, out Table.EffectPlay play));
        Assert.Same(plus, play.Destroyed);
        Assert.Equal(15, you.CurrentScore);
        Assert.False(you.IsHolding);
        Assert.True(play.Reopened);
    }
}
