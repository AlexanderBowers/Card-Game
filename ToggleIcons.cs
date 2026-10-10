using Godot;
using System.Globalization;

/// The on/off switch drawn on every CheckButton (2026-10-10). Godot's own switch is made for dark
/// themes: on our pale panels "on" was a faint white pill that read almost the same as "off".
/// This one is a blue track with the knob on the right when on, a grey track with the knob on the
/// left when off - the phone-settings switch everyone already knows.
///
/// Drawn from SVG at startup and set on the project theme, so every CheckButton picks it up (the
/// Options switches, Mirror for Player 2, the Collection reward). Rendered at twice the size it
/// is shown at, so it stays crisp when the UI is scaled up on a big phone.
public static class ToggleIcons
{
    private const int W = 64, H = 34;
    private const float Oversample = 2f;

    private static readonly Color TrackOff = new Color(0.79f, 0.83f, 0.89f);
    private static readonly Color TrackOffEdge = new Color(0.66f, 0.71f, 0.79f);

    public static void Apply()
    {
        Theme theme = ThemeDB.GetProjectTheme();
        if (theme == null) return;

        Texture2D on = Make(true, 1f), off = Make(false, 1f);
        Texture2D onDisabled = Make(true, 0.45f), offDisabled = Make(false, 0.45f);
        foreach (string suffix in new[] { "", "_mirrored" })
        {
            theme.SetIcon("checked" + suffix, "CheckButton", on);
            theme.SetIcon("unchecked" + suffix, "CheckButton", off);
            theme.SetIcon("checked_disabled" + suffix, "CheckButton", onDisabled);
            theme.SetIcon("unchecked_disabled" + suffix, "CheckButton", offDisabled);
        }
    }

    private static Texture2D Make(bool on, float opacity)
    {
        string track = Hex(on ? OverlayUi.Accent : TrackOff);
        string edge = Hex(on ? OverlayUi.AccentDeep : TrackOffEdge);
        string knobEdge = Hex(on ? OverlayUi.AccentDeep : TrackOffEdge);
        float knobX = on ? W - H / 2f : H / 2f;
        string o = opacity.ToString(CultureInfo.InvariantCulture);
        string svg =
            $"<svg xmlns='http://www.w3.org/2000/svg' width='{W}' height='{H}' viewBox='0 0 {W} {H}'>" +
            $"<g opacity='{o}'>" +
            $"<rect x='1' y='1' width='{W - 2}' height='{H - 2}' rx='{(H - 2) / 2f}' fill='{track}' stroke='{edge}' stroke-width='2'/>" +
            $"<circle cx='{F(knobX)}' cy='{F(H / 2f + 1.5f)}' r='12' fill='#000000' fill-opacity='0.18'/>" +
            $"<circle cx='{F(knobX)}' cy='{F(H / 2f)}' r='12' fill='#ffffff' stroke='{knobEdge}' stroke-width='1.5'/>" +
            "</g></svg>";

        Image image = new Image();
        if (image.LoadSvgFromString(svg, Oversample) != Error.Ok) return null;
        ImageTexture texture = ImageTexture.CreateFromImage(image);
        texture.SetSizeOverride(new Vector2I(W, H));
        return texture;
    }

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Hex(Color c) => "#" + c.ToHtml(false);
}
