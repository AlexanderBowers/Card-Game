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

    /// Where an overlay goes: the scene's overlay layer (UiScaler), not the scene root.
    /// Draw layer for everything that must be read before the table: every overlay (the
    /// set-end and match-end panels, menus, Options...) and the tutorial's spotlight. Above
    /// anything the table raises for itself - the effect banner and status toasts sit at 20 - so
    /// a "You won the set!" banner can never cover "You Win!" (S25, 2026-10-10).
    public const int PopupZ = 100;

    public static Control Host(Node root) => UiScaler.For(root);

    /// The size an overlay has to lay itself out in: its overlay layer's, or the viewport's for
    /// anything outside one.
    public static Vector2 ViewSize(Control c)
    {
        for (Node n = c; n != null; n = n.GetParent())
            if (n is UiScaler scaler) return scaler.Size;
        return c.GetViewportRect().Size;
    }

    /// The overlay layer's size under this root.
    public static Vector2 HostSize(Node root) => UiScaler.For(root).Size;

    /// Above every other overlay - and the layer itself above everything else on the scene root
    /// (the tutorial's spotlight, a stray animation card), as moving the overlay last on the
    /// root used to do.
    public static void BringToFront(Control overlay)
    {
        Node parent = overlay.GetParent();
        if (parent == null) return;
        parent.MoveChild(overlay, parent.GetChildCount() - 1);
        if (parent is UiScaler scaler && scaler.GetParent() is Node root)
            root.MoveChild(scaler, root.GetChildCount() - 1);
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
    /// `scroll`: the content sits in a DragScroll, so a panel taller than the screen scrolls
    /// rather than running off it. Off for a panel that has a scrolling list of its own.
    public static VBoxContainer AddPanel(Control root, int contentMargin = 24, int separation = 12, bool scroll = true)
    {
        PanelContainer panel = new PanelContainer();
        StylePanel(panel, contentMargin);
        root.AddChild(panel);

        // Anchored to the centre with zero offsets: a Control grows to its minimum size, and with
        // grow "both" it stays centred, so the panel always hugs its content.
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;

        VBoxContainer box = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        box.AddThemeConstantOverride("separation", separation);

        DragScroll scroller = null;
        if (scroll)
        {
            scroller = new DragScroll();
            panel.AddChild(scroller);
            scroller.AddChild(box);
        }
        else
        {
            panel.AddChild(box);
        }
        KeepOnScreen(panel, scroller, box);
        return box;
    }

    /// Fit and fill (size check, 2026-10-06/07). A centred panel never runs off the screen:
    /// - too TALL: its content scrolls (a DragScroll window as tall as the screen allows), so the
    ///   text keeps the size it was made bigger for. The start menu, Options and the table menu
    ///   all outgrow the 720 units of height a wide phone held sideways leaves them.
    /// - too WIDE, or too tall with nothing to scroll: drawn smaller until it fits - the last
    ///   resort, which a panel built for a phone held upright should never need.
    /// At every size where a panel already fitted, nothing changes.
    public static void KeepOnScreen(Control panel, ScrollContainer scroller = null, Control content = null, float margin = 10f)
    {
        bool fitting = false;
        void Fit()
        {
            if (fitting || !GodotObject.IsInstanceValid(panel) || !panel.IsInsideTree()) return;
            DebugLog.Count("KeepOnScreen.Fit", panel.GetParent()?.GetType().Name);
            fitting = true;
            try { FitNow(); }
            finally { fitting = false; }
        }

        void FitNow()
        {
            Vector2 view = ViewSize(panel);
            (float top, float bottom) = SafeInsets(panel);
            Vector2 room = new Vector2(view.X - 2f * margin, view.Y - 2f * margin - top - bottom);

            if (scroller != null && content != null)
            {
                float chrome = panel.GetThemeStylebox("panel")?.GetMinimumSize().Y ?? 0f;
                float wanted = content.GetCombinedMinimumSize().Y;
                float h = Mathf.Max(80f, Mathf.Min(wanted, room.Y - chrome));
                if (Mathf.Abs(scroller.CustomMinimumSize.Y - h) > 0.5f)
                    scroller.CustomMinimumSize = new Vector2(0f, h);
            }

            // Back to hugging the content: a Control grows to its minimum but never shrinks back on
            // its own, so a panel that was tall upright stayed tall after turning sideways.
            panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
            panel.GrowHorizontal = Control.GrowDirection.Both;
            panel.GrowVertical = Control.GrowDirection.Both;
            // Centred in the SAFE area: a status bar is taller than a gesture bar, and a panel
            // that just fits otherwise tucks its top under the camera.
            float shift = (top - bottom) / 2f;
            panel.OffsetTop += shift;
            panel.OffsetBottom += shift;
            Vector2 size = panel.GetCombinedMinimumSize();
            if (size.X < 1f || size.Y < 1f) return;
            float s = Mathf.Min(1f, Mathf.Min(room.X / size.X, room.Y / size.Y));
            panel.PivotOffset = panel.Size / 2f;
            panel.Scale = new Vector2(s, s);
        }

        if (content != null) content.MinimumSizeChanged += Fit;

        Viewport viewport = null;
        void Attach()
        {
            viewport = panel.GetViewport();
            viewport.SizeChanged += Fit;
            Callable.From(Fit).CallDeferred();
        }

        panel.Resized += Fit;
        panel.TreeEntered += Attach;
        if (panel.IsInsideTree()) Attach(); // usually already added by the caller
        panel.TreeExiting += () =>
        {
            if (viewport != null && GodotObject.IsInstanceValid(viewport)) viewport.SizeChanged -= Fit;
            viewport = null;
        };
    }

    /// Body text on every overlay is drawn this much bigger than the size asked for (playtest,
    /// 2026-10-06: Alexander's mother could not read the Market, the deck screen or the tutorial).
    /// Headings - 28 and up - are already big and keep their size, so the gap between a title and
    /// its text closes rather than everything simply growing.
    public const float BodyTextScale = 1.3f;
    public const int HeadingSize = 28;

    public static int Readable(int fontSize) =>
        fontSize >= HeadingSize ? fontSize : Mathf.RoundToInt(fontSize * BodyTextScale);

    public static Label MakeLabel(string text, int fontSize, Color? color = null)
    {
        Label label = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", Readable(fontSize));
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

    /// The notch / status bar (top) and gesture bar (bottom) as insets in canvas units, so a
    /// full-screen panel can keep its title and its bottom button clear of them. Zero on desktop,
    /// where the "safe area" is the monitor's work area rather than anything about our window.
    /// Pretend notch / gesture-bar insets in WINDOW pixels (top, bottom), for checking layouts on
    /// a desktop, where the real safe area is always the whole window. Null in the game.
    public static Vector2? DebugInsetsPx;

    public static (float Top, float Bottom) SafeInsets(Control anyControl)
    {
        if (anyControl != null && DebugInsetsPx.HasValue)
        {
            Vector2I w = DisplayServer.WindowGetSize();
            float k = w.Y > 0 ? ViewSize(anyControl).Y / w.Y : 1f;
            return (DebugInsetsPx.Value.X * k, DebugInsetsPx.Value.Y * k);
        }
        if (anyControl == null || !OS.HasFeature("mobile")) return (0f, 0f);

        Vector2I window = DisplayServer.WindowGetSize();
        if (window.X <= 0 || window.Y <= 0) return (0f, 0f);
        DebugLog.Count("SafeInsets");
        Rect2I safe = DisplayServer.GetDisplaySafeArea();
        float scale = ViewSize(anyControl).Y / window.Y; // window px -> canvas units

        float top = Mathf.Max(0, safe.Position.Y) * scale;
        float bottom = Mathf.Max(0, window.Y - safe.End.Y) * scale;
        return (top, bottom);
    }

    /// Anchors a panel to all four edges with a margin (smaller on a small phone) and keeps it
    /// clear of the notch and gesture bar. Call again whenever the viewport changes size.
    public static void FillScreen(Control panel, float margin = 24f)
    {
        if (panel == null) return;
        Vector2 view = ViewSize(panel);
        // The canvas is 720 on its short side (stretch "expand"), so a phone shows up as a long
        // aspect rather than a small number: a 19.5:9 phone gets the tighter margin.
        float aspect = Mathf.Max(view.X, view.Y) / Mathf.Max(1f, Mathf.Min(view.X, view.Y));
        float m = aspect > 1.9f ? Mathf.Min(margin, 14f) : margin;
        (float top, float bottom) = SafeInsets(panel);

        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.OffsetLeft = m;
        panel.OffsetRight = -m;
        panel.OffsetTop = m + top;
        panel.OffsetBottom = -(m + bottom);
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
