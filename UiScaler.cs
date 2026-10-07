using Godot;

/// The layer every full-screen overlay lives in - menus, Options, the Market, the deck screen,
/// the set-end panel (size check, 2026-10-07: "scale the UI for different sizes").
///
/// The canvas is sized for the TABLE: whichever of its design width or height is tighter decides
/// (TableUi.FitToWindow), so on a near-square screen - a Fold opened, a Flip's cover, a tablet -
/// the canvas comes out twice a phone's width and every overlay, built for a phone, drew at half
/// size in the middle of it. Here the overlays get a space of their own whose short side is about
/// a phone's (720 units), scaled up to the screen: up to 1.5x, so a tablet gets bigger panels and
/// bigger text without turning into a poster. On a phone the scale is 1 and nothing changes.
///
/// Code inside an overlay asks OverlayUi.ViewSize for the size it has, never the viewport.
public partial class UiScaler : Control
{
    public const float PhoneShortSide = 720f;
    public const float MaxScale = 1.5f;

    public float UiScale { get; private set; } = 1f;

    public UiScaler()
    {
        Name = "OverlayLayer";
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _EnterTree()
    {
        GetViewport().SizeChanged += Refit;
        Refit();
    }

    public override void _ExitTree()
    {
        Viewport viewport = GetViewport();
        if (viewport != null) viewport.SizeChanged -= Refit;
    }

    private void Refit()
    {
        Vector2 view = GetViewport().GetVisibleRect().Size;
        if (view.X < 1f || view.Y < 1f) return;
        UiScale = Mathf.Clamp(Mathf.Min(view.X, view.Y) / PhoneShortSide, 1f, MaxScale);
        SetAnchorsPreset(LayoutPreset.TopLeft);
        Position = Vector2.Zero;
        Scale = new Vector2(UiScale, UiScale);
        Size = view / UiScale;
    }

    /// The overlay layer under this root (the scene's GameManager), made on first use.
    public static UiScaler For(Node root)
    {
        foreach (Node child in root.GetChildren())
            if (child is UiScaler existing) return existing;
        UiScaler scaler = new UiScaler();
        root.AddChild(scaler);
        return scaler;
    }
}
