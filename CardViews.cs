using Godot;
using System;
using System.Collections.Generic;

/// What a card LOOKS like. Given a Card and a size, this makes the view that shows it - the
/// pre-rendered face (pass 58), or for the rare card with no render a plain fallback - and gives
/// it its shine.
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

    /// Tint for the rank in play: the face-down deck wears it, and so does a fallback main card.
    /// TableUi sets it with the rest of the rank's theme (ApplyRankTheme).
    public Color RankCardTint { get; set; } = Colors.White;

    /// A rescue card is not one you own, so a fallback rescue face is washed mint.
    public static readonly Color RescueTint = new Color(0.7f, 1.3f, 1.05f);

    private Shader _shineShader;

    public const string ArtDir = "res://assets/aimfor20_art/";

    // The blank faces, for the fallback only.
    private Texture2D _faceMain;     // green  - main-deck cards
    private Texture2D _facePlus;     // blue   - positive modifiers
    private Texture2D _faceMinus;    // red    - negative modifiers
    private Texture2D _faceFlip;     // violet - "+/-" (Flip Value) modifiers
    private Texture2D _faceEffect;   // foil   - effect cards (Copy, Shave, Veto, ...)
    private Texture2D _cardBack;     // the face-down deck, and the card that flies from it

    // The faces are pale, so a fallback number is drawn in a dark ink of the face's own hue.
    private static readonly Color InkMain = new Color(0.12f, 0.42f, 0.27f);
    private static readonly Color InkPlus = new Color(0.13f, 0.33f, 0.66f);
    private static readonly Color InkMinus = new Color(0.68f, 0.17f, 0.22f);
    public static readonly Color InkFlip = new Color(0.37f, 0.24f, 0.62f);
    private static readonly Color InkEffect = new Color(0.45f, 0.29f, 0.05f);

    // ------------------------------------------------------------------
    // Art drop-ins (ART_PIPELINE.md)
    //
    // Finished art is picked up by FILE NAME, so a render from Blender/Krita goes live by being
    // saved in the right place - no code change.
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
        : RunData.Instance.Endless ? Cosmetics.EndlessKey
        : RunData.Instance.CurrentRank.Name.ToLowerInvariant();

    /// An effect card's own illustrated face: cards/effect_copy.png, cards/effect_tradetotals.png...
    private Texture2D EffectArt(CardEffect effect) => Art($"cards/effect_{effect.ToString().ToLowerInvariant()}.png");

    // ------------------------------------------------------------------
    // The fallback face
    //
    // Every card the game deals has a render (pass 58), so this is only for what falls outside
    // the rendered set - a rescue card bigger than 11, or a render missing from a build. It used
    // to be ~400 lines of drawn faces (pips, signs, the +/- split, turned corners); now it is the
    // blank face in the card's colour with its value on it, which is enough to play the card.
    // ------------------------------------------------------------------
    private const float FallbackFontScale = 0.44f;
    private const float FallbackEffectFontScale = 0.30f; // a name ("Copy"), not a digit

    private Texture2D FallbackFace(Card card)
    {
        if (card.Type == CardType.Main) return _faceMain;
        // Before the sign test: a Shave carries Value 1 and would otherwise wear the blue "plus"
        // face, which is the opposite of what it does.
        if (card.Effect != CardEffect.None) return _faceEffect;
        if (card.CanFlipValue) return _faceFlip;
        return card.Value < 0 ? _faceMinus : _facePlus;
    }

    private static Color InkFor(Card card)
    {
        if (card.Type == CardType.Main) return InkMain;
        if (card.Effect != CardEffect.None) return InkEffect;
        if (card.CanFlipValue) return InkFlip;
        return card.Value < 0 ? InkMinus : InkPlus;
    }

    private static Color FallbackTint(Card card, Color rankTint)
    {
        if (card.Type == CardType.Main) return rankTint;
        if (card.IsRescue && card.Effect == CardEffect.None) return RescueTint;
        return Colors.White;
    }

    /// Paints the fallback face for this card onto a view: face, tint, and the value in ink.
    private void PaintFallback(TextureRect view, Card card)
    {
        view.Texture = FallbackFace(card);
        view.SelfModulate = FallbackTint(card, RankCardTint); // SelfModulate: the number stays ink
        Label label = view.GetNodeOrNull<Label>("Label");
        if (label == null) return;
        label.Text = card.DisplayText; // read from Value, so a flipped card shows its new sign
        label.AddThemeColorOverride("font_color", InkFor(card));
    }

    /// Sizes a card view. A pre-rendered face carries all its own marks, so its label is hidden;
    /// a fallback face scales its one number to the card.
    public void ApplyCardSize(TextureRect view, Vector2 size)
    {
        view.CustomMinimumSize = size;

        Label label = view.GetNodeOrNull<Label>("Label");
        if (label == null) return;
        label.Visible = !view.HasMeta(PrerenderedMeta);
        if (!label.Visible) return;

        float scale = view.HasMeta("effectCard") ? FallbackEffectFontScale : FallbackFontScale;
        label.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(size.Y * scale));
    }

    // ------------------------------------------------------------------
    // Pre-rendered faces (pass 58, tools/cardgen)
    //
    // Every card is rendered whole in Blender - frame, numbers, pips, signs - so the game only
    // loads the picture. The key names the exact face: the value, the sign a "+/-" card is set to,
    // the rank for a main card. Anything without a render gets the fallback face above.
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
        TextureRect view = (TextureRect)_cardViewScene.Instantiate();
        view.SetMeta("cardId", card.Id); // so a redraw can find this view again
        if (card.Effect != CardEffect.None) view.SetMeta("effectCard", true);

        // Online, the opponent's hand is face down: the phone does not know what is in it.
        if (card.IsHidden)
        {
            view.Texture = Art($"backs/card_back_{PlayerDeckKey}.png") ?? _cardBack;
            // The template's number belongs to a face; a back has none. Marked like a pre-rendered
            // face so ApplyCardSize (which runs again on every resize) keeps the label hidden.
            view.SetMeta(PrerenderedMeta, true);
            ApplyCardSize(view, size);
            return view;
        }

        Texture2D rendered = PrerenderedFace(card, opponentSide);
        if (rendered != null)
        {
            view.Texture = rendered;
            view.SetMeta(PrerenderedMeta, true);
        }
        else
        {
            PaintFallback(view, card);
        }

        ApplyCardSize(view, size);
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
    /// is from. A view whose new value has no render drops to the fallback face, rather than
    /// keeping a picture of the old value.
    public void Redraw(TextureRect view, Card card, Vector2 size, bool opponentSide)
    {
        Texture2D face = PrerenderedFace(card, opponentSide);
        if (face != null)
        {
            view.Texture = face;
            view.SelfModulate = Colors.White;
            view.SetMeta(PrerenderedMeta, true);
        }
        else
        {
            PaintFallback(view, card);
            if (view.HasMeta(PrerenderedMeta)) view.RemoveMeta(PrerenderedMeta);
        }
        ApplyCardSize(view, size);

        if (view.GetNodeOrNull<ColorRect>("Shine")?.Material is ShaderMaterial shine)
            shine.SetShaderParameter("card_face", view.Texture);
    }
}
