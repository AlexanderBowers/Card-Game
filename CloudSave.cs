using Godot;
using System;

/// Opt-in cloud save (2026-10-09): the profile kept in the player's own Google Play Games account
/// as well as on the phone, so it survives a new phone or a reinstall. Alexander's ask: opt-in,
/// tied to the Google account, and a local-only profile stays perfectly fine for anyone who never
/// turns it on.
///
/// What it does:
/// - Off until the player turns it on (Options > Cloud Save). Turning it on signs in to Play Games.
/// - On sign-in it reads the cloud copy. One further along than this phone's (a new phone, a
///   reinstall) is offered - "use the cloud save, or keep this phone's" - never applied silently.
///   Otherwise this phone's is uploaded.
/// - From then on every change is uploaded a little after it happens (UploadDelay) and at once
///   when the app is put away, so the cloud is never more than a moment behind.
/// - The online account goes with it (CloudBundle), which is also what makes an online account
///   survive a reinstall at last.
///
/// The Play Games side is the godot-play-game-services plugin (addons/GodotPlayGameServices,
/// MIT), reached through its GDScript clients. This class only exists where that plugin does -
/// an Android build with a Play Games project id - and does nothing anywhere else.
public partial class CloudSave : Node
{
    public static CloudSave Instance { get; private set; }

    /// The Play Games project ("game") id, from Play Console > Play Games Services > Configuration.
    /// Kept as a project setting so the code can tell a build that was configured from one that
    /// was not; the plugin reads its own copy from the Android export preset.
    public static string GameId => ProjectSettings.GetSetting("critical_count/play_games/game_id", "").AsString();

    private const string PluginSingleton = "GodotPlayGameServices";
    private const string PluginAutoload = "/root/GodotPlayGameServices";
    private const string SignInScript = "res://addons/GodotPlayGameServices/scripts/sign_in/sign_in_client.gd";
    private const string SnapshotsScript = "res://addons/GodotPlayGameServices/scripts/snapshots/snapshots_client.gd";

    /// Seconds after a change before it is uploaded: a match end writes the save several times in
    /// a row (medals, the rung, the rolled rules), and one upload covers them all.
    private const double UploadDelay = 20.0;

    /// Can this build use cloud save at all? Android, with the plugin in it and an id configured.
    public static bool Available =>
        OS.GetName() == "Android" && Engine.HasSingleton(PluginSingleton) && !string.IsNullOrEmpty(GameId);

    public enum State { Off, SigningIn, Syncing, Ready, Failed }

    public State Status { get; private set; } = State.Off;

    /// One line for the Options screen.
    public string StatusText { get; private set; } = string.Empty;

    /// Raised when Status or StatusText change, for a screen that is showing them.
    public event Action Changed;

    private Node _signIn;
    private Node _snapshots;
    private bool _pluginReady;
    private bool _signedIn;
    private bool _synced;         // the cloud copy has been read and settled: uploading is now safe
    private bool _uploadPending;
    private Timer _uploadTimer;

    // The cloud copy waiting on the player's choice.
    private string _cloudRun;
    private string _cloudOnlineId;
    private string _cloudOnlineSecret;

    public override void _Ready()
    {
        Instance = this;
        _uploadTimer = new Timer { OneShot = true, WaitTime = UploadDelay };
        _uploadTimer.Timeout += Upload;
        AddChild(_uploadTimer);

        if (RunData.Instance != null) RunData.Instance.Changed += ScheduleUpload;

        GameSettings.EnsureLoaded();
        if (Available && GameSettings.CloudSave) Start(interactive: false);
    }

    public override void _ExitTree()
    {
        if (RunData.Instance != null) RunData.Instance.Changed -= ScheduleUpload;
        if (Instance == this) Instance = null;
    }

    /// The app is being put away or closed: anything not yet uploaded goes now.
    public override void _Notification(int what)
    {
        if (what == NotificationApplicationPaused || what == NotificationWMCloseRequest)
            if (_uploadPending) Upload();
    }

    // ------------------------------------------------------------------
    // On and off (Options)
    // ------------------------------------------------------------------

    public void Enable()
    {
        GameSettings.SetCloudSave(true);
        Start(interactive: true);
    }

    /// Stops keeping the cloud copy up to date. The copy itself stays in the player's Play Games
    /// account, so turning it back on - here or on another phone - finds it.
    public void Disable()
    {
        GameSettings.SetCloudSave(false);
        _uploadTimer.Stop();
        _uploadPending = false;
        _synced = false;
        SetStatus(State.Off, string.Empty);
    }

    private void Start(bool interactive)
    {
        if (!Available) return;
        if (!EnsurePlugin())
        {
            SetStatus(State.Failed, "Google Play Games is not available on this phone");
            return;
        }
        SetStatus(State.SigningIn, "Signing in to Google Play Games...");
        // Play Games v2 signs a returning player in by itself at launch; asking is for the moment
        // the player has just pressed the switch.
        _signIn.Call(interactive ? "sign_in" : "is_authenticated");
    }

    private bool EnsurePlugin()
    {
        if (_pluginReady) return true;
        Node autoload = GetNodeOrNull(PluginAutoload);
        if (autoload == null) return false;
        if (autoload.Call("initialize").AsInt32() != 0) return false;

        _signIn = MakeClient(SignInScript);
        _snapshots = MakeClient(SnapshotsScript);
        if (_signIn == null || _snapshots == null) return false;

        _signIn.Connect("user_authenticated", Callable.From<bool>(OnAuthenticated));
        _snapshots.Connect("game_loaded", Callable.From<GodotObject>(OnGameLoaded));
        _snapshots.Connect("game_saved", Callable.From<bool, string, string>(OnGameSaved));
        _snapshots.Connect("conflict_emitted", Callable.From<GodotObject>(OnConflict));
        _pluginReady = true;
        return true;
    }

    private Node MakeClient(string path)
    {
        if (!ResourceLoader.Exists(path)) return null;
        Node client = GD.Load<GDScript>(path).New().AsGodotObject() as Node;
        if (client != null) AddChild(client);
        return client;
    }

    // ------------------------------------------------------------------
    // Signing in, and reading the cloud copy
    // ------------------------------------------------------------------

    private void OnAuthenticated(bool ok)
    {
        _signedIn = ok;
        if (!ok)
        {
            SetStatus(State.Failed, "Not signed in to Google Play Games. Turn Cloud Save off and on to try again");
            return;
        }
        SetStatus(State.Syncing, "Checking the cloud save...");
        _snapshots.Call("load_game", CloudBundle.SnapshotName, false);
    }

    private void OnGameLoaded(GodotObject snapshot)
    {
        RunData local = RunData.Instance;
        if (local == null) return;

        byte[] content = snapshot?.Get("content").AsByteArray();
        if (!CloudBundle.TryUnpack(content, out string run, out string id, out string secret))
        {
            // Nothing there yet (or nothing this version can read): this phone's becomes the copy.
            Settle();
            return;
        }

        RunData cloud = new RunData();
        if (!cloud.LoadSaveJson(run) || CloudBundle.Compare(cloud, local) <= 0)
        {
            Settle();
            return;
        }

        // The cloud is further along: the player chooses. Never applied without asking - a phone
        // that was simply played offline for a while must not lose those matches by surprise.
        _cloudRun = run;
        _cloudOnlineId = id;
        _cloudOnlineSecret = secret;
        SetStatus(State.Syncing, "A cloud save is waiting for your choice");
        ShowChoice(CloudBundle.Summary(cloud), CloudBundle.Summary(local));
    }

    /// The cloud copy and this phone agree on which is the one to keep: this phone's. Upload it.
    private void Settle()
    {
        _synced = true;
        Upload();
    }

    private void UseCloud()
    {
        if (_cloudRun != null && RunData.Instance != null && RunData.Instance.ReplaceFrom(_cloudRun))
        {
            OnlineService.Instance?.ImportAccount(_cloudOnlineId, _cloudOnlineSecret);
            _synced = true;
            _uploadPending = false;
            SetStatus(State.Ready, "Loaded your cloud save");
            // Everything on screen was drawn from the old profile: start the scene again from it.
            GetTree().ReloadCurrentScene();
        }
        else
        {
            SetStatus(State.Failed, "The cloud save could not be read. Keeping this phone's");
            Settle();
        }
        ClearPending();
    }

    private void KeepLocal()
    {
        ClearPending();
        Settle(); // this phone's replaces the cloud copy
    }

    private void ClearPending()
    {
        _cloudRun = _cloudOnlineId = _cloudOnlineSecret = null;
    }

    // ------------------------------------------------------------------
    // Uploading
    // ------------------------------------------------------------------

    private void ScheduleUpload()
    {
        if (!_synced) return; // never overwrite a cloud copy that has not been read yet
        _uploadPending = true;
        _uploadTimer.Start(); // restarts: a burst of changes is one upload
    }

    private void Upload()
    {
        _uploadTimer.Stop();
        if (!_signedIn || !_synced || _snapshots == null || RunData.Instance == null) return;
        _uploadPending = false;

        RunData run = RunData.Instance;
        (string id, string secret) = OnlineService.Instance?.ExportAccount() ?? (null, null);
        byte[] data = CloudBundle.Pack(run.ToSaveJson(), id, secret);
        _snapshots.Call("save_game", CloudBundle.SnapshotName, CloudBundle.Summary(run), data,
                        0, CloudBundle.ProgressValue(run));
        SetStatus(State.Syncing, "Saving to the cloud...");
    }

    private void OnGameSaved(bool saved, string name, string description)
    {
        if (saved) SetStatus(State.Ready, $"Saved to Google Play Games at {Time.GetTimeStringFromSystem().Substring(0, 5)}");
        else
        {
            SetStatus(State.Failed, "Couldn't reach the cloud. It will try again after your next match");
            _uploadPending = true;
        }
    }

    /// Two phones wrote at once. Read the copy again and weigh it the same way as at sign-in.
    private void OnConflict(GodotObject conflict)
    {
        _synced = false;
        SetStatus(State.Syncing, "Checking the cloud save...");
        _snapshots.Call("load_game", CloudBundle.SnapshotName, false);
    }

    private void SetStatus(State state, string text)
    {
        Status = state;
        StatusText = text;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------
    // The choice
    // ------------------------------------------------------------------

    private void ShowChoice(string cloudSummary, string localSummary)
    {
        Node scene = GetTree().CurrentScene;
        if (scene == null)
        {
            // Nowhere to ask yet (the very first frame): ask once the scene is up.
            Callable.From(() => ShowChoice(cloudSummary, localSummary)).CallDeferred();
            return;
        }

        Control overlay = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        OverlayUi.Host(scene).AddChild(overlay);
        OverlayUi.AddDim(overlay);
        VBoxContainer box = OverlayUi.AddPanel(overlay, contentMargin: 26, separation: 12);

        box.AddChild(OverlayUi.MakeLabel("Cloud save found", 36));
        box.AddChild(Wrapped("Your Google Play Games account has a save that is further along than this phone's.", 20, OverlayUi.Muted));
        box.AddChild(OverlayUi.MakeLabel("In the cloud", 22, OverlayUi.MedalGold));
        box.AddChild(Wrapped(cloudSummary, 20, null));
        box.AddChild(OverlayUi.MakeLabel("On this phone", 22, OverlayUi.MedalGold));
        box.AddChild(Wrapped(localSummary, 20, null));

        Button useCloud = new Button { Text = "Use the cloud save", CustomMinimumSize = new Vector2(380, 62) };
        useCloud.AddThemeFontSizeOverride("font_size", 24);
        OverlayUi.StyleButton(useCloud, primary: true);
        useCloud.Pressed += () => { overlay.QueueFree(); UseCloud(); };
        box.AddChild(useCloud);

        Button keep = new Button { Text = "Keep this phone's (replaces the cloud save)", CustomMinimumSize = new Vector2(380, 62) };
        keep.AddThemeFontSizeOverride("font_size", 20);
        OverlayUi.StyleButton(keep);
        keep.Pressed += () => { overlay.QueueFree(); KeepLocal(); };
        box.AddChild(keep);

        OverlayUi.BringToFront(overlay);
    }

    private static Label Wrapped(string text, int size, Color? color)
    {
        Label label = OverlayUi.MakeLabel(text, size, color);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(380, 0);
        return label;
    }
}
