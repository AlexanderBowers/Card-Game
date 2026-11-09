using Godot;
using System;

/// The game's own Play Store page: the Rate and Share buttons in Options, and Google Play's
/// in-app review card (Testers Community report, 2026-10-31: no way to rate or share the game
/// from inside it).
///
/// Always the release package id, even in debug builds (com.alexbowers.criticalcount): the debug
/// id has no store page, and the point is to land on the real listing.
///
/// Share and the review card go through the CloudyDayStore Android plugin (tools/android-plugin,
/// addons/CloudyDayStore). Where the plugin isn't there (a PC, a local export without the AAR),
/// Share copies the message with the link and Rate opens the store page instead.
public static class StoreLinks
{
    public const string PackageId = "com.cloudydaygames.criticalcount";
    public const string PlayWebUrl = "https://play.google.com/store/apps/details?id=" + PackageId;
    private const string PluginName = "CloudyDayStore";

    /// Only where there is a store page to point at (Android), plus debug builds so the buttons
    /// can be tried on a PC. iOS and Steam get their own links when those builds exist.
    public static bool Available => OS.HasFeature("android") || OS.IsDebugBuild();

    public static string ShareText =>
        "Try Critical Count - a quick card duel where you draw to 20 and bend the numbers. " + PlayWebUrl;

    private static GodotObject Plugin =>
        Engine.HasSingleton(PluginName) ? Engine.GetSingleton(PluginName) : null;

    /// The Play Store app's own page for the game; the web listing if the store app won't open.
    public static void OpenRatePage()
    {
        Error opened = OS.HasFeature("android")
            ? OS.ShellOpen("market://details?id=" + PackageId)
            : Error.Unavailable;
        if (opened != Error.Ok) OS.ShellOpen(PlayWebUrl);
    }

    /// Android's share sheet. Returns false when there is none and the message was copied instead,
    /// so the button can say "Link copied".
    public static bool Share()
    {
        GodotObject plugin = Plugin;
        if (plugin != null)
        {
            plugin.Call("share", "Critical Count", ShareText);
            return true;
        }
        DisplayServer.ClipboardSet(ShareText);
        return false;
    }

    // ------------------------------------------------------------------
    // The in-app review card. Asked for on the start menu after a won solo match - never mid-match,
    // never over a loss. The first time after the third win, then at most once every 30 days after
    // ten more. Play keeps its own quota on top and may show nothing; it never says whether the
    // player rated, so a request counts as asked either way.
    // ------------------------------------------------------------------
    private const string PromptFile = "user://store_prompt.cfg";
    private const int FirstAskWins = 3;
    private const int WinsBetweenAsks = 10;
    private const double DaysBetweenAsks = 30;

    private static bool _askPending;

    /// Called when the player wins a match against the bot.
    public static void NoteMatchWon()
    {
        ConfigFile cfg = Load();
        int wins = cfg.GetValue("review", "wins", 0).AsInt32() + 1;
        int winsAtLastAsk = cfg.GetValue("review", "wins_at_ask", -1).AsInt32();
        double lastAsk = cfg.GetValue("review", "asked_unix", 0.0).AsDouble();
        cfg.SetValue("review", "wins", wins);
        cfg.Save(PromptFile);

        bool firstTime = winsAtLastAsk < 0 && wins >= FirstAskWins;
        bool again = winsAtLastAsk >= 0 && wins - winsAtLastAsk >= WinsBetweenAsks
                     && Time.GetUnixTimeFromSystem() - lastAsk >= DaysBetweenAsks * 86400;
        if (firstTime || again) _askPending = true;
    }

    /// Called when the start menu shows: the quiet moment after a match.
    public static void AskForReviewIfDue()
    {
        if (!_askPending) return;
        _askPending = false;
        GodotObject plugin = Plugin;
        if (plugin == null) return;

        ConfigFile cfg = Load();
        cfg.SetValue("review", "wins_at_ask", cfg.GetValue("review", "wins", 0).AsInt32());
        cfg.SetValue("review", "asked_unix", Time.GetUnixTimeFromSystem());
        cfg.Save(PromptFile);
        plugin.Call("requestReview");
    }

    private static ConfigFile Load()
    {
        ConfigFile cfg = new ConfigFile();
        cfg.Load(PromptFile); // a missing file is a fresh start
        return cfg;
    }
}
