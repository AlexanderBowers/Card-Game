using Godot;
using System;
#if GODOT_ADMOB
using PoingStudios.AdMob.Api;
using PoingStudios.AdMob.Api.Core;
using PoingStudios.AdMob.Api.Listeners;
using PoingStudios.AdMob.Ump.Api;
using PoingStudios.AdMob.Ump.Core;
#endif

/// <summary>
/// Every AdMob call in the game lives in this one file (claude/pass-28-real-ads-and-billing.md).
///
/// `AdService` in Monetization.cs keeps the shape it has always had - `ShowInterstitial`,
/// `ShowRewarded`, `RewardedReady` - and calls in here. Nothing else in the project names an ad
/// SDK, so swapping the plugin later is one file, not a search of the codebase.
///
/// ## Compiled out until the plugin is installed
///
/// The poingstudios plugin is a C# API, so its types have to EXIST for this file to build.
/// `AimFor20.csproj` defines `GODOT_ADMOB` when `addons/admob/` is present, so installing the
/// plugin from the Asset Store turns this on and an un-installed checkout still compiles. With the
/// symbol off, `Available` is false and `AdService` falls back to its own test-ad overlay
/// (debug builds) or to no ads at all (a release build, which must never show a fake ad).
///
/// ## Which ad units
///
/// Google's public test units, always, until `AndroidInterstitialId` and friends below are
/// replaced with the real ones from the AdMob console. Test units serve real ad creatives and are
/// safe on a sideloaded build; live units on a dev device are how accounts get flagged for invalid
/// traffic. When the real ids go in, `UseTestAds` keeps debug builds on the test units.
///
/// The AdMob **application** id is NOT set here. It goes in
/// `Project - Project Settings - General - Admob - General`, per platform; the plugin writes it
/// into the manifest.
///
/// ## Threading
///
/// The plugin's docs say the SDK is told to load on background threads, and do not say which
/// thread the callbacks come back on. Everything that leaves this file therefore goes through
/// `Main()`, which defers onto the main thread - a callback that grants a rescue card or frees an
/// overlay from a worker thread is the kind of bug that shows up once a fortnight on one device.
/// </summary>
public static class AdMobBackend
{
    // ------------------------------------------------------------------
    // Ad units
    // ------------------------------------------------------------------
    // Google's sample units, from the plugin's "Enable test ads" page. Replace the four constants
    // below with the AdMob console's own ids when the account exists; leave the Test* ones alone.
    private const string TestAndroidInterstitial = "ca-app-pub-3940256099942544/1033173712";
    private const string TestAndroidRewarded = "ca-app-pub-3940256099942544/5224354917";
    private const string TestIosInterstitial = "ca-app-pub-3940256099942544/4411468910";
    private const string TestIosRewarded = "ca-app-pub-3940256099942544/1712485313";

    /// The real units. Empty means "no account yet" and the test units are used everywhere.
    private const string LiveAndroidInterstitial = "";
    private const string LiveAndroidRewarded = "";
    private const string LiveIosInterstitial = "";
    private const string LiveIosRewarded = "";

    /// A debug build never serves a live ad, even once the real ids are in.
    private static bool UseTestAds => OS.IsDebugBuild() || LiveAndroidInterstitial.Length == 0;

    private static bool IsIos => OS.GetName() == "iOS";

    private static string InterstitialUnitId =>
        UseTestAds ? (IsIos ? TestIosInterstitial : TestAndroidInterstitial)
                   : (IsIos ? LiveIosInterstitial : LiveAndroidInterstitial);

    private static string RewardedUnitId =>
        UseTestAds ? (IsIos ? TestIosRewarded : TestAndroidRewarded)
                   : (IsIos ? LiveIosRewarded : LiveAndroidRewarded);

    /// Runs an action on the main thread. See the threading note above.
    private static void Main(Action action)
    {
        if (action == null) return;
        Callable.From(action).CallDeferred();
    }

#if !GODOT_ADMOB
    // ------------------------------------------------------------------
    // The plugin is not installed. Everything answers "no".
    // ------------------------------------------------------------------
    public static bool Available => false;

    public static bool InterstitialReady => false;

    public static bool RewardedReady => false;

    public static void Initialize() { }

    public static void ShowInterstitial(Action onDone) => Main(() => onDone?.Invoke());

    public static void ShowRewarded(Action<bool> onEarned) => Main(() => onEarned?.Invoke(false));
#else
    // ------------------------------------------------------------------
    // The real thing
    // ------------------------------------------------------------------
    /// The plugin only has an SDK to talk to on a phone. In the editor its calls would either do
    /// nothing or throw, and `AdService` wants the test-ad overlay there anyway.
    public static bool Available => OS.HasFeature("mobile");

    private static bool _initialised;

    /// Consent has been asked for and answered (or was never required). Ads are not requested
    /// before this, so the first request already carries the right personalisation.
    private static bool _consentSettled;

    private static InterstitialAd _interstitial;
    private static RewardedAd _rewarded;
    private static bool _interstitialLoading;
    private static bool _rewardedLoading;

    // The docs' own examples keep these in fields rather than passing temporaries. Followed here
    // for the same reason: nothing else holds the delegates alive.
    private static ConsentForm _consentForm;
    private static FullScreenContentCallback _interstitialCallback;
    private static FullScreenContentCallback _rewardedCallback;
    private static OnUserEarnedRewardListener _rewardListener;

    public static bool InterstitialReady => _interstitial != null;

    public static bool RewardedReady => _rewarded != null;

    public static void Initialize()
    {
        if (_initialised || !Available) return;
        _initialised = true;

        MobileAds.Initialize();
        RequestConsent();
    }

    // ------------------------------------------------------------------
    // Consent (UMP)
    // ------------------------------------------------------------------
    // Required by AdMob policy in the EEA, the UK and several US states before a personalised ad
    // may be requested. The flow is: ask the SDK to refresh what it knows, and if that says a form
    // is required, load it and show it. Everywhere else it settles immediately and no dialog is
    // ever shown.
    //
    // Preloading waits for it either way. That costs nothing - the first ad the game needs is a
    // whole match away - and it is the only way to be sure the first request is not made before
    // the answer is in.
    private static void RequestConsent()
    {
        try
        {
            ConsentRequestParameters request = new ConsentRequestParameters
            {
                TagForUnderAgeOfConsent = false,
            };
            UserMessagingPlatform.ConsentInformation.Update(request, OnConsentUpdated, OnConsentUpdateFailed);
        }
        catch (Exception e)
        {
            GD.PushWarning($"AdMob: consent request failed to start ({e.Message}); continuing without it.");
            SettleConsent();
        }
    }

    private static void OnConsentUpdated()
    {
        if (!UserMessagingPlatform.ConsentInformation.GetIsConsentFormAvailable())
        {
            SettleConsent();
            return;
        }
        UserMessagingPlatform.LoadConsentForm(OnConsentFormLoaded, OnConsentFormFailed);
    }

    private static void OnConsentUpdateFailed(FormError error)
    {
        // No consent info is not a reason to have no game. AdMob serves non-personalised ads in
        // this case; the flow runs again next launch.
        GD.Print($"AdMob: consent update failed ({error?.Message}).");
        SettleConsent();
    }

    private static void OnConsentFormLoaded(ConsentForm form)
    {
        _consentForm = form;
        if (UserMessagingPlatform.ConsentInformation.GetConsentStatus() == ConsentStatus.Values.Required)
        {
            form.Show(OnConsentFormDismissed);
            return;
        }
        SettleConsent();
    }

    private static void OnConsentFormFailed(FormError error)
    {
        GD.Print($"AdMob: consent form failed to load ({error?.Message}).");
        SettleConsent();
    }

    private static void OnConsentFormDismissed(FormError error)
    {
        SettleConsent();
    }

    private static void SettleConsent()
    {
        if (_consentSettled) return;
        _consentSettled = true;
        Main(() =>
        {
            LoadInterstitial();
            LoadRewarded();
        });
    }

    // ------------------------------------------------------------------
    // Preloading
    // ------------------------------------------------------------------
    // Full-screen ads are single use: shown once, destroyed, loaded again. Both are kept warm so
    // that `RewardedReady` can be answered honestly at the moment the bust-rescue prompt has to
    // decide whether to offer the ad at all (spec 3.2) - asking at that moment and waiting would
    // show the player a spinner in the middle of a round.
    private static void LoadInterstitial()
    {
        if (!Available || !_consentSettled || _interstitial != null || _interstitialLoading) return;
        _interstitialLoading = true;

        InterstitialAdLoadCallback callback = new InterstitialAdLoadCallback
        {
            OnAdLoaded = ad => Main(() =>
            {
                _interstitialLoading = false;
                _interstitial = ad;
            }),
            OnAdFailedToLoad = error => Main(() =>
            {
                _interstitialLoading = false;
                GD.Print($"AdMob: interstitial failed to load ({error?.Message}).");
            }),
        };
        new InterstitialAdLoader().Load(InterstitialUnitId, new AdRequest(), callback);
    }

    private static void LoadRewarded()
    {
        if (!Available || !_consentSettled || _rewarded != null || _rewardedLoading) return;
        _rewardedLoading = true;

        RewardedAdLoadCallback callback = new RewardedAdLoadCallback
        {
            OnAdLoaded = ad => Main(() =>
            {
                _rewardedLoading = false;
                _rewarded = ad;
            }),
            OnAdFailedToLoad = error => Main(() =>
            {
                _rewardedLoading = false;
                GD.Print($"AdMob: rewarded failed to load ({error?.Message}).");
            }),
        };
        new RewardedAdLoader().Load(RewardedUnitId, new AdRequest(), callback);
    }

    // ------------------------------------------------------------------
    // Showing
    // ------------------------------------------------------------------
    /// The between-matches ad (spec 2). `onDone` runs exactly once, whatever happens - the next
    /// match is never held up by an ad that failed to show.
    public static void ShowInterstitial(Action onDone)
    {
        InterstitialAd ad = _interstitial;
        if (ad == null)
        {
            LoadInterstitial();
            Main(() => onDone?.Invoke());
            return;
        }

        // Taken out of the field first: whatever happens next, this ad is spent, and a second
        // caller must not be told it is ready.
        _interstitial = null;

        bool finished = false;
        void Finish()
        {
            if (finished) return;
            finished = true;
            Main(() =>
            {
                ad.Destroy();
                LoadInterstitial();
                onDone?.Invoke();
            });
        }

        _interstitialCallback = new FullScreenContentCallback
        {
            OnAdDismissedFullScreenContent = Finish,
            OnAdFailedToShowFullScreenContent = error =>
            {
                GD.Print($"AdMob: interstitial failed to show ({error?.Message}).");
                Finish();
            },
        };
        ad.FullScreenContentCallback = _interstitialCallback;
        ad.Show();
    }

    /// The optional ad behind the rescue card (spec 3.2). `onEarned` runs exactly once: true only
    /// if the player watched far enough for AdMob to count it.
    ///
    /// The reward and the dismissal arrive on two different objects - `OnUserEarnedReward` on the
    /// listener passed to `Show`, and `OnAdDismissedFullScreenContent` on the content callback -
    /// so the reward is recorded as a flag and the answer is given when the ad closes. That is the
    /// pattern the plugin's docs use, and it is also the only one that is right whichever order
    /// the two callbacks happen to arrive in.
    public static void ShowRewarded(Action<bool> onEarned)
    {
        RewardedAd ad = _rewarded;
        if (ad == null)
        {
            LoadRewarded();
            Main(() => onEarned?.Invoke(false));
            return;
        }

        _rewarded = null;

        bool earned = false;
        bool finished = false;
        void Finish()
        {
            if (finished) return;
            finished = true;
            bool result = earned;
            Main(() =>
            {
                ad.Destroy();
                LoadRewarded();
                onEarned?.Invoke(result);
            });
        }

        _rewardListener = new OnUserEarnedRewardListener
        {
            OnUserEarnedReward = _ => earned = true,
        };
        _rewardedCallback = new FullScreenContentCallback
        {
            OnAdDismissedFullScreenContent = Finish,
            OnAdFailedToShowFullScreenContent = error =>
            {
                GD.Print($"AdMob: rewarded failed to show ({error?.Message}).");
                Finish();
            },
        };
        ad.FullScreenContentCallback = _rewardedCallback;
        ad.Show(_rewardListener);
    }
#endif
}
