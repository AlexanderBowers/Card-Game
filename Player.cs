using Godot;
using System;
using System.Collections.Generic;

public partial class Player : RefCounted
{
    public string PlayerName { get; set; }
    public int CurrentScore { get; set; } = 0;
    public bool IsHolding { get; set; } = false;
    // Set when the player pressed End Turn for the current deal (cleared by the next deal).
    public bool HasEndedTurn { get; set; } = false;

    // A player acts (plays modifiers / ends the turn / holds) until they hold or end the deal.
    public bool CanAct => !IsHolding && !HasEndedTurn;

    //Cards currently available in the player's modifier hand
    public List<Card> ModifierHand { get; set; } = new List<Card>();

    //Cards played onto the board this round
    public List<Card> ActiveCardsOnBoard { get; set; } = new List<Card>();

    /// The main-deck card this player drew in the CURRENT deal, or null if they did not draw one
    /// (they are holding). TradeDraw needs to name it exactly, and "the last Main card on the
    /// board" is not the same thing once modifiers have been played on top.
    public Card LastDrawnCard { get; set; }

    public Player(string name)
    {
        PlayerName = name;
    }

    /// Deals a fresh modifier hand for a new match. Every card is a random non-zero value in
    /// -maxMagnitude..+maxMagnitude, and each one has a flipChance of being a "+/-" card that the
    /// player can swap between plus and minus before playing it.
    public void DealRandomModifierHand(Random rng, int handSize = 4, double flipChance = 0.10, int maxMagnitude = 4)
    {
        ModifierHand.Clear();
        for (int i = 0; i < handSize; i++)
        {
            ModifierHand.Add(CreateRandomModifier(rng, flipChance, maxMagnitude));
        }

        // One guarantee on top of the randomness: a hand always holds at least one way up and one
        // way down. A purely random hand comes out all-plus or all-minus about one match in eight,
        // and neither is playable - all minus can never climb to the target, all plus can never
        // recover from going over. A "+/-" card counts as both, since it can be played either way.
        EnsureBothSigns(rng);
    }

    /// Public because the stage recipes build the AI's hand card by card and need the same
    /// guarantee afterwards. Effect cards are left alone: an effect card is not a way up or down,
    /// and turning one into a plain -3 would silently delete the stage's whole new rule.
    public void EnsureBothSigns(Random rng)
    {
        List<Card> plain = ModifierHand.FindAll(c => c.Effect == CardEffect.None);
        if (plain.Count < 2) return;

        bool hasPlus = plain.Exists(c => c.IsFlip || c.Value > 0);
        bool hasMinus = plain.Exists(c => c.IsFlip || c.Value < 0);
        if (hasPlus && hasMinus) return;

        // Turn one card round rather than redealing, so every other card stays as it was dealt.
        Card card = plain[rng.Next(plain.Count)];
        int magnitude = Math.Abs(card.Value);
        int value = hasPlus ? -magnitude : magnitude;
        ModifierHand[ModifierHand.IndexOf(card)] = new Card(value, CardType.Modifier, "", card.IsFlip);
    }

    public static Card CreateRandomModifier(Random rng, double flipChance = 0.10, int maxMagnitude = 4)
    {
        int magnitude = rng.Next(1, maxMagnitude + 1);          // 1..maxMagnitude - never 0
        int value = rng.Next(2) == 0 ? -magnitude : magnitude;  // an even chance of either sign
        bool isFlip = rng.NextDouble() < flipChance;
        return new Card(value, CardType.Modifier, "", isFlip);
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
        HasEndedTurn = false;
        LastDrawnCard = null;
        ActiveCardsOnBoard.Clear();
    }
}
