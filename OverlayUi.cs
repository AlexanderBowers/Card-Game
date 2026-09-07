using Godot;
using System;

/// <summary>
/// The shared look of the full-screen overlays that sit on top of the table (round end, shop,
/// armory). They are built in code rather than as scenes so the solo and 2-player tables both get
/// them without NodePath wiring, and so the intermission is an overlay over the same table - not a
/// scene change (Alexander's call, 2026-09-06: one grounded venue look throughout).
/// </summary>
public static class OverlayUi
{
    public static readonly Color PanelBg = new Color(0.1f, 0.14f, 0.2f, 0.98f);
    public static readonly Color PanelBorder = new Color(0.55f, 0.65f, 0.8f);
    public static readonly Color MedalGold = new Color(1f, 0.85f, 0.35f);
    public static readonly Color Muted = new Color(0.72f, 0.78f, 0.86f);

    /// The dark wash that separates the overlay from the live table underneath.
    public static void AddDim(Control root)
    {
        ColorRect dim = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.6f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        root.AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    /// A centred panel that hugs its content, and the VBox to fill with it.
    public static VBoxContainer AddPanel(Control root, int contentMargin = 24, int separation = 12)
    {
        PanelContainer panel = new PanelContainer();
        StyleBoxFlat style = new StyleBoxFlat { BgColor = PanelBg, BorderColor = PanelBorder };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(12);
        style.SetContentMarginAll(contentMargin);
        panel.AddThemeStyleboxOverride("panel", style);
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
            BgColor = new Color(1f, 1f, 1f, 0.05f),
            BorderColor = new Color(0.55f, 0.65f, 0.8f, 0.45f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);

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
