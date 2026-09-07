using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The armory: the player's whole collection on the left, the twelve side-deck slots on the right,
/// tap a card to move it between them. Four of those twelve are dealt at random at the start of
/// each match - the deck is chosen, the hand is not.
///
/// Writes RunData.SideDeck (indices into RunData.Inventory) when Continue is pressed, so a run that
/// is quit here keeps the deck it arrived with rather than a half-built one.
/// </summary>
public partial class ArmoryOverlay : Control
{
    private const int Columns = 3;
    private const int VisibleRows = 4;   // how much of the collection is on screen before it scrolls

    private Func<Card, Vector2, Control> _cardFactory;
    private Action _onDone;
    private readonly List<int> _deck = new List<int>();   // working copy; committed on Continue
    private Vector2 _cardSize = new Vector2(59, 80);

    private GridContainer _collectionGrid;
    private GridContainer _slotGrid;
    private ScrollContainer _collectionScroll;
    private Label _collectionLabel;
    private Label _countLabel;
    private Button _continueButton;
    private bool _built;

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
        MouseFilter = Control.MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        OverlayUi.AddDim(this);
        VBoxContainer box = OverlayUi.AddPanel(this, contentMargin: 24, separation: 12);

        box.AddChild(OverlayUi.MakeLabel("Armory", 30));
        box.AddChild(OverlayUi.MakeLabel(
            "Tap a card to move it in or out of your side deck.\nFour of your twelve are dealt to you each match.",
            18, OverlayUi.Muted));

        HBoxContainer columns = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        columns.AddThemeConstantOverride("separation", 22);
        box.AddChild(columns);

        // Left: the collection (everything owned that is not in the deck).
        VBoxContainer left = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        left.AddThemeConstantOverride("separation", 6);
        columns.AddChild(left);
        _collectionLabel = OverlayUi.MakeLabel("Collection", 20);
        left.AddChild(_collectionLabel);

        _collectionScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        left.AddChild(_collectionScroll);
        _collectionGrid = new GridContainer { Columns = Columns };
        _collectionGrid.AddThemeConstantOverride("h_separation", 8);
        _collectionGrid.AddThemeConstantOverride("v_separation", 8);
        _collectionScroll.AddChild(_collectionGrid);

        // Right: the twelve slots.
        VBoxContainer right = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        right.AddThemeConstantOverride("separation", 6);
        columns.AddChild(right);
        right.AddChild(OverlayUi.MakeLabel("Side deck", 20));

        _slotGrid = new GridContainer { Columns = Columns };
        _slotGrid.AddThemeConstantOverride("h_separation", 8);
        _slotGrid.AddThemeConstantOverride("v_separation", 8);
        right.AddChild(_slotGrid);

        _countLabel = OverlayUi.MakeLabel("", 22, OverlayUi.MedalGold);
        box.AddChild(_countLabel);

        _continueButton = new Button { Text = "Start the Match" };
        _continueButton.Pressed += Close;
        box.AddChild(_continueButton);
    }

    public void Open(Vector2 cardSize, Action onDone)
    {
        _onDone = onDone;
        _cardSize = cardSize;

        RunData run = RunData.Instance;
        if (!_built || run == null)
        {
            _onDone = null;
            onDone?.Invoke(); // never strand the run because the armory failed to build
            return;
        }

        _deck.Clear();
        _deck.AddRange(run.SideDeck);

        // Four rows of the collection are on screen; the rest scrolls.
        _collectionScroll.CustomMinimumSize = new Vector2(
            Columns * _cardSize.X + (Columns - 1) * 8,
            VisibleRows * _cardSize.Y + (VisibleRows - 1) * 8);

        Node parent = GetParent();
        if (parent != null) parent.MoveChild(this, parent.GetChildCount() - 1);
        Visible = true;
        Refresh();
    }

    private void Close()
    {
        RunData.Instance?.SetSideDeck(_deck);
        Visible = false;
        Action done = _onDone;
        _onDone = null;
        done?.Invoke();
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
            if (deckFull) view.Modulate = new Color(0.45f, 0.45f, 0.5f); // no room until one comes out
            Button button = OverlayUi.CardButton(view, _cardSize, () => AddToDeck(captured));
            button.Disabled = deckFull;
            _collectionGrid.AddChild(button);
        }

        _collectionLabel.Text = spare > 0 ? $"Collection ({spare})" : "Collection (empty)";

        for (int slot = 0; slot < RunData.SideDeckSize; slot++)
        {
            if (slot < _deck.Count)
            {
                int inventoryIndex = _deck[slot];
                if (inventoryIndex < 0 || inventoryIndex >= run.Inventory.Count) continue;

                int captured = inventoryIndex;
                Control view = _cardFactory(run.Inventory[inventoryIndex].ToCard(), _cardSize);
                _slotGrid.AddChild(OverlayUi.CardButton(view, _cardSize, () => RemoveFromDeck(captured)));
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
        _countLabel.AddThemeColorOverride("font_color", ready ? OverlayUi.MedalGold : new Color(0.95f, 0.5f, 0.45f));
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
}
