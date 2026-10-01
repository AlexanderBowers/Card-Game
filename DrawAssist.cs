using System;
using System.Collections.Generic;

/// The Bronze rung's helping hand (playtest, 2026-09-30).
///
/// "Stage 1 is still too difficult. They should never be able to get to 20 naturally and only hold
/// if they get 19. For stages 1/2 the player should have their hand held: every set should look at
/// the remaining Modifiers and make sure hitting 20 is guaranteed."
///
/// Both halves work by choosing WHICH card is on top of a deck, never by changing what is in it:
/// the chosen value is moved to the top of the shuffled forty, so a deck still holds exactly four
/// of every value and counting your own board stays honest. Nothing here touches the run outside
/// Bronze, endless, local 2-player, or the tutorial's staged first set.
public static class DrawAssist
{
    /// Stages 1 and 2: Player 1's draws are steered so 20 is always within reach.
    public static bool GuidesPlayer(RunData run, bool vsBot) =>
        vsBot && run != null && !run.Endless && run.StepIndex <= 1;

    /// Stage 1 only: the bot never reaches the target - not by drawing, not by a Modifier - and
    /// holds only on target - 1.
    public static bool BotAvoidsTarget(RunData run, bool vsBot) =>
        vsBot && run != null && !run.Endless && run.StepIndex == 0;

    /// Moves one copy of `value` to the top (the END) of the deck. Never adds or removes a card.
    public static void StackTop(List<int> deck, int value)
    {
        int index = deck.LastIndexOf(value);
        if (index < 0 || index == deck.Count - 1) return;
        deck.RemoveAt(index);
        deck.Add(value);
    }

    /// The card Player 1 should draw next, or null to leave the deck alone.
    ///
    /// Order of preference:
    ///   1. land where a plus Modifier in hand finishes on the target (it teaches the Modifier),
    ///   2. land on the target itself,
    ///   3. step towards one of those so the NEXT draw can land it,
    ///   4. go over by exactly what a minus Modifier in hand takes back.
    /// On the opening deal, 1 and 2 are skipped: a set that is won before the player has pressed
    /// anything teaches nothing, so the opening only sets the finish up.
    public static int? PlayerPick(List<int> deck, Player player, int target, bool opening,
                                  Func<Card, bool> unavailable, Random rng)
    {
        int score = player.CurrentScore;
        if (score >= target) return null;

        var ups = new List<int>();
        var downs = new List<int>();
        foreach (Card card in player.Modifiers)
        {
            if (card.Effect != CardEffect.None || unavailable(card)) continue;
            int magnitude = Math.Abs(card.Value);
            if (magnitude == 0) continue;
            if (card.CanFlipValue || card.Value > 0) ups.Add(magnitude);
            if (card.CanFlipValue || card.Value < 0) downs.Add(magnitude);
        }

        bool Has(int value) => value >= 1 && value <= 10 && deck.Contains(value);

        if (!opening)
        {
            var finishes = new List<int>();
            foreach (int up in ups)
                if (Has(target - up - score)) finishes.Add(target - up - score);
            if (finishes.Count > 0) return finishes[rng.Next(finishes.Count)];

            if (Has(target - score)) return target - score;
        }

        // Step towards a finish: a Modifier finish when there is one ahead, else the target.
        var goals = new List<int>();
        foreach (int up in ups)
            if (target - up > score) goals.Add(target - up);
        if (goals.Count == 0) goals.Add(target);
        int goal = goals[rng.Next(goals.Count)];

        var near = new List<int>();   // leaves the goal one draw away
        int far = 0;                  // otherwise the biggest step that stays short of it
        foreach (int value in deck)
        {
            int after = score + value;
            if (after >= goal) continue;
            if (goal - after <= 10) near.Add(value);
            else far = Math.Max(far, value);
        }
        if (near.Count > 0) return near[rng.Next(near.Count)];
        if (far > 0) return far;

        if (!opening)
            foreach (int down in downs)
                if (Has(target + down - score)) return target + down - score;

        return null;
    }

    /// The card the stage 1 bot draws next: the top card, unless that would put it exactly on the
    /// target, in which case any other value from the deck.
    public static int? BotPick(List<int> deck, Player bot, int target, Random rng)
    {
        if (deck.Count == 0) return null;
        int top = deck[deck.Count - 1];
        if (bot.CurrentScore + top != target) return null;

        var others = deck.FindAll(value => bot.CurrentScore + value != target);
        if (others.Count == 0) return null;
        return others[rng.Next(others.Count)];
    }
}
