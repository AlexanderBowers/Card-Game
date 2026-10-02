using System;

/// A Modifier card as it is stored between matches. Card itself is a runtime object tied to a
/// match; this is the durable description the save file round-trips.
public readonly struct ModifierDef
{
    public readonly int Value;
    public readonly bool CanFlipValue;
    public readonly CardEffect Effect;

    public ModifierDef(int value, bool canFlipValue = false, CardEffect effect = CardEffect.None)
    {
        Value = value;
        CanFlipValue = canFlipValue;
        Effect = effect;
    }

    public Card ToCard() => new Card(Value, CardType.Modifier, CardName, CanFlipValue, Effect);

    private string CardName => Effect == CardEffect.None ? "" : CardEffects.Label(Effect);

    public string Label => Effect != CardEffect.None
        ? CardEffects.Label(Effect)
        : (CanFlipValue ? "±" : (Value > 0 ? "+" : "-")) + Math.Abs(Value);

    /// This card's collection log key (see CollectionLog.Key).
    public string LogKey => CollectionLog.Key(Value, CanFlipValue, Effect);
}
