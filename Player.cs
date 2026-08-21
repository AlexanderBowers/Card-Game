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
    public List<Card> ActiveModifiersOnBoard { get; set; } = new List<Card>();

    public Player(string name)
    {
        PlayerName = name;
    }
    
    public void ResetForNewRound()
    {
        CurrentScore = 0;
        IsHolding = false;
        IsActiveTurn = false;
        ActiveModifiersOnBoard.Clear();
    }
}
