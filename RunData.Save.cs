using Godot;
using System;
using System.Collections.Generic;

/// The save format: RunData to and from the dictionary RunStore writes as JSON.
///
/// Godot's own Json rather than System.Text.Json: no reflection, so nothing breaks under the AOT
/// trimming used for the iOS and Android builds. That is the only reason this half of RunData
/// needs Godot - the test project leaves this file out.
public partial class RunData
{
    public const int SaveVersion = 10;

    public Godot.Collections.Dictionary ToSaveData()
    {
        Godot.Collections.Array inventory = new Godot.Collections.Array();
        foreach (ModifierDef def in Inventory)
        {
            inventory.Add(new Godot.Collections.Dictionary
            {
                { "value", def.Value },
                { "flip", def.CanFlipValue },
                { "effect", (int)def.Effect },
            });
        }

        Godot.Collections.Array sideDeck = new Godot.Collections.Array();
        foreach (int index in SideDeck) sideDeck.Add(index);

        Godot.Collections.Array rolledEffects = new Godot.Collections.Array();
        foreach (CardEffect effect in RolledEffects) rolledEffects.Add((int)effect);

        Godot.Collections.Array endlessScores = new Godot.Collections.Array();
        foreach (EndlessScore score in EndlessScores)
        {
            endlessScores.Add(new Godot.Collections.Dictionary
            {
                { "streak", score.Streak },
                { "at", score.UnixTime },
            });
        }

        return new Godot.Collections.Dictionary
        {
            { "version", SaveVersion },
            { "ownedDecks", ToArray(OwnedDecks) },
            { "ownedBoards", ToArray(OwnedBoards) },
            { "deck", SelectedDeck },
            { "board", SelectedBoard },
            { "active", RunActive },
            { "medals", Medals },
            { "step", StepIndex },
            { "furthest", FurthestStep },
            { "tutorialSeen", TutorialSeen },
            { "cardsMet", ToArray(CardsMet) },
            { "collectorBack", CollectorBack },
            { "endless", Endless },
            { "endlessStreak", EndlessStreak },
            { "endlessBest", EndlessBest },
            { "endlessBanked", EndlessRunBanked },
            { "endlessScores", endlessScores },
            { "matchRescueUsed", MatchRescueUsed },
            { "rolledStep", RolledStep },
            { "rolledTarget", RolledTarget },
            { "rolledEffects", rolledEffects },
            { "inventory", inventory },
            { "sideDeck", sideDeck },
        };
    }

    private static Godot.Collections.Array ToArray(IEnumerable<string> keys)
    {
        Godot.Collections.Array array = new Godot.Collections.Array();
        foreach (string key in keys) array.Add(key);
        return array;
    }

    private static void LoadOwned(Godot.Collections.Dictionary data, string name, HashSet<string> into)
    {
        into.Clear();
        into.Add(Cosmetics.Default);
        if (!data.TryGetValue(name, out Variant list) || list.VariantType != Variant.Type.Array) return;
        foreach (Variant entry in list.AsGodotArray())
            if (Cosmetics.IsCosmetic(entry.AsString())) into.Add(entry.AsString());
    }

    /// Replaces this profile with what a save holds, migrating anything older on the way in.
    public void LoadSaveData(Godot.Collections.Dictionary data)
    {
        RunActive = data.TryGetValue("active", out Variant active) && active.AsBool();
        Medals = data.TryGetValue("medals", out Variant medals) ? medals.AsInt32() : 0;
        StepIndex = data.TryGetValue("step", out Variant step) ? step.AsInt32() : 0;
        FurthestStep = data.TryGetValue("furthest", out Variant furthest) ? furthest.AsInt32() : StepIndex;
        TutorialSeen = data.TryGetValue("tutorialSeen", out Variant taught) && taught.AsBool();

        // Version 9: decks and boards. Version 10: Classic is the default, not Bronze. A version 9
        // save was GIVEN Bronze, so it keeps owning it - but moves onto Classic, the new default.
        int saveVersion = data.TryGetValue("version", out Variant savedVersion) ? savedVersion.AsInt32() : 0;
        LoadOwned(data, "ownedDecks", OwnedDecks);
        LoadOwned(data, "ownedBoards", OwnedBoards);
        if (saveVersion == 9) { OwnedDecks.Add("bronze"); OwnedBoards.Add("bronze"); }
        bool keepChoice = saveVersion >= 10;
        SelectedDeck = keepChoice && data.TryGetValue("deck", out Variant deck) && OwnedDecks.Contains(deck.AsString()) ? deck.AsString() : Cosmetics.Default;
        SelectedBoard = keepChoice && data.TryGetValue("board", out Variant board) && OwnedBoards.Contains(board.AsString()) ? board.AsString() : Cosmetics.Default;
        Endless = data.TryGetValue("endless", out Variant endless) && endless.AsBool();
        EndlessStreak = data.TryGetValue("endlessStreak", out Variant streak) ? streak.AsInt32() : 0;
        EndlessBest = data.TryGetValue("endlessBest", out Variant best) ? best.AsInt32() : 0;

        // Version 8. A version 7 save has neither key: the board loads empty and the run in
        // progress loads as not yet banked, so an endless run that survives the update still gets
        // its place when it ends. EndlessBest is untouched, so the one number that existed before
        // is not lost - it simply has no dated rows behind it until the next run ends.
        EndlessRunBanked = data.TryGetValue("endlessBanked", out Variant banked) && banked.AsBool();
        EndlessScores.Clear();
        if (data.TryGetValue("endlessScores", out Variant scores))
        {
            foreach (Variant entry in scores.AsGodotArray())
            {
                if (entry.VariantType != Variant.Type.Dictionary) continue;
                Godot.Collections.Dictionary row = entry.AsGodotDictionary();
                // rowStreak, not streak: "streak" already names the out-variable of the
                // EndlessStreak read above, and an out-variable's scope is the whole method.
                int rowStreak = row.TryGetValue("streak", out Variant st) ? st.AsInt32() : 0;
                if (rowStreak <= 0) continue;
                long at = row.TryGetValue("at", out Variant when) ? when.AsInt64() : 0;
                EndlessScores.Add(new EndlessScore(rowStreak, at));
            }
            EndlessRules.SortAndTrim(EndlessScores);
        }
        // Version 7. A version 6 save carried "endlessRescueUsed" (once per endless RUN), which
        // no longer means anything; it is ignored, and a missing key loads as a fresh match.
        MatchRescueUsed = data.TryGetValue("matchRescueUsed", out Variant rescued) && rescued.AsBool();
        if (Endless) StepIndex = Ladder.Length - 1;

        // A version 4 save has no key and loads as an empty set, so an existing player is
        // introduced to each card once more. That is the right way round: the alternative is
        // assuming they have met cards nobody ever showed them.
        CardsMet.Clear();
        if (data.TryGetValue("cardsMet", out Variant met))
        {
            foreach (Variant entry in met.AsGodotArray())
            {
                string key = entry.AsString();
                if (!string.IsNullOrEmpty(key)) CardsMet.Add(key);
            }
        }

        Inventory.Clear();
        if (data.TryGetValue("inventory", out Variant inventoryVariant))
        {
            foreach (Variant entry in inventoryVariant.AsGodotArray())
            {
                Godot.Collections.Dictionary card = entry.AsGodotDictionary();
                int value = card.TryGetValue("value", out Variant v) ? v.AsInt32() : 0;
                bool flip = card.TryGetValue("flip", out Variant f) && f.AsBool();
                int rawEffect = card.TryGetValue("effect", out Variant e) ? e.AsInt32() : 0;
                if (MigrateModifier(value, flip, rawEffect, out ModifierDef def)) Inventory.Add(def);
            }
        }

        // Version 6 adds the collection log. An older save knows nothing of plain magnitudes, but
        // everything in the collection has plainly been met, so the log starts from what is owned
        // rather than from nothing.
        foreach (ModifierDef def in Inventory) CardsMet.Add(def.LogKey);
        CollectorBack = data.TryGetValue("collectorBack", out Variant gilded)
            ? gilded.AsBool()
            : CollectionComplete;

        // The finale's roll. Anything unreadable is dropped and re-rolled on arrival, which only
        // costs the player a different - still fair - set of rules.
        ClearRuleset();
        if (data.TryGetValue("rolledStep", out Variant rolledStep) && rolledStep.AsInt32() >= 0
            && data.TryGetValue("rolledEffects", out Variant rolledEffects))
        {
            foreach (Variant entry in rolledEffects.AsGodotArray())
            {
                int raw = entry.AsInt32();
                if (Enum.IsDefined(typeof(CardEffect), raw) && CardEffects.IsWired((CardEffect)raw))
                    RolledEffects.Add((CardEffect)raw);
            }
            RolledTarget = data.TryGetValue("rolledTarget", out Variant t) ? t.AsInt32() : 0;
            RolledStep = (RolledEffects.Count > 0 && RolledTarget > 0) ? rolledStep.AsInt32() : -1;
            if (RolledStep < 0) ClearRuleset();
        }

        SideDeck.Clear();
        if (data.TryGetValue("sideDeck", out Variant deckVariant))
        {
            foreach (Variant entry in deckVariant.AsGodotArray())
            {
                int index = entry.AsInt32();
                if (index >= 0 && index < Inventory.Count) SideDeck.Add(index);
            }
        }

        // A save that lost its cards (a failed write, a hand-edited file) is not recoverable as a
        // run - drop back to "no run" rather than starting a match with an empty hand. StartNewRun
        // then rebuilds the collection from the starters.
        if (RunActive && (Inventory.Count == 0 || SideDeck.Count == 0)) RunActive = false;
    }
}
