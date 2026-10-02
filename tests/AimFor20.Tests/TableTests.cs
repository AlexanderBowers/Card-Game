using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

/// A stand-in for GameManager: just enough of a game around the table to drive it.
internal sealed class FakeTableHost : ITableHost
{
    public Player Player1 { get; } = new Player("You");
    public Player Player2 { get; } = new Player("Them");
    public GameState State { get; } = new GameState();
    public Random Rng { get; set; } = new Random(7);
    public RunData Run { get; set; }
    public bool VsBot { get; set; } = true;
    public bool LocalSpecials { get; set; }
    public bool TutorialStaged { get; set; }
    public IReadOnlyList<int> TutorialOpening { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> TutorialModifiers { get; set; } = Array.Empty<int>();
    public List<CardEffect> LocalSpecialsUnlocked { get; set; } = new List<CardEffect>();

    public readonly List<(Player player, Card card)> Shown = new List<(Player, Card)>();
    public int BotHandsDealt;
    public bool? LastIntroduce;

    public List<CardEffect> UnlockedLocalSpecials() => LocalSpecialsUnlocked;
    public void DealBotHand() => BotHandsDealt++;
    public void ShowDrawnCard(Player player, Card card, float delay) => Shown.Add((player, card));
    public void HandsDealt(bool introduceCards) => LastIntroduce = introduceCards;
}

/// The cards on the table: the two forty-card decks, the opening deal, and the match hands.
public class TableTests
{
    private static (FakeTableHost host, Table table) NewTable(int target = 20)
    {
        var host = new FakeTableHost();
        host.State.TargetScore = target;
        var table = new Table(host);
        table.StartSet();
        return (host, table);
    }

    [Fact]
    public void Each_player_has_their_own_forty_card_deck()
    {
        var (host, table) = NewTable();
        Assert.Equal(40, table.Remaining(host.Player1));
        Assert.Equal(40, table.Remaining(host.Player2));
    }

    [Fact]
    public void The_opening_deal_is_two_cards_at_a_target_of_20_or_more_then_one_a_turn()
    {
        var (host, table) = NewTable(target: 20);
        table.DealTurn();
        Assert.Equal(2, host.Player1.ActiveCardsOnBoard.Count);
        Assert.Equal(2, host.Player2.ActiveCardsOnBoard.Count);
        Assert.Equal(38, table.Remaining(host.Player1));

        table.DealTurn();
        Assert.Equal(3, host.Player1.ActiveCardsOnBoard.Count);
    }

    [Fact]
    public void Below_20_the_opening_deal_is_one_card_because_two_could_bust()
    {
        var (host, table) = NewTable(target: 18);
        table.DealTurn();
        Assert.Single(host.Player1.ActiveCardsOnBoard);
    }

    [Fact]
    public void A_drawn_card_scores_lands_on_the_board_and_is_shown()
    {
        var (host, table) = NewTable(target: 18);
        table.DealTurn();
        Card drawn = host.Player1.ActiveCardsOnBoard.Single();
        Assert.Equal(CardType.Main, drawn.Type);
        Assert.Equal(drawn.Value, host.Player1.CurrentScore);
        Assert.Same(drawn, host.Player1.LastDrawnCard);
        Assert.Contains((host.Player1, drawn), host.Shown);
    }

    [Fact]
    public void A_holding_player_draws_nothing()
    {
        var (host, table) = NewTable(target: 18);
        host.Player2.IsHolding = true;
        table.DealTurn();
        Assert.Empty(host.Player2.ActiveCardsOnBoard);
        Assert.Equal(40, table.Remaining(host.Player2));
    }

    [Fact]
    public void Forty_draws_are_exactly_four_of_each_value_then_the_deck_reshuffles()
    {
        var (host, table) = NewTable(target: 18);
        host.Player2.IsHolding = true;
        var drawn = new List<int>();
        for (int i = 0; i < 40; i++)
        {
            table.DealTurn();
            drawn.Add(host.Player1.LastDrawnCard.Value);
        }
        Assert.All(Enumerable.Range(1, 10), v => Assert.Equal(4, drawn.Count(d => d == v)));
        Assert.Equal(0, table.Remaining(host.Player1));

        table.DealTurn(); // an empty deck reshuffles rather than crashing
        Assert.Equal(39, table.Remaining(host.Player1));
    }

    [Fact]
    public void A_new_turn_clears_last_turns_draws_effects_and_recall_lock()
    {
        var (host, table) = NewTable();
        table.DealTurn();
        Card recalled = TestTable.Mod(3);
        table.NoteEffectPlayed(host.Player1);
        table.LockRecall(host.Player1, recalled);
        host.Player1.HasEndedTurn = true;

        Assert.True(table.HasPlayedEffect(host.Player1));
        Assert.False(table.HasPlayedEffect(host.Player2));
        Assert.True(table.IsRecallLocked(host.Player1, recalled));
        Assert.False(table.IsRecallLocked(host.Player2, recalled));

        table.DealTurn();
        Assert.False(table.HasPlayedEffect(host.Player1));
        Assert.False(table.IsRecallLocked(host.Player1, recalled));
        Assert.False(host.Player1.HasEndedTurn);
    }

    [Fact]
    public void The_tutorials_staged_opening_is_drawn_in_order_and_decks_stay_whole()
    {
        var host = new FakeTableHost
        {
            TutorialStaged = true,
            // From the end: You, Them, You, Them.
            TutorialOpening = new[] { 1, 2, 3, 4 },
        };
        var table = new Table(host);
        table.StartSet();
        table.DealTurn();

        Assert.Equal(new[] { 4, 2 }, host.Player1.ActiveCardsOnBoard.Select(c => c.Value));
        Assert.Equal(new[] { 3, 1 }, host.Player2.ActiveCardsOnBoard.Select(c => c.Value));
        Assert.Equal(38, table.Remaining(host.Player1));
    }

    [Fact]
    public void A_runless_match_deals_a_plain_four_card_hand_and_lets_the_bot_build_its_own()
    {
        var (host, table) = NewTable();
        table.DealMatchHands();
        Assert.Equal(Table.HandSize, host.Player1.Modifiers.Count);
        Assert.All(host.Player1.Modifiers, c =>
        {
            Assert.Equal(CardType.Modifier, c.Type);
            Assert.InRange(Math.Abs(c.Value), 1, Table.MaxModifierMagnitude);
        });
        Assert.Equal(1, host.BotHandsDealt);
        Assert.True(host.LastIntroduce);
    }

    [Fact]
    public void In_a_run_the_hand_comes_from_the_players_deck()
    {
        var (host, table) = NewTable();
        var run = new RunData(new Random(3));
        run.StartNewRun();
        run.AddToInventory(new ModifierDef(0, false, CardEffect.Shave));
        run.SetSideDeck(new[] { 14, 13, 12, 11 }); // Shave and the last three starters
        host.Run = run;

        table.DealMatchHands();

        Assert.Equal(4, host.Player1.Modifiers.Count);
        Assert.Contains(host.Player1.Modifiers, c => c.Effect == CardEffect.Shave);
    }

    [Fact]
    public void The_staged_tutorial_hand_does_not_introduce_itself()
    {
        var host = new FakeTableHost { TutorialStaged = true, TutorialModifiers = new[] { 4, -2, 3, -1 } };
        var table = new Table(host);
        table.DealMatchHands();
        Assert.Equal(new[] { 4, -2, 3, -1 }, host.Player1.Modifiers.Select(c => c.Value));
        Assert.False(host.LastIntroduce);
    }

    [Fact]
    public void Local_specials_put_one_met_effect_in_the_hand()
    {
        var (host, table) = NewTable();
        host.VsBot = false;
        host.LocalSpecials = true;
        host.LocalSpecialsUnlocked = new List<CardEffect> { CardEffect.Shave };
        table.DealMatchHands();
        Assert.Single(host.Player1.Modifiers, c => c.Effect == CardEffect.Shave);
    }

    [Fact]
    public void At_Bronze_stage_1_the_bot_never_draws_onto_the_target()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            var host = new FakeTableHost { Rng = new Random(seed) };
            var run = new RunData(new Random(seed));
            run.StartNewRun();
            host.Run = run;
            var table = new Table(host);
            table.StartSet();
            host.Player1.IsHolding = true;
            for (int turn = 0; turn < 8 && host.Player2.CurrentScore < 20; turn++)
            {
                table.DealTurn();
                Assert.NotEqual(20, host.Player2.CurrentScore);
            }
        }
    }
}

/// When a set ends and who takes it.
public class SetRulesTests
{
    [Theory]
    [InlineData(19, 18, 20, 1)]
    [InlineData(18, 20, 20, 2)]
    [InlineData(21, 15, 20, 2)]  // a bust loses outright
    [InlineData(15, 23, 20, 1)]
    [InlineData(21, 22, 20, 0)]  // both bust: replayed
    [InlineData(17, 17, 20, 0)]  // same score: replayed
    [InlineData(20, 20, 20, 0)]
    public void The_set_goes_to_whoever_is_closer_without_going_over(int p1, int p2, int target, int winner)
    {
        Assert.Equal(winner, SetRules.Winner(p1, p2, target));
    }

    [Fact]
    public void A_set_ends_on_a_bust_or_when_both_hold()
    {
        Player a = TestTable.Player("You", 15);
        Player b = TestTable.Player("Them", 12);
        Assert.False(SetRules.IsSetOver(a, b, 20));

        a.IsHolding = true;
        Assert.False(SetRules.IsSetOver(a, b, 20));
        b.IsHolding = true;
        Assert.True(SetRules.IsSetOver(a, b, 20));

        Player bust = TestTable.Player("Them", 21);
        Assert.True(SetRules.IsSetOver(TestTable.Player("You", 10), bust, 20));
    }
}
