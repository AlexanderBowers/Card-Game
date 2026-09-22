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

    void ModifierPressed(Player player, Card card);
    void PlayPressed(Player player);
    void PutBackPressed(Player player);
    void FlipValuePressed(Player player);

    /// The Menu button on the table. Menus are their own concern; the table only makes room.
    void BuildTableMenu();
}

/// Everything the live table LOOKS like: the responsive layout and the fit that sizes it, the two
/// 3x3 boards, the card art and faces, the flight a drawn card makes, the score lines, the chips,
/// the typography and the one refresh that pushes the game's state onto all of it.
///
/// The line against Table.cs, in the other direction: Table knows where the cards are, TableUi
/// knows what they look like. Nothing in here decides anything - no score changes, no card is
/// spent, no turn ends. Presses go out through ITableUiHost and come back as new state to draw.
public sealed class TableUi
{
    /// The scene nodes the table paints into. GameManager keeps the [Export]s - the scene wires
    /// those - and hands them over once, here, so the 1,700 lines below can say _p1BoardContainer
    /// rather than _host.P1BoardContainer fifty times over.
    public sealed class Nodes
    {
        public BoxContainer MainLayout;
        public Control P1BoardContainer;
        public Control P2BoardContainer;
        public Control P1ModifierContainer;
        public Control P2ModifierContainer;
        public Label P1ScoreLabel;
        public Label P2ScoreLabel;
        public Label P1StatusLabel;
        public Label P2StatusLabel;
        public Label P1WinsLabel;
        public Label P2WinsLabel;
        public Control P2Rotator;
        public Button P1DrawCardButton;
        public Button P1HoldButton;
        public Button P2DrawCardButton;
        public Button P2HoldButton;
        public Button DrawCardButton;
        public Button HoldButton;
        public Label SetInfoLabel;
        public Control MainDeckPosition;
        public CheckButton MirrorToggle;
        public OptionButton GameModeButton;
    }

    private readonly ITableUiHost _host;

    /// The scene node the picture borrows for the things only a Node can do: adding a child,
    /// reaching the tree and the viewport, waiting on a timer, deferring a call to the end of the
    /// frame. It is never asked a question about the game - that is what _host is for.
    private readonly Node _root;

    private readonly Control _p1BoardContainer;
    private readonly Control _p2BoardContainer;
    private readonly Control _p1ModifierContainer;
    private readonly Control _p2ModifierContainer;
    private readonly Label _p1ScoreLabel;
    private readonly Label _p2ScoreLabel;
    private readonly Label _p1StatusLabel;
    private readonly Label _p2StatusLabel;
    private readonly Label _p1WinsLabel;
    private readonly Label _p2WinsLabel;
    private readonly Control _p2Rotator;
    private readonly Button _p1DrawCardButton;
    private readonly Button _p1HoldButton;
    private readonly Button _p2DrawCardButton;
    private readonly Button _p2HoldButton;
    private readonly Button _drawCardButton;
    private readonly Button _holdButton;
    private readonly Label _setInfoLabel;
    private readonly Control _mainDeckPosition;
    private readonly CheckButton _mirrorToggle;
    private readonly OptionButton _gameModeButton;

    private readonly PackedScene _cardViewScene = GD.Load<PackedScene>("res://CardView.tscn");

    public TableUi(ITableUiHost host, Node root, Nodes nodes)
    {
        _host = host;
        _root = root;
        _p1BoardContainer = nodes.P1BoardContainer;
        _p2BoardContainer = nodes.P2BoardContainer;
        _p1ModifierContainer = nodes.P1ModifierContainer;
        _p2ModifierContainer = nodes.P2ModifierContainer;
        _p1ScoreLabel = nodes.P1ScoreLabel;
        _p2ScoreLabel = nodes.P2ScoreLabel;
        _p1StatusLabel = nodes.P1StatusLabel;
        _p2StatusLabel = nodes.P2StatusLabel;
        _p1WinsLabel = nodes.P1WinsLabel;
        _p2WinsLabel = nodes.P2WinsLabel;
        _p2Rotator = nodes.P2Rotator;
        _p1DrawCardButton = nodes.P1DrawCardButton;
        _p1HoldButton = nodes.P1HoldButton;
        _p2DrawCardButton = nodes.P2DrawCardButton;
        _p2HoldButton = nodes.P2HoldButton;
        _drawCardButton = nodes.DrawCardButton;
        _holdButton = nodes.HoldButton;
        _setInfoLabel = nodes.SetInfoLabel;
        _mainDeckPosition = nodes.MainDeckPosition;
        _mirrorToggle = nodes.MirrorToggle;
        _gameModeButton = nodes.GameModeButton;

        _mainLayout = nodes.MainLayout;

        _cardSheet = GD.Load<Texture2D>("res://assets/kenney/cards.png");
        _chipSheet = GD.Load<Texture2D>("res://assets/kenney/chips.png");
        _sfxSlide = CreateSfx("res://assets/kenney/sfx/cardSlide1.ogg");
        _sfxPlace = CreateSfx("res://assets/kenney/sfx/cardPlace1.ogg");

        // Each side's Layout, cached NOW. ApplySideLayout moves the board into a row of its own in
        // portrait, so "the board's parent" stops being the Layout after the first pass - reading
        // it later would find the row and then reparent the row into itself.
        _p1SideLayout = _p1BoardContainer?.GetParent() as Control;
        _p2SideLayout = _p2BoardContainer?.GetParent() as Control;

        // P2Rotator spins around its own centre, so keep the pivot there whatever size it ends up.
        if (_p2Rotator != null) _p2Rotator.Resized += () => _p2Rotator.PivotOffset = _p2Rotator.Size / 2f;
    }

    /// The two rows of poker chips, one per side. Their labels are the scene's; the chips are ours.
    public void BuildWinChips()
    {
        _p1WinChips = EnsureWinChips(_p1WinsLabel);
        _p2WinChips = EnsureWinChips(_p2WinsLabel);
    }

    /// Both status lines reserve their two lines up front, so a status appearing never moves the
    /// board underneath it.
    public void ConfigureStatusLabels()
    {
        ConfigureStatusLabel(_p1StatusLabel);
        ConfigureStatusLabel(_p2StatusLabel);
    }

    /// The exported score Labels become the three-node ScoreLines each side actually shows.
    /// After StyleTableForReadability, which is what moves the label into its final parent.
    public void BuildScoreLines()
    {
        _p1Score = BuildScoreLines(_p1ScoreLabel);
        _p2Score = BuildScoreLines(_p2ScoreLabel);
    }

    /// Three places outside the picture need to POINT at something in it: the tutorial's
    /// spotlight and a coach mark's arrow. They get the node, never the right to change it.
    public Control P1ScoreBlock => _p1Score?.Root;
    public Control DeckFootprint => _deckHolder; // the turned deck's footprint in portrait
    public Control P1ActionRow => _p1ActionRow;
    public Control EffectBanner => _effectBanner;

    /// The two sounds a press makes. They live here because the card animations play them too,
    /// and one owner of the two AudioStreamPlayers is one fewer thing to keep in step.
    public void SoundPlace() => _sfxPlace?.Play();
    public void SoundSlide() => _sfxSlide?.Play();

    /// A call put off to the end of the frame.
    ///
    /// It used to be Node.CallDeferred on GameManager itself, which Godot simply drops if that
    /// object has been freed in the meantime - and a Restart frees it. A plain Callable has no
    /// such courtesy, so the check is made here instead; without it a deferred repaint could land
    /// on a table that no longer exists.
    private void Defer(Action action) =>
        Callable.From(() => { if (GodotObject.IsInstanceValid(_root)) action(); }).CallDeferred();

    /// A refresh at the end of the frame rather than now, and dropped if the table has gone away
    /// in between. Callers used to get this free from Node.CallDeferred(MethodName.UpdateUI).
    public void DeferRefresh() => Defer(Refresh);

    private Player P1 => _host.Player1;
    private Player P2 => _host.Player2;
    private GameState State => _host.State;

    /// A rescue card is not one you own, so it is washed mint wherever it appears. The rescue
    /// OFFER is monetisation and lives in GameManager; this is only its colour.
    public static readonly Color RescueTint = new Color(0.7f, 1.3f, 1.05f);

    // ------------------------------------------------------------------
    // UI scaling
    //
    // project.godot uses a 720x720 base viewport with stretch aspect "expand", so the SHORT
    // side of whatever screen we're on is always 720 design-pixels and the long side grows.
    // Everything below is sized in those design pixels. ApplyResponsiveLayout enlarges that
    // base (shrinking the whole UI uniformly) when a screen is too short for the full layout.
    // ------------------------------------------------------------------
    private const int BoardSlots = 9;                       // 3x3 board

    private const int WinsToTakeMatch = GameState.SetsToWinMatch;  // one chip slot per win needed

    public static readonly Vector2 BaseCardSize = new Vector2(84, 114); // Kenney cards are 140x190

    private const float ModifierCardScale = 0.8f;

    /// Portrait has the width for a bigger hand. Pass 23 shrank this to 0.86 to buy height for
    /// the 3x3 board; Alexander, 2026-09-17: "modifier placement was shifted a little, it was
    /// better where it was before" - the hand is right-aligned, so a smaller card moves its left
    /// edge inward and the whole row appears to shift. Back to 0.95, and the height comes from
    /// the middle panel instead.
    private const float ModifierCardScalePortrait = 0.95f;

    /// The face-down deck in the middle panel: a prop, not something anyone reads, so portrait
    /// draws it small and spends the height on the boards.
    private const float DeckCardScalePortrait = 0.75f;

    private bool _portraitLayout;

    private const float MinCardScale = 0.6f;

    private float _cardScale = 1f;

    private BoxContainer _mainLayout;

    // The middle panel's two added lines: the target, big, above the set line; and a banner
    // under it that says what an effect card just did. Both are built in code (BuildTableBanners)
    // so the two .tscn scenes stay as they are.
    private Label _targetLabel;

    private Label _effectBanner;

    private uint _effectBannerToken;   // so a stale timer never wipes a newer message

    public Vector2 CardSize => BaseCardSize * _cardScale;

    public Vector2 ModifierCardSize =>
        BaseCardSize * _cardScale * (_portraitLayout ? ModifierCardScalePortrait : ModifierCardScale);

    // ------------------------------------------------------------------
    // Art (Kenney Boardgame Pack, CC0 - see assets/kenney/LICENSE.txt)
    // Regions are taken from the pack's playingCardBacks.xml / chips.xml atlases.
    // ------------------------------------------------------------------
    private Texture2D _cardSheet;

    private Texture2D _chipSheet;

    private static readonly Rect2 RegionMain = new Rect2(280, 760, 140, 190);   // cardBack_green3 - main deck cards

    private static readonly Rect2 RegionPlus = new Rect2(280, 380, 140, 190);   // cardBack_blue3  - positive modifiers

    private static readonly Rect2 RegionMinus = new Rect2(0, 380, 140, 190);    // cardBack_red3   - negative modifiers

    private static readonly Rect2 RegionDeckBack = new Rect2(140, 190, 140, 190); // cardBack_green4 - face-down deck

    // The collection log's reward: a different back pattern, gilded. Cosmetic only.
    private static readonly Rect2 RegionCollectorBack = new Rect2(140, 570, 140, 190); // cardBack_green5

    private static readonly Color CollectorBackTint = new Color(1.45f, 1.2f, 0.45f);

    // The Kenney sheet has three hues and red/green/blue are already minus/main/plus, so there is
    // no fourth back to give an effect card: it takes a plain green one and wears EffectTint, or
    // a Shave landing in the opponent's grid would read as a card they just drew. A later pass
    // draws the real effect face in code, the way BuildFlipValueFace draws the blue-over-red one.
    private static readonly Rect2 RegionEffect = new Rect2(140, 0, 140, 190);     // cardBack_green1

    private static readonly Color EffectTint = new Color(1.15f, 0.85f, 1.35f);    // violet wash

    private static readonly Rect2 RegionChipWon = new Rect2(0, 194, 68, 68);    // chipGreen_border

    private static readonly Rect2 RegionChipEmpty = new Rect2(68, 0, 68, 68);   // chipWhite_border

    private const float ChipSize = 33f; // pass 23: was 28

    private HBoxContainer _p1WinChips;

    private HBoxContainer _p2WinChips;

    /// The felt colour of the 2-player table and of stage 1 - project.godot's clear colour.
    private static readonly Color DefaultTableColor = new Color(0.07f, 0.24f, 0.13f);

    /// Tint applied to the standard (main deck) card art for the rank in play. See ApplyRankTheme.
    private Color _rankCardTint = Colors.White;

    /// The face-down deck: the rank's own back, or the collection reward when it is switched on.
    private bool GildedDeck => RunData.Instance != null && RunData.Instance.UseCollectorBack;

    private Rect2 DeckBackRegion => GildedDeck ? RegionCollectorBack : RegionDeckBack;

    private Color DeckBackTint => GildedDeck ? CollectorBackTint : _rankCardTint;

    private AudioStreamPlayer _sfxSlide;

    private AudioStreamPlayer _sfxPlace;

    // ------------------------------------------------------------------
    // Audio
    // ------------------------------------------------------------------
    private AudioStreamPlayer CreateSfx(string path)
    {
        AudioStream stream = GD.Load<AudioStream>(path);
        if (stream == null) return null;
        AudioStreamPlayer player = new AudioStreamPlayer { Stream = stream, VolumeDb = -4f, Bus = GameSettings.SfxBus };
        _root.AddChild(player);
        return player;
    }

    // ------------------------------------------------------------------
    // Responsive layout
    // ------------------------------------------------------------------
    // Approximate design-pixel footprint of the whole UI (both sides + control panel) in each
    // orientation. If the screen can't show that much at the 720px base, the base is enlarged so
    // the entire UI scales down uniformly instead of cropping. (Phones in portrait are ~720x1560,
    // desktop landscape is 1280x720 - both fit as-is; a short portrait desktop window doesn't.)
    // (Each side is stats + board + hand + its own Draw Card / Hold row in the 2-player scene; the
    // middle panel also carries the How to Play button.)
    private const float BaseSide = 720f;

    // Portrait dropped by roughly the height of a stats block and an action row PER SIDE when
    // ApplySideLayout moved both beside the board, and grew sideways by the width of that column.
    // Left deliberately on the SMALL side: EnsureLayoutFits was built to correct an underestimate
    // (its high-water mark exists for exactly that), whereas an overestimate is never corrected -
    // it just scales the whole UI down further than it needs to go and nothing ever says so.
    // Pass 23 put the third board row back (the stack is gone) and raised every font, so both
    // grew. Still deliberately on the small side - see the note above.
    private static readonly Vector2 NeedPortrait = new Vector2(560, 1320);

    private static readonly Vector2 NeedLandscape = new Vector2(1080, 700);

    /// The GameUI MarginContainer's margin, per side, as both .tscn files set it. The estimate
    /// above is of MainLayout's contents; this is the frame around them.
    private const float GameUiMargin = 20f;

    /// Extra gutter inside the margin when a portrait side is stretched to the screen's width.
    private const float PortraitSidePad = 8f;

    // ------------------------------------------------------------------
    // ...and the correction, because the estimate above is a GUESS.
    //
    // Those two constants are a hand-maintained tally of everything in the portrait/landscape
    // column, and they have now been wrong three times: they were bumped for the confirm row, for
    // the How to Play button, and they were STILL short - local 2-player in portrait cropped both
    // players' Draw Card / Hold rows off the top and bottom of the phone (Alexander, 2026-09-13).
    // That is the mechanism working correctly on a wrong number, and it will go wrong again the
    // next time anyone adds a row, silently, on a device nobody is testing on.
    //
    // So the estimate is now only the FIRST guess. After the layout settles, the container is
    // asked what it actually needs - GetCombinedMinimumSize is exact, where the tally is not - and
    // if it does not fit, the base grows and the whole UI scales down until it does.
    //
    // Monotonic on purpose: _fitScale only ever GROWS within a given window size, so it converges
    // in a pass or two and cannot oscillate. A real window resize or a rotation resets it, so the
    // UI grows back when there is room again - the one thing a grow-only correction would
    // otherwise get wrong.
    // ------------------------------------------------------------------
    private float _fitScale = 1f;

    private int _fitAttempts;

    private bool _fitCheckPending;

    /// Everything the layout is measured AGAINST - it replaces the old _fitWindow, which only
    /// watched the window. When any of it changes, every size remembered from before it belongs
    /// to a different table (see ApplyResponsiveLayout).
    private (Vector2 Window, bool Portrait, bool Mirrored, bool VsBot) _layoutBasis;

    private Vector2 _fitLastNeeded = Vector2.Zero;

    private const int MaxFitAttempts = 6;

    // GROWS the UI into spare room as well as shrinking it to fit (Alexander, 2026-09-16:
    // "text is too small for how much space is now available"). The Need* tally is sized for the
    // 2-player table, so the solo table - one side, not two - was drawn at 2-player scale on a
    // phone with half the screen empty.
    //
    // Landscape was left alone at first (playtesters said it felt right on the sizes it was
    // tried on), but Alexander, 2026-09-21, screenshots off the S25 Ultra in landscape (2340x1080):
    // "3x3 grid isn't big enough. Score isn't big enough" - NeedLandscape is far smaller than that
    // window, and with growth gated to portrait only, that slack just sat there as green table felt
    // rather than being spent on the board and the score beside it. Both orientations now grow
    // into spare room the same way; only the ceiling differs (MaxLandscapeZoom below).
    private bool _fitPortrait;

    private float _fitBaseK = 1f; // k before _fitScale is applied, so the grow can respect the cap

    /// How far the UI may be enlarged past the 720px base (1 / this is the smallest _fitScale).
    /// Was 1.8, then 2.2; the S25 Ultra screenshots Alexander sent (2026-09-21) still showed real
    /// gaps above/below the board+score block at 2.2 on that phone's tall aspect - another guess
    /// to check against a real device, same as NeedPortrait above.
    private const float MaxPortraitZoom = 2.6f;

    /// Landscape's own ceiling (see the note above _fitPortrait). A first guess, the same way
    /// NeedLandscape is - worth raising further if a real phone still shows spare room at this
    /// cap. Kept a little under the portrait one for now since landscape has two boards side by
    /// side, so the same zoom reads as more growth across the whole table.
    private const float MaxLandscapeZoom = 1.8f;

    /// Spare room below this fraction is not worth a re-layout.
    private const float GrowThreshold = 0.95f;

    /// Forget every size this layout has settled on.
    ///
    /// The fit is ITERATIVE and its state is a set of high-water marks - _fitLastNeeded here, and
    /// StableBox's own inside each fixed slot - so it only lands on the same answer twice if it
    /// starts from the same clean state twice. Every route to a given table has to reset the same
    /// way, or the same screen comes out at two different sizes depending on how it was reached.
    public void ResetFitState()
    {
        _fitScale = 1f;
        _fitAttempts = 0;
        _fitLastNeeded = Vector2.Zero;
        StableBox.ResetAll();
    }

    public void ApplyResponsiveLayout()
    {
        Window root = _root.GetTree().Root;
        Vector2 win = root.Size;
        bool portrait = win.Y > win.X;
        _fitPortrait = portrait;
        _portraitLayout = portrait;

        // A genuine resize, rotation, mirror toggle or mode change: start the correction over, so
        // the UI can find the right size for the table it is actually showing. Re-running
        // ourselves from EnsureLayoutFits changes ContentScaleSize, not the basis below, so this
        // still does not fire on our own passes.
        //
        // The WINDOW alone is not enough, and that was the bug (Alexander, 2026-09-18: rotate to
        // landscape, back to portrait, then turn Mirror for Player 2 on or off, and Player 1's
        // side has shifted). The mirror swaps a one-line score for a two-line one and re-shapes
        // both sides around it at the SAME window size, so nothing here fired: the fixed slots
        // kept the taller of the two forms for good, and the fit kept the scale it had settled on
        // for it. The same screen then came out at a different size depending on the route to it.
        var basis = (win, portrait, IsMirrored, _host.VsBot);
        if (basis != _layoutBasis)
        {
            _layoutBasis = basis;
            ResetFitState(); // sizes from the old table mean nothing in this one
        }

        // What the viewport would be at the plain 720px base, and how much bigger it must be.
        float baseScale = Mathf.Min(win.X / BaseSide, win.Y / BaseSide);
        Vector2 baseViewport = win / Mathf.Max(baseScale, 0.001f);
        Vector2 need = portrait ? NeedPortrait : NeedLandscape;
        float k = Mathf.Max(1f, Mathf.Max(need.X / baseViewport.X, need.Y / baseViewport.Y)) * _fitScale;
        _fitBaseK = k / Mathf.Max(_fitScale, 0.001f);
        float maxZoom = portrait ? MaxPortraitZoom : MaxLandscapeZoom;
        k = Mathf.Max(k, 1f / maxZoom);
        Vector2I contentSize = (Vector2I)(new Vector2(BaseSide, BaseSide) * k).Round();
        if (root.ContentScaleSize != contentSize) root.ContentScaleSize = contentSize; // re-fires SizeChanged once

        Vector2 vp = _root.GetViewport().GetVisibleRect().Size;

        // Stack the two player sides vertically in portrait, side by side in landscape.
        if (_mainLayout != null)
        {
            _mainLayout.Vertical = portrait;

            // Portrait: P2 on top, P1 at the bottom (near the thumbs). Landscape: P1 left, P2 right.
            Control p1Side = _mainLayout.GetNodeOrNull<Control>("Player1Side");
            Control p2Side = _mainLayout.GetNodeOrNull<Control>("Player2Side");
            Control panel = _mainLayout.GetNodeOrNull<Control>("SharedControlPanel");
            if (p1Side != null && p2Side != null && panel != null)
            {
                _mainLayout.MoveChild(portrait ? p2Side : p1Side, 0);
                _mainLayout.MoveChild(panel, 1);
                _mainLayout.MoveChild(portrait ? p1Side : p2Side, 2);
            }
        }

        ApplyOrientationTypography(portrait);

        // Before ANYTHING is measured. The score's font and its one-or-two lines are chosen from
        // the orientation and the mirror, and either may have just changed; left to the next
        // UpdateUI they would only land after this pass had already sized every slot around the
        // form that is going away.
        RefreshScoreLines();

        // Portrait spreads each side across the screen (pass 22: "everything is compacted to the
        // middle"): the side is as wide as the screen allows, the score column sits at the left
        // edge and the board at the right. EnsureLayoutFits measures around this width (see
        // NaturalContentWidth), or the filler would look like content and stop the UI growing.
        float fill = portrait ? Mathf.Max(0f, vp.X - 2f * (GameUiMargin + PortraitSidePad)) : 0f;
        foreach (Control side in new[] { _p1SideLayout, _p2SideLayout })
            if (side != null) side.CustomMinimumSize = new Vector2(fill, 0f);

        // ...and each side re-flows within itself: a column in landscape, score and buttons beside
        // the board in portrait.
        ApplySideLayout(_p1SideLayout, _p1ActionRow ?? _drawCardButton?.GetParent() as Control,
                        _p1ConfirmRow, _p1ButtonSlot, portrait);
        ApplySideLayout(_p2SideLayout, _p2ActionRow, _p2ConfirmRow, _p2ButtonSlot, portrait);

        // Player 2's side faces the other way when the "Mirror" toggle is on (face-to-face play).
        if (_p2Rotator != null) _p2Rotator.RotationDegrees = IsMirrored ? 180f : 0f;

        // The base-size adjustment above guarantees the viewport is at least `need`, so this only
        // trims the cards in the rare case the estimate is a little short.
        // Measured against the viewport BEFORE the fit correction: when the fit enlarges the UI the
        // viewport shrinks below `need`, and trimming the cards for that would undo the enlargement.
        Vector2 vpUnfit = vp / Mathf.Max(_fitScale, 0.001f);
        _cardScale = Mathf.Clamp(Mathf.Min(vpUnfit.Y / need.Y, vpUnfit.X / need.X), MinCardScale, 1f);

        ResizeBoard(_p1BoardContainer);
        ResizeBoard(_p2BoardContainer);
        ApplyDeckOrientation(portrait);
        _deckCountLabel?.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(CardSize.Y * 0.30f));
        RefreshModifiersUI();
        Defer(UpdateRotatorSize);
        Defer(_host.LayoutChanged); // a rotation moves whatever is anchored to a node
        EnsureLayoutFits();
    }

    /// Re-flows ONE player's side for the orientation (Alexander's sketch, S25 Ultra, 2026-09-15).
    ///
    /// BOTH orientations now stand the score in a column BESIDE the board instead of in a row
    /// above it. What differs is which side of the board that column stands on, and whether the
    /// buttons travel into it.
    ///
    /// Portrait takes the score AND the buttons over, on the board's left:
    ///
    ///     [ You: 13   Wins O O O ]  [ . . . ]
    ///     [ Them: 8              ]  [ . . . ]
    ///     [ Draw Card            ]  [ . . . ]
    ///     [ Hold                 ]
    ///     [        the hand, full width     ]
    ///
    /// The reason it is worth the reparenting: the board is SQUARE and the phone is tall and
    /// narrow, so a 3x3 grid leaves a column of dead space beside it while the same screen is
    /// fighting for vertical room. Moving two blocks into that column buys back roughly the height
    /// of an action row and a stats block per side - which is what pays for the bigger buttons
    /// without EnsureLayoutFits shrinking everything to fit them.
    ///
    /// Landscape takes the score only, and puts it on the board's INNER side - P1 owns the screen's
    /// left half so its column goes to the RIGHT of its grid, P2's to the LEFT of its (Alexander,
    /// 2026-09-19). The two scores then face each other across the middle panel instead of sitting
    /// in the screen's two outer corners, where reading both of them meant crossing the whole
    /// table. The buttons stay under the hand, where landscape has the width to spare:
    ///
    ///     [ . . . ]  Wins O O O      Wins O O O  [ . . . ]
    ///     [ . . . ]  You  21/20      Them  20    [ . . . ]
    ///     [ . . . ]  Over target!    Thinking... [ . . . ]
    ///     [     the hand     ]        [     the hand     ]
    ///     [ Draw Card ][ Hold ]
    ///
    /// The confirm row travels WITH the action row it stands in for. It was inserted as that row's
    /// sibling (BuildConfirmRow), so leaving it behind would put Play / +- / Put back on the other
    /// side of the board from the buttons they replace.
    private void ApplySideLayout(Control layout, Control actionRow, Control confirmRow,
                                 Control buttonSlot, bool portrait)
    {
        if (layout == null) return;

        // Found by name rather than held, because they move: after one layout pass they live
        // under SideRow. owned:false - SideRow and SideColumn are built here and have no owner.
        Control stats = layout.FindChild("Stats", true, false) as Control;
        Control board = layout.FindChild("BoardSlotsContainer", true, false) as Control;
        Control hand = layout.FindChild("ModifierContainer", true, false) as Control;
        if (stats == null || board == null || hand == null) return;

        bool isP1 = layout == _p1SideLayout;

        // Pieces that change shape rather than just place (Alexander's sketch, 2026-09-16).
        ApplyWinsRow(stats, isP1 ? _p1WinChips : _p2WinChips);
        // Stacked in portrait (Alexander, 2026-09-16, asked twice). This silently did nothing
        // before pass 21: the rows were HBoxContainers, and Godot refuses to change the
        // orientation of an HBox/VBox - only a plain BoxContainer can turn. Both rows are plain
        // BoxContainers now (the .tscn ActionButtons nodes and BuildConfirmRow).
        SetRowOrientation(actionRow, portrait);
        SetRowOrientation(confirmRow, portrait);
        hand.SizeFlagsHorizontal = portrait ? Control.SizeFlags.ShrinkEnd : Control.SizeFlags.ShrinkCenter;

        HBoxContainer sideRow = EnsureSideRow(layout);
        Control slot = sideRow.GetNodeOrNull<Control>("SideSlot");
        Control column = slot?.GetNodeOrNull<Control>("SideColumn");
        Control columnSpacer = column?.GetNodeOrNull<Control>("ColumnSpacer");
        Control rowSpacer = sideRow.GetNodeOrNull<Control>("RowSpacer");
        if (slot == null || column == null) return;

        // Which side of the board the column stands on. Portrait keeps it on the left, where the
        // sketch put it. Landscape wants the inner edge, which is the right of P1's board and the
        // left of P2's - EXCEPT that P2's whole side is rotated 180 degrees for face-to-face play,
        // and a rotation swaps which end of the row draws on the left. So the child order flips
        // back with it, and P2 lands on the middle panel either way.
        bool columnFirst = portrait || (!isP1 && !IsMirrored);

        PlaceChild(stats, column, 0);
        PlaceChild(columnSpacer, column, 1);

        // Ordered board-first on purpose: PlaceChild clamps to the child count as it goes, so
        // moving the far end into place before the near one is what survives a flip between the
        // two arrangements without leaving the spacer stranded on the wrong side.
        PlaceChild(board, sideRow, columnFirst ? 2 : 0);
        PlaceChild(rowSpacer, sideRow, 1);
        PlaceChild(slot, sideRow, columnFirst ? 0 : 2);

        layout.MoveChild(sideRow, 0);
        PlaceChild(hand, layout, 1);

        // Portrait carries the buttons up into the column as well; landscape leaves them below the
        // hand, which is where the thumbs already expect them and where the width is free.
        Control buttons = buttonSlot ?? actionRow;
        if (portrait)
        {
            PlaceChild(buttons, column, 2);
            if (buttonSlot == null) PlaceChild(confirmRow, column, 3);
        }
        else
        {
            PlaceChild(buttons, layout, 2);
            if (buttonSlot == null) PlaceChild(confirmRow, layout, 3);
        }
    }

    /// The row holding one player's board and the column that stands beside it.
    ///
    /// Built on demand and then kept for good: both orientations use it now, where portrait alone
    /// used to, so nothing tears it down on a rotation any more. (The old teardown had to
    /// RemoveChild before QueueFree - a queued node stays in the tree until the end of the frame
    /// and still counts toward the layout's minimum size, which is the bug that squeezed P1 off
    /// screen when the hand was rebuilt, ui-scaling-and-art.md. One less way to hit it.)
    private static HBoxContainer EnsureSideRow(Control layout)
    {
        HBoxContainer sideRow = layout.GetNodeOrNull<HBoxContainer>("SideRow");
        if (sideRow != null) return sideRow;

        sideRow = new HBoxContainer { Name = "SideRow", Alignment = BoxContainer.AlignmentMode.Center };
        sideRow.AddThemeConstantOverride("separation", 14);
        layout.AddChild(sideRow);

        // The column sits in a fixed-size slot: a score growing from 7/20 to 24/20 or a status
        // line changing must not slide the board sideways (pass 21).
        StableBox slot = new StableBox { Name = "SideSlot", SizeFlagsVertical = Control.SizeFlags.Fill };
        sideRow.AddChild(slot);

        // Takes whatever width the side has spare, so the column and the board sit at the two
        // edges instead of huddling in the middle (pass 22).
        sideRow.AddChild(new Control
        {
            Name = "RowSpacer",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });

        VBoxContainer column = new VBoxContainer
        {
            Name = "SideColumn",
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = Control.SizeFlags.Fill,
        };
        column.AddThemeConstantOverride("separation", 12);
        slot.AddChild(column);

        // Holds Wins + score at the TOP of the column, level with the board's first row, and
        // pushes anything below it (portrait's buttons) down level with the board's last.
        column.AddChild(new Control
        {
            Name = "ColumnSpacer",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });

        return sideRow;
    }

    /// Turns a button row, and lines its buttons up from the top-left in portrait so Draw Card
    /// and Play sit in the same place, and Hold and Put back sit in the same place.
    private static void SetRowOrientation(Control row, bool portrait)
    {
        if (row is not BoxContainer box) return;
        if (box is HBoxContainer || box is VBoxContainer)
        {
            GD.PushWarning($"{row.Name} is an {box.GetClass()} and cannot be turned; make it a BoxContainer.");
            return;
        }
        box.Vertical = portrait;
        box.Alignment = portrait ? BoxContainer.AlignmentMode.Begin : BoxContainer.AlignmentMode.Center;
        // The button slot measures its rows on request; tell it the answer just changed.
        (row.GetParent() as Control)?.UpdateMinimumSize();
    }

    /// "Wins" and its chips take a line of their own ABOVE the score, in both orientations now
    /// that the block stands in a narrow column beside the board rather than a wide row above it
    /// (Alexander's sketch, 2026-09-16; landscape joined it on 2026-09-19):
    ///
    ///     Wins: O O O     [ . . . ]
    ///     You  14/20      [ . . . ]
    ///     Them 18         [ . . . ]
    ///
    /// Everything in the column reads from the same left edge, so the eye drops straight down the
    /// three lines instead of tracking a centred block that re-centres itself every time the score
    /// gains a digit.
    private void ApplyWinsRow(Control stats, HBoxContainer chips)
    {
        Label wins = stats.FindChild("WinsLabel", true, false) as Label;
        Control scoreRow = stats.GetNodeOrNull<Control>("Row1");
        if (wins == null || scoreRow == null) return;

        stats.AddThemeConstantOverride("separation", 14);
        if (stats is BoxContainer statsBox) statsBox.Alignment = BoxContainer.AlignmentMode.Begin;
        if (scoreRow is BoxContainer scoreBox) scoreBox.Alignment = BoxContainer.AlignmentMode.Begin;

        HBoxContainer winsRow = stats.GetNodeOrNull<HBoxContainer>("WinsRow");
        if (winsRow == null)
        {
            winsRow = new HBoxContainer { Name = "WinsRow" };
            winsRow.AddThemeConstantOverride("separation", 8);
            stats.AddChild(winsRow);
        }
        PlaceChild(winsRow, stats, 0);
        PlaceChild(wins, winsRow, 0);
        PlaceChild(chips, winsRow, 1);
    }

    /// Move a node to an exact slot under a parent, reparenting only when it is somewhere else.
    /// Called every layout pass, so the guard is what stops it thrashing the tree.
    ///
    /// Named PlaceChild rather than Reparent on purpose: Node already has a Reparent, and a
    /// same-named static here would quietly overload the engine's own method.
    private static void PlaceChild(Control node, Control parent, int index)
    {
        if (node == null || parent == null) return;

        if (node.GetParent() != parent)
        {
            node.GetParent()?.RemoveChild(node);
            parent.AddChild(node);
        }
        parent.MoveChild(node, Mathf.Clamp(index, 0, parent.GetChildCount() - 1));
    }

    /// Asks the layout what it ACTUALLY needs, and shrinks the whole UI until it fits. This is the
    /// backstop behind the Need* estimate; the reasoning is with those constants.
    ///
    /// A BoxContainer cannot go below the sum of its children's minimums - handed less room than
    /// that it overflows its own rect and the ends are simply cut off by the screen, which is
    /// exactly what portrait 2-player was doing. Nothing warns about it, so this looks instead.
    private async void EnsureLayoutFits()
    {
        // Setting ContentScaleSize re-fires SizeChanged, so ApplyResponsiveLayout re-enters and
        // calls this again while the first call is still waiting on its frames. Without the latch
        // each of them would apply the SAME overflow and the UI would end up several times
        // smaller than it needs to be.
        if (_fitCheckPending || _mainLayout == null) return;
        _fitCheckPending = true;
        bool relayout = false;

        try
        {
            // Two frames, not one. UpdateRotatorSize is deferred to the end of THIS frame and
            // writes P2Holder's minimum size; a container's combined minimum is only correct on
            // the pass after the one that changed it. Measuring earlier measures the old layout.
            for (int i = 0; i < 2; i++)
            {
                await _root.ToSignal(_root.GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!_root.IsInsideTree() || _mainLayout == null) return; // scene restarted mid-wait
            }

            Vector2 vp = _root.GetViewport().GetVisibleRect().Size;
            if (vp.X <= 1f || vp.Y <= 1f) return;

            Vector2 needed = _mainLayout.GetCombinedMinimumSize() + new Vector2(GameUiMargin, GameUiMargin) * 2f;
            if (_fitPortrait) needed.X = NaturalContentWidth() + 2f * (GameUiMargin + PortraitSidePad);

            // A high-water mark, and the thing that tells a LOOP apart from real GROWTH. A loop
            // that will not settle re-measures the same content over and over; content that has
            // genuinely grown measures bigger than anything seen before, and deserves a fresh
            // budget of attempts rather than being refused because an earlier fit spent them.
            //
            // This is not hypothetical - it is the bug. The first check runs from _Ready, and with
            // the start menu up the hands are not dealt until the player taps a mode seconds
            // later. The table measured on an EMPTY layout, two hand rows short, and nothing
            // re-measured once the cards landed (Alexander, 2026-09-13: toggling the mirror, which
            // re-runs the layout, was what fixed it by hand).
            if (needed.X > _fitLastNeeded.X + 1f || needed.Y > _fitLastNeeded.Y + 1f)
            {
                _fitAttempts = 0;
                _fitLastNeeded = new Vector2(Mathf.Max(_fitLastNeeded.X, needed.X),
                                             Mathf.Max(_fitLastNeeded.Y, needed.Y));
            }

            float overflow = Mathf.Max(needed.X / vp.X, needed.Y / vp.Y);
            if (_fitAttempts >= MaxFitAttempts) return; // a loop that will not settle

            if (overflow <= 1.002f)
            {
                // It fits. Also use the room that is left over - unless the UI is already as
                // large as it is allowed to get. The target is 98% so the next pass lands inside
                // the dead band (0.95..1.002) and stops rather than bouncing off the shrink branch
                // below.
                // Measured against the HIGH-WATER mark, not this frame: the hand gets narrower
                // every time a card is played, and growing into that would zoom the table mid-match.
                float room = Mathf.Max(_fitLastNeeded.X / vp.X, _fitLastNeeded.Y / vp.Y);
                float maxZoom = _fitPortrait ? MaxPortraitZoom : MaxLandscapeZoom;
                float minFit = (1f / maxZoom) / Mathf.Max(_fitBaseK, 0.001f);
                if (room >= GrowThreshold || _fitScale <= minFit + 0.001f) return;

                _fitAttempts++;
                _fitScale = Mathf.Max(minFit, _fitScale * room / 0.98f);
                GD.Print($"Layout has room ({needed.X:0}x{needed.Y:0} in {vp.X:0}x{vp.Y:0}) - "
                       + $"enlarging the UI, scale {_fitScale:0.000}");
                relayout = true; // no return: a return here would skip the re-layout after the finally
            }
            else
            {
                // The 1% of slack is what makes this converge in ONE step rather than creeping up
                // on the answer a fraction at a time and spending all the attempts getting there.
                _fitAttempts++;
                _fitScale *= overflow * 1.01f;
                relayout = true;
                GD.Print($"Layout did not fit ({needed.X:0}x{needed.Y:0} into {vp.X:0}x{vp.Y:0}) - "
                       + $"scaling the UI down by {_fitScale:0.000}");
            }
        }
        finally
        {
            _fitCheckPending = false;
        }

        // Only when the measurement above changed the scale (too big, or room to grow). Outside the
        // finally so the latch is already clear and the new pass may measure again.
        if (relayout) ApplyResponsiveLayout();
    }

    /// Portrait: how wide the content really is, ignoring the width each side is stretched to.
    /// The MainLayout is a column there, so this is its widest row.
    private float NaturalContentWidth()
    {
        float width = 0f;
        Control panel = _mainLayout?.GetNodeOrNull<Control>("SharedControlPanel");
        if (panel != null) width = panel.GetCombinedMinimumSize().X;

        foreach (Control side in new[] { _p1SideLayout, _p2SideLayout })
        {
            if (side == null) continue;
            foreach (Node node in side.GetChildren())
            {
                if (node is not Control child || !child.Visible) continue;
                width = Mathf.Max(width, child.GetCombinedMinimumSize().X);
            }
        }
        return width;
    }

    public bool IsMirrored => _mirrorToggle != null && _mirrorToggle.ButtonPressed;

    /// Player2Side (CenterContainer) > P2Holder (plain Control) > P2Rotator (full-rect, rotated) > Layout.
    /// Containers reset their children's rotation every time they re-lay them out, which is why
    /// the rotated node must NOT sit directly in a container - the holder takes the hit instead.
    /// The holder reports 0x0 on its own (its content is anchored, not laid out), so copy the
    /// Layout's minimum size onto it whenever the content changes.
    private void UpdateRotatorSize()
    {
        if (_p2Rotator == null || _p2Rotator.GetChildCount() == 0) return;
        if (_p2Rotator.GetChild(0) is Control layout && _p2Rotator.GetParent() is Control holder)
        {
            holder.CustomMinimumSize = layout.GetCombinedMinimumSize();
            _p2Rotator.RotationDegrees = IsMirrored ? 180f : 0f;
        }
    }

    // ------------------------------------------------------------------
    // The board (3x3, both orientations)
    //
    // Pass 21 tried a one-column pile in portrait, pass 22 a sideways five-wide fan. Alexander's
    // verdict, 2026-09-17: scrap the stack, the 3x3 grid is the board. So there is one board
    // layout again - nine slots, three columns, in portrait and in landscape alike - and the
    // debug switch that let the two be compared is gone with it.
    //
    // What the stack was really buying was SIZE: two rows instead of three left height over, and
    // the height paid for bigger cards. The grid has to find that room somewhere else, so it is
    // found in the things around the board instead - a smaller hand and a tighter middle panel in
    // portrait (see ModifierCardScalePortrait and CompactPortraitPanel) - and spent here, on the
    // board, through PortraitBoardCardScale.
    //
    // The ceiling is arithmetic and worth writing down, because the next "make them bigger" runs
    // into it: portrait shows TWO boards, so six card-heights plus two hands plus the middle panel
    // have to fit one phone. That caps a portrait card at roughly 1.25x what the grid drew before
    // this pass. Past that the rows have to overlap, which is the stack again.
    // ------------------------------------------------------------------
    private const int GridGap = 10;                 // the .tscn h/v_separation

    private const int GridGapPortrait = 6;          // every pixel here is a pixel off the cards

    /// Portrait spends the room reclaimed from the hand and the panel on the board itself.
    private const float PortraitBoardCardScale = 1.25f;

    // ------------------------------------------------------------------
    // The face-down deck in the middle panel
    //
    // Portrait lays it on its SIDE (Alexander, 2026-09-17: "deck should be rotated in portrait
    // mode to be horizontal"). The middle panel is a wide, short strip between the two boards
    // there, so an upright card is the one thing in it fighting the shape of its own space; on its
    // side it costs the strip roughly a third of the height and reads as a deck on a table edge.
    //
    // A container resets its children's rotation on every layout pass - the same fact that gave
    // Player 2 its Holder/Rotator pair - so the node that TURNS cannot be the row's direct child.
    // A plain holder stands in the row instead, reserving the footprint the turned card occupies,
    // and the deck sits inside it unmanaged.
    // ------------------------------------------------------------------
    private Control _deckHolder;

    private void ApplyDeckOrientation(bool portrait)
    {
        if (_mainDeckPosition == null) return;
        Vector2 size = portrait ? CardSize * DeckCardScalePortrait : CardSize;

        if (_deckHolder == null && _mainDeckPosition.GetParent() is Control row)
        {
            int at = _mainDeckPosition.GetIndex();
            _deckHolder = new Control
            {
                Name = "DeckHolder",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            row.AddChild(_deckHolder);
            row.MoveChild(_deckHolder, at);
            row.RemoveChild(_mainDeckPosition);
            _deckHolder.AddChild(_mainDeckPosition);
        }

        _mainDeckPosition.CustomMinimumSize = size;
        _mainDeckPosition.Size = size;
        _mainDeckPosition.PivotOffset = size / 2f; // turn about the middle, so the centre holds

        if (_deckHolder == null) return;

        // What the card occupies once it has turned: its own size with the axes swapped.
        Vector2 footprint = portrait ? new Vector2(size.Y, size.X) : size;
        _deckHolder.CustomMinimumSize = footprint;
        _mainDeckPosition.RotationDegrees = portrait ? 90f : 0f;
        _mainDeckPosition.Position = (footprint - size) / 2f;
    }

    /// The size of a card ON A BOARD. Everything else (deck, flying card, shop) keeps CardSize.
    private Vector2 BoardCardSize => _portraitLayout ? CardSize * PortraitBoardCardScale : CardSize;

    private void ResizeBoard(Control board)
    {
        if (board == null) return;
        Vector2 size = BoardCardSize;

        if (board is GridContainer grid)
        {
            grid.Columns = 3;
            int gap = _portraitLayout ? GridGapPortrait : GridGap;
            grid.AddThemeConstantOverride("h_separation", gap);
            grid.AddThemeConstantOverride("v_separation", gap);
        }

        foreach (Node child in board.GetChildren())
        {
            if (child is Control slot)
            {
                slot.CustomMinimumSize = size;
                foreach (Node inner in slot.GetChildren())
                {
                    if (inner is TextureRect view) ApplyCardSize(view, size);
                }
            }
        }
        RefreshBoardSlots(board);
    }

    /// Every empty slot shows its outline: on a grid the nine places ARE the board, and an empty
    /// one says how much room is left. (The stack hid them, because an outline peeking out from
    /// under the last card read as a card that was not there.)
    private void RefreshBoardSlots(Control board)
    {
        if (board == null) return;
        foreach (Node child in board.GetChildren())
        {
            if (child is not Control slot) continue;
            // SelfModulate: hides the slot's own outline without touching a card inside it.
            slot.SelfModulate = Colors.White;
        }
    }

    private Label _deckCountLabel;

    /// The count on the deck art - the counting aid, and the clearest signal that the deck is a
    /// real object with a bottom rather than a random number generator with a picture on it.
    ///
    /// Switched off in pass 22 (Alexander, 2026-09-16: "remaining cards don't need to be displayed
    /// as a number"). The label is simply never built; every use of it is null-safe.
    private const bool ShowDeckCount = false;

    public void BuildDeckCounter()
    {
        if (_mainDeckPosition == null || !ShowDeckCount) return;

        _deckCountLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _deckCountLabel.AddThemeColorOverride("font_color", Colors.White);
        _deckCountLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.75f));
        _deckCountLabel.AddThemeConstantOverride("outline_size", 8);
        _mainDeckPosition.AddChild(_deckCountLabel);
        _deckCountLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    private void UpdateDeckCounter()
    {
        if (_deckCountLabel == null) return;
        _deckCountLabel.Text = _host.GameStarted ? _host.Table.Remaining.ToString() : string.Empty;
    }

    /// The target, and the banner that narrates effect cards. Added around the existing set
    /// line in code rather than in the two scene files, so both scenes get them from one place.
    public void BuildTableBanners()
    {
        Control column = _setInfoLabel?.GetParent() as Control;
        if (column == null) return;
        int at = _setInfoLabel.GetIndex();

        // The target is the whole difficulty curve on this ladder - it moves from 20 to 23 to 18
        // and back up - so it is the one number that cannot be a fragment of a status string.
        _targetLabel = OverlayUi.MakeLabel(string.Empty, 36); // pass 23: was 30
        column.AddChild(_targetLabel);
        column.MoveChild(_targetLabel, at);

        // Kept VISIBLE and empty rather than hidden, so the panel does not jump by a line every
        // time an effect card resolves.
        _effectBanner = OverlayUi.MakeLabel(string.Empty, 21, new Color(0.86f, 0.74f, 1.0f));
        _effectBanner.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _effectBanner.CustomMinimumSize = new Vector2(0, 50); // two lines at the bigger font
        column.AddChild(_effectBanner);
        column.MoveChild(_effectBanner, at + 2);
    }

    /// A refusal is a sentence now, so the status line has to be able to hold one. The height for
    /// two lines is reserved up front - a label that grows when a card is picked up would shove
    /// the board down the screen mid-turn.
    private void ConfigureStatusLabel(Label label)
    {
        if (label == null) return;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.CustomMinimumSize = new Vector2(0, label.GetThemeFontSize("font_size") * 2.6f);
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
            _targetLabel.AddThemeColorOverride("font_color", OverlayUi.MedalGold);
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
        _targetLabel.AddThemeColorOverride("font_color", OverlayUi.MedalGold);
    }


    /// What an effect card just did, on the table. The explanation already existed - CardEffects
    /// writes one for every card it resolves - but it only ever went to the log, so at the table
    /// a score simply changed and nothing said why.
    public async void ShowEffectBanner(string text)
    {
        if (_effectBanner == null || string.IsNullOrEmpty(text)) return;

        uint token = ++_effectBannerToken;
        _effectBanner.Text = text;

        await _root.ToSignal(_root.GetTree().CreateTimer(5.0f), SceneTreeTimer.SignalName.Timeout);
        if (!_root.IsInsideTree() || token != _effectBannerToken) return; // a newer card owns the line

        _effectBanner.Text = string.Empty;
    }

    public void ClearEffectBanner()
    {
        _effectBannerToken++;
        if (_effectBanner != null) _effectBanner.Text = string.Empty;
    }

    // ------------------------------------------------------------------
    // UI refresh
    // ------------------------------------------------------------------
    /// The two score lines, in whichever form the orientation and the mirror call for.
    ///
    /// Its own method because TWO things need it: UpdateUI, whenever a number changes, and
    /// ApplyResponsiveLayout, BEFORE it measures - the form decided here is what the fixed slots
    /// are sized around, so a layout pass that ran first sized them around the outgoing one.
    ///
    /// The score is the number the whole decision hangs on, so it says whose it is and what it is
    /// chasing - "You  17/20" - rather than making the player find two labels on opposite sides of
    /// the screen and hold a target in their head (playtest, 2026-09-14).
    ///
    /// The target appears once per READER, never twice. Against the bot one person is looking at
    /// the screen, so it rides on their row only; in local 2-player each player reads their own
    /// row, so both carry it.
    private void RefreshScoreLines()
    {
        // The size is set every refresh rather than once in _Ready, because the mirror toggle
        // changes which of the two forms is on screen while the game is running.
        int scoreFont = _portraitLayout
            ? (IsMirrored ? ScoreFontSizeMirroredPortrait : ScoreFontSizePortrait)
            : (IsMirrored ? ScoreFontSizeMirrored : ScoreFontSize);
        // Each line is its own node now (ScoreLines): while a Modifier is picked up, the "You"
        // number shows what it WOULD be, in colour, instead of a sum on a line of its own
        // (Alexander, 2026-09-16).
        if (IsMirrored)
        {
            // Two lines, each side reading from ITS OWN player's point of view. "P1 13/20 P2 8"
            // was one dense row of four numbers and two labels that mean nothing to a stranger
            // (Alexander, 2026-09-15: "isn't elderly friendly"). A mirrored side is only ever read
            // by the person sitting at it, so "You" and "Them" are unambiguous there.
            SetScoreLines(_p1Score, scoreFont, "You: ", P1, $"Them: {P2.CurrentScore}");
            SetScoreLines(_p2Score, scoreFont, "You: ", P2, $"Them: {P1.CurrentScore}");
        }
        else if (_host.VsBot)
        {
            SetScoreLines(_p1Score, scoreFont, "You  ", P1, null);
            SetScoreLines(_p2Score, scoreFont, "Them  ", P2, null, preview: false, withTarget: false);
        }
        else
        {
            SetScoreLines(_p1Score, scoreFont, "P1  ", P1, null);
            SetScoreLines(_p2Score, scoreFont, "P2  ", P2, null);
        }
    }

    public void Refresh()
    {
        // A picked-up card that can no longer be played (spent, or the player just held) is
        // dropped before anything is drawn, so the status line and the buttons agree.
        _host.ValidateSelections();

        RefreshScoreLines();

        UpdateTargetLabel();
        UpdateDeckCounter();

        if (_p1StatusLabel != null) _p1StatusLabel.Text = _host.StatusFor(P1);
        if (_p2StatusLabel != null) _p2StatusLabel.Text = _host.StatusFor(P2);
        ApplyStatusColor(_p1StatusLabel, P1);
        ApplyStatusColor(_p2StatusLabel, P2);

        // Set wins are shown as chips next to the label (see UpdateWinChips).
        if (_p1WinsLabel != null) _p1WinsLabel.Text = "Wins:";
        if (_p2WinsLabel != null) _p2WinsLabel.Text = "Wins:";
        UpdateWinChips();

        // While the set-end explanation is up, EndSet owns this label.
        if (_host.GameStarted && _setInfoLabel != null && !State.IsGameOver && !_host.SetOverPending)
        {
            _setInfoLabel.Text = _host.SetInfoLine();
        }

        // Both sides act at once: each player's row stays live until THAT player has ended the
        // turn or is holding. Everything is locked while a set-end explanation is waiting to be
        // acknowledged. The solo scene's shared pair is Player 1's.
        bool p1Can = _host.CanAct(P1);
        bool p2Can = _host.CanAct(P2);
        SetEnabled(_drawCardButton, p1Can);
        SetEnabled(_holdButton, p1Can);
        SetEnabled(_p1DrawCardButton, p1Can);
        SetEnabled(_p1HoldButton, p1Can);
        SetEnabled(_p2DrawCardButton, p2Can);
        SetEnabled(_p2HoldButton, p2Can);

        // While a card is picked up, that player's Draw Card / Hold row is swapped for the
        // Play / +- / Put back row (same slot in the layout, so nothing moves).
        UpdateConfirmRow(P1, _p1ConfirmRow, _p1PlayButton, _p1FlipValueButton, _p1ActionRow);
        UpdateConfirmRow(P2, _p2ConfirmRow, _p2PlayButton, _p2FlipValueButton, _p2ActionRow);

        RefreshModifiersUI();
        RefreshBoardSlots(_p1BoardContainer);
        RefreshBoardSlots(_p2BoardContainer);
        _host.AfterRefresh();
        Defer(UpdateRotatorSize);

        // The layout is only as big as what is IN it, and what is in it changes here - the hands
        // are dealt, and the confirm row (four buttons) swaps in for Draw Card / Hold (two). A
        // check that only ran on resize measured the table before any of that existed. The latch
        // inside makes this cheap: at most one measurement is ever in flight.
        EnsureLayoutFits();
    }

    private static void SetEnabled(Button button, bool enabled)
    {
        if (button != null) button.Disabled = !enabled;
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

    /// Builds each player's Play / +- / Put back row, directly under the Draw Card / Hold row it
    /// stands in for. Found from the exported buttons rather than a NodePath, so it works in both
    /// scenes (and inside P2's rotator, so it flips with the rest of P2's side).
    public void BuildConfirmRows()
    {
        _p1ConfirmRow = BuildConfirmRow(P1, _p1DrawCardButton ?? _drawCardButton, out _p1PlayButton,
                                        out _p1FlipValueButton, out _p1ActionRow, out _p1ButtonSlot);
        _p2ConfirmRow = BuildConfirmRow(P2, _p2DrawCardButton, out _p2PlayButton,
                                        out _p2FlipValueButton, out _p2ActionRow, out _p2ButtonSlot);
    }

    private BoxContainer BuildConfirmRow(Player player, Button anchorButton, out Button playButton,
                                         out Button flipValueButton, out Control actionRow,
                                         out Control buttonSlot)
    {
        playButton = null;
        flipValueButton = null;
        actionRow = null;
        buttonSlot = null;
        if (anchorButton == null) return null; // that side has no buttons in this scene (the bot's)

        actionRow = anchorButton.GetParent() as Control; // the Draw Card / Hold row
        Node host = actionRow?.GetParent();              // the column that row lives in
        if (actionRow == null || host == null) return null;

        // A plain BoxContainer, not an HBox: only a BoxContainer can be turned vertical.
        BoxContainer row = new BoxContainer
        {
            Visible = false,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        row.AddThemeConstantOverride("separation", 12);

        // Play and Put back sit where Draw Card and Hold sit, and Flip Value goes last - so the
        // two buttons every card has never move, and a card without Flip Value leaves its space
        // empty at the end rather than closing the gap (pass 21).
        Button play = MakeConfirmButton("Play", new Color(0.24f, 0.62f, 0.31f));
        play.Pressed += () => _host.PlayPressed(player);
        row.AddChild(play);
        playButton = play;

        Button cancel = MakeConfirmButton("Put back", new Color(0.38f, 0.38f, 0.42f));
        cancel.Pressed += () => { _host.PutBackPressed(player); Refresh(); };
        row.AddChild(cancel);

        flipValueButton = MakeConfirmButton("Flip Value", new Color(0.22f, 0.44f, 0.78f));
        flipValueButton.Pressed += () => _host.FlipValuePressed(player);
        row.AddChild(flipValueButton);

        // Both rows go into one fixed-size slot where the action row was. The slot is as big as
        // the bigger of the two, so swapping them moves nothing else on the table.
        //
        // Measured, not remembered (pass 22): the size comes from the buttons and each row's
        // CURRENT orientation every time it is asked, and the rows keep their own height.
        Control action = actionRow;
        StableBox slot = new StableBox
        {
            Name = "ButtonSlot",
            StretchChildrenVertically = false,
            Measure = () => Bigger(MeasureButtonRow(action as BoxContainer), MeasureButtonRow(row)),
        };
        int at = actionRow.GetIndex();
        host.AddChild(slot);
        host.MoveChild(slot, at);
        actionRow.GetParent().RemoveChild(actionRow);
        slot.AddChild(actionRow);
        slot.AddChild(row);
        buttonSlot = slot;
        return row;
    }

    /// What a row of buttons needs laid out the way it is facing now. Hidden rows count too -
    /// that is what lets the slot hold the bigger of the two rows while only one is showing.
    private static Vector2 MeasureButtonRow(BoxContainer row)
    {
        if (row == null) return Vector2.Zero;
        int sep = row.GetThemeConstant("separation");
        float along = 0f, across = 0f;
        int count = 0;
        foreach (Node node in row.GetChildren())
        {
            if (node is not Control child) continue;
            Vector2 min = child.GetCombinedMinimumSize();
            along += row.Vertical ? min.Y : min.X;
            across = Mathf.Max(across, row.Vertical ? min.X : min.Y);
            count++;
        }
        if (count > 1) along += sep * (count - 1);
        return row.Vertical ? new Vector2(across, along) : new Vector2(along, across);
    }

    private static Vector2 Bigger(Vector2 a, Vector2 b) =>
        new Vector2(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y));

    private static Button MakeConfirmButton(string text, Color tint)
    {
        Button button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeColorOverride("font_color", Colors.White);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);

        StyleBoxFlat box = new StyleBoxFlat
        {
            BgColor = tint,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 14,
            ContentMarginBottom = 14,
        };
        StyleBoxFlat pressed = (StyleBoxFlat)box.Duplicate();
        pressed.BgColor = tint.Lightened(0.15f);

        button.AddThemeStyleboxOverride("normal", box);
        button.AddThemeStyleboxOverride("hover", pressed);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("focus", box);
        return button;
    }

    private void UpdateConfirmRow(Player player, BoxContainer row, Button playButton,
                                  Button flipValueButton, Control actionRow)
    {
        if (row == null) return;

        Card picked = _host.SelectedFor(player);
        row.Visible = picked != null;
        if (actionRow != null) actionRow.Visible = picked == null;
        if (flipValueButton != null)
        {
            // Kept in the layout and just not drawn or tappable, so its space never opens or
            // closes under the other two buttons.
            bool flippable = picked != null && picked.CanFlipValue;
            flipValueButton.Modulate = flippable ? Colors.White : new Color(1, 1, 1, 0);
            flipValueButton.Disabled = !flippable;
            flipValueButton.MouseFilter = flippable ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        }

        // An effect card that is not legal right now cannot be played at all, so the button says
        // so rather than the card bouncing back with a message. The status line above it is
        // already explaining why (see EffectPreview).
        if (playButton != null)
        {
            playButton.Disabled = picked != null && picked.Effect != CardEffect.None
                && !_host.CanPlayEffect(player, picked);
        }
    }

    private void RefreshModifiersUI()
    {
        if (_p1ModifierContainer == null || _p2ModifierContainer == null || P1 == null) return;

        // Detach immediately, not just QueueFree: queued nodes stay in the tree until the end of
        // the frame and would still count towards the hand's minimum size when the deferred
        // UpdateRotatorSize runs (P2's side then reserved room for 8-12 cards and pushed P1 off-screen).
        ClearChildren(_p1ModifierContainer);
        ClearChildren(_p2ModifierContainer);

        bool p1Can = _host.CanAct(P1);
        bool p2Can = _host.CanAct(P2);
        Card p1Picked = _host.SelectedFor(P1);
        Card p2Picked = _host.SelectedFor(P2);

        foreach (Card card in P1.Modifiers)
        {
            _p1ModifierContainer.AddChild(CreateModifierButton(
                card, !p1Can, card == p1Picked, p1Picked != null,
                () => _host.ModifierPressed(P1, card)));
        }

        foreach (Card card in P2.Modifiers)
        {
            _p2ModifierContainer.AddChild(CreateModifierButton(
                card, !p2Can, card == p2Picked, p2Picked != null,
                () => _host.ModifierPressed(P2, card)));
        }
    }

    /// A tappable modifier card: an invisible Button (so the theme's touch-friendly hit area
    /// and focus handling still apply) with the card art drawn on top. A picked-up card is lifted
    /// and the rest of the hand dims, so which card is in play is obvious without reading anything.
    private Button CreateModifierButton(Card card, bool disabled, bool selected, bool anySelected, Action onPressed)
    {
        Button button = new Button
        {
            Flat = true,
            CustomMinimumSize = ModifierCardSize,
            Disabled = disabled,
            FocusMode = Control.FocusModeEnum.None,
        };
        StyleBoxEmpty empty = new StyleBoxEmpty();
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
            button.AddThemeStyleboxOverride(state, empty);
        button.Pressed += onPressed;

        TextureRect view = CreateCardView(card, ModifierCardSize);
        view.MouseFilter = Control.MouseFilterEnum.Ignore;

        if (disabled) view.Modulate = new Color(0.55f, 0.55f, 0.55f);
        else if (anySelected && !selected) view.Modulate = new Color(0.5f, 0.5f, 0.55f); // the rest of the hand steps back
        else view.Modulate = Colors.White;

        button.AddChild(view);
        view.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        if (selected)
        {
            // Scale the art, not the Button: the Button is a container child and would have its
            // scale reset on the next layout pass, and growing it would shove the whole hand about.
            view.PivotOffset = ModifierCardSize / 2f;
            view.Scale = new Vector2(1.18f, 1.18f);

            StyleBoxFlat outline = new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0),
                BorderColor = new Color(1f, 0.92f, 0.4f),
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
            };
            outline.SetBorderWidthAll(4);

            Panel highlight = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            highlight.AddThemeStyleboxOverride("panel", outline);
            view.AddChild(highlight);
            highlight.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }

        return button;
    }

    // ------------------------------------------------------------------
    // Board slots
    // ------------------------------------------------------------------
    /// Clears the board and fills it with 9 empty, faintly outlined slots. Cards are placed
    /// INTO these slots (see InstantiateCardView) so the grid never grows or shifts.
    private static void ClearChildren(Node parent)
    {
        if (parent == null) return;
        foreach (Node child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    public void FillBoardWithSlots(Control board)
    {
        if (board == null) return;
        ClearChildren(board);

        for (int i = 0; i < BoardSlots; i++)
        {
            Panel slot = new Panel { CustomMinimumSize = BoardCardSize, MouseFilter = Control.MouseFilterEnum.Ignore };
            StyleBoxFlat style = new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0.18f),
                BorderColor = new Color(1, 1, 1, 0.12f),
            };
            style.SetBorderWidthAll(2);
            style.SetCornerRadiusAll(8);
            slot.AddThemeStyleboxOverride("panel", style);
            board.AddChild(slot);
        }
        RefreshBoardSlots(board);
    }

    private Control FindFreeSlot(Control board)
    {
        foreach (Node child in board.GetChildren())
        {
            if (child is Control slot && slot.GetChildCount() == 0) return slot;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Card views
    // ------------------------------------------------------------------
    private Rect2 RegionFor(Card card)
    {
        if (card.Type == CardType.Main) return RegionMain;
        // Before the sign test: a Shave carries Value 1 and would otherwise wear the blue "plus"
        // back, which is the opposite of what it does.
        if (card.Effect != CardEffect.None) return RegionEffect;
        if (card.Value < 0) return RegionMinus;
        return RegionPlus;
    }

    private AtlasTexture MakeAtlas(Texture2D sheet, Rect2 region)
    {
        return new AtlasTexture { Atlas = sheet, Region = region };
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
            label.Visible = !pipped;                   // the pips ARE the number when they are on
        }

        Label flipped = view.GetNodeOrNull<Label>("LabelMinus");
        if (flipped != null)
        {
            flipped.AddThemeFontSizeOverride("font_size", fontSize);
            flipped.Visible = flipValueFace;
            flipped.RotationDegrees = 0f; // upright for its owner; the CORNERS face the other way
        }

        // Corners, EXCEPT on a "+/-" card. That card already carries two numbers - +n over -n,
        // one per half - and it is already readable from both sides of the table because of it.
        // Adding corners put four numbers on one card and ran them into the borders
        // (Alexander, S25 Ultra, 2026-09-15). The halves are the two-way reading there.
        //
        int cornerFont = Mathf.Max(10, Mathf.RoundToInt(
            size.Y * (pipped ? PippedCornerFontScale : CornerFontScale)));
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
    }

    /// The dots in the middle of a main-deck card, in two columns the way a real card lays them
    /// out: ceil(n/2) rows of two, with a single centred dot on the last row when n is odd.
    private static void BuildPips(TextureRect view, int count, Vector2 size)
    {
        Control existing = view.GetNodeOrNull<Control>("Pips");
        if (existing != null)
        {
            view.RemoveChild(existing);
            existing.QueueFree();
        }
        if (count < 1 || count > 10) return;

        Control host = new Control { Name = "Pips", MouseFilter = Control.MouseFilterEnum.Ignore };
        view.AddChild(host);
        host.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        // Narrower and shorter than it was: the corner numbers grew into the space this used to
        // take, and a pip overlapping a digit is worse than a smaller pip.
        host.AnchorLeft = 0.30f;
        host.AnchorRight = 0.70f;
        host.AnchorTop = 0.26f;
        host.AnchorBottom = 0.74f;

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

        StyleBoxFlat pip = new StyleBoxFlat { BgColor = Colors.White };
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
        view.Texture = MakeAtlas(_cardSheet, RegionPlus); // blue, and the top half is what shows

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
            Texture = MakeAtlas(_cardSheet, RegionMinus),
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
            top.AnchorBottom = 0.5f;
            top.AddThemeFontSizeOverride("font_size", fontSize);
            top.Modulate = plusChosen ? Colors.White : DimmedHalf;
        }

        Label under = view.GetNodeOrNull<Label>("LabelMinus");
        if (under != null)
        {
            under.Text = "-" + magnitude;
            under.Visible = true;
            under.AddThemeFontSizeOverride("font_size", fontSize);
            under.Modulate = plusChosen ? DimmedHalf : Colors.White;
        }

        // Dim the art of the half that is not in play, so the choice reads from across the table.
        view.SelfModulate = plusChosen ? Colors.White : DimmedHalf;
        bottom.Modulate = plusChosen ? DimmedHalf : Colors.White;
    }

    /// Two sizes. The mirrored form carries two scores; stacked on two lines it is no longer
    /// wider than the screen (which was the original reason for shrinking it - a too-wide side
    /// inside a CenterContainer spills off BOTH edges and EnsureLayoutFits answers by scaling the
    /// whole UI down), so it only gives up what the extra line costs in height.
    private const int ScoreFontSize = 40;          // pass 23: was 34

    private const int ScoreFontSizeMirrored = 36;  // was 30

    private const int ActionFontSize = 35;         // was 30

    private const float ActionButtonHeight = 86f;  // was 76

    // Portrait sizes, raised again in pass 23 (Alexander, 2026-09-17: "cards and all other text
    // need to be MUCH bigger"). EnsureLayoutFits scales the whole table down if a phone cannot
    // take them, so this is not free: every point here is shared with the board through the fit.
    // The score line is the expensive one - it sits in the column BESIDE the board in portrait,
    // so its width comes straight off the cards. That is why it stops at 54 and not higher.
    private const int ScoreFontSizePortrait = 54;          // was 46

    private const int ScoreFontSizeMirroredPortrait = 46;  // was 40

    private const int ActionFontSizePortrait = 38;         // was 34

    private const float ActionButtonHeightPortrait = 92f;  // was 84

    private const int StatusFontSizePortrait = 33;         // was 28

    private const int WinsFontSizePortrait = 33;           // was 28

    private const float ChipSizePortrait = 42f;            // was 36

    private const int PanelFontSizePortrait = 34;          // was 28

    /// Font sizes that differ by orientation, applied on every layout pass.
    private void ApplyOrientationTypography(bool portrait)
    {
        foreach (Button button in ActionButtons())
            StyleActionButton(button, portrait);

        foreach (Label status in new[] { _p1StatusLabel, _p2StatusLabel })
        {
            if (status == null) continue;
            if (portrait) status.AddThemeFontSizeOverride("font_size", StatusFontSizePortrait);
            else status.RemoveThemeFontSizeOverride("font_size");
            ConfigureStatusLabel(status); // its reserved two lines follow the font
        }

        foreach (Label wins in new[] { _p1WinsLabel, _p2WinsLabel })
        {
            if (wins == null) continue;
            if (portrait) wins.AddThemeFontSizeOverride("font_size", WinsFontSizePortrait);
            else wins.RemoveThemeFontSizeOverride("font_size");
        }

        float chip = portrait ? ChipSizePortrait : ChipSize;
        foreach (HBoxContainer chips in new[] { _p1WinChips, _p2WinChips })
        {
            if (chips == null) continue;
            foreach (Node node in chips.GetChildren())
                if (node is Control c) c.CustomMinimumSize = new Vector2(chip, chip);
        }

        if (_setInfoLabel != null)
        {
            if (portrait) _setInfoLabel.AddThemeFontSizeOverride("font_size", PanelFontSizePortrait);
            else _setInfoLabel.RemoveThemeFontSizeOverride("font_size");
        }

        CompactPortraitPanel(portrait);
    }

    /// The middle panel sits BETWEEN the two boards in portrait, so every pixel of padding in it
    /// is a pixel the two 3x3 grids do not get (pass 23). Landscape keeps the roomier spacing -
    /// it has the width, and the playtesters said it already felt right.
    private void CompactPortraitPanel(bool portrait)
    {
        Control panel = _mainLayout?.GetNodeOrNull<Control>("SharedControlPanel");
        if (panel?.GetNodeOrNull<BoxContainer>("VBoxContainer") is not BoxContainer column) return;
        column.AddThemeConstantOverride("separation", portrait ? 4 : 10);
        if (_mainLayout != null) _mainLayout.AddThemeConstantOverride("separation", portrait ? 6 : 12);
    }

    private IEnumerable<Button> ActionButtons()
    {
        foreach (Button button in new[] { _drawCardButton, _holdButton, _p1DrawCardButton,
                                          _p1HoldButton, _p2DrawCardButton, _p2HoldButton })
            if (button != null) yield return button;

        // The confirm row stands in for Draw Card / Hold in the same slot, so it has to be the same
        // height - otherwise the whole side jumps every time a card is picked up or put back.
        foreach (Control row in new Control[] { _p1ConfirmRow, _p2ConfirmRow })
        {
            if (row == null) continue;
            foreach (Node child in row.GetChildren())
                if (child is Button button) yield return button;
        }
    }

    /// Blue: the picked-up Modifier keeps you at or under the target. Orange: it would take you over.
    private static readonly Color PreviewUnderColor = new Color(0.45f, 0.72f, 1.00f);

    private static readonly Color PreviewOverColor = new Color(1.00f, 0.55f, 0.30f);

    // ------------------------------------------------------------------
    // Score lines (pass 21)
    //
    // The exported ScoreLabel was one Label holding "You: 7/20\nThem: 12". A Label has one colour,
    // so the preview could not colour just the number - and the sum sat on a line of its own that
    // appeared and vanished as cards were picked up. Now each piece is its own node, built once
    // in the label's place; the exported label stays in the scene (hidden) so nothing that
    // references it breaks.
    // ------------------------------------------------------------------
    private sealed class ScoreLines
    {
        public Control Root;
        public Label YouPrefix;
        public Label YouValue;
        public Label Them;
    }

    private ScoreLines BuildScoreLines(Label source)
    {
        if (source?.GetParent() is not Control parent) return null;

        VBoxContainer root = new VBoxContainer { Name = "ScoreLines", MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddThemeConstantOverride("separation", 0);
        int at = source.GetIndex();
        parent.AddChild(root);
        parent.MoveChild(root, at);
        source.Visible = false;

        HBoxContainer youRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        youRow.AddThemeConstantOverride("separation", 0);
        root.AddChild(youRow);

        ScoreLines lines = new ScoreLines
        {
            Root = root,
            YouPrefix = new Label(),
            YouValue = new Label(),
            Them = new Label(),
        };
        youRow.AddChild(lines.YouPrefix);
        youRow.AddChild(lines.YouValue);
        root.AddChild(lines.Them);
        return lines;
    }

    private void SetScoreLines(ScoreLines lines, int fontSize, string prefix, Player player,
                               string themLine, bool preview = true, bool withTarget = true)
    {
        if (lines == null) return;

        foreach (Label label in new[] { lines.YouPrefix, lines.YouValue, lines.Them })
            label.AddThemeFontSizeOverride("font_size", fontSize);

        lines.YouPrefix.Text = prefix;

        int? previewed = preview ? PreviewedScore(player) : null;
        if (previewed.HasValue)
        {
            int value = previewed.Value;
            lines.YouValue.Text = _host.GameStarted ? $"{value}/{State.TargetScore}" : "-";
            lines.YouValue.AddThemeColorOverride("font_color",
                value > State.TargetScore ? PreviewOverColor : PreviewUnderColor);
        }
        else
        {
            lines.YouValue.Text = withTarget ? ScoreOf(player)
                                             : (_host.GameStarted ? player.CurrentScore.ToString() : "-");
            lines.YouValue.RemoveThemeColorOverride("font_color");
        }

        lines.Them.Visible = themLine != null;
        lines.Them.Text = themLine ?? string.Empty;
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

    /// The score is the biggest thing on the table and Draw Card / Hold are the biggest buttons,
    /// both from the same note: the 55+ players could not read either at arm's length.
    ///
    /// ONLY those two buttons grow. Restart / Exit / How to Play serve presses that happen once a
    /// session and sit behind the Menu button now; growing them as well would spend the portrait
    /// column's whole height budget, and EnsureLayoutFits would take it straight back by scaling
    /// the entire UI down - which is the trap this pass had to be built around, not sprung.
    public void StyleTableForReadability()
    {
        // The score leads its row. It was third, behind "Wins:" and the chips - which put the
        // largest and most-read thing on the table after the least-read (Alexander's sketch).
        foreach (Label score in new[] { _p1ScoreLabel, _p2ScoreLabel })
        {
            if (score?.GetParent() is Control row) row.MoveChild(score, 0);
        }

        foreach (Button button in ActionButtons())
            StyleActionButton(button, portrait: false);
    }

    private static void StyleActionButton(Button button, bool portrait)
    {
        if (button == null) return;
        button.AddThemeFontSizeOverride("font_size", portrait ? ActionFontSizePortrait : ActionFontSize);
        button.CustomMinimumSize = new Vector2(button.CustomMinimumSize.X,
                                               portrait ? ActionButtonHeightPortrait : ActionButtonHeight);
    }

    /// Portrait feedback: "centre is too clustered in portrait mode, but landscape feels nice".
    /// Landscape has a whole extra axis to spread the same content across, so the fix is NOT a
    /// landscape lock (the orientation lock was removed deliberately) - it is that the middle
    /// column carries too much for one phone-width strip.
    ///
    /// Three things move:
    ///  - How to Play / Restart / Exit / the mirror toggle go behind one "Menu" button. All four
    ///    were permanently on screen to serve presses that happen once a session.
    ///  - The solo scene's Draw Card / Hold leave the middle panel for Player 1's own side, under
    ///    the hand they act on and near the thumb, which is where the 2-player scene already has
    ///    them. (In the 2-player scene _drawCardButton is null and this does nothing.)
    ///  - The mode dropdown is hidden: the start menu has owned mode selection since pass 7. The
    ///    NODE stays, because "_gameModeButton != null" is how this class tells the two scenes
    ///    apart - deleting it would break the auto-start routing on both sides.
    ///
    /// Runs BEFORE BuildConfirmRows, which finds each action row from its exported button and
    /// inserts the Play / +- / Put back row next to it. Moving the row afterwards would leave the
    /// confirm row behind in the middle panel, which is exactly the bug this ordering prevents.
    public void CompactControlPanel()
    {
        if (_gameModeButton != null) _gameModeButton.Visible = false;

        Control actionRow = _drawCardButton?.GetParent() as Control;
        Control p1Layout = _root.GetNodeOrNull<Control>("GameUI/MainLayout/Player1Side/Layout");
        if (actionRow != null && p1Layout != null && actionRow.GetParent() != p1Layout)
        {
            actionRow.GetParent().RemoveChild(actionRow);
            p1Layout.AddChild(actionRow);
        }

        _host.BuildTableMenu();
    }

    /// Repaints the table and the standard cards for the rank the player is on. This is the whole
    /// progression display: no invented venue names, just a board that looks different every two
    /// rungs (Alexander, 2026-09-07).
    public void ApplyRankTheme()
    {
        RunData run = _host.InRun ? RunData.Instance : null;

        // No run (local 2-player, or the solo scene opened on its own) means the plain felt: the
        // clear colour is global and would otherwise follow us out of the run.
        Color table = (run == null) ? DefaultTableColor : run.CurrentRank.Table;
        _rankCardTint = (run == null) ? Colors.White : run.CurrentRank.CardTint;

        RenderingServer.SetDefaultClearColor(table);
        if (_mainDeckPosition is TextureRect deck)
        {
            // The gilded back is a profile reward, so it shows in local 2-player too.
            if (_cardSheet != null) deck.Texture = MakeAtlas(_cardSheet, DeckBackRegion);
            deck.SelfModulate = DeckBackTint;
        }
    }

    public TextureRect CreateCardView(Card card, Vector2 size)
    {
        TextureRect view = (TextureRect)_cardViewScene.Instantiate();
        view.Texture = MakeAtlas(_cardSheet, RegionFor(card));
        view.SetMeta("cardId", card.Id); // so RefreshCardFace can find this view again
        ApplyCardSize(view, size);

        string text = card.DisplayText; // read from Value, so a flipped card shows its new sign
        foreach (string name in CardLabelNames)
        {
            Label label = view.GetNodeOrNull<Label>(name);
            if (label != null) label.Text = text;
        }

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

        // Pips, and only on main-deck cards: they pip the 1-10 an ordinary playing card pips, and
        // a modifier is signed - there is no such thing as minus three dots.
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

        view.Texture = MakeAtlas(_cardSheet, RegionFor(card));

        string text = card.DisplayText; // computed from Value, so the new number is already in it
        foreach (string name in CardLabelNames)
        {
            Label label = view.GetNodeOrNull<Label>(name);
            if (label != null) label.Text = text;
        }

        // Copy rewrites a drawn main card's value, so its pips have to be re-counted - otherwise
        // the number in the corners and the dots in the middle disagree about the same card.
        if (view.HasMeta("pips"))
        {
            view.SetMeta("pips", card.Value);
            BuildPips(view, card.Value, BoardCardSize);
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
        }));
    }

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

        TextureRect cardNode = CreateCardView(card, BoardCardSize);

        // Drop the card into the next empty slot; if the board is somehow full, let the grid grow.
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
        Defer(() => AnimateCardDrop(cardNode, delay));
    }

    private void AnimateCardDrop(Control realCard, float delay = 0f)
    {
        // Fallback in case the deck isn't assigned in the inspector
        if (_mainDeckPosition == null || !GodotObject.IsInstanceValid(realCard))
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
            Texture = MakeAtlas(_cardSheet, DeckBackRegion),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Size = BoardCardSize,
            PivotOffset = BoardCardSize / 2f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SelfModulate = DeckBackTint,
        };
        _root.AddChild(fakeCard); // on the scene root so it draws above everything

        // 2. Start on the deck, small and upside down
        Vector2 deckCenter = _mainDeckPosition.GetGlobalTransform() * (_mainDeckPosition.Size / 2f);
        fakeCard.GlobalPosition = deckCenter - BoardCardSize / 2f;
        fakeCard.RotationDegrees = -180f;
        fakeCard.Scale = new Vector2(0.5f, 0.5f);

        // Target the slot's visual centre. Player 2's side may be rotated 180 degrees, so
        // go through the full global transform instead of GlobalPosition.
        Vector2 targetCenter = realCard.GetGlobalTransform() * (realCard.Size / 2f);
        float targetRotation = Mathf.RadToDeg(realCard.GetGlobalTransform().Rotation);

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
        tween.TweenProperty(fakeCard, "global_position", targetCenter - BoardCardSize / 2f, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(fakeCard, "rotation_degrees", targetRotation, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(fakeCard, "scale", Vector2.One, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            fakeCard.QueueFree();
            if (GodotObject.IsInstanceValid(realCard)) realCard.Modulate = Colors.White;
            if (_sfxPlace != null) _sfxPlace.Play();
        }));
    }

    private void RevealWithoutFlight(Control realCard)
    {
        if (!GodotObject.IsInstanceValid(realCard)) return;
        realCard.Modulate = Colors.White;
        if (_sfxPlace != null) _sfxPlace.Play();
    }

    // ------------------------------------------------------------------
    // Set-win chips
    // ------------------------------------------------------------------
    /// Adds a row of poker chips right after the "Wins" label (one per set needed to win the match).
    private HBoxContainer EnsureWinChips(Label winsLabel)
    {
        if (winsLabel == null || _chipSheet == null) return null;
        Node parent = winsLabel.GetParent();
        if (parent == null) return null;

        HBoxContainer chips = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        chips.AddThemeConstantOverride("separation", 4);
        for (int i = 0; i < WinsToTakeMatch; i++)
        {
            chips.AddChild(new TextureRect
            {
                Texture = MakeAtlas(_chipSheet, RegionChipEmpty),
                CustomMinimumSize = new Vector2(ChipSize, ChipSize),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            });
        }
        parent.AddChild(chips);
        parent.MoveChild(chips, winsLabel.GetIndex() + 1);
        return chips;
    }

    private void UpdateWinChips()
    {
        SetChips(_p1WinChips, State.SetsWonPlayer1);
        SetChips(_p2WinChips, State.SetsWonPlayer2);
    }

    private void SetChips(HBoxContainer chips, int wins)
    {
        if (chips == null) return;
        int i = 0;
        foreach (Node child in chips.GetChildren())
        {
            if (child is TextureRect chip && chip.Texture is AtlasTexture atlas)
            {
                atlas.Region = i < wins ? RegionChipWon : RegionChipEmpty;
            }
            i++;
        }
    }

    // The score as three separate nodes per side, so the "You" number can change colour on its
    // own while a Modifier is being previewed (pass 21).
    private ScoreLines _p1Score;

    private ScoreLines _p2Score;

    private BoxContainer _p1ConfirmRow;

    private BoxContainer _p2ConfirmRow;

    // Draw Card / Hold and Play / Put back / Flip Value share ONE fixed-size slot per player, so
    // picking a card up no longer resizes the side around it (pass 21).
    private Control _p1ButtonSlot;

    private Control _p2ButtonSlot;

    private Button _p1PlayButton;

    private Button _p2PlayButton;

    private Button _p1FlipValueButton;

    private Button _p2FlipValueButton;

    private Control _p1ActionRow;

    private Control _p1SideLayout;

    private Control _p2SideLayout;

    private Control _p2ActionRow;
}
