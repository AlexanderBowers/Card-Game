using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

/// The save format: RunData to and from the JSON text RunStore keeps in user://run.json.
///
/// System.Text.Json's reader and writer (JsonDocument / Utf8JsonWriter) rather than its
/// serializer: no reflection, so nothing breaks under the AOT trimming used for the iOS and
/// Android builds - the reason the game used Godot's Json before. These two need no Godot at all,
/// which is what lets the tests round-trip a save and replay every old version's migration.
///
/// Reading is deliberately forgiving. Godot's Json wrote every number as a double, so a number
/// is read as one and rounded; a missing or wrongly-typed key reads as its default, exactly as
/// the Variant-based reader treated it.
public partial class RunData
{
    public const int SaveVersion = 10;

    public string ToSaveJson()
    {
        using MemoryStream stream = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version", SaveVersion);
            WriteStrings(w, "ownedDecks", OwnedDecks);
            WriteStrings(w, "ownedBoards", OwnedBoards);
            w.WriteString("deck", SelectedDeck);
            w.WriteString("board", SelectedBoard);
            w.WriteBoolean("active", RunActive);
            w.WriteNumber("medals", Medals);
            w.WriteNumber("step", StepIndex);
            w.WriteNumber("furthest", FurthestStep);
            w.WriteBoolean("tutorialSeen", TutorialSeen);
            w.WriteBoolean("marketLessonSeen", MarketLessonSeen);
            w.WriteBoolean("flipLessonPending", FlipLessonPending);
            WriteStrings(w, "cardsMet", CardsMet);
            w.WriteBoolean("collectorBack", CollectorBack);
            w.WriteBoolean("endless", Endless);
            w.WriteNumber("endlessStreak", EndlessStreak);
            w.WriteNumber("endlessBest", EndlessBest);
            w.WriteBoolean("endlessBanked", EndlessRunBanked);

            w.WriteStartArray("endlessScores");
            foreach (EndlessScore score in EndlessScores)
            {
                w.WriteStartObject();
                w.WriteNumber("streak", score.Streak);
                w.WriteNumber("at", score.UnixTime);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteBoolean("matchRescueUsed", MatchRescueUsed);
            w.WriteNumber("rolledStep", RolledStep);
            w.WriteNumber("rolledTarget", RolledTarget);
            w.WriteStartArray("rolledEffects");
            foreach (CardEffect effect in RolledEffects) w.WriteNumberValue((int)effect);
            w.WriteEndArray();

            w.WriteStartArray("inventory");
            foreach (ModifierDef def in Inventory)
            {
                w.WriteStartObject();
                w.WriteNumber("value", def.Value);
                w.WriteBoolean("flip", def.CanFlipValue);
                w.WriteNumber("effect", (int)def.Effect);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("sideDeck");
            foreach (int index in SideDeck) w.WriteNumberValue(index);
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (string value in values) w.WriteStringValue(value);
        w.WriteEndArray();
    }

    /// Replaces this profile with what a save holds, migrating anything older on the way in.
    /// Returns false, changing nothing, for text that is not a JSON object.
    public bool LoadSaveJson(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json ?? ""); }
        catch (JsonException) { return false; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            LoadSaveData(doc.RootElement);
        }
        return true;
    }

    // ------------------------------------------------------------------
    // Forgiving readers
    // ------------------------------------------------------------------
    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value)
               && value.ValueKind != JsonValueKind.Null;
    }

    private static long AsLong(JsonElement e, long fallback = 0) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetInt64(out long l) ? l : (long)Math.Round(e.GetDouble()),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => fallback,
    };

    private static int AsInt(JsonElement e, int fallback = 0) =>
        (int)Math.Clamp(AsLong(e, fallback), int.MinValue, int.MaxValue);

    private static bool AsBool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => AsLong(e) != 0,
        _ => false,
    };

    private static string AsString(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int Int(JsonElement obj, string name, int fallback = 0) =>
        TryGet(obj, name, out JsonElement e) ? AsInt(e, fallback) : fallback;

    private static bool Bool(JsonElement obj, string name) => TryGet(obj, name, out JsonElement e) && AsBool(e);

    private static IEnumerable<JsonElement> Items(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out JsonElement e) || e.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement item in e.EnumerateArray()) yield return item;
    }

    private static void LoadOwned(JsonElement data, string name, HashSet<string> into)
    {
        into.Clear();
        into.Add(Cosmetics.Default);
        foreach (JsonElement entry in Items(data, name))
        {
            string key = AsString(entry);
            if (Cosmetics.IsCosmetic(key)) into.Add(key);
        }
    }

    private void LoadSaveData(JsonElement data)
    {
        RunActive = Bool(data, "active");
        Medals = Int(data, "medals");
        StepIndex = Int(data, "step");
        FurthestStep = Int(data, "furthest", StepIndex);
        TutorialSeen = Bool(data, "tutorialSeen");
        MarketLessonSeen = Bool(data, "marketLessonSeen");
        FlipLessonPending = Bool(data, "flipLessonPending");

        // Version 9: decks and boards. Version 10: Classic is the default, not Bronze. A version 9
        // save was GIVEN Bronze, so it keeps owning it - but moves onto Classic, the new default.
        int saveVersion = Int(data, "version");
        LoadOwned(data, "ownedDecks", OwnedDecks);
        LoadOwned(data, "ownedBoards", OwnedBoards);
        if (saveVersion == 9) { OwnedDecks.Add("bronze"); OwnedBoards.Add("bronze"); }
        bool keepChoice = saveVersion >= 10;
        string deck = TryGet(data, "deck", out JsonElement d) ? AsString(d) : null;
        string board = TryGet(data, "board", out JsonElement b) ? AsString(b) : null;
        SelectedDeck = keepChoice && deck != null && OwnedDecks.Contains(deck) ? deck : Cosmetics.Default;
        SelectedBoard = keepChoice && board != null && OwnedBoards.Contains(board) ? board : Cosmetics.Default;

        Endless = Bool(data, "endless");
        EndlessStreak = Int(data, "endlessStreak");
        EndlessBest = Int(data, "endlessBest");

        // Version 8. A version 7 save has neither key: the board loads empty and the run in
        // progress loads as not yet banked, so an endless run that survives the update still gets
        // its place when it ends. EndlessBest is untouched, so the one number that existed before
        // is not lost - it simply has no dated rows behind it until the next run ends.
        EndlessRunBanked = Bool(data, "endlessBanked");
        EndlessScores.Clear();
        foreach (JsonElement row in Items(data, "endlessScores"))
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            int rowStreak = Int(row, "streak");
            if (rowStreak <= 0) continue;
            long at = TryGet(row, "at", out JsonElement when) ? AsLong(when) : 0;
            EndlessScores.Add(new EndlessScore(rowStreak, at));
        }
        EndlessRules.SortAndTrim(EndlessScores);

        // Version 7. A version 6 save carried "endlessRescueUsed" (once per endless RUN), which
        // no longer means anything; it is ignored, and a missing key loads as a fresh match.
        MatchRescueUsed = Bool(data, "matchRescueUsed");
        if (Endless) StepIndex = Ladder.Length - 1;

        // A version 4 save has no key and loads as an empty set, so an existing player is
        // introduced to each card once more. That is the right way round: the alternative is
        // assuming they have met cards nobody ever showed them.
        CardsMet.Clear();
        foreach (JsonElement entry in Items(data, "cardsMet"))
        {
            string key = AsString(entry);
            if (!string.IsNullOrEmpty(key)) CardsMet.Add(key);
        }

        Inventory.Clear();
        foreach (JsonElement card in Items(data, "inventory"))
        {
            if (card.ValueKind != JsonValueKind.Object) continue;
            if (MigrateModifier(Int(card, "value"), Bool(card, "flip"), Int(card, "effect"), out ModifierDef def))
                Inventory.Add(def);
        }

        // Version 6 adds the collection log. An older save knows nothing of plain magnitudes, but
        // everything in the collection has plainly been met, so the log starts from what is owned
        // rather than from nothing.
        foreach (ModifierDef def in Inventory) CardsMet.Add(def.LogKey);
        CollectorBack = TryGet(data, "collectorBack", out JsonElement gilded) ? AsBool(gilded) : CollectionComplete;

        // The finale's roll. Anything unreadable is dropped and re-rolled on arrival, which only
        // costs the player a different - still fair - set of rules.
        ClearRuleset();
        int rolledStep = Int(data, "rolledStep", -1);
        if (rolledStep >= 0)
        {
            foreach (JsonElement entry in Items(data, "rolledEffects"))
            {
                int raw = AsInt(entry);
                if (Enum.IsDefined(typeof(CardEffect), raw) && CardEffects.IsWired((CardEffect)raw))
                    RolledEffects.Add((CardEffect)raw);
            }
            RolledTarget = Int(data, "rolledTarget");
            RolledStep = (RolledEffects.Count > 0 && RolledTarget > 0) ? rolledStep : -1;
            if (RolledStep < 0) ClearRuleset();
        }

        SideDeck.Clear();
        foreach (JsonElement entry in Items(data, "sideDeck"))
        {
            int index = AsInt(entry, -1);
            if (index >= 0 && index < Inventory.Count) SideDeck.Add(index);
        }

        // A save that lost its cards (a failed write, a hand-edited file) is not recoverable as a
        // run - drop back to "no run" rather than starting a match with an empty hand. StartNewRun
        // then rebuilds the collection from the starters.
        if (RunActive && (Inventory.Count == 0 || SideDeck.Count == 0)) RunActive = false;
    }
}
