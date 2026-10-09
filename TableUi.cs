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

    /// This hand card would land the player exactly on the target now - the table pulses it.
    bool HitsTarget(Player player, Card card);

    /// The words for the status line and the middle panel. Both are readings of the rules, so the
    /// rules write them; the table only decides how big they are and what colour.
    string StatusFor(Player player);
    string SetInfoLine();
    /// The middle line is the target (offline), not the turn clock (online).
    bool SetInfoIsTarget { get; }

    /// A picked-up card that can no longer be played is dropped before anything is drawn, so the
    /// status line and the buttons agree. First thing in every refresh.
    void ValidateSelections();

    /// The table has just been repainted. Anything watching the screen for progress - the
    /// tutorial, the coach marks - gets its look here.
    void AfterRefresh();

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
/// Four helpers each own one part of the picture, so this class stays about the layout:
///  - CardViews: what a card looks like (faces, pips, shine) - also used by the shop and prompts;
///  - CardMotion: cards flying from the deck or the hand onto a board;
///  - ScoreDisplay: the scores, the red/green boxes and the hold padlock;
///  - Toasts: the status toasts and the effect banner.
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

        Cards = new CardViews(() => _host.InRun);
        _chipWon = GD.Load<Texture2D>(CardViews.ArtDir + "ui/chip_won.png");
        _chipEmpty = GD.Load<Texture2D>(CardViews.ArtDir + "ui/chip_empty.png");
        _sfxSlide = CreateSfx("res://assets/kenney/sfx/cardSlide1.ogg");
        _sfxPlace = CreateSfx("res://assets/kenney/sfx/cardPlace1.ogg");
        _sfxLock = CreateSfx("res://assets/sfx/hold_lock.wav");
        _sfxOnTarget = CreateSfx("res://assets/sfx/on_target.wav");
        _sfxImpact = CreateSfx("res://assets/sfx/modifier_impact.wav");
        Toasts = new Toasts(_host, this, _root);
        Scores = new ScoreDisplay(_host, this, _sfxLock, _sfxOnTarget);
        _motion = new CardMotion(this, _root, _sfxSlide, _sfxPlace, _sfxImpact);
        Moments = new TableMoments(_root, _sfxSlide, _sfxImpact);
        _root.AddChild(new ShineDriver { Name = "ShineDriver" });
        World3D = new TableWorld3D { Name = "TableWorld3D", Ui = this };
        _root.AddChild(World3D);
        _root.AddChild(new FpsCounter { Name = "FpsCounter" });
    }

    /// What every card looks like, wherever it is drawn (the table, the shop, the prompts).
    public CardViews Cards { get; }

    /// The status toasts and the effect banner.
    public Toasts Toasts { get; }

    /// The scores, the red/green boxes and the padlocks.
    public ScoreDisplay Scores { get; }

    /// Cards flying onto the boards.
    private readonly CardMotion _motion;

    /// The big moments - Hold, Shave, Copy, Trade Hands - and the gate that holds the game while
    /// one plays (TableMoments).
    public TableMoments Moments { get; }

    /// The 3D table (prototype): draws the playmat, boards and cards in perspective under the 2D
    /// layer while GameSettings.Table3D is on. See TableWorld3D.
    public TableWorld3D World3D { get; }

    /// A match is on the table (the start menu is not covering it).
    public bool MatchLive => _host.GameStarted;

    // ------------------------------------------------------------------
    // What other classes point at. The tutorial's spotlight and a coach mark's arrow get the
    // node, never the right to change it. All of them follow the layout across a rotation.
    // ------------------------------------------------------------------
    public Control P1ScoreBlock => L?.P1ScoreBox;
    public Control DeckFootprint => L?.DeckFootprint ?? L?.Deck;
    /// The deck itself - the control the 3D table mirrors, so the one to ask where the deck is drawn.
    public Control DeckView => L?.Deck;
    public Control P1ActionRow => L?.P1ButtonSlot;
    public Control P1Hand => L?.P1Hand;
    public Control P1FlipValueButton => L?.P1FlipValue;
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

    /// The shape every card size in the game is derived from (the art is 140x190).
    public static readonly Vector2 BaseCardSize = new Vector2(84, 114);

    /// An ordinary card off the board: the flying card, and what the shop and deck screens scale.
    public Vector2 CardSize => L?.CardSize ?? BaseCardSize;

    /// A card in a hand, as the layout scene sets it.
    public Vector2 ModifierCardSize => L?.HandCardSize ?? BaseCardSize;

    // The layout's nodes under the names the painting code below has always used.
    private Control _mainDeckPosition => L?.Deck;
    private Label _setInfoLabel => L?.SetInfoLabel;
    public const int SetInfoFont = 28;        // the scenes' own size for the middle line
    private const int SetInfoTargetFont = 64; // ...and the target, which is all it shows mid-set
    private Label _targetLabel => L?.TargetLabel;
    private Control _p1BoardContainer => L?.P1Board;
    private Control _p2BoardContainer => L?.P2Board;

    // ------------------------------------------------------------------
    // The table's own art and colours. The cards' are in CardViews.
    // ------------------------------------------------------------------

    // Win markers: an empty ring, and a gold coin carrying the card back's diamond.
    private Texture2D _chipWon;
    private Texture2D _chipEmpty;

    /// The target on the table: gold on the dark mat.
    /// The ink for words printed straight onto the board (the target, the stage line). Dark,
    /// because the boards are light (2026-10-02); it was gold on dark felt.
    private static readonly Color BoardInk = new Color(0.2f, 0.27f, 0.36f);

    // ------------------------------------------------------------------
    // The board's emblem: the live target (2026-10-08, Chuck's playtest)
    // ------------------------------------------------------------------
    // The painted "20" in the board's disc read as the target even on a stage where the target was
    // 23, so the art's disc is empty now (cardgen build_playmat) and the game writes the CURRENT
    // target into it - ON the board, under the cards, like the paint was: a Label behind the canvas
    // on the flat table, a Label3D lying on the 3D table (TableWorld3D.SetEmblem).

    /// The number's size as a fraction of the board's short side, in the painted "20"'s font
    /// (Fredoka Bold). Godot's em runs larger than Blender's text size, so 0.31 here matches the
    /// width the art's 0.36 had and leaves the disc a margin round two digits.
    public const float EmblemEm = 0.31f;
    /// Nudged down by this much of the short side (0: centred reads right on the tilted table).
    public const float EmblemDrop = 0f;
    public static readonly Font EmblemFont = ResourceLoader.Exists("res://assets/fonts/Fredoka-Bold.ttf")
        ? GD.Load<Font>("res://assets/fonts/Fredoka-Bold.ttf") : null;
    private const string EmblemNodeName = "EmblemNumber";

    /// What the emblem says right now: the target, offline, while the middle of the table is not
    /// using the space for words (a set ending, the finale's rules) - empty otherwise. Online the
    /// middle is the turn clock, so the emblem stays plain there.
    private void UpdateEmblem()
    {
        bool middleBusy = (_setInfoLabel != null && _setInfoLabel.Visible)
                          || (_targetLabel != null && _targetLabel.Visible);
        bool show = _host.SetInfoIsTarget && !State.IsGameOver && !_host.SetOverPending && !middleBusy;
        string text = show ? $"{State.TargetScore}" : string.Empty;
        Color ink = EmblemInk;

        Label flat = EmblemLabel();
        if (flat != null)
        {
            flat.Text = text;
            flat.AddThemeColorOverride("font_color", ink);
            FitEmblem(flat);
        }
        World3D?.SetEmblem(text, ink);
    }

    /// The flat table's emblem label, made on first use as a child of the board's own picture, so it
    /// draws above the art and below everything on the canvas (and hides with it on the 3D table).
    private Label EmblemLabel()
    {
        TextureRect board = L?.Background;
        if (board == null) return null;
        if (board.GetNodeOrNull<Label>(EmblemNodeName) is Label existing) return existing;

        Label label = new Label
        {
            Name = EmblemNodeName,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            UseParentMaterial = false,
        };
        if (EmblemFont != null) label.AddThemeFontOverride("font", EmblemFont);
        // Printed into the board, not stuck on it: none of the theme's label shadow or outline.
        label.AddThemeColorOverride("font_shadow_color", Colors.Transparent);
        label.AddThemeConstantOverride("outline_size", 0);
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        board.AddChild(label);
        board.Resized += () => FitEmblem(label);
        return label;
    }

    private static void FitEmblem(Label label)
    {
        if (label.GetParent() is not Control board) return;
        float shortSide = Mathf.Min(board.Size.X, board.Size.Y);
        if (shortSide <= 0f) return;
        label.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(EmblemEm * shortSide));
        float drop = EmblemDrop * shortSide;
        label.OffsetTop = drop;
        label.OffsetBottom = drop;
    }

    // Each board's rim colour (cardgen MATS - keep in step), so the number reads as part of the
    // board it sits on. Nudged a little toward BoardInk so the pale boards (Gold, Silver) still
    // read it at a glance.
    private static readonly Dictionary<string, Color> BoardRims = new Dictionary<string, Color>
    {
        ["classic"] = new Color("7cbdea"),
        ["bronze"] = new Color("86bf98"),
        ["silver"] = new Color("a3b5c6"),
        ["gold"] = new Color("e9c055"),
        ["ruby"] = new Color("df8797"),
        ["obsidian"] = new Color("9a82d3"),
        ["endless"] = new Color("b3a9ea"),
    };
    private string _boardKey; // the painted board on show, or null for the old tinted mat

    private Color EmblemInk =>
        (_boardKey != null && BoardRims.TryGetValue(_boardKey, out Color rim))
            ? rim.Lerp(BoardInk, 0.12f)
            : BoardInk;

    /// An empty win chip, inked so its outline reads on a light board.
    private static readonly Color EmptyChipInk = new Color(0.29f, 0.37f, 0.47f, 0.75f);

    // The collection log's reward. There is one back now, so the reward is the gilding alone.
    private static readonly Color CollectorBackTint = new Color(1.45f, 1.2f, 0.45f);

    /// The mat's average colour. A rank's table colour divided by this is the tint that turns the
    /// teal mat into that rank's felt.
    private static readonly Color PlaymatAverage = new Color(0.114f, 0.227f, 0.243f);

    /// The felt colour of the 2-player table and of stage 1 - project.godot's clear colour.
    private static readonly Color DefaultTableColor = new Color(0.07f, 0.24f, 0.13f);

    private Color _playmatTint = Colors.White;

    /// The face-down deck: the rank's own back, or the collection reward when it is switched on.
    private bool GildedDeck => RunData.Instance != null && RunData.Instance.UseCollectorBack;

    internal Color DeckBackTint => GildedDeck ? CollectorBackTint : Cards.RankCardTint;

    private AudioStreamPlayer _sfxSlide;
    private AudioStreamPlayer _sfxPlace;
    private AudioStreamPlayer _sfxLock;      // Hold: the padlock snapping shut
    private AudioStreamPlayer _sfxOnTarget;  // a picked-up Modifier lands exactly on the target
    private AudioStreamPlayer _sfxImpact;    // a Modifier slammed down from the hand

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
        Defer(Toasts.PlaceEffectToast);
        Defer(() => { Toasts.PlaceStatusToast(Toasts.P1StatusToast, false); Toasts.PlaceStatusToast(Toasts.P2StatusToast, true); });
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

        // A moment mid-flight is aimed at the old layout's nodes: land it before they go.
        Moments.FinishNow();

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
            OverlayUi.StyleButton(flip, ring: CardViews.InkFlip);
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
        Toasts.BuildStatusToasts();
        Scores.Bind();
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

    internal void Show(Control c, bool show)
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

        Scores.ApplyScoreBadgeEnds(mirrored);
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
    // Portrait shows two rows of three (pass 32). The SIXTH card opens the third row, and every
    // card on THAT board shrinks so three rows fill exactly the height two did - so nothing else
    // on the table moves mid-set. The board goes back to two rows when it is cleared.
    //
    // Sixth, not seventh (playtest, 2026-10-08: "it wasn't clear you can play more than 6
    // cards"). A full two-row board read as a full board; opening the row as the last of the six
    // slots fills shows three empty slots waiting, so the board itself says there is room.
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
            if (i++ >= PortraitSlots - 1 && child.GetChildCount() > 0) return true;
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
                if (inner is TextureRect view) Cards.ApplyCardSize(view, size);
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
            TextureRect view = Cards.CreateCardView(card, BoardCardSizeFor(board), board == L?.P2Board);
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

        Scores.RefreshScoreLines();
        UpdateTargetLabel();

        Scores.RefreshBoxes();
        Toasts.UpdateStatusToast(0, P1, Toasts.P1StatusToast);
        Toasts.UpdateStatusToast(1, P2, Toasts.P2StatusToast);

        SetChips(L.P1Chips, State.SetsWonPlayer1);
        SetChips(L.P2Chips, State.SetsWonPlayer2);

        // While the set-end explanation is up, EndSet owns this label.
        if (_host.GameStarted && _setInfoLabel != null && !State.IsGameOver && !_host.SetOverPending)
        {
            // When the target line has news (the target moved, or the finale's rules), it already
            // names the target: a big "Target 23" over "Target 23 (up from 20)" said it twice
            // (playtest, 2026-10-06). The one line is enough.
            bool targetLineSpeaks = _targetLabel != null && !string.IsNullOrEmpty(_targetLabel.Text);
            _setInfoLabel.Text = targetLineSpeaks ? string.Empty : _host.SetInfoLine();
            // The target is the one number in the middle of the table: big while it is the target,
            // the scene's own size again while EndSet uses the label for a sentence.
            _setInfoLabel.AddThemeFontSizeOverride("font_size", SetInfoTargetFont);
        }
        // The middle panel's lines take no room while they have nothing to say (local 2-player
        // has no stage line, and the target line only speaks when the target moved).
        HideIfEmpty(_setInfoLabel);
        HideIfEmpty(_targetLabel);
        UpdateEmblem();

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
                () => _host.ModifierPressed(P1, card), p1Size, p1Lift,
                pulse: card == TutorialPulseCard || _host.HitsTarget(P1, card)));
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
            if (child is TextureRect chip)
            {
                chip.Texture = i < wins ? _chipWon : _chipEmpty;
                chip.Modulate = i < wins ? Colors.White : EmptyChipInk;
            }
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
        Cards.RankCardTint = (run == null) ? Colors.White : run.CurrentRank.CardTint;
        _playmatTint = PlaymatTintFor(table);

        // Whatever shows round the board is the same near-white room in 2D and 3D (2026-10-02:
        // the boards are light now, and the old dark rank colour read as a grey surround).
        RenderingServer.SetDefaultClearColor(TableWorld3D.RoomColor);
        if (L == null) return;
        // A rank's own painted playmat (playmats/playmat_<rank>_<portrait|landscape>.png) replaces the
        // tinted shared one; without it the shared mat is tinted as before.
        if (L.Background != null)
        {
            // The layout's own mat is remembered on the node, to go back to from a painted one.
            if (!L.Background.HasMeta("authoredMat")) L.Background.SetMeta("authoredMat", L.Background.Texture);
            Texture2D authored = L.Background.GetMeta("authoredMat").As<Texture2D>();
            // In a ladder stage the table is the STAGE's; anywhere else it is the player's own board.
            string boardKey = Cards.RankKey ?? RunData.Instance?.SelectedBoard ?? Cosmetics.Default;
            Texture2D mat = Cards.Art($"playmats/playmat_{boardKey}_{(L.Portrait ? "portrait" : "landscape")}.png");
            L.Background.Texture = mat ?? authored;
            _boardKey = mat != null ? boardKey : null;
            L.Background.SelfModulate = mat != null ? Colors.White : _playmatTint;
            ApplyBoardFx(L.Background, mat != null);
        }
        // Both decks share Player 1's back for now (the gilded back is a profile reward, so it
        // shows in local 2-player too). Pass 43 gave each player their own deck so that, with
        // online play, each side can show its owner's customised back.
        // Your deck is yours: its back is your chosen deck's (or the gilded back, once earned and
        // switched on). The opponent in a ladder stage uses the stage's deck; in local 2-player
        // both players share yours.
        SetDeckBack(L.Deck, GildedDeck ? null : Cards.Art($"backs/card_back_{CardViews.PlayerDeckKey}.png"));
        SetDeckBack(L.P2Deck, Cards.Art($"backs/card_back_{Cards.DeckKeyFor(true)}.png"));
    }

    // The board's live layer on the flat table (board_2d.gdshader): the same rim gleam, lattice
    // and rings as the 3D board. Only on a painted board - the old shared mat has no rim to light.
    private static ShaderMaterial _boardFx2D;

    private static void ApplyBoardFx(TextureRect background, bool painted)
    {
        if (!painted)
        {
            background.Material = null;
            return;
        }
        if (_boardFx2D == null && ResourceLoader.Exists("res://board_2d.gdshader"))
            _boardFx2D = new ShaderMaterial { Shader = GD.Load<Shader>("res://board_2d.gdshader") };
        if (_boardFx2D == null) return;
        background.Material = _boardFx2D;
        void Fit() => _boardFx2D.SetShaderParameter("board_size", background.Size);
        if (!background.HasMeta("boardFx"))
        {
            background.SetMeta("boardFx", true);
            background.Resized += Fit;
        }
        Fit();
    }

    private void SetDeckBack(TextureRect deck, Texture2D back)
    {
        if (deck == null) return;
        if (back != null) deck.Texture = back;
        else if (Cards.CardBack != null) deck.Texture = Cards.CardBack;
        deck.SelfModulate = back != null ? Colors.White : DeckBackTint;
    }

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

    // ------------------------------------------------------------------
    // The middle panel
    // ------------------------------------------------------------------

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
            _targetLabel.Text = $"{heading}  -  Target {State.TargetScore}{run.FinaleRulesLine("\n")}";
            _targetLabel.AddThemeColorOverride("font_color", BoardInk);
            return;
        }

        // A moved target used to be announced here for the whole stage ("Target 23 (up from 20)").
        // Since 2026-10-08 the target is written big in the board's emblem, which this line would
        // cover, so the move is said once instead - by the stage's slide-in ("Target: 23 (up from
        // 20)", GameManager) - and the emblem carries the number from then on.
        _targetLabel.Text = string.Empty;
        _targetLabel.RemoveThemeColorOverride("font_color");
    }

    /// A tappable modifier card: an invisible Button (so the theme's touch-friendly hit area
    /// and focus handling still apply) with the card art drawn on top. A picked-up card is lifted
    /// and the rest of the hand dims, so which card is in play is obvious without reading anything.
    private Button CreateModifierButton(Card card, bool disabled, bool selected, bool anySelected, Action onPressed,
                                        Vector2 size, float lift, bool pulse = false)
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

        TextureRect view = Cards.CreateCardView(card, size);
        view.MouseFilter = Control.MouseFilterEnum.Ignore;

        if (disabled) view.Modulate = new Color(0.55f, 0.55f, 0.55f);
        else if (anySelected && !selected) view.Modulate = new Color(0.5f, 0.5f, 0.55f); // the rest of the hand steps back
        else view.Modulate = Colors.White;

        button.AddChild(view);
        view.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Pass 49: the tutorial's card pulses until it is picked up - and since 2026-10-08, so
        // does any card that would land you exactly on the target. The art is scaled, not the
        // Button, for the same reason as the pick-up below.
        // A tween needs the tree, and this button is not in it yet: start once it arrives.
        if (!selected && !disabled && pulse)
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

            // Pass 57: held in the hand it floats - a soft shadow under it, and a slow sway, so
            // it reads as a card between your fingers rather than a selected tile.
            StyleBoxFlat shadowStyle = new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0.0f),
                ShadowColor = new Color(0, 0, 0, 0.45f),
                ShadowSize = 18,
                ShadowOffset = new Vector2(6, 14),
            };
            shadowStyle.SetCornerRadiusAll(12);
            Panel shadow = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, ShowBehindParent = true };
            shadow.AddThemeStyleboxOverride("panel", shadowStyle);
            view.AddChild(shadow);
            shadow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

            if (GameSettings.CardAnimations)
            {
                view.TreeEntered += () =>
                {
                    Tween sway = view.CreateTween().SetLoops();
                    sway.TweenProperty(view, "rotation_degrees", 1.6f, 1.1f)
                        .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                    sway.TweenProperty(view, "rotation_degrees", -1.6f, 1.1f)
                        .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                };
            }
        }

        return button;
    }

    // ------------------------------------------------------------------
    // Cards on the boards: putting a view into a slot, redrawing it, burning it. What the view
    // looks like is CardViews'; the flight there is CardMotion's.
    // ------------------------------------------------------------------

    private static Color PlaymatTintFor(Color table)
    {
        // The plain table (2-player, and the first rank) is the mat exactly as it was painted.
        if (table.IsEqualApprox(DefaultTableColor)) return Colors.White;
        return new Color(
            Mathf.Min(table.R / PlaymatAverage.R, 3f),
            Mathf.Min(table.G / PlaymatAverage.G, 3f),
            Mathf.Min(table.B / PlaymatAverage.B, 3f));
    }


    /// Redraws the face of a card that is already on a board, after something changed its Value.
    /// Silent when the card is not on this board - a Modifier has no view to redraw, and that is
    /// not an error.
    public void RefreshCardFace(Card card, Control board)
    {
        TextureRect view = FindCardView(card, board);
        if (view == null) return;
        Cards.Redraw(view, card, BoardCardSizeFor(board), board == L?.P2Board);
    }

    // ------------------------------------------------------------------
    // The effect moments (2026-10-05). Each is called right after the model has resolved the
    // effect and before the refresh that follows, so a score held at its old number is never
    // painted at the new one first. `cardLands` is how long the effect card's own flight onto
    // the board takes (CardMotion.AnimateModifierPlay: one deferred frame + ~0.44s).
    // ------------------------------------------------------------------
    private const float EffectCardLands = 0.45f;

    /// Shave: the slash across the target's score, which shows `before` until it lands.
    public void PlayShave(Player target, int before)
    {
        Scores.HoldShown(target, before);
        Control padlock = target.IsHolding ? Scores.PadlockOf(target) : null;
        Moments.PlayShave(Scores.ScoreBoxOf(target), padlock, () => Scores.ReleaseShown(target), EffectCardLands);
    }

    /// Trade Totals: both scores show their old numbers until the numbers have flown across.
    public void PlayTradeTotals(Player owner, int ownerBefore, Player target, int targetBefore)
    {
        Scores.HoldShown(owner, ownerBefore);
        Scores.HoldShown(target, targetBefore);
        Moments.PlayTradeTotals(Scores.ScoreBoxOf(owner), ownerBefore, Scores.ScoreBoxOf(target), targetBefore,
            () =>
            {
                Scores.ReleaseShown(owner);
                Scores.ReleaseShown(target);
            },
            EffectCardLands);
    }

    /// Copy: the owner's drawn card flips into the target's, and the owner's score (held at
    /// `ownerBefore`) ticks over while the card is edge-on.
    public void PlayCopy(Player owner, int ownerBefore, Player target, Control ownerBoard, Control targetBoard)
    {
        Card mine = owner.LastDrawnCard;
        Scores.HoldShown(owner, ownerBefore);
        Moments.PlayCopy(
            FindCardView(mine, ownerBoard),
            FindCardView(CardEffects.CopySource(target), targetBoard),
            () =>
            {
                RefreshCardFace(mine, ownerBoard);
                Scores.ReleaseShown(owner);
            },
            EffectCardLands);
    }

    /// Trade Hands, step one: copies of both hands as they are drawn now, before the refresh
    /// rebuilds each with the other's cards. Rescue cards stay with their owner, so stay out.
    public (List<TableMoments.HandCard> P1, List<TableMoments.HandCard> P2) SnapshotHands()
    {
        HashSet<int> rescue = new HashSet<int>();
        foreach (Card c in P1.Modifiers) if (c.IsRescue) rescue.Add(c.Id);
        foreach (Card c in P2.Modifiers) if (c.IsRescue) rescue.Add(c.Id);
        return (TableMoments.Snapshot(L?.P1Hand, rescue), TableMoments.Snapshot(L?.P2Hand, rescue));
    }

    /// Trade Hands, step two: fold, pass, unfold. Starts as the Trade Hands card slams down.
    public void PlayTradeHands((List<TableMoments.HandCard> P1, List<TableMoments.HandCard> P2) before)
    {
        Moments.PlayTradeHands(before.P1, before.P2, L?.P1Hand, L?.P2Hand, EffectCardLands - 0.15f);
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
        // A Modifier comes from its owner's HAND, not the deck (release playtest, 2026-09-29:
        // "playing a Modifier uses the same animation as drawing a card"). Find where it sits in
        // the hand now, before the refresh that follows rebuilds the hand without it.
        CardMotion.HandSpot? fromHand = card.Type != CardType.Main ? _motion.HandSpotOf(card) : null;

        TextureRect cardNode = Cards.CreateCardView(card, BoardCardSizeFor(parentContainer), parentContainer == L?.P2Board);

        // Drop the card into the next empty slot; if the board is somehow full, let the grid grow.
        // In portrait the sixth card opens the third row, and RefreshBoardSlots below re-sizes
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
        if (fromHand.HasValue)
        {
            CardMotion.HandSpot spot = fromHand.Value;
            Defer(() => _motion.AnimateModifierPlay(card, cardNode, delay, flightSize, spot, parentContainer));
            return;
        }
        Control fromDeck = DeckFor(parentContainer);
        Defer(() => _motion.AnimateCardDrop(cardNode, delay, flightSize, fromDeck));
    }

    /// Each player draws from their own deck (pass 43), so a card flies from the deck on its
    /// owner's side. A layout without a Player 2 deck falls back to Player 1's.
    private Control DeckFor(Control board) =>
        (board == L?.P2Board && L?.P2Deck != null) ? L.P2Deck : _mainDeckPosition;

}
