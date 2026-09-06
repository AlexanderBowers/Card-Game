using Godot;

public enum CardType
{
	Main,
	Modifier
}

public partial class Card : RefCounted
{
	/// The value this card is worth right now. For a flip card this changes sign when it is flipped.
	public int Value { get; set; }
	public CardType Type { get; set; }
	public string CardName { get; set; }

	/// A "+/-" card: the same magnitude can be played as either a plus or a minus. The player
	/// chooses the orientation before committing it (see Flip); Value always holds the orientation
	/// the card is currently showing.
	public bool IsFlip { get; set; }

	public Card(int value, CardType type, string cardName = "", bool isFlip = false)
	{
		Value = value;
		Type = type;
		IsFlip = isFlip;
		CardName = string.IsNullOrEmpty(cardName) ? DefaultName(value, type) : cardName;
	}

	/// Swaps a flip card between +n and -n. Returns false (and changes nothing) on any other card.
	public bool Flip()
	{
		if (!IsFlip) return false;
		Value = -Value;
		CardName = DefaultName(Value, Type);
		return true;
	}

	/// What the card shows: main cards are a bare number, modifiers always carry their sign.
	public string DisplayText => DefaultName(Value, Type);

	private static string DefaultName(int value, CardType type)
	{
		if (type == CardType.Main) return value.ToString();
		return value > 0 ? "+" + value : value.ToString();
	}
}
