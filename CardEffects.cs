using System;
using System.Collections.Generic;

/// <summary>
/// What a card does beyond adding its own value to its owner's score. CardEffect.None is an
/// ordinary modifier - every card that existed before the stage ladder is one of those.
/// </summary>
public enum CardEffect
{
    None,          // Value is added to the owner's score (the only kind of card before stage 4)
    Push,          // stage 4: Value - either sign - is added to the OPPONENT's score
    TradeDraw,     // stage 5: swap the two cards drawn this deal
    TradeTotals,   // stage 6: swap the two current scores
    Shave,         // stage 7: -1 to an opponent who is holding below the target
    TradeHands,    // stage 8: swap the two remaining hands
}

/// <summary>
/// Every rule for the effect cards, in one place.
///
/// Nothing in here knows which player is the human. The ladder gives these cards to the AI first,
/// but the market sells the player the same card one visit later (Alexander, 2026-09-07), so both
/// sides go through exactly these methods - which is the only reason player-owned effect cards
/// are cheap rather than a second implementation.
///
/// See claude/stage-ladder-spec.md for the design these rules come from.
/// </summary>
public static class CardEffects
{
    // ------------------------------------------------------------------
    // Which effects are finished
    //
    // An effect is only dealt once it both resolves AND reads correctly on the table. Pass 1 wires
    // up none of them: the model, the legality gate and the answering rule land first, so the
    // stage recipes below can name their card without the ladder dealing something inert.
    // Pass 2 adds Push and Shave here; pass 3 the three Trades.
    // ------------------------------------------------------------------
    private static readonly HashSet<CardEffect> Wired = new HashSet<CardEffect>
    {
        CardEffect.Push,   // pass 2
        CardEffect.Shave,  // pass 2
    };

    public static bool Implemented(CardEffect effect) =>
        effect == CardEffect.None || Wired.Contains(effect);

    /// What resolving a card did, for the caller to narrate and act on.
    public readonly struct EffectResult
    {
        public readonly bool Applied;
        /// The answering rule: a card that changed the target's score or hand re-opens their turn
        /// for this deal. GameManager still refuses to re-open a player who is HOLDING - which is
        /// exactly the state Shave exists to punish.
        public readonly bool ReopensTarget;
        public readonly string Narration;

        public EffectResult(bool applied, bool reopensTarget, string narration)
        {
            Applied = applied;
            ReopensTarget = reopensTarget;
            Narration = narration;
        }

        public static EffectResult Nothing => new EffectResult(false, false, string.Empty);
    }

    // ------------------------------------------------------------------
    // Legality
    // ------------------------------------------------------------------
    /// Can `self` play this card at `opponent` right now? Pure - the caller owns the separate
    /// "one opponent-facing card per deal" limit, which is about the deal, not about the card.
    public static bool CanPlay(Card card, Player self, Player opponent, int target)
    {
        if (card == null || self == null || opponent == null) return false;

        switch (card.Effect)
        {
            case CardEffect.None:
                return true;

            // Not at a locked score - unless it busts it. This is what keeps the two cards
            // distinct: Push THREATENS A BUST, Shave nibbles a locked score. If a Push could
            // freely drag a held score down, a -3 Push would be a strictly better Shave and
            // stage 7 would have nothing left to teach.
            case CardEffect.Push:
                return !opponent.IsHolding || opponent.CurrentScore + card.Value > target;

            // Only when both players actually drew this deal. A holding player does not draw
            // (DrawCardFor returns early), so the GDD's "if they aren't holding" falls out for free.
            case CardEffect.TradeDraw:
                return !self.IsHolding && !opponent.IsHolding
                    && self.LastDrawnCard != null && opponent.LastDrawnCard != null;

            // A held score is locked in. TradeTotals cannot take it.
            case CardEffect.TradeTotals:
                return !opponent.IsHolding;

            // The one card aimed at a locked score, and only a locked score BELOW the target -
            // the GDD is explicit that a player sitting exactly on the target is safe.
            case CardEffect.Shave:
                return opponent.IsHolding && opponent.CurrentScore < target;

            // The card being played is spent first, so an owner left empty-handed is the BEST case
            // (take theirs, give nothing). What has to be true is that there is something to take.
            case CardEffect.TradeHands:
                return opponent.ModifierHand.Count > 0;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Resolution
    //
    // Assumes CanPlay already said yes and the card has already been removed from its owner's
    // hand. Mutates the two players and reports what happened; the caller animates and narrates.
    // ------------------------------------------------------------------
    public static EffectResult Resolve(Card card, Player self, Player opponent, int target)
    {
        if (card == null || self == null || opponent == null) return EffectResult.Nothing;

        switch (card.Effect)
        {
            case CardEffect.Push:
            {
                int before = opponent.CurrentScore;
                opponent.CurrentScore += card.Value;
                return new EffectResult(true, true,
                    $"{self.PlayerName} plays Push {Signed(card.Value)} - {opponent.PlayerName}: {before} to {opponent.CurrentScore}");
            }

            case CardEffect.TradeDraw:
            {
                Card mine = self.LastDrawnCard;
                Card theirs = opponent.LastDrawnCard;
                if (mine == null || theirs == null) return EffectResult.Nothing;

                int a = mine.Value;
                int b = theirs.Value;

                // Each side keeps the card object it already has on the board and takes the other
                // one's number; the scores move by the difference. (Refreshing the two card VIEWS
                // is pass 3 - the model is what the round is scored on.)
                // Value only: the card VIEWS are built at draw time and are refreshed in pass 3.
                // Writing CardName here would look like it updated the board, and it does not.
                mine.Value = b;
                theirs.Value = a;

                self.CurrentScore += b - a;
                opponent.CurrentScore += a - b;

                return new EffectResult(true, true,
                    $"{self.PlayerName} plays Trade Draw - the {a} and the {b} change places");
            }

            case CardEffect.TradeTotals:
            {
                int mine = self.CurrentScore;
                int theirs = opponent.CurrentScore;
                self.CurrentScore = theirs;
                opponent.CurrentScore = mine;

                return new EffectResult(true, true,
                    $"{self.PlayerName} plays Trade Totals - {mine} and {theirs} change places");
            }

            case CardEffect.Shave:
            {
                int before = opponent.CurrentScore;
                opponent.CurrentScore -= 1;

                // Deliberately does NOT re-open the target: they are holding, and this is the one
                // card in the game they cannot answer.
                return new EffectResult(true, false,
                    $"{self.PlayerName} plays Shave - {opponent.PlayerName} is locked at {before}, now {opponent.CurrentScore}");
            }

            case CardEffect.TradeHands:
            {
                List<Card> mine = new List<Card>(self.ModifierHand);
                self.ModifierHand.Clear();
                self.ModifierHand.AddRange(opponent.ModifierHand);
                opponent.ModifierHand.Clear();
                opponent.ModifierHand.AddRange(mine);

                return new EffectResult(true, true,
                    $"{self.PlayerName} plays Trade Hands - takes {self.ModifierHand.Count}, gives {opponent.ModifierHand.Count}");
            }
        }

        return EffectResult.Nothing;
    }

    // ------------------------------------------------------------------
    // Presentation helpers
    // ------------------------------------------------------------------
    /// True when the card belongs in the TARGET's board grid rather than its owner's: the two
    /// effects that change the other player's score should sit where that score is.
    public static bool LandsOnTarget(CardEffect effect) =>
        effect == CardEffect.Push || effect == CardEffect.Shave;

    public static string Label(CardEffect effect)
    {
        switch (effect)
        {
            case CardEffect.Push: return "Push";
            case CardEffect.TradeDraw: return "Trade Draw";
            case CardEffect.TradeTotals: return "Trade Totals";
            case CardEffect.Shave: return "Shave";
            case CardEffect.TradeHands: return "Trade Hands";
            default: return "Modifier";
        }
    }

    /// One mark per effect - the card face carries this, not a sentence (the 5-to-85 tenet).
    ///
    /// Kept to characters the built-in font actually has: ui_theme.tres sets a font SIZE but no
    /// font, so Godot falls back to Open Sans and anything exotic renders as an empty box. The
    /// swap arrows "-><-" stand in for one mark until pass 3 draws the real faces; all three
    /// Trades share them, which is exactly why pass 3 has to draw them properly.
    public static string Glyph(CardEffect effect)
    {
        switch (effect)
        {
            case CardEffect.Push: return "->";        // it goes at THEM
            case CardEffect.TradeDraw: return "-><-"; // things change places
            case CardEffect.TradeTotals: return "-><-";
            case CardEffect.TradeHands: return "-><-";
            case CardEffect.Shave: return "-1";
            default: return string.Empty;
        }
    }

    // ------------------------------------------------------------------
    // Building one
    // ------------------------------------------------------------------
    /// A fresh effect card for a stage recipe or a shop offer. Only Push carries a number: 2..5,
    /// either sign, so it is big enough to bust a careless score without being an instant loss.
    public static Card Create(CardEffect effect, Random rng)
    {
        int value = 0;
        if (effect == CardEffect.Push)
        {
            int magnitude = rng.Next(2, 6);
            value = rng.Next(2) == 0 ? magnitude : -magnitude;
        }
        else if (effect == CardEffect.Shave)
        {
            value = 1; // fixed by the rule, not by the roll
        }

        return new Card(value, CardType.Modifier, Label(effect), false, effect);
    }

    private static string Signed(int value) => value > 0 ? "+" + value : value.ToString();
}
