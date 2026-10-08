using Godot;

/// Tap your deck to see the odds of your next draw (2026-10-08): staying under the target, hitting
/// it, going over - and what is left in the deck to make them. A first version to tune by
/// playtest; the arithmetic is DeckOdds.
///
/// The deck has no button of its own (it is a picture, and in the 3D table it is a piece on the
/// felt), so the tap is caught as UNHANDLED input: anything with a real control under the finger -
/// a button, a card in the hand, any overlay - has already taken it by the time it gets here.
public partial class GameManager
{
    /// A finger is a blunt pointer and the deck is a small target: this much slack all round.
    private const float DeckTapSlack = 14f;

    private Control _deckOdds;

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
        if (!_isGameStarted || _gameState == null || _gameState.IsGameOver || _setOverPending) return;
        if (_prompts.Showing || _teaching.Running || _ui?.DeckView == null) return;
        if (!DeckContains(_ui.DeckView, click.Position)) return;

        int[] counts = _online ? _onlineDeckCounts : _table?.DeckCounts(_player1);
        if (counts == null) return; // online against a server too old to say

        GetViewport().SetInputAsHandled();
        ShowDeckOdds(counts);
    }

    /// Where the deck is on screen: the 3D piece if the table is drawn in 3D, else the flat card.
    private static bool DeckContains(Control deck, Vector2 point)
    {
        if (TableWorld3D.Instance != null && TableWorld3D.Instance.TryScreenRect(deck, out Rect2 seen))
            return seen.Grow(DeckTapSlack).HasPoint(point);
        if (!deck.IsVisibleInTree()) return false;
        Rect2 flat = deck.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, deck.Size);
        return flat.Grow(DeckTapSlack).HasPoint(point);
    }

    private void ShowDeckOdds(int[] counts)
    {
        if (_deckOdds != null && GodotObject.IsInstanceValid(_deckOdds)) _deckOdds.QueueFree();

        // Any tap closes it - it is a glance, not a screen.
        Control overlay = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        OverlayUi.Host(this).AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        OverlayUi.AddDim(overlay);
        overlay.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true }) overlay.QueueFree();
        };
        _deckOdds = overlay;

        VBoxContainer box = OverlayUi.AddPanel(overlay, separation: 10, scroll: false);
        // A tap on the panel closes it too: the panel lets taps through to the overlay.
        box.MouseFilter = Control.MouseFilterEnum.Ignore;
        if (box.GetParent() is Control panel) panel.MouseFilter = Control.MouseFilterEnum.Ignore;

        int target = _gameState.TargetScore;
        int score = _player1.CurrentScore;
        DeckOdds.Odds odds = DeckOdds.NextDraw(counts, score, target);

        box.AddChild(OverlayUi.MakeLabel("Your deck", 30));
        box.AddChild(OverlayUi.MakeLabel($"{odds.Cards} cards left", 16, OverlayUi.Muted));
        box.AddChild(CountsGrid(counts));

        if (_player1.IsHolding)
        {
            box.AddChild(OverlayUi.MakeLabel("You are holding:\nno more draws this set.", 18, OverlayUi.Muted));
        }
        else if (odds.Cards > 0)
        {
            int[] pct = DeckOdds.Percentages(odds);
            box.AddChild(OverlayUi.MakeLabel($"Your next draw, on {score}:", 16, OverlayUi.Muted));
            box.AddChild(OddsLine($"Stay under {target}", pct[0], OverlayUi.Ink));
            box.AddChild(OddsLine($"Hit {target}", pct[1], OverlayUi.MedalGold));
            box.AddChild(OddsLine($"Go over {target}", pct[2], OverlayUi.Warning));
        }

        box.AddChild(OverlayUi.MakeLabel("Tap anywhere to close", 13, OverlayUi.Muted));
        OverlayUi.BringToFront(overlay);
    }

    /// Two rows - the values, and how many of each are left - so the odds can be checked by eye.
    private static Control CountsGrid(int[] counts)
    {
        GridContainer grid = new GridContainer { Columns = 10, MouseFilter = Control.MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 2);
        for (int v = 1; v <= 10; v++) grid.AddChild(OverlayUi.MakeLabel(v.ToString(), 14, OverlayUi.Muted));
        for (int v = 1; v <= 10; v++)
            grid.AddChild(OverlayUi.MakeLabel(counts[v - 1].ToString(), 18,
                counts[v - 1] == 0 ? OverlayUi.Muted : OverlayUi.Ink));

        CenterContainer centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.AddChild(grid);
        return centre;
    }

    private static Control OddsLine(string what, int percent, Color colour)
    {
        HBoxContainer row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 24);
        Label name = OverlayUi.MakeLabel(what, 20, colour);
        name.HorizontalAlignment = HorizontalAlignment.Left;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Label value = OverlayUi.MakeLabel($"{percent}%", 22, colour);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.CustomMinimumSize = new Vector2(70, 0);
        row.AddChild(name);
        row.AddChild(value);
        return row;
    }
}
