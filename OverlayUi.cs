using Godot;
using System;

/// <summary>
/// The shared look of the full-screen overlays that sit on top of the table (set end, shop,
/// armory). They are built in code rather than as scenes so the solo and 2-player tables both get
/// them without NodePath wiring, and so the intermission is an overlay over the same table - not a
/// scene change (Alexander's call, 2026-09-06: one grounded venue look throughout).
/// </summary>
public static class OverlayUi
{
    // ------------------------------------------------------------------
    // The look (pass 31, after Pokemon TCG Pocket - Alexander, 2026-09-24).
    //   - Surfaces are pale and frosted, text on them is dark ink; the dark playmat is the only
    //     dark thing on screen, so everything you can touch floats on it.
    //   - One accent, a clear blue, marks the ONE thing the screen wants you to do next.
    //   - Big soft corners, soft drop shadows, no hard outlines. Buttons are raised: a thicker
    //     bottom edge that flattens when pressed.
    // The theme (ui_theme.tres) carries the same numbers for every stock Button.
    // ------------------------------------------------------------------
    public static readonly Color Surface = new Color(0.965f, 0.972f, 0.985f);
    public static readonly Color PanelSurface = new Color(0.93f, 0.945f, 0.97f, 0.97f);
    public static readonly Color SurfaceEdge = new Color(0.76f, 0.8f, 0.87f);
    public static readonly Color Ink = new Color(0.17f, 0.21f, 0.29f);
    public static readonly Color Accent = new Color(0.16f, 0.58f, 0.95f);
    public static readonly Color AccentDeep = new Color(0.1f, 0.42f, 0.78f);
    public static readonly Color AccentGlow = new Color(0.3f, 0.7f, 1f, 0.55f);
    public static readonly Color Shadow = new Color(0.01f, 0.04f, 0.1f, 0.35f);

    /// Inside a frosted panel. The old names are kept so every overlay picks the new look up.
    public static readonly Color PanelBg = PanelSurface;
    public static readonly Color PanelBorder = new Color(1f, 1f, 1f, 0.9f);
    public static readonly Color MedalGold = new Color(0.8f, 0.53f, 0.02f);   // gold INK, for pale panels
    public static readonly Color Muted = new Color(0.42f, 0.47f, 0.56f);
    public static readonly Color Warning = new Color(0.8f, 0.22f, 0.2f);

    /// Dark labels inside panels; the project theme keeps them white on the mat.
    public static readonly Theme PanelTheme = GD.Load<Theme>("res://ui_panel_theme.tres");

    /// The wash between an overlay and the live table: a deep navy rather than black.
    public static readonly Color DimColor = new Color(0.02f, 0.05f, 0.1f, 0.6f);

    public static StyleBoxFlat PanelStyle(int contentMargin = 24)
    {
        StyleBoxFlat style = new StyleBoxFlat
        {
            BgColor = PanelSurface,
            BorderColor = PanelBorder,
            ShadowColor = new Color(0.01f, 0.03f, 0.08f, 0.45f),
            ShadowSize = 18,
            ShadowOffset = new Vector2(0, 6),
            AntiAliasingSize = 1.2f,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(26);
        style.SetContentMarginAll(contentMargin);
        return style;
    }

    /// The coach-mark bubble: white, ringed in the accent and glowing, so an instruction never
    /// looks like just another panel.
    public static StyleBoxFlat BubbleStyle(int contentMargin = 18)
    {
        StyleBoxFlat style = PanelStyle(contentMargin);
        style.BgColor = new Color(1f, 1f, 1f, 0.98f);
        style.BorderColor = Accent;
        style.SetBorderWidthAll(3);
        style.ShadowColor = AccentGlow;
        style.ShadowSize = 14;
        style.ShadowOffset = Vector2.Zero;
        return style;
    }

    /// Frosted panel look + dark-label theme, for panels built outside AddPanel.
    public static void StylePanel(PanelContainer panel, int contentMargin = 24, bool bubble = false)
    {
        panel.AddThemeStyleboxOverride("panel", bubble ? BubbleStyle(contentMargin) : PanelStyle(contentMargin));
        if (PanelTheme != null) panel.Theme = PanelTheme;
    }

    /// A raised pill button in the house style. primary = ringed in the accent and glowing;
    /// ring = a coloured ring for a secondary action that still wants its own colour.
    public static void StyleButton(Button button, bool primary = false, Color? ring = null)
    {
        Color edge = primary ? Accent : (ring ?? SurfaceEdge);
        StyleBoxFlat normal = new StyleBoxFlat
        {
            BgColor = Surface,
            BorderColor = edge,
            ShadowColor = primary ? AccentGlow : Shadow,
            ShadowSize = primary ? 12 : 8,
            ShadowOffset = primary ? Vector2.Zero : new Vector2(0, 3),
            AntiAliasingSize = 1.2f,
            ContentMarginLeft = 24,
            ContentMarginRight = 24,
            ContentMarginTop = 14,
            ContentMarginBottom = 14,
        };
        normal.SetCornerRadiusAll(20);
        if (primary || ring.HasValue) normal.SetBorderWidthAll(3);
        normal.BorderWidthBottom = 5;

        StyleBoxFlat hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = Colors.White;

        StyleBoxFlat pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(0.87f, 0.9f, 0.94f);
        pressed.BorderWidthBottom = primary || ring.HasValue ? 3 : 1;
        pressed.ShadowSize = 3;
        pressed.ContentMarginTop = 17;
        pressed.ContentMarginBottom = 11;

        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("hover_pressed", pressed);
        Color text = primary ? AccentDeep : Ink;
        button.AddThemeColorOverride("font_color", text);
        button.AddThemeColorOverride("font_hover_color", text);
        button.AddThemeColorOverride("font_focus_color", text);
        button.AddThemeColorOverride("font_pressed_color", AccentDeep);
        button.AddThemeColorOverride("font_hover_pressed_color", AccentDeep);
    }

    /// The dark wash that separates the overlay from the live table underneath.
    public static void AddDim(Control root)
    {
        ColorRect dim = new ColorRect
        {
            Color = DimColor,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        root.AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    /// A centred panel that hugs its content, and the VBox to fill with it.
    public static VBoxContainer AddPanel(Control root, int contentMargin = 24, int separation = 12)
    {
        PanelContainer panel = new PanelContainer();
        StylePanel(panel, contentMargin);
        root.AddChild(panel);

        // Anchored to the centre with zero offsets: a Control grows to its minimum size, and with
        // grow "both" it stays centred, so the panel always hugs its content.
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;

        VBoxContainer box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", separation);
        panel.AddChild(box);
        return box;
    }

    public static Label MakeLabel(string text, int fontSize, Color? color = null)
    {
        Label label = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        if (color.HasValue) label.AddThemeColorOverride("font_color", color.Value);
        return label;
    }

    /// A card you can tap: the card art itself is the button face.
    public static Button CardButton(Control cardView, Vector2 size, Action onPressed)
    {
        Button button = new Button
        {
            Flat = true,
            CustomMinimumSize = size,
            FocusMode = Control.FocusModeEnum.None,
        };
        StyleBoxEmpty empty = new StyleBoxEmpty();
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
            button.AddThemeStyleboxOverride(state, empty);
        if (onPressed != null) button.Pressed += onPressed;

        cardView.MouseFilter = Control.MouseFilterEnum.Ignore;
        button.AddChild(cardView);
        cardView.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return button;
    }

    /// An empty side-deck slot: a card-shaped outline, so twelve of them read as a deck.
    public static Panel EmptySlot(Vector2 size)
    {
        StyleBoxFlat style = new StyleBoxFlat
        {
            BgColor = new Color(0.17f, 0.21f, 0.29f, 0.04f),
            BorderColor = new Color(0.17f, 0.21f, 0.29f, 0.22f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(10);

        Panel slot = new Panel { CustomMinimumSize = size, MouseFilter = Control.MouseFilterEnum.Ignore };
        slot.AddThemeStyleboxOverride("panel", style);
        return slot;
    }

    public static void ClearChildren(Node parent)
    {
        if (parent == null) return;
        foreach (Node child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }
}
