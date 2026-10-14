using System;
using System.IO;
using System.Text;
using System.Text.Json;

/// What goes into the cloud save, and how two saves are weighed against each other (2026-10-09).
/// Plain C#, so the tests hold it; CloudSave is the Godot side that moves the bytes.
///
/// One blob, versioned: the profile exactly as user://run.json keeps it, plus the online account
/// (user://online.cfg's id and secret), so a player on a new phone keeps their name, friends and
/// leaderboard place as well as their cards. Nothing else - no device id, no email - and it lives
/// in the player's own Google Play Games account, never on the game's server.
public static class CloudBundle
{
    /// The one save slot, by name. Play Games allows letters, digits and - . _ ~ only.
    public const string SnapshotName = "critical-count-profile";

    public const int Version = 1;

    public static byte[] Pack(string runJson, string onlineId, string onlineSecret)
    {
        using MemoryStream stream = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("run", runJson ?? string.Empty);
            if (!string.IsNullOrEmpty(onlineId) && !string.IsNullOrEmpty(onlineSecret))
            {
                w.WriteStartObject("online");
                w.WriteString("id", onlineId);
                w.WriteString("secret", onlineSecret);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// False for anything that is not a bundle this version can read - an empty slot, junk, or a
    /// bundle from a newer game - and the caller then treats the cloud as having nothing in it.
    public static bool TryUnpack(byte[] data, out string runJson, out string onlineId, out string onlineSecret)
    {
        runJson = onlineId = onlineSecret = null;
        if (data == null || data.Length == 0) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(data));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("v", out JsonElement v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() > Version)
                return false;
            if (!root.TryGetProperty("run", out JsonElement run) || run.ValueKind != JsonValueKind.String) return false;
            runJson = run.GetString();
            if (root.TryGetProperty("online", out JsonElement online) && online.ValueKind == JsonValueKind.Object)
            {
                if (online.TryGetProperty("id", out JsonElement id)) onlineId = id.GetString();
                if (online.TryGetProperty("secret", out JsonElement secret)) onlineSecret = secret.GetString();
            }
            return !string.IsNullOrEmpty(runJson);
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (FormatException) { return false; }
    }

    /// Which save is further along: positive when `a` is, negative when `b` is, 0 when neither.
    /// Climb first (the furthest stage, then the tier checkpoint), then the Endless record, then
    /// the collection - the things a player would be upset to lose, in the order they would mind.
    /// Medals come last: they go down when spent, so they only break a tie.
    public static int Compare(RunData a, RunData b)
    {
        int c = a.FurthestStep.CompareTo(b.FurthestStep);
        if (c == 0) c = a.CheckpointStep.CompareTo(b.CheckpointStep);
        if (c == 0) c = a.EndlessBest.CompareTo(b.EndlessBest);
        if (c == 0) c = a.Inventory.Count.CompareTo(b.Inventory.Count);
        if (c == 0) c = (a.OwnedDecks.Count + a.OwnedBoards.Count).CompareTo(b.OwnedDecks.Count + b.OwnedBoards.Count);
        if (c == 0) c = a.Medals.CompareTo(b.Medals);
        return Math.Sign(c);
    }

    /// The same order as one number, for Play Games' own "keep the save with the most progress"
    /// rule when two phones write at once.
    public static long ProgressValue(RunData run) =>
        run.FurthestStep * 1_000_000L + run.EndlessBest * 1_000L + Math.Min(run.Inventory.Count, 999);

    /// One line a player can recognise their save by: "Stage 23 reached - 412 medals - 31 cards".
    public static string Summary(RunData run)
    {
        int stage = Math.Min(run.FurthestStep + 1, Ladder.Length);
        string line = run.FurthestStep >= Ladder.Length
            ? "All 50 stages cleared"
            : $"Stage {stage} reached";
        line += $"   -   {run.Medals} medals   -   {run.Inventory.Count} cards";
        if (run.EndlessBest > 0) line += $"   -   Endless best {run.EndlessBest}";
        return line;
    }
}
