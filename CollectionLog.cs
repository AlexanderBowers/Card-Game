using System;
using System.Collections.Generic;

/// The collection log's fixed shape (playtest-feedback-family.md §5.2): which entries exist, in
/// what order, and how a card maps to its entry. Which entries THIS player has is profile state:
/// the cards they own (RunData.Inventory, via RunData.OwnsCard - 2026-10-05; it used to be CardsMet).
///
/// Read straight from CardsMet - the same set the coach-marks use, so an effect card is "met"
/// on exactly the occasion the game explained it. Plain and flip-value Modifiers need no
/// explanation, so they get their own keys ("+3", "-3", "flip3") and are marked when they
/// enter the collection, are dealt to you, or are played at you.
public static class CollectionLog
{
    public const int MaxMagnitude = 6;   // the market's own ceiling (ShopOverlay.RollOffers)

    private static readonly CardEffect[] Effects =
    {
        CardEffect.Copy, CardEffect.TradeTotals, CardEffect.Shave,
        CardEffect.TradeHands, CardEffect.Recall, CardEffect.Veto,
    };

    /// Every entry, in display order: four rows of six - plus, minus, flip value, special.
    public static readonly string[] Keys = BuildKeys();

    private static string[] BuildKeys()
    {
        List<string> keys = new List<string>();
        for (int m = 1; m <= MaxMagnitude; m++) keys.Add(Key(m, false, CardEffect.None));
        for (int m = 1; m <= MaxMagnitude; m++) keys.Add(Key(-m, false, CardEffect.None));
        for (int m = 1; m <= MaxMagnitude; m++) keys.Add(Key(m, true, CardEffect.None));
        foreach (CardEffect effect in Effects) keys.Add(Key(0, false, effect));
        return keys.ToArray();
    }

    /// The effect's NAME for an effect card (the same key CardEffects.MetKey gives it), otherwise
    /// the signed magnitude. Null for a card that is not a Modifier at all.
    public static string Key(int value, bool canFlipValue, CardEffect effect)
    {
        if (effect != CardEffect.None) return effect.ToString();
        int magnitude = Math.Abs(value);
        if (magnitude == 0) return null;
        if (canFlipValue) return "flip" + magnitude;
        return (value > 0 ? "+" : "-") + magnitude;
    }

    public static string Key(Card card) =>
        (card == null || card.Type != CardType.Modifier) ? null : Key(card.Value, card.CanFlipValue, card.Effect);

    /// The card a log key stands for, so the screen can draw it.
    public static ModifierDef Entry(string key)
    {
        if (key.StartsWith("flip")) return new ModifierDef(int.Parse(key.Substring(4)), canFlipValue: true);
        if (key[0] == '+' || key[0] == '-') return new ModifierDef(int.Parse(key));
        return new ModifierDef(0, false, Enum.Parse<CardEffect>(key));
    }

    /// How many log entries this set of met keys covers.
    public static int Found(ICollection<string> met)
    {
        int found = 0;
        foreach (string key in Keys) if (met.Contains(key)) found++;
        return found;
    }
}
