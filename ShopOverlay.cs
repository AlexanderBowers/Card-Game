using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The market between two rungs of the ladder: a handful of modifier cards for sale, priced in the
/// medals won on the table. Bought cards go into RunData.Inventory; the deck screen (which opens next)
/// decides which twelve of them make the side deck.
///
/// An overlay over the table rather than a scene of its own, so the intermission never breaks the
/// one grounded venue look.
/// </summary>
public partial class ShopOverlay : Control
{
    private const int OfferCount = 4;

    private sealed class Offer
    {
        public ModifierDef Def;
        public int Price;
        public bool Sold;
    }

    private Func<Card, Vector2, Control> _cardFactory;
    private Action _onDone;
    private readonly Random _random = new Random();
    private readonly List<Offer> _offers = new List<Offer>();
    private Vector2 _cardSize = new Vector2(84, 114);

    private Label _subtitle;
    private Label _medalLabel;
    private Button _continueButton;
    private bool _built;

    // ------------------------------------------------------------------
    // Prices
    //
    // Magnitude-based, with a premium on the +/- cards - being able to choose the sign at the table
    // is worth about twice a fixed card of the same size. A minus card costs the same as the plus
    // of the same magnitude: it is what saves a bust, so it is not the cheap half of the deck.
    //
    // A won match pays roughly 5 medals early and 12 late (sets taken + the venue purse), so a
    // visit buys about one card - two if the player is saving or the stock is small.
    // ------------------------------------------------------------------
    public static int PriceOf(ModifierDef def)
    {
        // Effect cards are one-shot swings rather than arithmetic, so they sit above the whole
        // modifier table. Trade Totals is the most expensive card in the game on purpose: it takes
        // a won set off the other player, and it should cost most of a match's winnings.
        switch (def.Effect)
        {
            // Copy is a bust-saver and nothing else - it never touches the other player, which
            // is what keeps it the cheapest reach in the game.
            case CardEffect.Copy: return 10;
            case CardEffect.Shave: return 10;
            case CardEffect.TradeHands: return 14;
            case CardEffect.TradeTotals: return 18;
            // Recall is the only effect in the game that is never a dead card: every other one
            // needs something true of the table, this one needs one card in your own spent pile,
            // which is true from the second deal of a match onward. A card that always works
            // should not be the cheapest thing in the shop, whatever its ceiling.
            case CardEffect.Recall: return 12;
            // The only card that takes something away permanently. At best of five with a
            // match-long four-card hand, destroying one is a quarter of the opponent's match.
            case CardEffect.Veto: return 16;
        }

        int magnitude = Math.Abs(def.Value);
        int price;
        switch (magnitude)
        {
            case 1: price = 3; break;
            case 2: price = 4; break;
            case 3: price = 6; break;
            case 4: price = 8; break;
            case 5: price = 11; break;
            default: price = 14; break;
        }
        return def.CanFlipValue ? price * 2 : price;
    }

    /// The stock for one visit.
    ///
    /// Slot one is always the SIGNATURE CARD of the stage just cleared (Alexander's rule): you
    /// lose two sets to a Copy, you clear the stage, and a Copy is waiting on the next screen.
    /// The rest rolls from everything unlocked so far - bigger cards and commoner +/- further up
    /// the ladder, and effects at about a quarter of the stock so the arithmetic deck still grows.
    public static List<ModifierDef> RollOffers(Random rng, RunData run, int count)
    {
        List<ModifierDef> offers = new List<ModifierDef>();
        int stepIndex = run?.StepIndex ?? 0;
        int maxMagnitude = Mathf.Clamp(3 + stepIndex / 3, 3, 6);
        double flipValueChance = 0.15 + 0.02 * stepIndex;

        List<CardEffect> unlocked = run != null ? run.UnlockedEffects() : new List<CardEffect>();
        if (run != null) offers.Add(SignatureOffer(rng, run, unlocked, maxMagnitude));

        for (int attempt = 0; attempt < count * 12 && offers.Count < count; attempt++)
        {
            ModifierDef def;

            if (unlocked.Count > 0 && rng.NextDouble() < 0.25)
            {
                def = ToDef(CardEffects.Create(unlocked[rng.Next(unlocked.Count)], rng));
            }
            else
            {
                bool canFlipValue = rng.NextDouble() < flipValueChance;
                int magnitude = RollMagnitude(rng, maxMagnitude);

                // A +/- card is stored positive; the player picks its sign at the table.
                int value = (canFlipValue || rng.Next(2) == 0) ? magnitude : -magnitude;
                def = new ModifierDef(value, canFlipValue);
            }

            if (!IsDuplicate(offers, def)) offers.Add(def);
        }

        // A market where nothing at all can be bought is a dead screen. An unaffordable SIGNATURE
        // card is fine - that one is a savings target - so the cheap card replaces the last slot.
        if (run != null && run.Medals >= 4 && offers.Count > 1
            && offers.TrueForAll(o => PriceOf(o) > run.Medals))
        {
            offers[offers.Count - 1] = new ModifierDef(rng.Next(2) == 0 ? 2 : -2);
        }

        return offers;
    }

    /// The one card every visit guarantees: whatever the stage just cleared was about.
    private static ModifierDef SignatureOffer(Random rng, RunData run,
                                                      List<CardEffect> unlocked, int maxMagnitude)
    {
        // Endless parks on the last rung, so "the stage just cleared" would be Veto every visit.
        // Every special Modifier is in play there; any of them is the right signature.
        if (run.Endless && unlocked.Count > 0)
            return ToDef(CardEffects.Create(unlocked[rng.Next(unlocked.Count)], rng));

        LadderStep cleared = Ladder.At(run.ClearedStepIndex);
        int stage = run.ClearedStepIndex + 1;

        // Stages 4-8: the card the player has just been hit with, now for sale.
        if (cleared.AiEffect != CardEffect.None && run.EffectUnlocked(cleared.AiEffect))
        {
            return ToDef(CardEffects.Create(cleared.AiEffect, rng));
        }

        // Stage 1 is the plain game; stage 2 is the one that introduces "+/-".
        if (stage <= 1) return new ModifierDef(RollMagnitude(rng, 3) * (rng.Next(2) == 0 ? 1 : -1));
        if (stage == 2) return new ModifierDef(RollMagnitude(rng, 3), canFlipValue: true);

        // The cleared stage WAS about an effect card, but not one that is built yet - stages 5, 6
        // and 8 name a Trade that pass 3 has still to wire - or it is a randomized rung that names
        // none until pass 4. Either way the player has just cleared a rung well up the ladder, and
        // the first build handed them a plain number for it: Alexander cleared stage 8 and the
        // Obsidian market opened with arithmetic. Offer another unlocked effect instead, and only
        // fall back to a big plain card when the player owns no effect stage yet.
        if (stage >= 4 && unlocked.Count > 0)
        {
            return ToDef(CardEffects.Create(unlocked[rng.Next(unlocked.Count)], rng));
        }

        // Stage 3 is the one that moves the target and introduces no card of its own. A moved
        // target is exactly when a big swing earns its price.

        int magnitude = Math.Max(4, RollMagnitude(rng, Math.Max(4, maxMagnitude)));
        return new ModifierDef(rng.Next(2) == 0 ? magnitude : -magnitude);
    }

    private static ModifierDef ToDef(Card card) =>
        new ModifierDef(card.Value, card.CanFlipValue, card.Effect);

    /// Two offers are the same card when they would play identically. Effect cards collide on the
    /// effect alone: two Copies in one market is a thin visit.
    private static bool IsDuplicate(List<ModifierDef> offers, ModifierDef def)
    {
        if (def.Effect != CardEffect.None) return offers.Exists(o => o.Effect == def.Effect);
        return offers.Exists(o => o.Effect == CardEffect.None && o.Value == def.Value && o.CanFlipValue == def.CanFlipValue);
    }

    private static int RollMagnitude(Random rng, int max)
    {
        // Small cards are the common case; the big ones are the reason to save medals.
        int roll = rng.Next(100);
        int magnitude;
        if (roll < 35) magnitude = 1;
        else if (roll < 65) magnitude = 2;
        else if (roll < 85) magnitude = 3;
        else if (roll < 95) magnitude = 4;
        else if (roll < 99) magnitude = 5;
        else magnitude = 6;
        return Math.Min(magnitude, max);
    }

    // ------------------------------------------------------------------
    // Building and opening
    // ------------------------------------------------------------------
    public void Setup(Func<Card, Vector2, Control> cardFactory)
    {
        _cardFactory = cardFactory;
        Build();
    }

    // ------------------------------------------------------------------
    // The screen (2026-10-05: full screen - "it's far too small"). Built like How to Play and the
    // deck screen: a backdrop and a panel anchored to all four edges, clear of the notch and the
    // gesture bar, and the cards sized to the room it has. Four offers sit 2 x 2 in portrait and
    // in one row in landscape.
    // ------------------------------------------------------------------
    private PanelContainer _panel;
    private Control _top;
    private Control _bottom;
    private GridContainer _offerGrid;
    private int _columns = 2;
    private float _cellWidth = 200f;
    private Vector2 _laidOutFor;

    private const int PanelPad = 20;
    private const int BoxGap = 12;
    private const int CellGap = 18;
    private const float BelowCardFixed = 24f + 52f + 3 * 6f; // price + Buy + gaps
    private const float EffectTextRoom = 22f + 56f + 2 * 6f;  // name + up to three lines of rule

    private void Build()
    {
        if (_built) return;
        _built = true;

        Visible = false;
        MouseFilter = Control.MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        ColorRect dim = new ColorRect { Color = OverlayUi.DimColor, MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        OverlayUi.StylePanel(_panel, PanelPad);
        AddChild(_panel);
        OverlayUi.FillScreen(_panel);

        VBoxContainer box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", BoxGap);
        _panel.AddChild(box);

        VBoxContainer top = new VBoxContainer();
        top.AddThemeConstantOverride("separation", 6);
        box.AddChild(top);
        _top = top;
        top.AddChild(OverlayUi.MakeLabel("The Market", 34));
        _subtitle = OverlayUi.MakeLabel("", 20, OverlayUi.Muted);
        top.AddChild(_subtitle);
        _medalLabel = OverlayUi.MakeLabel("", 26, OverlayUi.MedalGold);
        top.AddChild(_medalLabel);

        // The offers fill everything between the header and the button, centred in it.
        CenterContainer middle = new CenterContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        box.AddChild(middle);
        _offerGrid = new GridContainer { Columns = 2 };
        _offerGrid.AddThemeConstantOverride("h_separation", CellGap);
        _offerGrid.AddThemeConstantOverride("v_separation", CellGap);
        middle.AddChild(_offerGrid);

        VBoxContainer bottom = new VBoxContainer();
        bottom.AddThemeConstantOverride("separation", 10);
        box.AddChild(bottom);
        _bottom = bottom;
        Label note = OverlayUi.MakeLabel(
            "Modifiers you buy are yours to keep - a lost run never takes them away.\nYou choose which twelve go in your deck next.",
            17, OverlayUi.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        bottom.AddChild(note);

        _continueButton = new Button { Text = "Continue to your Deck", CustomMinimumSize = new Vector2(300, 52) };
        OverlayUi.StyleButton(_continueButton, primary: true);
        _continueButton.Pressed += Close;
        CenterContainer buttonRow = new CenterContainer();
        buttonRow.AddChild(_continueButton);
        bottom.AddChild(buttonRow);
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
        if (_panel == null) return;
        OverlayUi.FillScreen(_panel);
        if (Visible) CallDeferred(nameof(LayoutForSize));
    }

    public void Open(Vector2 cardSize, Action onDone)
    {
        _onDone = onDone;
        _cardSize = cardSize;

        RunData run = RunData.Instance;
        if (!_built || run == null)
        {
            _onDone = null;
            onDone?.Invoke(); // never strand the run because the shop failed to build
            return;
        }

        _offers.Clear();
        foreach (ModifierDef def in RollOffers(_random, run, OfferCount))
            _offers.Add(new Offer { Def = def, Price = PriceOf(def) });

        _subtitle.Text = $"Next: {run.CurrentOpponent} - target {run.CurrentTarget}";

        // Above the set-end overlay and any stray animation card.
        Node parent = GetParent();
        if (parent != null) parent.MoveChild(this, parent.GetChildCount() - 1);
        OverlayUi.FillScreen(_panel);
        Visible = true;
        _laidOutFor = Vector2.Zero;
        LayoutForSize();
        CallDeferred(nameof(LayoutForSize)); // again once the header and footer have measured
    }

    private void Close()
    {
        Visible = false;
        Action done = _onDone;
        _onDone = null;
        done?.Invoke();
    }

    /// Card size from the room the panel has (worked out from the screen, never read back off the
    /// grid, which is as big as the cards it was last given).
    private void LayoutForSize()
    {
        if (!Visible || _panel == null) return;

        Vector2 view = GetViewportRect().Size;
        float panelW = view.X - _panel.OffsetLeft + _panel.OffsetRight;
        float panelH = view.Y - _panel.OffsetTop + _panel.OffsetBottom;
        float chrome = _top.GetCombinedMinimumSize().Y + _bottom.GetCombinedMinimumSize().Y + 2 * BoxGap;
        Vector2 room = new Vector2(panelW - 2 * PanelPad, panelH - 2 * PanelPad - chrome);
        if (room.X < 10 || room.Y < 10) return;
        if ((room - _laidOutFor).Length() < 2f) return;
        _laidOutFor = room;

        bool portrait = view.Y > view.X;
        _columns = portrait ? 2 : Math.Max(1, _offers.Count);
        int rows = (_offers.Count + _columns - 1) / _columns;

        bool anyEffect = _offers.Exists(o => o.Def.Effect != CardEffect.None);
        float below = BelowCardFixed + (anyEffect ? EffectTextRoom : 0f);
        float aspect = TableUi.BaseCardSize.Y / TableUi.BaseCardSize.X;

        _cellWidth = (room.X - (_columns - 1) * CellGap) / _columns;
        float byWidth = _cellWidth * (portrait ? 0.8f : 0.7f);
        float byHeight = ((room.Y - (rows - 1) * CellGap) / rows - below) / aspect;
        float width = Mathf.Clamp(Mathf.Min(byWidth, byHeight), 60f, 260f);
        _cardSize = new Vector2(Mathf.Floor(width), Mathf.Floor(width * aspect));
        _offerGrid.Columns = _columns;

        Refresh();
    }

    // ------------------------------------------------------------------
    // The stock
    // ------------------------------------------------------------------
    private void Refresh()
    {
        RunData run = RunData.Instance;
        if (run == null) return;

        _medalLabel.Text = $"Medals: {run.Medals}";
        OverlayUi.ClearChildren(_offerGrid);

        float textWidth = Mathf.Max(_cardSize.X * 1.2f, _cellWidth - 12f);
        foreach (Offer offer in _offers)
        {
            VBoxContainer column = new VBoxContainer
            {
                Alignment = BoxContainer.AlignmentMode.Begin,
                CustomMinimumSize = new Vector2(textWidth, 0),
            };
            column.AddThemeConstantOverride("separation", 6);
            _offerGrid.AddChild(column);

            Control view = _cardFactory(offer.Def.ToCard(), _cardSize);
            if (offer.Sold) view.Modulate = new Color(1f, 1f, 1f, 0.4f);
            Button card = OverlayUi.CardButton(view, _cardSize, null);
            card.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            column.AddChild(card);

            column.AddChild(OverlayUi.MakeLabel(
                offer.Sold ? "bought" : $"{offer.Price} medals",
                20, offer.Sold ? OverlayUi.Muted : OverlayUi.MedalGold));

            // An effect card is a rule, not a number, and the face only has room for a glyph. The
            // market is where the player decides whether to spend a match's winnings on one, so
            // it is the one screen that has to spell the rule out.
            if (offer.Def.Effect != CardEffect.None)
            {
                column.AddChild(OverlayUi.MakeLabel(CardEffects.Label(offer.Def.Effect), 19));
                Label rule = OverlayUi.MakeLabel(CardEffects.Description(offer.Def.Effect), 16, OverlayUi.Muted);
                rule.CustomMinimumSize = new Vector2(textWidth, 0);
                rule.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                column.AddChild(rule);
            }

            Offer captured = offer;
            Button buy = new Button
            {
                Text = offer.Sold ? "Bought" : "Buy",
                Disabled = offer.Sold || run.Medals < offer.Price,
                CustomMinimumSize = new Vector2(Mathf.Min(textWidth, 180f), 48),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            OverlayUi.StyleButton(buy, primary: !offer.Sold && run.Medals >= offer.Price);
            buy.Pressed += () => Buy(captured);
            column.AddChild(buy);
        }
    }

    private void Buy(Offer offer)
    {
        RunData run = RunData.Instance;
        if (run == null || offer.Sold || run.Medals < offer.Price) return;

        run.SpendMedals(offer.Price);
        bool wasComplete = run.CollectionComplete;
        run.AddToInventory(offer.Def);
        // The collection log fills by owning now, so the Market is where it completes.
        if (!wasComplete && run.CollectionComplete) _subtitle.Text = "Collection complete! Your deck now has a gilded back.";
        offer.Sold = true;
        Refresh();
    }
}
