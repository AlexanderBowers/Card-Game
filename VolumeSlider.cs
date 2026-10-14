using Godot;
using System;

/// <summary>
/// A notched volume slider: a pale rounded track, filled blue up to the level, with five notches
/// and a white knob that snaps to the nearest one. Tap anywhere on the track or drag along it.
/// (The layout - notches, icon-to-mute - came from the old RuneScape options panel.)
///
/// Drawn in code - there is no art for it yet, and a drawn control scales cleanly with the
/// table's UI zoom. Swapping in textures later only changes _Draw.
/// </summary>
public partial class VolumeSlider : Control
{
    public GameSettings.Channel Channel { get; set; }

    // Pale track, blue fill up to the level, white knob: the same palette as the rest of the menus
    // (OverlayUi). Was a dark RuneScape stone rail with a green gem until 2026-10-10.
    private static readonly Color Track = new Color(0.86f, 0.89f, 0.93f);
    private static readonly Color TrackEdge = OverlayUi.SurfaceEdge;
    private static readonly Color Dot = new Color(0.62f, 0.67f, 0.75f);
    private static readonly Color MutedFill = new Color(0.66f, 0.7f, 0.76f);

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
        float t = Thickness * 0.62f;
        float r = t / 2f;
        float x0 = RailLeft + r, x1 = RailRight - r;
        bool muted = GameSettings.IsMuted(Channel);
        float knobX = NotchX(GameSettings.GetLevel(Channel));
        Color fill = muted ? MutedFill : OverlayUi.Accent;

        // The track: a pill, with the part up to the knob filled in.
        Pill(x0, x1, cy, r + 1f, TrackEdge);
        Pill(x0, x1, cy, r, Track);
        Pill(x0, knobX, cy, r, fill);

        for (int i = 0; i <= GameSettings.VolumeSteps; i++)
        {
            float x = NotchX(i);
            DrawCircle(new Vector2(x, cy), Mathf.Max(1.5f, t * 0.16f), x <= knobX ? new Color(1f, 1f, 1f, 0.8f) : Dot);
        }

        // The knob: white, ringed in the fill colour, with a soft shadow under it.
        float k = Size.Y * 0.34f;
        Vector2 c = new Vector2(knobX, cy);
        DrawCircle(c + new Vector2(0, 2f), k, new Color(0f, 0.04f, 0.1f, 0.18f));
        DrawCircle(c, k, muted ? MutedFill : OverlayUi.AccentDeep);
        DrawCircle(c, k - 2f, Colors.White);
    }

    private void Pill(float xa, float xb, float cy, float r, Color color)
    {
        if (xb < xa) return;
        DrawRect(new Rect2(xa, cy - r, xb - xa, r * 2f), color);
        DrawCircle(new Vector2(xa, cy), r, color);
        DrawCircle(new Vector2(xb, cy), r, color);
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
