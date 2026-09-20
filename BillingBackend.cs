using Godot;
using System;

/// <summary>
/// Every Google Play Billing call in the game lives in this one file
/// (claude/pass-28-real-ads-and-billing.md). `PurchaseService` in Monetization.cs keeps its shape
/// and calls in here.
///
/// ## Why this talks to the plugin by name rather than by type
///
/// `GodotGooglePlayBilling` (the Godot Foundation's first-party plugin) is GDScript over a Java
/// singleton. There is no C# class to reference, so this goes straight to the JNI singleton with
/// `Engine.GetSingleton` and string method names. That has one large advantage over loading the
/// GDScript wrapper: no compile-time or file-time dependency at all. A checkout without the addon
/// still builds, runs, and simply reports the store as unavailable - which is also exactly what
/// happens on desktop and in the editor, where the singleton does not exist either.
///
/// The cost is that the names below are the Java ones (camelCase) and the product type is the raw
/// string `"inapp"`, where the GDScript wrapper would have offered `query_product_details` and a
/// `ProductType` enum. They are listed in one place, here, so there is one table to check against
/// the plugin's docs if a version ever renames one.
///
/// `initPlugin` has to be called before anything else; the Kotlin side builds its BillingClient
/// there and every other call throws on a `lateinit` field until it has run. The GDScript wrapper
/// does this in `_init`, which is easy to miss when going around it.
///
/// ## What can actually be tested, and when
///
/// Nothing here works from a sideloaded build alone. Google switches billing on per package only
/// after a build carrying the billing plugin has been uploaded to a Play Console track, the
/// product is created and ACTIVE, and the device's primary Google account is on the licence
/// testing list. After that first upload, licence testers can sideload debug builds and iterate.
/// Until then every call below reports the store as unavailable, which is the correct behaviour
/// and not a bug to chase.
/// </summary>
public static class BillingBackend
{
    // ------------------------------------------------------------------
    // The plugin's own names, in one table
    // ------------------------------------------------------------------
    private const string SingletonName = "GodotGooglePlayBilling";

    private const string MethodInit = "initPlugin";
    private const string MethodStartConnection = "startConnection";
    private const string MethodQueryProductDetails = "queryProductDetails";
    private const string MethodQueryPurchases = "queryPurchases";
    private const string MethodPurchase = "purchase";
    private const string MethodAcknowledge = "acknowledgePurchase";

    private const string SignalConnected = "connected";
    private const string SignalDisconnected = "disconnected";
    private const string SignalConnectError = "connect_error";
    private const string SignalProductDetails = "query_product_details_response";
    private const string SignalPurchasesQueried = "query_purchases_response";
    // Singular. The engine's own older documentation pages call this `on_purchases_updated`, which
    // does not exist and silently never fires.
    private const string SignalPurchaseUpdated = "on_purchase_updated";

    private const string ProductTypeInApp = "inapp";

    /// Billing response codes worth naming (BillingClient.gd).
    private const int ResponseOk = 0;
    private const int ResponseUserCancelled = 1;
    private const int ResponseItemAlreadyOwned = 7;

    /// Purchase states. Nothing is granted below PURCHASED - a PENDING purchase is a bank transfer
    /// that has not cleared.
    private const int PurchaseStatePurchased = 1;

    public const string NoAdsProductId = "no_ads";

    /// A store call that never answers must not leave the options screen waiting for good.
    private const double CallbackTimeoutSeconds = 45;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------
    private static GodotObject _plugin;
    private static Node _host;
    private static bool _initialised;

    private static Action _onChanged;
    private static Action<bool> _pendingPurchase;
    private static Action<bool> _pendingRestore;

    /// The plugin is present and this platform has the singleton. False on desktop, in the editor,
    /// and in any build exported without the addon.
    public static bool Available => _plugin != null;

    public static bool Connected { get; private set; }

    /// The product has been fetched from Play, so its real price is known and a purchase can be
    /// launched. `purchase()` returns DEVELOPER_ERROR if the details were never queried.
    public static bool ProductReady { get; private set; }

    /// Play's own localised price string, e.g. "$0.99" or "0,99 EUR". Null until the query lands.
    public static string PriceLabel { get; private set; }

    /// Play says this account owns No Ads.
    ///
    /// Only ever set TRUE. A failed or offline query is not evidence that a purchase was revoked,
    /// and taking a paid feature away from someone on a train is a far worse failure than leaving
    /// it switched on for a rare refund.
    public static bool Owned { get; private set; }

    // ------------------------------------------------------------------
    // Setup
    // ------------------------------------------------------------------
    /// Connects to Play and asks, in order, what this account already owns and what the product
    /// costs. `onChanged` is called whenever the answer to either moves, so the options screen can
    /// redraw itself if it happens to be open.
    public static void Initialize(Node host, Action onChanged)
    {
        if (_initialised) return;
        _initialised = true;
        _host = host;
        _onChanged = onChanged;

        if (!Engine.HasSingleton(SingletonName)) return;

        _plugin = Engine.GetSingleton(SingletonName);
        if (_plugin == null) return;

        try
        {
            _plugin.Call(MethodInit); // before anything else - see the class note
            _plugin.Connect(SignalConnected, Callable.From(OnConnected));
            _plugin.Connect(SignalDisconnected, Callable.From(OnDisconnected));
            _plugin.Connect(SignalConnectError, Callable.From<int, string>(OnConnectError));
            _plugin.Connect(SignalProductDetails, Callable.From<Godot.Collections.Dictionary>(OnProductDetails));
            _plugin.Connect(SignalPurchasesQueried, Callable.From<Godot.Collections.Dictionary>(OnPurchasesQueried));
            _plugin.Connect(SignalPurchaseUpdated, Callable.From<Godot.Collections.Dictionary>(OnPurchaseUpdated));
            _plugin.Call(MethodStartConnection);
        }
        catch (Exception e)
        {
            GD.PushWarning($"Billing: could not start the Play connection ({e.Message}).");
            _plugin = null;
        }
    }

    private static void OnConnected()
    {
        Connected = true;
        // Ownership first, price second. Someone who already bought No Ads should never see the
        // buy button flash up while the price arrives.
        QueryPurchases();
        QueryProduct();
    }

    private static void OnDisconnected()
    {
        Connected = false;
        Changed();
    }

    private static void OnConnectError(int responseCode, string debugMessage)
    {
        Connected = false;
        GD.Print($"Billing: connection error {responseCode} - {debugMessage}");
        Settle(ref _pendingPurchase);
        Settle(ref _pendingRestore);
        Changed();
    }

    private static void QueryProduct()
    {
        if (_plugin == null) return;
        _plugin.Call(MethodQueryProductDetails,
                     new Godot.Collections.Array { NoAdsProductId }, ProductTypeInApp);
    }

    private static void QueryPurchases()
    {
        if (_plugin == null) return;
        _plugin.Call(MethodQueryPurchases, ProductTypeInApp, false);
    }

    // ------------------------------------------------------------------
    // Buying and restoring
    // ------------------------------------------------------------------
    /// Launches Play's purchase sheet. `onDone` runs exactly once with whether No Ads is owned
    /// afterwards - including on a cancel, where the answer is simply unchanged.
    public static void Purchase(Action<bool> onDone)
    {
        if (_plugin == null || !Connected || !ProductReady)
        {
            onDone?.Invoke(Owned);
            return;
        }

        Hold(ref _pendingPurchase, onDone);

        // The optional arguments are the purchase-option id, the offer id and whether the offer is
        // personalised. A plain one-time product uses none of them, but the Java signature takes
        // all four, so all four are passed.
        Godot.Collections.Dictionary launch =
            _plugin.Call(MethodPurchase, NoAdsProductId, "", "", false).AsGodotDictionary();
        int code = ReadCode(launch);

        if (code == ResponseItemAlreadyOwned)
        {
            // Bought on another device, or bought here and lost locally. Not an error: ask Play
            // what this account owns and let the query answer the caller.
            QueryPurchases();
            return;
        }
        if (code != ResponseOk)
        {
            GD.Print($"Billing: purchase flow did not launch ({code} - {ReadMessage(launch)}).");
            Settle(ref _pendingPurchase);
        }
    }

    /// Apple requires a Restore button and Play has no harm in one. There is no restore call as
    /// such: a non-consumable that was never consumed simply comes back in the purchases query,
    /// on any device signed into the same account.
    public static void Restore(Action<bool> onDone)
    {
        if (_plugin == null || !Connected)
        {
            onDone?.Invoke(Owned);
            return;
        }
        Hold(ref _pendingRestore, onDone);
        QueryPurchases();
    }

    // ------------------------------------------------------------------
    // Responses
    // ------------------------------------------------------------------
    private static void OnProductDetails(Godot.Collections.Dictionary response)
    {
        if (ReadCode(response) != ResponseOk || !response.ContainsKey("product_details"))
        {
            GD.Print($"Billing: product query failed ({ReadCode(response)} - {ReadMessage(response)}).");
            return;
        }

        foreach (Variant entry in response["product_details"].AsGodotArray())
        {
            Godot.Collections.Dictionary product = entry.AsGodotDictionary();
            if (product["product_id"].AsString() != NoAdsProductId) continue;

            // The offer list is plural and an Array as of plugin 3.2; older write-ups show a
            // singular Dictionary. A product with no special offers has exactly one entry, the
            // base offer, and its formatted_price is the string to put on the button.
            if (!product.ContainsKey("one_time_purchase_offer_details_list")) continue;
            Godot.Collections.Array offers = product["one_time_purchase_offer_details_list"].AsGodotArray();
            if (offers.Count == 0) continue;

            Godot.Collections.Dictionary offer = offers[0].AsGodotDictionary();
            if (offer.ContainsKey("formatted_price"))
                PriceLabel = offer["formatted_price"].AsString();

            ProductReady = true;
            Changed();
            return;
        }

        GD.Print($"Billing: Play did not return {NoAdsProductId}. Is the product active in the console?");
    }

    private static void OnPurchasesQueried(Godot.Collections.Dictionary response)
    {
        ProcessPurchases(response);
        Settle(ref _pendingRestore);
    }

    private static void OnPurchaseUpdated(Godot.Collections.Dictionary response)
    {
        int code = ReadCode(response);
        if (code == ResponseUserCancelled)
        {
            Settle(ref _pendingPurchase);
            return;
        }
        if (code != ResponseOk)
            GD.Print($"Billing: purchase update {code} - {ReadMessage(response)}");

        ProcessPurchases(response);
        Settle(ref _pendingPurchase);
    }

    /// Reads a purchases array out of either response and grants on anything that is really ours,
    /// really paid for, and acknowledges it if Play has not seen an acknowledgement yet.
    ///
    /// The `purchases` key is absent on a cancel and on every error, in BOTH signals - indexing
    /// for it unguarded is the crash this guard exists for.
    private static void ProcessPurchases(Godot.Collections.Dictionary response)
    {
        if (ReadCode(response) != ResponseOk || !response.ContainsKey("purchases")) return;

        foreach (Variant entry in response["purchases"].AsGodotArray())
        {
            Godot.Collections.Dictionary purchase = entry.AsGodotDictionary();
            if (System.Array.IndexOf(Names(purchase), NoAdsProductId) < 0) continue;
            if (purchase["purchase_state"].AsInt32() != PurchaseStatePurchased) continue;

            if (!Owned)
            {
                Owned = true;
                Changed();
            }

            // Play refunds an unacknowledged purchase after three days and takes the entitlement
            // back. This has to run from the restore query as well as from the purchase itself: a
            // purchase made and then killed before it was acknowledged comes back here, still
            // unacknowledged, on the next launch.
            if (!purchase["is_acknowledged"].AsBool())
                _plugin?.Call(MethodAcknowledge, purchase["purchase_token"].AsString());
        }
    }

    /// The ids a purchase covers. A PackedStringArray in the response, and a plain array here
    /// rather than anything LINQ-shaped: one product, one lookup, no reason to reach for it.
    private static string[] Names(Godot.Collections.Dictionary purchase) =>
        purchase.ContainsKey("product_ids") ? purchase["product_ids"].AsStringArray() : System.Array.Empty<string>();

    // ------------------------------------------------------------------
    // Plumbing
    // ------------------------------------------------------------------
    private static int ReadCode(Godot.Collections.Dictionary response) =>
        response != null && response.ContainsKey("response_code") ? response["response_code"].AsInt32() : -1;

    private static string ReadMessage(Godot.Collections.Dictionary response) =>
        response != null && response.ContainsKey("debug_message") ? response["debug_message"].AsString() : "";

    private static void Changed() => _onChanged?.Invoke();

    /// Parks a caller's callback and starts a clock on it. Whichever comes first - the store's
    /// answer or the timeout - the caller hears back exactly once.
    private static void Hold(ref Action<bool> slot, Action<bool> callback)
    {
        Settle(ref slot); // a second call while one is in flight answers the first
        slot = callback;

        if (_host == null || !_host.IsInsideTree()) return;
        SceneTreeTimer timer = _host.GetTree().CreateTimer(CallbackTimeoutSeconds);
        Action<bool> parked = callback;
        timer.Timeout += () =>
        {
            if (_pendingPurchase == parked) Settle(ref _pendingPurchase);
            if (_pendingRestore == parked) Settle(ref _pendingRestore);
        };
    }

    private static void Settle(ref Action<bool> slot)
    {
        Action<bool> callback = slot;
        slot = null;
        callback?.Invoke(Owned);
    }
}
