using Godot;

/// The game's own Play Store page, for the Rate and Share buttons in Options (Testers Community
/// report, 2026-10-31: no way to rate or share the game from inside it).
///
/// Always the release package id, even in debug builds (com.alexbowers.criticalcount): the debug
/// id has no store page, and the point is to land on the real listing.
///
/// Share copies a message with the link rather than opening Android's share sheet. Godot has no
/// share sheet of its own; that needs a small Android plugin, and a copied link works everywhere
/// a player would paste it in the meantime.
public static class StoreLinks
{
    public const string PackageId = "com.cloudydaygames.criticalcount";
    public const string PlayWebUrl = "https://play.google.com/store/apps/details?id=" + PackageId;

    /// Only where there is a store page to point at (Android), plus debug builds so the buttons
    /// can be tried on a PC. iOS and Steam get their own links when those builds exist.
    public static bool Available => OS.HasFeature("android") || OS.IsDebugBuild();

    public static string ShareText =>
        "Try Critical Count - a quick card duel where you draw to 20 and bend the numbers. " + PlayWebUrl;

    /// The Play Store app's own page for the game; the web listing if the store app won't open.
    public static void OpenRatePage()
    {
        Error opened = OS.HasFeature("android")
            ? OS.ShellOpen("market://details?id=" + PackageId)
            : Error.Unavailable;
        if (opened != Error.Ok) OS.ShellOpen(PlayWebUrl);
    }

    public static void CopyShareText() => DisplayServer.ClipboardSet(ShareText);
}
