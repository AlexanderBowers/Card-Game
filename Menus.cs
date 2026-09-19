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

    /// Start the match the dropdown and the setup pages have described.
    void StartMatch();

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
        public Button StartButton;
        public Button ExitButton;
        public Button RestartButton;
        public OptionButton GameModeButton;
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

    private readonly Button _startButton;
    private readonly Button _exitButton;
    private readonly Button _restartButton;
    private readonly OptionButton _gameModeButton;
    private readonly CheckButton _mirrorToggle;

    public Menus(IMenusHost host, Node root, TableUi ui, Nodes nodes)
    {
        _host = host;
        _root = root;
        _ui = ui;
        _startButton = nodes.StartButton;
        _exitButton = nodes.ExitButton;
        _restartButton = nodes.RestartButton;
        _gameModeButton = nodes.GameModeButton;
        _mirrorToggle = nodes.MirrorToggle;
    }

    private Control _tableMenuOverlay;

    private VBoxContainer _tableMenuBox;

    public void BuildTableMenu()
    {
        Node systemRow = _restartButton?.GetParent();
        Control column = systemRow?.GetParent() as Control;
        if (column == null) return;

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

        Button close = new Button { Text = "Back to the table" };
        close.Pressed += HideTableMenu;
        box.AddChild(close);

        _tableMenuBox = box; // re-sized on every open: MenuButtonWidth follows the viewport

        // ...and the one button left on the table, in the slot the Restart / Exit row had.
        Button open = new Button { Text = "Menu" };
        open.Pressed += ShowTableMenu;
        column.AddChild(open);
        if (systemRow.GetParent() == column) column.MoveChild(open, systemRow.GetIndex());
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

    /// Opaque, and a deeper shade of the table's own felt so the menu still reads as this game.
    private static readonly Color MenuBackdrop = new Color(0.04f, 0.10f, 0.07f);

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

        _startMenuBox = OverlayUi.AddPanel(_startMenuOverlay, contentMargin: 32, separation: 12);

        _collectionOverlay = new CollectionOverlay();
        _root.AddChild(_collectionOverlay);
        _collectionOverlay.Setup(_ui.CreateCardView);
    }

    public void ShowStartMenu()
    {
        if (_startMenuOverlay == null) return;

        FillStartMenu();
        _startMenuOverlay.Visible = true;

        // The menu replaces them both, and they are behind an opaque backdrop anyway. Leaving them
        // live is two ways to do one thing, and the dropdown's answer is not the menu's.
        if (_startButton != null) _startButton.Visible = false;
        if (_gameModeButton != null) _gameModeButton.Visible = false;
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
        _startMenuBox.AddChild(OverlayUi.MakeLabel(title, MenuTitleFont));

        RunData run = RunData.Instance;
        bool runInProgress = run != null && run.RunActive && !run.RunComplete;

        Label blurb = OverlayUi.MakeLabel(
            runInProgress ? "A climb is in progress." : "Climb the ladder, or play someone across the table.",
            MenuNoteFont, OverlayUi.Muted);
        blurb.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        blurb.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
        _startMenuBox.AddChild(blurb);
        _startMenuBox.AddChild(MenuSpacer());

        // First, and named with the rung, because a player who left mid-ladder came back for this
        // one thing and should not have to guess which button keeps their climb.
        if (runInProgress && run.Endless)
        {
            AddMenuButton($"Continue - Endless, streak {run.EndlessStreak}",
                          $"target {run.CurrentTarget}{_host.FinaleRulesLine(run, "   -   ")}",
                          () => MenuStartRun(fresh: false));
        }
        else if (runInProgress)
        {
            AddMenuButton($"Continue - Match {run.MatchNumber} of {RunData.LadderLength}",
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

        if (_gameModeButton != null)
        {
            if (RunData.Instance != null) RunData.Instance.AutoStartNextMatch = true;
            _root.GetTree().ChangeSceneToFile("res://solo_table_scene.tscn");
            return;
        }

        HideStartMenu();
        _host.StartMatch();
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

            Label streak = OverlayUi.MakeLabel($"{score.Streak}", 34, best ? OverlayUi.MedalGold : Colors.White);
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

        if (_gameModeButton == null)
        {
            PendingLocal2Player = true;
            _root.GetTree().ChangeSceneToFile("res://table_scene.tscn");
            return;
        }

        // Before starting, not after: OnStartButtonPressed reads this dropdown to decide whether to
        // route to the solo scene, and on "vs. Bot" it would send us straight back out again.
        _gameModeButton.Select(0);
        if (_mirrorToggle != null) _mirrorToggle.Visible = true;

        HideStartMenu();
        _host.StartMatch();
    }

    // ------------------------------------------------------------------
    // How to Play
    //
    // A "How to Play" button is added in code to the middle panel's ButtonColumn (just above the
    // Restart / Exit row) so both scenes get it without NodePath wiring. It opens a full-screen
    // overlay with the rules; it can be opened at any time and changes no game state. In mirrored
    // 2-player mode a "Flip for other player" button turns the panel upside down for Player 2.
    // ------------------------------------------------------------------
    // TWO texts, because the two modes have different learners (Alexander, 2026-09-15).
    //
    // In local 2-player somebody who already knows the game is sitting next to somebody who does
    // not, and a person explains it far better than a panel does. That screen only has to carry
    // the handful of rules the explainer might forget - so it is short on purpose, and making it
    // longer would make it worse.
    //
    // Against the bot nobody is there to explain, so the game has to teach. The first-launch
    // tutorial does that (claude/tutorial-and-how-to-play-spec.md); this text is the reference
    // you come back to, and it deliberately does NOT enumerate the six effect cards - the ladder
    // introduces them one rung at a time and explains each one where you meet it. A list of all
    // six here would undo that, and it is exactly the "text-heavy wall" the tenets rule out.
    private static readonly string HowToPlayShort =
        "GOAL\n" +
        "Get as close to the target without going over. The target is on your score line - " +
        $"\"You  17/20\". Win {GameState.SetsToWinMatch} sets to win the match.\n\n" +
        "EACH TURN\n" +
        "Both players are dealt a card at the same time. You both decide at the same time too - " +
        "nobody waits for anyone.\n\n" +
        "MODIFIERS\n" +
        "Tap a Modifier to see its effect, then tap it again to play it. It adds its value to your " +
        "score. You get four, and they have to last the whole match.\n\n" +
        "DRAW CARD or HOLD\n" +
        "Draw Card: you are done for this turn, and you take another card on the next one.\n" +
        "Hold: you stop taking cards, and your score is locked for the rest of the set.\n\n" +
        "GOING OVER\n" +
        "Over the target is only a warning until you press Draw Card or Hold - a minus Modifier can " +
        "still save you. Draw while over, and you bust.\n\n" +
        "That is the whole game. Everything else is a Modifier that explains itself when you meet it.";

    private static readonly string HowToPlayFull =
        "GOAL\n" +
        "Get as close to the target as you can without going over. The target is on your own score " +
        "line - \"You  17/20\" - and it CHANGES as you climb: 20 at first, then 23, then 18, and on " +
        $"up. Win {GameState.SetsToWinMatch} sets to win the match.\n\n" +
        "MATCH, SET, TURN\n" +
        "A match is played in sets, and a set is played in turns. Win a set by finishing closer to " +
        "the target than your opponent.\n\n" +
        "THE DECK\n" +
        "One deck of 40 cards, shared by both players: four each of 1 to 10. It is shuffled fresh " +
        "every set, and the number on it is how many cards are left - so it can be counted.\n\n" +
        "A TURN\n" +
        "Each turn, every player who isn't holding is dealt one card at the same time. Both players " +
        "then decide - at the same time, without waiting for each other - whether to play a " +
        "Modifier, and then press Draw Card or Hold.\n" +
        "When the target is 20 or more, the FIRST turn of a set gives everyone two cards. Two " +
        "cards can never total more than 20, so that opening can never bust you.\n\n" +
        "MODIFIERS\n" +
        "You get 4 Modifiers at the start of a match, and they have to last every set of it - " +
        "a Modifier spent in the first set is gone for the rest. Plain ones are worth -4 to +4 and " +
        "add their value to your score.\n\n" +
        "PLAYING A MODIFIER\n" +
        "Tap a Modifier to pick it up. It lifts, and your score changes to what it would " +
        "become - for example 17/20 turns into 20/20. Blue means you would still be at or under the " +
        "target, orange means it would take you over. Nothing is spent yet: tap Play (or tap it again) to " +
        "commit it, or Put back to change your mind.\n\n" +
        "+/- MODIFIERS\n" +
        "A Modifier marked +/- can be played either way round. Pick it up and press Flip Value " +
        "to swap it between plus and minus - as often as you like - before playing it. " +
        "A +3 becomes a -3, and back again.\n\n" +
        "DRAW CARD\n" +
        "You are done for this turn, and you take another card on the next one.\n\n" +
        "HOLD\n" +
        "You stop taking cards for the rest of the set. Your score is locked in.\n\n" +
        "GOING OVER\n" +
        "Going over the target is only a warning (\"Over target!\") - you can still play a " +
        "minus Modifier to get back under. If you press Draw Card or Hold while still over the " +
        "target, you bust and lose the set when the turn resolves.\n\n" +
        "HOW A SET ENDS\n" +
        "Once both players have pressed Draw Card or Hold, the turn resolves:\n" +
        "- Anyone over the target busts. If both bust, the set is a tie and is replayed.\n" +
        "- If both players are holding, the higher score wins the set. Equal scores tie and the set " +
        "is replayed.\n" +
        "- Otherwise the next turn is dealt to everyone who isn't holding.\n\n" +
        "Filling all 9 board slots without busting is still a good place to be - hold!\n\n" +
        "THE BOT\n" +
        "It plays the same rules as you and decides at the same time as you do - you never wait for " +
        "it. It gets sharper as you climb: the early opponents play their own Modifiers, " +
        "the last ones play yours.\n\n" +
        "THE CLIMB\n" +
        "Ten matches, each against a tougher opponent at a different target. Winning pays medals; " +
        "medals buy Modifiers in the market between matches; the Modifiers you own are slotted into " +
        "a deck of 12, and 4 of those 12 are dealt to you each match. Losing a match ends the run - " +
        "but nothing you own is ever taken away.\n\n" +
        "SPECIAL MODIFIERS\n" +
        "Most rungs of the climb introduce one new Modifier that does something other than add a " +
        "number - changing a card, taking a score, undoing a play. Each one is explained the first " +
        "time you meet it, and the market sells it to you straight afterwards. There is nothing to " +
        "memorise here: you will always have met a Modifier before you can buy it.";

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

        ColorRect dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _howToPlayOverlay.AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _howToPlayPanel = new PanelContainer();
        StyleBoxFlat style = new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.14f, 0.2f, 0.98f),
            BorderColor = new Color(0.55f, 0.65f, 0.8f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(12);
        style.SetContentMarginAll(20);
        _howToPlayPanel.AddThemeStyleboxOverride("panel", style);
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

    /// Which of the two texts this screen is showing. Against the bot, the long one; with two
    /// people at one device, the short one. From the start menu - before a mode has been picked -
    /// the short one too: somebody who has not started yet wants to know what the game IS, and the
    /// tutorial will teach them the rest at the table.
    private string HowToPlayForThisMode() =>
        (_host.GameStarted && _host.VsBot) ? HowToPlayFull : HowToPlayShort;

    public void ShowHowToPlay()
    {
        if (_howToPlayOverlay == null) return;
        if (_howToPlayRules != null) _howToPlayRules.Text = HowToPlayForThisMode();
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
