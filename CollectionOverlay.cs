using Godot;
using System;

/// <summary>
/// The collection log: every Modifier the game has, and which of them this player has met.
/// Opened from the start menu (playtest-feedback-family.md §5.2).
///
/// Four rows of six, and the rows ARE the categories - plus, minus, flip value, special - so the
/// gaps in a row say what is missing without a single word. A card is "met" when it enters your
/// collection, is dealt to you, or is played at you by the bot: this is a record of what you have
/// seen the game do, not of what you happen to own.
///
/// Reads RunData.CardsMet and never writes it. The one thing this screen changes is the reward
/// toggle, which is cosmetic by rule: a family game must not gate strength behind completionism.
/// </summary>
public partial class CollectionOverlay : Control
{
    private const int Columns = 6;
    private const int Gap = 6;

    private Func<Card, Vector2, Control> _cardFactory;
    private Action _onClosed;
    private Vector2 _cardSize = new Vector2(48, 65);

    private GridContainer _grid;
    private Label _countLabel;
    private Label _rewardLabel;
    private CheckButton _rewardToggle;
    private bool _built;

    public void Setup(Func<Card, Vector2, Control> cardFactory)
    {
        _cardFactory = cardFactory;
        if (_built) return;
        _built = true;

        Visible = false;
        MouseFilter = Control.MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        OverlayUi.AddDim(this);
        VBoxContainer box = OverlayUi.AddPanel(this, contentMargin: 22, separation: 10);

        box.AddChild(OverlayUi.MakeLabel("Collection", 30));
        box.AddChild(OverlayUi.MakeLabel(
            "Every Modifier you have held, bought, or had played against you.", 15, OverlayUi.Muted));

        _grid = new GridContainer { Columns = Columns };
        _grid.AddThemeConstantOverride("h_separation", Gap);
        _grid.AddThemeConstantOverride("v_separation", Gap);
        CenterContainer center = new CenterContainer();
        center.AddChild(_grid);
        box.AddChild(center);

        _countLabel = OverlayUi.MakeLabel("", 22, OverlayUi.MedalGold);
        box.AddChild(_countLabel);

        _rewardLabel = OverlayUi.MakeLabel("", 14, OverlayUi.Muted);
        box.AddChild(_rewardLabel);

        _rewardToggle = new CheckButton { Text = "Gilded deck back", FocusMode = Control.FocusModeEnum.None };
        _rewardToggle.Toggled += on => RunData.Instance?.SetCollectorBack(on);
        CenterContainer toggleRow = new CenterContainer();
        toggleRow.AddChild(_rewardToggle);
        box.AddChild(toggleRow);

        Button close = new Button { Text = "Close", CustomMinimumSize = new Vector2(200, 44) };
        close.Pressed += Close;
        CenterContainer closeRow = new CenterContainer();
        closeRow.AddChild(close);
        box.AddChild(closeRow);
    }

    /// cardSize is the caller's to choose: six across has to fit a phone held upright.
    public void Open(Vector2 cardSize, Action onClosed = null)
    {
        _onClosed = onClosed;
        _cardSize = cardSize;
        if (!_built || RunData.Instance == null) { Close(); return; }

        Node parent = GetParent();
        if (parent != null) parent.MoveChild(this, parent.GetChildCount() - 1);
        Visible = true;
        Refresh();
    }

    private void Close()
    {
        Visible = false;
        Action done = _onClosed;
        _onClosed = null;
        done?.Invoke();
    }

    private void Refresh()
    {
        RunData run = RunData.Instance;
        OverlayUi.ClearChildren(_grid);

        foreach (string key in RunData.CollectionKeys)
        {
            if (run.HasMetCard(key))
            {
                Control view = _cardFactory(RunData.CollectionEntry(key).ToCard(), _cardSize);
                _grid.AddChild(OverlayUi.CardButton(view, _cardSize, null));
            }
            else
            {
                _grid.AddChild(UnknownSlot());
            }
        }

        int found = run.CollectionFound;
        int total = RunData.CollectionKeys.Length;
        _countLabel.Text = $"{found} / {total} found";

        bool complete = run.CollectionComplete;
        _rewardLabel.Text = complete
            ? "Complete. Your deck has earned a gilded back."
            : $"Find all {total} to earn a gilded back for your deck.";
        _rewardToggle.Visible = complete;
        _rewardToggle.SetPressedNoSignal(run.CollectorBack);
    }

    /// An outline with a question mark: the shape of a card you have not met yet.
    private Control UnknownSlot()
    {
        Panel slot = OverlayUi.EmptySlot(_cardSize);
        Label mark = OverlayUi.MakeLabel("?", Mathf.RoundToInt(_cardSize.Y * 0.4f), new Color(1f, 1f, 1f, 0.3f));
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.MouseFilter = Control.MouseFilterEnum.Ignore;
        slot.AddChild(mark);
        mark.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return slot;
    }
}
