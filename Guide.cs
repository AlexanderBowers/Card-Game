using Godot;
using System;

/// A spotlight for lessons on a full-screen overlay - the Market and the deck screen (playtest,
/// 2026-10-07). Lives INSIDE the overlay it teaches, as its last child, so it shares the overlay's
/// space and scale and needs no coordinates translated between layers.
///
/// It asks its owner every frame what the current step is (Func&lt;Step&gt;), so the lesson is
/// driven by the screen's own state: a step whose thing has been done simply stops being the
/// step, and a player who undoes it gets the earlier step back. A null step hides the spotlight.
///   - A TELL step dims everything, the hole included, and shows "Got it".
///   - A DO step leaves the hole live: the one control in it is the only thing that can be tapped.
public partial class Guide : Control
{
    public sealed class Step
    {
        public Func<Control> Target;  // what the hole is cut round; null = a plain dim
        public string Text;
        public Action GotIt;          // set = a TELL step with a "Got it" button
    }

    private readonly Func<Step> _current;
    private readonly ColorRect[] _shades = new ColorRect[4];
    private Control _holeBlock;
    private Panel _ring;
    private PanelContainer _caption;
    private Label _label;
    private Button _gotIt;
    private Step _shown;
    private float _time;

    private static readonly Color Shade = new Color(0.02f, 0.05f, 0.1f, 0.62f);
    private const float Pad = 8f;
    private const float Gap = 14f;
    private const float Margin = 16f;

    public Guide() : this(() => null) { } // Godot wants a parameterless constructor

    public Guide(Func<Step> current)
    {
        _current = current;
        Name = "Guide";
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        for (int i = 0; i < 4; i++)
        {
            _shades[i] = new ColorRect { Color = Shade, MouseFilter = MouseFilterEnum.Stop };
            AddChild(_shades[i]);
        }
        _holeBlock = new Control { MouseFilter = MouseFilterEnum.Stop };
        AddChild(_holeBlock);

        StyleBoxFlat ring = new StyleBoxFlat
        {
            DrawCenter = false,
            BorderColor = new Color(0.45f, 0.8f, 1f),
            AntiAliasingSize = 1.2f,
        };
        ring.SetBorderWidthAll(4);
        ring.SetCornerRadiusAll(12);
        _ring = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        _ring.AddThemeStyleboxOverride("panel", ring);
        AddChild(_ring);

        _caption = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        OverlayUi.StylePanel(_caption, 18, bubble: true);
        AddChild(_caption);
        VBoxContainer box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 12);
        _caption.AddChild(box);
        _label = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _label.AddThemeFontSizeOverride("font_size", 28);
        box.AddChild(_label);
        _gotIt = new Button { Text = "Got it", SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        _gotIt.AddThemeFontSizeOverride("font_size", 26);
        _gotIt.Pressed += () => _shown?.GotIt?.Invoke();
        box.AddChild(_gotIt);

        Visible = false;
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        Step step = GetParent() is CanvasItem p && p.IsVisibleInTree() ? _current() : null;
        _shown = step;
        if (step == null)
        {
            Visible = false;
            return;
        }

        // Last child of the overlay, so nothing the overlay rebuilds is drawn over it.
        if (GetIndex() != GetParent().GetChildCount() - 1) GetParent().MoveChild(this, -1);
        Visible = true;

        Vector2 view = Size;
        Control target = step.Target?.Invoke();
        Rect2 hole = target != null && target.IsVisibleInTree() && target.Size.X > 1f
            ? LocalRectOf(target).Grow(Pad)
            : new Rect2(view / 2f, Vector2.Zero);

        float l = Mathf.Clamp(hole.Position.X, 0f, view.X), t = Mathf.Clamp(hole.Position.Y, 0f, view.Y);
        float r = Mathf.Clamp(hole.End.X, 0f, view.X), b = Mathf.Clamp(hole.End.Y, 0f, view.Y);
        Place(_shades[0], 0f, 0f, view.X, t);
        Place(_shades[1], 0f, b, view.X, view.Y - b);
        Place(_shades[2], 0f, t, l, b - t);
        Place(_shades[3], r, t, view.X - r, b - t);
        Place(_holeBlock, l, t, r - l, b - t);
        bool tell = step.GotIt != null;
        _holeBlock.Visible = tell;
        _gotIt.Visible = tell;

        // The ring breathes on a DO step: this is the thing to tap.
        _ring.Visible = hole.Size.X > 1f;
        Place(_ring, l - 3f, t - 3f, r - l + 6f, b - t + 6f);
        _ring.Modulate = new Color(1f, 1f, 1f, tell ? 0.8f : 0.55f + 0.45f * Mathf.Sin(_time * 5f) * Mathf.Sin(_time * 5f));

        // The caption: under the hole or over it, whichever side has more room, centred on it.
        _label.Text = step.Text;
        float width = Mathf.Min(view.X - 2f * Margin, 600f);
        _label.CustomMinimumSize = new Vector2(width - 40f, 0f);
        _label.Size = new Vector2(width - 40f, _label.Size.Y);
        float height = _caption.GetCombinedMinimumSize().Y;
        float x = Mathf.Clamp(hole.GetCenter().X - width / 2f, Margin, Mathf.Max(Margin, view.X - Margin - width));
        float roomAbove = t - Gap - Margin, roomBelow = view.Y - b - Gap - Margin;
        float y = roomBelow >= height || roomBelow >= roomAbove ? b + Gap : t - Gap - height;
        if (hole.Size.X < 1f) y = view.Y / 2f - height / 2f;
        y = Mathf.Clamp(y, Margin, Mathf.Max(Margin, view.Y - Margin - height));
        _caption.Position = new Vector2(x, y);
        _caption.Size = new Vector2(width, height);
    }

    private Rect2 LocalRectOf(Control target)
    {
        Transform2D toLocal = GetGlobalTransform().AffineInverse() * target.GetGlobalTransform();
        Vector2 s = target.Size;
        Vector2 a = toLocal * Vector2.Zero, c = toLocal * s;
        Vector2 bq = toLocal * new Vector2(s.X, 0f), d = toLocal * new Vector2(0f, s.Y);
        Vector2 min = new Vector2(Mathf.Min(Mathf.Min(a.X, bq.X), Mathf.Min(c.X, d.X)), Mathf.Min(Mathf.Min(a.Y, bq.Y), Mathf.Min(c.Y, d.Y)));
        Vector2 max = new Vector2(Mathf.Max(Mathf.Max(a.X, bq.X), Mathf.Max(c.X, d.X)), Mathf.Max(Mathf.Max(a.Y, bq.Y), Mathf.Max(c.Y, d.Y)));
        return new Rect2(min, max - min);
    }

    private static void Place(Control c, float x, float y, float w, float h)
    {
        c.Position = new Vector2(x, y);
        c.Size = new Vector2(Mathf.Max(0f, w), Mathf.Max(0f, h));
    }
}
