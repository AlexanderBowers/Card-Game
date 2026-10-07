using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The deck screen: the twelve side-deck slots and the player's collection, tap a card to move it
/// between them. Four of those twelve are dealt at random at the start of each match - the deck is
/// chosen, the hand is not.
///
/// Full screen (2026-10-05, after "difficult to scroll" on the old centred panel): built like How
/// to Play - a dim backdrop and a panel anchored to all four edges, clear of the notch and the
/// gesture bar.
///   - The Collection on the left (the only thing that scrolls), the Deck on the right - the
///     original arrangement, which Alexander preferred to a stacked one (2026-10-05). In portrait
///     the Deck is two columns of six; in landscape whichever shape gives the biggest cards.
///   - The counter and Start the Match are pinned at the bottom.
/// Card size is worked out from the room the panel actually has, so cards grow on big screens.
///
/// Writes RunData.SideDeck (indices into RunData.Inventory) when Start the Match is pressed, so a
/// run that is quit here keeps the deck it arrived with rather than a half-built one.
/// </summary>
public partial class DeckOverlay : Control
{
    private const int GridGap = 8;
    private const int SectionGap = 6;
    private const int BodyGap = 16;
    private const int PortraitColumns = 4;
    private const int LandscapeDeckColumns = 3;
    private const int PanelPad = 18;      // the panel's content margin
    private const int BoxGap = 10;        // between the panel's rows
    private const int ScrollBarRoom = 16; // the Collection's vertical scroll bar
    private const float DragThreshold = 12f;

    private Func<Card, Vector2, Control> _cardFactory;
    private Action _onDone;
    private readonly List<int> _deck = new List<int>();   // working copy; committed on Start
    private Vector2 _baseCardSize = new Vector2(59, 80);  // only its aspect and the size limits use it
    private Vector2 _cardSize = new Vector2(59, 80);
    private int _collectionColumns = PortraitColumns;
    private bool _portrait = true;
    private Vector2 _laidOutFor = Vector2.Zero;

    private PanelContainer _panel;
    private Control _header;
    private Control _hint;
    private Control _startRow;
    private float _collectionViewH;
    private BoxContainer _body;
    private VBoxContainer _deckSection;
    private VBoxContainer _collectionSection;
    private GridContainer _collectionGrid;
    private GridContainer _slotGrid;
    private ScrollContainer _collectionScroll;
    private Label _collectionLabel;
    private Label _deckLabel;
    private Label _countLabel;
    private Button _continueButton;
    private bool _built;

    // Drag-to-scroll, the same fix the cosmetic shop got (playtest, 2026-09-30): the cards are
    // buttons and swallow the touch before the ScrollContainer sees it, so the overlay scrolls the
    // Collection itself - and a press that turned into a drag never moves a card.
    private bool _dragging;
    private bool _dragMoved;
    private float _dragDistance;

    // ------------------------------------------------------------------
    // Building and opening
    // ------------------------------------------------------------------
    public void Setup(Func<Card, Vector2, Control> cardFactory)
    {
        _cardFactory = cardFactory;
        Build();
    }

    private void Build()
    {
        if (_built) return;
        _built = true;

        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // The backdrop stops touches too, so nothing reaches the table underneath.
        ColorRect dim = new ColorRect { Color = OverlayUi.DimColor, MouseFilter = MouseFilterEnum.Stop };
        AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        OverlayUi.StylePanel(_panel, 18);
        AddChild(_panel);
        OverlayUi.FillScreen(_panel);

        VBoxContainer box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(box);

        // The title.
        HBoxContainer header = new HBoxContainer();
        _header = header;
        header.AddThemeConstantOverride("separation", 8);
        box.AddChild(header);
        // No corner button (2026-10-05): Start the Match is the one way on from here.
        Label title = OverlayUi.MakeLabel("Your Deck", 30);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);

        Label hint = OverlayUi.MakeLabel(
            "Tap a Modifier to move it in or out of your deck.\nFour of your twelve are dealt to you each match.",
            18, OverlayUi.Muted);
        hint.AutowrapMode = TextServer.AutowrapMode.Word;
        _hint = hint;
        box.AddChild(hint);

        // The body flips between stacked (portrait) and side by side (landscape).
        _body = new BoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", BodyGap);
        box.AddChild(_body);
        _body.Resized += () => CallDeferred(nameof(LayoutForSize));

        // The Deck: twelve slots, never scrolls.
        _deckSection = new VBoxContainer();
        _deckSection.AddThemeConstantOverride("separation", SectionGap);
        _body.AddChild(_deckSection);
        _deckLabel = OverlayUi.MakeLabel("Deck (12)", 20);
        _deckSection.AddChild(_deckLabel);
        _slotGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        _slotGrid.AddThemeConstantOverride("h_separation", GridGap);
        _slotGrid.AddThemeConstantOverride("v_separation", GridGap);
        _deckSection.AddChild(_slotGrid);

        // The Collection: everything owned that is not in the deck - the only thing that scrolls.
        _collectionSection = new VBoxContainer();
        _collectionSection.AddThemeConstantOverride("separation", SectionGap);
        _body.AddChild(_collectionSection);
        _collectionLabel = OverlayUi.MakeLabel("Collection", 20);
        _collectionSection.AddChild(_collectionLabel);
        _collectionScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ScrollDeadzone = 8,
        };
        _collectionSection.AddChild(_collectionScroll);
        _collectionGrid = new GridContainer
        {
            Columns = PortraitColumns,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter | SizeFlags.Expand,
        };
        _collectionGrid.AddThemeConstantOverride("h_separation", GridGap);
        _collectionGrid.AddThemeConstantOverride("v_separation", GridGap);
        _collectionScroll.AddChild(_collectionGrid);

        // Collection on the left, Deck on the right (the original layout, kept full screen).
        _body.MoveChild(_collectionSection, 0);

        // Pinned at the bottom.
        _countLabel = OverlayUi.MakeLabel("", 22, OverlayUi.MedalGold);
        box.AddChild(_countLabel);
        _continueButton = new Button { Text = "Start the Match", CustomMinimumSize = new Vector2(260, 52) };
        OverlayUi.StyleButton(_continueButton, primary: true);
        _continueButton.Pressed += Close;
        CenterContainer startRow = new CenterContainer();
        _startRow = startRow;
        startRow.AddChild(_continueButton);
        box.AddChild(startRow);
    }

    public override void _Ready()
    {
        GetViewport().SizeChanged += OnViewportResized;
    }

    public override void _ExitTree()
    {
        Viewport viewport = GetViewport();
        if (viewport != null) viewport.SizeChanged -= OnViewportResized;
    }

    private void OnViewportResized()
    {
        if (_panel != null) OverlayUi.FillScreen(_panel);
    }

    public void Open(Vector2 cardSize, Action onDone)
    {
        _onDone = onDone;
        _baseCardSize = cardSize;
        _cardSize = cardSize;

        RunData run = RunData.Instance;
        if (!_built || run == null)
        {
            _onDone = null;
            onDone?.Invoke(); // never strand the run because the deck screen failed to build
            return;
        }

        _deck.Clear();
        _deck.AddRange(run.SideDeck);

        Node parent = GetParent();
        if (parent != null) OverlayUi.BringToFront(this);
        OverlayUi.FillScreen(_panel);
        Visible = true;
        _laidOutFor = Vector2.Zero;
        Refresh();                              // something on screen this frame...
        CallDeferred(nameof(LayoutForSize));    // ...then sized to the room once the panel has laid out
    }

    private void Close()
    {
        RunData.Instance?.SetSideDeck(_deck);
        Finish();
    }

    private void Finish()
    {
        Visible = false;
        _dragging = false;
        Action done = _onDone;
        _onDone = null;
        done?.Invoke();
    }

    // ------------------------------------------------------------------
    // Sizing the cards to the room the panel has
    // ------------------------------------------------------------------
    private void LayoutForSize()
    {
        if (!Visible || _body == null) return;

        // The room is worked out from the SCREEN, not read off the body: the body is as big as
        // its cards ask for, so measuring it after a layout with the wrong card size would feed
        // that size straight back in (and push Start the Match off the bottom).
        Vector2 view = OverlayUi.ViewSize(this);
        float panelW = view.X - _panel.OffsetLeft + _panel.OffsetRight;
        float panelH = view.Y - _panel.OffsetTop + _panel.OffsetBottom;
        float chrome = _header.GetCombinedMinimumSize().Y + _hint.GetCombinedMinimumSize().Y
                     + _countLabel.GetCombinedMinimumSize().Y + _startRow.GetCombinedMinimumSize().Y
                     + 4 * BoxGap;
        Vector2 room = new Vector2(panelW - 2 * PanelPad, panelH - 2 * PanelPad - chrome);
        if (room.X < 10 || room.Y < 10) return;
        if ((room - _laidOutFor).Length() < 2f) return; // already laid out for this size
        _laidOutFor = room;

        // 2026-10-05 (S25): back to the earlier format - the Collection on the left, the Deck on
        // the right - but full screen. Stacking the Deck over the Collection was cumbersome: with
        // 30+ Modifiers owned and 12 chosen, the Collection got a sliver of the screen.
        //   Portrait: the Deck is two columns of six, for readability; the Collection takes the
        //   rest of the width (at least three columns) and the full height, and scrolls.
        //   Landscape: the Deck takes whichever of 3x4 / 4x3 gives (6x2 looked stretched - 2026-10-05) the biggest cards.
        _portrait = view.Y > view.X;
        _body.Vertical = false;

        float aspect = _baseCardSize.Y / Mathf.Max(1f, _baseCardSize.X);
        float labelH = _deckLabel.GetCombinedMinimumSize().Y;
        float gridH = room.Y - labelH - SectionGap;
        const int MinCollectionColumns = 3;

        int deckColumns = 2;
        float width = 0f;
        // A tall phone keeps two columns of six. A near-square screen held upright (a Fold opened,
        // an iPad, a Flip's cover) has width to spare and short height, so it may take 3x4 or
        // 4x3 if that gives bigger cards (size check, 2026-10-07: the cards stayed phone-sized
        // in a sea of empty panel).
        bool tallPortrait = _portrait && view.Y / Mathf.Max(1f, view.X) > 1.6f;
        foreach (int columns in tallPortrait ? new[] { 2 } : _portrait ? new[] { 2, 3, 4 } : new[] { 3, 4 })
        {
            int rows = (RunData.SideDeckSize + columns - 1) / columns;
            float byHeight = (gridH - (rows - 1) * GridGap) / rows / aspect;
            // The deck's columns plus at least three of the Collection's, across the width.
            int across = columns + MinCollectionColumns;
            float byWidth = (room.X - BodyGap - ScrollBarRoom - (across - 2) * GridGap) / across;
            float w = Mathf.Min(byHeight, byWidth);
            if (w > width) { width = w; deckColumns = columns; }
        }

        // Never tiny, never silly-big.
        width = Mathf.Clamp(width, _baseCardSize.X * 0.5f, _baseCardSize.X * 2.2f);
        _cardSize = new Vector2(Mathf.Floor(width), Mathf.Floor(width * aspect));

        float deckWidth = deckColumns * _cardSize.X + (deckColumns - 1) * GridGap;
        float collectionRoom = room.X - deckWidth - BodyGap - ScrollBarRoom;
        _collectionColumns = Mathf.Clamp(
            Mathf.FloorToInt((collectionRoom + GridGap) / (_cardSize.X + GridGap)), 1, 8);

        _slotGrid.Columns = deckColumns;
        _collectionGrid.Columns = _collectionColumns;
        _deckSection.SizeFlagsVertical = SizeFlags.ExpandFill;
        _deckSection.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _collectionSection.SizeFlagsVertical = SizeFlags.ExpandFill;
        _collectionSection.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        // How tall the Collection's window is, for the "scroll for more" hint.
        _collectionViewH = gridH;

        Refresh();
    }

    // ------------------------------------------------------------------
    // Moving cards between the collection and the deck
    // ------------------------------------------------------------------
    private void Refresh()
    {
        RunData run = RunData.Instance;
        if (run == null) return;

        OverlayUi.ClearChildren(_collectionGrid);
        OverlayUi.ClearChildren(_slotGrid);

        bool deckFull = _deck.Count >= RunData.SideDeckSize;
        int spare = 0;

        for (int index = 0; index < run.Inventory.Count; index++)
        {
            if (_deck.Contains(index)) continue;
            spare++;

            int captured = index;
            Control view = _cardFactory(run.Inventory[index].ToCard(), _cardSize);
            if (deckFull) view.Modulate = new Color(1f, 1f, 1f, 0.4f); // no room until one comes out
            Button button = OverlayUi.CardButton(view, _cardSize, () => { if (!_dragMoved) AddToDeck(captured); });
            button.Disabled = deckFull;
            _collectionGrid.AddChild(button);
        }

        // Tell the player to scroll only when there is something below the fold.
        int rows = (spare + _collectionColumns - 1) / Math.Max(1, _collectionColumns);
        float contentH = rows * _cardSize.Y + Math.Max(0, rows - 1) * GridGap;
        float viewH = _collectionViewH > 0 ? _collectionViewH : _collectionScroll.Size.Y;
        bool scrolls = viewH > 0 && contentH > viewH + 1f;
        _collectionLabel.Text = spare == 0 ? "Collection (empty)"
                              : scrolls ? $"Collection ({spare}) - scroll for more"
                              : $"Collection ({spare})";

        for (int slot = 0; slot < RunData.SideDeckSize; slot++)
        {
            if (slot < _deck.Count)
            {
                int inventoryIndex = _deck[slot];
                if (inventoryIndex < 0 || inventoryIndex >= run.Inventory.Count) continue;

                int captured = inventoryIndex;
                Control view = _cardFactory(run.Inventory[inventoryIndex].ToCard(), _cardSize);
                _slotGrid.AddChild(OverlayUi.CardButton(view, _cardSize, () => { if (!_dragMoved) RemoveFromDeck(captured); }));
            }
            else
            {
                _slotGrid.AddChild(OverlayUi.EmptySlot(_cardSize));
            }
        }

        // Exactly twelve, unless the player somehow owns fewer than that.
        int required = Math.Min(RunData.SideDeckSize, run.Inventory.Count);
        bool ready = _deck.Count == required;
        _countLabel.Text = $"{_deck.Count} / {required} chosen";
        _countLabel.AddThemeColorOverride("font_color", ready ? OverlayUi.MedalGold : OverlayUi.Warning);
        _continueButton.Disabled = !ready;
    }

    private void AddToDeck(int inventoryIndex)
    {
        if (_deck.Count >= RunData.SideDeckSize || _deck.Contains(inventoryIndex)) return;
        _deck.Add(inventoryIndex);
        Refresh();
    }

    private void RemoveFromDeck(int inventoryIndex)
    {
        _deck.Remove(inventoryIndex);
        Refresh();
    }

    // ------------------------------------------------------------------
    // Drag-to-scroll on the Collection
    // ------------------------------------------------------------------
    public override void _Input(InputEvent e)
    {
        if (!Visible || _collectionScroll == null) return;

        // A phone sends touches AND emulated mouse events; read whichever is the real one, once.
        bool touch = DisplayServer.IsTouchscreenAvailable();
        switch (e)
        {
            case InputEventScreenTouch t when touch:
                Press(t.Pressed, t.Position);
                break;
            case InputEventMouseButton mb when !touch && mb.ButtonIndex == MouseButton.Left:
                Press(mb.Pressed, mb.Position);
                break;
            case InputEventScreenDrag d when touch && _dragging:
                Drag(d.Relative.Y);
                break;
            case InputEventMouseMotion mm when !touch && _dragging && (mm.ButtonMask & MouseButtonMask.Left) != 0:
                Drag(mm.Relative.Y);
                break;
        }
    }

    private void Press(bool down, Vector2 at)
    {
        if (down)
        {
            _dragging = _collectionScroll.GetGlobalRect().HasPoint(at);
            _dragMoved = false;
            _dragDistance = 0f;
        }
        else
        {
            // Released: the card button's Pressed fires after this and still sees _dragMoved.
            _dragging = false;
        }
    }

    private void Drag(float dy)
    {
        _dragDistance += Mathf.Abs(dy);
        if (_dragDistance > DragThreshold) _dragMoved = true;
        if (_dragMoved) _collectionScroll.ScrollVertical -= Mathf.RoundToInt(dy);
    }
}
