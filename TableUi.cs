using Godot;
using System;
using System.Collections.Generic;

/// What the table's PICTURE needs from the game behind it.
///
/// Same shape as IBotTable and ITableHost: GameManager implements it explicitly, so nothing in
/// GameManager can call these by accident. The direction of every member is the point - TableUi
/// asks questions and reports presses, and decides nothing. There is no member here that changes
/// a score, spends a card or ends a turn.
public interface ITableUiHost
{
    Player Player1 { get; }
    Player Player2 { get; }
    GameState State { get; }
    Table Table { get; }

    bool GameStarted { get; }
    bool VsBot { get; }
    bool InRun { get; }

    /// The set-end explanation is up and owns the middle label; nothing else may write to it.
    bool SetOverPending { get; }

    /// The card this player has picked up, or null. The pick-up itself is a decision and stays
    /// with the rules; the table only draws the consequence.
    Card SelectedFor(Player player);

    /// May a person press Draw Card / Hold / a Modifier for this player right now?
    bool CanAct(Player player);
    bool CanPlayEffect(Player owner, Card card);

    /// The words for the status line and the middle panel. Both are readings of the rules, so the
    /// rules write them; the table only decides how big they are and what colour.
    string StatusFor(Player player);
    string SetInfoLine();

    /// A picked-up card that can no longer be played is dropped before anything is drawn, so the
    /// status line and the buttons agree. First thing in every refresh.
    void ValidateSelections();

    /// The table has just been repainted. Anything watching the screen for progress - the
    /// tutorial, the coach marks - gets its look here.
    void AfterRefresh();

    /// A sentence about the run's rules, for the target line. A reading of the rules, so the
    /// rules write it.
    string FinaleRulesLine(RunData run, string prefix);

    /// The layout has just moved. Anything anchored to a node on the table - the tutorial's
    /// spotlight - has to be placed again.
    void LayoutChanged();

    /// A new layout scene has just been put up (first load, or a rotation swapped it). Anything
    /// the game hangs on the layout itself - the debug buttons - goes on again here.
    void LayoutBound(TableLayout layout);

    void DrawCardPressed(Player player);
    void HoldPressed(Player player);
    void ModifierPressed(Player player, Card card);
    void PlayPressed(Player player);
    void PutBackPressed(Player player);
    void FlipValuePressed(Player player);
    void MenuPressed();
}

/// Everything the live table LOOKS like - painted into a layout SCENE (pass 34).
///
/// Until pass 33 this class also BUILT the table: it took two plain scenes and re-arranged,
/// re-parented, re-sized and re-styled their nodes in code for whichever orientation the phone was
/// in, which is why neither scene looked like the game when opened in the editor. That is gone.
/// There are now two layout scenes, table_landscape.tscn and table_portrait.tscn, each laid out
/// by hand in the editor exactly as it appears (see TableLayout.cs). This class:
///  - puts up the one that fits the screen, and swaps it on a rotation (the match carries on -
///    the boards are redrawn from the players' cards, everything else from the next Refresh);
///  - scales the whole UI so the layout fills the screen (FitToWindow);
///  - and paints the game onto it: cards, scores, chips, which buttons are live.
///
/// The line against Table.cs, in the other direction: Table knows where the cards are, TableUi
/// knows what they look like. Nothing in here decides anything - no score changes, no card is
/// spent, no turn ends. Presses go out through ITableUiHost and come back as new state to draw.
public sealed class TableUi
{
    public const string LandscapeScenePath = "res://table_landscape.tscn";
    public const string PortraitScenePath = "res://table_portrait.tscn";

    private readonly ITableUiHost _host;

    /// The scene node the picture borrows for the things only a Node can do: adding a child,
    /// reaching the tree and the viewport, waiting on a timer, deferring a call to the end of the
    /// frame. It is never asked a question about the game - that is what _host is for.
    private readonly Node _root;

    /// Where the layout scene is instanced (table_scene.tscn's LayoutHost).
    private readonly Control _layoutHost;

    /// The face-to-face toggle. It lives in the table menu; the layout only reads it.
    private readonly CheckButton _mirrorToggle;

    private readonly PackedScene _cardViewScene = GD.Load<PackedScene>("res://CardView.tscn");

    private PackedScene _landscapeScene;
    private PackedScene _portraitScene;

    /// The layout on screen now. Null only before the first ApplyResponsiveLayout.
    private TableLayout L;

    public TableLayout Layout => L;

    public TableUi(ITableUiHost host, Node root, Control layoutHost, CheckButton mirrorToggle)
    {
        _host = host;
        _root = root;
        _layoutHost = layoutHost;
        _mirrorToggle = mirrorToggle;

        _faceMain = GD.Load<Texture2D>(ArtDir + "card_main.png");
        _facePlus = GD.Load<Texture2D>(ArtDir + "card_plus.png");
        _faceMinus = GD.Load<Texture2D>(ArtDir + "card_minus.png");
        _faceFlip = GD.Load<Texture2D>(ArtDir + "card_flip.png");
        _faceEffect = GD.Load<Texture2D>(ArtDir + "card_foil.png");
        _cardBack = GD.Load<Texture2D>(ArtDir + "card_back.png");
        _chipWon = GD.Load<Texture2D>(ArtDir + "ui/chip_won.png");
        _chipEmpty = GD.Load<Texture2D>(ArtDir + "ui/chip_empty.png");
        _sfxSlide = CreateSfx("res://assets/kenney/sfx/cardSlide1.ogg");
        _sfxPlace = CreateSfx("res://assets/kenney/sfx/cardPlace1.ogg");
        _sfxLock = CreateSfx("res://assets/sfx/hold_lock.wav");
        _sfxOnTarget = CreateSfx("res://assets/sfx/on_target.wav");
    }

    // ------------------------------------------------------------------
    // What other classes point at. The tutorial's spotlight and a coach mark's arrow get the
    // node, never the right to change it. All of them follow the layout across a rotation.
    // ------------------------------------------------------------------
    public Control P1ScoreBlock => L?.P1ScoreBox;
    public Control DeckFootprint => L?.DeckFootprint ?? L?.Deck;
    public Control P1ActionRow => L?.P1ButtonSlot;
    public Control P1Hand => L?.P1Hand;
    public Control P1WinsRow => L?.P1WinsRow;
    public Control EffectBanner => (Control)L?.EffectToast ?? L?.EffectLabel;
    public Control P1Board => L?.P1Board;
    public Control P2Board => L?.P2Board;

    /// The two sounds a press makes. They live here because the card animations play them too,
    /// and one owner of the two AudioStreamPlayers is one fewer thing to keep in step.
    public void SoundPlace() => _sfxPlace?.Play();
    public void SoundSlide() => _sfxSlide?.Play();

    /// A call put off to the end of the frame, and dropped if the table has gone away in between
    /// (a Restart frees it; a plain Callable would still fire on the freed object).
    private void Defer(Action action) =>
        Callable.From(() => { if (GodotObject.IsInstanceValid(_root)) action(); }).CallDeferred();

    /// A refresh at the end of the frame rather than now.
    public void DeferRefresh() => Defer(Refresh);

    private Player P1 => _host.Player1;
    private Player P2 => _host.Player2;
    private GameState State => _host.State;

    /// Face-to-face local play turns Player 2's side round. Never against the bot.
    public bool IsMirrored => _mirrorToggle != null && _mirrorToggle.ButtonPressed && !_host.VsBot;

    /// A rescue card is not one you own, so it is washed mint wherever it appears. The rescue
    /// OFFER is monetisation and lives in GameManager; this is only its colour.
    public static readonly Color RescueTint = new Color(0.7f, 1.3f, 1.05f);

    /// The shape every card size in the game is derived from (the art is 140x190).
    public static readonly Vector2 BaseCardSize = new Vector2(84, 114);

    /// An ordinary card off the board: the flying card, and what the shop and deck screens scale.
    public Vector2 CardSize => L?.CardSize ?? BaseCardSize;

    /// A card in a hand, as the layout scene sets it.
    public Vector2 ModifierCardSize => L?.HandCardSize ?? BaseCardSize;

    // The layout's nodes under the names the painting code below has always used.
    private Control _mainDeckPosition => L?.Deck;
    private Label _setInfoLabel => L?.SetInfoLabel;
    private Label _targetLabel => L?.TargetLabel;
    private Label _effectBanner => L?.EffectLabel;
    private PanelContainer _effectToast => L?.EffectToast;
    private Control _p1BoardContainer => L?.P1Board;
    private Control _p2BoardContainer => L?.P2Board;

    private Tween _effectFade;

    private uint _effectBannerToken;   // so a stale timer never wipes a newer message

    // ------------------------------------------------------------------
    // Art. Card faces, the back and the playmat are the game's own (assets/aimfor20_art/, one
    // 560x760 PNG per face - the same 140:190 shape the Kenney cards had).
    // ------------------------------------------------------------------
    private const string ArtDir = "res://assets/aimfor20_art/";

    private Texture2D _faceMain;     // green  - main-deck cards
    private Texture2D _facePlus;     // blue   - positive modifiers
    private Texture2D _faceMinus;    // red    - negative modifiers
    private Texture2D _faceFlip;     // violet - "+/-" (Flip Value) modifiers
    private Texture2D _faceEffect;   // foil   - effect cards (Copy, Shave, Veto, ...)
    private Texture2D _cardBack;     // the face-down deck, and the card that flies from it

    // Win markers: an empty ring, and a gold coin carrying the card back's diamond.
    private Texture2D _chipWon;
    private Texture2D _chipEmpty;

    /// The target on the table: gold on the dark mat.
    private static readonly Color TableGold = new Color(1f, 0.85f, 0.35f);

    // The collection log's reward. There is one back now, so the reward is the gilding alone.
    private static readonly Color CollectorBackTint = new Color(1.45f, 1.2f, 0.45f);

    /// A "+/-" card wears its own violet face, split into a +n half and a -n half.
    private const bool FlipCardsUseOwnFace = true;

    // The faces are pale, so the numbers and pips are drawn in a dark ink of the face's own hue.
    private static readonly Color InkMain = new Color(0.12f, 0.42f, 0.27f);
    private static readonly Color InkPlus = new Color(0.13f, 0.33f, 0.66f);
    private static readonly Color InkMinus = new Color(0.68f, 0.17f, 0.22f);
    private static readonly Color InkFlip = new Color(0.37f, 0.24f, 0.62f);
    private static readonly Color InkEffect = new Color(0.45f, 0.29f, 0.05f);

    /// Effect cards used to wear a violet wash over a green back; the foil face replaces it.
    private static readonly Color EffectTint = Colors.White;

    /// The mat's average colour. A rank's table colour divided by this is the tint that turns the
    /// teal mat into that rank's felt.
    private static readonly Color PlaymatAverage = new Color(0.114f, 0.227f, 0.243f);

    /// The felt colour of the 2-player table and of stage 1 - project.godot's clear colour.
    private static readonly Color DefaultTableColor = new Color(0.07f, 0.24f, 0.13f);

    /// Tint applied to the standard (main deck) card art for the rank in play. See ApplyRankTheme.
    private Color _rankCardTint = Colors.White;
    private Color _playmatTint = Colors.White;

    /// The face-down deck: the rank's own back, or the collection reward when it is switched on.
    private bool GildedDeck => RunData.Instance != null && RunData.Instance.UseCollectorBack;

    private Color DeckBackTint => GildedDeck ? CollectorBackTint : _rankCardTint;

    private AudioStreamPlayer _sfxSlide;
    private AudioStreamPlayer _sfxPlace;
    private AudioStreamPlayer _sfxLock;      // Hold: the padlock snapping shut
    private AudioStreamPlayer _sfxOnTarget;  // a picked-up Modifier lands exactly on the target

    private AudioStreamPlayer CreateSfx(string path)
    {
        AudioStream stream = GD.Load<AudioStream>(path);
        if (stream == null) return null;
        AudioStreamPlayer player = new AudioStreamPlayer { Stream = stream, VolumeDb = -4f, Bus = GameSettings.SfxBus };
        _root.AddChild(player);
        return player;
    }

    // ------------------------------------------------------------------
    // Which layout, and how big
    //
    // project.godot keeps a 720x720 base with stretch "canvas_items" / aspect "expand": every
    // size in the layout scenes is in those design pixels. Each layout scene draws the table on a
    // fixed design canvas (720x1560 portrait, 1560x720 landscape - a 19.5:9 phone), with every
    // element placed by hand. The fit scales that canvas to fit the screen and centres it; on a
    // phone of another shape the playmat simply shows a little more at the edges.
    // ------------------------------------------------------------------

    private bool _fitPending;

    private (Vector2I Window, bool Portrait, bool Mirrored, bool VsBot) _fitBasis;

    /// Forget every size this layout has settled on (a real resize, a rotation, a mirror toggle).
    public void ResetFitState() => StableBox.ResetAll();

    /// Puts up the right layout for the window and fits it. Runs on every window resize and
    /// rotation, when the mirror is toggled, and when a match starts (the mode decides P2's side).
    public void ApplyResponsiveLayout()
    {
        Window window = _root.GetTree().Root;
        Vector2I win = window.Size;
        bool portrait = win.Y > win.X;

        if (L == null || L.Portrait != portrait) SwapLayout(portrait);
        if (L == null) return;

        var basis = (win, portrait, IsMirrored, _host.VsBot);
        if (basis != _fitBasis)
        {
            _fitBasis = basis;
            ResetFitState();
        }

        ApplyMirror();
        Show(L.P2ButtonSlot, !_host.VsBot); // the bot presses nothing
        Defer(PlaceEffectToast);
        Defer(() => { PlaceStatusToast(_p1StatusToast, false); PlaceStatusToast(_p2StatusToast, true); });
        Defer(_host.LayoutChanged); // a rotation moves whatever is anchored to a node
        FitToWindow();
    }

    /// Instances the layout scene for this orientation and paints the match onto it.
    private void SwapLayout(bool portrait)
    {
        PackedScene scene = portrait
            ? (_portraitScene ??= GD.Load<PackedScene>(PortraitScenePath))
            : (_landscapeScene ??= GD.Load<PackedScene>(LandscapeScenePath));
        if (scene == null)
        {
            GD.PushError($"TableUi: missing layout scene {(portrait ? PortraitScenePath : LandscapeScenePath)}");
            return;
        }

        TableLayout next = scene.Instantiate<TableLayout>();

        if (L != null)
        {
            // Detached now, not just queued: a queued node is still in the tree until the end of
            // the frame and would still be measured, and still be found by FindChild.
            _layoutHost.RemoveChild(L);
            L.QueueFree();
        }

        L = next;
        _layoutHost.AddChild(L);
        // The scene's root is sized like a phone so it previews properly in the editor; in the
        // game it fills whatever the screen is.
        L.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        BindLayout();
        _host.LayoutBound(L);
        Refresh();
    }

    /// Everything a fresh layout needs once: its buttons wired and styled, its boards measured,
    /// and whatever is already on the table redrawn into it.
    private void BindLayout()
    {
        RememberAuthoredVisibility();
        Wire(L.P1DrawCard, () => _host.DrawCardPressed(P1));
        Wire(L.P1Hold, () => _host.HoldPressed(P1));
        Wire(L.P1Play, () => _host.PlayPressed(P1));
        Wire(L.P1PutBack, () => { _host.PutBackPressed(P1); Refresh(); });
        Wire(L.P1FlipValue, () => _host.FlipValuePressed(P1));
        Wire(L.P2DrawCard, () => _host.DrawCardPressed(P2));
        Wire(L.P2Hold, () => _host.HoldPressed(P2));
        Wire(L.P2Play, () => _host.PlayPressed(P2));
        Wire(L.P2PutBack, () => { _host.PutBackPressed(P2); Refresh(); });
        Wire(L.P2FlipValue, () => _host.FlipValuePressed(P2));
        Wire(L.MenuButton, () => _host.MenuPressed());

        // Pass 31's house pill. Draw Card and Play are the table's one accent - the thing you do
        // next, most turns - and Flip Value is ringed in the violet of a +/- card.
        foreach (Button primary in new[] { L.P1DrawCard, L.P2DrawCard, L.P1Play, L.P2Play })
            if (primary != null) OverlayUi.StyleButton(primary, primary: true);
        foreach (Button plain in new[] { L.P1Hold, L.P2Hold, L.P1PutBack, L.P2PutBack })
            if (plain != null) OverlayUi.StyleButton(plain);
        foreach (Button flip in new[] { L.P1FlipValue, L.P2FlipValue })
        {
            if (flip == null) continue;
            OverlayUi.StyleButton(flip, ring: InkFlip);
            // The house pill has 24px of padding each side, which in portrait's narrow Wins spot
            // left too little room for "Value" and it broke mid-word ("Valu / e"). Thin padding,
            // and wrap only between words.
            foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus", "hover_pressed" })
            {
                if (!flip.HasThemeStyleboxOverride(state)) continue;
                StyleBox box = (StyleBox)flip.GetThemeStylebox(state).Duplicate();
                box.ContentMarginLeft = 8;
                box.ContentMarginRight = 8;
                flip.AddThemeStyleboxOverride(state, box);
            }
            flip.AutowrapMode = TextServer.AutowrapMode.Word;
        }

        // Play / Put back land on Draw Card / Hold's spots (see ConfirmCopiesAction).
        MatchConfirmRow(L.P1DrawCard, L.P1Hold, L.P1Play, L.P1PutBack);
        MatchConfirmRow(L.P2DrawCard, L.P2Hold, L.P2Play, L.P2PutBack);
        SetUpButtonSlot(L.P1ActionRow, L.P1ConfirmRow);
        SetUpButtonSlot(L.P2ActionRow, L.P2ConfirmRow);

        // The board size is whatever the scene's slots are - remembered before portrait's third
        // row ever changes it.
        foreach (GridContainer board in new[] { L.P1Board, L.P2Board })
        {
            if (board == null || board.GetChildCount() == 0) continue;
            if (board.GetChild(0) is Control first) board.SetMeta(BaseCardMeta, first.CustomMinimumSize);
            board.SetMeta(ThirdRowMeta, true); // as authored: all nine; RefreshBoardSlots decides
        }

        if (L.EffectToast != null)
        {
            L.EffectToast.TopLevel = true; // never takes room in the middle panel
            L.EffectToast.Visible = false;
        }

        // Whatever is already on the table - a rotation mid-set - drawn straight in, no flight.
        BuildStatusToasts();
        ResetScoreFeedback();
        TurnRound(L.P1ScoreFarEnd);
        TurnRound(L.P2ScoreFarEnd);
        if (L.P1ScoreThem != null) L.P1ScoreThem.Visible = false; // the box is one line now
        if (L.P2ScoreThem != null) L.P2ScoreThem.Visible = false;
        RestoreBoard(L.P1Board, P1);
        RestoreBoard(L.P2Board, P2);
        ApplyRankTheme();
    }

    // ------------------------------------------------------------------
    // Things the code shows and hides (the opponent score, Wins, Hold, P2's buttons) stay hidden
    // if you hid them in the layout scene - so hiding one in the editor is how you drop it from
    // the design without the game switching it back on.
    // ------------------------------------------------------------------
    private readonly HashSet<Control> _authoredHidden = new HashSet<Control>();

    private void RememberAuthoredVisibility()
    {
        _authoredHidden.Clear();
        foreach (Control c in new Control[] { L.P1OpponentBox, L.P2OpponentBox, L.P1WinsRow, L.P2WinsRow,
                                              L.P1Hold, L.P2Hold, L.P1ButtonSlot, L.P2ButtonSlot })
            if (c != null && !c.Visible) _authoredHidden.Add(c);
    }

    private void Show(Control c, bool show)
    {
        if (c != null) c.Visible = show && !_authoredHidden.Contains(c);
    }

    private static void Wire(Button button, Action onPressed)
    {
        if (button == null) return;
        button.FocusMode = Control.FocusModeEnum.None;
        button.Pressed += onPressed;
    }

    /// Draw Card / Hold are placed by hand in the layout scene (pass 38: no containers - each
    /// button sits exactly where and as big as you draw it). With ConfirmCopiesAction on, the
    /// Play / Put back pair that stands in for them while a card is picked up is put on the same
    /// spots - Play on Draw Card's, Put back on Hold's - so only ActionRow ever needs arranging.
    private void MatchConfirmRow(Button draw, Button hold, Button play, Button putBack)
    {
        if (!L.ConfirmCopiesAction) return;
        foreach ((Button from, Button to) in new[] { (draw, play), (hold, putBack) })
        {
            if (from == null || to == null) continue;
            to.OffsetLeft = from.OffsetLeft;
            to.OffsetTop = from.OffsetTop;
            to.OffsetRight = from.OffsetRight;
            to.OffsetBottom = from.OffsetBottom;
            to.GrowHorizontal = from.GrowHorizontal;
            to.GrowVertical = from.GrowVertical;
            to.Scale = from.Scale;
            to.PivotOffset = from.PivotOffset;
            to.Rotation = from.Rotation;
        }
    }

    /// Only one of the two button rows shows at a time; the confirm row waits hidden.
    private static void SetUpButtonSlot(Control action, Control confirm)
    {
        if (confirm != null) confirm.Visible = false;
        if (action != null) action.Visible = true;
    }

    /// Mirror on: Player 2's side is turned round about its centre. In landscape every element in
    /// it is also mirrored left-to-right first, so that once the side is turned its score column
    /// still stands on the side facing the middle (turning alone would put it on the outer edge).
    ///
    /// Portrait, mirror off (against the bot, or local play without the mirror): Player 2's side is
    /// NOT turned - its cards and score stay the right way up for the one person reading them - but
    /// every element is mirrored top-to-bottom instead (pass 40), so the side still faces the
    /// middle the way Pokemon TCG Pocket's opponent does: board by the middle, hand cropped off the
    /// top edge. So Player 2's side is authored exactly like Player 1's, as if seen by its own
    /// player, and one arrangement serves every mode.
    ///
    /// The mirrored positions are worked out from the edges you placed in the editor - the two
    /// edges swap ends, and so does the direction a box grows - and put back exactly when the
    /// mirror goes off.
    private void ApplyMirror()
    {
        Control side = L?.P2Side;
        if (side == null) return;
        bool mirrored = IsMirrored;
        int flip = L.Portrait ? (mirrored ? FlipNone : FlipY) : (mirrored ? FlipX : FlipNone);
        Vector2 extent = side.Size;

        foreach (Node node in side.GetChildren())
        {
            if (node is not Control child) continue;
            int current = child.HasMeta(FlippedMeta) ? child.GetMeta(FlippedMeta).AsInt32() : FlipNone;
            if (current == flip) continue;

            if (!child.HasMeta(AuthoredLeftMeta))
            {
                float centred = child.HasMeta(CentreShiftMeta) ? child.GetMeta(CentreShiftMeta).AsSingle() : 0f;
                child.SetMeta(AuthoredLeftMeta, child.OffsetLeft - centred);
                child.SetMeta(AuthoredRightMeta, child.OffsetRight - centred);
                child.SetMeta(AuthoredTopMeta, child.OffsetTop);
                child.SetMeta(AuthoredBottomMeta, child.OffsetBottom);
                child.SetMeta(AuthoredGrowMeta, (int)child.GrowHorizontal);
                child.SetMeta(AuthoredGrowVMeta, (int)child.GrowVertical);
            }
            float left = child.GetMeta(AuthoredLeftMeta).AsSingle();
            float right = child.GetMeta(AuthoredRightMeta).AsSingle();
            float top = child.GetMeta(AuthoredTopMeta).AsSingle();
            float bottom = child.GetMeta(AuthoredBottomMeta).AsSingle();
            var grow = (Control.GrowDirection)child.GetMeta(AuthoredGrowMeta).AsInt32();
            var growV = (Control.GrowDirection)child.GetMeta(AuthoredGrowVMeta).AsInt32();

            // Back to the authored rect first, then mirrored along one axis if called for.
            child.OffsetLeft = left;
            child.OffsetRight = right;
            child.OffsetTop = top;
            child.OffsetBottom = bottom;
            child.GrowHorizontal = grow;
            child.GrowVertical = growV;
            if (flip == FlipX)
            {
                child.OffsetLeft = extent.X - right;
                child.OffsetRight = extent.X - left;
                child.GrowHorizontal = Opposite(grow);
            }
            else if (flip == FlipY)
            {
                child.OffsetTop = extent.Y - bottom;
                child.OffsetBottom = extent.Y - top;
                child.GrowVertical = Opposite(growV);

                // Pass 43: a tilted board shortens toward its near edge. Player 1's near edge is
                // the bottom, so its board is drawn pulled away from the middle. Mirrored
                // top-to-bottom, Player 2's board keeps its far edge at the top (the table
                // recedes from Player 1), so it would shorten TOWARD the middle and sit on the
                // centre ring. Lift it by the height it gives up, so it is drawn exactly where a
                // true mirror of Player 1's board would be.
                if (child is PerspectiveBoard board && board.Tilt)
                {
                    float lift = (1f - board.Depth) * (bottom - top);
                    child.OffsetTop -= lift;
                    child.OffsetBottom -= lift;
                }
            }
            // A board re-centred for portrait's third row keeps that shift (CentreBoardColumns).
            // Only the vertical axis is ever mirrored in portrait, so it applies unchanged.
            if (child.HasMeta(CentreShiftMeta))
            {
                float shift = child.GetMeta(CentreShiftMeta).AsSingle();
                child.OffsetLeft += flip == FlipX ? -shift : shift;
                child.OffsetRight += flip == FlipX ? -shift : shift;
            }
            child.SetMeta(FlippedMeta, flip);
        }

        side.PivotOffset = side.Size / 2f;
        side.RotationDegrees = mirrored ? 180f : 0f;

        // Face to face, Player 2 sits at the top of the screen, so their board recedes toward the
        // middle of the table rather than toward the top edge (pass 39).
        if (L.P2Board is PerspectiveBoard p2Board) p2Board.FarAtTop = !mirrored;

        ApplyScoreBadgeEnds(mirrored);
    }

    private static Control.GrowDirection Opposite(Control.GrowDirection grow) => grow switch
    {
        Control.GrowDirection.Begin => Control.GrowDirection.End,
        Control.GrowDirection.End => Control.GrowDirection.Begin,
        _ => grow,
    };

    private const int FlipNone = 0;
    private const int FlipX = 1;
    private const int FlipY = 2;
    private const string FlippedMeta = "mirrorFlip";
    private const string AuthoredLeftMeta = "authoredLeft";
    private const string AuthoredRightMeta = "authoredRight";
    private const string AuthoredTopMeta = "authoredTop";
    private const string AuthoredBottomMeta = "authoredBottom";
    private const string AuthoredGrowMeta = "authoredGrow";
    private const string AuthoredGrowVMeta = "authoredGrowV";

    /// Scales the whole UI so the layout's design canvas exactly fits the screen: whichever axis
    /// is tighter decides, and the canvas is centred along the other (the playmat fills the rest).
    /// A frame first, so a freshly swapped layout has its size.
    private async void FitToWindow()
    {
        if (_fitPending) return;
        _fitPending = true;
        try
        {
            await _root.ToSignal(_root.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!GodotObject.IsInstanceValid(_root) || !_root.IsInsideTree() || L == null) return;

            Window window = _root.GetTree().Root;
            Vector2 win = window.Size;
            if (win.X <= 1f || win.Y <= 1f) return;

            Control canvas = L.Canvas ?? L;
            Vector2 design = canvas.Size;
            if (design.X <= 1f || design.Y <= 1f) return;

            // Design pixels per screen pixel.
            float k = Mathf.Max(design.X / win.X, design.Y / win.Y);
            Vector2I size = (Vector2I)(win * k).Round();

            if (window.ContentScaleSize != size)
            {
                GD.Print($"Table fit: canvas {design.X:0}x{design.Y:0} -> design screen {size.X}x{size.Y}");
                window.ContentScaleSize = size; // re-fires SizeChanged; the next pass lands on the same size
            }
        }
        finally
        {
            _fitPending = false;
        }
    }

    // ------------------------------------------------------------------
    // The boards
    //
    // Nine slot Panels per board, placed in the layout scene; a card is dropped INTO the next
    // free slot, so the grid never grows or shifts. Their size in the scene is the board card
    // size.
    //
    // Portrait shows two rows of three (pass 32). The seventh card opens the third row, and every
    // card on THAT board shrinks so three rows fill exactly the height two did - so nothing else
    // on the table moves mid-set. The board goes back to two rows when it is cleared.
    // ------------------------------------------------------------------
    private const int PortraitRows = 2;

    private const int PortraitSlots = PortraitRows * 3;

    private const string BaseCardMeta = "baseCardSize";

    private const string ThirdRowMeta = "thirdRow";

    private bool ShowsThirdRow(Control board)
    {
        if (L == null || !L.Portrait || board == null) return true;
        int i = 0;
        foreach (Node child in board.GetChildren())
        {
            if (i++ >= PortraitSlots && child.GetChildCount() > 0) return true;
        }
        return false;
    }

    /// The size of a card on THIS board: the scene's slot size, or, in portrait with the third
    /// row open, small enough that three rows take the height two did.
    private Vector2 BoardCardSizeFor(Control board)
    {
        Vector2 size = board != null && board.HasMeta(BaseCardMeta)
            ? board.GetMeta(BaseCardMeta).AsVector2()
            : CardSize;
        if (L == null || !L.Portrait || !ShowsThirdRow(board)) return size;

        float gap = board.GetThemeConstant("v_separation");
        float h = (PortraitRows * size.Y + (PortraitRows - 1) * gap - 2f * gap) / 3f;
        return size * (h / size.Y);
    }

    private void ResizeBoard(Control board)
    {
        if (board == null) return;
        Vector2 size = BoardCardSizeFor(board);
        board.SetMeta(ThirdRowMeta, ShowsThirdRow(board));
        foreach (Node child in board.GetChildren())
        {
            if (child is not Control slot) continue;
            slot.CustomMinimumSize = size;
            foreach (Node inner in slot.GetChildren())
                if (inner is TextureRect view) ApplyCardSize(view, size);
        }
        CentreBoardColumns(board, size);
    }

    /// A GridContainer lays its cells out from its left edge, so when portrait's third row
    /// shrinks every card the three columns bunched up on the left (S25 release playtest,
    /// 2026-09-29). Slide the board right by half the width it gave up, so the columns stay
    /// centred where the full-size ones were. The shift is remembered, so it is undone exactly,
    /// and ApplyMirror re-applies it after putting the authored rect back.
    private const string CentreShiftMeta = "centreShift";

    private void CentreBoardColumns(Control board, Vector2 size)
    {
        if (board is not GridContainer grid || !board.HasMeta(BaseCardMeta)) return;
        int cols = Mathf.Max(1, grid.Columns);
        float gap = board.GetThemeConstant("h_separation");
        Vector2 full = board.GetMeta(BaseCardMeta).AsVector2();
        float want = ((cols * full.X + (cols - 1) * gap) - (cols * size.X + (cols - 1) * gap)) / 2f;
        float had = board.HasMeta(CentreShiftMeta) ? board.GetMeta(CentreShiftMeta).AsSingle() : 0f;
        if (Mathf.IsEqualApprox(want, had)) return;
        board.OffsetLeft += want - had;
        board.OffsetRight += want - had;
        board.SetMeta(CentreShiftMeta, want);
    }

    /// Opens and closes portrait's third row (a change re-sizes every card on that board).
    private void RefreshBoardSlots(Control board)
    {
        if (board == null) return;
        bool third = ShowsThirdRow(board);
        int i = 0;
        foreach (Node child in board.GetChildren())
        {
            if (child is not Control slot) continue;
            slot.Visible = third || i < PortraitSlots;
            i++;
        }

        bool had = board.HasMeta(ThirdRowMeta) && board.GetMeta(ThirdRowMeta).AsBool();
        if (had != third) ResizeBoard(board);
    }

    /// Empties a board for a new set: every card view leaves its slot, the slots stay.
    public void FillBoardWithSlots(Control board)
    {
        if (board == null) return;
        foreach (Node child in board.GetChildren())
            ClearChildren(child);
        RefreshBoardSlots(board);
    }

    /// Draws a player's cards straight into a freshly bound layout - no flight, no sound.
    private void RestoreBoard(Control board, Player player)
    {
        if (board == null || player == null) return;
        FillBoardWithSlots(board);
        if (!_host.GameStarted) return;

        foreach (Card card in player.ActiveCardsOnBoard)
        {
            Control slot = FindFreeSlot(board);
            if (slot == null) break;
            TextureRect view = CreateCardView(card, BoardCardSizeFor(board));
            slot.AddChild(view);
            view.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            RefreshBoardSlots(board);
        }
        ResizeBoard(board); // one size for every card, now the row count is known
    }

    private Control FindFreeSlot(Control board)
    {
        foreach (Node child in board.GetChildren())
        {
            if (child is Control slot && slot.GetChildCount() == 0) return slot;
        }
        return null;
    }

    private static void ClearChildren(Node parent)
    {
        if (parent == null) return;
        foreach (Node child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    // ------------------------------------------------------------------
    // The refresh: the game's state, pushed onto the layout
    // ------------------------------------------------------------------
    public void Refresh()
    {
        if (L == null) return;

        // A picked-up card that can no longer be played (spent, or the player just held) is
        // dropped before anything is drawn, so the status line and the buttons agree.
        _host.ValidateSelections();

        RefreshScoreLines();
        UpdateTargetLabel();

        RefreshOpponentLines();
        ApplyScoreDanger(L.P1ScoreBox, P1, preview: true);
        ApplyScoreDanger(L.P2ScoreBox, P2, preview: true);
        ApplyScoreDanger(L.P1OpponentBox, P2); // the opponent's score, boxed and red the same way
        ApplyScoreDanger(L.P2OpponentBox, P1);
        UpdateHoldLock(L.P1ScoreBox, P1);
        UpdateHoldLock(L.P2ScoreBox, P2);
        _scoreFeedbackPrimed = true; // from here on, changes animate and make their sound
        UpdateStatusToast(0, P1, _p1StatusToast);
        UpdateStatusToast(1, P2, _p2StatusToast);

        SetChips(L.P1Chips, State.SetsWonPlayer1);
        SetChips(L.P2Chips, State.SetsWonPlayer2);

        // While the set-end explanation is up, EndSet owns this label.
        if (_host.GameStarted && _setInfoLabel != null && !State.IsGameOver && !_host.SetOverPending)
        {
            _setInfoLabel.Text = _host.SetInfoLine();
        }
        // The middle panel's lines take no room while they have nothing to say (local 2-player
        // has no stage line, and the target line only speaks when the target moved).
        HideIfEmpty(_setInfoLabel);
        HideIfEmpty(_targetLabel);

        // Both sides act at once: each player's row stays live until THAT player has ended the
        // turn or is holding. Everything is locked while a set-end explanation is waiting.
        bool p1Can = _host.CanAct(P1);
        bool p2Can = _host.CanAct(P2);
        SetEnabled(L.P1DrawCard, p1Can && !TutorialLockDraw);
        SetEnabled(L.P1Hold, p1Can);
        SetEnabled(L.P2DrawCard, p2Can);
        SetEnabled(L.P2Hold, p2Can);

        // Whose move it is lives on the buttons (pass 33), not in a status line.
        bool live = _host.GameStarted && !State.IsGameOver;
        ApplyWaiting(L.P1DrawCard, L.P1Hold, live && !p1Can);
        ApplyWaiting(L.P2DrawCard, L.P2Hold, live && !p2Can);
        ApplyHoldPulse(TutorialPulseHold && p1Can);

        // While a card is picked up, that player's Draw Card / Hold row is swapped for the
        // Play / Put back row (same slot, so nothing moves).
        UpdateConfirmRow(P1, L.P1ConfirmRow, L.P1Play, L.P1FlipValue, L.P1ActionRow, L.P1WinsRow);
        UpdateConfirmRow(P2, L.P2ConfirmRow, L.P2Play, L.P2FlipValue, L.P2ActionRow, L.P2WinsRow);

        RefreshModifiersUI();
        RefreshBoardSlots(L.P1Board);
        RefreshBoardSlots(L.P2Board);
        _host.AfterRefresh();

        ApplyMirror(); // only does anything when the mirror has just changed
    }

    /// The score lines, in whichever form the mirror calls for.
    ///
    /// The score says whose it is and what it is chasing - "You  17/20" (playtest, 2026-09-14).
    /// The target appears once per READER: against the bot only your row carries it; in local
    /// 2-player each player reads their own row, so both do. Mirrored, each side reads from its
    /// own player's point of view ("You: 13/20").
    private void RefreshScoreLines()
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
        string p1 = _host.GameStarted ? P1.CurrentScore.ToString() : "-";
        string p2 = _host.GameStarted ? P2.CurrentScore.ToString() : "-";

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
            SetText(L.P1OpponentScore, $"Them  {p2}");
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
            StyleBox onTarget = (StyleBox)authored?.Duplicate();
            if (onTarget is StyleBoxFlat green)
            {
                green.BgColor = OnTargetFill;
                green.BorderColor = OnTargetEdge;
                green.ShadowColor = OnTargetGlow;
                green.ShadowSize = 14;
            }
            _onTargetBoxStyle[box] = onTarget;
        }

        // Follows the number the box is SHOWING: with a Modifier picked up that is the preview, so
        // a minus card that brings you back under the target clears the red along with the digits.
        // The opponent boxes show the real score, so they never use the preview.
        int? previewed = preview ? PreviewedScore(player) : null;
        int shown = previewed ?? player.CurrentScore;
        bool started = _host.GameStarted;
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
        if (!holding || !_scoreFeedbackPrimed) return;

        _sfxLock?.Play();
        padlock.PivotOffset = LockSize / 2f;
        padlock.Scale = new Vector2(1.8f, 1.8f);
        padlock.Modulate = new Color(1, 1, 1, 0);
        Tween snap = padlock.CreateTween().SetParallel();
        snap.TweenProperty(padlock, "scale", Vector2.One, 0.22f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        snap.TweenProperty(padlock, "modulate:a", 1f, 0.10f);
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

    // ------------------------------------------------------------------
    // Status toasts (pass 36)
    //
    // The messages about a side - what a picked-up effect card would do, "Just recalled",
    // "Holding" - float over the middle of the table instead of taking a spot beside the score.
    // One toast per player, copied from the layout's EffectToast so they share its look: Player
    // 1's just below the middle, Player 2's just above (turned round with P2's side when
    // mirrored). A message about a picked-up card stays while the card is picked up; any other
    // message shows for a moment and fades.
    // ------------------------------------------------------------------
    private PanelContainer _p1StatusToast;
    private PanelContainer _p2StatusToast;
    private readonly string[] _statusShown = new string[2];
    private readonly uint[] _statusToken = new uint[2];

    private const float StatusToastSeconds = 2.5f;

    private void BuildStatusToasts()
    {
        _statusShown[0] = _statusShown[1] = null;
        _statusToken[0]++;
        _statusToken[1]++;
        _p1StatusToast = MakeStatusToast("P1StatusToast");
        _p2StatusToast = MakeStatusToast("P2StatusToast");
    }

    private PanelContainer MakeStatusToast(string name)
    {
        if (L.EffectToast == null) return null;
        PanelContainer toast = (PanelContainer)L.EffectToast.Duplicate();
        toast.Name = name;
        toast.TopLevel = true;
        toast.Visible = false;
        L.EffectToast.GetParent().AddChild(toast);
        return toast;
    }

    private static Label ToastLabel(Control toast)
    {
        foreach (Node child in toast.GetChildren())
            if (child is Label label) return label;
        return null;
    }

    private void UpdateStatusToast(int index, Player player, PanelContainer toast)
    {
        if (toast == null || player == null) return;
        Label label = ToastLabel(toast);
        if (label == null) return;

        string text = _host.StatusFor(player);
        if (string.IsNullOrEmpty(text))
        {
            if (_statusShown[index] != null)
            {
                _statusToken[index]++;
                toast.Visible = false;
            }
            _statusShown[index] = null;
            return;
        }

        bool lasting = _host.SelectedFor(player) != null; // about the card in hand: stays while held
        bool changed = text != _statusShown[index];
        _statusShown[index] = text;
        if (!changed && !lasting) return; // a timed message already had its moment

        label.Text = text;
        ApplyStatusColor(label, player);
        toast.Modulate = Colors.White;
        toast.Visible = true;
        PlaceStatusToast(toast, index == 1);
        Defer(() => PlaceStatusToast(toast, index == 1)); // again once the text has been shaped

        if (changed)
        {
            uint token = ++_statusToken[index];
            if (!lasting) FadeStatusToastLater(toast, index, token);
        }
    }

    private async void FadeStatusToastLater(PanelContainer toast, int index, uint token)
    {
        await _root.ToSignal(_root.GetTree().CreateTimer(StatusToastSeconds), SceneTreeTimer.SignalName.Timeout);
        if (!GodotObject.IsInstanceValid(toast) || token != _statusToken[index]) return;

        Tween fade = toast.CreateTween();
        fade.TweenProperty(toast, "modulate:a", 0f, 0.35f);
        await _root.ToSignal(fade, Tween.SignalName.Finished);
        if (!GodotObject.IsInstanceValid(toast) || token != _statusToken[index]) return;
        toast.Visible = false;
    }

    private void PlaceStatusToast(PanelContainer toast, bool playerTwo)
    {
        if (toast == null || !toast.Visible || !toast.IsInsideTree() || L?.Canvas == null) return;
        Label label = ToastLabel(toast);
        Vector2 vp = _root.GetViewport().GetVisibleRect().Size;
        float width = Mathf.Min(520f, vp.X - 32f);
        if (label != null) label.CustomMinimumSize = new Vector2(Mathf.Max(80f, width - 40f), 0f);
        toast.ResetSize();
        Vector2 size = toast.GetCombinedMinimumSize();
        toast.Size = size;

        // Clear of the effect banner, which sits on the middle itself.
        Vector2 centre = L.Canvas.GetGlobalRect().GetCenter();
        float offset = size.Y / 2f + 70f;
        Vector2 at = centre + new Vector2(-size.X / 2f, (playerTwo ? -offset : offset) - size.Y / 2f);
        at.X = Mathf.Clamp(at.X, 16f, Mathf.Max(16f, vp.X - size.X - 16f));
        at.Y = Mathf.Clamp(at.Y, 16f, Mathf.Max(16f, vp.Y - size.Y - 16f));
        toast.GlobalPosition = at;

        // Player 2's reads from the other side of the table when the side is turned round.
        toast.PivotOffset = size / 2f;
        toast.RotationDegrees = (playerTwo && IsMirrored) ? 180f : 0f;
    }

    /// The three labels of one side's score, as the layout scene has them.
    private sealed class ScoreLines
    {
        public Label YouPrefix;
        public Label YouValue;
        public Label Them;
    }

    private void UpdateConfirmRow(Player player, Control row, Button playButton,
                                  Button flipValueButton, Control actionRow, Control winsRow)
    {
        if (row == null) return;

        Card picked = _host.SelectedFor(player);
        row.Visible = picked != null;
        if (actionRow != null) actionRow.Visible = picked == null;

        bool flippable = picked != null && picked.CanFlipValue;
        if (flipValueButton != null)
        {
            flipValueButton.Disabled = !flippable;
            if (L.Portrait)
            {
                // Portrait: Flip Value stands in the bar's Wins spot, and only while it applies.
                flipValueButton.Visible = flippable;
                Show(winsRow, !flippable);
            }
            else
            {
                // Landscape: it stays in the confirm row, drawn and tappable only when it applies,
                // so its space never opens or closes under the other two buttons.
                flipValueButton.Visible = true;
                flipValueButton.Modulate = flippable ? Colors.White : new Color(1, 1, 1, 0);
                flipValueButton.MouseFilter = flippable ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
                Show(winsRow, true);
            }
        }

        // An effect card that is not legal right now cannot be played at all, so the button says
        // so rather than the card bouncing back with a message. The status line explains why.
        if (playButton != null)
        {
            playButton.Disabled = picked != null && picked.Effect != CardEffect.None
                && !_host.CanPlayEffect(player, picked);
        }
    }

    private void RefreshModifiersUI()
    {
        if (L?.P1Hand == null || L.P2Hand == null || P1 == null) return;

        // Detach immediately, not just QueueFree: queued nodes stay in the tree until the end of
        // the frame and would still count towards the hand's minimum size.
        ClearChildren(L.P1Hand);
        ClearChildren(L.P2Hand);

        bool p1Can = _host.CanAct(P1);
        bool p2Can = _host.CanAct(P2);
        Card p1Picked = _host.SelectedFor(P1);
        Card p2Picked = _host.SelectedFor(P2);

        Vector2 p1Size = ModifierSizeFor(L.P1Hand, P1.Modifiers.Count);
        Vector2 p2Size = ModifierSizeFor(L.P2Hand, P2.Modifiers.Count);
        // Which way a picked-up card rises: toward the table. Only Player 2's side in portrait
        // without the mirror has its hand at the TOP of the screen (mirrored top-to-bottom, not
        // turned round - see ApplyMirror), so there "toward the table" is down.
        float p1Lift = -L.HandLiftOnPick;
        float p2Lift = (L.Portrait && !IsMirrored) ? L.HandLiftOnPick : -L.HandLiftOnPick;

        foreach (Card card in P1.Modifiers)
        {
            L.P1Hand.AddChild(CreateModifierButton(
                card, !p1Can, card == p1Picked, p1Picked != null,
                () => _host.ModifierPressed(P1, card), p1Size, p1Lift));
        }

        foreach (Card card in P2.Modifiers)
        {
            L.P2Hand.AddChild(CreateModifierButton(
                card, !p2Can, card == p2Picked, p2Picked != null,
                () => _host.ModifierPressed(P2, card), p2Size, p2Lift));
        }
    }

    /// The layout's hand card size, made smaller only if this many cards would not fit across the
    /// hand's rect (a fifth card - a rescue - in a hand sized for four).
    private Vector2 ModifierSizeFor(Container hand, int count)
    {
        Vector2 size = ModifierCardSize;
        if (hand == null || count <= 0) return size;
        float gap = hand.GetThemeConstant("separation");
        float width = hand.Size.X;
        float needed = count * size.X + (count - 1) * gap;
        if (width <= 1f || needed <= width) return size;
        float k = Mathf.Max(0.5f, (width - (count - 1) * gap) / (count * size.X));
        return (size * k).Floor();
    }

    private void SetChips(Container chips, int wins)
    {
        if (chips == null) return;
        int i = 0;
        foreach (Node child in chips.GetChildren())
        {
            if (child is TextureRect chip) chip.Texture = i < wins ? _chipWon : _chipEmpty;
            i++;
        }
    }

    /// Repaints the table and the standard cards for the rank the player is on. This is the whole
    /// progression display: a board that looks different every two rungs (Alexander, 2026-09-07).
    public void ApplyRankTheme()
    {
        RunData run = _host.InRun ? RunData.Instance : null;

        // No run (local 2-player, or the scene opened on its own) means the plain felt: the
        // clear colour is global and would otherwise follow us out of the run.
        Color table = (run == null) ? DefaultTableColor : run.CurrentRank.Table;
        _rankCardTint = (run == null) ? Colors.White : run.CurrentRank.CardTint;
        _playmatTint = PlaymatTintFor(table);

        RenderingServer.SetDefaultClearColor(table);
        if (L == null) return;
        if (L.Background != null) L.Background.SelfModulate = _playmatTint;
        // Both decks share Player 1's back for now (the gilded back is a profile reward, so it
        // shows in local 2-player too). Pass 43 gave each player their own deck so that, with
        // online play, each side can show its owner's customised back.
        foreach (TextureRect deck in new[] { L.Deck, L.P2Deck })
        {
            if (deck == null) continue;
            if (_cardBack != null) deck.Texture = _cardBack;
            deck.SelfModulate = DeckBackTint;
        }
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
    // than in a "You  11/20" box in a row of its own. The badge sits on the board it counts,
    // so it needs no "You" or "Them".
    //
    // Face to face, both players read both badges from opposite ends of the phone, so each badge
    // grows a second end turned round (a playing card's two corner indices): its owner reads the
    // near end, the player across the table reads the far end. The badge is then the same both
    // ways up, so it looks like one object rather than a label with an upside-down copy. The far
    // end shows the real score, never the preview of a card the owner has only picked up.
    // ------------------------------------------------------------------
    private void RefreshScoreBadges()
    {
        bool bothRead = !_host.VsBot; // the bot's badge has no reader of its own: no target on it
        SetScoreBadge(P1, L.P1ScoreValue, L.P1ScoreTarget, L.P1ScoreFarValue, L.P1ScoreFarTarget, true, true);
        SetScoreBadge(P2, L.P2ScoreValue, L.P2ScoreTarget, L.P2ScoreFarValue, L.P2ScoreFarTarget, bothRead, bothRead);

        // Pass 42: a bare number beside a board still read as ambiguous in playtest, so each end
        // says whose score it is to the person reading that end. The owner reads the near end;
        // the far end (face to face only) is read from across the table. Against the bot, the
        // bot's near end is read by Player 1.
        // Pass 43: Player 2's badge only says YOU face to face, when Player 2 reads it. Without the
        // mirror the whole table is read from Player 1's end, so it is THEM - "YOU" on both
        // badges was the confusing case in playtest.
        SetCaption(L.P1ScoreCaption, "YOU");
        SetCaption(L.P1ScoreFarCaption, "THEM");
        SetCaption(L.P2ScoreCaption, IsMirrored ? "YOU" : "THEM");
        SetCaption(L.P2ScoreFarCaption, "THEM");
    }

    private static void SetCaption(Label caption, string text)
    {
        if (caption != null) caption.Text = text;
    }

    private void SetScoreBadge(Player player, Label value, Label target, Label farValue, Label farTarget,
                               bool preview, bool withTarget)
    {
        if (player == null) return;
        bool started = _host.GameStarted;
        string targetText = started && withTarget ? $"/{State.TargetScore}" : string.Empty;
        string scoreText = started ? player.CurrentScore.ToString() : "-";

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
        if (farValue != null) farValue.Text = scoreText;
        if (farTarget != null)
        {
            farTarget.Text = targetText;
            farTarget.Visible = targetText.Length > 0;
        }
    }

    /// Pass 43: the turned-round far end is retired - face to face, each player now gets a
    /// "THEM" badge on their own half instead (RefreshOpponentLines), so the far end and its
    /// divider stay hidden in every mode. The nodes are left in the scene in case the idea
    /// comes back. Each badge hugs its content from its anchored edge.
    private void ApplyScoreBadgeEnds(bool faceToFace)
    {
        if (!L.ScoreBadges) return;
        foreach ((Control far, Control divider, Control box) in new[]
                 { (L.P1ScoreFarEnd, L.P1ScoreDivider, L.P1ScoreBox), (L.P2ScoreFarEnd, L.P2ScoreDivider, L.P2ScoreBox) })
        {
            if (far != null) far.Visible = false;
            if (divider != null) divider.Visible = false;
            HugContent(box);
        }
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

    /// The far end is turned round about its own centre. A container resets the rotation of its
    /// own children, so the far end is a plain Control holding the turned content, and takes that
    /// content's size as its minimum so the badge's column still makes room for it.
    private static void TurnRound(Control end)
    {
        if (end == null || end.GetChildCount() == 0 || end.GetChild(0) is not Control turned) return;
        void Fit()
        {
            end.CustomMinimumSize = turned.GetCombinedMinimumSize();
            turned.PivotOffset = turned.Size / 2f;
            turned.RotationDegrees = 180f;
        }
        turned.MinimumSizeChanged += Fit;
        turned.Resized += Fit;
        Fit();
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
            lines.YouValue.Text = withTarget ? ScoreOf(player)
                                             : (_host.GameStarted ? player.CurrentScore.ToString() : "-");
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
        return player.CurrentScore + picked.Value;
    }

    /// A player's score with the target behind it - "17/20" - so "how close am I" is one glance
    /// rather than arithmetic against a number somewhere else on the screen.
    private string ScoreOf(Player player) =>
        _host.GameStarted ? $"{player.CurrentScore}/{State.TargetScore}" : "-";

    private static void SetEnabled(Button button, bool enabled)
    {
        if (button != null) button.Disabled = !enabled;
    }

    private static void HideIfEmpty(Label label)
    {
        if (label != null) label.Visible = !string.IsNullOrEmpty(label.Text);
    }

    /// A side that cannot act right now - the other player (or the bot) is still deciding, it has
    /// ended its turn, or it is holding - shows one disabled "Waiting..." button (playtest, pass
    /// 33): Draw Card changes its words, in its own spot, and Hold steps out of the way.
    private void ApplyWaiting(Button draw, Button hold, bool waiting)
    {
        if (draw != null) draw.Text = waiting ? "Waiting..." : "Draw Card";
        Show(hold, !waiting);
    }

    // ------------------------------------------------------------------
    // Tutorial emphasis (pass 49)
    //
    // When a tutorial step has one right answer, the table says so: that control pulses - grows
    // and shrinks a little - and the wrong answer is greyed out. Teaching sets these; the table
    // only draws them. The pulse is a looping tween owned by the node it scales, so it dies with
    // the node (hand cards are rebuilt on every refresh and simply get a fresh one).
    // ------------------------------------------------------------------

    /// Player 1's Draw Card is disabled - "you are exactly on the target, Hold".
    public bool TutorialLockDraw;

    /// Player 1's Hold button pulses.
    public bool TutorialPulseHold;

    /// This card in Player 1's hand pulses (the Modifier the lesson is about).
    public Card TutorialPulseCard;

    private const float PulseScale = 1.08f;
    private const float PulseHalfSeconds = 0.45f;

    private Tween _holdPulse;

    /// Player 1's Hold button, for the tutorial's hole.
    public Control P1HoldButton => L?.P1Hold;

    /// The button in Player 1's hand showing this card, or null. The hand is rebuilt in the order
    /// of the player's Modifiers, so the index is the same.
    public Control P1HandCardFor(Card card)
    {
        if (L?.P1Hand == null || card == null || P1 == null) return null;
        int i = P1.Modifiers.IndexOf(card);
        return (i >= 0 && i < L.P1Hand.GetChildCount()) ? L.P1Hand.GetChild(i) as Control : null;
    }

    /// Pulses about the target's pivot - set it to the centre first.
    private static Tween StartPulse(Control target)
    {
        Tween tween = target.CreateTween().SetLoops();
        tween.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        tween.TweenProperty(target, "scale", new Vector2(PulseScale, PulseScale), PulseHalfSeconds);
        tween.TweenProperty(target, "scale", Vector2.One, PulseHalfSeconds);
        return tween;
    }

    private void ApplyHoldPulse(bool on)
    {
        Button hold = L?.P1Hold;
        if (on && hold != null)
        {
            hold.PivotOffset = hold.Size / 2f; // every refresh, so a rotation re-centres it
            if (_holdPulse == null || !_holdPulse.IsValid()) _holdPulse = StartPulse(hold);
            return;
        }
        if (_holdPulse != null && _holdPulse.IsValid()) _holdPulse.Kill();
        _holdPulse = null;
        if (hold != null) hold.Scale = Vector2.One;
    }

    /// Colours the status line for a picked-up EFFECT card (green: playable, red: not). A plain
    /// Modifier's preview is on the score line instead (SetScoreLines).
    private void ApplyStatusColor(Label label, Player player)
    {
        if (label == null) return;

        Card picked = _host.SelectedFor(player);
        if (picked == null)
        {
            label.RemoveThemeColorOverride("font_color");
            return;
        }

        // An effect card is not added to this player's score, so "would this bust me" is the wrong
        // question: colour it by whether it can be played at all.
        if (picked.Effect != CardEffect.None)
        {
            label.AddThemeColorOverride("font_color", _host.CanPlayEffect(player, picked)
                ? new Color(0.55f, 0.95f, 0.60f)
                : new Color(1f, 0.45f, 0.42f));
            return;
        }

        // A plain Modifier's preview lives on the score line (ScorePreviewColor), not here.
        label.RemoveThemeColorOverride("font_color");
    }

    private static Vector2 Bigger(Vector2 a, Vector2 b) =>
        new Vector2(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y));

    // ------------------------------------------------------------------
    // The middle panel
    // ------------------------------------------------------------------
    /// Centres the floating banner on the middle panel. Its width is chosen here (autowrap needs
    /// one), and its height follows from the text.
    private void PlaceEffectToast()
    {
        if (_effectToast == null || _effectBanner == null || !_effectToast.Visible || !_effectToast.IsInsideTree()) return;
        Control column = L?.Canvas ?? _effectToast.GetParent() as Control;
        if (column == null) return;

        Vector2 vp = _root.GetViewport().GetVisibleRect().Size;
        float width = Mathf.Min(560f, vp.X - 32f);
        _effectBanner.CustomMinimumSize = new Vector2(Mathf.Max(80f, width - 40f), 0f);
        _effectToast.ResetSize(); // shrink back to the new text before measuring
        Vector2 size = _effectToast.GetCombinedMinimumSize();
        _effectToast.Size = size;

        Vector2 centre = column.GetGlobalRect().GetCenter();
        Vector2 at = centre - size / 2f;
        at.X = Mathf.Clamp(at.X, 16f, Mathf.Max(16f, vp.X - size.X - 16f));
        at.Y = Mathf.Clamp(at.Y, 16f, Mathf.Max(16f, vp.Y - size.Y - 16f));
        _effectToast.GlobalPosition = at;
    }


    private void UpdateTargetLabel()
    {
        if (_targetLabel == null) return;

        if (!_host.GameStarted)
        {
            _targetLabel.Text = string.Empty;
            _targetLabel.RemoveThemeColorOverride("font_color");
            return;
        }

        // The target lives on each player's own score line now ("17 / 20"), so this banner is no
        // longer where the target is READ - it is where the game says the target has MOVED. A
        // permanent "TARGET 20" here as well was one line of the clutter the playtest complained
        // about, and it said nothing the score line does not say closer to the number it governs.
        RunData run = _host.InRun ? RunData.Instance : null;

        // The finale's rules were rolled, so they are news every time - say them whether or not
        // the target happens to have moved.
        if (run != null && run.CurrentRolledEffects != null)
        {
            string heading = run.Endless ? "ENDLESS" : "FINAL";
            _targetLabel.Text = $"{heading}  -  TARGET {State.TargetScore}{_host.FinaleRulesLine(run, "\n")}";
            _targetLabel.AddThemeColorOverride("font_color", TableGold);
            return;
        }

        if (run == null || !run.TargetMovedThisStage)
        {
            _targetLabel.Text = string.Empty;
            _targetLabel.RemoveThemeColorOverride("font_color");
            return;
        }

        // A target that changes quietly is the game changing its own rules behind the player's back.
        string direction = run.CurrentTarget > run.PreviousTarget ? "up" : "down";
        _targetLabel.Text = $"TARGET  {State.TargetScore}   ({direction} from {run.PreviousTarget})";
        _targetLabel.AddThemeColorOverride("font_color", TableGold);
    }

    /// What an effect card just did, on the table. The explanation already existed - CardEffects
    /// writes one for every card it resolves - but it only ever went to the log, so at the table
    /// a score simply changed and nothing said why.
    public async void ShowEffectBanner(string text)
    {
        if (_effectBanner == null || string.IsNullOrEmpty(text)) return;

        uint token = ++_effectBannerToken;
        _effectBanner.Text = text;
        if (_effectToast != null)
        {
            _effectFade?.Kill(); // an older banner's fade must not carry on into this one
            _effectToast.Modulate = Colors.White;
            _effectToast.Visible = true;
            PlaceEffectToast();
            Defer(PlaceEffectToast); // again once the new text has been shaped
        }

        await _root.ToSignal(_root.GetTree().CreateTimer(5.0f), SceneTreeTimer.SignalName.Timeout);
        if (!_root.IsInsideTree() || token != _effectBannerToken) return; // a newer card owns the line

        // A short fade rather than a blink, so a player who was looking elsewhere sees it leave.
        if (_effectToast != null)
        {
            Tween fade = _effectFade = _effectToast.CreateTween();
            fade.TweenProperty(_effectToast, "modulate:a", 0f, 0.35f);
            await _root.ToSignal(fade, Tween.SignalName.Finished);
            if (!_root.IsInsideTree() || token != _effectBannerToken) return;
            _effectToast.Visible = false;
        }
        _effectBanner.Text = string.Empty;
    }

    public void ClearEffectBanner()
    {
        _effectBannerToken++;
        if (_effectBanner != null) _effectBanner.Text = string.Empty;
        _effectFade?.Kill();
        if (_effectToast != null) _effectToast.Visible = false;
    }

    /// A tappable modifier card: an invisible Button (so the theme's touch-friendly hit area
    /// and focus handling still apply) with the card art drawn on top. A picked-up card is lifted
    /// and the rest of the hand dims, so which card is in play is obvious without reading anything.
    private Button CreateModifierButton(Card card, bool disabled, bool selected, bool anySelected, Action onPressed,
                                        Vector2 size, float lift)
    {
        Button button = new Button
        {
            Flat = true,
            CustomMinimumSize = size,
            Disabled = disabled,
            FocusMode = Control.FocusModeEnum.None,
        };
        StyleBoxEmpty empty = new StyleBoxEmpty();
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
            button.AddThemeStyleboxOverride(state, empty);
        button.Pressed += onPressed;

        TextureRect view = CreateCardView(card, size);
        view.MouseFilter = Control.MouseFilterEnum.Ignore;

        if (disabled) view.Modulate = new Color(0.55f, 0.55f, 0.55f);
        else if (anySelected && !selected) view.Modulate = new Color(0.5f, 0.5f, 0.55f); // the rest of the hand steps back
        else view.Modulate = Colors.White;

        button.AddChild(view);
        view.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Pass 49: the tutorial's card pulses until it is picked up. The art is scaled, not the
        // Button, for the same reason as the pick-up below.
        // A tween needs the tree, and this button is not in it yet: start once it arrives.
        if (!selected && !disabled && card == TutorialPulseCard)
        {
            view.PivotOffset = size / 2f;
            view.TreeEntered += () => StartPulse(view);
        }

        if (selected)
        {
            // Scale the art, not the Button: the Button is a container child and would have its
            // scale reset on the next layout pass, and growing it would shove the whole hand about.
            view.PivotOffset = size / 2f;
            view.Scale = new Vector2(1.18f, 1.18f);
            // Pass 40: and it rises out of the cropped hand, so the whole card shows while you decide.
            view.OffsetTop += lift;
            view.OffsetBottom += lift;

            // Pass 31: an accent ring just outside the card, the colour every "do this next"
            // thing on screen shares, instead of a yellow box drawn over the art.
            StyleBoxFlat outline = new StyleBoxFlat
            {
                DrawCenter = false,
                BorderColor = new Color(0.45f, 0.8f, 1f),
                AntiAliasingSize = 1.2f,
            };
            outline.SetCornerRadiusAll(12);
            outline.SetBorderWidthAll(4);
            outline.SetExpandMarginAll(3);

            Panel highlight = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            highlight.AddThemeStyleboxOverride("panel", outline);
            view.AddChild(highlight);
            highlight.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }

        return button;
    }


    // ------------------------------------------------------------------
    // Card views
    // ------------------------------------------------------------------
    private Texture2D FaceFor(Card card)
    {
        if (card.Type == CardType.Main) return _faceMain;
        // Before the sign test: a Shave carries Value 1 and would otherwise wear the blue "plus"
        // face, which is the opposite of what it does.
        if (card.Effect != CardEffect.None) return _faceEffect;
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

    private static Color PlaymatTintFor(Color table)
    {
        // The plain table (2-player, and the first rank) is the mat exactly as it was painted.
        if (table.IsEqualApprox(DefaultTableColor)) return Colors.White;
        return new Color(
            Mathf.Min(table.R / PlaymatAverage.R, 3f),
            Mathf.Min(table.G / PlaymatAverage.G, 3f),
            Mathf.Min(table.B / PlaymatAverage.B, 3f));
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

    private void ApplyCardSize(TextureRect view, Vector2 size)
    {
        view.CustomMinimumSize = size;

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

    public TextureRect CreateCardView(Card card, Vector2 size)
    {
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
        if (card.Type == CardType.Main) view.SelfModulate = _rankCardTint;

        // A rescue card is not one the player owns, and may carry a value no bought card can.
        if (card.IsRescue && card.Effect == CardEffect.None) view.SelfModulate = RescueTint;

        // An effect card is a Modifier, so this never fights the rank tint above.
        if (card.Effect != CardEffect.None)
        {
            view.SelfModulate = EffectTint;
            view.SetMeta("effectCard", true); // ApplyCardSize gives its longer mark a smaller font
            ApplyCardSize(view, size);        // re-run now that the meta is set
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
        return view;
    }

    /// Redraws the face of a card that is already on a board, after something changed its Value.
    /// Silent when the card is not on this board - a Modifier has no view to redraw, and that is
    /// not an error.
    public void RefreshCardFace(Card card, Control board)
    {
        TextureRect view = FindCardView(card, board);
        if (view == null) return;

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
            BuildPips(view, card.Value, BoardCardSizeFor(board));
        }
        if (view.HasMeta(SignedPipsMeta))
        {
            MarkSignedFace(view, card);
            if (view.HasMeta(SignedPipsMeta)) BuildSignedFace(view, BoardCardSizeFor(board), view.HasNode("FlipValueBottom"));
        }
    }

    /// Takes a card off a board with an animation that reads as DESTROYED rather than moved: it
    /// reddens, shrinks toward its own middle and fades where it sits, then frees itself. Veto's
    /// only, and the one place in the game a card leaves a board before the set is over.
    ///
    /// The node is deliberately NOT pulled out of its slot up front. Leaving the slot occupied
    /// while it burns is what stops the incoming Veto card dropping into the hole and landing on
    /// top of the very thing the player is meant to be watching; the slot frees itself when the
    /// tween finishes, and the board is rebuilt at the set boundary anyway.
    public void BurnCardView(Card card, Control board)
    {
        TextureRect view = FindCardView(card, board);
        if (view == null) return;

        if (!GameSettings.CardAnimations)   // Options > Battery: the card simply goes
        {
            view.GetParent()?.RemoveChild(view);
            view.QueueFree();
            RefreshBoardSlots(board);
            return;
        }

        view.PivotOffset = view.Size / 2f; // shrink toward the middle, not the top-left corner

        Tween tween = _root.GetTree().CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(view, "modulate", new Color(1f, 0.3f, 0.25f, 0f), 0.45f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.In);
        tween.TweenProperty(view, "scale", new Vector2(0.5f, 0.5f), 0.45f)
             .SetTrans(Tween.TransitionType.Back)
             .SetEase(Tween.EaseType.In);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(view)) return;
            view.GetParent()?.RemoveChild(view); // empties the slot for the next card
            view.QueueFree();
            if (GodotObject.IsInstanceValid(board)) RefreshBoardSlots(board); // may close row 3
        }));
    }

    /// The view of a card on either board, for the coach mark to point at.
    public Control BoardCardView(Card card) =>
        FindCardView(card, L?.P2Board) ?? FindCardView(card, L?.P1Board);

    /// The view showing this exact card, or null. Cards live one-per-slot (FillBoardWithSlots),
    /// so this is a walk of nine slots rather than a search.
    private TextureRect FindCardView(Card card, Control board)
    {
        if (card == null || board == null) return null;

        foreach (Node slot in board.GetChildren())
        {
            foreach (Node child in slot.GetChildren())
            {
                if (child is TextureRect view && view.HasMeta("cardId")
                    && view.GetMeta("cardId").AsInt32() == card.Id)
                {
                    return view;
                }
            }
        }
        return null;
    }

    public void InstantiateCardView(Card card, Control parentContainer, float delay = 0f)
    {
        if (_cardViewScene == null) return;

        TextureRect cardNode = CreateCardView(card, BoardCardSizeFor(parentContainer));

        // Drop the card into the next empty slot; if the board is somehow full, let the grid grow.
        // In portrait the seventh card opens the third row, and RefreshBoardSlots below re-sizes
        // every card on the board for it - this one included.
        Control slot = FindFreeSlot(parentContainer);
        cardNode.Modulate = new Color(1, 1, 1, 0); // invisible until the turn animation lands
        if (slot != null)
        {
            slot.AddChild(cardNode);
            cardNode.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        else
        {
            parentContainer.AddChild(cardNode);
        }
        RefreshBoardSlots(parentContainer);

        // Defer the animation by one frame so Godot has time to calculate its final Grid position
        Vector2 flightSize = BoardCardSizeFor(parentContainer);
        Control fromDeck = DeckFor(parentContainer);
        Defer(() => AnimateCardDrop(cardNode, delay, flightSize, fromDeck));
    }

    /// Each player draws from their own deck (pass 43), so a card flies from the deck on its
    /// owner's side. A layout without a Player 2 deck falls back to Player 1's.
    private Control DeckFor(Control board) =>
        (board == L?.P2Board && L?.P2Deck != null) ? L.P2Deck : _mainDeckPosition;

    private void AnimateCardDrop(Control realCard, float delay, Vector2 cardSize, Control fromDeck)
    {
        // Fallback in case the deck isn't assigned in the inspector
        if (fromDeck == null || !GodotObject.IsInstanceValid(realCard))
        {
            if (GodotObject.IsInstanceValid(realCard)) realCard.Modulate = Colors.White;
            return;
        }

        // Options > Battery: no flight. The card appears in place, with its sound, after the
        // same stagger - so a two-card opening still lands one card then the other.
        if (!GameSettings.CardAnimations)
        {
            if (delay <= 0f) { RevealWithoutFlight(realCard); return; }
            _root.GetTree().CreateTimer(delay).Timeout += () => RevealWithoutFlight(realCard);
            return;
        }

        // 1. A face-down card that flies from the deck to the slot
        TextureRect fakeCard = new TextureRect
        {
            Texture = _cardBack,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Size = cardSize,
            PivotOffset = cardSize / 2f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SelfModulate = DeckBackTint,
        };
        _root.AddChild(fakeCard); // on the scene root so it draws above everything

        // 2. Start on the deck, small and upside down
        Vector2 deckCenter = fromDeck.GetGlobalTransform() * (fromDeck.Size / 2f);
        fakeCard.GlobalPosition = deckCenter - cardSize / 2f;
        fakeCard.RotationDegrees = -180f;
        fakeCard.Scale = new Vector2(0.5f, 0.5f);

        // Target the slot's visual centre. Player 2's side may be rotated 180 degrees, so
        // go through the full global transform instead of GlobalPosition.
        Vector2 targetCenter = realCard.GetGlobalTransform() * (realCard.Size / 2f);
        float targetRotation = Mathf.RadToDeg(realCard.GetGlobalTransform().Rotation);

        // A tilted board draws the card somewhere else than its slot's flat rect (pass 39), and
        // narrower: aim for where it will be SEEN, at the size it will be seen.
        Vector2 landScale = Vector2.One;
        if (FindBoardOf(realCard) is PerspectiveBoard tilted)
        {
            Vector2 flatCenter = targetCenter;
            targetCenter = tilted.Warp(flatCenter);
            float s = tilted.Tilt ? tilted.WidthScaleAt(flatCenter) : 1f;
            landScale = new Vector2(s, tilted.Tilt ? tilted.Depth : 1f);
        }

        // 3. Fly, spin and grow at the same time, then reveal the real card
        Tween tween = _root.GetTree().CreateTween();
        tween.SetParallel(true);

        // The stagger sits in its own step, and Chain() forces the flight into the NEXT one.
        // Without the Chain the first property tweener would join the interval's step - that is
        // what SetParallel(true) means - and the card would fly during the pause instead of after.
        if (delay > 0f)
        {
            tween.TweenInterval(delay);
            tween.Chain();
        }

        // The slide belongs to the moment the card LEAVES the deck, not the moment the tween is
        // built. Played up front, a staggered card announces itself before it moves.
        tween.TweenCallback(Callable.From(() => { if (_sfxSlide != null) _sfxSlide.Play(); }));
        tween.TweenProperty(fakeCard, "global_position", targetCenter - cardSize / 2f, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(fakeCard, "rotation_degrees", targetRotation, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(fakeCard, "scale", landScale, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            fakeCard.QueueFree();
            if (GodotObject.IsInstanceValid(realCard)) realCard.Modulate = Colors.White;
            if (_sfxPlace != null) _sfxPlace.Play();
        }));
    }

    private static Control FindBoardOf(Node node)
    {
        for (Node at = node?.GetParent(); at != null; at = at.GetParent())
            if (at is GridContainer grid) return grid;
        return null;
    }

    private void RevealWithoutFlight(Control realCard)
    {
        if (!GodotObject.IsInstanceValid(realCard)) return;
        realCard.Modulate = Colors.White;
        if (_sfxPlace != null) _sfxPlace.Play();
    }
}
