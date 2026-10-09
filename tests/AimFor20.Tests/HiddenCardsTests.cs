using System.Linq;
using Xunit;

/// The hidden-card rule (2026-10-09): on the boss rung and in Endless the bot's cards after its
/// first two each set land face down, its hand is face down all match, and only what the player
/// is TOLD changes - never what the cards are worth.
public class HiddenCardsTests
{
    private static (FakeTableHost host, Table table) NewTable(bool hidden = true, int target = 20, bool vsBot = true)
    {
        var host = new FakeTableHost { VsBot = vsBot };
        host.State.TargetScore = target;
        host.State.HiddenOpponent = hidden;
        var table = new Table(host);
        table.StartSet();
        return (host, table);
    }

    /// The bot's next draw, face down as the rule lays it, without going through a whole turn.
    private static Card HiddenDraw(Player p, int value)
    {
        Card card = TestTable.Draw(p, value);
        card.IsHidden = true;
        return card;
    }

    [Fact]
    public void The_bots_first_two_cards_are_face_up_and_every_draw_after_them_is_face_down()
    {
        var (host, table) = NewTable();
        table.DealTurn(); // the two-card opening
        table.DealTurn();
        table.DealTurn();

        var bot = host.Player2.ActiveCardsOnBoard;
        Assert.Equal(4, bot.Count);
        Assert.False(bot[0].IsHidden);
        Assert.False(bot[1].IsHidden);
        Assert.True(bot[2].IsHidden);
        Assert.True(bot[3].IsHidden);
        Assert.DoesNotContain(host.Player1.ActiveCardsOnBoard, c => c.IsHidden); // yours never are
    }

    [Fact]
    public void Below_20_the_second_face_up_card_is_the_next_turns_draw()
    {
        var (host, table) = NewTable(target: 18); // a one-card opening
        table.DealTurn();
        table.DealTurn();
        table.DealTurn();

        var bot = host.Player2.ActiveCardsOnBoard;
        Assert.False(bot[0].IsHidden);
        Assert.False(bot[1].IsHidden);
        Assert.True(bot[2].IsHidden);
    }

    [Fact]
    public void Nothing_is_hidden_when_the_rung_does_not_hide_or_in_local_two_player()
    {
        foreach (var (hidden, vsBot) in new[] { (false, true), (true, false) })
        {
            var (host, table) = NewTable(hidden, vsBot: vsBot);
            for (int i = 0; i < 4; i++) table.DealTurn();
            Assert.DoesNotContain(host.Player2.ActiveCardsOnBoard, c => c.IsHidden);
        }
    }

    [Fact]
    public void The_bots_match_hand_is_dealt_face_down_and_yours_is_not()
    {
        var (host, table) = NewTable();
        host.Player2.Modifiers.AddRange(new[] { TestTable.Mod(3), TestTable.Mod(-2), TestTable.Effect(CardEffect.Copy) });
        table.DealMatchHands();

        Assert.All(host.Player2.Modifiers, c => Assert.True(c.IsHidden));
        Assert.DoesNotContain(host.Player1.Modifiers, c => c.IsHidden);
    }

    [Fact]
    public void A_face_down_board_shows_only_what_its_face_up_cards_add_up_to()
    {
        Player bot = TestTable.Player("Them");
        TestTable.Draw(bot, 6);
        TestTable.Draw(bot, 5);
        HiddenDraw(bot, 7);

        Assert.True(bot.HasHiddenCards);
        Assert.Equal(7, bot.HiddenTotal);
        Assert.Equal(11, bot.CurrentScore - bot.HiddenTotal); // the table reads "11+?"
        Assert.Equal(18, bot.CurrentScore);                    // the rules read 18
    }

    [Fact]
    public void The_set_end_turns_the_bots_board_over_but_not_its_hand()
    {
        var (host, table) = NewTable();
        Player bot = host.Player2;
        TestTable.Draw(bot, 6);
        TestTable.Draw(bot, 5);
        Card down = HiddenDraw(bot, 7);
        Card inHand = TestTable.Mod(2);
        inHand.IsHidden = true;
        bot.Modifiers.Add(inHand);

        var turned = table.RevealOpponentBoard();

        Assert.Equal(new[] { down }, turned);
        Assert.False(bot.HasHiddenCards);
        Assert.True(inHand.IsHidden);
    }

    [Fact]
    public void An_effect_card_from_a_face_down_hand_is_played_face_up()
    {
        var (host, table) = NewTable();
        Player you = host.Player1, bot = host.Player2;
        TestTable.Draw(you, 10);
        TestTable.Draw(bot, 9);
        Card trade = TestTable.Effect(CardEffect.TradeTotals);
        trade.IsHidden = true;
        bot.Modifiers.Add(trade);

        Assert.True(table.TryPlayEffect(bot, trade, null, out _));
        Assert.False(trade.IsHidden);
    }

    [Fact]
    public void Trade_Totals_turns_the_whole_board_over_because_your_score_is_now_theirs()
    {
        var (host, table) = NewTable();
        Player you = host.Player1, bot = host.Player2;
        TestTable.Draw(you, 9);
        TestTable.Draw(bot, 6);
        TestTable.Draw(bot, 5);
        Card a = HiddenDraw(bot, 4);
        Card b = HiddenDraw(bot, 3);
        Card trade = TestTable.Effect(CardEffect.TradeTotals);
        you.Modifiers.Add(trade);

        Assert.True(table.TryPlayEffect(you, trade, null, out Table.EffectPlay play));
        Assert.Equal(new[] { a, b }, play.Revealed);
        Assert.False(bot.HasHiddenCards);
    }

    [Fact]
    public void Copy_may_be_played_blind_onto_a_face_down_card_of_the_same_value_and_turns_it_over()
    {
        var (host, table) = NewTable();
        Player you = host.Player1, bot = host.Player2;
        TestTable.Draw(bot, 6);
        TestTable.Draw(bot, 5);
        Card theirs = HiddenDraw(bot, 7);
        TestTable.Draw(you, 7); // the same value: refused face up, a gamble face down
        Card copy = TestTable.Effect(CardEffect.Copy);
        you.Modifiers.Add(copy);

        Assert.True(CardEffects.CanPlay(copy, you, bot, TestTable.Target));
        Assert.Equal("Your 7 becomes a copy of their face-down card", table.EffectPreview(you, copy));
        Assert.True(table.TryPlayEffect(you, copy, null, out Table.EffectPlay play));
        Assert.Contains(theirs, play.Revealed);
        Assert.False(theirs.IsHidden);
    }

    [Fact]
    public void Shave_is_played_blind_at_a_face_down_hold_and_changes_nothing_on_the_target()
    {
        var (host, table) = NewTable();
        Player you = host.Player1, bot = host.Player2;
        TestTable.Draw(bot, 10);
        TestTable.Draw(bot, 6);
        HiddenDraw(bot, 4); // exactly 20, face down
        bot.IsHolding = true;
        Card shave = TestTable.Effect(CardEffect.Shave);
        you.Modifiers.Add(shave);

        Assert.True(CardEffects.CanPlay(shave, you, bot, TestTable.Target)); // face up it would be refused
        Assert.DoesNotContain("20", table.EffectPreview(you, shave));
        Assert.True(table.TryPlayEffect(you, shave, null, out Table.EffectPlay play));
        Assert.Equal(20, bot.CurrentScore);
        Assert.Contains("on the target", play.Narration);
    }

    [Fact]
    public void Shave_below_the_target_says_what_it_did_without_their_score()
    {
        var (host, table) = NewTable();
        Player you = host.Player1, bot = host.Player2;
        TestTable.Draw(bot, 10);
        TestTable.Draw(bot, 3);
        HiddenDraw(bot, 4); // 17, face down
        bot.IsHolding = true;
        Card shave = TestTable.Effect(CardEffect.Shave);
        you.Modifiers.Add(shave);

        Assert.True(table.TryPlayEffect(you, shave, null, out Table.EffectPlay play));
        Assert.Equal(16, bot.CurrentScore);
        Assert.DoesNotContain("17", play.Narration);
        Assert.DoesNotContain("16", play.Narration);
    }

    [Fact]
    public void Veto_shows_the_card_it_burns_but_not_the_score_it_leaves()
    {
        var (host, table) = NewTable();
        Player you = host.Player1, bot = host.Player2;
        TestTable.Draw(bot, 10);
        TestTable.Draw(bot, 2);
        HiddenDraw(bot, 5); // 17, face down
        Card played = TestTable.Mod(3);
        played.IsHidden = true;
        bot.Modifiers.Add(played);
        bot.PlayModifierCard(played, host.State); // 20, the +3 face down
        Card veto = TestTable.Effect(CardEffect.Veto);
        you.Modifiers.Add(veto);

        Assert.Equal("Destroy the face-down Modifier they just played", table.EffectPreview(you, veto));
        Assert.True(table.TryPlayEffect(you, veto, null, out Table.EffectPlay play));
        Assert.Same(played, play.Destroyed);
        Assert.Contains(played, play.Revealed);
        Assert.Contains("+3", play.Narration);
        Assert.DoesNotContain("17", play.Narration);
        Assert.DoesNotContain("20", play.Narration);
    }

    [Fact]
    public void Trade_Hands_turns_their_hand_face_up_as_it_becomes_yours()
    {
        var (host, table) = NewTable();
        Player you = host.Player1, bot = host.Player2;
        Card theirs = TestTable.Mod(4);
        theirs.IsHidden = true;
        bot.Modifiers.Add(theirs);
        you.Modifiers.Add(TestTable.Mod(-1));
        Card trade = TestTable.Effect(CardEffect.TradeHands);
        you.Modifiers.Add(trade);

        Assert.True(table.TryPlayEffect(you, trade, null, out Table.EffectPlay play));
        Assert.Contains(theirs, you.Modifiers);
        Assert.False(theirs.IsHidden);
        Assert.DoesNotContain(bot.Modifiers, c => c.IsHidden);
    }

    [Fact]
    public void Trade_Totals_against_a_face_down_board_is_previewed_without_their_number()
    {
        var (host, table) = NewTable();
        Player you = host.Player1, bot = host.Player2;
        TestTable.Draw(you, 9);
        TestTable.Draw(bot, 6);
        TestTable.Draw(bot, 5);
        HiddenDraw(bot, 8);
        Card trade = TestTable.Effect(CardEffect.TradeTotals);
        you.Modifiers.Add(trade);

        Assert.Equal("Trade Totals: your 9 for their hidden total", table.EffectPreview(you, trade));
    }

    [Fact]
    public void Only_the_boss_rung_hides_cards_so_every_earlier_rung_still_brings_one_new_idea()
    {
        for (int i = 0; i < Ladder.Length; i++)
            Assert.Equal(i == Ladder.Length - 1, Ladder.At(i).HidesOpponentCards);
    }
}
