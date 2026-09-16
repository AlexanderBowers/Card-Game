using Godot;
using System;

/// <summary>
/// Ads and the No Ads purchase (claude/monetization-spec.md, decided 2026-09-16).
///
/// STUBS. No ad SDK and no store plugin are wired yet. The spec's build order says to build and
/// playtest the flows first, then add AdMob / Play Billing / StoreKit behind these same calls, so
/// nothing in GameManager has to change when they arrive.
///
///   - A "test ad" is a full-screen overlay with a countdown: watch it to the end, or close it
///     early. Both outcomes of the rescue can be exercised on desktop and on the S25.
///   - The test store grants No Ads for free, but ONLY in debug builds. A release export reports
///     the store as unavailable, so a stub can never give the purchase away in a shipped game.
/// </summary>
public static class AdService
{
    public enum RewardResult
    {
        /// Watched to the end: the reward is earned.
        Completed,
        /// Closed early: counts as "No thanks" (spec §3.2).
        Skipped,
        /// Nothing to show (no network / no fill). The caller must carry on without it.
        Failed,
    }

    /// How long the test ad runs before it counts as watched.
    private const float TestAdSeconds = 3f;

    /// Debug only: pretend the ad network has nothing to show, to test spec §2 / §3.2's "ad can't
    /// load" paths without pulling the network cable.
    public static bool DebugSimulateNoFill { get; set; }

    /// Ads are a mobile thing. Steam / desktop players get the No Ads behaviour for free (spec §5).
    /// Debug builds count as mobile so the flows can be tested from the editor.
    public static bool PlatformHasAds => OS.HasFeature("mobile") || OS.IsDebugBuild();

    /// True when this player should see ads at all.
    public static bool AdsActive => PlatformHasAds && !PurchaseService.OwnsNoAds;

    /// Whether a rewarded ad could be shown right now. The real SDK answers this from its
    /// preloaded ad; the stub answers from the debug switch.
    public static bool RewardedReady => AdsActive && !DebugSimulateNoFill;

    public static bool InterstitialReady => AdsActive && !DebugSimulateNoFill;

    /// The short ad the player can close, between local co-op matches (spec §2). Always calls
    /// onDone exactly once - immediately if there is nothing to show, so the next match is
    /// never blocked waiting on an ad.
    public static void ShowInterstitial(Node host, Action onDone)
    {
        if (!InterstitialReady || host == null)
        {
            onDone?.Invoke();
            return;
        }
        ShowTestAd(host, rewarded: false, result => onDone?.Invoke());
    }

    /// The optional ad behind the rescue card (spec §3.2). Always calls onResult exactly once.
    public static void ShowRewarded(Node host, Action<RewardResult> onResult)
    {
        if (!RewardedReady || host == null)
        {
            onResult?.Invoke(RewardResult.Failed);
            return;
        }
        ShowTestAd(host, rewarded: true, onResult);
    }

    // ------------------------------------------------------------------
    // The test ad
    // ------------------------------------------------------------------
    private static void ShowTestAd(Node host, bool rewarded, Action<RewardResult> onResult)
    {
        // Its own CanvasLayer, so it sits above every table overlay whatever their order.
        CanvasLayer layer = new CanvasLayer { Layer = 100 };
        host.AddChild(layer);

        Control root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        layer.AddChild(root);
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        ColorRect bg = new ColorRect { Color = new Color(0.05f, 0.05f, 0.08f, 1f), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddChild(bg);
        bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        VBoxContainer box = OverlayUi.AddPanel(root);
        box.AddChild(OverlayUi.MakeLabel("TEST AD", 36, OverlayUi.MedalGold));
        box.AddChild(OverlayUi.MakeLabel(
            "No ad network is connected yet.\nA real ad would play here.", 16, OverlayUi.Muted));

        Label countdown = OverlayUi.MakeLabel("", 22);
        box.AddChild(countdown);

        Button close = new Button { CustomMinimumSize = new Vector2(280, 48) };
        box.AddChild(close);

        bool finished = false;
        double remaining = TestAdSeconds;

        void Finish(RewardResult result)
        {
            if (finished) return;
            finished = true;
            layer.QueueFree();
            onResult?.Invoke(result);
        }

        void Refresh()
        {
            bool done = remaining <= 0;
            countdown.Text = done ? "Ad finished." : $"{Math.Ceiling(remaining)}...";
            // A rewarded ad says what closing early costs; the between-matches ad just closes.
            close.Text = done ? "Close"
                       : rewarded ? "Close early (no reward)"
                       : "Skip";
        }

        Refresh();

        Timer timer = new Timer { WaitTime = 0.25, OneShot = false, Autostart = true };
        layer.AddChild(timer);
        timer.Timeout += () =>
        {
            if (finished) return;
            remaining -= timer.WaitTime;
            Refresh();
        };

        close.Pressed += () =>
            Finish(remaining <= 0 ? RewardResult.Completed : RewardResult.Skipped);
    }
}

/// <summary>
/// The $0.99 No Ads non-consumable (spec §4). Ownership is profile level and lives in its own file,
/// not in run.json, so neither a lost run nor the debug "Wipe Save" can take a purchase away.
/// </summary>
public static class PurchaseService
{
    public const string NoAdsProductId = "no_ads";
    public const string NoAdsPriceLabel = "$0.99"; // the real SDK supplies the local price string

    private const string SavePath = "user://purchases.cfg";
    private const string Section = "owned";

    private static bool _loaded;
    private static bool _ownsNoAds;

    public static bool OwnsNoAds
    {
        get
        {
            EnsureLoaded();
            return _ownsNoAds;
        }
    }

    /// Whether a purchase can be made on this build. The stub store only exists in debug builds,
    /// so a release export never shows a Remove Ads button that would hand the purchase out free.
    public static bool StoreAvailable => AdService.PlatformHasAds && OS.IsDebugBuild();

    /// Apple requires a Restore button for non-consumables. Shown wherever the store is.
    public static bool ShowRestoreButton => StoreAvailable;

    /// Always calls onDone exactly once, with whether the player now owns No Ads.
    public static void BuyNoAds(Action<bool> onDone)
    {
        if (!StoreAvailable)
        {
            onDone?.Invoke(OwnsNoAds);
            return;
        }

        // Stub: the store "succeeds" at once. The real flow is asynchronous and can be cancelled.
        GD.Print("PurchaseService (stub): granting No Ads");
        SetOwned(true);
        onDone?.Invoke(true);
    }

    /// Always calls onDone exactly once, with whether the player owns No Ads after the restore.
    public static void RestorePurchases(Action<bool> onDone)
    {
        // Stub: the file IS the store. The real flow asks Play / StoreKit and writes the answer here.
        _loaded = false;
        onDone?.Invoke(OwnsNoAds);
    }

    /// Debug row only.
    public static void DebugSetOwned(bool owned) => SetOwned(owned);

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        ConfigFile file = new ConfigFile();
        _ownsNoAds = file.Load(SavePath) == Error.Ok
                     && file.GetValue(Section, NoAdsProductId, false).AsBool();
    }

    private static void SetOwned(bool owned)
    {
        _ownsNoAds = owned;
        _loaded = true;

        ConfigFile file = new ConfigFile();
        file.Load(SavePath); // keep any other products; a missing file is fine
        file.SetValue(Section, NoAdsProductId, owned);
        Error error = file.Save(SavePath);
        if (error != Error.Ok) GD.PushWarning($"Could not write {SavePath}: {error}");
    }
}
