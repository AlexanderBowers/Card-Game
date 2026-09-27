using Godot;
using System;
using System.Collections.Generic;

/// What the menus need from the game behind them.
///
/// Short, because a menu's whole job is to take one decision and hand it over. Every verb here is
/// "start this" - the menus never carry out what they offer.
public interface IMenusHost
{
    bool GameStarted { get; }
    bool VsBot { get; }

    /// A sentence about the run's rules, for the continue button's note.
    string FinaleRulesLine(RunData run, string prefix);

    /// Deal a match: the solo ladder, or local 2-player as the setup page described it.
    void StartMatch(bool local2Player);

    /// Reload this match with the first-launch walkthrough staged into it. The walkthrough is
    /// staged into the deal, so it has to be decided before the cards are dealt - which is why
    /// replaying it is a reload and not a flag flipped mid-match.
    void ReplayTutorial();

    void OpenOptions(Action onClosed = null);
}

/// Every screen that covers the table: the start menu and its sub-pages (local 2-player setup, the
/// endless board), the collection log, How to Play, and the table's own Menu button.
///
/// They are the easiest of the four UI classes to reason about because they are almost stateless -
/// built once, filled on every show (what a menu has to say changes between visits), and talking
/// to the game through a handful of "start this" calls.
///
/// Three pieces of state are static on purpose: they have to survive the scene reload that
/// starting a match performs, and none of them belongs in a save file, because each describes THIS
/// reload rather than the run.
public sealed class Menus
{
    /// The nodes the menus borrow from the table beneath them - hidden while a menu is up, and in
    /// the table menu's case moved into it outright.
    public sealed class Nodes
    {
        public Button ExitButton;
        public Button RestartButton;
        public CheckButton MirrorToggle;
    }

    /// True while a menu is covering the table. A coach mark shown over the top of one teaches
    /// nobody anything, so the table asks before it puts one up.
    public bool Covering => (_howToPlayOverlay != null && _howToPlayOverlay.Visible)
                         || (_tableMenuOverlay != null && _tableMenuOverlay.Visible);

    private readonly IMenusHost _host;

    /// The scene node the menus hang their overlays on.
    private readonly Node _root;

    /// The table's picture. Menus sit on top of it and ask it two things: draw a card face for the
    /// collection log, and put the rank's felt back when that log closes. UI talking to UI, so it
    /// is a plain reference rather than a contract.
    private readonly TableUi _ui;

    private readonly Button _exitButton;
    private readonly Button _restartButton;
    private readonly CheckButton _mirrorToggle;

    public Menus(IMenusHost host, Node root, TableUi ui, Nodes nodes)
    {
        _host = host;
        _root = root;
        _ui = ui;
        _exitButton = nodes.ExitButton;
        _restartButton = nodes.RestartButton;
        _mirrorToggle = nodes.MirrorToggle;
    }

    private Control _tableMenuOverlay;

    private VBoxContainer _tableMenuBox;

    /// Where the debug rows go, inside the table menu. Empty (and so invisible) in a release build.
    public VBoxContainer DebugSlot { get; private set; }

    public void BuildTableMenu()
    {
        _tableMenuOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _root.AddChild(_tableMenuOverlay);
        _tableMenuOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        OverlayUi.AddDim(_tableMenuOverlay);

        VBoxContainer box = OverlayUi.AddPanel(_tableMenuOverlay, contentMargin: 30, separation: 12);
        box.AddChild(OverlayUi.MakeLabel("Menu", MenuTitleFont));

        Button howTo = new Button { Text = "How to Play" };
        howTo.Pressed += () => { HideTableMenu(); ShowHowToPlay(); };
        box.AddChild(howTo);

        // Closing Options lands back on this menu, where the player opened it from.
        Button options = new Button { Text = "Options" };
        options.Pressed += () => { HideTableMenu(); _host.OpenOptions(ShowTableMenu); };
        box.AddChild(options);

        // Restart, Exit and the mirror toggle MOVE rather than being rebuilt here. They are
        // exported nodes whose signals are already connected in _Ready, and a rebuilt copy would
        // need a second connection to the same handlers - two buttons, one of them dead.
        if (_mirrorToggle != null)
        {
            _mirrorToggle.GetParent()?.RemoveChild(_mirrorToggle);
            box.AddChild(_mirrorToggle);
        }
        if (_exitButton != null) _exitButton.Text = "Main Menu";
        foreach (Button moved in new[] { _restartButton, _exitButton })
        {
            if (moved == null) continue;
            moved.GetParent()?.RemoveChild(moved);
            box.AddChild(moved);
        }

        // Replaying reloads the match, because the walkthrough is staged into the turn - so it
        // has to be decided before the cards are dealt, not after. The static survives the reload.
        Button replay = new Button { Text = "Replay the tutorial" };
        replay.Pressed += () =>
        {
            HideTableMenu();
            _host.ReplayTutorial();
        };
        box.AddChild(replay);

        // Debug builds put their stage-jump / ad switches here (GameManager.BuildDebugRow) - in
        // the menu rather than on the table, where they covered the layout (pass 35).
        DebugSlot = new VBoxContainer { Name = "DebugSlot" };
        DebugSlot.AddThemeConstantOverride("separation", 6);
        box.AddChild(DebugSlot);

        Button close = new Button { Text = "Back to the table" };
        close.Pressed += HideTableMenu;
        box.AddChild(close);

        _tableMenuBox = box; // re-sized on every open: MenuButtonWidth follows the viewport

        // The button that opens this menu is in the table layout scenes now (MenuButton, pass 34);
        // TableUi wires it to ShowTableMenu.
    }

    /// One size for every button on a full-screen menu. See the MenuButton* constants for why
    /// this can be generous where the table cannot.
    private void StyleMenuButton(Button button)
    {
        if (button == null) return;
        button.AddThemeFontSizeOverride("font_size", MenuButtonFont);
        button.CustomMinimumSize = new Vector2(MenuButtonWidth, MenuButtonHeight);
    }

    public void ShowTableMenu()
    {
        if (_tableMenuOverlay == null) return;

        // Every button in here, on every open - including Restart, Main Menu and the mirror
        // toggle, which were MOVED in from the table and so arrive carrying the table's sizing.
        // Done here rather than at build time because MenuButtonWidth follows the viewport, and
        // the viewport changes with the orientation and with how far portrait has zoomed in.
        if (_tableMenuBox != null)
            foreach (Node child in _tableMenuBox.GetChildren())
                if (child is Button menuButton) StyleMenuButton(menuButton);

        // The toggle only means anything with two people at one device.
        if (_mirrorToggle != null) _mirrorToggle.Visible = !_host.VsBot;

        _root.MoveChild(_tableMenuOverlay, _root.GetChildCount() - 1); // above every other overlay
        _tableMenuOverlay.Visible = true;
    }

    public void HideTableMenu()
    {
        if (_tableMenuOverlay != null) _tableMenuOverlay.Visible = false;
    }

    /// Set just before ChangeSceneToFile sends the player to the two-player table, and read by the
    /// _Ready on the other side, so they land in a game rather than on a second front door.
    ///
    /// A static rather than a field on RunData, which is where AutoStartNextMatch lives: RunData is
    /// the RUN, and local 2-player never touches a run - putting this there would be the first
    /// thing to contradict that file's opening line. A static outlives ChangeSceneToFile for the
    /// same reason the autoload does, which is the whole reason either of them works.
    public static bool PendingLocal2Player;

    private Control _startMenuOverlay;

    private VBoxContainer _startMenuBox;

    private Button _newRunButton;

    private bool _newRunArmed; // "New Run" over an unfinished climb asks a second time

    private bool _endlessArmed; // ...and so does Endless

    /// Behind the playmat, in case it fails to load: the mat's own edge colour.
    private static readonly Color MenuBackdrop = new Color(0.1f, 0.14f, 0.16f);

    // Pass 31: the start menu is the game's front door, so it gets the Pocket treatment - the
    // playmat, a slanted accent banner across it, and the logo above the buttons (portrait).
    private static readonly Texture2D MenuMatLandscape = GD.Load<Texture2D>("res://assets/aimfor20_art/playmat_landscape.png");
    private static readonly Texture2D MenuMatPortrait = GD.Load<Texture2D>("res://assets/aimfor20_art/playmat_portrait.png");
    private static readonly Texture2D MenuLogo = GD.Load<Texture2D>("res://assets/aimfor20_art/ui/menu_logo.png");

    private const float MenuBannerAngle = -9f;   // degrees; the Pocket "Battle" / "VS." slant
    private const float MenuLogoSize = 170f;

    public void BuildStartMenu()
    {
        _startMenuOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _root.AddChild(_startMenuOverlay);
        _startMenuOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // OPAQUE, not OverlayUi.AddDim (Alexander, 2026-09-13). Every other overlay in the game
        // sits on top of a live table and wants it showing through - that is the point of the dim,
        // and why the intermission is an overlay rather than a scene change. This one is the screen
        // BEFORE there is a table, and a dealt hand behind it says a game is already running.
        ColorRect backdrop = new ColorRect { Color = MenuBackdrop, MouseFilter = Control.MouseFilterEnum.Ignore };
        _startMenuOverlay.AddChild(backdrop);
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Still opaque - the mat covers the table completely, it just is not a flat colour now.
        TextureRect mat = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Texture = MenuMatLandscape,
        };
        _startMenuOverlay.AddChild(mat);
        mat.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Two slanted bands behind the panel: a wide pale one and a narrower accent one inside it.
        ColorRect bandWide = new ColorRect { Color = new Color(0.55f, 0.82f, 1f, 0.22f), MouseFilter = Control.MouseFilterEnum.Ignore };
        ColorRect band = new ColorRect { Color = new Color(OverlayUi.Accent, 0.75f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _startMenuOverlay.AddChild(bandWide);
        _startMenuOverlay.AddChild(band);

        void Relayout()
        {
            Vector2 size = _startMenuOverlay.Size;
            if (size.X <= 0 || size.Y <= 0) return;
            mat.Texture = size.Y > size.X ? MenuMatPortrait : MenuMatLandscape;

            // Longer than the diagonal, so the ends never show whatever the aspect.
            float length = size.Length() * 1.2f;
            float centreY = size.Y * 0.5f;
            PlaceBand(bandWide, length, size.Y * 0.42f, size.X / 2f, centreY);
            PlaceBand(band, length, size.Y * 0.2f, size.X / 2f, centreY);
        }
        _startMenuOverlay.Resized += Relayout;
        Relayout();

        _startMenuBox = OverlayUi.AddPanel(_startMenuOverlay, contentMargin: 32, separation: 12);

        _collectionOverlay = new CollectionOverlay();
        _root.AddChild(_collectionOverlay);
        _collectionOverlay.Setup(_ui.CreateCardView);
    }

    private static void PlaceBand(ColorRect band, float length, float thickness, float cx, float cy)
    {
        band.Size = new Vector2(length, thickness);
        band.Position = new Vector2(cx - length / 2f, cy - thickness / 2f);
        band.PivotOffset = band.Size / 2f;
        band.RotationDegrees = MenuBannerAngle;
    }

    public void ShowStartMenu()
    {
        if (_startMenuOverlay == null) return;

        FillStartMenu();
        _startMenuOverlay.Visible = true;

    }

    public void HideStartMenu()
    {
        if (_startMenuOverlay != null) _startMenuOverlay.Visible = false;
    }

    /// Rebuilt on every show rather than once, because what it has to say changes: whether there is
    /// a climb to continue, which rung it is on, and what the player has banked.
    private void FillStartMenu()
    {
        OverlayUi.ClearChildren(_startMenuBox);
        _newRunArmed = false;

        // The project's own name, so renaming the game renames this too instead of leaving a second
        // copy of the title to go stale.
        string title = ProjectSettings.GetSetting("application/config/name").AsString();
        if (string.IsNullOrWhiteSpace(title)) title = "Card Game";
        // The logo only where there is height to spare: a phone held upright. Landscape spends
        // every pixel of its 720 base on the buttons.
        Vector2 view = _root.GetViewport().GetVisibleRect().Size;
        if (MenuLogo != null && view.Y > view.X)
        {
            _startMenuBox.AddChild(new TextureRect
            {
                Texture = MenuLogo,
                CustomMinimumSize = new Vector2(MenuLogoSize, MenuLogoSize),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }
        _startMenuBox.AddChild(OverlayUi.MakeLabel(title, MenuTitleFont));

        RunData run = RunData.Instance;
        // Pass 47: the table behind the menu starts a run the moment the scene loads
        // (GameManager.BeginRunMatch), so on first launch RunActive is already true and the menu
        // used to offer "Continue - Match 1 of 10" over a climb nobody had played. A climb only
        // counts as in progress once it has got somewhere - past the first rung, or into endless.
        // At match 1, continuing and starting afresh are the same thing, so nothing is lost.
        bool runInProgress = run != null && run.RunActive && !run.RunComplete
                             && (run.StepIndex > 0 || run.Endless);

        Label blurb = OverlayUi.MakeLabel(
            runInProgress ? "A climb is in progress." : "Climb the ladder, or play someone across the table.",
            MenuNoteFont, OverlayUi.Muted);
        blurb.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        blurb.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
        _startMenuBox.AddChild(blurb);
        _startMenuBox.AddChild(MenuSpacer());

        // First, and named with the rung, because a player who left mid-ladder came back for this
        // one thing and should not have to guess which button keeps their climb.
        Button continueButton = null;
        if (runInProgress && run.Endless)
        {
            continueButton = AddMenuButton($"Continue - Endless, streak {run.EndlessStreak}",
                          $"target {run.CurrentTarget}{_host.FinaleRulesLine(run, "   -   ")}",
                          () => MenuStartRun(fresh: false));
        }
        else if (runInProgress)
        {
            continueButton = AddMenuButton($"Continue - Match {run.MatchNumber} of {RunData.LadderLength}",
                          $"{run.CurrentOpponent}   -   target {run.CurrentTarget}",
                          () => MenuStartRun(fresh: false));
        }

        // Over an unfinished climb this button throws the climb away, so it asks twice. A second
        // tap is the cheapest confirmation there is and it costs no second overlay.
        _newRunButton = AddMenuButton(
            runInProgress ? "New Run" : "Start a Run",
            runInProgress ? "Gives up the climb above. Your cards and medals stay." : null,
            () =>
            {
                if (runInProgress && !_newRunArmed)
                {
                    _newRunArmed = true;
                    _newRunButton.Text = "New Run - tap again to give up the climb";
                    return;
                }
                MenuStartRun(fresh: true);
            });

        // One accent per screen: the button a returning player came for.
        OverlayUi.StyleButton(continueButton ?? _newRunButton, primary: true);

        // No explanatory line under either of the two plain modes (Alexander, 2026-09-13): a menu
        // that describes its own buttons is a menu that does not trust them. The one note that
        // stays is the New Run warning, which is not a description - it is a consequence.
        // Endless: earned by clearing the ladder once. Over a run in progress it gives that run up,
        // so it asks twice, exactly as New Run does.
        if (run != null && run.EndlessUnlocked)
        {
            _endlessArmed = false;
            Button endless = null;
            endless = AddMenuButton(
                run.EndlessBest > 0 ? $"Endless   (best streak {run.EndlessBest})" : "Endless",
                null,
                () =>
                {
                    if (runInProgress && !_endlessArmed)
                    {
                        _endlessArmed = true;
                        endless.Text = "Endless - tap again to give up the run above";
                        return;
                    }
                    RunData.Instance.StartEndless();
                    MenuStartRun(fresh: false);
                });

            // Only once there is something on it. An empty board on a player who has unlocked
            // endless but never played it is a row that explains nothing.
            if (run.EndlessScores.Count > 0)
                AddMenuButton("Endless Scores", null, FillEndlessScores);
        }

        AddMenuButton("Local 2-Player", null, FillLocal2PlayerSetup);

        _startMenuBox.AddChild(MenuSpacer());
        AddMenuButton("How to Play", null, ShowHowToPlay);
        AddMenuButton("Options", null, () => _host.OpenOptions());
        if (run != null)
            AddMenuButton($"Collection   {run.CollectionFound}/{RunData.CollectionKeys.Length}", null, OpenCollection);

        // Quit everywhere but iOS (Alexander, 2026-09-16: "start menu should have quit game").
        // Android allows an app to close itself; Apple's review guidelines reject a quit button,
        // and iOS apps are left to the home gesture.
        if (!OS.HasFeature("ios")) AddMenuButton("Quit Game", null, () => _root.GetTree().Quit());

        // The proof that a lost run did not erase anything - which is the promise the run makes,
        // and the one place the player can be shown it before deciding to climb again.
        if (run != null && (run.Medals > 0 || run.FurthestStep > 0))
        {
            _startMenuBox.AddChild(MenuSpacer());
            Label banked = OverlayUi.MakeLabel(
                $"{run.Medals} medals   -   {run.Inventory.Count} cards owned   -   best: match {run.FurthestStep + 1}",
                MenuNoteFont, OverlayUi.Muted);
            banked.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            banked.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
            _startMenuBox.AddChild(banked);
        }
    }

    /// One row of the menu: a wide button, and optionally a line under it saying what it does. The
    /// note is a separate label rather than a second line inside the button so that arming the New
    /// Run button has exactly one string to rewrite.
    private Button AddMenuButton(string text, string note, Action onPressed)
    {
        Button button = new Button { Text = text, CustomMinimumSize = new Vector2(MenuButtonWidth, MenuButtonHeight) };
        button.AddThemeFontSizeOverride("font_size", MenuButtonFont);
        if (onPressed != null) button.Pressed += onPressed;
        _startMenuBox.AddChild(button);

        if (!string.IsNullOrEmpty(note))
        {
            Label label = OverlayUi.MakeLabel(note, MenuNoteFont, OverlayUi.Muted);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
            _startMenuBox.AddChild(label);
        }

        return button;
    }

    // Pass 25 (Alexander, 2026-09-17: "menu text needs to be significantly enlarged"). These are
    // FREE in a way the table's fonts are not: every menu is a full-screen overlay over the table
    // rather than part of MainLayout, so EnsureLayoutFits never measures them and nothing shrinks
    // to pay for them. The only ceiling is the panel fitting a phone held upright, which at a
    // 720-wide base leaves room for a 420 button and its margins.
    /// The widest a menu button may get, and how much air is left either side of it. The width
    /// is CLAMPED to the viewport rather than fixed, because portrait enlarges the whole UI when
    /// there is room (EnsureLayoutFits) - which shrinks the design-pixel viewport, sometimes well
    /// below the 720 base. A fixed 420 would hang off both edges of a table zoomed that far in.
    private const float MenuButtonWidthMax = 460f; // was a fixed 340

    private const float MenuSideGutter = 44f;

    private float MenuButtonWidth => Mathf.Clamp(
        _root.GetViewport().GetVisibleRect().Size.X - 2f * MenuSideGutter, 240f, MenuButtonWidthMax);

    private const float MenuButtonHeight = 62; // was 44

    private const int MenuButtonFont = 26;     // was 18

    private const int MenuTitleFont = 52;      // was 40

    private const int MenuNoteFont = 17;       // was 12

    private const int MenuHeadingFont = 34;    // a sub-page's title (Local 2-Player, Endless Scores)

    private const int MenuSectionFont = 26;    // a heading inside a sub-page (Target, Specials)

    private static Control MenuSpacer() => new Control { CustomMinimumSize = new Vector2(0, 8) };

    /// The ladder lives in the solo scene. From the two-player table that is a scene change, and
    /// the note RunData already keeps for the deck screen is what makes the new scene deal itself.
    private void MenuStartRun(bool fresh)
    {
        if (fresh) RunData.Instance?.StartNewRun();

        HideStartMenu();
        _host.StartMatch(local2Player: false);
    }

    // ------------------------------------------------------------------
    // The collection log
    // ------------------------------------------------------------------
    private CollectionOverlay _collectionOverlay;

    /// Fixed rather than scaled with the table: six across has to fit a phone held upright.
    private static readonly Vector2 CollectionCardSize = TableUi.BaseCardSize * 0.65f;

    private void OpenCollection()
    {
        // Closing refreshes the menu (the count on its button) and the deck back (the toggle).
        _collectionOverlay?.Open(CollectionCardSize, () => { FillStartMenu(); _ui.ApplyRankTheme(); });
    }

    // ------------------------------------------------------------------
    // Local 2-player setup (roadmap, from the mobile playtest group's ask for effect cards in
    // local 2-player). Two choices before the deal: the target, and whether the special Modifiers
    // are in the hands. Static for the same reason PendingLocal2Player is - they have to survive
    // the scene change and Restart's reload - and, like it, never saved to disk: the menu
    // remembers the last choice for this launch only.
    // ------------------------------------------------------------------
    private static readonly int[] Local2PlayerTargets = { 18, 20, 23 };

    public static int Local2PlayerTarget = 20;

    public static bool Local2PlayerSpecials;

    /// The start menu's second page. Reuses the menu panel rather than opening another overlay,
    /// so Back is a refill and there is no second panel to stack or dismiss.
    private void FillLocal2PlayerSetup()
    {
        // Only what the player has met in single player is offered (Alexander, 2026-09-16):
        // a target once a ladder rung has been played at it, a special once its rung has been
        // reached. Nothing unlocked means nothing to choose, so the page is skipped.
        List<int> targets = UnlockedLocalTargets();
        List<CardEffect> effects = UnlockedLocalSpecials();
        SanitizeLocal2PlayerChoices(targets, effects);
        if (targets.Count <= 1 && effects.Count == 0)
        {
            MenuStartLocal2Player();
            return;
        }

        OverlayUi.ClearChildren(_startMenuBox);
        _startMenuBox.AddChild(OverlayUi.MakeLabel("Local 2-Player", MenuHeadingFont));
        _startMenuBox.AddChild(MenuSpacer());

        if (targets.Count > 1)
        {
            _startMenuBox.AddChild(OverlayUi.MakeLabel("Target", MenuSectionFont));
            HBoxContainer targetRow = AddChoiceRow();
            foreach (int target in targets)
            {
                int value = target;
                AddChoice(targetRow, value.ToString(), Local2PlayerTarget == value,
                          () => Local2PlayerTarget = value);
            }
        }

        if (effects.Count > 0)
        {
            _startMenuBox.AddChild(MenuSpacer());
            _startMenuBox.AddChild(OverlayUi.MakeLabel("Special Modifiers", MenuSectionFont));
            HBoxContainer specials = AddChoiceRow();
            AddChoice(specials, "Off", !Local2PlayerSpecials, () => Local2PlayerSpecials = false);
            AddChoice(specials, "On", Local2PlayerSpecials, () => Local2PlayerSpecials = true);

            List<string> names = new List<string>();
            foreach (CardEffect effect in effects) names.Add(CardEffects.Label(effect));
            Label note = OverlayUi.MakeLabel(
                $"On: each hand has one special Modifier - {string.Join(", ", names)}.",
                MenuNoteFont, OverlayUi.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.HorizontalAlignment = HorizontalAlignment.Center;
            note.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
            _startMenuBox.AddChild(note);
        }

        _startMenuBox.AddChild(MenuSpacer());
        AddMenuButton("Deal", null, MenuStartLocal2Player);
        AddMenuButton("Back", null, FillStartMenu);
    }

    /// The endless board (pass 24). A page of the start menu rather than an overlay of its own:
    /// it is read from the menu, it is five rows long, and FillLocal2PlayerSetup already proved
    /// the pattern - swap the menu's contents, and Back swaps them straight back.
    private void FillEndlessScores()
    {
        RunData run = RunData.Instance;
        if (run == null) { FillStartMenu(); return; }

        OverlayUi.ClearChildren(_startMenuBox);
        _startMenuBox.AddChild(OverlayUi.MakeLabel("Endless Scores", MenuHeadingFont));
        _startMenuBox.AddChild(OverlayUi.MakeLabel(
            "How many matches in a row, before the run ended.", MenuNoteFont, OverlayUi.Muted));
        _startMenuBox.AddChild(MenuSpacer());

        for (int i = 0; i < run.EndlessScores.Count; i++)
        {
            RunData.EndlessScore score = run.EndlessScores[i];
            bool best = i == 0;

            HBoxContainer row = new HBoxContainer();
            row.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
            row.AddThemeConstantOverride("separation", 10);
            _startMenuBox.AddChild(row);

            Label place = OverlayUi.MakeLabel($"{i + 1}.", 26, best ? OverlayUi.MedalGold : OverlayUi.Muted);
            place.CustomMinimumSize = new Vector2(44, 0);
            place.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(place);

            Label streak = OverlayUi.MakeLabel($"{score.Streak}", 34, best ? OverlayUi.MedalGold : OverlayUi.Ink);
            streak.CustomMinimumSize = new Vector2(72, 0);
            streak.HorizontalAlignment = HorizontalAlignment.Left;
            row.AddChild(streak);

            Label when = OverlayUi.MakeLabel(
                score.UnixTime > 0 ? Time.GetDateStringFromUnixTime(score.UnixTime) : string.Empty,
                MenuNoteFont, OverlayUi.Muted);
            when.HorizontalAlignment = HorizontalAlignment.Left;
            when.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(when);
        }

        _startMenuBox.AddChild(MenuSpacer());
        Label footer = OverlayUi.MakeLabel(
            $"Best streak {run.EndlessBest}.   The rules are re-rolled every match; "
            + $"past a streak of {RunData.EndlessThirdRuleStreak} the opponent carries three specials.",
            MenuNoteFont, OverlayUi.Muted);
        footer.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        footer.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
        _startMenuBox.AddChild(footer);

        _startMenuBox.AddChild(MenuSpacer());
        AddMenuButton("Back", null, FillStartMenu);
    }

    private const int DefaultLocalTarget = 20;

    /// 20 always; 18 and 23 once a ladder rung at that target has been reached.
    private static List<int> UnlockedLocalTargets()
    {
        List<int> targets = new List<int>();
        foreach (int target in Local2PlayerTargets)
        {
            if (target == DefaultLocalTarget || (RunData.Instance?.TargetReached(target) ?? false))
                targets.Add(target);
        }
        return targets;
    }

    public static List<CardEffect> UnlockedLocalSpecials() =>
        RunData.Instance?.MetEffects() ?? new List<CardEffect>();

    /// A remembered choice that is not on offer (a wiped save, say) falls back to the default.
    private static void SanitizeLocal2PlayerChoices(List<int> targets, List<CardEffect> effects)
    {
        if (!targets.Contains(Local2PlayerTarget)) Local2PlayerTarget = DefaultLocalTarget;
        if (effects.Count == 0) Local2PlayerSpecials = false;
    }

    private HBoxContainer AddChoiceRow()
    {
        HBoxContainer row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        _startMenuBox.AddChild(row);
        return row;
    }

    /// One option of a pick-one row. Toggle buttons sharing the row's ButtonGroup, so the pressed
    /// look IS the current choice and there is nothing else to keep in sync. The group is found
    /// from the row's first button rather than stored, so the row needs no bookkeeping of its own.
    private static void AddChoice(HBoxContainer row, string text, bool selected, Action onChosen)
    {
        ButtonGroup group = (row.GetChildCount() > 0 && row.GetChild(0) is Button first)
            ? first.ButtonGroup
            : new ButtonGroup();

        Button button = new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonGroup = group,
            ButtonPressed = selected,
            CustomMinimumSize = new Vector2(120, 58),
        };
        button.AddThemeFontSizeOverride("font_size", 26);
        button.Toggled += pressed => { if (pressed) onChosen(); };
        row.AddChild(button);
    }

    /// ...and the mirrored face-to-face table lives in the other scene.
    private void MenuStartLocal2Player()
    {
        SanitizeLocal2PlayerChoices(UnlockedLocalTargets(), UnlockedLocalSpecials());

        HideStartMenu();
        _host.StartMatch(local2Player: true);
    }

    // ------------------------------------------------------------------
    // How to Play
    //
    // A "How to Play" button is added in code to the middle panel's ButtonColumn (just above the
    // Restart / Exit row) so both scenes get it without NodePath wiring. It opens a full-screen
    // overlay with the rules; it can be opened at any time and changes no game state. In mirrored
    // 2-player mode a "Flip for other player" button turns the panel upside down for Player 2.
    // ------------------------------------------------------------------
    // ONE text for every mode (pass 48, Alexander's rewrite). There used to be a long version for
    // play against the bot as well; it was dropped. The first-launch tutorial teaches the table
    // (claude/tutorial-and-how-to-play-spec.md), the ladder introduces each effect card where you
    // meet it, and this is the short reference you come back to.
    private static readonly string HowToPlayShort =
        "GOAL\n" +
        $"Draw Cards to get close to the Target without going over. Closest player under the Target wins the Set.\nWin {GameState.SetsToWinMatch} Sets to win the Match.\n\n" +
        "EACH TURN\n" +
        "Both players Draw a random Card of value 1-10. Each player decides whether they want to Draw another Card, Hold, or Play Modifier(s).\n\n" +
        "MODIFIERS\n" +
        "Special Cards allowing you to get closer to the Target. Tap a Modifier to see its effects.\n" +
        "Tap it again to play it. Each player gets four Modifiers per Match and are one-time use. They are not replenished between Sets.\n\n" +
        "DRAW CARD or HOLD\n" +
        "Draw Card: Your turn ends and you will receive another Card at the beginning of the next Turn.\n" +
        "Hold: Your Turn ends and locks in your Score for the remainder of the Set. \n\n" +
        "GOING OVER\n" +
        "When your Score is above the Target, you have the ability to play Modifier(s) to lower your Score before ending your turn. " +
        "If your Turn ends while over the Target, your opponent wins the Set. If both players are over, the Set is a tie and repeated.";

    private Control _howToPlayOverlay;

    private Label _howToPlayRules;

    private PanelContainer _howToPlayPanel;

    private Button _howToPlayFlipButton;

    private bool _howToPlayFlipped = false;

    public void BuildHowToPlay()
    {
        // The button that opens this lives in the table menu now (CompactControlPanel) - it was
        // one of four things permanently on screen in a middle column the playtest called
        // cluttered, and it is pressed once a session.
        //
        // The overlay: dim + centred panel + scrolling rules + Close (and Flip when mirrored).
        _howToPlayOverlay = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _root.AddChild(_howToPlayOverlay);
        _howToPlayOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        ColorRect dim = new ColorRect { Color = OverlayUi.DimColor, MouseFilter = Control.MouseFilterEnum.Ignore };
        _howToPlayOverlay.AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _howToPlayPanel = new PanelContainer();
        OverlayUi.StylePanel(_howToPlayPanel, 20);
        _howToPlayOverlay.AddChild(_howToPlayPanel);
        // Fill the screen with a margin so the rules get as much room as the device has.
        _howToPlayPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _howToPlayPanel.OffsetLeft = 24;
        _howToPlayPanel.OffsetTop = 24;
        _howToPlayPanel.OffsetRight = -24;
        _howToPlayPanel.OffsetBottom = -24;
        _howToPlayPanel.Resized += () =>
        {
            _howToPlayPanel.PivotOffset = _howToPlayPanel.Size / 2f;
            _howToPlayPanel.RotationDegrees = _howToPlayFlipped ? 180f : 0f;
        };

        VBoxContainer box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        _howToPlayPanel.AddChild(box);

        Label title = OverlayUi.MakeLabel("", 30);
        title.Text = "How to Play";
        box.AddChild(title);

        ScrollContainer scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        box.AddChild(scroll);

        _howToPlayRules = new Label
        {
            Text = HowToPlayShort,
            AutowrapMode = TextServer.AutowrapMode.Word,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _howToPlayRules.AddThemeFontSizeOverride("font_size", 20);
        scroll.AddChild(_howToPlayRules);

        HBoxContainer buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 16);
        box.AddChild(buttons);

        _howToPlayFlipButton = new Button { Text = "Flip for other player", Visible = false };
        _howToPlayFlipButton.Pressed += () =>
        {
            _howToPlayFlipped = !_howToPlayFlipped;
            _howToPlayPanel.PivotOffset = _howToPlayPanel.Size / 2f;
            _howToPlayPanel.RotationDegrees = _howToPlayFlipped ? 180f : 0f;
        };
        buttons.AddChild(_howToPlayFlipButton);

        Button close = new Button { Text = "Close" };
        close.Pressed += HideHowToPlay;
        buttons.AddChild(close);
    }

    public void ShowHowToPlay()
    {
        if (_howToPlayOverlay == null) return;
        if (_howToPlayRules != null) _howToPlayRules.Text = HowToPlayShort;
        _howToPlayFlipped = false;
        _howToPlayPanel.RotationDegrees = 0f;
        _howToPlayFlipButton.Visible = _ui.IsMirrored;
        _root.MoveChild(_howToPlayOverlay, _root.GetChildCount() - 1); // above any stray animation card
        _howToPlayOverlay.Visible = true;
    }

    private void HideHowToPlay()
    {
        if (_howToPlayOverlay == null) return;
        _howToPlayOverlay.Visible = false;
    }
}
