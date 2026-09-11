using System;
using System.Collections.Generic;

/// <summary>
/// What a card does beyond adding its own value to its owner's score. CardEffect.None is an
/// ordinary modifier - every card that existed before the stage ladder is one of those.
/// </summary>
public enum CardEffect
{
    None,          // Value is added to the owner's score (the only kind of card before stage 4)

    /// RETIRED 2026-09-10. Push added its number to the opponent's score, and it could not be
    /// balanced: two-signed it was a gift as often as a threat, plus-only it was either
    /// irrelevant or an execution, and allowed at a locked score it simply won the round. Copy
    /// took its place at stage 4.
    ///
    /// The SLOT stays because these values are written into save files as ints - deleting it
    /// would renumber every effect below it and turn a saved Shave into something else. Nothing
    /// creates one any more, and RunData.Load migrates any that were saved.
    Push,

    /// RETIRED 2026-09-11. Trade Draw swapped the two cards drawn this deal. Copy (stage 4) is
    /// the same idea without the second half, and two cards that both rewrite the drawn cards is
    /// one idea wearing two faces - so this one goes and Copy keeps the mechanic.
    ///
    /// The SLOT stays for the same reason Push's does: these values are ints in the save file.
    TradeDraw,

    // The ORDER of the members below is not the ladder's order and never will be again - these
    // values are ints in the save file, so a card's slot is fixed the day it is written, while
    // its rung lives in RunData.Ladder and moved once already (2026-09-11).
    TradeTotals,   // stage 5: swap the two current scores
    Shave,         // stage 6: -1 to an opponent who is holding below the target
    TradeHands,    // stage 7: swap the two remaining hands
    Copy,          // stage 4: YOUR drawn card becomes a copy of theirs - they are untouched
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
    // Pass 2 wired Push and Shave; pass 4 scrapped Push and put Copy in its place at stage 4;
    // pass 5 wired the two remaining Trades. Stages 4-7 now each deal their own card, and stage 8
    // is the only rung still without one.
    // ------------------------------------------------------------------
    private static readonly HashSet<CardEffect> Wired = new HashSet<CardEffect>
    {
        CardEffect.Copy,        // pass 4 - stage 4, replaced Push
        CardEffect.TradeTotals, // pass 5 - stage 5
        CardEffect.Shave,       // pass 2 - stage 6
        CardEffect.TradeHands,  // pass 5 - stage 7
    };

    public static bool Implemented(CardEffect effect) =>
        effect == CardEffect.None || Wired.Contains(effect);

    /// Implemented() treats an ordinary card as fine, because it is. IsWired asks the narrower
    /// question the ladder and the market need: is this a finished EFFECT card, something a stage
    /// can actually be about? None is not.
    public static bool IsWired(CardEffect effect) => Wired.Contains(effect);

    /// Every finished effect, in enum order - what a stage or a market may fall back on when the
    /// card it wanted is not built yet. The caller filters by rung (GameManager's stage recipe
    /// drops anything introduced above the current match), because enum order is NOT stage order.
    public static List<CardEffect> WiredEffects()
    {
        List<CardEffect> wired = new List<CardEffect>(Wired);
        wired.Sort((a, b) => ((int)a).CompareTo((int)b));
        return wired;
    }

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

            // Both players must have drawn this deal - there has to be a card of mine to rewrite
            // and a card of theirs to rewrite it with. A holding player does not draw
            // (DrawCardFor returns early), so "neither of us is holding" falls out for free.
            //
            // And the two cards have to actually DIFFER. Copying a 5 onto a 5 spends the card to
            // change nothing, which is the one outcome no player ever means to buy.
            case CardEffect.Copy:
                return !self.IsHolding && !opponent.IsHolding
                    && self.LastDrawnCard != null && opponent.LastDrawnCard != null
                    && self.LastDrawnCard.Value != opponent.LastDrawnCard.Value;

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

    /// WHY CanPlay said no, in the player's own terms - one sentence, naming the rule rather than
    /// the state. Returns null when the card is playable.
    ///
    /// "Not against them right now" (the first build) tells the player nothing, and this is a game
    /// whose whole difficulty is knowing what your cards do. The same sentence is what greys the
    /// Play button out, so the answer is never more than one card-tap away.
    public static string RefusalReason(Card card, Player self, Player opponent, int target)
    {
        if (card == null || self == null || opponent == null) return "No card selected.";
        if (card.Effect == CardEffect.None) return null;
        if (!Implemented(card.Effect)) return $"{Label(card.Effect)} is not in play yet.";
        if (CanPlay(card, self, opponent, target)) return null;

        string them = opponent.PlayerName;

        switch (card.Effect)
        {
            case CardEffect.Copy:
                if (self.IsHolding) return "You are holding, so you did not draw a card to replace.";
                if (opponent.IsHolding) return $"{them} is holding, so they have no card this deal to copy.";
                if (self.LastDrawnCard == null || opponent.LastDrawnCard == null)
                    return "Copy needs a freshly drawn card on both sides of the table.";
                return $"You both drew a {self.LastDrawnCard.Value} - copying it would change nothing.";

            case CardEffect.TradeTotals:
                return $"{them} is holding - a locked score cannot be traded away.";

            case CardEffect.Shave:
                if (!opponent.IsHolding) return $"{them} is still drawing - Shave only trims a score that is locked in.";
                return $"{them} is holding on {opponent.CurrentScore}, and Shave only trims a score below the target of {target}.";

            case CardEffect.TradeHands:
                return $"{them} has no cards left to take.";
        }

        return $"{Label(card.Effect)} cannot be played right now.";
    }

    /// One line for the market: what the card does, before the player has ever been hit with it.
    public static string Description(CardEffect effect)
    {
        switch (effect)
        {
            case CardEffect.Copy: return "Your drawn card becomes a copy of theirs.\nTheir card and their score do not change.";
            case CardEffect.TradeTotals: return "Swap the two current scores.";
            case CardEffect.Shave: return "Take 1 off an opponent who is holding\nbelow the target. They cannot answer.";
            case CardEffect.TradeHands: return "Swap the two remaining hands.";
            default: return string.Empty;
        }
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
            case CardEffect.Copy:
            {
                Card mine = self.LastDrawnCard;
                Card theirs = opponent.LastDrawnCard;
                if (mine == null || theirs == null) return EffectResult.Nothing;

                int wasCard = mine.Value;
                int nowCard = theirs.Value;
                int wasScore = self.CurrentScore;

                // MY card takes THEIR card's number. Theirs is not touched, and neither is their
                // score - which is why this does not re-open their turn below. The card object on
                // my board is mutated rather than replaced, so the caller can refresh its face.
                mine.Value = nowCard;
                self.CurrentScore += nowCard - wasCard;

                return new EffectResult(true, false,
                    $"{self.PlayerName} plays Copy - their {nowCard} replaces the {wasCard}. {self.PlayerName}: {wasScore} to {self.CurrentScore}");
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
    /// True when the card belongs in the TARGET's board grid rather than its owner's: a card whose
    /// whole effect lands on one other number should sit where that number is.
    ///
    /// Only Shave. Trade Totals moves BOTH scores, so there is no single side it belongs to, and
    /// putting it on the target's board would read as "this happened to you" when half of it
    /// happened to the player who spent the card. It stays with its owner.
    public static bool LandsOnTarget(CardEffect effect) =>
        effect == CardEffect.Shave;

    /// True when this effect rewrites a card that is already face-up on a board. Those cards are
    /// mutated in place rather than replaced, so whoever plays one has to redraw its FACE as well
    /// - otherwise the board still reads 10 while the score has already been paid at 2.
    ///
    /// Only Copy does this now that Trade Draw is gone. It stays a predicate rather than becoming
    /// `effect == Copy` at the call site because it names the REASON, which is the thing a future
    /// card would have to share to need the same treatment.
    public static bool RewritesDrawnCards(CardEffect effect) =>
        effect == CardEffect.Copy;

    public static string Label(CardEffect effect)
    {
        switch (effect)
        {
            case CardEffect.Copy: return "Copy";
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
    /// The two Trades used to share "-><-", which made the stage 5 card and the stage 7 card
    /// indistinguishable on the table. They still share the swap mark "<>", because they ARE both
    /// swaps, but each now says WHAT is being swapped: "#" for a score, "[]" for a hand. Four
    /// characters either way, which is the width the card face was already laid out for.
    ///
    /// These are placeholders for drawn faces, not the final art.
    public static string Glyph(CardEffect effect)
    {
        switch (effect)
        {
            case CardEffect.Copy: return "<-";        // their card comes to ME
            case CardEffect.TradeTotals: return "#<>#";
            case CardEffect.TradeHands: return "[<>]";
            case CardEffect.Shave: return "-1";
            default: return string.Empty;
        }
    }

    // ------------------------------------------------------------------
    // Building one
    // ------------------------------------------------------------------
    /// A fresh effect card for a stage recipe or a shop offer.
    ///
    /// No effect card carries a rolled number any more. Push was the only one that did, and its
    /// number was exactly what could not be balanced: the same card was a gift at +2 and an
    /// execution at +5, and no range made it mean one thing. Copy takes its number from the
    /// TABLE - whatever the opponent happened to draw - so the card is always the same card and
    /// the drama comes from the deal instead of from the roll.
    public static Card Create(CardEffect effect, Random rng)
    {
        int value = 0;
        if (effect == CardEffect.Shave)
        {
            value = 1; // fixed by the rule, not by the roll
        }

        return new Card(value, CardType.Modifier, Label(effect), false, effect);
    }
}
