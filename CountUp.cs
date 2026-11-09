using Godot;

/// Score numbers that count to their new value instead of jumping (2026-11-08, from Ian's
/// playtest notes: "animated counters"). A 7 landing on 12 runs 13, 14 ... 19 in a quarter of a
/// second, so the eye follows the change.
///
/// Only whole numbers count; anything else ("-", "34+?") is set at once. A label asked for the
/// same text again keeps counting rather than restarting, because the table refreshes its score
/// labels far more often than the scores change. Card animations off = no counting.
public static class CountUp
{
    private const string WantMeta = "count_up_want";
    private const string TweenMeta = "count_up_tween";
    private const double SecondsPerStep = 0.045;
    private const double MinSeconds = 0.16;
    private const double MaxSeconds = 0.45;

    public static void SetText(Label label, string text, bool animate = true)
    {
        if (label == null) return;
        if (label.HasMeta(WantMeta) && label.GetMeta(WantMeta).AsString() == text) return;
        label.SetMeta(WantMeta, text);

        Tween running = label.HasMeta(TweenMeta) ? label.GetMeta(TweenMeta).As<Tween>() : null;
        if (running != null && running.IsValid()) running.Kill();

        bool counts = animate && GameSettings.CardAnimations && label.IsVisibleInTree()
                      && int.TryParse(text, out int to) && int.TryParse(label.Text, out int from) && from != to
                      && to != 0; // a new set clears the board: 0 arrives at once, not as a countdown
        if (!counts)
        {
            label.Text = text;
            return;
        }

        int start = int.Parse(label.Text);
        int end = int.Parse(text);
        double seconds = Mathf.Clamp(Mathf.Abs(end - start) * SecondsPerStep, MinSeconds, MaxSeconds);
        Tween tween = label.CreateTween();
        tween.TweenMethod(Callable.From<int>(v => label.Text = v.ToString()), start, end, seconds)
             .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenCallback(Callable.From(() => label.Text = text));
        label.SetMeta(TweenMeta, tween);
    }
}
