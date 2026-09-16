using Godot;
using System;

/// <summary>
/// The icon beside a volume slider, and its mute button: tap to silence that channel, tap again
/// to bring it back at the level it had. A muted icon is dimmed and struck through.
///
/// Master is an orange speaker, Music a pair of notes, Sound effects a speaker cone seen from the
/// front - the three the RuneScape panel uses. Drawn in code until there is art.
/// </summary>
public partial class SoundIconButton : Control
{
    public GameSettings.Channel Channel { get; set; }

    private static readonly Color Orange = new Color(0.90f, 0.47f, 0.10f);
    private static readonly Color OrangeDark = new Color(0.45f, 0.22f, 0.04f);
    private static readonly Color Bone = new Color(0.86f, 0.83f, 0.76f);
    private static readonly Color Cone1 = new Color(0.55f, 0.51f, 0.44f);
    private static readonly Color Cone2 = new Color(0.36f, 0.33f, 0.28f);
    private static readonly Color Cone3 = new Color(0.20f, 0.19f, 0.16f);
    private static readonly Color Strike = new Color(0.90f, 0.18f, 0.15f);

    public SoundIconButton()
    {
        CustomMinimumSize = new Vector2(42, 36);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        FocusMode = FocusModeEnum.None;
    }

    public override void _EnterTree() => GameSettings.Changed += OnSettingsChanged;
    public override void _ExitTree() => GameSettings.Changed -= OnSettingsChanged;
    private void OnSettingsChanged() => QueueRedraw();

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left && button.Pressed)
        {
            GameSettings.ToggleMute(Channel);
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        bool muted = GameSettings.IsMuted(Channel);
        float s = Mathf.Min(Size.X, Size.Y);
        Vector2 o = (Size - new Vector2(s, s)) / 2f;   // square drawing area, centred
        float a = muted ? 0.45f : 1f;

        switch (Channel)
        {
            case GameSettings.Channel.Master: DrawSpeaker(o, s, a); break;
            case GameSettings.Channel.Music: DrawNotes(o, s, a); break;
            default: DrawCone(o, s, a); break;
        }

        if (muted)
            DrawLine(o + new Vector2(s * 0.12f, s * 0.88f), o + new Vector2(s * 0.88f, s * 0.12f), Strike, Mathf.Max(2f, s * 0.08f));
    }

    private static Color A(Color c, float a) => new Color(c.R, c.G, c.B, c.A * a);

    private void DrawSpeaker(Vector2 o, float s, float a)
    {
        Vector2 P(float x, float y) => o + new Vector2(x * s, y * s);

        DrawRect(new Rect2(P(0.10f, 0.38f), new Vector2(0.16f * s, 0.24f * s)), A(Orange, a));
        Vector2[] cone = { P(0.26f, 0.38f), P(0.48f, 0.18f), P(0.48f, 0.82f), P(0.26f, 0.62f) };
        DrawColoredPolygon(cone, A(Orange, a));
        DrawPolyline(new[] { cone[0], cone[1], cone[2], cone[3], cone[0] }, A(OrangeDark, a), 1.5f);

        float w = Mathf.Max(1.5f, s * 0.06f);
        DrawArc(P(0.48f, 0.5f), 0.16f * s, -0.9f, 0.9f, 12, A(Orange, a), w);
        DrawArc(P(0.48f, 0.5f), 0.30f * s, -0.9f, 0.9f, 16, A(Orange, a), w);
    }

    private void DrawNotes(Vector2 o, float s, float a)
    {
        Vector2 P(float x, float y) => o + new Vector2(x * s, y * s);
        Color c = A(Bone, a);
        float w = Mathf.Max(1.5f, s * 0.06f);

        DrawCircle(P(0.28f, 0.74f), 0.10f * s, c);
        DrawCircle(P(0.68f, 0.66f), 0.10f * s, c);
        DrawLine(P(0.37f, 0.74f), P(0.37f, 0.22f), c, w);
        DrawLine(P(0.77f, 0.66f), P(0.77f, 0.14f), c, w);
        DrawColoredPolygon(new[] { P(0.36f, 0.22f), P(0.78f, 0.12f), P(0.78f, 0.24f), P(0.36f, 0.34f) }, c);
    }

    private void DrawCone(Vector2 o, float s, float a)
    {
        Vector2 c = o + new Vector2(s * 0.5f, s * 0.5f);
        DrawCircle(c, 0.40f * s, A(Cone3, a));
        DrawCircle(c, 0.34f * s, A(Cone1, a));
        DrawCircle(c, 0.24f * s, A(Cone2, a));
        DrawCircle(c, 0.12f * s, A(Cone3, a));
        DrawArc(c, 0.34f * s, 3.6f, 5.2f, 10, A(Bone, a * 0.6f), Mathf.Max(1f, s * 0.03f));
    }
}
