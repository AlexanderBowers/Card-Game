using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/// Debug log for test builds (2026-10-10). Test builds are the debug exports CI makes from master
/// and branches; the release branch exports with --export-release, so none of this exists there.
///
/// - Godot's own log file is switched on for debug builds only (project.godot:
///   debug/file_logging/enable_file_logging.debug), and every line is flushed straight to disk
///   (application/run/flush_stdout_on_print.debug), so a freeze or a crash still leaves the
///   lines leading up to it.
/// - The file is user://logs/godot.log. Each launch moves the previous one aside as
///   godot<date>.log, so after a freeze the interesting log is the previous session's.
/// - Options > Debug > "Copy debug log" puts the end of this session's log and the previous
///   session's on the clipboard, to paste into a message - no cable or adb needed.
public static class DebugLog
{
    private const string LogDir = "user://logs";
    private const string Current = "godot.log";
    private const int LinesPerFile = 300;

    public static bool Enabled => OS.IsDebugBuild();

    /// One tagged line in the log (and in logcat). Debug builds only.
    public static void Write(string tag, string message)
    {
        if (Enabled) GD.Print($"[{Time.GetTimeStringFromSystem()}] {tag}: {message}");
    }

    /// The end of the previous session's log, then the end of this one. Empty when there is none.
    public static string Collect()
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine($"Critical Count {ProjectSettings.GetSetting("application/config/version")} - {OS.GetName()} {OS.GetModelName()} - {Time.GetDatetimeStringFromSystem()}");

        string previous = PreviousLogName();
        if (previous != null) Append(text, previous, "Previous session");
        Append(text, Current, "This session");
        return text.ToString();
    }

    public static bool CopyToClipboard()
    {
        string text = Collect();
        if (string.IsNullOrWhiteSpace(text)) return false;
        DisplayServer.ClipboardSet(text);
        return true;
    }

    private static string PreviousLogName()
    {
        using DirAccess dir = DirAccess.Open(LogDir);
        if (dir == null) return null;
        // Rotated logs are named godot<date>T<time>.log, so the newest sorts last.
        return dir.GetFiles()
            .Where(f => f.StartsWith("godot") && f.EndsWith(".log") && f != Current)
            .OrderBy(f => f, StringComparer.Ordinal)
            .LastOrDefault();
    }

    private static void Append(StringBuilder text, string file, string heading)
    {
        string path = $"{LogDir}/{file}";
        if (!FileAccess.FileExists(path)) return;
        using FileAccess f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null) return;

        Queue<string> tail = new Queue<string>();
        while (f.GetPosition() < f.GetLength())
        {
            tail.Enqueue(f.GetLine());
            if (tail.Count > LinesPerFile) tail.Dequeue();
        }
        text.AppendLine();
        text.AppendLine($"===== {heading} ({file}, last {tail.Count} lines) =====");
        foreach (string line in tail) text.AppendLine(line);
    }
}
