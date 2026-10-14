using Godot;
using System;

/// <summary>
/// The icon beside a volume slider, and its mute button: tap to silence that channel, tap again
/// to bring it back at the level it had. A muted icon is dimmed and struck through.
///
/// Master is a speaker, Music a pair of notes, Sound effects a speaker cone seen from the
/// front - the three the RuneScape panel uses. Drawn in code until there is art.
/// </summary>
public partial class SoundIconButton : Control
{
    public GameSettings.Channel Channel { get; set; }

    // The menus' palette (OverlayUi): blue speaker, ink notes, slate cone. Was orange and stone
    // for the old dark panel until 2026-10-10.
    private static readonly Color Speaker = OverlayUi.Accent;
    private static readonly Color SpeakerEdge = OverlayUi.AccentDeep;
    private static readonly Color NoteInk = OverlayUi.Ink;
    private static readonly Color Cone1 = new Color(0.62f, 0.67f, 0.75f);
    private static readonly Color Cone2 = OverlayUi.Muted;
    private static readonly Color Cone3 = OverlayUi.Ink;
    private static readonly Color Strike = OverlayUi.Warning;

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

        DrawRect(new Rect2(P(0.10f, 0.38f), new Vector2(0.16f * s, 0.24f * s)), A(Speaker, a));
        Vector2[] cone = { P(0.26f, 0.38f), P(0.48f, 0.18f), P(0.48f, 0.82f), P(0.26f, 0.62f) };
        DrawColoredPolygon(cone, A(Speaker, a));
        DrawPolyline(new[] { cone[0], cone[1], cone[2], cone[3], cone[0] }, A(SpeakerEdge, a), 1.5f);

        float w = Mathf.Max(1.5f, s * 0.06f);
        DrawArc(P(0.48f, 0.5f), 0.16f * s, -0.9f, 0.9f, 12, A(Speaker, a), w);
        DrawArc(P(0.48f, 0.5f), 0.30f * s, -0.9f, 0.9f, 16, A(Speaker, a), w);
    }

    private void DrawNotes(Vector2 o, float s, float a)
    {
        Vector2 P(float x, float y) => o + new Vector2(x * s, y * s);
        Color c = A(NoteInk, a);
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
        DrawArc(c, 0.34f * s, 3.6f, 5.2f, 10, A(Colors.White, a * 0.7f), Mathf.Max(1f, s * 0.03f));
    }
}
