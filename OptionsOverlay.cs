using Godot;
using System;

/// <summary>
/// Options: sound, battery, and (debug builds only) the debug-button switch. Reached from the
/// table's Menu and from the start menu - never from the board itself.
///
/// Every control writes GameSettings immediately; there is no Apply button to forget.
/// </summary>
public partial class OptionsOverlay : Control
{
    private static readonly Color PanelStone = new Color(0.16f, 0.15f, 0.14f, 0.98f);
    private static readonly Color PanelEdge = new Color(0.05f, 0.05f, 0.05f);

    private CheckButton _animations;
    private CheckButton _batterySaver;
    private CheckButton _debugButtons;
    private CheckButton _stackedBoard;
    private Action _onClosed;
    private bool _built;

    public void Build()
    {
        if (_built) return;
        _built = true;
        GameSettings.EnsureLoaded();

        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        OverlayUi.AddDim(this);

        VBoxContainer box = OverlayUi.AddPanel(this, contentMargin: 22, separation: 8);
        box.AddChild(OverlayUi.MakeLabel("Options", 30));

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
        box.AddChild(OverlayUi.MakeLabel("Tap an icon to mute it.", 13, OverlayUi.Muted));

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
            _stackedBoard = Toggle("3x3 grid board (portrait)", GameSettings.SetGridBoardPortrait);
            box.AddChild(_stackedBoard);
        }

        Button close = new Button { Text = "Close", CustomMinimumSize = new Vector2(220, 44) };
        close.Pressed += Close;
        CenterContainer closeRow = new CenterContainer();
        closeRow.AddChild(close);
        box.AddChild(closeRow);
    }

    public void Open(Action onClosed = null)
    {
        Build();
        _onClosed = onClosed;

        _animations.SetPressedNoSignal(GameSettings.CardAnimations);
        _batterySaver.SetPressedNoSignal(GameSettings.BatterySaver);
        _debugButtons?.SetPressedNoSignal(GameSettings.ShowDebugButtons);
        _stackedBoard?.SetPressedNoSignal(GameSettings.GridBoardPortrait);

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

    private static Label SectionLabel(string text)
    {
        Label label = OverlayUi.MakeLabel(text, 18, OverlayUi.MedalGold);
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
        toggle.Toggled += on => onToggled(on);
        return toggle;
    }
}
