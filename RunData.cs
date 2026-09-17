using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The single piece of state that outlives a scene change: everything about the player's current
/// run down the ladder, plus the permanent collection that outlives it. Registered as an autoload
/// (see project.godot), so the table, the shop and the deck screen all read and write the same
/// instance.
///
/// Local 2-player never touches this - that mode stays a self-contained match with the randomized
/// hands dealt by Player.DealRandomModifiers.
/// </summary>
public partial class RunData : Node
{
    public static RunData Instance { get; private set; }

    public const int SideDeckSize = 12;   // the deck holds exactly this many
    public const int MatchModifierCount = 4;   // ...and this many are drawn from it each match
    private const string SavePath = "user://run.json";

    // ------------------------------------------------------------------
    // A modifier card as it is stored between matches. Card itself is a runtime object tied to a
    // match; this is the durable description the save file round-trips.
    // ------------------------------------------------------------------
    public readonly struct ModifierDef
    {
        public readonly int Value;
        public readonly bool CanFlipValue;
        public readonly CardEffect Effect;

        public ModifierDef(int value, bool canFlipValue = false, CardEffect effect = CardEffect.None)
        {
            Value = value;
            CanFlipValue = canFlipValue;
            Effect = effect;
        }

        public Card ToCard() => new Card(Value, CardType.Modifier, CardName, CanFlipValue, Effect);

        private string CardName => Effect == CardEffect.None ? "" : CardEffects.Label(Effect);

        public string Label => Effect != CardEffect.None
            ? CardEffects.Label(Effect)
            : (CanFlipValue ? "±" : (Value > 0 ? "+" : "-")) + Math.Abs(Value);
    }

    // ------------------------------------------------------------------
    // One rung of the linear ladder.
    //
    // There are no invented venue names (Alexander's call, 2026-09-07): a place called the "Neon
    // Underground" tells the player nothing they can see. Progress is shown instead - each RANK
    // repaints the table and the standard cards, so two rungs in you are looking at a different
    // board. Rank is also the whole difficulty story: it sets the target score.
    // ------------------------------------------------------------------
    public readonly struct LadderStep
    {
        public readonly int Rank;            // index into Ranks
        public readonly string Opponent;
        public readonly int TargetScore;
        public readonly int MedalReward;

        /// Stage 1 is the standard game and the AI's hand holds no "+/-" cards; every stage above
        /// it guarantees the AI exactly one.
        public readonly bool AiHasFlipValueCards;

        /// The one effect card in the AI's four-card hand at this rung, or None. The AI's hand
        /// lasts the whole match, so one effect card is about one dramatic moment per match.
        public readonly CardEffect AiEffect;

        /// The finale: target and effect cards are rolled when the player arrives on this rung
        /// (RunData.EnsureRuleset). TargetScore is then only a fallback that nothing should reach.
        public readonly bool Randomised;

        public LadderStep(int rank, string opponent, int targetScore, int medalReward,
                          bool aiHasFlipValueCards = true, CardEffect aiEffect = CardEffect.None,
                          bool randomised = false)
        {
            Randomised = randomised;
            Rank = rank;
            Opponent = opponent;
            TargetScore = targetScore;
            MedalReward = medalReward;
            AiHasFlipValueCards = aiHasFlipValueCards;
            AiEffect = aiEffect;
        }
    }

    /// A rank of the ladder: what it is called, what the table looks like, and how the standard
    /// (main deck) cards are tinted while you are in it. Two rungs per rank, so the board changes
    /// every other match and the player can SEE how far up they are.
    public readonly struct Rank
    {
        public readonly string Name;
        public readonly Color Table;      // the felt behind everything
        public readonly Color CardTint;   // multiplied into the standard card art

        public Rank(string name, Color table, Color cardTint)
        {
            Name = name;
            Table = table;
            CardTint = cardTint;
        }
    }

    private static readonly Rank[] Ranks =
    {
        new Rank("Bronze",   new Color(0.07f, 0.24f, 0.13f), new Color(1.00f, 1.00f, 1.00f)),
        new Rank("Silver",   new Color(0.10f, 0.20f, 0.26f), new Color(0.86f, 1.00f, 1.12f)),
        new Rank("Gold",     new Color(0.18f, 0.16f, 0.06f), new Color(1.28f, 1.08f, 0.55f)),
        new Rank("Ruby",     new Color(0.22f, 0.07f, 0.10f), new Color(1.30f, 0.74f, 0.74f)),
        new Rank("Obsidian", new Color(0.10f, 0.07f, 0.16f), new Color(0.86f, 0.72f, 1.20f)),
    };

    /// The gauntlet. Difficulty escalates by moving the target away from the comfortable 20 - never
    /// by inflating the arithmetic (the 5-to-85 accessibility tenet rules out multiplier math).
    ///
    /// EVERY CARD MOVED UP A RUNG on 2026-09-11 (Alexander's call), closing the hole Trade Draw
    /// left at stage 5. Trade Totals 6 to 5, Shave 7 to 6, Trade Hands 8 to 7 - so every rung from
    /// 4 to 7 still introduces exactly one new idea, which is the rule the whole ladder is built
    /// on. Stage 8 is Recall and stage 9 is Veto (2026-09-13), so every rung from 1 to 9 now
    /// introduces exactly one new thing and stage 10 is the single randomised finale. The
    /// randomiser held two rungs only because that is what the table happened to have; after nine
    /// rungs of learning, one boss rung that tests all of it is the better shape.
    ///
    /// Veto is not wired yet (CardEffects.Wired), so stage 9 still falls back to a card at or
    /// below its rung until pass 7 turns it on.
    ///
    /// Note what moved WITH the cards and what did not: the targets belong to the RANKS, not to
    /// the cards, so Shave is now met at a target of 18 rather than 24. Worth watching at the
    /// table - Shave punishes holding below the target, and there is less room to hold below 18.
    private static readonly LadderStep[] Ladder =
    {
        //             rank  opponent               target medals  +/-    effect introduced here
        new LadderStep(0, "Bronze Challenger",   20,  3, false),                            // 1 standard rules
        new LadderStep(0, "Bronze Champion",     20,  3, true),                             // 2 the AI gets +/-
        new LadderStep(1, "Silver Challenger",   23,  4, true),                             // 3 the target moves
        new LadderStep(1, "Silver Champion",     23,  5, true, CardEffect.Copy),            // 4
        new LadderStep(2, "Gold Challenger",     18,  5, true, CardEffect.TradeTotals),     // 5
        new LadderStep(2, "Gold Champion",       18,  6, true, CardEffect.Shave),           // 6
        new LadderStep(3, "Ruby Challenger",     24,  6, true, CardEffect.TradeHands),      // 7
        new LadderStep(3, "Ruby Champion",       24,  8, true, CardEffect.Recall),          // 8
        new LadderStep(4, "Obsidian Challenger", 22,  8, true, CardEffect.Veto),            // 9
        new LadderStep(4, "Obsidian Champion",   25, 10, true, randomised: true),           // 10 ruleset rolled
    };

    public static int LadderLength => Ladder.Length;

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

    /// Every card TYPE this player has been introduced to - see CardEffects.MetKey for what a key
    /// is. Profile level, for the same reason TutorialSeen is: meeting a card is something that
    /// happened to the player, not to a run, and a lost run must not un-teach it.
    ///
    /// This is also the set a collection log will read. It is keyed by string rather than by
    /// CardEffect so it can hold the "+/-" card, which is not an effect at all, and later the
    /// plain magnitudes - without renumbering anything already written to a save.
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

    // ------------------------------------------------------------------
    // The collection log (playtest-feedback-family.md §5.2)
    //
    // Read straight from CardsMet - the same set the coach-marks use, so an effect card is "met"
    // on exactly the occasion the game explained it. Plain and flip-value Modifiers need no
    // explanation, so they get their own keys ("+3", "-3", "flip3") and are marked when they
    // enter the collection, are dealt to you, or are played at you.
    // ------------------------------------------------------------------
    public const int LogMaxMagnitude = 6;   // the market's own ceiling (ShopOverlay.RollOffers)

    private static readonly CardEffect[] LogEffects =
    {
        CardEffect.Copy, CardEffect.TradeTotals, CardEffect.Shave,
        CardEffect.TradeHands, CardEffect.Recall, CardEffect.Veto,
    };

    /// Every entry, in display order: four rows of six - plus, minus, flip value, special.
    public static readonly string[] CollectionKeys = BuildCollectionKeys();

    private static string[] BuildCollectionKeys()
    {
        List<string> keys = new List<string>();
        for (int m = 1; m <= LogMaxMagnitude; m++) keys.Add(LogKey(m, false, CardEffect.None));
        for (int m = 1; m <= LogMaxMagnitude; m++) keys.Add(LogKey(-m, false, CardEffect.None));
        for (int m = 1; m <= LogMaxMagnitude; m++) keys.Add(LogKey(m, true, CardEffect.None));
        foreach (CardEffect effect in LogEffects) keys.Add(LogKey(0, false, effect));
        return keys.ToArray();
    }

    /// The effect's NAME for an effect card (the same key CardEffects.MetKey gives it), otherwise
    /// the signed magnitude. Null for a card that is not a Modifier at all.
    public static string LogKey(int value, bool canFlipValue, CardEffect effect)
    {
        if (effect != CardEffect.None) return effect.ToString();
        int magnitude = Math.Abs(value);
        if (magnitude == 0) return null;
        if (canFlipValue) return "flip" + magnitude;
        return (value > 0 ? "+" : "-") + magnitude;
    }

    public static string LogKey(Card card) =>
        (card == null || card.Type != CardType.Modifier) ? null : LogKey(card.Value, card.CanFlipValue, card.Effect);

    /// The card a log key stands for, so the screen can draw it.
    public static ModifierDef CollectionEntry(string key)
    {
        if (key.StartsWith("flip")) return new ModifierDef(int.Parse(key.Substring(4)), canFlipValue: true);
        if (key[0] == '+' || key[0] == '-') return new ModifierDef(int.Parse(key));
        return new ModifierDef(0, false, Enum.Parse<CardEffect>(key));
    }

    public int CollectionFound
    {
        get
        {
            int found = 0;
            foreach (string key in CollectionKeys) if (CardsMet.Contains(key)) found++;
            return found;
        }
    }

    public bool CollectionComplete => CollectionFound == CollectionKeys.Length;

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

    /// Marks a plain or flip-value Modifier as met. Effect cards are left to the coach-marks,
    /// which mark them when they are explained - marking one here first would skip its explanation.
    public bool MarkPlainModifierMet(Card card)
    {
        if (card == null || card.Effect != CardEffect.None) return false;
        return MarkCardMet(LogKey(card));
    }

    /// Set by the deck screen just before the table scene is reloaded for the next rung, so the player
    /// walks straight into the match instead of landing back on a Start button. Deliberately not
    /// saved: it is about this reload, not about the run. (This autoload survives the reload.)
    public bool AutoStartNextMatch { get; set; }
    public int StepIndex { get; private set; }              // 0-based rung of the ladder

    /// Every modifier card the player owns. Cards are only ever added (there is no selling), so an
    /// index into this list is a stable id - which is what SideDeck stores.
    public List<ModifierDef> Inventory { get; } = new List<ModifierDef>();

    /// Indices into Inventory. Exactly SideDeckSize of them once the deck has been confirmed.
    public List<int> SideDeck { get; } = new List<int>();

    private readonly Random _random = new Random();

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

    public override void _Ready()
    {
        Instance = this;
        Load();
    }

    // ------------------------------------------------------------------
    // The ladder
    // ------------------------------------------------------------------
    public LadderStep CurrentStep => Ladder[Mathf.Clamp(StepIndex, 0, Ladder.Length - 1)];
    public int CurrentTarget =>
        (CurrentStep.Randomised && RolledStep == StepIndex) ? RolledTarget : CurrentStep.TargetScore;
    public Rank CurrentRank => Ranks[Mathf.Clamp(CurrentStep.Rank, 0, Ranks.Length - 1)];
    public int MatchNumber => Mathf.Clamp(StepIndex, 0, Ladder.Length - 1) + 1;
    public bool RunComplete => !Endless && StepIndex >= Ladder.Length;

    public string CurrentOpponent => Endless ? $"Endless Challenger {EndlessStreak + 1}" : CurrentStep.Opponent;

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

    /// The target range widens as the streak grows - one step further from 20 on each side every
    /// two wins - so a long streak is harder arithmetic, never bigger multipliers. Capped where
    /// a 9-slot board and a shared 40-card deck still comfortably reach it.
    public static (int min, int max) EndlessTargetRange(int streak)
    {
        int widen = streak / 2;
        return (Math.Max(15, 18 - widen), Math.Min(30, 25 + widen));
    }

    public void StartEndless()
    {
        StartNewRun();          // the collection check, the deck repair, the cleared roll
        Endless = true;
        EndlessStreak = 0;
        StepIndex = Ladder.Length - 1;
        EnsureRuleset();
        Save();
    }

    /// The target of the rung below this one - what the player has been playing to until now.
    public int PreviousTarget => StepIndex > 0 ? StepAt(StepIndex - 1).TargetScore : CurrentTarget;

    /// True when stepping onto this rung MOVED the target. Difficulty on this ladder is the
    /// target moving away from a comfortable 20, so the one thing the table must not do is change
    /// that number quietly - the player has to be told, on the rung where it happens.
    public bool TargetMovedThisStage => StepIndex > 0 && CurrentTarget != PreviousTarget;

    // ------------------------------------------------------------------
    // The randomised finale (stage-ladder-spec.md, "Stage 9+")
    //
    // Rolled when the player ARRIVES on the rung and saved with the run, so quitting and resuming
    // plays the same match rather than re-rolling until the dice are kind. Endless mode rolls with
    // the same method.
    // ------------------------------------------------------------------
    public readonly struct Ruleset
    {
        public readonly int Target;
        public readonly CardEffect[] Effects;
        public Ruleset(int target, CardEffect[] effects) { Target = target; Effects = effects; }
    }

    /// The rung the saved roll belongs to, or -1. A roll for a rung the player is not on is stale.
    public int RolledStep { get; private set; } = -1;
    public int RolledTarget { get; private set; }
    public List<CardEffect> RolledEffects { get; } = new List<CardEffect>();

    /// The finale's rules, if the player is standing on it; otherwise null.
    public List<CardEffect> CurrentRolledEffects =>
        (CurrentStep.Randomised && RolledStep == StepIndex) ? RolledEffects : null;

    /// A target away from the familiar 20, and two different effect cards - never Copy with
    /// Trade Totals, which are both "the AI undoes the draw that ruined it" and together read as
    /// the game cheating rather than as two rules.
    public static Ruleset RollRuleset(Random rng, int minTarget = 18, int maxTarget = 25)
    {
        int target;
        do target = rng.Next(minTarget, maxTarget + 1); while (target == 20 && minTarget < maxTarget);

        List<CardEffect> pool = CardEffects.WiredEffects();
        CardEffect first = pool[rng.Next(pool.Count)];
        pool.Remove(first);
        if (first == CardEffect.Copy) pool.Remove(CardEffect.TradeTotals);
        if (first == CardEffect.TradeTotals) pool.Remove(CardEffect.Copy);
        if (pool.Count == 0) return new Ruleset(target, new[] { first });
        CardEffect second = pool[rng.Next(pool.Count)];

        // Stage order, so the finale names them the way the ladder taught them.
        CardEffect[] effects = { first, second };
        Array.Sort(effects, (a, b) => StageThatIntroduces(a).CompareTo(StageThatIntroduces(b)));
        return new Ruleset(target, effects);
    }

    /// Rolls the current rung's rules if it is randomised and has not been rolled. Safe to call
    /// any number of times: the second call is a no-op, which is the whole point.
    public void EnsureRuleset()
    {
        if (!CurrentStep.Randomised || RolledStep == StepIndex) return;

        Ruleset rolled;
        if (Endless)
        {
            (int min, int max) = EndlessTargetRange(EndlessStreak);
            rolled = RollRuleset(_random, min, max);
        }
        else
        {
            rolled = RollRuleset(_random);
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

    /// Starts a run at the bottom of the ladder. The LADDER resets; the COLLECTION does not.
    ///
    /// Losing must not wipe singleplayer progress (Alexander, 2026-09-07): every card the player
    /// has unlocked, the deck they built out of it, and their banked medals all survive a lost run
    /// and carry into the next one. What a loss costs is the climb, not the cards.
    public void StartNewRun()
    {
        RunActive = true;
        StepIndex = 0;
        Endless = false;
        EndlessStreak = 0;
        MatchRescueUsed = false;
        ClearRuleset();

        if (Inventory.Count == 0) Inventory.AddRange(StarterCollection);
        foreach (ModifierDef def in Inventory) CardsMet.Add(LogKey(def.Value, def.CanFlipValue, def.Effect));

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
            // A medal per set taken, plus the rung's purse - a clean 2-0 is worth keeping.
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
    public int ClearedStepIndex => Mathf.Clamp(StepIndex - 1, 0, Ladder.Length - 1);

    public LadderStep StepAt(int index) => Ladder[Mathf.Clamp(index, 0, Ladder.Length - 1)];

    /// The stage number (1-based) that introduces this effect, or 0 if no stage does.
    public static int StageThatIntroduces(CardEffect effect)
    {
        for (int i = 0; i < Ladder.Length; i++)
        {
            if (Ladder[i].AiEffect == effect) return i + 1;
        }
        return 0;
    }

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
        int stage = StageThatIntroduces(effect);
        return stage > 0 && StepIndex >= stage;
    }

    // ------------------------------------------------------------------
    // What local 2-player may offer (pass 21): only what single player has shown the player.
    // Profile level (FurthestStep), not this run - these are things the player has SEEN, and a
    // lost run does not unsee them.
    // ------------------------------------------------------------------

    /// True once the player has reached a fixed-target rung that plays to this target.
    public bool TargetReached(int target)
    {
        int last = Mathf.Min(FurthestStep, Ladder.Length - 1);
        for (int i = 0; i <= last; i++)
        {
            if (!Ladder[i].Randomised && Ladder[i].TargetScore == target) return true;
        }
        return false;
    }

    /// The playable effect cards whose introducing rung the player has reached, in ladder order.
    public List<CardEffect> MetEffects()
    {
        List<CardEffect> met = new List<CardEffect>();
        foreach (CardEffect effect in CardEffects.WiredEffects())
        {
            int stage = StageThatIntroduces(effect);
            if (stage > 0 && stage - 1 <= FurthestStep) met.Add(effect);
        }
        met.Sort((a, b) => StageThatIntroduces(a).CompareTo(StageThatIntroduces(b)));
        return met;
    }

    public List<CardEffect> UnlockedEffects()
    {
        List<CardEffect> unlocked = new List<CardEffect>();
        foreach (LadderStep step in Ladder)
        {
            if (step.AiEffect != CardEffect.None && !unlocked.Contains(step.AiEffect)
                && EffectUnlocked(step.AiEffect))
            {
                unlocked.Add(step.AiEffect);
            }
        }
        return unlocked;
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
        StepIndex = Mathf.Clamp(stepIndex, 0, Ladder.Length - 1);
        if (StepIndex > FurthestStep) FurthestStep = StepIndex;
        ClearRuleset();
        EnsureRuleset(); // a debug jump onto the finale re-rolls it, which is what testing wants
        Save();
    }

    /// Back to a brand new player: no collection, no deck, no medals, no run.
    public void DebugWipeSave()
    {
        RunActive = false;
        Medals = 0;
        StepIndex = 0;
        FurthestStep = 0;
        Endless = false;
        EndlessStreak = 0;
        EndlessBest = 0;
        MatchRescueUsed = false;
        ClearRuleset();
        TutorialSeen = false; // a wiped save IS a first launch, tutorial included
        CardsMet.Clear();
        CollectorBack = false;
        Inventory.Clear();
        SideDeck.Clear();
        if (FileAccess.FileExists(SavePath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(SavePath));
        Save();
    }

    public void SpendMedals(int amount) { Medals = Math.Max(0, Medals - amount); Save(); }

    /// Adds a bought card to the collection and returns its index - which is its permanent id,
    /// because the collection is append-only (there is no selling).
    public int AddToInventory(ModifierDef def)
    {
        Inventory.Add(def);
        // Bought is met. For an effect card that is already true - the market only sells a card
        // you have been shown - so this never skips an explanation.
        bool wasComplete = CollectionComplete;
        CardsMet.Add(LogKey(def.Value, def.CanFlipValue, def.Effect));
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

    // ------------------------------------------------------------------
    // Dealing a match hand
    // ------------------------------------------------------------------
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
    // Save / load
    //
    // Godot's own Json + FileAccess rather than System.Text.Json: no reflection, so nothing breaks
    // under the AOT trimming used for the iOS and Android builds.
    // ------------------------------------------------------------------
    public void Save()
    {
        Godot.Collections.Array inventory = new Godot.Collections.Array();
        foreach (ModifierDef def in Inventory)
        {
            inventory.Add(new Godot.Collections.Dictionary
            {
                { "value", def.Value },
                { "flip", def.CanFlipValue },
                { "effect", (int)def.Effect },
            });
        }

        Godot.Collections.Array sideDeck = new Godot.Collections.Array();
        foreach (int index in SideDeck) sideDeck.Add(index);

        Godot.Collections.Array rolledEffects = new Godot.Collections.Array();
        foreach (CardEffect effect in RolledEffects) rolledEffects.Add((int)effect);

        Godot.Collections.Array cardsMet = new Godot.Collections.Array();
        foreach (string key in CardsMet) cardsMet.Add(key);

        Godot.Collections.Dictionary data = new Godot.Collections.Dictionary
        {
            { "version", 7 },
            { "active", RunActive },
            { "medals", Medals },
            { "step", StepIndex },
            { "furthest", FurthestStep },
            { "tutorialSeen", TutorialSeen },
            { "cardsMet", cardsMet },
            { "collectorBack", CollectorBack },
            { "endless", Endless },
            { "endlessStreak", EndlessStreak },
            { "endlessBest", EndlessBest },
            { "matchRescueUsed", MatchRescueUsed },
            { "rolledStep", RolledStep },
            { "rolledTarget", RolledTarget },
            { "rolledEffects", rolledEffects },
            { "inventory", inventory },
            { "sideDeck", sideDeck },
        };

        using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            GD.PushWarning($"Could not write the run save: {FileAccess.GetOpenError()}");
            return;
        }
        file.StoreString(Json.Stringify(data));
    }

    public void Load()
    {
        if (!FileAccess.FileExists(SavePath)) return;

        using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
        if (file == null) return;

        Variant parsed = Json.ParseString(file.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) return;

        Godot.Collections.Dictionary data = parsed.AsGodotDictionary();

        RunActive = data.TryGetValue("active", out Variant active) && active.AsBool();
        Medals = data.TryGetValue("medals", out Variant medals) ? medals.AsInt32() : 0;
        StepIndex = data.TryGetValue("step", out Variant step) ? step.AsInt32() : 0;
        FurthestStep = data.TryGetValue("furthest", out Variant furthest) ? furthest.AsInt32() : StepIndex;
        TutorialSeen = data.TryGetValue("tutorialSeen", out Variant taught) && taught.AsBool();
        Endless = data.TryGetValue("endless", out Variant endless) && endless.AsBool();
        EndlessStreak = data.TryGetValue("endlessStreak", out Variant streak) ? streak.AsInt32() : 0;
        EndlessBest = data.TryGetValue("endlessBest", out Variant best) ? best.AsInt32() : 0;
        // Version 7. A version 6 save carried "endlessRescueUsed" (once per endless RUN), which
        // no longer means anything; it is ignored, and a missing key loads as a fresh match.
        MatchRescueUsed = data.TryGetValue("matchRescueUsed", out Variant rescued) && rescued.AsBool();
        if (Endless) StepIndex = Ladder.Length - 1;

        // A version 4 save has no key and loads as an empty set, so an existing player is
        // introduced to each card once more. That is the right way round: the alternative is
        // assuming they have met cards nobody ever showed them.
        CardsMet.Clear();
        if (data.TryGetValue("cardsMet", out Variant met))
        {
            foreach (Variant entry in met.AsGodotArray())
            {
                string key = entry.AsString();
                if (!string.IsNullOrEmpty(key)) CardsMet.Add(key);
            }
        }

        Inventory.Clear();
        if (data.TryGetValue("inventory", out Variant inventoryVariant))
        {
            foreach (Variant entry in inventoryVariant.AsGodotArray())
            {
                Godot.Collections.Dictionary card = entry.AsGodotDictionary();
                int value = card.TryGetValue("value", out Variant v) ? v.AsInt32() : 0;
                bool flip = card.TryGetValue("flip", out Variant f) && f.AsBool();

                // A version 2 save has no "effect" key at all, which reads as None - so an older
                // collection loads unchanged rather than being thrown away.
                // Range-checked: an out-of-range int would otherwise become an undefined effect
                // that renders blank, can never be played, and sits in a deck slot forever.
                int rawEffect = card.TryGetValue("effect", out Variant e) ? e.AsInt32() : 0;
                // Enum.IsDefined rather than a hand-written upper bound. The bound used to read
                // "<= (int)CardEffect.Copy", which was correct only for as long as Copy happened
                // to be the last member - appending Recall and Veto would have made every saved
                // copy of them load as None and silently vanish from the player's collection,
                // with no error anywhere. This version is right for every future card too.
                CardEffect effect = (rawEffect > 0 && Enum.IsDefined(typeof(CardEffect), rawEffect))
                    ? (CardEffect)rawEffect
                    : CardEffect.None;

                // Push was scrapped (2026-09-10) and Copy took its stage 4 slot. A Push already in
                // someone's collection becomes a Copy rather than a card that no longer exists:
                // it keeps its place in the deck, and what the player owns is still "the stage 4
                // effect card". Copy carries no number, so the old rolled value goes with it.
                //
                // Cards are never TAKEN away - that rule is what makes losing a run survivable,
                // and it applies just as much when the design changes underneath a card.
                // Trade Draw was removed the next day (2026-09-11) for overlapping Copy. It was
                // never wired and so never buyable, which means no honest save can hold one - but
                // a hand-edited or half-migrated file could, and a card nothing can play is worse
                // than a card that plays as its replacement.
                if (effect == CardEffect.Push || effect == CardEffect.TradeDraw)
                {
                    effect = CardEffect.Copy;
                    value = 0;
                }

                // Effect cards are worth 0 - none of them carries a number - so the "value != 0"
                // guard against junk rows only applies to ordinary modifiers.
                if (value != 0 || effect != CardEffect.None) Inventory.Add(new ModifierDef(value, flip, effect));
            }
        }

        // Version 6 adds the collection log. An older save knows nothing of plain magnitudes, but
        // everything in the collection has plainly been met, so the log starts from what is owned
        // rather than from nothing.
        foreach (ModifierDef def in Inventory) CardsMet.Add(LogKey(def.Value, def.CanFlipValue, def.Effect));
        CollectorBack = data.TryGetValue("collectorBack", out Variant gilded)
            ? gilded.AsBool()
            : CollectionComplete;

        // The finale's roll. Anything unreadable is dropped and re-rolled on arrival, which only
        // costs the player a different - still fair - set of rules.
        ClearRuleset();
        if (data.TryGetValue("rolledStep", out Variant rolledStep) && rolledStep.AsInt32() >= 0
            && data.TryGetValue("rolledEffects", out Variant rolledEffects))
        {
            foreach (Variant entry in rolledEffects.AsGodotArray())
            {
                int raw = entry.AsInt32();
                if (Enum.IsDefined(typeof(CardEffect), raw) && CardEffects.IsWired((CardEffect)raw))
                    RolledEffects.Add((CardEffect)raw);
            }
            RolledTarget = data.TryGetValue("rolledTarget", out Variant t) ? t.AsInt32() : 0;
            RolledStep = (RolledEffects.Count > 0 && RolledTarget > 0) ? rolledStep.AsInt32() : -1;
            if (RolledStep < 0) ClearRuleset();
        }

        SideDeck.Clear();
        if (data.TryGetValue("sideDeck", out Variant deckVariant))
        {
            foreach (Variant entry in deckVariant.AsGodotArray())
            {
                int index = entry.AsInt32();
                if (index >= 0 && index < Inventory.Count) SideDeck.Add(index);
            }
        }

        // A save that lost its cards (a failed write, a hand-edited file) is not recoverable as a
        // run - drop back to "no run" rather than starting a match with an empty hand. StartNewRun
        // then rebuilds the collection from the starters.
        if (RunActive && (Inventory.Count == 0 || SideDeck.Count == 0)) RunActive = false;
    }
}
