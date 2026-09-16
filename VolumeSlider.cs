using Godot;
using System;

/// <summary>
/// A notched volume slider in the style of the old RuneScape options panel: a dark stone rail
/// with arrow-shaped ends, five dots, and a green gem that sits on one of them. Tap anywhere on
/// the rail or drag along it; the gem snaps to the nearest notch.
///
/// Drawn in code - there is no art for it yet, and a drawn control scales cleanly with the
/// table's UI zoom. Swapping in textures later only changes _Draw.
/// </summary>
public partial class VolumeSlider : Control
{
    public GameSettings.Channel Channel { get; set; }

    private static readonly Color RailFill = new Color(0.11f, 0.11f, 0.105f);
    private static readonly Color RailEdge = new Color(0.03f, 0.03f, 0.03f);
    private static readonly Color RailLight = new Color(0.33f, 0.33f, 0.30f);
    private static readonly Color Groove = new Color(0.19f, 0.19f, 0.18f);
    private static readonly Color Dot = new Color(0.40f, 0.40f, 0.37f);
    private static readonly Color GemFrame = new Color(0.78f, 0.78f, 0.76f);
    private static readonly Color GemDark = new Color(0.04f, 0.30f, 0.07f);
    private static readonly Color Gem = new Color(0.12f, 0.70f, 0.18f);
    private static readonly Color GemShine = new Color(0.50f, 0.93f, 0.52f);

    public VolumeSlider()
    {
        CustomMinimumSize = new Vector2(220, 36);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        FocusMode = FocusModeEnum.None;
    }

    public override void _EnterTree() => GameSettings.Changed += OnSettingsChanged;
    public override void _ExitTree() => GameSettings.Changed -= OnSettingsChanged;
    private void OnSettingsChanged() => QueueRedraw();

    // Geometry, shared by drawing and hit-testing so the notch you see is the notch you hit.
    private float Thickness => Size.Y * 0.46f;
    private float RailLeft => Size.Y * 0.2f;
    private float RailRight => Size.X - Size.Y * 0.2f;
    private float NotchLeft => RailLeft + Thickness * 1.1f;
    private float NotchRight => RailRight - Thickness * 1.1f;

    private float NotchX(int level) =>
        Mathf.Lerp(NotchLeft, NotchRight, level / (float)GameSettings.VolumeSteps);

    public override void _Draw()
    {
        float cy = Size.Y / 2f;
        float t = Thickness;
        float x0 = RailLeft, x1 = RailRight;

        // The rail: a long hexagon, pointed at both ends.
        Vector2[] rail =
        {
            new Vector2(x0, cy), new Vector2(x0 + t / 2f, cy - t / 2f),
            new Vector2(x1 - t / 2f, cy - t / 2f), new Vector2(x1, cy),
            new Vector2(x1 - t / 2f, cy + t / 2f), new Vector2(x0 + t / 2f, cy + t / 2f),
        };
        DrawColoredPolygon(rail, RailFill);
        Vector2[] outline = new Vector2[rail.Length + 1];
        rail.CopyTo(outline, 0);
        outline[rail.Length] = rail[0];
        DrawPolyline(outline, RailEdge, 2f);
        // Bevel: light along the top edge, like carved stone catching the light.
        DrawLine(new Vector2(x0 + t / 2f + 1, cy - t / 2f + 2), new Vector2(x1 - t / 2f - 1, cy - t / 2f + 2), RailLight, 1f);

        DrawLine(new Vector2(NotchLeft, cy), new Vector2(NotchRight, cy), Groove, 2f);
        for (int i = 0; i <= GameSettings.VolumeSteps; i++)
            DrawCircle(new Vector2(NotchX(i), cy), Mathf.Max(1.5f, t * 0.12f), Dot);

        // The gem. Grey when the channel is muted, so the slider says so too.
        bool muted = GameSettings.IsMuted(Channel);
        float k = Size.Y * 0.62f;
        Vector2 c = new Vector2(NotchX(GameSettings.GetLevel(Channel)), cy);
        Rect2 frame = new Rect2(c - new Vector2(k, k) / 2f, new Vector2(k, k));
        DrawRect(frame, GemFrame);
        Rect2 inner = frame.Grow(-2f);
        DrawRect(inner, muted ? new Color(0.2f, 0.2f, 0.2f) : GemDark);
        Rect2 body = inner.Grow(-1.5f);
        DrawRect(body, muted ? new Color(0.42f, 0.42f, 0.42f) : Gem);
        DrawRect(new Rect2(body.Position, body.Size * 0.4f), muted ? new Color(0.62f, 0.62f, 0.62f) : GemShine);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left && button.Pressed)
        {
            SetFromX(button.Position.X);
            AcceptEvent();
        }
        else if (@event is InputEventMouseMotion motion && (motion.ButtonMask & MouseButtonMask.Left) != 0)
        {
            SetFromX(motion.Position.X);
            AcceptEvent();
        }
    }

    private void SetFromX(float x)
    {
        float span = NotchRight - NotchLeft;
        if (span <= 0f) return;
        int level = Mathf.RoundToInt(Mathf.Clamp((x - NotchLeft) / span, 0f, 1f) * GameSettings.VolumeSteps);
        if (level != GameSettings.GetLevel(Channel)) GameSettings.SetLevel(Channel, level);
        QueueRedraw();
    }
}
