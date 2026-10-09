using Godot;

/// The banner at the start of every ladder match (Alexander, 2026-09-30): "Stage 2 / Target: 20"
/// slides in from the left, stays a second, then slides off to the right. It replaces the stage
/// line that used to sit in the middle of the table, which now shows only the target.
///
/// Purely a picture: it ignores input, frees itself, and the match is already live underneath.
public static class StageIntro
{
    private const float SlideIn = 0.45f;
    private const float Hold = 1.0f;
    private const float SlideOut = 0.4f;

    /// Banners on screen right now (GameManager holds coach marks back while one crosses).
    public static int Playing { get; private set; }

    /// `finished` runs once the banner has gone (or at once, if it could not be shown).
    public static void Play(Node root, string title, string subtitle, System.Action finished = null)
    {
        if (root == null || !root.IsInsideTree())
        {
            finished?.Invoke();
            return;
        }

        Control layer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddChild(layer);
        Playing++;
        layer.TreeExiting += () => Playing = System.Math.Max(0, Playing - 1); // freed, or the scene left
        layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        PanelContainer panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        OverlayUi.StylePanel(panel, contentMargin: 28);
        layer.AddChild(panel);

        VBoxContainer box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 4);
        panel.AddChild(box);
        box.AddChild(OverlayUi.MakeLabel(title, 64));

        // The panel is never wider than the screen: the rules lines wrap inside it (2026-10-09 - the
        // stage 50 banner names a target, three specials and four rules, and ran off a phone).
        Vector2 view = layer.GetViewport().GetVisibleRect().Size;
        float width = Mathf.Min(view.X * 0.9f, 560f) - 2 * 28f;
        float hold = Hold;
        if (!string.IsNullOrEmpty(subtitle))
        {
            // The first line is the target; any line after it (the opponent's specials, and the
            // rules of the stages above 10) is smaller, so a full list still fits a phone.
            string[] lines = subtitle.Split('\n', 2);
            box.AddChild(OverlayUi.MakeLabel(lines[0], 36));
            if (lines.Length > 1)
            {
                Label rules = OverlayUi.MakeLabel(lines[1], 24, OverlayUi.Muted);
                rules.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                rules.CustomMinimumSize = new Vector2(width, 0);
                box.AddChild(rules);
                // More to read, a little longer to read it.
                hold += 0.5f * lines[1].Split('\n').Length;
            }
        }

        Vector2 size = panel.GetCombinedMinimumSize();
        size.X = Mathf.Max(size.X, Mathf.Min(view.X * 0.8f, 520f));
        panel.Size = size;

        float y = (view.Y - size.Y) * 0.5f;
        float centreX = (view.X - size.X) * 0.5f;
        panel.Position = new Vector2(-size.X - 40f, y);

        Tween tween = panel.CreateTween();
        tween.TweenProperty(panel, "position:x", centreX, SlideIn)
             .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tween.TweenInterval(hold);
        tween.TweenProperty(panel, "position:x", view.X + 40f, SlideOut)
             .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        tween.TweenCallback(Callable.From(() =>
        {
            layer.QueueFree();
            finished?.Invoke();
        }));
    }
}
