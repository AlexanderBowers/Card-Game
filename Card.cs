using Godot;

public enum CardType
{
	Main,
	Modifier
}

public partial class Card : RefCounted
{
	public int Value { get; set; }
	public CardType Type { get; set; }
	public string CardName { get; set; }

	public Card(int value, CardType type, string cardName = "")
	{
		Value = value;
		Type = type;
		CardName = string.IsNullOrEmpty(cardName) ? value.ToString() : cardName;
	}
}
