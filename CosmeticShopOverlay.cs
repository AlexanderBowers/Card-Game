using Godot;
using System;

/// <summary>
/// The Shop, opened from the main menu (playtest, 2026-09-30): decks and boards bought with
/// medals.
///
/// A DECK is the player's card back plus the look of their 1-10 cards; a BOARD is the table they
/// play on outside the ladder (local 2-player, and later online and endless). One of each per
/// rank. Bronze is owned from the start; the rest unlock for purchase once the player has beaten
/// that rank's first stage, and cost Cosmetics.Price medals. What the player picks here is
/// the only way their deck or board changes - a stage never changes it.
///
/// Reads and writes RunData's cosmetic fields only; closing hands back to the menu, which
/// refreshes the table theme.
/// </summary>
public partial class CosmeticShopOverlay : Control
{
    private const string ArtDir = "res://assets/aimfor20_art/";
    private static readonly Vector2 CardPreview = new Vector2(64, 87);
    private static readonly Vector2 BoardPreview = new Vector2(66, 117);

    private Action _onClosed;
    private bool _showBoards;
    private bool _built;

    private Label _medals;
    private Button _decksTab;
    private Button _boardsTab;
    private VBoxContainer _rows;
    private ScrollContainer _scroll;

    // Drag-to-scroll (playtest, 2026-09-30: "difficult to scroll; it requires holding the
    // scrollbar"). The rows are buttons and panels, which swallow the touch before the
    // ScrollContainer sees it, so the overlay scrolls the list itself from _Input - and a press
    // that turned into a drag never buys or equips anything.
    private bool _dragging;
    private bool _dragMoved;
    private float _dragDistance;
    private const float DragThreshold = 12f;

    public void Build()
    {
        if (_built) return;
        _built = true;

        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        OverlayUi.AddDim(this);

        // No outer scroll: the list below scrolls itself, and is sized to the screen in Open.
        VBoxContainer box = OverlayUi.AddPanel(this, contentMargin: 22, separation: 10, scroll: false);
        box.AddChild(OverlayUi.MakeLabel("Customizations", 32));
        _medals = OverlayUi.MakeLabel("", 20, OverlayUi.MedalGold);
        box.AddChild(_medals);

        HBoxContainer tabs = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        tabs.AddThemeConstantOverride("separation", 10);
        _decksTab = TabButton("Decks", () => { _showBoards = false; Refresh(); });
        _boardsTab = TabButton("Boards", () => { _showBoards = true; Refresh(); });
        tabs.AddChild(_decksTab);
        tabs.AddChild(_boardsTab);
        box.AddChild(tabs);

        // Five rows can outgrow a landscape phone, so they scroll inside a fixed-height window.
        ScrollContainer scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(430, 460),
            ScrollDeadzone = 8,
        };
        _scroll = scroll;
        box.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_rows);

        Button close = new Button { Text = "Close", CustomMinimumSize = new Vector2(200, 44) };
        OverlayUi.StyleButton(close);
        close.Pressed += Close;
        CenterContainer closeRow = new CenterContainer();
        closeRow.AddChild(close);
        box.AddChild(closeRow);
    }

    public void Open(Action onClosed = null)
    {
        Build();
        _onClosed = onClosed;
        Node parent = GetParent();
        if (parent != null) OverlayUi.BringToFront(this);
        FitListToScreen();
        Visible = true;
        Refresh();
    }

    /// The list's window is as tall as the screen leaves it - up to its full 460 on a phone held
    /// upright, less on a wide phone held sideways (size check, 2026-10-06: the panel ran off a
    /// 20:9 screen), more never: five rows is all there is.
    private void FitListToScreen()
    {
        if (_scroll == null) return;
        float viewH = OverlayUi.ViewSize(this).Y;
        (float top, float bottom) = OverlayUi.SafeInsets(this);
        const float Chrome = 300f; // title, medals, tabs, Close, the panel's margins
        _scroll.CustomMinimumSize = new Vector2(430, Mathf.Clamp(viewH - top - bottom - Chrome, 220f, 460f));
    }

    private void Close()
    {
        Visible = false;
        Action done = _onClosed;
        _onClosed = null;
        done?.Invoke();
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible || _scroll == null) return;

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
            _dragging = _scroll.GetGlobalRect().HasPoint(at);
            _dragMoved = false;
            _dragDistance = 0f;
        }
        else
        {
            _dragging = false;
        }
    }

    private void Drag(float dy)
    {
        _dragDistance += Mathf.Abs(dy);
        if (_dragDistance > DragThreshold) _dragMoved = true;
        if (_dragMoved) _scroll.ScrollVertical -= Mathf.RoundToInt(dy);
    }

    private Button TabButton(string text, Action onPressed)
    {
        Button b = new Button { Text = text, CustomMinimumSize = new Vector2(150, 44), FocusMode = FocusModeEnum.None };
        b.Pressed += onPressed;
        return b;
    }

    private void Refresh()
    {
        RunData run = RunData.Instance;
        if (run == null) { Close(); return; }

        _medals.Text = $"{run.Medals} medals";
        OverlayUi.StyleButton(_decksTab, primary: !_showBoards);
        OverlayUi.StyleButton(_boardsTab, primary: _showBoards);

        OverlayUi.ClearChildren(_rows);
        foreach (string key in Cosmetics.Keys)
            _rows.AddChild(Row(run, key));
    }

    private Control Row(RunData run, string key)
    {
        bool board = _showBoards;
        bool owned = (board ? run.OwnedBoards : run.OwnedDecks).Contains(key);
        bool unlocked = run.CosmeticUnlocked(key);
        // Not yet earned = not yet seen (playtest, 2026-09-30): a "?" where the art would be, and
        // Endless's set gives away nothing at all, not even its name.
        bool hidden = !owned && !unlocked;
        bool secret = hidden && key == "endless";
        string name = secret ? "???"
                    : char.ToUpperInvariant(key[0]) + key.Substring(1) + (board ? " Board" : " Deck");

        PanelContainer frame = new PanelContainer();
        StyleBoxFlat style = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.55f),
            BorderColor = new Color(0.17f, 0.21f, 0.29f, 0.12f),
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(12);
        style.SetContentMarginAll(8);
        frame.AddThemeStyleboxOverride("panel", style);

        HBoxContainer row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        frame.AddChild(row);

        // What it looks like: a deck is its back beside a 7; a board is the table itself.
        HBoxContainer preview = new HBoxContainer();
        preview.AddThemeConstantOverride("separation", 4);
        if (hidden)
        {
            preview.AddChild(Mystery(board ? BoardPreview : CardPreview));
            if (!board) preview.AddChild(Mystery(CardPreview));
        }
        else if (board)
        {
            preview.AddChild(Picture($"playmats/playmat_{key}_portrait.png", BoardPreview));
        }
        else
        {
            preview.AddChild(Picture($"backs/card_back_{key}.png", CardPreview));
            preview.AddChild(Picture($"cards/main/main_7_{key}.png", CardPreview));
        }
        row.AddChild(preview);

        VBoxContainer info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        Label title = OverlayUi.MakeLabel(name, 20, OverlayUi.Ink);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        info.AddChild(title);

        bool inUse = (board ? run.SelectedBoard : run.SelectedDeck) == key;
        int price = Cosmetics.Price(key);

        string note = owned ? (inUse ? "In use" : "Owned")
                    : unlocked ? $"{price} medals"
                    : secret ? "???"
                    : Cosmetics.UnlockStepIndex(key) >= 0
                        ? $"Beat stage {Cosmetics.UnlockStepIndex(key) + 1} to unlock."
                        : "Climb further up the ladder to unlock.";
        Label sub = OverlayUi.MakeLabel(note, 14, owned || unlocked ? OverlayUi.Muted : OverlayUi.Warning);
        sub.HorizontalAlignment = HorizontalAlignment.Left;
        sub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        info.AddChild(sub);
        row.AddChild(info);

        Button action = new Button { CustomMinimumSize = new Vector2(96, 44), FocusMode = FocusModeEnum.None };
        if (owned)
        {
            action.Text = inUse ? "Using" : "Use";
            action.Disabled = inUse;
            action.Pressed += () => { if (_dragMoved) return; run.SelectCosmetic(key, board); Refresh(); };
            OverlayUi.StyleButton(action);
        }
        else
        {
            action.Text = unlocked ? "Buy" : "Locked";
            action.Disabled = !unlocked || run.Medals < price;
            action.Pressed += () => { if (_dragMoved) return; run.BuyCosmetic(key, board); Refresh(); };
            OverlayUi.StyleButton(action, primary: unlocked && run.Medals >= price);
        }
        CenterContainer actionBox = new CenterContainer();
        actionBox.AddChild(action);
        row.AddChild(actionBox);
        return frame;
    }

    /// A card-shaped "?" standing in for art the player has not earned a look at.
    private static Control Mystery(Vector2 size)
    {
        PanelContainer card = new PanelContainer { CustomMinimumSize = size, MouseFilter = MouseFilterEnum.Ignore };
        StyleBoxFlat style = new StyleBoxFlat
        {
            BgColor = new Color(0.17f, 0.21f, 0.29f, 0.85f),
            BorderColor = new Color(1f, 1f, 1f, 0.35f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(8);
        card.AddThemeStyleboxOverride("panel", style);
        Label mark = OverlayUi.MakeLabel("?", 34, new Color(1f, 1f, 1f, 0.9f));
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.MouseFilter = MouseFilterEnum.Ignore;
        card.AddChild(mark);
        return card;
    }

    private static Control Picture(string relative, Vector2 size)
    {
        string path = ArtDir + relative;
        TextureRect pic = new TextureRect
        {
            Texture = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = size,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        return pic;
    }
}
