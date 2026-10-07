using Xunit;
using static TestTable;

/// The six effect cards: when each may be played, and what it does to the two players.
/// Each test names the rule it holds, so a failure says which design decision broke.
public class CardEffectsTests
{
    // ---------------- Copy ----------------

    [Fact]
    public void Copy_needs_a_fresh_draw_on_both_sides_that_differs()
    {
        Player me = Player("You"), them = Player("Them");
        Card copy = Effect(CardEffect.Copy);
        Assert.False(CardEffects.CanPlay(copy, me, them, Target)); // nobody has drawn

        Draw(me, 5);
        Draw(them, 5);
        Assert.False(CardEffects.CanPlay(copy, me, them, Target)); // a 5 onto a 5 changes nothing

        them.LastDrawnCard.Value = 9;
        Assert.True(CardEffects.CanPlay(copy, me, them, Target));

        me.IsHolding = true;
        Assert.False(CardEffects.CanPlay(copy, me, them, Target));
    }

    [Fact]
    public void Copy_works_on_a_holding_opponent_using_their_last_drawn_card()
    {
        Player me = Player("You"), them = Player("Them");
        Draw(them, 6);
        Card theirLast = Draw(them, 9);
        them.LastDrawnCard = null;   // a new turn: they are holding and draw nothing
        them.IsHolding = true;
        Card mine = Draw(me, 4);
        int theirScore = them.CurrentScore;

        Card copy = Effect(CardEffect.Copy);
        Assert.True(CardEffects.CanPlay(copy, me, them, Target));
        Assert.Same(theirLast, CardEffects.CopySource(them));

        var result = CardEffects.Resolve(copy, me, them, Target);
        Assert.True(result.Applied);
        Assert.Equal(9, mine.Value);
        Assert.Equal(9, theirLast.Value);
        Assert.Equal(theirScore, them.CurrentScore); // untouched
        Assert.True(them.IsHolding);
    }

    [Fact]
    public void Copy_rewrites_my_card_and_score_and_leaves_theirs_alone()
    {
        Player me = Player("You"), them = Player("Them");
        Draw(me, 3);
        Card mine = Draw(me, 4);
        Card theirs = Draw(them, 10);

        var result = CardEffects.Resolve(Effect(CardEffect.Copy), me, them, Target);

        Assert.True(result.Applied);
        Assert.False(result.ReopensTarget);
        Assert.Equal(10, mine.Value);       // the same card object, mutated, so its face can be redrawn
        Assert.Equal(13, me.CurrentScore);  // 3 + 10
        Assert.Equal(10, theirs.Value);
        Assert.Equal(10, them.CurrentScore);
    }

    // ---------------- Trade Totals ----------------

    [Fact]
    public void TradeTotals_cannot_take_a_held_score()
    {
        Player me = Player("You", 12), them = Player("Them", 19, holding: true);
        Assert.False(CardEffects.CanPlay(Effect(CardEffect.TradeTotals), me, them, Target));
        them.IsHolding = false;
        Assert.True(CardEffects.CanPlay(Effect(CardEffect.TradeTotals), me, them, Target));
    }

    [Fact]
    public void TradeTotals_swaps_the_scores_and_reopens_them()
    {
        Player me = Player("You", 12), them = Player("Them", 19);
        var result = CardEffects.Resolve(Effect(CardEffect.TradeTotals), me, them, Target);
        Assert.Equal(19, me.CurrentScore);
        Assert.Equal(12, them.CurrentScore);
        Assert.True(result.ReopensTarget);
    }

    // ---------------- Shave ----------------

    [Theory]
    [InlineData(false, 18, false)] // still drawing
    [InlineData(true, 18, true)]   // holding below the target
    [InlineData(true, 20, false)]  // exactly on the target is safe
    public void Shave_only_trims_a_held_score_below_the_target(bool holding, int score, bool legal)
    {
        Player me = Player("You"), them = Player("Them", score, holding);
        Assert.Equal(legal, CardEffects.CanPlay(Effect(CardEffect.Shave), me, them, Target));
    }

    [Fact]
    public void Shave_takes_one_and_cannot_be_answered()
    {
        Player me = Player("You"), them = Player("Them", 18, holding: true);
        var result = CardEffects.Resolve(Effect(CardEffect.Shave), me, them, Target);
        Assert.Equal(17, them.CurrentScore);
        Assert.False(result.ReopensTarget);
        Assert.True(them.IsHolding);
    }

    // ---------------- Trade Hands ----------------

    [Fact]
    public void TradeHands_needs_something_to_take_and_a_rescue_card_is_not_something()
    {
        Player me = Player("You"), them = Player("Them", hand: Rescue(-3));
        Assert.False(CardEffects.CanPlay(Effect(CardEffect.TradeHands), me, them, Target));
        them.Modifiers.Add(Mod(2));
        Assert.True(CardEffects.CanPlay(Effect(CardEffect.TradeHands), me, them, Target));
    }

    [Fact]
    public void TradeHands_swaps_the_hands_but_rescue_cards_stay_with_their_owner()
    {
        Card myRescue = Rescue(-5);
        Player me = Player("You", hand: new[] { Mod(1), myRescue });
        Player them = Player("Them", hand: new[] { Mod(-2), Mod(3) });

        var result = CardEffects.Resolve(Effect(CardEffect.TradeHands), me, them, Target);

        Assert.True(result.ReopensTarget);
        Assert.Contains(myRescue, me.Modifiers);
        Assert.Equal(new[] { -5, -2, 3 }, Values(me.Modifiers));
        Assert.Equal(new[] { 1 }, Values(them.Modifiers));
    }

    // ---------------- Recall ----------------

    [Fact]
    public void Recall_is_dead_until_a_plain_Modifier_has_been_spent()
    {
        Player me = Player("You", hand: Mod(2)), them = Player("Them");
        Card recall = Effect(CardEffect.Recall);
        Assert.False(CardEffects.CanPlay(recall, me, them, Target));

        me.PlayModifierCard(me.Modifiers[0], new GameState());
        Assert.True(CardEffects.CanPlay(recall, me, them, Target));
    }

    [Fact]
    public void Recall_returns_the_chosen_card_but_not_its_points()
    {
        Card two = Mod(2);
        Player me = Player("You", 10, hand: two), them = Player("Them");
        me.PlayModifierCard(two, new GameState());
        Assert.Equal(12, me.CurrentScore);

        var result = CardEffects.Resolve(Effect(CardEffect.Recall), me, them, Target, two);

        Assert.True(result.Applied);
        Assert.Contains(two, me.Modifiers);
        Assert.DoesNotContain(two, me.SpentCards);
        Assert.Equal(12, me.CurrentScore);
    }

    [Fact]
    public void Recall_refuses_to_guess_when_no_card_was_chosen()
    {
        Card two = Mod(2);
        Player me = Player("You", hand: two), them = Player("Them");
        me.PlayModifierCard(two, new GameState());

        Assert.False(CardEffects.Resolve(Effect(CardEffect.Recall), me, them, Target, null).Applied);
        Assert.False(CardEffects.Resolve(Effect(CardEffect.Recall), me, them, Target, Mod(4)).Applied); // never spent
        Assert.Contains(two, me.SpentCards);
    }

    // ---------------- Veto ----------------

    [Fact]
    public void Veto_needs_a_Modifier_played_this_turn_and_works_against_a_holder()
    {
        Card three = Mod(3);
        Player me = Player("You"), them = Player("Them", 15, hand: three);
        Card veto = Effect(CardEffect.Veto);
        Assert.False(CardEffects.CanPlay(veto, me, them, Target));

        them.PlayModifierCard(three, new GameState());
        them.IsHolding = true;
        Assert.True(CardEffects.CanPlay(veto, me, them, Target)); // deliberately legal against a holder
    }

    [Fact]
    public void Veto_destroys_the_card_reverts_the_score_and_releases_the_hold()
    {
        Card three = Mod(3);
        Player me = Player("You"), them = Player("Them", 15, hand: three);
        them.PlayModifierCard(three, new GameState());
        them.IsHolding = true;

        var result = CardEffects.Resolve(Effect(CardEffect.Veto), me, them, Target);

        Assert.True(result.ReopensTarget);
        Assert.True(result.ReleasesHold);
        Assert.Equal(15, them.CurrentScore);
        Assert.DoesNotContain(three, them.ActiveCardsOnBoard);
        Assert.Null(them.LastPlayedModifier);
        // Destroyed, not spent: a later Recall must not be able to bring it back.
        Assert.DoesNotContain(three, them.SpentCards);
        Assert.DoesNotContain(three, them.Modifiers);
    }

    [Fact]
    public void A_vetoed_card_cannot_be_recalled()
    {
        Card three = Mod(3);
        Player me = Player("You"), them = Player("Them", 15, hand: three);
        them.PlayModifierCard(three, new GameState());
        CardEffects.Resolve(Effect(CardEffect.Veto), me, them, Target);

        // Their only spent card was burned, so their Recall has nothing to bring back.
        Assert.False(CardEffects.CanPlay(Effect(CardEffect.Recall), them, me, Target));
        Assert.False(CardEffects.Resolve(Effect(CardEffect.Recall), them, me, Target, three).Applied);
    }

    // ---------------- Shared rules ----------------

    [Fact]
    public void No_effect_card_can_be_the_target_of_another()
    {
        Assert.True(CardEffects.IsPlainModifier(Mod(2)));
        Assert.False(CardEffects.IsPlainModifier(MainCard(2)));
        foreach (CardEffect effect in CardEffects.WiredEffects())
            Assert.False(CardEffects.IsPlainModifier(Effect(effect)));
    }

    [Fact]
    public void RefusalReason_is_null_exactly_when_the_card_is_playable()
    {
        Player me = Player("You"), them = Player("Them", 18, holding: true);
        Assert.Null(CardEffects.RefusalReason(Effect(CardEffect.Shave), me, them, Target));
        Assert.NotNull(CardEffects.RefusalReason(Effect(CardEffect.TradeTotals), me, them, Target));
        Assert.Null(CardEffects.RefusalReason(Mod(3), me, them, Target));
    }

    [Fact]
    public void Retired_cards_are_never_dealt()
    {
        Assert.DoesNotContain(CardEffect.Push, CardEffects.WiredEffects());
        Assert.DoesNotContain(CardEffect.TradeDraw, CardEffects.WiredEffects());
        Assert.Equal(6, CardEffects.WiredEffects().Count);
    }

    [Fact]
    public void Save_slots_never_move()
    {
        // These ints are written into save files. Reordering the enum would turn a saved Shave
        // into something else - this test is the tripwire.
        Assert.Equal(0, (int)CardEffect.None);
        Assert.Equal(1, (int)CardEffect.Push);
        Assert.Equal(2, (int)CardEffect.TradeDraw);
        Assert.Equal(3, (int)CardEffect.TradeTotals);
        Assert.Equal(4, (int)CardEffect.Shave);
        Assert.Equal(5, (int)CardEffect.TradeHands);
        Assert.Equal(6, (int)CardEffect.Copy);
        Assert.Equal(7, (int)CardEffect.Recall);
        Assert.Equal(8, (int)CardEffect.Veto);
    }

    [Fact]
    public void Shave_is_built_worth_one_and_every_other_effect_worth_nothing()
    {
        foreach (CardEffect effect in CardEffects.WiredEffects())
            Assert.Equal(effect == CardEffect.Shave ? 1 : 0, Effect(effect).Value);
    }
}
