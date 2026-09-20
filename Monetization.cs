using Godot;
using System;

/// <summary>
/// One call to start both services. Made from GameManager._Ready, before anything asks whether an
/// ad is ready or whether No Ads is owned - Play takes a second or two to answer, and the options
/// screen should not be the thing that starts the clock.
/// </summary>
public static class Monetization
{
    public static void Initialize(Node host)
    {
        AdMobBackend.Initialize();
        PurchaseService.Initialize(host);
    }
}

/// <summary>
/// Ads (claude/monetization-spec.md, decided 2026-09-16; wired to AdMob in pass 28).
///
/// The shape of this class has not changed since it was stubs - ShowInterstitial, ShowRewarded,
/// RewardedReady - because the point of the stubs was that it would not have to. What changed is
/// where the calls go:
///
///   - On a phone with the AdMob plugin compiled in, to AdMobBackend and a real ad.
///   - In the editor and on a desktop debug build, to the same "test ad" overlay as before: a
///     full-screen countdown you can watch out or close early, so both outcomes of the bust
///     rescue can still be exercised without a phone.
///   - On a release build with no ad SDK, nowhere. A shipped game must never show a player a
///     placeholder that says TEST AD, so the ad is simply skipped and the caller carries on.
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

    /// Whether there is anything to stand in for an ad at all when no SDK is compiled in. The
    /// test overlay is a development tool, so it exists in debug builds and nowhere else.
    private static bool TestAdAllowed => OS.IsDebugBuild();

    /// Whether a rewarded ad could be shown right now. With the SDK in, this is answered by the
    /// preloaded ad, which is why the backend keeps one warm: the bust-rescue prompt asks this
    /// question mid-round and cannot afford to wait for a load.
    public static bool RewardedReady => AdsActive && !DebugSimulateNoFill
        && (AdMobBackend.Available ? AdMobBackend.RewardedReady : TestAdAllowed);

    public static bool InterstitialReady => AdsActive && !DebugSimulateNoFill
        && (AdMobBackend.Available ? AdMobBackend.InterstitialReady : TestAdAllowed);

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
        if (AdMobBackend.Available)
        {
            AdMobBackend.ShowInterstitial(onDone);
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
        if (AdMobBackend.Available)
        {
            // AdMob reports one bit - did the player watch far enough to earn it. Closing early
            // is the spec's "No thanks", which is Skipped rather than Failed: the offer WAS made
            // and the player turned it down, and the rescue's own fallback handles the rest.
            AdMobBackend.ShowRewarded(earned =>
                onResult?.Invoke(earned ? RewardResult.Completed : RewardResult.Skipped));
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
/// The No Ads non-consumable (spec §4), backed by Google Play Billing in pass 28.
///
/// Ownership is profile level and lives in its own file, not in run.json, so neither a lost run
/// nor the debug "Wipe Save" can take a purchase away. That file is now a CACHE of what Play said
/// rather than the record itself: Play is asked at every launch, and what it says is written here
/// so the game knows the answer while offline.
///
/// The cache is only ever written TRUE. Play not answering - no network, no Play Services, the
/// query timing out - is not evidence that a purchase was revoked, and switching a paid feature
/// off for someone on a plane is a far worse failure than leaving it on through a rare refund.
/// </summary>
public static class PurchaseService
{
    public const string NoAdsProductId = BillingBackend.NoAdsProductId;

    /// Play's own localised price, so the button reads the right currency in the right format.
    /// The fallback is only ever seen before the store has answered, or by the debug stub.
    public static string NoAdsPriceLabel => BillingBackend.PriceLabel ?? "$0.99";

    private const string SavePath = "user://purchases.cfg";
    private const string Section = "owned";

    private static bool _loaded;
    private static bool _ownsNoAds;

    public static bool OwnsNoAds
    {
        get
        {
            EnsureLoaded();
            return _ownsNoAds || BillingBackend.Owned;
        }
    }

    /// Whether a purchase can be made on this build.
    ///
    /// With Play present, both halves have to be true: connected, and the product actually
    /// fetched. Launching a purchase for a product whose details were never queried is a
    /// DEVELOPER_ERROR, and a Remove Ads button that cannot say a price is not one to show.
    ///
    /// Without Play - desktop, the editor, or before the first upload to a Play track - the old
    /// stub store stands in, and only in debug builds, so a release export can never show a button
    /// that hands the purchase out free.
    public static bool StoreAvailable => BillingBackend.Available
        ? (BillingBackend.Connected && BillingBackend.ProductReady)
        : (AdService.PlatformHasAds && OS.IsDebugBuild());

    /// Apple requires a Restore button for non-consumables. Shown wherever the store is.
    public static bool ShowRestoreButton => StoreAvailable;

    /// Starts Play's connection and asks what this account already owns. Safe to call once, from
    /// Monetization.Initialize; everything else here works whether or not it ever succeeded.
    public static void Initialize(Node host)
    {
        BillingBackend.Initialize(host, () =>
        {
            // Play answered. Cache a yes so the next launch knows it before the network does.
            if (BillingBackend.Owned) SetOwned(true);
        });
    }

    /// Always calls onDone exactly once, with whether the player now owns No Ads.
    ///
    /// "Exactly once" is load-bearing: the options screen redraws itself in this callback, and the
    /// real flow can be cancelled, can fail, or can simply never come back if Play's sheet is
    /// dismissed by the system. BillingBackend holds a clock on it for that last case.
    public static void BuyNoAds(Action<bool> onDone)
    {
        if (!StoreAvailable)
        {
            onDone?.Invoke(OwnsNoAds);
            return;
        }

        if (BillingBackend.Available)
        {
            BillingBackend.Purchase(owned =>
            {
                if (owned) SetOwned(true);
                onDone?.Invoke(OwnsNoAds);
            });
            return;
        }

        // Stub: the store "succeeds" at once, in debug builds only.
        GD.Print("PurchaseService (stub): granting No Ads");
        SetOwned(true);
        onDone?.Invoke(true);
    }

    /// Always calls onDone exactly once, with whether the player owns No Ads after the restore.
    ///
    /// There is no restore call as such on Play: a non-consumable that was never consumed comes
    /// back in the purchases query on any device signed into the same account. Apple requires the
    /// button, and it costs nothing to honour it here too - someone who reinstalled and is staring
    /// at ads they paid to remove will press it before they write a review.
    public static void RestorePurchases(Action<bool> onDone)
    {
        if (BillingBackend.Available)
        {
            BillingBackend.Restore(owned =>
            {
                if (owned) SetOwned(true);
                onDone?.Invoke(OwnsNoAds);
            });
            return;
        }

        // Stub: the file IS the store.
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
