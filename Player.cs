using Godot;
using System.Collections.Generic;

public partial class Player : RefCounted
{
    public string PlayerName { get; set; }
    public int CurrentScore { get; set; } = 0;
    public bool IsHolding { get; set; } = false;
    public bool IsActiveTurn {get; set; } = false;

    //Cards currently available in the player's modifier hand
    public List<Card> ModifierHand { get; set; } = new List<Card>();

    //Cards played onto the board this round
    public List<Card> ActiveCardsOnBoard { get; set; } = new List<Card>();

    public Player(string name)
    {
        PlayerName = name;
        InitializeDefaultModifierHand();
    }
    
    private void InitializeDefaultModifierHand()
    {
        ModifierHand.Add(new Card(1, CardType.Modifier, "+1"));
        ModifierHand.Add(new Card(2, CardType.Modifier, "+2"));
        ModifierHand.Add(new Card(-1, CardType.Modifier, "-1"));
        ModifierHand.Add(new Card(-2, CardType.Modifier, "-2"));

    }

    public bool PlayModifierCard(Card card, GameState gameState)
    {
        if (!ModifierHand.Contains(card)) return false;

        ModifierHand.Remove(card);
        ActiveCardsOnBoard.Add(card);

        CurrentScore += card.Value;
        return true;
    }
    public void ResetForNewRound()
    {
        CurrentScore = 0;
        IsHolding = false;
        IsActiveTurn = false;
        ActiveCardsOnBoard.Clear();
    }
}
