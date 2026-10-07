using Godot;
using System;
using System.Collections.Generic;

/// What the lessons need from the game they are teaching.
public interface ITeachingHost
{
    Player Player1 { get; }
    GameState State { get; }
    bool GameStarted { get; }
    bool VsBot { get; }
    bool InRun { get; }
    bool SetOverPending { get; }

    /// A Recall choice or a rescue offer owns the screen. A lesson shown over the top of one
    /// teaches nobody anything.
    bool PromptShowing { get; }

    /// The bot stands still for the whole walkthrough, so the lesson cannot desync from a table
    /// moving underneath it. This lets it think again.
    void ReleaseBot();

    /// The card just dismissed was the last one in the collection.
    void CollectionComplete();

    /// The card this player has picked up, if any (pass 49: the Modifier lesson follows it).
    Card SelectedFor(Player player);
}

/// The two things that teach: the first-launch walkthrough with its spotlight, and the coach mark
/// that explains a card the first time you meet it.
///
/// They share the same overlay and the same Next button, which is why they are one class: a coach
/// mark is a one-step tutorial, and the button in the caption has two owners.
///
/// Everything here watches the screen rather than the rules - the walkthrough waits for the player
/// to actually pick a card up, and a coach mark waits for a quiet moment. That is why it hangs off
/// TableUi's refresh (ITableUiHost.AfterRefresh) rather than off any game event.
///
/// THE FIRST-LAUNCH TUTORIAL (Alexander, 2026-09-15)
///
/// Against the bot there is nobody in the room to explain the game, so the game has to teach
/// it. Six steps, each highlighting ONE control with one or two lines - the anti-wall-of-text
/// the tenets ask for, and the only form that works at both ends of the 5-to-85 range.
///
/// It teaches THE TABLE AND ONLY THE TABLE. The ladder is already the tutorial for the cards:
/// ten rungs, one new card each, used on you by an opponent before the market will sell it to
/// you. A tutorial that also explained effect cards would be competing with a teaching
/// structure that already works, and would have to explain six cards the player cannot yet own.
///
/// THE FIRST MATCH IS STAGED (Alexander's call): the deck is stacked so the opening deal is
/// 10 + 6 = 16 against the bot's 9 + 5, and the hand holds a +4. The lesson therefore ends with
/// the player making the RIGHT play - picking up the +4, seeing 16 + 4 = 20 in green, playing
/// it and holding on the target - rather than any play. Staging only happens at a target of 20
/// (see ShouldStageTutorial); replayed at any other rung the same six steps run on a real deal,
/// and every caption reads live values so none of them can lie.
///
/// COACH-MARKS: one line, the first time you meet a card
///
/// This is what actually delivers the ladder's promise. The ladder introduces one new card per
/// rung and the market sells it to you straight afterwards - but until now the card simply
/// appeared and something happened to your score. One highlight and one sentence, once ever.
///
/// The sentence is CardEffects.Introduction, which is the market's own Description. Two copies
/// of an explanation drift, and the player would end up being told two different things about
/// one card.
///
/// "Met" is profile level (RunData.CardsMet), so a lost run does not un-teach it, and it is the
/// same set a collection log will read.
public sealed class Teaching
{
    private readonly ITeachingHost _host;

    /// The scene node the overlay hangs on, and the source of the engine services a plain class
    /// has no access to.
    private readonly Node _root;

    /// The table's picture and the menus. A lesson points at things in one and refuses to run over
    /// the top of the other - UI talking to UI, so plain references rather than contracts.
    private readonly TableUi _ui;
    private readonly Menus _menus;

    // What a lesson points at is asked of the picture every time rather than held: a rotation
    // swaps the whole layout scene (pass 34), and a node held from before it would be freed.
    private Control _p1ModifierContainer => _ui.P1Hand;

    public Teaching(ITeachingHost host, Node root, TableUi ui, Menus menus)
    {
        _host = host;
        _root = root;
        _ui = ui;
        _menus = menus;
    }

    /// Whether the walkthrough runs this match, and whether the deal is staged for it.
    ///
    /// Asked BEFORE the hand is dealt and before the first shuffle, because staging is a change to
    /// both of them.
    public bool PrepareForMatch()
    {
        bool run = ShouldRunTutorial();
        Staged = run && ShouldStageTutorial();
        return run;
    }

    /// A call put off to the end of the frame, and dropped if the table has been freed in between -
    /// the courtesy Node.CallDeferred used to do for this code when it lived on the node.
    private void Defer(Action action) =>
        Callable.From(() => { if (GodotObject.IsInstanceValid(_root)) action(); }).CallDeferred();

    /// The opening cards, APPENDED in this order - the deck is drawn from the end, and the turn
    /// order is P1, P2, P1, P2. So the last entry is Player 1's first card.
    public static readonly int[] Opening = { 5, 6, 9, 10 };

    /// Player 1's staged hand. The +4 is the lesson; the rest are there so the hand looks normal.
    public static readonly int[] Modifiers = { 4, 3, -2, -1 };

    private const int TutorialSteps = 5;

    // ------------------------------------------------------------------
    // The second lesson: over is not bust (playtest, 2026-10-06 - Alexander's mother did not
    // know you can go over the target and then play a minus card).
    //
    // After a completed staged walkthrough, the next set is stacked: Player 1 draws 10 and 1 on
    // the opening deal (11), then 10 on the next turn (21). The moment they are over with a minus
    // card in hand that brings them back, one more DO step appears - the -1 pulses, exactly like
    // the +4 did, and playing it is the lesson. If they hold at 11 instead, it waits for the next
    // time they go over in this match.
    // ------------------------------------------------------------------

    /// Player 1's first draws in the second lesson's set, in draw order.
    public static readonly int[] OverLessonDraws = { 10, 1, 10 };

    /// The step index the second lesson runs as (after the five walkthrough steps).
    private const int OverStep = TutorialSteps;

    /// The walkthrough was finished (not skipped) on a staged match, and the second lesson has
    /// not been shown yet.
    public bool OverLessonPending { get; private set; }

    private const float SpotlightPad = 10f;

    /// Set by "Replay the tutorial", which reloads the scene - so it is a static, for the same
    /// reason Menus.PendingLocal2Player is. It survives the reload; it is never saved to disk.
    public static bool PendingTutorial;

    public bool Running { get; private set; }

    public bool Staged { get; private set; }   // this match's deck and hand are stacked for the lesson

    private int _tutorialIndex;

    private int _tutorialModifierCount;  // to notice a card actually being played

    private Control _spotlightOverlay;

    private ColorRect[] _spotlightShades;

    private ColorRect _spotlightHoleBlock;

    private PanelContainer _spotlightCaption;

    private Label _spotlightLabel;

    private Button _spotlightNext;

    private Button _spotlightSkip;

    private bool _spotlightSettling;

    /// A second, LOOK-only hole (pass 46): the tutorial's Modifier step points at the hand AND at
    /// the score that a picked-up card previews. The four shades stay the input gate for the main
    /// hole; this one is only see-through - the shades skip drawing inside it, but still stop
    /// taps there, so nothing in it can be pressed. Rect in canvas space (x, y, w, h); zero size
    /// means no second hole.
    private ShaderMaterial _shadeMaterial;

    private const string ShadeShader = @"shader_type canvas_item;
uniform vec4 hole1 = vec4(0.0);
uniform vec4 hole2 = vec4(0.0);
varying vec2 world;
void vertex() { world = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy; }
bool inside(vec4 h, vec2 p) { return h.z > 0.0 && p.x >= h.x && p.x <= h.x + h.z && p.y >= h.y && p.y <= h.y + h.w; }
void fragment() {
    if (inside(hole1, world) || inside(hole2, world)) COLOR.a = 0.0;
}";

    private ColorRect _spotlightDim;

    /// A hole cut in a dim, made of FOUR rects around the highlighted control rather than a
    /// shader. Cheap, no material, correct at every scale and orientation - and it degrades
    /// honestly: a wrong rect shows a misplaced hole rather than a black screen.
    ///
    /// The shades are the input gate as well as the dim. They stop mouse events; the hole has no
    /// child, so taps inside it fall straight through to the control being taught. That is the
    /// whole mechanism behind a "do" step, and it needs no changes to HumanCanActFor at all. A
    /// "tell" step drops a transparent blocker over the hole as well, and nothing is clickable.
    public void BuildSpotlight()
    {
        _spotlightOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(_spotlightOverlay);
        _spotlightOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        Color shade = new Color(0.02f, 0.05f, 0.1f, 0.72f);
        _shadeMaterial = new ShaderMaterial { Shader = new Shader { Code = ShadeShader } };
        _shadeMaterial.SetShaderParameter("hole2", Vector4.Zero);
        _shadeMaterial.SetShaderParameter("hole1", Vector4.Zero);

        // The DIM is one full-screen rect with the holes cut out by the shader (playtest,
        // 2026-10-06: a hairline left un-dimmed above the card). Four dim rects meeting edge to
        // edge leave a seam wherever their shared edge falls between two screen pixels - and the
        // canvas is scaled to fit the phone, so that is most edges. The four rects stay, now
        // invisible, because they are still the input gate (see above).
        _spotlightDim = new ColorRect { Color = shade, MouseFilter = Control.MouseFilterEnum.Ignore, Material = _shadeMaterial };
        _spotlightOverlay.AddChild(_spotlightDim);
        _spotlightDim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _spotlightShades = new ColorRect[4];
        for (int i = 0; i < _spotlightShades.Length; i++)
        {
            ColorRect rect = new ColorRect { Color = new Color(0, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Stop };
            _spotlightOverlay.AddChild(rect);
            _spotlightShades[i] = rect;
        }

        _spotlightHoleBlock = new ColorRect
        {
            Color = new Color(0, 0, 0, 0),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        _spotlightOverlay.AddChild(_spotlightHoleBlock);

        _spotlightCaption = new PanelContainer();
        OverlayUi.StylePanel(_spotlightCaption, 18, bubble: true);
        _spotlightOverlay.AddChild(_spotlightCaption);

        VBoxContainer box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 12);
        _spotlightCaption.AddChild(box);

        _spotlightLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _spotlightLabel.AddThemeFontSizeOverride("font_size", 28); // was 22: bigger for older eyes (2026-10-06)
        box.AddChild(_spotlightLabel);

        HBoxContainer buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 16);
        box.AddChild(buttons);

        // Skip is on EVERY step, not earned by sitting through the first one. Somebody who has
        // played this kind of game before does not want six taps, and making them work for the
        // way out is how you lose them on the first screen.
        _spotlightSkip = new Button { Text = "Skip" };
        _spotlightSkip.Pressed += () => FinishTutorial();
        buttons.AddChild(_spotlightSkip);

        _spotlightNext = new Button { Text = "Got it" };
        _spotlightNext.Pressed += OnSpotlightNextPressed;
        buttons.AddChild(_spotlightNext);
    }

    /// The highlighted control's axis-aligned box in screen space. Taken through the full
    /// transform rather than GlobalPosition, for the same reason AnimateCardDrop does it: a side
    /// of the table may be rotated 180 degrees, and a rotated control's position is not its corner.
    private static Rect2 ScreenRectOf(Control target)
    {
        // On the 3D table a board card or deck is drawn somewhere else than its flat 2D rect:
        // ring the one the player can see (pass 65).
        if (TableWorld3D.Instance != null && TableWorld3D.Instance.TryScreenRect(target, out Rect2 seen)) return seen;

        Transform2D t = target.GetGlobalTransform();
        Vector2 size = target.Size;
        Vector2 a = t * Vector2.Zero;
        Vector2 b = t * new Vector2(size.X, 0f);
        Vector2 c = t * new Vector2(0f, size.Y);
        Vector2 d = t * size;

        Vector2 min = new Vector2(Mathf.Min(Mathf.Min(a.X, b.X), Mathf.Min(c.X, d.X)),
                                  Mathf.Min(Mathf.Min(a.Y, b.Y), Mathf.Min(c.Y, d.Y)));
        Vector2 max = new Vector2(Mathf.Max(Mathf.Max(a.X, b.X), Mathf.Max(c.X, d.X)),
                                  Mathf.Max(Mathf.Max(a.Y, b.Y), Mathf.Max(c.Y, d.Y)));
        return new Rect2(min, max - min);
    }

    private static bool Placed(Control c) => c != null && c.IsInsideTree() && c.Visible && c.Size.X > 1f;

    private void PlaceSpotlight(Control target, bool blockHole, Control lookOnly = null, float extraTop = 0f)
    {
        if (_spotlightOverlay == null) return;

        Vector2 vp = _root.GetViewport().GetVisibleRect().Size;
        Rect2 hole = (target != null && target.IsInsideTree() && target.Size.X > 1f)
            ? ScreenRectOf(target).Grow(SpotlightPad).GrowIndividual(0f, extraTop, 0f, 0f)
            : new Rect2(vp / 2f, Vector2.Zero); // no target: a plain dim, no hole

        Rect2? hole2 = Placed(lookOnly) ? ScreenRectOf(lookOnly).Grow(SpotlightPad) : null;
        _shadeMaterial?.SetShaderParameter("hole2", hole2.HasValue
            ? new Vector4(hole2.Value.Position.X, hole2.Value.Position.Y, hole2.Value.Size.X, hole2.Value.Size.Y)
            : Vector4.Zero);

        float left = Mathf.Clamp(hole.Position.X, 0f, vp.X);
        float top = Mathf.Clamp(hole.Position.Y, 0f, vp.Y);
        float right = Mathf.Clamp(hole.End.X, 0f, vp.X);
        float bottom = Mathf.Clamp(hole.End.Y, 0f, vp.Y);

        SetRect(_spotlightShades[0], 0f, 0f, vp.X, top);                       // above
        SetRect(_spotlightShades[1], 0f, bottom, vp.X, vp.Y - bottom);         // below
        SetRect(_spotlightShades[2], 0f, top, left, bottom - top);             // left
        SetRect(_spotlightShades[3], right, top, vp.X - right, bottom - top);  // right

        _shadeMaterial?.SetShaderParameter("hole1", new Vector4(left, top, right - left, bottom - top));
        SetRect(_spotlightHoleBlock, left, top, right - left, bottom - top);
        _spotlightHoleBlock.Visible = blockHole;

        // The caption goes under the hole, or over it when the hole is low - it must never cover
        // the thing it is pointing at.
        //
        // The width comes from the VIEWPORT, never from the caption's own minimum: an autowrapping
        // Label reports its UNWRAPPED single-line width as its minimum until it has been laid out
        // once. Measure first and the panel comes out screen-wide and one line tall, with the text
        // clipped - the same trap the stats row hit with HFlowContainer. Give it the width, and
        // the height follows from it. The floor covers the frame before that height is right.
        //
        // 2026-10-05: the caption sits DIRECTLY next to the thing it is about - CaptionGap from
        // the hole, on whichever side has the most room - and is centred on the hole along that
        // side, so the spotlight and the words read as one unit rather than a hole here and a
        // caption parked somewhere in the middle of the screen.
        Rect2 h1 = new Rect2(left, top, right - left, bottom - top);
        Rect2 placed = PlaceCaption(h1, hole2, vp);
        SetRect(_spotlightCaption, placed.Position.X, placed.Position.Y, placed.Size.X, placed.Size.Y);

        // If the panel could not shrink to that height yet (its label has not wrapped at the new
        // width - see ShowCoachMark), keep it invisible for the frame rather than flash a pillar.
        // SettleSpotlight places it again on the next frame, when it can.
        float alpha = _spotlightCaption.Size.Y > placed.Size.Y + 4f ? 0f : 1f;
        _spotlightCaption.Modulate = new Color(1f, 1f, 1f, alpha);
    }

    private const float CaptionGap = 16f;     // between the hole and the caption
    private const float ScreenMargin = 16f;   // between the caption and the screen's edge
    private const float SideCaptionMinWidth = 240f;

    /// Where the caption goes for this hole: tries the four sides in order of how much room each
    /// has, and takes the first where it fits on screen without covering either hole. Falls back
    /// to the roomiest of above/below, clamped onto the screen.
    private Rect2 PlaceCaption(Rect2 hole, Rect2? hole2, Vector2 vp)
    {
        float roomAbove = hole.Position.Y - CaptionGap - ScreenMargin;
        float roomBelow = vp.Y - hole.End.Y - CaptionGap - ScreenMargin;
        float roomLeft = hole.Position.X - CaptionGap - ScreenMargin;
        float roomRight = vp.X - hole.End.X - CaptionGap - ScreenMargin;

        // Sides ranked by room. Above/below get the usual width; left/right get whatever is
        // beside the hole, and only qualify if that is wide enough to read.
        var sides = new List<(char Side, float Room)>
        {
            ('a', roomAbove), ('b', roomBelow), ('l', roomLeft), ('r', roomRight),
        };
        sides.Sort((x, y) => y.Room.CompareTo(x.Room));

        Rect2? fallback = null;
        foreach ((char side, float room) in sides)
        {
            bool beside = side == 'l' || side == 'r';
            float width = beside
                ? Mathf.Min(room, vp.X * 0.6f)
                : Mathf.Min(vp.X * 0.72f, vp.X - 2f * ScreenMargin);
            if (beside && width < SideCaptionMinWidth) continue;

            float height = CaptionHeightFor(width, vp);
            float x, y;
            switch (side)
            {
                case 'a': x = hole.GetCenter().X - width / 2f; y = hole.Position.Y - CaptionGap - height; break;
                case 'b': x = hole.GetCenter().X - width / 2f; y = hole.End.Y + CaptionGap; break;
                case 'l': x = hole.Position.X - CaptionGap - width; y = hole.GetCenter().Y - height / 2f; break;
                default:  x = hole.End.X + CaptionGap; y = hole.GetCenter().Y - height / 2f; break;
            }

            // Slide along the side to stay on screen; never away from the hole.
            if (beside) y = Mathf.Clamp(y, ScreenMargin, Mathf.Max(ScreenMargin, vp.Y - ScreenMargin - height));
            else x = Mathf.Clamp(x, ScreenMargin, Mathf.Max(ScreenMargin, vp.X - ScreenMargin - width));

            Rect2 cap = new Rect2(x, y, width, height);
            fallback ??= cap;
            bool onScreen = cap.Position.X >= ScreenMargin - 0.5f && cap.Position.Y >= ScreenMargin - 0.5f
                         && cap.End.X <= vp.X - ScreenMargin + 0.5f && cap.End.Y <= vp.Y - ScreenMargin + 0.5f;
            if (!onScreen) continue;
            if (cap.Intersects(hole) || (hole2.HasValue && cap.Intersects(hole2.Value))) continue;
            return cap;
        }

        // Nothing fits cleanly (a hole as big as the screen): the roomiest side, kept on screen.
        Rect2 f = fallback ?? new Rect2(ScreenMargin, ScreenMargin, vp.X - 2f * ScreenMargin, vp.Y * 0.08f);
        f.Size = new Vector2(f.Size.X, CaptionHeightFor(f.Size.X, vp)); // the label's width is this one's again
        float fy = Mathf.Clamp(f.Position.Y, ScreenMargin, Mathf.Max(ScreenMargin, vp.Y - ScreenMargin - f.Size.Y));
        float fx = Mathf.Clamp(f.Position.X, ScreenMargin, Mathf.Max(ScreenMargin, vp.X - ScreenMargin - f.Size.X));
        return new Rect2(fx, fy, f.Size.X, f.Size.Y);
    }

    /// The caption's height at this width. The label is given the width first (see above); the
    /// floor covers the frame before the wrapped height is right - the real height takes over on
    /// the next, deferred, refresh.
    private float CaptionHeightFor(float width, Vector2 vp)
    {
        float labelWidth = Mathf.Max(80f, width - 40f);
        _spotlightLabel.CustomMinimumSize = new Vector2(labelWidth, 0f);
        // Give the label that width NOW, so it re-wraps before anything asks it for a minimum:
        // a stale tall minimum would also stop the panel shrinking to the height set below.
        _spotlightLabel.Size = new Vector2(labelWidth, _spotlightLabel.Size.Y);

        // Playtest 2026-10-05: a coach mark's first frame came up as a white pillar the height of
        // the screen. An autowrapping Label reports the height it wrapped to at its LAST width
        // (a narrow one, before it is laid out at this width), and the panel took that. So the
        // text is measured here at the width it is about to get, and only the rest of the
        // panel - margins, the button row - is read off the controls (the stale label height is
        // in both minimums, so it cancels out of the difference).
        Font font = _spotlightLabel.GetThemeFont("font");
        int fontSize = _spotlightLabel.GetThemeFontSize("font_size");
        float textHeight = font != null
            ? font.GetMultilineStringSize(_spotlightLabel.Text, HorizontalAlignment.Left, labelWidth, fontSize).Y
            : _spotlightLabel.GetCombinedMinimumSize().Y;
        float chrome = _spotlightCaption.GetCombinedMinimumSize().Y - _spotlightLabel.GetCombinedMinimumSize().Y;
        return Mathf.Max(chrome + textHeight + 4f, vp.Y * 0.06f);
    }

    private static void SetRect(Control control, float x, float y, float width, float height)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        control.Position = new Vector2(x, y);
        control.Size = new Vector2(Mathf.Max(0f, width), Mathf.Max(0f, height));
    }

    private Control TutorialTarget(int step)
    {
        switch (step)
        {
            case 0: return _ui.P1ScoreBlock;
            case 1: return _ui.DeckFootprint;
            case 2: return LessonCardControl() ?? _ui.P1Hand; // pass 49: the one right card
            case 3: return _ui.P1ActionRow;
            case 4: return _ui.P1WinsRow;
            case OverStep: return LessonCardControl() ?? _ui.P1Hand; // the minus card that saves you
            default: return null;
        }
    }

    /// Steps 2-3 are DO steps: the player performs the thing rather than reading about it. That is
    /// the difference between a tutorial and a slideshow, and for both ends of the 5-to-85 range
    /// it is the whole point - playing a card is learned by playing one.
    ///
    /// Picking a card up and committing it used to be two steps, with the second one highlighting
    /// the Play button. That was wrong twice over (Alexander, S25 Ultra): the hole landed beside
    /// the confirm row rather than on it, so the button the step asked for was under the dim and
    /// could not be pressed - and the lesson did not need two steps anyway. Tapping the same card
    /// again commits it (the quick path the touch model has always had), so one step teaches both
    /// halves and never has to find a control that only exists mid-gesture.
    private static bool TutorialIsDoStep(int step) => step == 2 || step == 3 || step == OverStep;

    private string TutorialTextFor(int step)
    {
        switch (step)
        {
            case 0:
                return $"This is your score. You are at {_host.Player1.CurrentScore}, and you are aiming "
                     + $"for {_host.State.TargetScore} without going over.";
            case 1:
                // Pass 46: each player has their own deck now, and it carries no count.
                return "Your deck: four of each card from 1 to 10, shuffled every set. Your "
                     + "opponent draws from a deck of their own.";
            case 2:
                return "Tap a Modifier and your score shows what it would become. Tap it again "
                     + "to Play it.";
            case 3:
                return HoldOrDrawText();
            case 4:
                return $"Win {GameState.SetsToWinMatch} sets to take the match. These are yours "
                     + "so far. That is everything - good luck.";
            case OverStep:
            {
                Card fix = BestModifier();
                string name = fix != null ? (fix.Value > 0 ? "+" : "") + fix.Value : "minus Modifier";
                return $"You are at {_host.Player1.CurrentScore}, over {_host.State.TargetScore} - but it is "
                     + "not a bust until your turn ends. Play a minus Modifier to come back: "
                     + $"tap the {name}, then tap it again.";
            }
            default:
                return string.Empty;
        }
    }

    /// The Draw Card / Hold step reads the live score, so it is honest on the staged first match,
    /// on a replay's real deal, and after whatever Modifier was just played. "On target" is said
    /// only when it is true (pass 46: it used to be said anywhere within 2, e.g. at 19/20).
    private string HoldOrDrawText()
    {
        int score = _host.Player1.CurrentScore;
        int target = _host.State.TargetScore;

        if (score == target)
            return $"You are exactly on {target}. Hold stops you taking cards and locks your score "
                 + "in for the rest of the set.";
        if (score > target)
            return $"You are over {target}. If the turn ends like this, you bust. Draw Card or "
                 + "Hold to end the turn.";
        if (score >= target - 2)
            return $"You are at {score}, close to {target}. Hold locks that in for the rest of the "
                 + $"set. Draw Card risks going over for a better score.";
        return "Draw Card takes another card next turn. Hold stops you there and locks your "
             + "score in. Choose one.";
    }

    /// True once the player has done the thing the current DO step asked for.
    private bool TutorialStepDone(int step)
    {
        switch (step)
        {
            case 2: return _host.Player1.Modifiers.Count < _tutorialModifierCount;
            case 3: return !_host.Player1.CanAct;
            // Played a card (normally the pulsing minus), or the turn is over some other way.
            case OverStep:
                return _host.Player1.Modifiers.Count < _tutorialModifierCount || !_host.Player1.CanAct
                    || _host.Player1.CurrentScore <= _host.State.TargetScore;
            default: return false;
        }
    }

    /// First launch, or an explicit replay. Gated on the run's OWN step rather than the all-time
    /// best, and on the profile-level flag, so it happens once and never nags.
    private bool ShouldRunTutorial()
    {
        if (!_host.VsBot || !_host.InRun) return false;
        if (PendingTutorial) return true;

        RunData run = RunData.Instance;
        return run != null && !run.TutorialSeen && run.StepIndex == 0;
    }

    /// Only stage the turn where the staged numbers are true. The lesson lands on exactly the
    /// target, which needs a two-card opening (target 20 or more) and a +4 that reaches it from
    /// 16 - so it is stage 1's ruleset or nothing. A replay at any other rung runs the same six
    /// steps on a real deal, and every caption reads live values, so nothing said is ever wrong.
    private bool ShouldStageTutorial() => _host.State.TargetScore == 20;

    public async void StartTutorial()
    {
        Running = true;
        _tutorialIndex = 0;
        _tutorialModifierCount = _host.Player1.Modifiers.Count;

        // Let the opening deal land first - being taught about a score before the cards that made
        // it have arrived is worse than waiting half a second.
        for (int i = 0; i < 40; i++)
        {
            await _root.ToSignal(_root.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!_root.IsInsideTree() || !Running) return;
        }

        _root.MoveChild(_spotlightOverlay, _root.GetChildCount() - 1);
        _spotlightOverlay.Visible = true;
        RefreshSpotlight();
    }

    private void AdvanceTutorial()
    {
        if (!Running) return;

        // The second lesson is one step on its own.
        if (_tutorialIndex == OverStep)
        {
            FinishTutorial();
            return;
        }

        _tutorialIndex++;
        if (_tutorialIndex >= TutorialSteps)
        {
            FinishTutorial(walkthroughCompleted: true);
            return;
        }

        RefreshSpotlight();
    }

    /// The second lesson, if it is waiting and the moment has come: Player 1 is over the target,
    /// can still act, and holds a minus card that brings them back. Watches the screen like the
    /// rest of the teaching, from CheckTutorialProgress.
    private void TryStartOverLesson()
    {
        if (!OverLessonPending || Running) return;
        if (!_host.GameStarted || _host.State.IsGameOver || _host.SetOverPending || _host.PromptShowing) return;
        if (_menus.Covering) return;

        Player you = _host.Player1;
        if (!you.CanAct || you.CurrentScore <= _host.State.TargetScore) return;
        Card fix = BestModifier();
        if (fix == null || fix.Value >= 0) return;

        OverLessonPending = false;
        StartOverLesson();
    }

    private async void StartOverLesson()
    {
        // Running at once, so the bot stands still (Bot.ProcessTurn checks it) - then the card
        // that took the player over is allowed to land before the lesson about it appears.
        Running = true;
        _tutorialIndex = OverStep;
        _tutorialModifierCount = _host.Player1.Modifiers.Count;

        for (int i = 0; i < 40; i++)
        {
            await _root.ToSignal(_root.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!_root.IsInsideTree() || !Running) return;
        }

        _root.MoveChild(_spotlightOverlay, _root.GetChildCount() - 1);
        _spotlightOverlay.Visible = true;
        RefreshSpotlight();
    }

    public void RefreshSpotlight()
    {
        PlaceCurrentSpotlight();
        SettleSpotlight();
    }

    /// Where the hole goes right now, for whichever of the two owners has the overlay.
    private void PlaceCurrentSpotlight()
    {
        if (_spotlightOverlay == null || !_spotlightOverlay.Visible) return;

        // A coach-mark borrows the same overlay, so it has to be re-placed on a rotation too.
        if (_coachShowing.HasValue)
        {
            _spotlightSkip.Visible = false; // one line, once ever: nothing to skip
            _spotlightNext.Visible = true;
            PlaceSpotlight(CoachTarget(_coachShowing.Value), blockHole: true);
            return;
        }

        if (!Running) return;

        bool doStep = TutorialIsDoStep(_tutorialIndex);
        ApplyTutorialEmphasis(_tutorialIndex);
        _spotlightLabel.Text = TutorialTextFor(_tutorialIndex);
        _spotlightNext.Visible = !doStep;   // a DO step is finished by doing it, not by a button
        // Skip on the FIRST step only (playtest, 2026-10-06: still showing on the deck step). It is
        // the one chance to opt out before the lesson starts; once the player has carried on past
        // it, every later step finishes with its own button or by doing the thing.
        _spotlightSkip.Visible = _tutorialIndex == 0;
        Control target = TutorialTarget(_tutorialIndex);
        // A picked-up card rises and grows out of its slot. Ring the art itself (scaled and lifted -
        // GetGlobalTransform carries both), not the slot plus a guess: the guess overshot and cut a
        // bright notch into the Play button above the card (playtest, 2026-10-06).
        if ((_tutorialIndex == 2 || _tutorialIndex == OverStep) && target is Button
            && _host.SelectedFor(_host.Player1) != null && target.GetChildCount() > 0
            && target.GetChild(0) is TextureRect art)
            target = art;
        PlaceSpotlight(target, blockHole: !doStep, TutorialLookTarget(_tutorialIndex));
    }

    // ------------------------------------------------------------------
    // One right answer (pass 49)
    //
    // When a step has one right answer, the tutorial points at it and nothing else: the answer
    // pulses, and the wrong answer is out of reach.
    // - The Modifier step: the one Modifier that takes you closest to the target without going
    //   over (the staged first match: the +4, from 16 to 20). The hole is cut round that card
    //   alone, so the rest of the hand cannot be tapped, and the score is lit beside it.
    // - The Draw Card / Hold step, when you are exactly on the target: Hold pulses and Draw Card
    //   is greyed out. On any other score both are fair choices, so neither is pushed.
    // ------------------------------------------------------------------

    /// The plain Modifier that lands closest to the target without going over, or null when none
    /// improves on the current score (then the whole hand is fair game and nothing is singled out).
    private Card BestModifier()
    {
        Player p = _host.Player1;
        int target = _host.State.TargetScore;
        Card best = null;
        int bestScore = p.CurrentScore <= target ? p.CurrentScore : int.MinValue;
        foreach (Card card in p.Modifiers)
        {
            if (card.Effect != CardEffect.None) continue;
            int after = p.CurrentScore + card.Value;
            if (after > target || after <= bestScore) continue;
            best = card;
            bestScore = after;
        }
        return best;
    }

    /// The card the Modifier step is about. Once it is picked up it is the SELECTED card (the
    /// hole let nothing else be tapped), and the hole stays on it so tapping it again plays it.
    private Control LessonCardControl()
    {
        Card lesson = _host.SelectedFor(_host.Player1) ?? BestModifier();
        return lesson != null ? _ui.P1HandCardFor(lesson) : null;
    }

    private bool OnTargetExactly => _host.Player1.CurrentScore == _host.State.TargetScore;

    private void ApplyTutorialEmphasis(int step)
    {
        Card pulseCard = (step == 2 || step == OverStep) && _host.SelectedFor(_host.Player1) == null ? BestModifier() : null;
        bool forceHold = step == 3 && OnTargetExactly;
        SetEmphasis(pulseCard, forceHold);
    }

    private void SetEmphasis(Card pulseCard, bool forceHold)
    {
        if (_ui.TutorialPulseCard == pulseCard && _ui.TutorialPulseHold == forceHold && _ui.TutorialLockDraw == forceHold)
            return;
        _ui.TutorialPulseCard = pulseCard;
        _ui.TutorialPulseHold = forceHold;
        _ui.TutorialLockDraw = forceHold;
        _ui.DeferRefresh(); // the table draws them; only asked for when something changed
    }

    /// A second thing a step points at, to look at only (pass 46). The Modifier step shows the
    /// score as well as the hand: picking a card up previews the score it would make, and that
    /// change is the lesson.
    private Control TutorialLookTarget(int step) => (step == 2 || step == OverStep) ? _ui.P1ScoreBlock : null;

    /// ...and then again once the layout has actually settled.
    ///
    /// THE BUG this exists for (Alexander, S25 Ultra, 2026-09-15): rotating portrait to landscape
    /// left the hole over the score off to one side. A single deferred pass measures the layout
    /// mid-move - the sides are re-ordered, the base resolution is rewritten, and EnsureLayoutFits
    /// may still be scaling - so GetGlobalTransform returns where the control WAS. This is the
    /// same two-frame lesson EnsureLayoutFits already learned, and for the same reason: a
    /// container's geometry is only right on the pass after the one that changed it.
    ///
    /// Placed immediately as well, so the hole never blinks; the settle pass only corrects it.
    /// Latched, because RefreshSpotlight is deferred from every UpdateUI and a pile of overlapping
    /// waits would all place the same rect.
    private async void SettleSpotlight()
    {
        if (_spotlightSettling || _spotlightOverlay == null || !_spotlightOverlay.Visible) return;
        _spotlightSettling = true;

        try
        {
            // Re-placed on BOTH frames: after the first the caption's label has wrapped at its new
            // width (so the panel can shrink to the right height), after the second the layout
            // underneath has settled too.
            for (int i = 0; i < 2; i++)
            {
                await _root.ToSignal(_root.GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!_root.IsInsideTree()) return;
                PlaceCurrentSpotlight();
            }
        }
        finally
        {
            _spotlightSettling = false;
        }
    }

    /// Runs from UpdateUI, which is called after every action that could complete a step - so a
    /// step's completion never has to be wired into the five handlers that could cause it.
    public void CheckTutorialProgress()
    {
        TryStartOverLesson();
        if (!Running || _spotlightOverlay == null || !_spotlightOverlay.Visible) return;

        if (TutorialIsDoStep(_tutorialIndex) && TutorialStepDone(_tutorialIndex))
        {
            AdvanceTutorial();
            return;
        }

        Defer(RefreshSpotlight); // the highlighted control may have moved
    }

    private void FinishTutorial(bool walkthroughCompleted = false)
    {
        if (!Running) return;

        Running = false;
        PendingTutorial = false;
        // Finished properly (not skipped) on the staged first match: the second lesson - over is
        // not bust - waits for the next set.
        if (walkthroughCompleted && Staged) OverLessonPending = true;
        SetEmphasis(null, false); // no pulse or locked Draw Card outlives the lesson
        if (_spotlightOverlay != null) _spotlightOverlay.Visible = false;
        RunData.Instance?.MarkTutorialSeen();

        // The bot has been held for the whole walkthrough (Bot.ProcessTurn refuses to run while
        // the tutorial is up) so the lesson could not desync from a table moving underneath it.
        // Let it think now, and the turn resolves normally from here.
        if (_host.VsBot && _host.GameStarted && !_host.State.IsGameOver) _host.ReleaseBot();

        // Deferred: FinishTutorial can be reached from inside UpdateUI (a DO step completing on
        // the last one), and a re-entrant refresh is the kind of thing that works until it doesn't.
        _ui.DeferRefresh();
    }

    private readonly struct CoachMark
    {
        public readonly Card Card;
        public readonly bool FromOpponent;

        public CoachMark(Card card, bool fromOpponent)
        {
            Card = card;
            FromOpponent = fromOpponent;
        }
    }

    private readonly Queue<CoachMark> _coachQueue = new Queue<CoachMark>();

    private CoachMark? _coachShowing;

    /// The effect banner's text, put away while a coach mark is up.
    private string _bannerHeldForCoach;

    /// A coach mark is on screen (the bot waits while one is).
    public bool CoachShowing => _coachShowing.HasValue;

    /// Where the highlight goes: the middle panel's banner when the card was played AT you (the
    /// banner is the thing that just narrated it), your own hand when it is a card you now hold.
    ///
    /// Release playtest (2026-09-29): the banner is the middle panel's toast, which in the current
    /// layouts is not where the card is - the highlight sat on the stage label while the card it
    /// explained was up on the opponent's board. Point at the card itself.
    private Control CoachTarget(CoachMark mark) =>
        mark.FromOpponent ? (_ui.BoardCardView(mark.Card) ?? _ui.EffectBanner) : _p1ModifierContainer;

    public void QueueCoachMark(Card card, bool fromOpponent)
    {
        string key = CardEffects.MetKey(card);
        if (key == null) return; // a plain +3 explains itself

        RunData run = RunData.Instance;
        if (run == null || run.HasMetCard(key)) return;

        // The same card can arrive twice in one turn - once in the hand and once across the table
        // - and the same explanation twice is worse than none.
        foreach (CoachMark queued in _coachQueue)
            if (CardEffects.MetKey(queued.Card) == key) return;
        if (_coachShowing.HasValue && CardEffects.MetKey(_coachShowing.Value.Card) == key) return;

        _coachQueue.Enqueue(new CoachMark(card, fromOpponent));
    }

    /// Local 2-player is left alone on purpose: there is a person in the room to explain, which is
    /// the same reason that mode's How to Play is short.
    public void QueueCoachMarksForModifiers()
    {
        if (!_host.VsBot) return;
        foreach (Card card in _host.Player1.Modifiers) QueueCoachMark(card, fromOpponent: false);
    }

    /// Runs from UpdateUI. Shows at most one at a time, and only when nothing else owns the
    /// screen - a card explained over the top of a set-end panel teaches nobody anything.
    public void DrainCoachMarks()
    {
        if (_coachShowing.HasValue)
        {
            Defer(RefreshSpotlight); // the highlighted control may have moved
            return;
        }

        if (Running || _coachQueue.Count == 0) return;
        if (!_host.GameStarted || _host.State.IsGameOver || _host.SetOverPending) return;
        if (_menus.Covering) return; // How to Play or the table menu is up
        if (_host.PromptShowing) return; // a Recall choice or a rescue offer owns the screen

        ShowCoachMark(_coachQueue.Dequeue());
    }

    private void ShowCoachMark(CoachMark mark)
    {
        _coachShowing = mark;

        _spotlightLabel.Text = CardEffects.Introduction(mark.Card);
        _spotlightNext.Visible = true;

        // The coach mark comes first (playtest, 2026-10-06: the effect banner - "Silver Champion
        // plays Copy..." - was drawn over the coach mark's caption). The banner steps aside while
        // the card is explained and comes back when the player taps Got it.
        _bannerHeldForCoach = _ui.Toasts.EffectBannerText;
        if (_bannerHeldForCoach != null) _ui.Toasts.ClearEffectBanner();
        _spotlightSkip.Visible = false; // there is nothing to skip: it is one line, once ever

        _root.MoveChild(_spotlightOverlay, _root.GetChildCount() - 1);
        _spotlightOverlay.Visible = true;
        // Placed now AND again once the caption has been laid out (SettleSpotlight). THE BUG
        // (S25, 2026-10-05: a white pillar the height of the screen): the caption had never been
        // laid out at this width, so the panel's minimum was the wrapped-per-word height of its
        // label, and setting its size could not go below that. This used to be placed once and
        // never again, so the pillar stayed until something else happened to refresh it.
        RefreshSpotlight();
    }

    private void DismissCoachMark()
    {
        if (!_coachShowing.HasValue) return;

        if (RunData.Instance != null && RunData.Instance.MarkCardMet(CardEffects.MetKey(_coachShowing.Value.Card)))
            _host.CollectionComplete();
        _coachShowing = null;

        if (_spotlightOverlay != null) _spotlightOverlay.Visible = false;
        if (_spotlightSkip != null) _spotlightSkip.Visible = true;

        // The banner that stepped aside for the coach mark comes back.
        if (_bannerHeldForCoach != null) _ui.Toasts.ShowEffectBanner(_bannerHeldForCoach);
        _bannerHeldForCoach = null;

        _ui.DeferRefresh(); // which drains the next one, if there is one
    }

    /// One button, two owners. The tutorial advances; a coach-mark is simply done.
    private void OnSpotlightNextPressed()
    {
        if (_coachShowing.HasValue) DismissCoachMark();
        else AdvanceTutorial();
    }
}
