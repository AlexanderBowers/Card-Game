using Godot;
using System.Collections.Generic;

/// The scores on the table: each side's own score (as a badge or a "You  17/20" line), the
/// other player's beside it, the preview of a picked-up Modifier, the box turning red over the
/// target and green exactly on it, and the padlock when a player holds.
///
/// Like the rest of the picture it decides nothing - it reads the players and the state through
/// ITableUiHost and paints the layout TableUi has up.
public sealed class ScoreDisplay
{
    private readonly ITableUiHost _host;
    private readonly TableUi _ui;

    private readonly AudioStreamPlayer _sfxLock;      // Hold: the padlock snapping shut
    private readonly AudioStreamPlayer _sfxOnTarget;  // a picked-up Modifier lands exactly on the target

    public ScoreDisplay(ITableUiHost host, TableUi ui, AudioStreamPlayer lockSound, AudioStreamPlayer onTargetSound)
    {
        _host = host;
        _ui = ui;
        _sfxLock = lockSound;
        _sfxOnTarget = onTargetSound;
    }

    private TableLayout L => _ui.Layout;
    private Player P1 => _host.Player1;
    private Player P2 => _host.Player2;
    private GameState State => _host.State;
    private bool IsMirrored => _ui.IsMirrored;
    private void Show(Control c, bool show) => _ui.Show(c, show);

    // ------------------------------------------------------------------
    // The number a score box SHOWS (2026-10-05). An effect is resolved in the model first and
    // the animation catches the picture up, so for the length of a Shave or a Copy the box keeps
    // showing the old number and ticks over at the moment of impact (TableMoments).
    // ------------------------------------------------------------------
    private readonly Dictionary<Player, int> _shownOverride = new Dictionary<Player, int>();

    private int Shown(Player player) =>
        player != null && _shownOverride.TryGetValue(player, out int value) ? value : player?.CurrentScore ?? 0;

    /// The score as written on the table. With face-down cards on the board (the hidden-card rule)
    /// it is only what the face-up cards add up to, and "+?" for the rest - "14+?" - so the player
    /// can see how much they know and that there is more they don't.
    private string ScoreText(Player player)
    {
        if (!_host.GameStarted || player == null) return "-";
        if (!player.HasHiddenCards) return Shown(player).ToString();
        return $"{Shown(player) - player.HiddenTotal}+?";
    }

    /// Keep showing `value` for this player until ReleaseShown.
    public void HoldShown(Player player, int value)
    {
        if (player != null) _shownOverride[player] = value;
    }

    /// Show the real score again, now.
    public void ReleaseShown(Player player)
    {
        if (player == null || !_shownOverride.Remove(player)) return;
        RefreshScoreLines();
        RefreshBoxes();
    }

    public void ReleaseAllShown()
    {
        if (_shownOverride.Count == 0) return;
        _shownOverride.Clear();
        RefreshScoreLines();
        RefreshBoxes();
    }

    /// The box holding this player's own score, and its padlock (for the animations to aim at).
    public Control ScoreBoxOf(Player player) => player == P1 ? L?.P1ScoreBox : player == P2 ? L?.P2ScoreBox : null;

    public Control PadlockOf(Player player)
    {
        Control box = ScoreBoxOf(player);
        return box != null && _holdLocks.TryGetValue(box, out Control lockNode) && GodotObject.IsInstanceValid(lockNode)
            ? lockNode : null;
    }

    /// A freshly bound layout: forget the old one's boxes and locks, and set up its badges.
    public void Bind()
    {
        ResetScoreFeedback();
        if (L.P1ScoreThem != null) L.P1ScoreThem.Visible = false; // the box is one line now
        if (L.P2ScoreThem != null) L.P2ScoreThem.Visible = false;
    }

    /// Everything after the score lines: the opponent's score, red/green boxes and the padlocks.
    public void RefreshBoxes()
    {
        RefreshOpponentLines();
        ApplyScoreDanger(L.P1ScoreBox, P1, preview: true);
        ApplyScoreDanger(L.P2ScoreBox, P2, preview: true);
        ApplyScoreDanger(L.P1OpponentBox, P2); // the opponent's score, boxed and red the same way
        ApplyScoreDanger(L.P2OpponentBox, P1);
        UpdateHoldLock(L.P1ScoreBox, P1);
        UpdateHoldLock(L.P2ScoreBox, P2);
        _scoreFeedbackPrimed = true; // from here on, changes animate and make their sound
    }

    /// The score lines, in whichever form the mirror calls for.
    ///
    /// The score says whose it is and what it is chasing - "You  17/20" (playtest, 2026-09-14).
    /// The target appears once per READER: against the bot only your row carries it; in local
    /// 2-player each player reads their own row, so both do. Mirrored, each side reads from its
    /// own player's point of view ("You: 13/20").
    public void RefreshScoreLines()
    {
        if (L.ScoreBadges)
        {
            RefreshScoreBadges();
            return;
        }

        ScoreLines p1 = new ScoreLines { YouPrefix = L.P1ScorePrefix, YouValue = L.P1ScoreValue, Them = L.P1ScoreThem };
        ScoreLines p2 = new ScoreLines { YouPrefix = L.P2ScorePrefix, YouValue = L.P2ScoreValue, Them = L.P2ScoreThem };

        if (IsMirrored)
        {
            // Pass 36: the "Them" line left the box for the spot beside it (RefreshOpponentLines).
            SetScoreLines(p1, "You: ", P1, null);
            SetScoreLines(p2, "You: ", P2, null);
        }
        else if (_host.VsBot)
        {
            SetScoreLines(p1, "You  ", P1, null);
            SetScoreLines(p2, "Them  ", P2, null, preview: false, withTarget: false);
        }
        else
        {
            // Local 2-player, not mirrored: each side is still read by its own player, so it
            // speaks to them - "You" / "Them", never "P1" / "P2" (playtest, pass 42).
            SetScoreLines(p1, "You  ", P1, null);
            SetScoreLines(p2, "You  ", P2, null);
        }
    }

    /// The other player's score, in the spot beside each player's own (pass 36). Always shown to
    /// the person reading that side; against the bot the bot's side has no reader, so it is
    /// left empty there rather than repeating your score across the table.
    private void RefreshOpponentLines()
    {
        string p1 = ScoreText(P1);
        string p2 = ScoreText(P2);

        if (L.ScoreBadges)
        {
            // Pass 43: face to face, each player reads BOTH scores on their own half - their badge
            // and a "THEM" badge beside it - instead of reading the opponent's badge upside down
            // across the table. Otherwise the opponent's own badge is already the right way up.
            bool faceToFace = IsMirrored;
            string target = _host.GameStarted ? $"/{State.TargetScore}" : string.Empty;
            SetOpponentBadge(L.P1OpponentBox, L.P1OpponentScore, L.P1OpponentCaption, L.P1OpponentTarget, p2, target, faceToFace);
            SetOpponentBadge(L.P2OpponentBox, L.P2OpponentScore, L.P2OpponentCaption, L.P2OpponentTarget, p1, target, faceToFace);
            return;
        }

        if (IsMirrored)
        {
            SetText(L.P1OpponentScore, $"Them: {p2}");
            SetText(L.P2OpponentScore, $"Them: {p1}");
        }
        else if (_host.VsBot)
        {
            // Side by side against the bot, the bot's own box already reads "Them 13", the right
            // way up and level with yours: a second "Them" under "You" said it twice (playtest,
            // 2026-10-06).
            SetText(L.P1OpponentScore, string.Empty);
            SetText(L.P2OpponentScore, string.Empty);
        }
        else
        {
            SetText(L.P1OpponentScore, $"Them  {p2}");
            SetText(L.P2OpponentScore, $"Them  {p1}");
        }
    }

    private void SetOpponentBadge(Control box, Label value, Label caption, Label target,
                                  string score, string targetText, bool show)
    {
        Show(box, show);
        if (value != null) value.Text = score;
        if (caption != null) caption.Text = "THEM";
        if (target != null)
        {
            target.Text = targetText;
            target.Visible = targetText.Length > 0;
        }
    }

    private void SetText(Label label, string text)
    {
        if (label == null) return;
        label.Text = text;
        // An empty line hides its box too (the bot's side, against the bot).
        Control box = label == L.P1OpponentScore ? L.P1OpponentBox : label == L.P2OpponentScore ? L.P2OpponentBox : null;
        Show(box, !string.IsNullOrEmpty(text));
    }

    // ------------------------------------------------------------------
    // Over the target: the score box turns red (pass 36), in place of the "Over target!" and
    // "Bust!" words. Red while the score is over - a warning mid-turn (play a minus Modifier
    // before ending it), a bust once the turn is over.
    // ------------------------------------------------------------------
    private readonly Dictionary<Control, StyleBox> _authoredBoxStyle = new Dictionary<Control, StyleBox>();
    private readonly Dictionary<Control, StyleBox> _dangerBoxStyle = new Dictionary<Control, StyleBox>();

    private static readonly Color DangerFill = new Color(0.5f, 0.06f, 0.08f, 0.88f);
    private static readonly Color DangerEdge = new Color(1f, 0.45f, 0.42f, 0.95f);

    private void ApplyScoreDanger(Control box, Player player, bool preview = false)
    {
        if (box is not PanelContainer panel || player == null) return;

        if (!_authoredBoxStyle.TryGetValue(box, out StyleBox authored))
        {
            // Whatever the scene gave the box is its normal look; red is made from a copy of it,
            // so a restyle in the editor carries over to the red version too.
            authored = panel.GetThemeStylebox("panel");
            _authoredBoxStyle[box] = authored;
            StyleBox danger = (StyleBox)authored?.Duplicate();
            if (danger is StyleBoxFlat flat)
            {
                flat.BgColor = DangerFill;
                flat.BorderColor = DangerEdge;
            }
            _dangerBoxStyle[box] = danger;

            // On target (release playtest, 2026-09-29): green, with a soft glow round it.
            StyleBox onTargetStyle = (StyleBox)authored?.Duplicate();
            if (onTargetStyle is StyleBoxFlat green)
            {
                green.BgColor = OnTargetFill;
                green.BorderColor = OnTargetEdge;
                green.ShadowColor = OnTargetGlow;
                green.ShadowSize = 14;
            }
            _onTargetBoxStyle[box] = onTargetStyle;
        }

        // Follows the number the box is SHOWING: with a Modifier picked up that is the preview, so
        // a minus card that brings you back under the target clears the red along with the digits.
        // The opponent boxes show the real score, so they never use the preview.
        int? previewed = preview ? PreviewedScore(player) : null;
        int shown = previewed ?? Shown(player);
        // A score with face-down cards in it is neither red nor green: either would tell the
        // player what the hidden cards add up to.
        bool started = _host.GameStarted && !player.HasHiddenCards;
        bool over = started && shown > State.TargetScore;
        bool onTarget = started && previewed.HasValue && previewed.Value == State.TargetScore;

        StyleBox want = over ? _dangerBoxStyle[box] : onTarget ? _onTargetBoxStyle[box] : authored;
        if (want != null) panel.AddThemeStyleboxOverride("panel", want);

        bool was = _onTargetShown.TryGetValue(box, out bool w) && w;
        _onTargetShown[box] = onTarget;
        if (onTarget && !was && _scoreFeedbackPrimed) CelebrateOnTarget(box);
    }

    // ------------------------------------------------------------------
    // Feedback on the score box (release playtest, 2026-09-29)
    //
    // On target: a picked-up Modifier that would land you EXACTLY on the target turns the box
    // green with a glow, pulses it once and plays a light chime - the "yes, that one" moment.
    //
    // Hold: a padlock snaps onto the box's corner with a click and the box dims a little. It stays
    // until the set ends. It replaces the "Holding" toast, which read as an afterthought.
    // ------------------------------------------------------------------
    private readonly Dictionary<Control, StyleBox> _onTargetBoxStyle = new Dictionary<Control, StyleBox>();
    private readonly Dictionary<Control, bool> _onTargetShown = new Dictionary<Control, bool>();
    private readonly Dictionary<Control, Control> _holdLocks = new Dictionary<Control, Control>();

    /// False straight after a layout is bound (first launch, or a rotation mid-set): whatever is
    /// already true is drawn as it is, without replaying the click or the chime.
    private bool _scoreFeedbackPrimed;

    private static readonly Color OnTargetFill = new Color(0.07f, 0.36f, 0.20f, 0.92f);
    private static readonly Color OnTargetEdge = new Color(0.55f, 1.00f, 0.68f, 1.00f);
    private static readonly Color OnTargetGlow = new Color(0.40f, 1.00f, 0.55f, 0.55f);
    private static readonly Color PreviewOnTargetColor = new Color(0.72f, 1.00f, 0.78f);

    private static readonly Color LockGold = new Color(0.96f, 0.78f, 0.30f);
    private static readonly Color LockShade = new Color(0.55f, 0.40f, 0.10f);
    private static readonly Color HeldDim = new Color(0.78f, 0.78f, 0.82f);
    private static readonly Vector2 LockSize = new Vector2(40f, 50f);

    private void ResetScoreFeedback()
    {
        _scoreFeedbackPrimed = false;
        _onTargetShown.Clear();
        _onTargetBoxStyle.Clear();
        _authoredBoxStyle.Clear();
        _dangerBoxStyle.Clear();
        _holdLocks.Clear(); // the old layout's locks went with its nodes
    }

    private void CelebrateOnTarget(Control box)
    {
        _sfxOnTarget?.Play();
        box.PivotOffset = box.Size / 2f;
        Tween pulse = box.CreateTween();
        pulse.TweenProperty(box, "scale", new Vector2(1.12f, 1.12f), 0.10f)
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        pulse.TweenProperty(box, "scale", Vector2.One, 0.20f)
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
    }

    private void UpdateHoldLock(Control box, Player player)
    {
        if (box == null || player == null || box.GetParent() is not Control parent) return;
        bool holding = _host.GameStarted && player.IsHolding && !State.IsGameOver;

        if (!_holdLocks.TryGetValue(box, out Control padlock) || !GodotObject.IsInstanceValid(padlock))
        {
            padlock = BuildPadlock();
            parent.AddChild(padlock);
            parent.MoveChild(padlock, box.GetIndex() + 1); // drawn over the box, under the rest
            _holdLocks[box] = padlock;
        }

        // The box's top-right corner, overlapping it: the lock is ON the score, not beside it.
        padlock.Position = box.Position + new Vector2(box.Size.X - LockSize.X * 0.6f, -LockSize.Y * 0.4f);
        box.Modulate = holding ? HeldDim : Colors.White;

        if (holding == padlock.Visible) return;
        padlock.Visible = holding;
        padlock.Modulate = Colors.White;
        padlock.Scale = Vector2.One;
        if (!holding || !_scoreFeedbackPrimed) return;

        // 2026-10-05: the corner badge alone was easy to miss when the BOT held. A big padlock
        // drops onto the score, locks with the click, and shrinks into this badge - the same for
        // both sides, so seeing your own teaches you to recognise the bot's. The badge stays
        // invisible until the big one arrives in it.
        padlock.Modulate = new Color(1, 1, 1, 0);
        Player holder = box == L.P1ScoreBox ? P1 : P2;
        Control board = holder == P1 ? _ui.P1Board : _ui.P2Board;
        _ui.Moments.PlayHold(box, padlock, board, BuildPadlock, LockSize, _sfxLock);
    }

    /// A padlock from three flat shapes - a shackle (an arch of border only), a body and a
    /// keyhole - so it needs no art and stays crisp at any scale. Swap for a texture later if
    /// the art pass wants one.
    private static Control BuildPadlock()
    {
        Control root = new Control
        {
            Name = "HoldLock",
            Size = LockSize,
            CustomMinimumSize = LockSize,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };

        StyleBoxFlat shackleStyle = new StyleBoxFlat { DrawCenter = false, BorderColor = LockShade };
        shackleStyle.BorderWidthLeft = shackleStyle.BorderWidthRight = shackleStyle.BorderWidthTop = 6;
        shackleStyle.BorderWidthBottom = 0;
        shackleStyle.CornerRadiusTopLeft = shackleStyle.CornerRadiusTopRight = 13;
        Panel shackle = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        shackle.AddThemeStyleboxOverride("panel", shackleStyle);
        shackle.Position = new Vector2(8f, 0f);
        shackle.Size = new Vector2(24f, 26f);
        root.AddChild(shackle);

        StyleBoxFlat bodyStyle = new StyleBoxFlat
        {
            BgColor = LockGold,
            BorderColor = LockShade,
            ShadowColor = new Color(0, 0, 0, 0.45f),
            ShadowSize = 4,
        };
        bodyStyle.SetBorderWidthAll(2);
        bodyStyle.SetCornerRadiusAll(7);
        Panel body = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        body.AddThemeStyleboxOverride("panel", bodyStyle);
        body.Position = new Vector2(0f, 20f);
        body.Size = new Vector2(40f, 30f);
        root.AddChild(body);

        StyleBoxFlat holeStyle = new StyleBoxFlat { BgColor = LockShade };
        holeStyle.SetCornerRadiusAll(4);
        Panel keyhole = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        keyhole.AddThemeStyleboxOverride("panel", holeStyle);
        keyhole.Position = new Vector2(16f, 28f);
        keyhole.Size = new Vector2(8f, 14f);
        root.AddChild(keyhole);

        return root;
    }


    /// The three labels of one side's score, as the layout scene has them.
    private sealed class ScoreLines
    {
        public Label YouPrefix;
        public Label YouValue;
        public Label Them;
    }


    /// Blue: the picked-up Modifier keeps you at or under the target. Orange: it would take you over.
    private static readonly Color PreviewUnderColor = new Color(0.45f, 0.72f, 1.00f);

    private static readonly Color PreviewOverColor = new Color(1.00f, 0.55f, 0.30f);

    /// Exactly on the target is its own colour, matching the green box around it.
    private Color PreviewColorFor(int value) =>
        value > State.TargetScore ? PreviewOverColor
        : value == State.TargetScore ? PreviewOnTargetColor
        : PreviewUnderColor;

    // ------------------------------------------------------------------
    // Score badges (pass 40, portrait)
    //
    // Each side's score sits in a badge beside its own board, Pokemon TCG Pocket-style, rather
    // than in a "You  11/20" box in a row of its own. Face to face, the player across the table
    // reads it from the THEM badge on their own half (RefreshOpponentLines); the turned-round far
    // end that used to do that job (pass 40) was retired in pass 43 and removed in pass 66.
    // ------------------------------------------------------------------
    private void RefreshScoreBadges()
    {
        bool bothRead = !_host.VsBot; // the bot's badge has no reader of its own: no target on it
        SetScoreBadge(P1, L.P1ScoreValue, L.P1ScoreTarget, true, true);
        SetScoreBadge(P2, L.P2ScoreValue, L.P2ScoreTarget, bothRead, bothRead);

        // Pass 42: a bare number beside a board still read as ambiguous in playtest, so each badge
        // says whose score it is to the person reading it.
        // Pass 43: Player 2's badge only says YOU face to face, when Player 2 reads it. Without the
        // mirror the whole table is read from Player 1's end, so it is THEM - "YOU" on both
        // badges was the confusing case in playtest.
        SetCaption(L.P1ScoreCaption, "YOU");
        SetCaption(L.P2ScoreCaption, IsMirrored ? "YOU" : "THEM");
    }

    private static void SetCaption(Label caption, string text)
    {
        if (caption != null) caption.Text = text;
    }

    private void SetScoreBadge(Player player, Label value, Label target, bool preview, bool withTarget)
    {
        if (player == null) return;
        bool started = _host.GameStarted;
        string targetText = started && withTarget ? $"/{State.TargetScore}" : string.Empty;
        string scoreText = ScoreText(player);

        if (value != null)
        {
            int? previewed = preview ? PreviewedScore(player) : null;
            if (previewed.HasValue && started)
            {
                value.Text = previewed.Value.ToString();
                value.AddThemeColorOverride("font_color", PreviewColorFor(previewed.Value));
            }
            else
            {
                value.Text = scoreText;
                value.RemoveThemeColorOverride("font_color");
            }
        }
        if (target != null)
        {
            target.Text = targetText;
            target.Visible = targetText.Length > 0;
        }
    }

    /// Face to face, each player gets a "THEM" badge stacked on their own (RefreshOpponentLines).
    /// Each badge hugs its content from its anchored edge.
    public void ApplyScoreBadgeEnds(bool faceToFace)
    {
        if (!L.ScoreBadges) return;
        HugContent(L.P1ScoreBox);
        HugContent(L.P2ScoreBox);
        StackBadgePair(L.P1ScoreBox, L.P1OpponentBox, faceToFace);
        StackBadgePair(L.P2ScoreBox, L.P2OpponentBox, faceToFace);
        HugContent(L.P1OpponentBox);
        HugContent(L.P2OpponentBox);
        Show(L.P1OpponentBox, faceToFace);
        Show(L.P2OpponentBox, faceToFace);
    }

    // ------------------------------------------------------------------
    // YOU / THEM, stacked (pass 45)
    //
    // Face to face, each half shows its player's YOU badge with a THEM badge directly above it,
    // in the column left of the board. Pass 44 tried going side by side on wide screens; in
    // playtest stacked read better everywhere, so it is stacked always.
    //
    // THEM is placed from YOU's size rather than a fixed spot in the scene, so the pair stays
    // snug whatever the badge fonts are - move or resize the YOU badge in the editor and THEM
    // follows it. THEM keeps its own authored rect when it is hidden.
    // ------------------------------------------------------------------
    private const float BadgeGap = 10f;

    private static void StackBadgePair(Control you, Control them, bool faceToFace)
    {
        if (you == null || them == null || !faceToFace) return;
        float youHeight = you.GetCombinedMinimumSize().Y;
        float bottom = you.OffsetBottom - youHeight - BadgeGap;
        them.OffsetLeft = you.OffsetLeft;
        them.OffsetRight = you.OffsetRight;
        them.OffsetBottom = bottom;
        them.OffsetTop = bottom - 1f;
    }

    /// Shrinks a free-standing box to its content, keeping the edge it grows away from.
    private static void HugContent(Control box)
    {
        if (box == null) return;
        if (box.GrowVertical == Control.GrowDirection.Begin) box.OffsetTop = box.OffsetBottom - 1f;
        else if (box.GrowVertical == Control.GrowDirection.End) box.OffsetBottom = box.OffsetTop + 1f;
    }

    private void SetScoreLines(ScoreLines lines, string prefix, Player player,
                               string themLine, bool preview = true, bool withTarget = true)
    {
        if (lines?.YouPrefix == null || lines.YouValue == null) return;

        lines.YouPrefix.Text = prefix;

        int? previewed = preview ? PreviewedScore(player) : null;
        if (previewed.HasValue)
        {
            int value = previewed.Value;
            lines.YouValue.Text = _host.GameStarted ? $"{value}/{State.TargetScore}" : "-";
            lines.YouValue.AddThemeColorOverride("font_color", PreviewColorFor(value));
        }
        else
        {
            lines.YouValue.Text = withTarget ? ScoreOf(player) : ScoreText(player);
            lines.YouValue.RemoveThemeColorOverride("font_color");
        }

        if (lines.Them != null)
        {
            lines.Them.Visible = themLine != null;
            lines.Them.Text = themLine ?? string.Empty;
        }
    }

    /// The score a picked-up plain Modifier would give, or null when nothing like that is picked
    /// up. Effect cards and a just-recalled card explain themselves on the status line instead.
    private int? PreviewedScore(Player player)
    {
        Card picked = _host.SelectedFor(player);
        if (picked == null || picked.Effect != CardEffect.None || _host.Table.IsRecallLocked(player, picked)) return null;
        return Shown(player) + picked.Value;
    }

    /// A player's score with the target behind it - "17/20" - so "how close am I" is one glance
    /// rather than arithmetic against a number somewhere else on the screen.
    private string ScoreOf(Player player) =>
        _host.GameStarted ? $"{ScoreText(player)}/{State.TargetScore}" : "-";
}
