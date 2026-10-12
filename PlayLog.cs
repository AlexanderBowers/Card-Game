using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

/// The play log: an opt-in record of what happened at the table, for bug reports from release
/// builds (tester feedback, 2026-10-12). Every report in that batch was "the game told me no and I
/// don't know why", rebuilt from cropped screenshots - this answers most of them in one line each.
///
/// - Off in release until the player turns it on (Options > Bug reports). Test builds always log,
///   alongside DebugLog's godot.log.
/// - One JSON object per line: the match setup at the start (build, device, screen, mode, stage,
///   target), then every draw, Modifier, special, hold and set result, with the scores before and
///   after - and on a refusal, the rule's own sentence and the values it saw.
/// - Kept on this device only, in user://logs/play.log; when that passes MaxBytes it becomes
///   play.prev.log and a fresh one starts, so the last several matches are always there and the
///   pair never grows past twice the cap. Nothing is uploaded: "Copy play log" puts the end of it
///   on the clipboard to paste into a message, which is how testers already send the screenshots.
/// - No names, accounts or IDs are written. Opponent names are the bot's stage names.
public static class PlayLog
{
    private const string LogDir = "user://logs";
    private const string CurrentPath = LogDir + "/play.log";
    private const string PreviousPath = LogDir + "/play.prev.log";
    private const long MaxBytes = 256 * 1024;
    /// How much the clipboard copy carries: enough for a few matches, small enough to paste.
    private const int CopyLines = 400;

    public static bool Enabled
    {
        get
        {
            if (OS.IsDebugBuild()) return true;
            GameSettings.EnsureLoaded();
            return GameSettings.PlayLog;
        }
    }

    private static int _turn;

    /// A new match: everything needed to read the lines after it, and to set the same table up again.
    public static void MatchStart(string mode, int stage, int target, string opponent, string rules)
    {
        if (!Enabled) return;
        _turn = 0;
        Vector2I screen = DisplayServer.WindowGetSize();
        Event("match_start",
            ("build", ProjectSettings.GetSetting("application/config/version").AsString()),
            ("debug_build", OS.IsDebugBuild()),
            ("os", OS.GetName()),
            ("device", OS.GetModelName()),
            ("screen", $"{screen.X}x{screen.Y}"),
            ("mode", mode),
            ("stage", stage),
            ("target", target),
            ("opponent", opponent),
            ("rules", rules));
    }

    /// A turn has been dealt. Every line after it carries this number until the next.
    public static void TurnDealt(int you, int them)
    {
        if (!Enabled) return;
        _turn++;
        Event("turn", ("you", you), ("them", them));
    }

    /// One thing that happened, with whatever values explain it.
    public static void Event(string type, params (string Key, object Value)[] fields)
    {
        if (!Enabled) return;
        try
        {
            Dictionary<string, object> line = new Dictionary<string, object>
            {
                ["t"] = Time.GetTimeStringFromSystem(),
                ["turn"] = _turn,
                ["ev"] = type,
            };
            foreach ((string key, object value) in fields)
                if (value != null) line[key] = value;
            Append(JsonSerializer.Serialize(line));
        }
        catch (Exception e)
        {
            // Logging must never take the game down with it.
            GD.PushWarning($"PlayLog: {e.Message}");
        }
    }

    /// A card as the log names it: "+3", "+/-2", "Trade Totals".
    public static string Name(Card card)
    {
        if (card == null) return null;
        if (card.Effect != CardEffect.None) return CardEffects.Label(card.Effect);
        if (card.Type == CardType.Main) return card.Value.ToString();
        string sign = card.CanFlipValue ? "+/-" : card.Value > 0 ? "+" : "-";
        return $"{sign}{Math.Abs(card.Value)}";
    }

    /// Who did it, as the log says it: "you" or "them" (local 2-player: "p1" / "p2").
    public static string Side(bool isPlayer1, bool vsBot) =>
        vsBot ? (isPlayer1 ? "you" : "them") : (isPlayer1 ? "p1" : "p2");

    private static void Append(string text)
    {
        DirAccess.MakeDirRecursiveAbsolute(LogDir);

        if (FileAccess.FileExists(CurrentPath))
        {
            using (FileAccess probe = FileAccess.Open(CurrentPath, FileAccess.ModeFlags.Read))
            {
                if (probe != null && probe.GetLength() > MaxBytes)
                {
                    probe.Close();
                    if (FileAccess.FileExists(PreviousPath)) DirAccess.RemoveAbsolute(PreviousPath);
                    DirAccess.RenameAbsolute(CurrentPath, PreviousPath);
                }
            }
        }

        // Opened and closed per line, so a freeze or a crash still leaves every line before it.
        using FileAccess f = FileAccess.FileExists(CurrentPath)
            ? FileAccess.Open(CurrentPath, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(CurrentPath, FileAccess.ModeFlags.Write);
        if (f == null) return;
        f.SeekEnd();
        f.StoreLine(text);
    }

    /// The end of the log, oldest first, with a header line. Empty when nothing has been logged.
    public static string Collect()
    {
        Queue<string> tail = new Queue<string>();
        ReadInto(PreviousPath, tail);
        ReadInto(CurrentPath, tail);
        if (tail.Count == 0) return string.Empty;

        StringBuilder text = new StringBuilder();
        text.AppendLine($"Critical Count play log - {ProjectSettings.GetSetting("application/config/version")} - "
                      + $"{OS.GetName()} {OS.GetModelName()} - copied {Time.GetDatetimeStringFromSystem()}");
        foreach (string line in tail) text.AppendLine(line);
        return text.ToString();
    }

    public static bool CopyToClipboard()
    {
        string text = Collect();
        if (string.IsNullOrWhiteSpace(text)) return false;
        DisplayServer.ClipboardSet(text);
        return true;
    }

    private static void ReadInto(string path, Queue<string> tail)
    {
        if (!FileAccess.FileExists(path)) return;
        using FileAccess f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null) return;
        while (f.GetPosition() < f.GetLength())
        {
            string line = f.GetLine();
            if (line.Length == 0) continue;
            tail.Enqueue(line);
            if (tail.Count > CopyLines) tail.Dequeue();
        }
    }
}
