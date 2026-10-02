using Godot;
using System;
using System.Collections.Generic;

/// What a card LOOKS like. Given a Card and a size, this makes the view that shows it - the
/// pre-rendered face when there is one, otherwise the drawn face (tinted art, numbers, pips,
/// the +/- split) - and gives it its shine.
///
/// It knows nothing about boards, hands or the layout: TableUi decides where a view goes and how
/// big it is, CardMotion flies it there. The shop, the deck screen and the prompts ask for views
/// too, which is why this is its own class rather than a corner of the table.
public sealed class CardViews
{
    /// Is a ladder run on the table? A rank's deck and art only apply inside one.
    private readonly Func<bool> _inRun;

    private readonly PackedScene _cardViewScene = GD.Load<PackedScene>("res://CardView.tscn");

    public CardViews(Func<bool> inRun)
    {
        _inRun = inRun;
        _faceMain = GD.Load<Texture2D>(ArtDir + "card_main.png");
        _facePlus = GD.Load<Texture2D>(ArtDir + "card_plus.png");
        _faceMinus = GD.Load<Texture2D>(ArtDir + "card_minus.png");
        _faceFlip = GD.Load<Texture2D>(ArtDir + "card_flip.png");
        _faceEffect = GD.Load<Texture2D>(ArtDir + "card_foil.png");
        _cardBack = GD.Load<Texture2D>(ArtDir + "card_back.png");
        _shineShader = GD.Load<Shader>("res://card_shine.gdshader");
    }

    /// The plain face-down back, for a deck with no art of its own and the card that flies from it.
    public Texture2D CardBack => _cardBack;

    /// Tint applied to the standard (main deck) card art for the rank in play. TableUi sets it
    /// with the rest of the rank's theme (ApplyRankTheme).
    public Color RankCardTint { get; set; } = Colors.White;

    /// A rescue card is not one you own, so it is washed mint wherever it appears. The rescue
    /// OFFER is monetisation and lives in GameManager; this is only its colour.
    public static readonly Color RescueTint = new Color(0.7f, 1.3f, 1.05f);

    private Shader _shineShader;

    public const string ArtDir = "res://assets/aimfor20_art/";

    private Texture2D _faceMain;     // green  - main-deck cards
    private Texture2D _facePlus;     // blue   - positive modifiers
    private Texture2D _faceMinus;    // red    - negative modifiers
    private Texture2D _faceFlip;     // violet - "+/-" (Flip Value) modifiers
    private Texture2D _faceEffect;   // foil   - effect cards (Copy, Shave, Veto, ...)
    private Texture2D _cardBack;     // the face-down deck, and the card that flies from it

    /// A "+/-" card wears its own violet face, split into a +n half and a -n half.
    private const bool FlipCardsUseOwnFace = true;

    // The faces are pale, so the numbers and pips are drawn in a dark ink of the face's own hue.
    private static readonly Color InkMain = new Color(0.12f, 0.42f, 0.27f);
    private static readonly Color InkPlus = new Color(0.13f, 0.33f, 0.66f);
    private static readonly Color InkMinus = new Color(0.68f, 0.17f, 0.22f);
    public static readonly Color InkFlip = new Color(0.37f, 0.24f, 0.62f);
    private static readonly Color InkEffect = new Color(0.45f, 0.29f, 0.05f);

    /// Effect cards used to wear a violet wash over a green back; the foil face replaces it.
    private static readonly Color EffectTint = Colors.White;

    // ------------------------------------------------------------------
    // Art drop-ins (ART_PIPELINE.md)
    //
    // Finished art is picked up by FILE NAME, so a render from Blender/Krita goes live by being
    // saved in the right place - no code change. Anything missing falls back to what the game
    // draws today (the tinted shared face, the text mark), so art can arrive one piece at a time.
    // ------------------------------------------------------------------
    private readonly Dictionary<string, Texture2D> _artCache = new Dictionary<string, Texture2D>();

    /// The texture at assets/aimfor20_art/<relative>, or null if it has not been made yet.
    public Texture2D Art(string relative)
    {
        if (_artCache.TryGetValue(relative, out Texture2D cached)) return cached;
        string path = ArtDir + relative;
        Texture2D tex = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
        // Only a hit is remembered: a miss (an image the editor has not finished importing) is
        // asked again next time, rather than leaving that card on its fallback face all session.
        if (tex != null) _artCache[relative] = tex;
        return tex;
    }

    /// "bronze", "silver", ... for the rank in play, or null outside a run.
    /// Endless mode has its own set (pass 60): a night-sky table and an iridescent opponent deck,
    /// rather than borrowing the Obsidian stage it is parked on.
    public string RankKey =>
        !_inRun() || RunData.Instance == null ? null
        : RunData.Instance.Endless ? "endless"
        : RunData.Instance.CurrentRank.Name.ToLowerInvariant();

    private Texture2D RankArt(string prefix) => RankKey == null ? null : Art($"{prefix}_{RankKey}.png");

    /// An effect card's own illustrated face: cards/effect_copy.png, cards/effect_tradetotals.png...
    private Texture2D EffectArt(CardEffect effect) => Art($"cards/effect_{effect.ToString().ToLowerInvariant()}.png");

    private Texture2D FaceFor(Card card)
    {
        if (card.Type == CardType.Main) return RankArt("cards/card_main") ?? _faceMain;
        // Before the sign test: a Shave carries Value 1 and would otherwise wear the blue "plus"
        // face, which is the opposite of what it does.
        if (card.Effect != CardEffect.None) return EffectArt(card.Effect) ?? _faceEffect;
        if (card.IsRescue) return Art("cards/rescue.png") ?? (card.Value < 0 ? _faceMinus : _facePlus);
        if (card.Value < 0) return _faceMinus;
        return _facePlus;
    }

    private static Color InkFor(Card card)
    {
        if (card.Type == CardType.Main) return InkMain;
        if (card.Effect != CardEffect.None) return InkEffect;
        return card.Value < 0 ? InkMinus : InkPlus;
    }

    /// Colours every number on a card view, and remembers the ink so BuildPips can match it.
    private static void ApplyInk(TextureRect view, Color ink)
    {
        view.SetMeta("ink", ink);
        foreach (string name in CardLabelNames)
        {
            Label label = view.GetNodeOrNull<Label>(name);
            if (label != null) label.AddThemeColorOverride("font_color", ink);
        }
    }


    /// Pips in the middle of a main-deck card, or the plain big number. Flip this and look at it
    /// on the phone: at 84x114 ten dots may read as texture rather than as a number, and that is a
    /// question for a screen rather than for a spec (claude/playtest-feedback-family.md).
    private const bool PipsOnMainCards = true;

    // Pass 23 (Alexander, 2026-09-17: "cards and all other text need to be MUCH bigger"). These
    // cost the layout NOTHING - the number is drawn inside a card that is already that size - so
    // they are the one place readability is free, and they are pushed as far as the card face
    // takes before a two-digit number runs into the border.
    // Pass 25 splits the corner in two. On a MAIN-DECK card the pips are the middle and the
    // corner number is the only text there is, so it can be huge; on a Modifier the corner sits
    // under a big centre number and is a second reading of it, so it stays a corner.
    // "Reduce size of center icons if necessary to fit larger text" (Alexander, 2026-09-17) - so
    // the pips gave way, here and in BuildPips.
    private const float CornerFontScale = 0.34f;        // a card that also shows a centre number

    private const float PippedCornerFontScale = 0.44f;  // a main-deck card: the corner IS the number

    private const float CentreFontScale = 0.62f;     // was 0.58

    private const float FlipHalfFontScale = 0.52f;   // was 0.50 - two numbers, half a card each

    private const float EffectFontScale = 0.34f;     // was 0.32 - a mark ("->+4"), not a digit

    private static readonly string[] CardLabelNames = { "Label", "LabelMinus", "CornerTL", "CornerBR" };

    private static readonly string[] CardCornerNames = { "CornerTL", "CornerBR" };

    public void ApplyCardSize(TextureRect view, Vector2 size)
    {
        view.CustomMinimumSize = size;

        // A pre-rendered face already carries its numbers, pips and signs: nothing is drawn on it.
        if (view.HasMeta(PrerenderedMeta))
        {
            foreach (string name in CardLabelNames)
            {
                Label painted = view.GetNodeOrNull<Label>(name);
                if (painted != null) painted.Visible = false;
            }
            return;
        }

        // A "+/-" card is always drawn two-way - blue +n above, red -n below - because that split
        // IS how you tell it from an ordinary modifier. Nothing else uses the lower label now.
        bool flipValueFace = view.HasNode("FlipValueBottom");
        bool pipped = view.HasMeta("pips");
        // Release playtest (2026-09-29): a Modifier's middle is its SIGN drawn as a shape, with
        // pips for the amount - the big centre number was too big. The corners keep the number.
        bool signed = view.HasMeta(SignedPipsMeta);

        // An effect card's face is a mark rather than one number ("->+4", "-1"), so it needs a
        // smaller font than a card showing a single digit or two.
        // All three grew in pass 22 ("card texts need to be bigger", Alexander, 2026-09-16).
        float fontScale = view.HasMeta("effectCard") ? EffectFontScale
                        : (flipValueFace ? FlipHalfFontScale : CentreFontScale);
        int fontSize = Mathf.RoundToInt(size.Y * fontScale);

        Label label = view.GetNodeOrNull<Label>("Label");
        if (label != null)
        {
            label.AddThemeFontSizeOverride("font_size", fontSize);
            label.AnchorBottom = flipValueFace ? 0.5f : 1f; // top half, or the whole card
            label.Visible = !pipped && !signed;        // the pips ARE the number when they are on
        }

        Label flipped = view.GetNodeOrNull<Label>("LabelMinus");
        if (flipped != null)
        {
            flipped.AddThemeFontSizeOverride("font_size", fontSize);
            flipped.Visible = flipValueFace && !signed;
            flipped.RotationDegrees = 0f; // upright for its owner; the CORNERS face the other way
        }

        // Corners, EXCEPT on a "+/-" card. That card already carries two numbers - +n over -n,
        // one per half - and it is already readable from both sides of the table because of it.
        // Adding corners put four numbers on one card and ran them into the borders
        // (Alexander, S25 Ultra, 2026-09-15). The halves are the two-way reading there.
        //
        int cornerFont = Mathf.Max(10, Mathf.RoundToInt(
            size.Y * (pipped ? PippedCornerFontScale : signed ? SignedCornerFontScale : CornerFontScale)));
        foreach (string name in CardCornerNames)
        {
            Label corner = view.GetNodeOrNull<Label>(name);
            if (corner == null) continue;
            corner.Visible = !flipValueFace;
            corner.AddThemeFontSizeOverride("font_size", cornerFont);
        }

        // A pip is a Panel with a fully rounded StyleBoxFlat, and a corner radius is an integer
        // number of pixels - so a resized dot has to be re-made rather than scaled to stay round.
        if (pipped) BuildPips(view, view.GetMeta("pips").AsInt32(), size);
        if (signed) BuildSignedFace(view, size, flipValueFace);
    }

    // ------------------------------------------------------------------
    // Signed faces (release playtest, 2026-09-29)
    //
    // "+ and - cards need the dots. Keep the + or - in the centre, with dots instead of numbers."
    // A plain Modifier shows its sign as a shape in the upper middle and its amount as pips under
    // it. A "+/-" card does the same in each half: the sign on the left, the pips on the right,
    // blue + over red -. The corners still carry the number on a plain Modifier.
    //
    // The sign is drawn from bars rather than typed, so it matches the pips (same ink, same
    // rounded ends) and never depends on the font having a proper minus.
    // ------------------------------------------------------------------
    private const string SignedPipsMeta = "signedPips";

    private const float SignedCornerFontScale = 0.36f;

    private static void BuildSignedFace(TextureRect view, Vector2 size, bool flipValueFace)
    {
        int count = view.GetMeta(SignedPipsMeta).AsInt32();
        foreach (string old in new[] { "Pips", "PipsTop", "PipsBottom", "Sign", "SignTop", "SignBottom" })
        {
            Node existing = view.GetNodeOrNull(old);
            if (existing == null) continue;
            view.RemoveChild(existing);
            existing.QueueFree();
        }

        if (!flipValueFace)
        {
            Color ink = view.HasMeta("ink") ? view.GetMeta("ink").AsColor() : InkMain;
            bool plus = !view.HasMeta("signNegative");
            BuildSign(view, "Sign", plus, new Rect2(0.30f, 0.20f, 0.40f, 0.26f), size.Y * 0.20f, ink);
            BuildPips(view, count, size, new Rect2(0.28f, 0.50f, 0.44f, 0.34f), "Pips", ink);
            return;
        }

        // Each half follows the modulate its number label was given (the half not in play is dimmed).
        Color topTint = view.GetNodeOrNull<Label>("Label")?.Modulate ?? Colors.White;
        Color bottomTint = view.GetNodeOrNull<Label>("LabelMinus")?.Modulate ?? Colors.White;
        float half = size.Y * 0.16f;
        Control a = BuildSign(view, "SignTop", true, new Rect2(0.08f, 0.06f, 0.36f, 0.40f), half, InkPlus);
        Control b = BuildPips(view, count, size, new Rect2(0.46f, 0.08f, 0.44f, 0.36f), "PipsTop", InkPlus);
        Control c = BuildSign(view, "SignBottom", false, new Rect2(0.08f, 0.54f, 0.36f, 0.40f), half, InkMinus);
        Control d = BuildPips(view, count, size, new Rect2(0.46f, 0.56f, 0.44f, 0.36f), "PipsBottom", InkMinus);
        foreach (Control top in new[] { a, b }) if (top != null) top.Modulate = topTint;
        foreach (Control bottom in new[] { c, d }) if (bottom != null) bottom.Modulate = bottomTint;
    }

    /// A + or - made of rounded bars, centred in the given anchor rect of the card.
    private static Control BuildSign(TextureRect view, string name, bool plus, Rect2 anchors, float length, Color ink)
    {
        Control host = new Control { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore };
        view.AddChild(host);
        host.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        host.AnchorLeft = anchors.Position.X;
        host.AnchorTop = anchors.Position.Y;
        host.AnchorRight = anchors.End.X;
        host.AnchorBottom = anchors.End.Y;

        CenterContainer centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        host.AddChild(centre);
        centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        float l = Mathf.Max(8f, length);
        float t = Mathf.Max(3f, Mathf.Round(l * 0.26f));
        Control glyph = new Control { CustomMinimumSize = new Vector2(l, l), MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.AddChild(glyph);

        StyleBoxFlat bar = new StyleBoxFlat { BgColor = ink };
        bar.SetCornerRadiusAll(Mathf.Max(1, Mathf.RoundToInt(t / 2f)));

        Panel across = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        across.AddThemeStyleboxOverride("panel", bar);
        across.Position = new Vector2(0f, (l - t) / 2f);
        across.Size = new Vector2(l, t);
        glyph.AddChild(across);

        if (plus)
        {
            Panel up = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            up.AddThemeStyleboxOverride("panel", bar);
            up.Position = new Vector2((l - t) / 2f, 0f);
            up.Size = new Vector2(t, l);
            glyph.AddChild(up);
        }
        return host;
    }

    /// Marks a Modifier view for the signed face. Plain +n/-n and "+/-" cards only: a main card
    /// has its own pips, and an effect card's face is a mark rather than an amount.
    private static void MarkSignedFace(TextureRect view, Card card)
    {
        int magnitude = Mathf.Abs(card.Value);
        if (card.Type == CardType.Main || card.Effect != CardEffect.None || magnitude < 1 || magnitude > 10)
        {
            if (view.HasMeta(SignedPipsMeta)) view.RemoveMeta(SignedPipsMeta);
            return;
        }
        view.SetMeta(SignedPipsMeta, magnitude);
        if (card.Value < 0) view.SetMeta("signNegative", true);
        else if (view.HasMeta("signNegative")) view.RemoveMeta("signNegative");
    }

    /// The dots in the middle of a main-deck card, in two columns the way a real card lays them
    /// out: ceil(n/2) rows of two, with a single centred dot on the last row when n is odd.
    /// Where a main-deck card's pips go. Narrower and shorter than it was: the corner numbers
    /// grew into the space this used to take, and a pip overlapping a digit is worse than a
    /// smaller pip.
    private static readonly Rect2 MainPipArea = new Rect2(0.30f, 0.26f, 0.40f, 0.48f);

    private static Control BuildPips(TextureRect view, int count, Vector2 size,
                                     Rect2? area = null, string name = "Pips", Color? inkOverride = null)
    {
        Control existing = view.GetNodeOrNull<Control>(name);
        if (existing != null)
        {
            view.RemoveChild(existing);
            existing.QueueFree();
        }
        if (count < 1 || count > 10) return null;

        Rect2 r = area ?? MainPipArea;
        Control host = new Control { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore };
        view.AddChild(host);
        host.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        host.AnchorLeft = r.Position.X;
        host.AnchorRight = r.End.X;
        host.AnchorTop = r.Position.Y;
        host.AnchorBottom = r.End.Y;

        float dot = Mathf.Max(4f, size.Y * 0.058f); // pass 25: the corner number is the number now
        int gap = Mathf.Max(2, Mathf.RoundToInt(dot * 0.7f));

        VBoxContainer rows = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        rows.AddThemeConstantOverride("separation", gap);
        host.AddChild(rows);
        rows.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        Color ink = inkOverride ?? (view.HasMeta("ink") ? view.GetMeta("ink").AsColor() : InkMain);
        StyleBoxFlat pip = new StyleBoxFlat { BgColor = ink };
        pip.SetCornerRadiusAll(Mathf.Max(2, Mathf.RoundToInt(dot / 2f)));

        for (int remaining = count; remaining > 0; )
        {
            int inRow = Mathf.Min(2, remaining);
            remaining -= inRow;

            HBoxContainer row = new HBoxContainer
            {
                Alignment = BoxContainer.AlignmentMode.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            row.AddThemeConstantOverride("separation", gap);
            rows.AddChild(row);

            for (int i = 0; i < inRow; i++)
            {
                Panel dotNode = new Panel
                {
                    CustomMinimumSize = new Vector2(dot, dot),
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                dotNode.AddThemeStyleboxOverride("panel", pip);
                row.AddChild(dotNode);
            }
        }
        return host;
    }

    /// Half the card's colour when that orientation is not the one currently chosen.
    private static readonly Color DimmedHalf = new Color(0.58f, 0.58f, 0.62f);

    /// Draws a "+/-" card as two halves: a blue top reading +n and a red bottom reading -n
    /// (Alexander, 2026-09-07). The bright half is the orientation the card is currently set to,
    /// so tapping +/- visibly moves the card from one half to the other.
    ///
    /// The red half is the same Kenney card art clipped to the bottom of the frame rather than a
    /// flat rectangle, so the border and the rounded corners still line up.
    private void BuildFlipValueFace(TextureRect view, Card card, Vector2 size)
    {
        // The top half is what shows of this one: violet, or blue in the pass-7 look.
        view.Texture = FlipCardsUseOwnFace ? _faceFlip : _facePlus;

        Control bottom = new Control
        {
            Name = "FlipValueBottom",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        view.AddChild(bottom);
        view.MoveChild(bottom, 0); // behind the two numbers, in front of the blue art
        bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        bottom.AnchorTop = 0.5f;
        bottom.OffsetTop = 0f;

        TextureRect red = new TextureRect
        {
            Name = "FlipValueBottomArt",
            Texture = FlipCardsUseOwnFace ? _faceFlip : _faceMinus,
            ExpandMode = view.ExpandMode,
            StretchMode = view.StretchMode,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bottom.AddChild(red);
        red.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Grow the red art back up over the hidden half so it is a whole card, clipped to this
        // one. Re-run on every resize, because the card is re-sized on rotation.
        red.OffsetTop = -size.Y * 0.5f;
        bottom.Resized += () => red.OffsetTop = -bottom.Size.Y;

        int magnitude = Mathf.Abs(card.Value);
        bool plusChosen = card.Value >= 0;
        int fontSize = Mathf.RoundToInt(size.Y * FlipHalfFontScale);

        Label top = view.GetNodeOrNull<Label>("Label");
        if (top != null)
        {
            top.Text = "+" + magnitude;
            top.AddThemeColorOverride("font_color", InkPlus);
            top.AnchorBottom = 0.5f;
            top.AddThemeFontSizeOverride("font_size", fontSize);
            top.Modulate = plusChosen ? Colors.White : DimmedHalf;
        }

        Label under = view.GetNodeOrNull<Label>("LabelMinus");
        if (under != null)
        {
            under.Text = "-" + magnitude;
            under.AddThemeColorOverride("font_color", InkMinus);
            under.Visible = true;
            under.AddThemeFontSizeOverride("font_size", fontSize);
            under.Modulate = plusChosen ? DimmedHalf : Colors.White;
        }

        // Dim the art of the half that is not in play, so the choice reads from across the table.
        view.SelfModulate = plusChosen ? Colors.White : DimmedHalf;
        bottom.Modulate = plusChosen ? DimmedHalf : Colors.White;
    }

    // ------------------------------------------------------------------
    // Pre-rendered faces (pass 58, tools/cardgen)
    //
    // Every card is rendered whole in Blender - frame, numbers, pips, signs - so the game only
    // loads the picture. The key names the exact face: the value, the sign a "+/-" card is set to,
    // the rank for a main card. Anything without a render falls back to the drawn face.
    // ------------------------------------------------------------------
    private const string PrerenderedMeta = "prerendered";

    /// The player's chosen deck - "bronze", "silver"... (RunData.SelectedDeck).
    public static string PlayerDeckKey => RunData.Instance?.SelectedDeck ?? Cosmetics.Default;

    /// Whose deck a main card on this side is drawn from: the opponent in a ladder stage uses the
    /// STAGE's deck; everyone else (you, and both players in local 2-player) uses yours.
    public string DeckKeyFor(bool opponentSide) => opponentSide && RankKey != null ? RankKey : PlayerDeckKey;

    private Texture2D PrerenderedFace(Card card, bool opponentSide = false)
    {
        int n = Mathf.Abs(card.Value);
        string sign = card.Value < 0 ? "minus" : "plus";
        if (card.Type == CardType.Main)
            return n is >= 1 and <= 10 ? Art($"cards/main/main_{n}_{DeckKeyFor(opponentSide)}.png") : null;
        if (card.Effect != CardEffect.None) return EffectArt(card.Effect);
        if (n < 1) return null;
        if (card.CanFlipValue) return Art($"cards/mods/flip_{n}_{sign}.png");
        if (card.IsRescue) return Art($"cards/mods/rescue_{sign}_{n}.png");
        return Art($"cards/mods/{sign}_{n}.png");
    }

    public TextureRect CreateCardView(Card card, Vector2 size) => CreateCardView(card, size, false);

    public TextureRect CreateCardView(Card card, Vector2 size, bool opponentSide)
    {
        Texture2D rendered = PrerenderedFace(card, opponentSide);
        if (rendered != null)
        {
            TextureRect ready = (TextureRect)_cardViewScene.Instantiate();
            ready.Texture = rendered;
            ready.SetMeta("cardId", card.Id);
            ready.SetMeta(PrerenderedMeta, true);
            if (card.Effect != CardEffect.None) ready.SetMeta("effectCard", true);
            ApplyCardSize(ready, size);
            AddShine(ready, card);
            return ready;
        }

        TextureRect view = (TextureRect)_cardViewScene.Instantiate();
        view.Texture = FaceFor(card);
        view.SetMeta("cardId", card.Id); // so RefreshCardFace can find this view again
        ApplyCardSize(view, size);

        string text = card.DisplayText; // read from Value, so a flipped card shows its new sign
        foreach (string name in CardLabelNames)
        {
            Label label = view.GetNodeOrNull<Label>(name);
            if (label != null) label.Text = text;
        }
        ApplyInk(view, InkFor(card));

        // Standard cards carry the rank's tint, so the deck you are playing with visibly changes
        // as you climb. SelfModulate, not Modulate: the number on top stays white.
        // A rank's own painted face (cards/card_main_<rank>.png) is shown as painted.
        if (card.Type == CardType.Main) view.SelfModulate = RankArt("cards/card_main") != null ? Colors.White : RankCardTint;

        // A rescue card is not one the player owns, and may carry a value no bought card can.
        if (card.IsRescue && card.Effect == CardEffect.None)
            view.SelfModulate = Art("cards/rescue.png") != null ? Colors.White : RescueTint;

        // An effect card is a Modifier, so this never fights the rank tint above.
        if (card.Effect != CardEffect.None)
        {
            view.SelfModulate = EffectTint;
            view.SetMeta("effectCard", true); // ApplyCardSize gives its longer mark a smaller font
            ApplyCardSize(view, size);        // re-run now that the meta is set

            // An illustrated effect card carries its own picture and name: no text mark on top.
            if (EffectArt(card.Effect) != null)
                foreach (string name in CardLabelNames)
                {
                    Label label = view.GetNodeOrNull<Label>(name);
                    if (label != null) label.Visible = false;
                }
        }

        // The +/- face would rewrite both labels and hide the effect entirely, so only an
        // ordinary card gets it - a hand-edited save carrying both flags cannot lie about itself.
        // ApplyCardSize runs again afterwards: the first run happened before the face existed, so
        // it left the corners on, and they drew "+3" over the card's own "+3" (pass 22).
        if (card.CanFlipValue && card.Effect == CardEffect.None)
        {
            BuildFlipValueFace(view, card, size);
            ApplyCardSize(view, size);
        }

        // A Modifier's signed face: its sign as a shape and pips for the amount (after the "+/-"
        // face exists, so it can draw into both halves).
        MarkSignedFace(view, card);
        if (view.HasMeta(SignedPipsMeta)) ApplyCardSize(view, size);

        // Pips on main-deck cards: they pip the 1-10 an ordinary playing card pips.
        if (PipsOnMainCards && card.Type == CardType.Main)
        {
            view.SetMeta("pips", card.Value);
            ApplyCardSize(view, size); // builds them from the meta, and hides the centre number
        }

        // The bottom-right corner is the top-left one turned round, which is the whole reason the
        // card can be read from the other side of the table. Rotated about its own centre once the
        // layout has given it a size - PivotOffset means nothing before that.
        Label corner = view.GetNodeOrNull<Label>("CornerBR");
        if (corner != null)
        {
            corner.Resized += () =>
            {
                corner.PivotOffset = corner.Size / 2f;
                corner.RotationDegrees = 180f;
            };
        }
        AddShine(view, card);
        return view;
    }

    // ------------------------------------------------------------------
    // Shine (pass 57, after Pokemon TCG Pocket)
    //
    // Every card gets a gloss streak that slides as the phone tilts (card_shine.gdshader,
    // ShineDriver). Special cards also get holo foil: effect cards full strength, rescue cards and
    // "+/-" cards lighter. A painted foil mask (<face>_foil.png beside the face, ART_PIPELINE.md)
    // limits the foil to the parts the artist chose; without one the whole face shimmers.
    // ------------------------------------------------------------------
    private float FoilStrengthFor(Card card)
    {
        if (card.Effect != CardEffect.None) return 0.85f;
        if (card.IsRescue) return 0.6f;
        if (card.CanFlipValue) return 0.3f;
        return 0f;
    }

    private void AddShine(TextureRect view, Card card)
    {
        if (_shineShader == null || view.Texture == null) return;

        ShaderMaterial mat = new ShaderMaterial { Shader = _shineShader };
        mat.SetShaderParameter("card_face", view.Texture);
        mat.SetShaderParameter("foil_strength", FoilStrengthFor(card));
        mat.SetShaderParameter("gloss_strength", 0.18f);
        string facePath = view.Texture.ResourcePath;
        if (!string.IsNullOrEmpty(facePath) && facePath.EndsWith(".png"))
        {
            string maskPath = facePath.Substring(0, facePath.Length - 4) + "_foil.png";
            if (ResourceLoader.Exists(maskPath)) mat.SetShaderParameter("foil_mask", GD.Load<Texture2D>(maskPath));
        }

        ColorRect shine = new ColorRect
        {
            Name = "Shine",
            Color = Colors.White,
            Material = mat,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        shine.AddToGroup(PerspectiveBoard.OwnMaterialGroup);
        view.AddChild(shine);
        shine.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    /// Redraws a view that is already showing this card, after something changed its Value (Copy,
    /// a flip). Size is the card's size where it sits; opponentSide picks whose deck a main card
    /// is from.
    public void Redraw(TextureRect view, Card card, Vector2 size, bool opponentSide)
    {
        if (view.HasMeta(PrerenderedMeta))
        {
            Texture2D face = PrerenderedFace(card, opponentSide);
            if (face != null)
            {
                view.Texture = face;
                if (view.GetNodeOrNull<ColorRect>("Shine")?.Material is ShaderMaterial shine)
                    shine.SetShaderParameter("card_face", face);
            }
            return;
        }

        // A flip card keeps its own face - only an ordinary card's face follows its sign.
        if (!view.HasNode("FlipValueBottom")) view.Texture = FaceFor(card);

        string text = card.DisplayText; // computed from Value, so the new number is already in it
        foreach (string name in CardLabelNames)
        {
            Label label = view.GetNodeOrNull<Label>(name);
            if (label != null) label.Text = text;
        }
        if (!view.HasNode("FlipValueBottom")) ApplyInk(view, InkFor(card));

        // Copy rewrites a drawn main card's value, so its pips have to be re-counted - otherwise
        // the number in the corners and the dots in the middle disagree about the same card.
        if (view.HasMeta("pips"))
        {
            view.SetMeta("pips", card.Value);
            BuildPips(view, card.Value, size);
        }
        if (view.HasMeta(SignedPipsMeta))
        {
            MarkSignedFace(view, card);
            if (view.HasMeta(SignedPipsMeta)) BuildSignedFace(view, size, view.HasNode("FlipValueBottom"));
        }
    }
}
