using Godot;
using System;
using System.Collections.Generic;

public partial class Player : RefCounted
{
    public string PlayerName { get; set; }
    public int CurrentScore { get; set; } = 0;
    public bool IsHolding { get; set; } = false;
    // Set when the player pressed Draw Card for the current deal (cleared by the next turn).
    public bool HasEndedTurn { get; set; } = false;

    // A player acts (plays modifiers / ends the turn / holds) until they hold or end the turn.
    public bool CanAct => !IsHolding && !HasEndedTurn;

    //Cards currently available in the player's modifier hand
    public List<Card> Modifiers { get; set; } = new List<Card>();

    //Cards played onto the board this set
    public List<Card> ActiveCardsOnBoard { get; set; } = new List<Card>();

    /// The main-deck card this player drew in the CURRENT deal, or null if they did not draw one
    /// (they are holding). Copy needs to name it exactly, and "the last Main card on the
    /// board" is not the same thing once modifiers have been played on top.
    public Card LastDrawnCard { get; set; }

    /// The modifier this player most recently played in the CURRENT deal, or null. Veto names
    /// exactly this card, and "the last card on the board" is not the same thing - the board keeps
    /// every card played all set, and Veto only reaches into the turn being played.
    public Card LastPlayedModifier { get; set; }

    /// Every plain modifier this player has spent THIS MATCH. Recall draws from here.
    ///
    /// Deliberately NOT cleared by ResetForNewSet: ActiveCardsOnBoard is per-set and is wiped
    /// between sets, this is per-match and is wiped when the match hand is dealt. Getting that
    /// backwards makes Recall a per-set card, which still works and is quietly wrong - the whole
    /// premise is that a four-card hand has to last every set of the match.
    public List<Card> SpentCards { get; set; } = new List<Card>();

    public Player(string name)
    {
        PlayerName = name;
    }

    /// Deals a fresh modifier hand for a new match. Every card is a random non-zero value in
    /// -maxMagnitude..+maxMagnitude, and each one has a flipValueChance of being a "+/-" card that the
    /// player can swap between plus and minus before playing it.
    public void DealRandomModifiers(Random rng, int modifierCount = 4, double flipValueChance = 0.10, int maxMagnitude = 4)
    {
        Modifiers.Clear();
        for (int i = 0; i < modifierCount; i++)
        {
            Modifiers.Add(CreateRandomModifier(rng, flipValueChance, maxMagnitude));
        }

        // One guarantee on top of the randomness: a hand always holds at least one way up and one
        // way down. A purely random hand comes out all-plus or all-minus about one match in eight,
        // and neither is playable - all minus can never climb to the target, all plus can never
        // recover from going over. A "+/-" card counts as both, since it can be played either way.
        EnsureBothSigns(rng);
    }

    /// Public because the stage recipes build the AI's Modifier by card and need the same
    /// guarantee afterwards. Effect cards are left alone: an effect card is not a way up or down,
    /// and turning one into a plain -3 would silently delete the stage's whole new rule.
    public void EnsureBothSigns(Random rng)
    {
        List<Card> plain = Modifiers.FindAll(c => c.Effect == CardEffect.None);
        if (plain.Count < 2) return;

        bool hasPlus = plain.Exists(c => c.CanFlipValue || c.Value > 0);
        bool hasMinus = plain.Exists(c => c.CanFlipValue || c.Value < 0);
        if (hasPlus && hasMinus) return;

        // Turn one card round rather than redealing, so every other card stays as it was dealt.
        Card card = plain[rng.Next(plain.Count)];
        int magnitude = Math.Abs(card.Value);
        int value = hasPlus ? -magnitude : magnitude;
        Modifiers[Modifiers.IndexOf(card)] = new Card(value, CardType.Modifier, "", card.CanFlipValue);
    }

    public static Card CreateRandomModifier(Random rng, double flipValueChance = 0.10, int maxMagnitude = 4)
    {
        int magnitude = rng.Next(1, maxMagnitude + 1);          // 1..maxMagnitude - never 0
        int value = rng.Next(2) == 0 ? -magnitude : magnitude;  // an even chance of either sign
        bool canFlipValue = rng.NextDouble() < flipValueChance;
        return new Card(value, CardType.Modifier, "", canFlipValue);
    }

    public bool PlayModifierCard(Card card, GameState gameState)
    {
        if (!Modifiers.Contains(card)) return false;

        Modifiers.Remove(card);
        ActiveCardsOnBoard.Add(card);
        LastPlayedModifier = card;
        if (CardEffects.IsPlainModifier(card)) SpentCards.Add(card);

        CurrentScore += card.Value;
        return true;
    }
    public void ResetForNewSet()
    {
        CurrentScore = 0;
        IsHolding = false;
        HasEndedTurn = false;
        LastDrawnCard = null;
        LastPlayedModifier = null;
        ActiveCardsOnBoard.Clear();
        // SpentCards is NOT cleared here - see the field. It belongs to the match, not the set.
    }

    /// Called when a fresh match hand is dealt. The only place SpentCards is emptied.
    public void ResetForNewMatch()
    {
        SpentCards.Clear();
    }
}
