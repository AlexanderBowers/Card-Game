using System;
using System.Collections.Generic;

/// Everything about the player that outlives a scene change: the current run down the ladder,
/// plus the permanent profile that outlives the run (collection, deck, medals, records, cosmetics).
///
/// Plain C#, no Godot: the RunStore autoload creates the one instance, loads it from disk, and
/// writes it back whenever Changed fires. That keeps every rule in here unit-testable - a test
/// just news one up, and can round-trip it through the save format (RunData.Save.cs). The
/// fixed data the run is measured against is in Ladder, CollectionLog, Cosmetics, Ruleset and
/// EndlessRules.
///
/// Local 2-player never touches this - that mode stays a self-contained match with the randomized
/// hands dealt by Player.DealRandomModifiers.
public partial class RunData
{
    /// The live profile, set by RunStore when the game boots.
    public static RunData Instance { get; internal set; }

    public const int SideDeckSize = 12;   // the deck holds exactly this many
    public const int MatchModifierCount = 4;   // ...and this many are drawn from it each match

    /// Raised after every change worth keeping. RunStore writes the save file on it.
    public event Action Changed;

    /// Seconds since the epoch, for dating scoreboard rows. Swappable so tests can pin it.
    public Func<long> Clock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private readonly Random _random;

    public RunData(Random random = null) => _random = random ?? new Random();

    private void Save() => Changed?.Invoke();

    // ------------------------------------------------------------------
    // Run state
    // ------------------------------------------------------------------
    public bool RunActive { get; private set; }
    public int Medals { get; private set; }

    /// The highest rung ever reached, across every run. Nothing gates on it yet - it is the
    /// player's record, and the proof on the losing screen that a loss did not erase them.
    public int FurthestStep { get; private set; }

    /// Whether this player has been walked through the table once.
    ///
    /// PROFILE level, not run level - what the player has been TAUGHT survives a lost run, exactly
    /// as the collection does. StartNewRun deliberately leaves it alone, and that is the one line
    /// worth guarding: clearing it there would replay the tutorial at the top of every new run, on
    /// a player who has already climbed nine rungs.
    ///
    /// A version 3 save has no key for this and so loads as false. That is right rather than
    /// unfortunate: the tutorial only ever triggers on stage 1 of a fresh run, so an existing
    /// player mid-ladder never sees it, and one who starts over gets a walkthrough of a table that
    /// has changed a great deal since they last read anything about it.
    public bool TutorialSeen { get; private set; }

    /// The first Market visit has been walked through: what medals are, what a price is, buying
    /// the guaranteed +/-1 and putting it in the deck (playtest, 2026-10-07: "the first time a
    /// player encounters the Modifier Shop can be confusing"). Profile level, like TutorialSeen.
    public bool MarketLessonSeen { get; private set; }

    /// The Market lesson put a +/-1 in the deck, and the table has not yet shown how to flip it.
    /// Saved, because the next match is a scene reload away.
    public bool FlipLessonPending { get; private set; }

    public void CompleteMarketLesson(bool flipCardInDeck)
    {
        MarketLessonSeen = true;
        FlipLessonPending = flipCardInDeck;
        Save();
    }

    public void CompleteFlipLesson()
    {
        if (!FlipLessonPending) return;
        FlipLessonPending = false;
        Save();
    }

    /// Set by the deck screen just before the table scene is reloaded for the next rung, so the player
    /// walks straight into the match instead of landing back on a Start button. Deliberately not
    /// saved: it is about this reload, not about the run. (This object survives the reload.)
    public bool AutoStartNextMatch { get; set; }
    public int StepIndex { get; private set; }              // 0-based rung of the ladder

    /// Every modifier card the player owns. Cards are only ever added (there is no selling), so an
    /// index into this list is a stable id - which is what SideDeck stores.
    public List<ModifierDef> Inventory { get; } = new List<ModifierDef>();

    /// Indices into Inventory. Exactly SideDeckSize of them once the deck has been confirmed.
    public List<int> SideDeck { get; } = new List<int>();

    /// What a player owns before they have ever bought anything: enough that the deck screen is a
    /// real choice from the first visit (14 owned, 12 slotted). Handed out once, not once per run.
    ///
    /// Plain arithmetic only. A "+/-" card is a STORE card (Alexander, 2026-09-07): stage 2 is the
    /// rung that introduces it - you meet one across the table, then the stage 2 market sells you
    /// your first one. Handing one out at the start spends that introduction before it happens.
    private static readonly ModifierDef[] StarterCollection =
    {
        new ModifierDef(1), new ModifierDef(1), new ModifierDef(2), new ModifierDef(2),
        new ModifierDef(3), new ModifierDef(3), new ModifierDef(4),
        new ModifierDef(-1), new ModifierDef(-1), new ModifierDef(-2), new ModifierDef(-2),
        new ModifierDef(-3), new ModifierDef(-3), new ModifierDef(-4),
    };

    // ------------------------------------------------------------------
    // Cards met, and the collection log built on them
    // ------------------------------------------------------------------

    /// Every card TYPE this player has been introduced to - see CardEffects.MetKey for what a key
    /// is. Profile level, for the same reason TutorialSeen is: meeting a card is something that
    /// happened to the player, not to a run, and a lost run must not un-teach it.
    ///
    /// This is also the set the collection log reads. It is keyed by string rather than by
    /// CardEffect so it can hold the "+/-" card, which is not an effect at all, and the plain
    /// magnitudes - without renumbering anything already written to a save.
    public HashSet<string> CardsMet { get; } = new HashSet<string>();

    public bool HasMetCard(string key) => key == null || CardsMet.Contains(key);

    /// Records a card as met. Returns true only on the call that completes the collection log,
    /// so the table can say so at the moment it happens - and switches the reward on, because a
    /// prize the player has to go and find a toggle for is a prize most players never see.
    public bool MarkCardMet(string key)
    {
        if (key == null) return false;
        bool wasComplete = CollectionComplete;
        if (!CardsMet.Add(key)) return false;

        bool justCompleted = !wasComplete && CollectionComplete;
        if (justCompleted) CollectorBack = true;
        Save();
        return justCompleted;
    }

    /// Marks a plain or flip-value Modifier as met. Effect cards are left to the coach-marks,
    /// which mark them when they are explained - marking one here first would skip its explanation.
    public bool MarkPlainModifierMet(Card card)
    {
        if (card == null || card.Effect != CardEffect.None) return false;
        return MarkCardMet(CollectionLog.Key(card));
    }

    /// The collection log counts what you OWN - a card in your collection (Inventory), bought or
    /// starter - not every card you have merely met (Alexander, 2026-10-05). CardsMet still drives
    /// the coach-marks: being shown a card and owning one are different things.
    public bool OwnsCard(string key) => key != null && Inventory.Exists(def => def.LogKey == key);

    public int CollectionFound
    {
        get
        {
            HashSet<string> owned = new HashSet<string>();
            foreach (ModifierDef def in Inventory) if (def.LogKey != null) owned.Add(def.LogKey);
            return CollectionLog.Found(owned);
        }
    }

    public bool CollectionComplete => CollectionFound == CollectionLog.Keys.Length;

    /// The log's reward: the face-down deck wears a gilded back. Cosmetic, and switchable, because
    /// the rank's own colour on the deck is part of how the ladder shows progress.
    public bool CollectorBack { get; private set; }

    /// True when the gilded back should actually be drawn.
    public bool UseCollectorBack => CollectorBack && CollectionComplete;

    public void SetCollectorBack(bool on)
    {
        if (CollectorBack == on) return;
        CollectorBack = on;
        Save();
    }

    // ------------------------------------------------------------------
    // The ladder
    // ------------------------------------------------------------------
    public LadderStep CurrentStep => Ladder.At(StepIndex);
    public int CurrentTarget =>
        (CurrentStep.Randomised && RolledStep == StepIndex) ? RolledTarget : CurrentStep.TargetScore;
    public int MatchNumber => Ladder.ClampIndex(StepIndex) + 1;
    public bool RunComplete => !Endless && StepIndex >= Ladder.Length;

    public string CurrentOpponent => Endless ? $"Endless Challenger {EndlessStreak + 1}" : CurrentStep.Opponent;

    /// The target of the rung below this one - what the player has been playing to until now.
    public int PreviousTarget => StepIndex > 0 ? Ladder.At(StepIndex - 1).TargetScore : CurrentTarget;

    /// True when stepping onto this rung MOVED the target. Difficulty on this ladder is the
    /// target moving away from a comfortable 20, so the one thing the table must not do is change
    /// that number quietly - the player has to be told, on the rung where it happens.
    public bool TargetMovedThisStage => StepIndex > 0 && CurrentTarget != PreviousTarget;

    // ------------------------------------------------------------------
    // Endless mode (playtest-feedback-family.md §5.1)
    //
    // The finale with no rung above it: the run parks on the last step, every won match re-rolls
    // the rules, and the score is how many matches in a row. Unlocked by clearing the ladder once.
    // Same run machinery throughout - market, deck, medals, saves - so a loss costs exactly what a
    // ladder loss costs: the streak, never the cards.
    // ------------------------------------------------------------------
    public bool Endless { get; private set; }
    public int EndlessStreak { get; private set; }

    /// Profile level: the record survives every run, like FurthestStep.
    public int EndlessBest { get; private set; }

    public bool EndlessUnlocked => FurthestStep >= Ladder.Length;

    // ------------------------------------------------------------------
    // The endless scoreboard (pass 24)
    //
    // EndlessBest is one number, and one number cannot say "I have been close three times". The
    // playtest asked for endless to have "its own high-score list" - so every endless run that
    // ends with a streak on it is written down with the date, and the best five are kept.
    // Local only for now; this is the shape the online list will upload.
    //
    // A run is BANKED once, whenever it stops being playable: lost, given up for a new run, or
    // given up for a fresh endless run. EndlessRunBanked is what stops the same streak landing on
    // the board twice, and it is saved, because quitting the app between the loss and the next
    // menu is an ordinary thing to do.
    // ------------------------------------------------------------------

    /// Best first, and for a tie the more recent run first. Never longer than EndlessRules.ScoreboardSize.
    public List<EndlessScore> EndlessScores { get; } = new List<EndlessScore>();

    /// Whether the endless run in progress (or just lost) has already been written to the board.
    public bool EndlessRunBanked { get; private set; }

    /// Writes the endless run in progress onto the board, if it earned a place. Safe to call from
    /// anywhere a run can end; the second call for the same run does nothing.
    private void BankEndlessRun()
    {
        if (!Endless || EndlessRunBanked || EndlessStreak <= 0) return;
        EndlessRunBanked = true;
        EndlessScores.Add(new EndlessScore(EndlessStreak, Clock()));
        EndlessRules.SortAndTrim(EndlessScores);
    }

    public void StartEndless()
    {
        StartNewRun();          // banks any endless run being given up, then resets the rest
        Endless = true;
        EndlessRunBanked = false;
        EndlessStreak = 0;
        StepIndex = Ladder.Length - 1;
        EnsureRuleset();
        Save();
    }

    // ------------------------------------------------------------------
    // The rescue offer (claude/monetization-spec.md §3)
    //
    // Ladder stages 4-10 and endless: a bust that would lose the set rolls RescueChance, at most
    // one rescue per MATCH. The flag is saved so quitting mid-match cannot hand out a fresh one,
    // and it is only cleared when a match is banked (CompleteMatch) or a new run begins.
    // ------------------------------------------------------------------
    public const int RescueFirstStage = 4;
    public const double RescueChance = 0.15;

    public bool MatchRescueUsed { get; private set; }

    /// Whether the match being played can offer a rescue at all (the roll comes after).
    public bool RescueEligible =>
        RunActive && !MatchRescueUsed && (Endless || StepIndex >= RescueFirstStage - 1);

    public void UseMatchRescue()
    {
        MatchRescueUsed = true;
        Save();
    }

    // ------------------------------------------------------------------
    // The randomised finale (stage-ladder-spec.md, "Stage 9+")
    //
    // Rolled when the player ARRIVES on the rung and saved with the run, so quitting and resuming
    // plays the same match rather than re-rolling until the dice are kind. Endless mode rolls with
    // the same method.
    // ------------------------------------------------------------------

    /// The rung the saved roll belongs to, or -1. A roll for a rung the player is not on is stale.
    public int RolledStep { get; private set; } = -1;
    public int RolledTarget { get; private set; }
    public List<CardEffect> RolledEffects { get; } = new List<CardEffect>();

    /// The finale's rules, if the player is standing on it; otherwise null.
    public List<CardEffect> CurrentRolledEffects =>
        (CurrentStep.Randomised && RolledStep == StepIndex) ? RolledEffects : null;

    /// "Opponent's specials: Copy + Shave" for the finale (and every endless match), after the
    /// prefix; empty on any other rung.
    ///
    /// It said "Rules: Copy + Shave" until a tester asked what those rules were (2026-10-06). They
    /// are not rules at all: they are the special Modifiers the opponent carries this match, so the
    /// line says exactly that. Each card is still explained by its coach mark when it is played.
    public string FinaleRulesLine(string prefix)
    {
        List<CardEffect> rolled = CurrentRolledEffects;
        if (rolled == null || rolled.Count == 0) return string.Empty;
        List<string> names = new List<string>();
        foreach (CardEffect effect in rolled) names.Add(CardEffects.Label(effect));
        return $"{prefix}Opponent's specials: {string.Join(" + ", names)}";
    }

    /// Rolls the current rung's rules if it is randomised and has not been rolled. Safe to call
    /// any number of times: the second call is a no-op, which is the whole point.
    public void EnsureRuleset()
    {
        if (!CurrentStep.Randomised || RolledStep == StepIndex) return;

        Ruleset rolled;
        if (Endless)
        {
            (int min, int max) = EndlessRules.TargetRange(EndlessStreak);
            rolled = Ruleset.Roll(_random, min, max, EndlessRules.RuleCount(EndlessStreak));
        }
        else
        {
            rolled = Ruleset.Roll(_random);
        }
        RolledStep = StepIndex;
        RolledTarget = rolled.Target;
        RolledEffects.Clear();
        RolledEffects.AddRange(rolled.Effects);
        Save();
    }

    private void ClearRuleset()
    {
        RolledStep = -1;
        RolledTarget = 0;
        RolledEffects.Clear();
    }

    // ------------------------------------------------------------------
    // Starting, finishing and ending runs
    // ------------------------------------------------------------------

    /// Starts a run at the bottom of the ladder. The LADDER resets; the COLLECTION does not.
    ///
    /// Losing must not wipe singleplayer progress (Alexander, 2026-09-07): every card the player
    /// has unlocked, the deck they built out of it, and their banked medals all survive a lost run
    /// and carry into the next one. What a loss costs is the climb, not the cards.
    public void StartNewRun()
    {
        BankEndlessRun();       // an endless run being given up still earned its place
        RunActive = true;
        EndlessRunBanked = false;
        StepIndex = 0;
        Endless = false;
        EndlessStreak = 0;
        MatchRescueUsed = false;
        ClearRuleset();

        if (Inventory.Count == 0) Inventory.AddRange(StarterCollection);
        foreach (ModifierDef def in Inventory) CardsMet.Add(def.LogKey);

        // Keep the deck they last built; only fill it in if it is missing or has gone stale.
        SideDeck.RemoveAll(index => index < 0 || index >= Inventory.Count);
        int required = Math.Min(SideDeckSize, Inventory.Count);
        for (int i = 0; SideDeck.Count < required && i < Inventory.Count; i++)
        {
            if (!SideDeck.Contains(i)) SideDeck.Add(i);
        }

        Save();
    }

    /// Called once the walkthrough finishes or is skipped. Skipping counts as seen - a player who
    /// skipped it chose that, and asking again next launch is nagging, not teaching. The table
    /// menu's "Replay the tutorial" is how they get it back.
    public void MarkTutorialSeen()
    {
        if (TutorialSeen) return;
        TutorialSeen = true;
        Save();
    }

    public void ReplayTutorial()
    {
        TutorialSeen = false;
        Save();
    }

    public void EndRun()
    {
        BankEndlessRun();
        RunActive = false;
        Endless = false;
        Save();
    }

    /// Banks the medals for a won match and moves the player up one rung.
    public void CompleteMatch(int setsWon, bool won)
    {
        if (!RunActive) return;

        MatchRescueUsed = false; // the next match gets its own rescue

        if (won && Endless)
        {
            Medals += setsWon + CurrentStep.MedalReward;
            EndlessStreak++;
            if (EndlessStreak > EndlessBest) EndlessBest = EndlessStreak;
            ClearRuleset();
            EnsureRuleset();    // the next match's rules, rolled now so the market can name them
        }
        else if (won)
        {
            // A medal per set taken, plus the rung's purse - a clean 3-0 is worth keeping.
            Medals += setsWon + CurrentStep.MedalReward;
            StepIndex++;
            if (StepIndex > FurthestStep) FurthestStep = StepIndex;
            // Rolled now, not when the match starts, so the market can already say what is next.
            if (!RunComplete) EnsureRuleset();
        }
        else
        {
            // The run ends, and that is ALL it costs: Inventory, SideDeck and Medals are untouched
            // here, and StartNewRun deliberately keeps them.
            //
            // Banked BEFORE RunActive goes false, and the streak is left standing: the run-end
            // screen reads EndlessStreak to say where the run stopped, so zeroing it here would
            // tell the player their streak ended at 0.
            BankEndlessRun();
            RunActive = false;
        }

        Save();
    }

    // ------------------------------------------------------------------
    // What the market may sell
    //
    // Alexander's rule: after clearing a stage the shop stocks cards from that stage or earlier,
    // with at least one card related to the stage just cleared. So you always meet a card across
    // the table before you can own one.
    // ------------------------------------------------------------------

    /// The rung the player has just cleared. CompleteMatch has already moved StepIndex on by the
    /// time the market opens, so "the stage I just played" is the one behind it.
    public int ClearedStepIndex => Ladder.ClampIndex(StepIndex - 1);

    /// Buyable once the player has cleared the stage that introduced it IN THIS RUN.
    ///
    /// Gated on StepIndex, not on FurthestStep: the market stocks "that stage or previous stages",
    /// and that ladder resets when the run does. Gating on the all-time best put the stage 4 effect
    /// card in the stage 2 market of every run after the first time the player got that far, which
    /// read as the shop running ahead of the game (Alexander, 2026-09-07).
    ///
    /// Cards already OWNED are untouched by this: a Copy bought in an earlier run stays in the
    /// collection and can be decked at stage 1. Losing costs the climb, never the cards - you just
    /// cannot buy a NEW one until you have earned your way back to its stage.
    public bool EffectUnlocked(CardEffect effect)
    {
        if (!CardEffects.Implemented(effect)) return false;
        int stage = Ladder.StageThatIntroduces(effect);
        return stage > 0 && StepIndex >= stage;
    }

    public List<CardEffect> UnlockedEffects()
    {
        List<CardEffect> unlocked = new List<CardEffect>();
        for (int i = 0; i < Ladder.Length; i++)
        {
            CardEffect effect = Ladder.At(i).AiEffect;
            if (effect != CardEffect.None && !unlocked.Contains(effect) && EffectUnlocked(effect))
            {
                unlocked.Add(effect);
            }
        }
        return unlocked;
    }

    // ------------------------------------------------------------------
    // What local 2-player may offer (pass 21): only what single player has shown the player.
    // Profile level (FurthestStep), not this run - these are things the player has SEEN, and a
    // lost run does not unsee them.
    // ------------------------------------------------------------------

    /// True once the player has reached a fixed-target rung that plays to this target.
    public bool TargetReached(int target)
    {
        int last = Math.Min(FurthestStep, Ladder.Length - 1);
        for (int i = 0; i <= last; i++)
        {
            LadderStep step = Ladder.At(i);
            if (!step.Randomised && step.TargetScore == target) return true;
        }
        return false;
    }

    /// The playable effect cards whose introducing rung the player has reached, in ladder order.
    public List<CardEffect> MetEffects()
    {
        List<CardEffect> met = new List<CardEffect>();
        foreach (CardEffect effect in CardEffects.WiredEffects())
        {
            int stage = Ladder.StageThatIntroduces(effect);
            if (stage > 0 && stage - 1 <= FurthestStep) met.Add(effect);
        }
        met.Sort((a, b) => Ladder.StageThatIntroduces(a).CompareTo(Ladder.StageThatIntroduces(b)));
        return met;
    }

    // ------------------------------------------------------------------
    // Medals, decks and boards (see Cosmetics for the catalogue)
    // ------------------------------------------------------------------
    public void SpendMedals(int amount) { Medals = Math.Max(0, Medals - amount); Save(); }

    public HashSet<string> OwnedDecks { get; } = new HashSet<string> { Cosmetics.Default };
    public HashSet<string> OwnedBoards { get; } = new HashSet<string> { Cosmetics.Default };
    public string SelectedDeck { get; private set; } = Cosmetics.Default;
    public string SelectedBoard { get; private set; } = Cosmetics.Default;

    /// Classic is always yours. A rank's set: beaten the rank's first stage = reached the rung
    /// after it at least once. Endless's set: cleared the ladder (Endless itself is open).
    public bool CosmeticUnlocked(string key)
    {
        if (key == Cosmetics.Default) return true;
        if (key == Cosmetics.EndlessKey) return EndlessUnlocked;
        int step = Cosmetics.UnlockStepIndex(key);
        return step >= 0 && FurthestStep >= step + 1;
    }

    public bool BuyCosmetic(string key, bool board)
    {
        HashSet<string> owned = board ? OwnedBoards : OwnedDecks;
        int price = Cosmetics.Price(key);
        if (!Cosmetics.IsCosmetic(key) || owned.Contains(key) || !CosmeticUnlocked(key) || Medals < price) return false;
        Medals -= price;
        owned.Add(key);
        if (board) SelectedBoard = key; else SelectedDeck = key; // bought to be used
        Save();
        return true;
    }

    public void SelectCosmetic(string key, bool board)
    {
        if (!(board ? OwnedBoards : OwnedDecks).Contains(key)) return;
        if (board) SelectedBoard = key; else SelectedDeck = key;
        Save();
    }

    // ------------------------------------------------------------------
    // The collection and the 12-card deck
    // ------------------------------------------------------------------

    /// Adds a bought card to the collection and returns its index - which is its permanent id,
    /// because the collection is append-only (there is no selling).
    public int AddToInventory(ModifierDef def)
    {
        // Owning is what fills the collection log now, so completion is decided here.
        bool wasComplete = CollectionComplete;
        Inventory.Add(def);
        // Bought is met. For an effect card that is already true - the market only sells a card
        // you have been shown - so this never skips an explanation.
        CardsMet.Add(def.LogKey);
        if (!wasComplete && CollectionComplete) CollectorBack = true;
        Save();
        return Inventory.Count - 1;
    }

    /// The deck screen's output: the indices the player chose. Anything out of range or repeated is
    /// dropped rather than trusted, so a bad save can never deal a card that isn't there.
    public void SetSideDeck(IEnumerable<int> indices)
    {
        SideDeck.Clear();
        foreach (int index in indices)
        {
            if (index >= 0 && index < Inventory.Count && !SideDeck.Contains(index)) SideDeck.Add(index);
        }
        Save();
    }

    /// Draws MatchModifierCount cards at random from the player's side deck. This is the whole point of
    /// the 12-card deck: the deck is chosen, the hand is not.
    public List<Card> DrawMatchModifiers()
    {
        List<int> pool = new List<int>(SideDeck);
        List<Card> hand = new List<Card>();

        for (int i = 0; i < MatchModifierCount && pool.Count > 0; i++)
        {
            int pick = _random.Next(pool.Count);
            int inventoryIndex = pool[pick];
            pool.RemoveAt(pick);

            if (inventoryIndex >= 0 && inventoryIndex < Inventory.Count)
            {
                hand.Add(Inventory[inventoryIndex].ToCard());
            }
        }

        return hand;
    }

    /// The "No thanks" rescue card (monetization-spec.md §3.2): a COPY of one card from the
    /// 12-card deck, picked at random. The deck and the inventory are untouched. Null only when
    /// the deck is empty, which a live run never is.
    public Card DrawRescueCopy()
    {
        List<int> pool = SideDeck.FindAll(index => index >= 0 && index < Inventory.Count);
        if (pool.Count == 0) return null;
        return Inventory[pool[_random.Next(pool.Count)]].ToCard();
    }

    // ------------------------------------------------------------------
    // Debug
    //
    // Called only from the debug row on the table, which GameManager builds behind
    // OS.IsDebugBuild() - an exported build has no way to reach either of these.
    // ------------------------------------------------------------------

    /// Drops the run onto any rung, for testing a stage without climbing to it.
    public void DebugJumpToStep(int stepIndex)
    {
        if (!RunActive) StartNewRun();
        Endless = false;
        MatchRescueUsed = false;
        // "Stage >" on the finale unlocks endless mode, so it can be tested without a full climb.
        if (stepIndex >= Ladder.Length) FurthestStep = Ladder.Length;
        StepIndex = Ladder.ClampIndex(stepIndex);
        if (StepIndex > FurthestStep) FurthestStep = StepIndex;
        ClearRuleset();
        EnsureRuleset(); // a debug jump onto the finale re-rolls it, which is what testing wants
        Save();
    }

    /// Back to a brand new player: no collection, no deck, no medals, no run. The save that
    /// follows overwrites the file whole, so nothing of the old profile survives on disk.
    public void DebugWipeSave()
    {
        RunActive = false;
        Medals = 0;
        StepIndex = 0;
        FurthestStep = 0;
        Endless = false;
        EndlessStreak = 0;
        EndlessBest = 0;
        EndlessRunBanked = false;
        EndlessScores.Clear();
        MatchRescueUsed = false;
        ClearRuleset();
        TutorialSeen = false; // a wiped save IS a first launch, tutorial included
        MarketLessonSeen = false;
        FlipLessonPending = false;
        CardsMet.Clear();
        CollectorBack = false;
        Inventory.Clear();
        SideDeck.Clear();
        OwnedDecks.Clear(); OwnedDecks.Add(Cosmetics.Default);
        OwnedBoards.Clear(); OwnedBoards.Add(Cosmetics.Default);
        SelectedDeck = SelectedBoard = Cosmetics.Default;
        Save();
    }
}
