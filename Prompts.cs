using Godot;
using System;
using System.Collections.Generic;

/// What the prompts need from the game they interrupt.
public interface IPromptsHost
{
    Player Player1 { get; }
    Player Player2 { get; }
    GameState State { get; }
    Random Rng { get; }
    bool VsBot { get; }
    bool InRun { get; }

    /// No offer is made over a lesson: the walkthrough is a staged match and a rescue there would
    /// teach a rule that is not being taught.
    bool TutorialRunning { get; }

    /// Recall's chosen card, played through the one path every effect card goes through.
    bool PlayEffectCard(Player owner, Card card, Card chosen = null);

    /// Put the Recall card back in the player's hand when the play was refused, so it is never
    /// silently eaten.
    void SetSelection(Player player, Card card);
}

/// The three overlays that STOP the game and wait for an answer: the set-end explanation, Recall's
/// "which card comes back", and the bust rescue offer.
///
/// What makes them one class rather than three is the shape they share. Each one takes the screen,
/// holds it while the table underneath is frozen, and hands back a single decision - which is also
/// why the tutorial and the coach marks ask `Showing` before putting a lesson up.
///
/// They differ from Menus in that a menu is entered; a prompt arrives.
public sealed class Prompts
{
    private readonly IPromptsHost _host;

    /// The scene node the overlays hang on.
    private readonly Node _root;

    /// The table's picture: prompts draw card faces in it, read whether the board is mirrored
    /// (both Recall and the set-end panel show a second, upside-down copy for the player sitting
    /// opposite), and ask for a repaint once an answer is in.
    private readonly TableUi _ui;

    public Prompts(IPromptsHost host, Node root, TableUi ui)
    {
        _host = host;
        _root = root;
        _ui = ui;
    }

    private Player P1 => _host.Player1;
    private Player P2 => _host.Player2;
    private GameState State => _host.State;

    /// True while a Recall choice or a rescue offer is waiting for its answer.
    ///
    /// Not the set-end panel: that one is tracked by the game as _setOverPending, because it stops
    /// the MATCH rather than just the screen, and every caller already checks it separately.
    public bool Showing => RescueShowing || (_recallOverlay != null && _recallOverlay.Visible);

    /// A call put off to the end of the frame, and dropped if the table has been freed in between.
    private void Defer(Action action) =>
        Callable.From(() => { if (GodotObject.IsInstanceValid(_root)) action(); }).CallDeferred();

    // ------------------------------------------------------------------
    // The rescue offer (claude/monetization-spec.md §3)
    //
    // Solo run, ladder stage 4+ or endless. A turn that ends with the player bust - and the bot
    // not, since both over is a tie that is replayed anyway - rolls 15%. On a hit the set waits:
    //
    //   free player   Watch an ad -> a one-off card that puts them on exactly target - 1.
    //                 No thanks   -> a copy of a random card from their 12-card deck (may not help).
    //                 Closing the ad early counts as No thanks; no ad to show offers No thanks only.
    //   No Ads owner  One "Rescue" button -> the exact card. Same 15%, no ad.
    //   (Steam / desktop release builds have no ads, so they get the No Ads owner's version.)
    //
    // Whatever card is given goes into the hand and the turn re-opens: the player still has to
    // play it, and the bust is judged again when the turn ends. At most one rescue per match -
    // the flag is spent the moment the offer appears, whichever way the player answers.
    // ------------------------------------------------------------------
    private Control _rescueOverlay;

    private VBoxContainer _rescueBox;

    /// True from the moment an offer appears until its card is handed over (the ad included), so
    /// nothing on the table can be pressed underneath it.
    private bool _rescuePending;

    /// Debug row: roll 100% instead of 15%, so the offer can be tested without busting for an hour.
    public static bool DebugAlwaysRescue;

    private bool RescueShowing => _rescuePending;

    /// True if the offer is now up and the set must wait for it.
    public bool OfferRescue()
    {
        RunData run = _host.InRun ? RunData.Instance : null;
        if (run == null || !_host.VsBot || _host.TutorialRunning || !run.RescueEligible) return false;

        int target = State.TargetScore;
        // Only a bust that LOSES the set. Both over is a tie and is replayed anyway.
        if (P1.CurrentScore <= target || P2.CurrentScore > target) return false;

        double chance = DebugAlwaysRescue ? 1.0 : RunData.RescueChance;
        if (_host.Rng.NextDouble() >= chance)
        {
            GD.Print($"Rescue roll missed ({chance:P0}).");
            return false;
        }

        run.UseMatchRescue(); // spent now: an offer is the match's one rescue, whatever the answer
        _rescuePending = true;

        if (_rescueOverlay == null)
        {
            _rescueOverlay = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
            _root.AddChild(_rescueOverlay);
            _rescueOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            OverlayUi.AddDim(_rescueOverlay);
            _rescueBox = OverlayUi.AddPanel(_rescueOverlay);
        }
        OverlayUi.ClearChildren(_rescueBox);

        int landing = target - 1;
        bool guaranteed = !AdService.AdsActive; // bought No Ads, or a platform with no ads

        if (guaranteed)
        {
            _rescueBox.AddChild(OverlayUi.MakeLabel("Bust!  Rescue!", 30, OverlayUi.MedalGold));
            _rescueBox.AddChild(OverlayUi.MakeLabel(
                $"Take a card that puts you on {landing}.\nPlay it before you end your turn.",
                16, OverlayUi.Muted));
            AddRescueButton("Rescue", 48, GiveExactRescue);
        }
        else
        {
            bool adReady = AdService.RewardedReady;
            _rescueBox.AddChild(OverlayUi.MakeLabel("Bust!  Try again?", 30, OverlayUi.MedalGold));
            _rescueBox.AddChild(OverlayUi.MakeLabel(
                adReady
                    ? $"Watch a short ad for a card that puts you on {landing}.\n"
                      + "Or take a random card from your deck - it might not help."
                    : "Take a random card from your deck.\nIt might be enough. It might not.",
                16, OverlayUi.Muted));

            if (adReady) AddRescueButton($"Watch ad - land on {landing}", 48, WatchRescueAd);
            AddRescueButton("No thanks - random card", adReady ? 40 : 48, GiveRandomRescue);
        }

        _root.MoveChild(_rescueOverlay, _root.GetChildCount() - 1);
        _rescueOverlay.Visible = true;
        _ui.Refresh();
        return true;
    }

    private void AddRescueButton(string text, int height, Action onPressed)
    {
        Button button = new Button { Text = text, CustomMinimumSize = new Vector2(300, height) };
        button.Pressed += onPressed;
        _rescueBox.AddChild(button);
    }

    private void WatchRescueAd()
    {
        _rescueOverlay.Visible = false; // the ad covers the table; the lock stays on
        AdService.ShowRewarded(_root, result =>
        {
            GD.Print($"Rescue ad: {result}");
            if (result == AdService.RewardResult.Completed) GiveExactRescue();
            else GiveRandomRescue(); // closed early, or nothing to show: that is "No thanks"
        });
    }

    /// The card that lands the player on target - 1. Its value can be as low as -11 (a bust
    /// overshoots by up to 10), which is below any card the game sells - hence its own card rather
    /// than a lookup into the ordinary modifiers.
    private void GiveExactRescue()
    {
        int value = (State.TargetScore - 1) - P1.CurrentScore;
        Card rescue = new Card(value, CardType.Modifier) { IsRescue = true };
        GiveRescueCard(rescue, $"Rescue: play your {rescue.DisplayText} to land on {State.TargetScore - 1}.");
    }

    private void GiveRandomRescue()
    {
        Card copy = RunData.Instance?.DrawRescueCopy()
                    ?? new Card(-_host.Rng.Next(1, 7), CardType.Modifier); // an empty deck; a live run never has one
        copy.IsRescue = true;
        GiveRescueCard(copy, $"Rescue: you take a {copy.DisplayText}. It might be enough.");
    }

    private void GiveRescueCard(Card card, string banner)
    {
        if (_rescueOverlay != null) _rescueOverlay.Visible = false;
        _rescuePending = false;

        // Into the hand - as a 5th card if the hand is full; it never replaces one the player chose.
        // Deliberately NOT NoteModifierMet: a rescue card is not part of the collection.
        P1.Modifiers.Add(card);

        // Re-open the turn exactly as an effect card does: no new draw, just a chance to play.
        P1.IsHolding = false;
        P1.HasEndedTurn = false;
        _ui.ShowEffectBanner(banner);
        _ui.Refresh();
    }

    // ------------------------------------------------------------------
    // Recall: choosing which spent card comes back
    //
    // Built in code from OverlayUi's pieces, like every other overlay here, so both the solo and
    // the 2-player table get it with no NodePath wiring.
    //
    // It asks rather than picking for you. Always returning the most recently spent card would
    // need no screen at all, and it would turn the interesting decision - spend a +4 early KNOWING
    // you can have it again - into a lookup.
    // ------------------------------------------------------------------
    private Control _recallOverlay;

    private VBoxContainer _recallBox;

    private Player _recallChooser;

    private Card _recallCard;

    public void ShowRecallOverlay(Player chooser, Card recallCard)
    {
        _recallChooser = chooser;
        _recallCard = recallCard;

        if (_recallOverlay == null)
        {
            _recallOverlay = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
            _root.AddChild(_recallOverlay); // scene root, after GameUI, so it draws and takes input on top
            _recallOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            OverlayUi.AddDim(_recallOverlay);
            _recallBox = OverlayUi.AddPanel(_recallOverlay);
        }

        OverlayUi.ClearChildren(_recallBox);

        _recallBox.AddChild(OverlayUi.MakeLabel("Recall", 30));
        _recallBox.AddChild(OverlayUi.MakeLabel(
            "Take one spent Modifier back.\nYou can play it from your next turn.", 16, OverlayUi.Muted));

        HBoxContainer row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        _recallBox.AddChild(row);

        foreach (Card spent in chooser.SpentCards)
        {
            if (!CardEffects.IsPlainModifier(spent)) continue;
            Card choice = spent; // capture per iteration, not the loop variable
            row.AddChild(OverlayUi.CardButton(_ui.CreateCardView(choice, _ui.ModifierCardSize), _ui.ModifierCardSize,
                () => OnRecallChosen(choice)));
        }

        Button cancel = new Button { Text = "Cancel" };
        cancel.Pressed += HideRecallOverlay;
        _recallBox.AddChild(cancel);

        // Mirrored 2-player: Player 2 reads the table upside down, so their chooser does too.
        if (_recallOverlay.GetChildCount() > 1 && _recallOverlay.GetChild(1) is Control panel)
        {
            panel.PivotOffset = panel.Size / 2f;
            panel.RotationDegrees = (_ui.IsMirrored && chooser == P2) ? 180f : 0f;
        }

        _recallOverlay.Visible = true;
        _ui.Refresh();
    }

    private void OnRecallChosen(Card chosen)
    {
        Player chooser = _recallChooser;
        Card recallCard = _recallCard;
        HideRecallOverlay();

        if (chooser == null || recallCard == null) return;

        // A refused play puts the card back under their finger with the reason showing, exactly as
        // every other effect card does.
        if (!_host.PlayEffectCard(chooser, recallCard, chosen)) _host.SetSelection(chooser, recallCard);
        _ui.Refresh();
    }

    private void HideRecallOverlay()
    {
        if (_recallOverlay != null) _recallOverlay.Visible = false;
        _recallChooser = null;
        _recallCard = null;
        _ui.Refresh();
    }

    // ------------------------------------------------------------------
    // Set-end overlay
    //
    // A full-screen layer over the table (blocks every tap underneath) with a centred panel:
    // title, why the set ended, and one button. In mirrored 2-player there's also an
    // upside-down copy of the text at the top of the panel, nearest Player 2. Built in code so
    // both scenes get it without any NodePath wiring.
    // ------------------------------------------------------------------
    private Control _setEndOverlay;

    private Label _setEndTitle;

    private Label _setEndBody;

    private Button _setEndButton;

    private Control _setEndFlippedHolder;   // plain Control: containers reset a child's rotation, holders don't

    private VBoxContainer _setEndFlippedBox; // the node that is rotated 180 degrees

    private Label _setEndFlippedTitle;

    private Label _setEndFlippedBody;

    private HSeparator _setEndDivider;

    private Control _setEndSpacerTop;

    private Control _setEndSpacerBottom;

    private PanelContainer _setEndPanel;

    private VBoxContainer _setEndBox;

    private Action _setEndAction;

    /// Pass 23: "set won text is much better, but ... also increase text size" (Alexander,
    /// 2026-09-17). This panel is read once per set, from wherever the player is sitting, and it
    /// is the only thing on screen while it is up - so it can afford to be the biggest text in
    /// the game.
    private const int SetEndTitleFont = 42;          // was 30

    private const int SetEndBodyFont = 32;           // was 22

    private const int SetEndTitleFontMirrored = 46;  // was 36

    private const int SetEndBodyFontMirrored = 34;   // was 26

    private const int SetEndButtonFont = 34;

    /// The gap either side of the divider when two copies share the panel.
    private const float SetEndMirrorGap = 34f;

    public void BuildSetEndOverlay()
    {
        _setEndOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _root.AddChild(_setEndOverlay); // on the scene root, after GameUI, so it draws (and gets input) on top
        _setEndOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        ColorRect dim = new ColorRect { Color = OverlayUi.DimColor, MouseFilter = Control.MouseFilterEnum.Ignore };
        _setEndOverlay.AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        PanelContainer panel = new PanelContainer();
        OverlayUi.StylePanel(panel, 28);
        _setEndOverlay.AddChild(panel);
        // Anchored to the centre with zero offsets: a Control grows to its minimum size, and with
        // grow "both" it stays centred, so the panel always hugs its content.
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;

        VBoxContainer box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 14);
        panel.AddChild(box);
        _setEndPanel = panel;
        _setEndBox = box;

        // Player 2's upside-down copy (mirrored 2-player only). Same pattern as P2Holder/P2Rotator.
        _setEndFlippedHolder = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_setEndFlippedHolder);
        _setEndFlippedBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _setEndFlippedBox.AddThemeConstantOverride("separation", 6);
        _setEndFlippedHolder.AddChild(_setEndFlippedBox);
        _setEndFlippedBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _setEndFlippedBox.Resized += () =>
        {
            _setEndFlippedBox.PivotOffset = _setEndFlippedBox.Size / 2f;
            _setEndFlippedBox.RotationDegrees = 180f;
        };
        _setEndFlippedTitle = MakeOverlayLabel(SetEndTitleFont);
        _setEndFlippedBody = MakeOverlayLabel(SetEndBodyFont);
        _setEndFlippedBox.AddChild(_setEndFlippedTitle);
        _setEndFlippedBox.AddChild(_setEndFlippedBody);
        // Mirrored only: a gap either side of the divider so the two copies read as two blocks
        // rather than one. Pass 22 made these EXPANDING, inside a panel stretched to 86% x 62% of
        // the screen, which threw each copy out to its own edge; Alexander, 2026-09-17: "centre
        // the text instead of having them at the edges". So they are FIXED gaps now, the panel
        // hugs its content again, and the whole block sits in the middle of the screen with each
        // player's copy the right way up for them.
        _setEndSpacerTop = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_setEndSpacerTop);
        _setEndDivider = new HSeparator { Visible = false };
        box.AddChild(_setEndDivider);
        _setEndSpacerBottom = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_setEndSpacerBottom);

        _setEndTitle = MakeOverlayLabel(SetEndTitleFont);
        _setEndBody = MakeOverlayLabel(SetEndBodyFont);
        box.AddChild(_setEndTitle);
        box.AddChild(_setEndBody);

        _setEndButton = new Button { Text = "Next Set" };
        _setEndButton.AddThemeFontSizeOverride("font_size", SetEndButtonFont);
        _setEndButton.CustomMinimumSize = new Vector2(260, 84);
        _setEndButton.Pressed += OnSetEndButtonPressed;
        box.AddChild(_setEndButton);
    }

    private static Label MakeOverlayLabel(int fontSize)
    {
        Label label = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }

    public void ShowSetEnd(string title, string why, string buttonText, Action onAcknowledged)
    {
        _setEndAction = onAcknowledged;
        if (_setEndOverlay == null)
        {
            onAcknowledged?.Invoke(); // overlay failed to build: don't strand the game
            return;
        }

        // Make it visible first: minimum sizes are only reliable for nodes visible in the tree,
        // and everything below resolves in the same frame before it is drawn.
        _root.MoveChild(_setEndOverlay, _root.GetChildCount() - 1); // above any stray animation card
        _setEndOverlay.Visible = true;

        _setEndTitle.Text = title;
        _setEndBody.Text = why;
        _setEndButton.Text = buttonText;

        bool mirrored = _ui.IsMirrored;
        _setEndFlippedHolder.Visible = mirrored;
        _setEndDivider.Visible = mirrored;
        _setEndSpacerTop.Visible = mirrored;
        _setEndSpacerBottom.Visible = mirrored;

        // Centred, both forms: the panel hugs its content and the content sits in the middle of
        // the screen. Mirrored only adds the two fixed gaps and the divider between the copies.
        _setEndPanel.CustomMinimumSize = Vector2.Zero;
        float gap = mirrored ? SetEndMirrorGap : 0f;
        _setEndSpacerTop.CustomMinimumSize = new Vector2(0, gap);
        _setEndSpacerBottom.CustomMinimumSize = new Vector2(0, gap);
        _setEndBox.AddThemeConstantOverride("separation", mirrored ? 18 : 16);
        int titleFont = mirrored ? SetEndTitleFontMirrored : SetEndTitleFont;
        int bodyFont = mirrored ? SetEndBodyFontMirrored : SetEndBodyFont;
        // One column width for every line, so both copies are the same block and each line is
        // centred in it. The body is a sentence, and at this size a long one would otherwise push
        // the panel wider than the phone, so it wraps instead.
        float wrap = Mathf.Clamp(_root.GetViewport().GetVisibleRect().Size.X * 0.78f, 340f, 620f);
        foreach (Label heading in new[] { _setEndTitle, _setEndFlippedTitle })
        {
            heading.AddThemeFontSizeOverride("font_size", titleFont);
            heading.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            heading.CustomMinimumSize = new Vector2(wrap, 0);
        }
        foreach (Label text in new[] { _setEndBody, _setEndFlippedBody })
        {
            text.AddThemeFontSizeOverride("font_size", bodyFont);
            text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            text.CustomMinimumSize = new Vector2(wrap, 0);
        }
        _setEndFlippedBox.AddThemeConstantOverride("separation", mirrored ? 12 : 6);
        if (mirrored)
        {
            _setEndFlippedTitle.Text = title;
            _setEndFlippedBody.Text = why;
            // The holder reports 0x0 on its own; give it the rotated block's footprint.
            _setEndFlippedHolder.CustomMinimumSize = _setEndFlippedBox.GetCombinedMinimumSize();
        }
        Defer(UpdateSetEndFlippedSize); // re-measure once the first layout pass has run
    }

    private void UpdateSetEndFlippedSize()
    {
        // Hug the content again (a panel never shrinks by itself after being mirrored-size) and
        // stay centred.
        if (_setEndPanel != null)
        {
            _setEndPanel.ResetSize();
            _setEndPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
            _setEndPanel.GrowHorizontal = Control.GrowDirection.Both;
            _setEndPanel.GrowVertical = Control.GrowDirection.Both;
        }
        if (_setEndFlippedHolder == null || !_setEndFlippedHolder.Visible) return;
        _setEndFlippedHolder.CustomMinimumSize = _setEndFlippedBox.GetCombinedMinimumSize();
        _setEndFlippedBox.PivotOffset = _setEndFlippedBox.Size / 2f;
        _setEndFlippedBox.RotationDegrees = 180f;
    }

    private void OnSetEndButtonPressed()
    {
        _setEndOverlay.Visible = false;
        Action action = _setEndAction;
        _setEndAction = null;
        action?.Invoke();
    }
}
