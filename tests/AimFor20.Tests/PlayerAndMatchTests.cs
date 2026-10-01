using System;
using Xunit;
using static TestTable;

/// The hand, the spent pile, and the best-of-five match.
public class PlayerTests
{
    [Fact]
    public void Playing_a_Modifier_moves_it_to_the_board_and_the_spent_pile()
    {
        Card two = Mod(2);
        Player p = Player("You", 10, hand: two);

        Assert.True(p.PlayModifierCard(two, new GameState()));

        Assert.Equal(12, p.CurrentScore);
        Assert.DoesNotContain(two, p.Modifiers);
        Assert.Contains(two, p.ActiveCardsOnBoard);
        Assert.Contains(two, p.SpentCards);
        Assert.Same(two, p.LastPlayedModifier);
    }

    [Fact]
    public void A_card_not_in_the_hand_cannot_be_played()
    {
        Player p = Player("You", 10);
        Assert.False(p.PlayModifierCard(Mod(2), new GameState()));
        Assert.Equal(10, p.CurrentScore);
    }

    [Fact]
    public void A_rescue_card_is_never_put_on_the_spent_pile()
    {
        Card rescue = Rescue(-7);
        Player p = Player("You", 25, hand: rescue);
        p.PlayModifierCard(rescue, new GameState());
        Assert.Equal(18, p.CurrentScore);
        Assert.DoesNotContain(rescue, p.SpentCards);
    }

    [Fact]
    public void The_spent_pile_lasts_the_match_not_the_set()
    {
        Card two = Mod(2);
        Player p = Player("You", hand: two);
        p.PlayModifierCard(two, new GameState());

        p.ResetForNewSet();
        Assert.Equal(0, p.CurrentScore);
        Assert.Empty(p.ActiveCardsOnBoard);
        Assert.Contains(two, p.SpentCards);

        p.ResetForNewMatch();
        Assert.Empty(p.SpentCards);
    }

    [Fact]
    public void Rescue_cards_last_one_set()
    {
        Player p = Player("You", hand: new[] { Mod(1), Rescue(-4), Mod(-2) });
        Assert.Equal(1, p.DiscardRescueCards());
        Assert.Equal(new[] { 1, -2 }, Values(p.Modifiers));
    }

    [Fact]
    public void A_dealt_hand_always_has_a_way_up_and_a_way_down()
    {
        for (int seed = 0; seed < 2000; seed++)
        {
            Player p = new Player("You");
            p.DealRandomModifiers(new Random(seed));

            Assert.Equal(4, p.Modifiers.Count);
            Assert.Contains(p.Modifiers, c => c.CanFlipValue || c.Value > 0);
            Assert.Contains(p.Modifiers, c => c.CanFlipValue || c.Value < 0);
            Assert.All(p.Modifiers, c => Assert.InRange(Math.Abs(c.Value), 1, 4));
        }
    }

    [Fact]
    public void EnsureBothSigns_leaves_effect_cards_alone()
    {
        Card shave = Effect(CardEffect.Shave);
        Player p = Player("Bot", hand: new[] { Mod(2), Mod(3), shave });
        p.EnsureBothSigns(new Random(7));

        Assert.Contains(shave, p.Modifiers);
        Assert.Contains(p.Modifiers, c => c.Effect == CardEffect.None && c.Value < 0);
    }
}

public class CardTests
{
    [Fact]
    public void Only_a_flip_card_can_change_sign()
    {
        Card flip = Mod(3, flip: true);
        Assert.True(flip.FlipValue());
        Assert.Equal(-3, flip.Value);
        Assert.Equal("-3", flip.DisplayText);

        Card plain = Mod(3);
        Assert.False(plain.FlipValue());
        Assert.Equal(3, plain.Value);
    }

    [Fact]
    public void Display_text_follows_the_card_kind()
    {
        Assert.Equal("7", MainCard(7).DisplayText);
        Assert.Equal("+2", Mod(2).DisplayText);
        Assert.Equal("-1", Mod(-1).DisplayText);
        Assert.Equal(CardEffects.Glyph(CardEffect.Veto), Effect(CardEffect.Veto).DisplayText);
    }

    [Fact]
    public void Every_card_gets_its_own_id()
    {
        Assert.NotEqual(Mod(1).Id, Mod(1).Id);
    }
}

public class GameStateTests
{
    [Fact]
    public void A_match_is_best_of_five()
    {
        GameState state = new GameState();
        Assert.True(state.IsFirstSet);

        state.RecordSetWinner(1);
        state.RecordSetWinner(2);
        state.RecordSetWinner(1);
        Assert.False(state.IsFirstSet);
        Assert.False(state.CheckMatchWinner(out _));
        Assert.False(state.IsGameOver);

        state.RecordSetWinner(1);
        Assert.True(state.CheckMatchWinner(out int winner));
        Assert.Equal(1, winner);
        Assert.True(state.IsGameOver);
    }

    [Fact]
    public void A_tied_set_is_still_the_first_set()
    {
        GameState state = new GameState();
        state.RecordSetWinner(0); // a tie records no winner
        Assert.True(state.IsFirstSet);
    }
}
