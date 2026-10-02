using System;

/// Save migrations that are pure rules rather than file format, so the tests can hold them.
public partial class RunData
{
    /// One saved Modifier row as it should load today, or false for a row to drop.
    ///
    /// A version 2 save has no "effect" key at all, which reads as None - so an older collection
    /// loads unchanged rather than being thrown away. The effect is range-checked with
    /// Enum.IsDefined rather than a hand-written upper bound: the bound used to read
    /// "<= (int)CardEffect.Copy", which was correct only for as long as Copy happened to be the
    /// last member - appending Recall and Veto would have made every saved copy of them load as
    /// None and silently vanish from the player's collection. This is right for every future card.
    public static bool MigrateModifier(int value, bool flip, int rawEffect, out ModifierDef def)
    {
        CardEffect effect = (rawEffect > 0 && Enum.IsDefined(typeof(CardEffect), rawEffect))
            ? (CardEffect)rawEffect
            : CardEffect.None;

        // Push was scrapped (2026-09-10) and Copy took its stage 4 slot. A Push already in
        // someone's collection becomes a Copy rather than a card that no longer exists: it keeps
        // its place in the deck, and what the player owns is still "the stage 4 effect card".
        // Copy carries no number, so the old rolled value goes with it.
        //
        // Cards are never TAKEN away - that rule is what makes losing a run survivable, and it
        // applies just as much when the design changes underneath a card. Trade Draw was removed
        // the next day (2026-09-11) for overlapping Copy. It was never wired and so never
        // buyable, which means no honest save can hold one - but a hand-edited or half-migrated
        // file could, and a card nothing can play is worse than a card that plays as its
        // replacement.
        if (effect == CardEffect.Push || effect == CardEffect.TradeDraw)
        {
            effect = CardEffect.Copy;
            value = 0;
        }

        // Effect cards are worth 0 - none of them carries a number - so the "value != 0" guard
        // against junk rows only applies to ordinary modifiers.
        def = new ModifierDef(value, flip, effect);
        return value != 0 || effect != CardEffect.None;
    }
}
