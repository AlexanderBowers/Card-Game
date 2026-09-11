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
        // Effect cards are one-shot swings rather than arithmetic, so they sit above the whole
        // modifier table. Trade Totals is the most expensive card in the game on purpose: it takes
        // a won round off the other player, and it should cost most of a match's winnings.
        switch (def.Effect)
        {
            // Copy is a bust-saver and nothing else - it never touches the other player, which
            // is what keeps it the cheapest reach in the game.
            case CardEffect.Copy: return 10;
            case CardEffect.Shave: return 10;
            case CardEffect.TradeHands: return 14;
            case CardEffect.TradeTotals: return 18;
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
        return def.IsFlip ? price * 2 : price;
    }

    /// The stock for one visit.
    ///
    /// Slot one is always the SIGNATURE CARD of the stage just cleared (Alexander's rule): you
    /// lose two rounds to a Copy, you clear the stage, and a Copy is waiting on the next screen.
    /// The rest rolls from everything unlocked so far - bigger cards and commoner +/- further up
    /// the ladder, and effects at about a quarter of the stock so the arithmetic deck still grows.
    public static List<RunData.ModifierDef> RollOffers(Random rng, RunData run, int count)
    {
        List<RunData.ModifierDef> offers = new List<RunData.ModifierDef>();
        int stepIndex = run?.StepIndex ?? 0;
        int maxMagnitude = Mathf.Clamp(3 + stepIndex / 3, 3, 6);
        double flipChance = 0.15 + 0.02 * stepIndex;

        List<CardEffect> unlocked = run != null ? run.UnlockedEffects() : new List<CardEffect>();
        if (run != null) offers.Add(SignatureOffer(rng, run, unlocked, maxMagnitude));

        for (int attempt = 0; attempt < count * 12 && offers.Count < count; attempt++)
        {
            RunData.ModifierDef def;

            if (unlocked.Count > 0 && rng.NextDouble() < 0.25)
            {
                def = ToDef(CardEffects.Create(unlocked[rng.Next(unlocked.Count)], rng));
            }
            else
            {
                bool isFlip = rng.NextDouble() < flipChance;
                int magnitude = RollMagnitude(rng, maxMagnitude);

                // A +/- card is stored positive; the player picks its sign at the table.
                int value = (isFlip || rng.Next(2) == 0) ? magnitude : -magnitude;
                def = new RunData.ModifierDef(value, isFlip);
            }

            if (!IsDuplicate(offers, def)) offers.Add(def);
        }

        // A market where nothing at all can be bought is a dead screen. An unaffordable SIGNATURE
        // card is fine - that one is a savings target - so the cheap card replaces the last slot.
        if (run != null && run.Medals >= 4 && offers.Count > 1
            && offers.TrueForAll(o => PriceOf(o) > run.Medals))
        {
            offers[offers.Count - 1] = new RunData.ModifierDef(rng.Next(2) == 0 ? 2 : -2);
        }

        return offers;
    }

    /// The one card every visit guarantees: whatever the stage just cleared was about.
    private static RunData.ModifierDef SignatureOffer(Random rng, RunData run,
                                                      List<CardEffect> unlocked, int maxMagnitude)
    {
        RunData.LadderStep cleared = run.StepAt(run.ClearedStepIndex);
        int stage = run.ClearedStepIndex + 1;

        // Stages 4-8: the card the player has just been hit with, now for sale.
        if (cleared.AiEffect != CardEffect.None && run.EffectUnlocked(cleared.AiEffect))
        {
            return ToDef(CardEffects.Create(cleared.AiEffect, rng));
        }

        // Stage 1 is the plain game; stage 2 is the one that introduces "+/-".
        if (stage <= 1) return new RunData.ModifierDef(RollMagnitude(rng, 3) * (rng.Next(2) == 0 ? 1 : -1));
        if (stage == 2) return new RunData.ModifierDef(RollMagnitude(rng, 3), isFlip: true);

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
        return new RunData.ModifierDef(rng.Next(2) == 0 ? magnitude : -magnitude);
    }

    private static RunData.ModifierDef ToDef(Card card) =>
        new RunData.ModifierDef(card.Value, card.IsFlip, card.Effect);

    /// Two offers are the same card when they would play identically. Effect cards collide on the
    /// effect alone: two Copies in one market is a thin visit.
    private static bool IsDuplicate(List<RunData.ModifierDef> offers, RunData.ModifierDef def)
    {
        if (def.Effect != CardEffect.None) return offers.Exists(o => o.Effect == def.Effect);
        return offers.Exists(o => o.Effect == CardEffect.None && o.Value == def.Value && o.IsFlip == def.IsFlip);
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
            "Cards you buy are yours to keep - a lost run never takes them away.\nYou choose which twelve go in your deck next.",
            16, OverlayUi.Muted));

        _continueButton = new Button { Text = "Continue to your Deck" };
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
        foreach (RunData.ModifierDef def in RollOffers(_random, run, OfferCount))
            _offers.Add(new Offer { Def = def, Price = PriceOf(def) });

        RunData.LadderStep next = run.CurrentStep;
        _subtitle.Text = $"Next: {next.Opponent} - target {next.TargetScore}";

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

            // An effect card is a rule, not a number, and the face only has room for a glyph. The
            // market is where the player decides whether to spend a match's winnings on one, so
            // it is the one screen that has to spell the rule out.
            if (offer.Def.Effect != CardEffect.None)
            {
                column.AddChild(OverlayUi.MakeLabel(CardEffects.Label(offer.Def.Effect), 16));
                Label rule = OverlayUi.MakeLabel(CardEffects.Description(offer.Def.Effect), 13, OverlayUi.Muted);
                rule.CustomMinimumSize = new Vector2(_cardSize.X * 2.0f, 0);
                rule.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                column.AddChild(rule);
            }

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
