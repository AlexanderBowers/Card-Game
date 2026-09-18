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
    private static readonly Color PanelStone = new Color(0.16f, 0.15f, 0.14f, 0.98f);
    private static readonly Color PanelEdge = new Color(0.05f, 0.05f, 0.05f);

    private CheckButton _animations;
    private CheckButton _batterySaver;
    private CheckButton _debugButtons;
    private Button _closeButton;
    private Control _storeSection;      // the heading and the row, so both hide when there is no store
    private VBoxContainer _storeRows;   // refilled on every Open: what it says depends on what is owned
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
        Mathf.Clamp(GetViewport().GetVisibleRect().Size.X - 88f, 240f, ButtonWidthMax), ButtonHeight);

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

        // --- Sound: the RuneScape-style block, on its own dark stone plate.
        box.AddChild(SectionLabel("Sound"));
        PanelContainer plate = new PanelContainer();
        StyleBoxFlat stone = new StyleBoxFlat { BgColor = PanelStone, BorderColor = PanelEdge };
        stone.SetBorderWidthAll(2);
        stone.SetCornerRadiusAll(4);
        stone.SetContentMarginAll(10);
        plate.AddThemeStyleboxOverride("panel", stone);
        box.AddChild(plate);

        VBoxContainer rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);
        plate.AddChild(rows);
        rows.AddChild(VolumeRow(GameSettings.Channel.Master));
        rows.AddChild(VolumeRow(GameSettings.Channel.Music));
        rows.AddChild(VolumeRow(GameSettings.Channel.Sfx));
        box.AddChild(OverlayUi.MakeLabel("Tap an icon to mute it.", NoteFont, OverlayUi.Muted));

        // --- Purchases (monetization-spec.md §4). Only where there is a store to talk to: no ads
        // on this platform means nothing to remove, and the stub store only exists in debug builds.
        VBoxContainer store = new VBoxContainer();
        store.AddThemeConstantOverride("separation", 10);
        box.AddChild(store);
        _storeSection = store;
        store.AddChild(SectionLabel("Purchases"));
        _storeRows = new VBoxContainer();
        _storeRows.AddThemeConstantOverride("separation", 8);
        store.AddChild(_storeRows);

        // --- Battery. Little to save today; the switches are here for when the art is heavier.
        box.AddChild(SectionLabel("Battery"));
        _animations = Toggle("Card animations", GameSettings.SetCardAnimations);
        box.AddChild(_animations);
        _batterySaver = Toggle("Battery saver (30 fps)", GameSettings.SetBatterySaver);
        box.AddChild(_batterySaver);

        // --- Debug. An exported build has no debug rows to show, so it gets no switch either.
        if (OS.IsDebugBuild())
        {
            box.AddChild(SectionLabel("Debug"));
            _debugButtons = Toggle("Show debug buttons", GameSettings.SetShowDebugButtons);
            box.AddChild(_debugButtons);
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
        _debugButtons?.SetPressedNoSignal(GameSettings.ShowDebugButtons);
        if (_closeButton != null) _closeButton.CustomMinimumSize = WideButton;
        FillStore(); // sized here too, because WideButton follows the viewport

        Node parent = GetParent();
        if (parent != null) parent.MoveChild(this, parent.GetChildCount() - 1);
        Visible = true;
    }

    private void Close()
    {
        Visible = false;
        Action done = _onClosed;
        _onClosed = null;
        done?.Invoke();
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
                                            () => PurchaseService.BuyNoAds(_ => FillStore())));
        }

        if (PurchaseService.ShowRestoreButton)
            _storeRows.AddChild(StoreButton("Restore Purchases",
                                            () => PurchaseService.RestorePurchases(_ => FillStore())));
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
