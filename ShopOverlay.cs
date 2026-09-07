using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The market between two rungs of the ladder: a handful of modifier cards for sale, priced in the
/// medals won on the table. Bought cards go into RunData.Inventory; the armory (which opens next)
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
        public RunData.ModifierDef Def;
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
    private HBoxContainer _offerRow;
    private Button _continueButton;
    private bool _built;

    // ------------------------------------------------------------------
    // Prices
    //
    // Magnitude-based, with a premium on the +/- cards - being able to choose the sign at the table
    // is worth about twice a fixed card of the same size. A minus card costs the same as the plus
    // of the same magnitude: it is what saves a bust, so it is not the cheap half of the deck.
    //
    // A won match pays roughly 5 medals early and 12 late (rounds taken + the venue purse), so a
    // visit buys about one card - two if the player is saving or the stock is small.
    // ------------------------------------------------------------------
    public static int PriceOf(RunData.ModifierDef def)
    {
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
        return def.IsFlip ? price * 2 : price;
    }

    /// The stock for one visit. Bigger cards appear further up the ladder, and +/- cards get
    /// commoner - progression comes from the player's deck, never from inflating the arithmetic.
    public static List<RunData.ModifierDef> RollOffers(Random rng, int stepIndex, int count)
    {
        List<RunData.ModifierDef> offers = new List<RunData.ModifierDef>();
        int maxMagnitude = Mathf.Clamp(3 + stepIndex / 3, 3, 6);
        double flipChance = 0.15 + 0.02 * stepIndex;

        for (int attempt = 0; attempt < count * 8 && offers.Count < count; attempt++)
        {
            bool isFlip = rng.NextDouble() < flipChance;
            int magnitude = RollMagnitude(rng, maxMagnitude);

            // A +/- card is stored positive; the player picks its sign at the table.
            int value = (isFlip || rng.Next(2) == 0) ? magnitude : -magnitude;
            RunData.ModifierDef def = new RunData.ModifierDef(value, isFlip);

            bool duplicate = offers.Exists(o => o.Value == def.Value && o.IsFlip == def.IsFlip);
            if (!duplicate) offers.Add(def);
        }
        return offers;
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

    private void Build()
    {
        if (_built) return;
        _built = true;

        Visible = false;
        MouseFilter = Control.MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        OverlayUi.AddDim(this);
        VBoxContainer box = OverlayUi.AddPanel(this, contentMargin: 24, separation: 14);

        box.AddChild(OverlayUi.MakeLabel("The Market", 30));
        _subtitle = OverlayUi.MakeLabel("", 20, OverlayUi.Muted);
        box.AddChild(_subtitle);

        _medalLabel = OverlayUi.MakeLabel("", 24, OverlayUi.MedalGold);
        box.AddChild(_medalLabel);

        _offerRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _offerRow.AddThemeConstantOverride("separation", 14);
        box.AddChild(_offerRow);

        box.AddChild(OverlayUi.MakeLabel(
            "Cards you buy are yours for the rest of the run.\nYou choose which twelve go in your side deck next.",
            16, OverlayUi.Muted));

        _continueButton = new Button { Text = "Continue to the Armory" };
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
            onDone?.Invoke(); // never strand the run because the shop failed to build
            return;
        }

        _offers.Clear();
        foreach (RunData.ModifierDef def in RollOffers(_random, run.StepIndex, OfferCount))
            _offers.Add(new Offer { Def = def, Price = PriceOf(def) });

        RunData.LadderStep next = run.CurrentStep;
        _subtitle.Text = $"Next: {next.Opponent} at the {next.Venue} - target {next.TargetScore}";

        // Above the round-end overlay and any stray animation card.
        Node parent = GetParent();
        if (parent != null) parent.MoveChild(this, parent.GetChildCount() - 1);
        Visible = true;
        Refresh();
    }

    private void Close()
    {
        Visible = false;
        Action done = _onDone;
        _onDone = null;
        done?.Invoke();
    }

    // ------------------------------------------------------------------
    // The stock
    // ------------------------------------------------------------------
    private void Refresh()
    {
        RunData run = RunData.Instance;
        if (run == null) return;

        _medalLabel.Text = $"Medals: {run.Medals}";
        OverlayUi.ClearChildren(_offerRow);

        foreach (Offer offer in _offers)
        {
            VBoxContainer column = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            column.AddThemeConstantOverride("separation", 6);
            _offerRow.AddChild(column);

            Control view = _cardFactory(offer.Def.ToCard(), _cardSize);
            if (offer.Sold) view.Modulate = new Color(0.45f, 0.45f, 0.5f);
            column.AddChild(OverlayUi.CardButton(view, _cardSize, null));

            column.AddChild(OverlayUi.MakeLabel(
                offer.Sold ? "bought" : $"{offer.Price} medals",
                16, offer.Sold ? OverlayUi.Muted : OverlayUi.MedalGold));

            Offer captured = offer;
            Button buy = new Button
            {
                Text = offer.Sold ? "Bought" : "Buy",
                Disabled = offer.Sold || run.Medals < offer.Price,
            };
            buy.Pressed += () => Buy(captured);
            column.AddChild(buy);
        }
    }

    private void Buy(Offer offer)
    {
        RunData run = RunData.Instance;
        if (run == null || offer.Sold || run.Medals < offer.Price) return;

        run.SpendMedals(offer.Price);
        run.AddToInventory(offer.Def);
        offer.Sold = true;
        Refresh();
    }
}
