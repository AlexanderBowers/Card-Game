using Godot;
using System;
using System.Collections.Generic;

/// The table's big moments (2026-10-05): Hold, Shave, Copy and Trade Hands each get an animation
/// you can't miss, the bot's included.
///
/// The rule all four follow: the effect is resolved in the MODEL first, then the animation catches
/// the picture up - a score box keeps showing the old number until the moment of impact
/// (ScoreDisplay.HoldShown), a hand stays hidden until its cards have flown there. Each one is
/// kept under about a second.
///
/// While any of them is playing:
///  - Busy is true. GameManager will not resolve the turn (so no deal, no set end) and the bot
///    waits; both pick up again from Idle.
///  - A transparent shield over the table takes every touch, so nothing can be pressed half way
///    through - and a tap on it fast-forwards everything to its end state, so a repeat player
///    never has to sit through it.
/// Sounds fire from TweenCallbacks at the moment of impact, never when the tween is built.
/// With Options > Battery's card animations off, each moment jumps straight to its end state.
public sealed class TableMoments
{
    private readonly Node _root;
    private readonly AudioStreamPlayer _sfxSlide;
    private readonly AudioStreamPlayer _sfxImpact;

    private readonly List<Tween> _live = new List<Tween>();
    private readonly List<Action> _cleanup = new List<Action>();
    private ulong _startedMs;
    private Control _shield;

    /// No moment can hang the game: past this, whatever is left is fast-forwarded.
    private const ulong SafetyMs = 4000;

    public TableMoments(Node root, AudioStreamPlayer slide, AudioStreamPlayer impact)
    {
        _root = root;
        _sfxSlide = slide;
        _sfxImpact = impact;
    }

    /// Something is playing; the turn must not be resolved and nobody may act.
    public bool Busy
    {
        get
        {
            Prune();
            if (_live.Count > 0 && Time.GetTicksMsec() - _startedMs > SafetyMs) FinishNow();
            return _live.Count > 0;
        }
    }

    /// Every moment has finished (raised at the end of that frame).
    public event Action Idle;

    // ------------------------------------------------------------------
    // Bookkeeping
    // ------------------------------------------------------------------
    private Tween NewTween()
    {
        Tween tween = _root.GetTree().CreateTween();
        if (_live.Count == 0) _startedMs = Time.GetTicksMsec();
        _live.Add(tween);
        tween.Finished += () => OnTweenDone(tween);
        RaiseShield();
        return tween;
    }

    private void OnTweenDone(Tween tween)
    {
        _live.Remove(tween);
        if (_live.Count == 0) Settle();
    }

    private void Prune()
    {
        int before = _live.Count;
        _live.RemoveAll(t => t == null || !t.IsValid());
        if (before > 0 && _live.Count == 0) Settle();
    }

    /// Everything done: put back anything a moment had hidden or held, drop the shield, tell the game.
    private void Settle()
    {
        List<Action> cleanup = new List<Action>(_cleanup);
        _cleanup.Clear();
        foreach (Action action in cleanup) action();
        LowerShield();
        Callable.From(() =>
        {
            if (_live.Count == 0 && GodotObject.IsInstanceValid(_root) && _root.IsInsideTree()) Idle?.Invoke();
        }).CallDeferred();
    }

    /// Runs every live moment to its end state now - the tap-to-skip, a rotation mid-moment, and
    /// the Battery setting. A moment's last callback may start the next stage of it (Trade Hands'
    /// unfold), so this goes round until nothing is left.
    public void FinishNow()
    {
        for (int pass = 0; pass < 8 && _live.Count > 0; pass++)
        {
            foreach (Tween tween in _live.ToArray())
            {
                if (tween != null && tween.IsValid()) tween.CustomStep(60.0);
            }
            Prune();
        }
    }

    /// Ends a moment that was just built, if the player has card animations switched off.
    private void SkipIfAnimationsOff()
    {
        if (!GameSettings.CardAnimations) FinishNow();
    }

    private void RaiseShield()
    {
        if (_shield == null || !GodotObject.IsInstanceValid(_shield))
        {
            _shield = new Control { Name = "MomentShield", MouseFilter = Control.MouseFilterEnum.Stop };
            _shield.GuiInput += e =>
            {
                bool tap = (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                        || (e is InputEventScreenTouch st && st.Pressed);
                if (tap) FinishNow();
                _shield?.AcceptEvent();
            };
            _root.AddChild(_shield);
            _shield.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        _shield.Visible = true;
        _root.MoveChild(_shield, _root.GetChildCount() - 1);
    }

    private void LowerShield()
    {
        if (_shield != null && GodotObject.IsInstanceValid(_shield)) _shield.Visible = false;
    }

    // ------------------------------------------------------------------
    // Geometry: everything goes through the full global transform, because Player 2's side may be
    // turned round 180 degrees and the whole layout is scaled to fit the screen.
    // ------------------------------------------------------------------
    private static Vector2 CentreOf(Control c) => c.GetGlobalTransform() * (c.Size / 2f);
    private static float ScaleOf(Control c) => c.GetGlobalTransform().Scale.Abs().X;
    private static float RotationOf(Control c) => c.GetGlobalTransform().Rotation;

    private static Rect2 ScreenRectOf(Control c)
    {
        Transform2D t = c.GetGlobalTransform();
        Vector2 a = t * Vector2.Zero, b = t * new Vector2(c.Size.X, 0f);
        Vector2 d = t * new Vector2(0f, c.Size.Y), e = t * c.Size;
        Vector2 min = new Vector2(Mathf.Min(Mathf.Min(a.X, b.X), Mathf.Min(d.X, e.X)), Mathf.Min(Mathf.Min(a.Y, b.Y), Mathf.Min(d.Y, e.Y)));
        Vector2 max = new Vector2(Mathf.Max(Mathf.Max(a.X, b.X), Mathf.Max(d.X, e.X)), Mathf.Max(Mathf.Max(a.Y, b.Y), Mathf.Max(d.Y, e.Y)));
        return new Rect2(min, max - min);
    }

    private static bool Usable(Control c) => c != null && GodotObject.IsInstanceValid(c) && c.IsInsideTree();

    /// Puts a free-floating control so its middle is at `centre` (its pivot is its middle).
    private static void PlaceCentred(Control c, Vector2 centre) => c.GlobalPosition = centre - c.Size / 2f;

    // ------------------------------------------------------------------
    // Shared bits
    // ------------------------------------------------------------------

    /// A glowing frame that flashes round a board, so the eye goes to the right side of the table.
    public void PulseBorder(Control board, Color colour)
    {
        if (!Usable(board)) return;
        Rect2 rect = ScreenRectOf(board).Grow(6f);
        StyleBoxFlat style = new StyleBoxFlat
        {
            DrawCenter = false,
            BorderColor = colour,
            ShadowColor = new Color(colour, 0.5f),
            ShadowSize = 16,
        };
        style.SetBorderWidthAll(5);
        style.SetCornerRadiusAll(22);
        Panel frame = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        frame.AddThemeStyleboxOverride("panel", style);
        _root.AddChild(frame);
        frame.GlobalPosition = rect.Position;
        frame.Size = rect.Size;

        Tween t = NewTween();
        t.TweenProperty(frame, "modulate:a", 1f, 0.10f);
        t.TweenProperty(frame, "modulate:a", 0f, 0.35f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        t.TweenCallback(Callable.From(frame.QueueFree));
    }

    /// A few quick, shrinking nudges, then exactly back where it was.
    private void Jitter(Control c, float strength)
    {
        if (!Usable(c) || c.HasMeta("jittering")) return;
        c.SetMeta("jittering", true);
        Vector2 rest = c.Position;
        Tween t = NewTween();
        Vector2[] steps =
        {
            new Vector2(strength, -strength * 0.5f), new Vector2(-strength * 0.8f, strength * 0.4f),
            new Vector2(strength * 0.5f, strength * 0.2f), Vector2.Zero,
        };
        foreach (Vector2 step in steps)
            t.TweenProperty(c, "position", rest + step, 0.035f);
        t.TweenCallback(Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(c)) return;
            c.Position = rest;
            c.RemoveMeta("jittering");
        }));
    }

    /// A quick shake about its own middle - the padlock taking a hit.
    private void Rattle(Control c, float radians)
    {
        if (!Usable(c) || !c.Visible) return;
        c.PivotOffset = c.Size / 2f;
        float rest = c.Rotation;
        Tween t = NewTween();
        t.TweenProperty(c, "rotation", rest + radians, 0.04f);
        t.TweenProperty(c, "rotation", rest - radians * 0.8f, 0.05f);
        t.TweenProperty(c, "rotation", rest + radians * 0.4f, 0.04f);
        t.TweenProperty(c, "rotation", rest, 0.04f);
    }

    // ------------------------------------------------------------------
    // Hold: a big padlock that locks
    // ------------------------------------------------------------------

    /// A padlock about 2.6x the badge drops onto the holder's score with an overshoot, its shackle
    /// snaps shut (the click), it gives one small shake, then shrinks and slides into the corner
    /// badge. The holder's board flashes at the click. The same for the player and the bot.
    public void PlayHold(Control box, Control badge, Control board, Func<Control> buildLock,
                         Vector2 lockSize, AudioStreamPlayer click)
    {
        if (!Usable(box) || !Usable(badge))
        {
            if (Usable(badge)) badge.Modulate = Colors.White;
            click?.Play();
            return;
        }

        float s = ScaleOf(box);
        float rot = RotationOf(box);
        Vector2 centre = CentreOf(box);

        Control big = buildLock();
        big.Visible = true;
        big.Size = lockSize;
        big.PivotOffset = lockSize / 2f;
        big.Scale = Vector2.One * 2.6f * s;
        big.Rotation = rot;
        big.Modulate = new Color(1, 1, 1, 0);
        _root.AddChild(big);
        Vector2 above = centre - new Vector2(0f, 70f * s).Rotated(rot);
        PlaceCentred(big, above);

        // The shackle is the lock's first child (ScoreDisplay.BuildPadlock): lifted open, it
        // closes on the click.
        Control shackle = big.GetChildCount() > 0 ? big.GetChild(0) as Control : null;
        float shut = shackle?.Position.Y ?? 0f;
        if (shackle != null) shackle.Position = new Vector2(shackle.Position.X, shut - 9f);

        // The badge is where the big one ends up; it stays invisible until it arrives.
        _cleanup.Add(() => { if (GodotObject.IsInstanceValid(badge)) badge.Modulate = Colors.White; });
        _cleanup.Add(() => { if (GodotObject.IsInstanceValid(big)) big.QueueFree(); });

        Tween t = NewTween();
        t.SetParallel(true);

        // Drop in, with a little overshoot (0.2s).
        t.TweenProperty(big, "global_position", centre - lockSize / 2f, 0.20f)
         .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        t.TweenProperty(big, "modulate:a", 1f, 0.08f);

        // Shackle shuts (0.07s) - the click, and the board lights up, at the moment it closes.
        if (shackle != null)
        {
            t.Chain().TweenProperty(shackle, "position:y", shut, 0.07f)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        }
        t.Chain().TweenCallback(Callable.From(() =>
        {
            click?.Play();
            PulseBorder(board, new Color(0.98f, 0.8f, 0.32f));
        }));

        // One small shake (0.13s), a beat to read it (0.08s)...
        t.Chain().TweenProperty(big, "rotation", rot + 0.14f, 0.04f);
        t.Chain().TweenProperty(big, "rotation", rot - 0.10f, 0.05f);
        t.Chain().TweenProperty(big, "rotation", rot, 0.04f);
        t.Chain().TweenInterval(0.08f);

        // ...then shrink and slide into the corner badge (0.2s).
        Vector2 badgeCentre = CentreOf(badge);
        float badgeScale = ScaleOf(badge);
        t.Chain().TweenProperty(big, "global_position", badgeCentre - lockSize / 2f, 0.20f)
         .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        t.TweenProperty(big, "scale", Vector2.One * badgeScale, 0.20f)
         .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        t.TweenProperty(big, "rotation", RotationOf(badge), 0.20f);

        t.Chain().TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(big)) big.QueueFree();
            if (GodotObject.IsInstanceValid(badge)) badge.Modulate = Colors.White;
        }));

        SkipIfAnimationsOff();
    }

    // ------------------------------------------------------------------
    // Shave: a slash across their score
    // ------------------------------------------------------------------

    /// After the Shave card lands (`delay`), a bright slash sweeps across the target's score box.
    /// On impact the box jitters and flashes red, the number ticks down, a "-1" floats up, and the
    /// padlock rattles - a locked score was hit.
    public void PlayShave(Control box, Control padlock, Action tickDown, float delay)
    {
        if (!Usable(box))
        {
            tickDown?.Invoke();
            return;
        }
        _cleanup.Add(() => tickDown?.Invoke()); // never leave the old number up

        Rect2 rect = ScreenRectOf(box);
        float s = ScaleOf(box);
        Vector2 centre = rect.GetCenter();
        float angle = Mathf.Atan2(rect.Size.Y, rect.Size.X);
        Vector2 dir = Vector2.Right.Rotated(angle);
        float length = rect.Size.Length() * 1.2f;
        float thick = 9f * s;

        // A hot red-orange: the score boxes are pale now, and a white blade vanished against them.
        ColorRect slash = new ColorRect
        {
            Color = new Color(1f, 0.36f, 0.22f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Size = new Vector2(length, thick),
            PivotOffset = new Vector2(0f, thick / 2f),
            Rotation = angle,
            Scale = new Vector2(0f, 1f),
            Modulate = new Color(1, 1, 1, 0),
        };
        _root.AddChild(slash);
        slash.GlobalPosition = centre - dir * length / 2f - new Vector2(0f, thick / 2f);
        _cleanup.Add(() => { if (GodotObject.IsInstanceValid(slash)) slash.QueueFree(); });

        Tween t = NewTween();
        if (delay > 0f) t.TweenInterval(delay);
        t.TweenCallback(Callable.From(() => slash.Modulate = Colors.White));
        t.TweenProperty(slash, "scale:x", 1f, 0.14f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);

        t.TweenCallback(Callable.From(() =>
        {
            _sfxImpact?.Play();
            tickDown?.Invoke();
            Jitter(box, 6f * s);
            FlashRed(rect, s);
            FloatText("-1", centre, s);
            Rattle(padlock, 0.3f);
        }));
        t.TweenProperty(slash, "modulate:a", 0f, 0.16f);
        t.TweenCallback(Callable.From(slash.QueueFree));

        SkipIfAnimationsOff();
    }

    private void FlashRed(Rect2 rect, float s)
    {
        StyleBoxFlat style = new StyleBoxFlat { BgColor = new Color(1f, 0.18f, 0.16f, 0.6f) };
        style.SetCornerRadiusAll(Mathf.RoundToInt(14 * s));
        Panel flash = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        flash.AddThemeStyleboxOverride("panel", style);
        _root.AddChild(flash);
        flash.GlobalPosition = rect.Position;
        flash.Size = rect.Size;

        Tween t = NewTween();
        t.TweenProperty(flash, "modulate:a", 0f, 0.3f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        t.TweenCallback(Callable.From(flash.QueueFree));
    }

    private void FloatText(string text, Vector2 centre, float s)
    {
        Label label = new Label
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Size = new Vector2(120f, 60f),
        };
        label.AddThemeFontSizeOverride("font_size", 40);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.26f));
        label.AddThemeColorOverride("font_outline_color", Colors.White);
        label.AddThemeConstantOverride("outline_size", 8);
        label.PivotOffset = label.Size / 2f;
        label.Scale = Vector2.One * s;
        _root.AddChild(label);
        PlaceCentred(label, centre);

        Tween t = NewTween().SetParallel(true);
        t.TweenProperty(label, "global_position", label.GlobalPosition - new Vector2(0f, 50f * s), 0.45f)
         .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        t.TweenProperty(label, "modulate:a", 0f, 0.45f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        t.Chain().TweenCallback(Callable.From(label.QueueFree));
    }

    // ------------------------------------------------------------------
    // Trade Totals: the two numbers swap places
    // ------------------------------------------------------------------

    /// After the Trade Totals card lands, each score box lets go of its number: the two numbers
    /// arc across the table past each other and drop into the other box, which then shows it.
    /// (2026-10-05: the verification pass found Trade Totals was the one effect with no moment -
    /// both scores simply changed.)
    public void PlayTradeTotals(Control boxA, int aBefore, Control boxB, int bBefore, Action swapShown, float delay)
    {
        if (!Usable(boxA) || !Usable(boxB))
        {
            swapShown?.Invoke();
            return;
        }
        bool swapped = false;
        Action once = () => { if (swapped) return; swapped = true; swapShown?.Invoke(); };
        _cleanup.Add(once);

        float s = ScaleOf(boxA);
        Vector2 a = CentreOf(boxA), b = CentreOf(boxB);
        Label fromA = FlyingNumber(aBefore.ToString(), a, s);
        Label fromB = FlyingNumber(bBefore.ToString(), b, s);
        _cleanup.Add(() =>
        {
            if (GodotObject.IsInstanceValid(fromA)) fromA.QueueFree();
            if (GodotObject.IsInstanceValid(fromB)) fromB.QueueFree();
        });

        Vector2 across = b - a;
        Vector2 bow = new Vector2(-across.Y, across.X).Normalized() * Mathf.Min(across.Length() * 0.35f, 260f * s);

        Tween t = NewTween();
        if (delay > 0f) t.TweenInterval(delay);
        t.TweenCallback(Callable.From(() =>
        {
            _sfxSlide?.Play();
            fromA.Visible = true;
            fromB.Visible = true;
            PulseBox(boxA);
            PulseBox(boxB);
        }));
        t.SetParallel(true);
        t.TweenMethod(Callable.From<float>(u => ArcTo(fromA, a, b, (a + b) / 2f + bow, u)), 0f, 1f, 0.38f)
         .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        t.TweenMethod(Callable.From<float>(u => ArcTo(fromB, b, a, (a + b) / 2f - bow, u)), 0f, 1f, 0.38f)
         .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        t.Chain().TweenCallback(Callable.From(() =>
        {
            once();
            _sfxImpact?.Play();
            if (GodotObject.IsInstanceValid(fromA)) fromA.QueueFree();
            if (GodotObject.IsInstanceValid(fromB)) fromB.QueueFree();
            PulseBox(boxA);
            PulseBox(boxB);
        }));

        SkipIfAnimationsOff();
    }

    private Label FlyingNumber(string text, Vector2 centre, float s)
    {
        Label label = new Label
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Size = new Vector2(120f, 80f),
            Visible = false,
        };
        label.AddThemeFontSizeOverride("font_size", 56);
        label.AddThemeColorOverride("font_color", new Color(0.17f, 0.21f, 0.29f));
        label.AddThemeColorOverride("font_outline_color", Colors.White);
        label.AddThemeConstantOverride("outline_size", 10);
        label.PivotOffset = label.Size / 2f;
        label.Scale = Vector2.One * s;
        _root.AddChild(label);
        PlaceCentred(label, centre);
        return label;
    }

    private static void ArcTo(Control c, Vector2 from, Vector2 to, Vector2 control, float u)
    {
        if (!GodotObject.IsInstanceValid(c)) return;
        float a = 1f - u;
        PlaceCentred(c, a * a * from + 2f * a * u * control + u * u * to);
    }

    /// One quick swell of a score box about its middle.
    private void PulseBox(Control box)
    {
        if (!Usable(box)) return;
        box.PivotOffset = box.Size / 2f;
        Tween t = NewTween();
        t.TweenProperty(box, "scale", new Vector2(1.12f, 1.12f), 0.08f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        t.TweenProperty(box, "scale", Vector2.One, 0.14f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        _cleanup.Add(() => { if (GodotObject.IsInstanceValid(box)) box.Scale = Vector2.One; });
    }

    // ------------------------------------------------------------------
    // Copy: the card turns into the other card
    // ------------------------------------------------------------------

    /// After the Copy card lands (`delay`), the opponent's source card pulses and, at the same
    /// moment, the copying card flips: edge-on, its face is redrawn (pips and corners change
    /// under cover of the flip) and the score ticks over; then it turns back with a shimmer.
    public void PlayCopy(Control mine, Control theirs, Action redrawAndTick, float delay)
    {
        if (!Usable(mine))
        {
            redrawAndTick?.Invoke();
            return;
        }
        bool redrawn = false;
        Action once = () => { if (redrawn) return; redrawn = true; redrawAndTick?.Invoke(); };
        _cleanup.Add(once);
        _cleanup.Add(() =>
        {
            if (!GodotObject.IsInstanceValid(mine)) return;
            mine.Scale = Vector2.One;
            mine.SelfModulate = Colors.White;
        });

        mine.PivotOffset = mine.Size / 2f;

        Tween t = NewTween();
        if (delay > 0f) t.TweenInterval(delay);
        t.TweenCallback(Callable.From(() => { PulseSource(theirs); _sfxSlide?.Play(); }));
        t.TweenProperty(mine, "scale:x", 0f, 0.12f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        t.TweenCallback(Callable.From(() =>
        {
            once();
            if (GodotObject.IsInstanceValid(mine)) mine.SelfModulate = new Color(1.8f, 1.8f, 1.8f);
        }));
        t.SetParallel(true);
        t.TweenProperty(mine, "scale:x", 1f, 0.16f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        t.TweenProperty(mine, "self_modulate", Colors.White, 0.3f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

        SkipIfAnimationsOff();
    }

    /// "This is where the number came from": the source card grows a little and glows gold.
    private void PulseSource(Control card)
    {
        if (!Usable(card)) return;
        card.PivotOffset = card.Size / 2f;
        Tween t = NewTween();
        t.SetParallel(true);
        t.TweenProperty(card, "scale", new Vector2(1.15f, 1.15f), 0.12f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        t.TweenProperty(card, "self_modulate", new Color(1.5f, 1.3f, 0.7f), 0.12f);
        t.Chain().TweenProperty(card, "scale", Vector2.One, 0.18f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        t.TweenProperty(card, "self_modulate", Colors.White, 0.18f);
        _cleanup.Add(() =>
        {
            if (!GodotObject.IsInstanceValid(card)) return;
            card.Scale = Vector2.One;
            card.SelfModulate = Colors.White;
        });
    }

    // ------------------------------------------------------------------
    // Trade Hands: fold, pass, unfold
    // ------------------------------------------------------------------

    /// One card as it was drawn in a hand just before the trade.
    public readonly struct HandCard
    {
        public readonly int CardId;
        public readonly TextureRect Look;   // a detached copy of the hand card's view
        public readonly Vector2 Centre;
        public readonly Vector2 Scale;
        public readonly float Rotation;

        public HandCard(int cardId, TextureRect look, Vector2 centre, Vector2 scale, float rotation)
        {
            CardId = cardId;
            Look = look;
            Centre = centre;
            Scale = scale;
            Rotation = rotation;
        }
    }

    /// Copies of the cards drawn in a hand right now, where they are drawn. Taken BEFORE the hand
    /// is rebuilt with its new cards. Cards already hidden (the one just played) and the ids in
    /// `skip` (rescue cards, which stay with their owner) are left out.
    public static List<HandCard> Snapshot(Control hand, ICollection<int> skip)
    {
        List<HandCard> cards = new List<HandCard>();
        if (!Usable(hand)) return cards;
        Collect(hand, skip, cards);
        return cards;
    }

    private static void Collect(Node parent, ICollection<int> skip, List<HandCard> into)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is TextureRect view && view.HasMeta("cardId"))
            {
                int id = view.GetMeta("cardId").AsInt32();
                if (view.Modulate.A < 0.05f || (skip != null && skip.Contains(id)) || !view.IsVisibleInTree()) continue;
                Transform2D t = view.GetGlobalTransform();
                TextureRect look = (TextureRect)view.Duplicate();
                // Anchors first: switching them from full-rect with the old zero offsets would
                // collapse the copy to nothing.
                look.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                look.Scale = Vector2.One;
                look.Rotation = 0f;
                look.Position = Vector2.Zero;
                look.Size = view.Size;
                look.MouseFilter = Control.MouseFilterEnum.Ignore;
                look.Modulate = Colors.White;
                into.Add(new HandCard(id, look, t * (view.Size / 2f), t.Scale.Abs(), t.Rotation));
                continue;
            }
            Collect(child, skip, into);
        }
    }

    /// Each side's cards fold into a stack in the middle of their own hand, the two stacks cross the
    /// table, and each unfolds into a fan on its new owner's side. The hands themselves (already
    /// holding the swapped cards - the model traded first) stay hidden until the fans land in them.
    public void PlayTradeHands(List<HandCard> fromP1, List<HandCard> fromP2, Control p1Hand, Control p2Hand, float delay)
    {
        if (!Usable(p1Hand) || !Usable(p2Hand))
        {
            FreeLooks(fromP1);
            FreeLooks(fromP2);
            return;
        }

        p1Hand.Modulate = new Color(1, 1, 1, 0);
        p2Hand.Modulate = new Color(1, 1, 1, 0);
        _cleanup.Add(() =>
        {
            if (GodotObject.IsInstanceValid(p1Hand)) p1Hand.Modulate = Colors.White;
            if (GodotObject.IsInstanceValid(p2Hand)) p2Hand.Modulate = Colors.White;
        });

        List<(TextureRect Flier, int Id, Control Destination)> fliers = new List<(TextureRect, int, Control)>();
        void Launch(List<HandCard> cards, Control destination)
        {
            foreach (HandCard card in cards)
            {
                TextureRect flier = card.Look;
                flier.PivotOffset = flier.Size / 2f;
                flier.Scale = card.Scale;
                flier.Rotation = card.Rotation;
                flier.SetMeta(TableWorld3D.FlyingMeta, true);
                _root.AddChild(flier);
                PlaceCentred(flier, card.Centre);
                fliers.Add((flier, card.CardId, destination));
            }
        }
        Launch(fromP1, p2Hand);
        Launch(fromP2, p1Hand);
        _cleanup.Add(() =>
        {
            foreach ((TextureRect flier, _, _) in fliers)
                if (GodotObject.IsInstanceValid(flier)) flier.QueueFree();
        });

        Vector2 p1Centre = CentreOf(p1Hand), p2Centre = CentreOf(p2Hand);
        float p1Rot = RotationOf(p1Hand), p2Rot = RotationOf(p2Hand);

        Tween t = NewTween();
        t.SetParallel(true);
        if (delay > 0f)
        {
            t.TweenInterval(delay);
            t.Chain();
        }

        // Playtest 2026-10-05 (S25 recording): each stack read as ONE card, and it flew straight
        // over the boards, so the trade looked like two cards being played. Now:
        //  - the stack gathers a little in from the hand (the hands are cropped at the screen's
        //    edge, so a stack at the hand's own middle was half off screen);
        //  - its cards stay visibly fanned in the stack - a small offset and tilt each;
        //  - the two stacks travel on arcs that bow out to opposite sides, passing each other
        //    in the middle instead of sliding down the centre of the boards.
        Vector2 screenMiddle = _root.GetViewport().GetVisibleRect().GetCenter();
        Vector2 p1Gather = p1Centre.Lerp(screenMiddle, 0.18f);
        Vector2 p2Gather = p2Centre.Lerp(screenMiddle, 0.18f);

        int p1Count = 0, p2Count = 0;
        List<(TextureRect Flier, bool FromP1, int K)> stacked = new List<(TextureRect, bool, int)>();
        foreach ((TextureRect flier, _, Control destination) in fliers)
        {
            bool p1Side = destination == p2Hand;
            stacked.Add((flier, p1Side, p1Side ? p1Count++ : p2Count++));
        }

        Vector2 StackOffset(int k, int count) => new Vector2((k - (count - 1) / 2f) * 9f, -k * 4f);
        float StackTilt(int k, int count) => Mathf.DegToRad((k - (count - 1) / 2f) * 6f);

        // 1. Fold: into a fanned stack just in from their own hand, a little smaller (0.18s).
        foreach ((TextureRect flier, bool p1Side, int k) in stacked)
        {
            int count = p1Side ? p1Count : p2Count;
            Vector2 home = p1Side ? p1Gather : p2Gather;
            float baseRot = p1Side ? p1Rot : p2Rot;
            t.TweenProperty(flier, "global_position", home + StackOffset(k, count) - flier.Size / 2f, 0.18f)
             .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            t.TweenProperty(flier, "scale", flier.Scale * 0.85f, 0.18f);
            t.TweenProperty(flier, "rotation", baseRot + StackTilt(k, count), 0.18f);
        }

        // 2. Pass: each stack arcs across to the other side (0.32s), bowing out to its own side
        // of the table so the two pass each other half way - with the swish as they do.
        t.Chain().TweenCallback(Callable.From(() => { })); // a step boundary for the crossing
        foreach ((TextureRect flier, bool p1Side, int k) in stacked)
        {
            int count = p1Side ? p1Count : p2Count;
            Vector2 from = (p1Side ? p1Gather : p2Gather) + StackOffset(k, count);
            Vector2 to = (p1Side ? p2Gather : p1Gather) + StackOffset(k, count);
            Vector2 across = to - from;
            // The curve peaks at half the control point's offset: about a quarter of the screen's
            // width out from the straight line, never off its edge.
            float bowLength = Mathf.Min(across.Length() * 0.3f, screenMiddle.X);
            Vector2 bow = new Vector2(-across.Y, across.X).Normalized() * bowLength;
            Vector2 control = (from + to) / 2f + bow; // the two stacks bow opposite ways: `across` is reversed
            Vector2 half = flier.Size / 2f;
            TextureRect f = flier;
            t.TweenMethod(Callable.From<float>(u =>
            {
                if (!GodotObject.IsInstanceValid(f)) return;
                float a = 1f - u;
                f.GlobalPosition = a * a * from + 2f * a * u * control + u * u * to - half; // quadratic Bezier
            }), 0f, 1f, 0.32f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
            float endRot = (p1Side ? p2Rot : p1Rot) + StackTilt(k, count);
            t.TweenProperty(flier, "rotation", endRot, 0.32f)
             .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        }
        t.TweenCallback(Callable.From(() => _sfxSlide?.Play())).SetDelay(0.16f);

        // 3. Unfold into a fan on the new owner's side: aimed at where the hand now draws each card.
        t.Chain().TweenCallback(Callable.From(() => Unfold(fliers, p1Hand, p2Hand)));

        SkipIfAnimationsOff();
    }

    private void Unfold(List<(TextureRect Flier, int Id, Control Destination)> fliers, Control p1Hand, Control p2Hand)
    {
        Tween t = NewTween();
        t.SetParallel(true);
        foreach ((TextureRect flier, int id, Control destination) in fliers)
        {
            if (!GodotObject.IsInstanceValid(flier)) continue;
            TextureRect target = Usable(destination) ? FindView(destination, id) : null;
            if (target == null)
            {
                t.TweenProperty(flier, "modulate:a", 0f, 0.18f);
                continue;
            }
            Transform2D tt = target.GetGlobalTransform();
            Vector2 centre = tt * (target.Size / 2f);
            Vector2 scale = tt.Scale.Abs() * new Vector2(target.Size.X / Mathf.Max(1f, flier.Size.X),
                                                          target.Size.Y / Mathf.Max(1f, flier.Size.Y));
            t.TweenProperty(flier, "global_position", centre - flier.Size / 2f, 0.18f)
             .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            t.TweenProperty(flier, "scale", scale, 0.18f);
            t.TweenProperty(flier, "rotation", tt.Rotation, 0.18f);
        }
        t.Chain().TweenCallback(Callable.From(() =>
        {
            foreach ((TextureRect flier, _, _) in fliers)
                if (GodotObject.IsInstanceValid(flier)) flier.QueueFree();
            if (GodotObject.IsInstanceValid(p1Hand)) p1Hand.Modulate = Colors.White;
            if (GodotObject.IsInstanceValid(p2Hand)) p2Hand.Modulate = Colors.White;
            _sfxSlide?.Play();
        }));
    }

    private static TextureRect FindView(Node parent, int id)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is TextureRect view && view.HasMeta("cardId") && view.GetMeta("cardId").AsInt32() == id) return view;
            TextureRect deeper = FindView(child, id);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static void FreeLooks(List<HandCard> cards)
    {
        if (cards == null) return;
        foreach (HandCard card in cards)
            if (GodotObject.IsInstanceValid(card.Look)) card.Look.Free();
    }
}
