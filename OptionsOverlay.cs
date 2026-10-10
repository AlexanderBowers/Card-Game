using Godot;
using System;

/// <summary>
/// Options: sound, purchases, battery, and (debug builds only) the debug-button switch. Reached
/// from the table's Menu and from the start menu - never from the board itself.
///
/// Every control writes GameSettings immediately; there is no Apply button to forget.
///
/// Remove Ads / Restore Purchases live here as of pass 25. They were on the start menu, which
/// Alexander called "too much for the first thing someone sees when opening the game" - and a
/// purchase is a settings errand, run once, not one of the ways into the game.
/// </summary>
public partial class OptionsOverlay : Control
{

    private CheckButton _animations;
    private CheckButton _batterySaver;
    private CheckButton _highFrameRate;
    private VBoxContainer _onlineSection;
    private Button _deleteAccount;
    private Label _onlineNote;
    private bool _deleteArmed;

    /// Two taps, like New Run: the second one deletes, immediately and for good.
    private async void OnDeleteAccountPressed()
    {
        if (!_deleteArmed)
        {
            _deleteArmed = true;
            _deleteAccount.Text = "Tap again to delete for good";
            return;
        }
        _deleteAccount.Disabled = true;
        _deleteAccount.Text = "Deleting...";
        string problem = OnlineService.Instance == null ? "Not available" : await OnlineService.Instance.DeleteAccount();
        if (!IsInstanceValid(this)) return;
        if (problem != null)
        {
            _deleteArmed = false;
            _deleteAccount.Disabled = false;
            _deleteAccount.Text = "Delete Online Account";
            _onlineNote.Text = problem;
            return;
        }
        _deleteAccount.Text = "Deleted";
        _onlineNote.Text = "Your online account is gone. Opening Online again starts a new one.";
    }
    private CheckButton _table3D;
    private VBoxContainer _cloudSection;
    private CheckButton _cloudToggle;
    private Label _cloudNote;
    private CheckButton _showFps;
    private CheckButton _debugButtons;
    private Button _closeButton;
    private Control _storeSection;      // the heading and the row, so both hide when there is no store
    private VBoxContainer _storeRows;   // refilled on every Open: what it says depends on what is owned
    private Control _privacySection;    // "Privacy Choices": only where the consent SDK requires it
    private Action _onClosed;
    private bool _built;

    // Pass 25: this is a full-screen overlay, so nothing in the table's layout pays for its size.
    private const int TitleFont = 42;
    private const int SectionFont = 26;
    private const int BodyFont = 22;
    private const int NoteFont = 17;
    private const int ButtonFont = 26;
    /// Clamped to the viewport rather than fixed: portrait enlarges the whole UI when there is
    /// room for it, which shrinks the design-pixel viewport this panel is measured in.
    private const float ButtonWidthMax = 460f;
    private const float ButtonHeight = 62f;
    private Vector2 WideButton => new Vector2(
        Mathf.Clamp(OverlayUi.ViewSize(this).X - 88f, 240f, ButtonWidthMax), ButtonHeight);

    private BoxContainer _columns;

    public void Build()
    {
        if (_built) return;
        _built = true;
        GameSettings.EnsureLoaded();

        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        OverlayUi.AddDim(this);

        VBoxContainer box = OverlayUi.AddPanel(this, contentMargin: 26, separation: 10);
        box.AddChild(OverlayUi.MakeLabel("Options", TitleFont));

        // Two columns side by side when the screen is held sideways, one under the other upright
        // (size check, 2026-10-07: as one column it outgrew a wide phone held sideways). Which way
        // is decided each time the screen opens - see Open.
        _columns = new BoxContainer { Vertical = true };
        _columns.AddThemeConstantOverride("separation", 10);
        box.AddChild(_columns);
        VBoxContainer colA = new VBoxContainer();
        colA.AddThemeConstantOverride("separation", 10);
        _columns.AddChild(colA);
        VBoxContainer colB = new VBoxContainer();
        colB.AddThemeConstantOverride("separation", 10);
        _columns.AddChild(colB);

        // --- Sound: icon + notched slider per channel, on a card styled like the menu buttons.
        colA.AddChild(SectionLabel("Sound"));
        PanelContainer plate = new PanelContainer();
        StyleBoxFlat card = new StyleBoxFlat
        {
            BgColor = OverlayUi.Surface,
            BorderColor = OverlayUi.SurfaceEdge,
            ShadowColor = OverlayUi.Shadow,
            ShadowSize = 8,
            ShadowOffset = new Vector2(0, 3),
            AntiAliasingSize = 1.2f,
        };
        card.SetCornerRadiusAll(20);
        card.SetBorderWidthAll(1);
        card.BorderWidthBottom = 5;
        card.ContentMarginLeft = card.ContentMarginRight = 16;
        card.ContentMarginTop = card.ContentMarginBottom = 10;
        plate.AddThemeStyleboxOverride("panel", card);
        colA.AddChild(plate);

        VBoxContainer rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);
        plate.AddChild(rows);
        rows.AddChild(VolumeRow(GameSettings.Channel.Master));
        rows.AddChild(VolumeRow(GameSettings.Channel.Music));
        rows.AddChild(VolumeRow(GameSettings.Channel.Sfx));
        colA.AddChild(OverlayUi.MakeLabel("Tap an icon to mute it.", NoteFont, OverlayUi.Muted));

        // --- Purchases (monetization-spec.md §4). Only where there is a store to talk to: no ads
        // on this platform means nothing to remove, and the stub store only exists in debug builds.
        VBoxContainer store = new VBoxContainer();
        store.AddThemeConstantOverride("separation", 10);
        colA.AddChild(store);
        _storeSection = store;
        store.AddChild(SectionLabel("Purchases"));
        _storeRows = new VBoxContainer();
        _storeRows.AddThemeConstantOverride("separation", 8);
        store.AddChild(_storeRows);

        // --- Privacy (GDPR / US state laws). Players who were shown the consent form must be able
        // to change their answer later; the SDK says who they are.
        VBoxContainer privacy = new VBoxContainer();
        privacy.AddThemeConstantOverride("separation", 10);
        colA.AddChild(privacy);
        _privacySection = privacy;
        privacy.AddChild(SectionLabel("Privacy"));
        privacy.AddChild(StoreButton("Privacy Choices", () => AdService.ShowPrivacyOptions(null)));

        // --- Graphics. The 3D table (prototype); off is the flat table, and the lighter one.
        colB.AddChild(SectionLabel("Graphics"));
        _table3D = Toggle("3D table", GameSettings.SetTable3D);
        colB.AddChild(_table3D);
        _showFps = Toggle("Show frame rate", GameSettings.SetShowFps);
        colB.AddChild(_showFps);

        // --- Battery. Little to save today; the switches are here for when the art is heavier.
        colB.AddChild(SectionLabel("Battery"));
        _animations = Toggle("Card animations", GameSettings.SetCardAnimations);
        colB.AddChild(_animations);
        _batterySaver = Toggle("Battery saver (30 fps)", GameSettings.SetBatterySaver);
        colB.AddChild(_batterySaver);
        _highFrameRate = Toggle("High frame rate (up to 120 fps)", GameSettings.SetHighFrameRate);
        colB.AddChild(_highFrameRate);

        // --- Cloud Save (2026-10-09). Only where it can work: an Android build with Play Games set
        // up (CloudSave.Available). Off until the player turns it on.
        _cloudSection = new VBoxContainer();
        _cloudSection.AddThemeConstantOverride("separation", 6);
        _cloudSection.AddChild(SectionLabel("Cloud Save"));
        _cloudToggle = Toggle("Save progress to Google Play Games", on =>
        {
            DebugLog.Write("Options", $"Cloud Save switch -> {on}");
            if (CloudSave.Instance == null) return;
            if (on) CloudSave.Instance.Enable(); else CloudSave.Instance.Disable();
            DebugLog.Write("Options", "cloud switch handled");
            RefreshCloud();
            DebugLog.Write("Options", "cloud note refreshed");
        });
        _cloudSection.AddChild(_cloudToggle);
        _cloudNote = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(260, 0) };
        _cloudNote.AddThemeFontSizeOverride("font_size", OverlayUi.Readable(14));
        _cloudNote.AddThemeColorOverride("font_color", OverlayUi.Muted);
        _cloudSection.AddChild(_cloudNote);
        colB.AddChild(_cloudSection);

        // --- Online. Only once an online account exists (Google Play requires in-app deletion).
        _onlineSection = new VBoxContainer();
        _onlineSection.AddChild(SectionLabel("Online"));
        _deleteAccount = new Button { Text = "Delete Online Account" };
        _deleteAccount.AddThemeFontSizeOverride("font_size", ButtonFont);
        _deleteAccount.Pressed += OnDeleteAccountPressed;
        _onlineSection.AddChild(_deleteAccount);
        _onlineNote = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(260, 0) };
        _onlineNote.AddThemeFontSizeOverride("font_size", OverlayUi.Readable(14));
        _onlineNote.AddThemeColorOverride("font_color", OverlayUi.Muted);
        _onlineSection.AddChild(_onlineNote);
        colB.AddChild(_onlineSection);

        // --- Debug. An exported build has no debug rows to show, so it gets no switch either.
        if (OS.IsDebugBuild())
        {
            colB.AddChild(SectionLabel("Debug"));
            _debugButtons = Toggle("Show debug buttons", GameSettings.SetShowDebugButtons);
            colB.AddChild(_debugButtons);

            // Test builds keep a log file (DebugLog); this hands it over without a cable.
            Button copyLog = new Button { Text = "Copy debug log", CustomMinimumSize = WideButton };
            copyLog.AddThemeFontSizeOverride("font_size", ButtonFont);
            copyLog.Pressed += () =>
            {
                DebugLog.Write("Options", "Copy debug log pressed");
                bool ok = DebugLog.CopyToClipboard();
                DebugLog.Write("Options", $"copied to clipboard: {ok}");
                copyLog.Text = ok ? "Copied - paste it in a message" : "No log yet";
                DebugLog.Write("Options", "button text changed");
            };
            colB.AddChild(copyLog);
        }

        _closeButton = new Button { Text = "Close" };
        _closeButton.AddThemeFontSizeOverride("font_size", ButtonFont);
        _closeButton.Pressed += Close;
        CenterContainer closeRow = new CenterContainer();
        closeRow.AddChild(_closeButton);
        box.AddChild(closeRow);
    }

    public void Open(Action onClosed = null)
    {
        Build();
        _onClosed = onClosed;

        _animations.SetPressedNoSignal(GameSettings.CardAnimations);
        _batterySaver.SetPressedNoSignal(GameSettings.BatterySaver);
        _highFrameRate.SetPressedNoSignal(GameSettings.HighFrameRate);
        _deleteArmed = false;
        _deleteAccount.Text = "Delete Online Account";
        _deleteAccount.Disabled = false;
        _onlineNote.Text = "Deletes your name, friends and leaderboard entry from the server. Your progress on this device stays.";
        _onlineSection.Visible = OnlineService.Instance?.HasAccount ?? false;
        _table3D.SetPressedNoSignal(GameSettings.Table3D);
        _showFps.SetPressedNoSignal(GameSettings.ShowFps);
        _debugButtons?.SetPressedNoSignal(GameSettings.ShowDebugButtons);
        Vector2 view = OverlayUi.ViewSize(this);
        if (_columns != null) _columns.Vertical = view.X <= view.Y * 1.25f;
        if (_closeButton != null) _closeButton.CustomMinimumSize = WideButton;
        FillStore(); // sized here too, because WideButton follows the viewport
        if (_privacySection != null) _privacySection.Visible = AdService.PrivacyOptionsRequired;
        RefreshCloud();
        if (CloudSave.Instance != null)
        {
            CloudSave.Instance.Changed -= RefreshCloud;
            CloudSave.Instance.Changed += RefreshCloud;
        }

        Node parent = GetParent();
        if (parent != null) OverlayUi.BringToFront(this);
        Visible = true;
    }

    private void Close()
    {
        if (CloudSave.Instance != null) CloudSave.Instance.Changed -= RefreshCloud;
        Visible = false;
        Action done = _onClosed;
        _onClosed = null;
        done?.Invoke();
    }

    /// The Cloud Save switch and the line under it: what it does when off, how it is doing when on.
    private void RefreshCloud()
    {
        DebugLog.Count("Options.RefreshCloud");
        if (_cloudSection == null || !GodotObject.IsInstanceValid(_cloudSection)) return;
        _cloudSection.Visible = CloudSave.Available && CloudSave.Instance != null;
        if (!_cloudSection.Visible) return;

        _cloudToggle.SetPressedNoSignal(GameSettings.CloudSave);
        string status = CloudSave.Instance.StatusText;
        _cloudNote.Text = !GameSettings.CloudSave
            ? "Keeps your cards, decks, medals and climb in your Google Play Games account, so a new phone picks up where this one left off."
            : string.IsNullOrEmpty(status) ? "On" : status;
    }

    /// Rebuilt every time the screen opens: buying No Ads replaces the button with a thank-you,
    /// and a restore can do the same without anything else on screen changing.
    private void FillStore()
    {
        if (_storeSection == null || _storeRows == null) return;

        _storeSection.Visible = PurchaseService.StoreAvailable;
        if (!_storeSection.Visible) return;

        OverlayUi.ClearChildren(_storeRows);

        if (PurchaseService.OwnsNoAds)
        {
            _storeRows.AddChild(OverlayUi.MakeLabel("No Ads - thank you!", BodyFont, OverlayUi.Muted));
        }
        else
        {
            _storeRows.AddChild(StoreButton($"Remove Ads  {PurchaseService.NoAdsPriceLabel}",
                                            () => PurchaseService.BuyNoAds(_ => Redraw())));
        }

        if (PurchaseService.ShowRestoreButton)
            _storeRows.AddChild(StoreButton("Restore Purchases",
                                            () => PurchaseService.RestorePurchases(_ => Redraw())));
    }

    /// FillStore, but safe to be called back into from the store.
    ///
    /// A real purchase is asynchronous: Play's sheet can sit on top of the game for a minute, and
    /// the player can close this screen - or the whole table can be restarted - before the answer
    /// arrives. Rebuilding rows on a freed node is a hard crash, so the callback checks it is
    /// still here first. It is not enough to be alive: a screen that has since been closed should
    /// not silently rebuild itself either, which Visible covers.
    private void Redraw()
    {
        if (!GodotObject.IsInstanceValid(this) || !Visible) return;
        FillStore();
    }

    private Button StoreButton(string text, Action onPressed)
    {
        Button button = new Button { Text = text, CustomMinimumSize = WideButton };
        button.AddThemeFontSizeOverride("font_size", ButtonFont);
        button.Pressed += onPressed;
        return button;
    }

    private static Label SectionLabel(string text)
    {
        Label label = OverlayUi.MakeLabel(text, SectionFont, OverlayUi.MedalGold);
        label.HorizontalAlignment = HorizontalAlignment.Left;
        return label;
    }

    private static Control VolumeRow(GameSettings.Channel channel)
    {
        HBoxContainer row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(new SoundIconButton { Channel = channel });
        VolumeSlider slider = new VolumeSlider { Channel = channel };
        slider.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(slider);
        return row;
    }

    private static CheckButton Toggle(string text, Action<bool> onToggled)
    {
        CheckButton toggle = new CheckButton { Text = text, FocusMode = FocusModeEnum.None };
        toggle.AddThemeFontSizeOverride("font_size", BodyFont);
        toggle.Toggled += on => onToggled(on);
        return toggle;
    }
}
