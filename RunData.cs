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
/// hands dealt by Player.DealRandomModifierHand.
/// </summary>
public partial class RunData : Node
{
    public static RunData Instance { get; private set; }

    public const int SideDeckSize = 12;   // the deck holds exactly this many
    public const int MatchHandSize = 4;   // ...and this many are drawn from it each match
    private const string SavePath = "user://run.json";

    // ------------------------------------------------------------------
    // A modifier card as it is stored between matches. Card itself is a runtime object tied to a
    // match; this is the durable description the save file round-trips.
    // ------------------------------------------------------------------
    public readonly struct ModifierDef
    {
        public readonly int Value;
        public readonly bool IsFlip;
        public readonly CardEffect Effect;

        public ModifierDef(int value, bool isFlip = false, CardEffect effect = CardEffect.None)
        {
            Value = value;
            IsFlip = isFlip;
            Effect = effect;
        }

        public Card ToCard() => new Card(Value, CardType.Modifier, CardName, IsFlip, Effect);

        private string CardName => Effect == CardEffect.None ? "" : CardEffects.Label(Effect);

        public string Label => Effect != CardEffect.None
            ? CardEffects.Label(Effect)
            : (IsFlip ? "±" : (Value > 0 ? "+" : "-")) + Math.Abs(Value);
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
        public readonly bool AiHasFlipCards;

        /// The one effect card in the AI's four-card hand at this rung, or None. The AI's hand
        /// lasts the whole match, so one effect card is about one dramatic moment per match.
        public readonly CardEffect AiEffect;

        public LadderStep(int rank, string opponent, int targetScore, int medalReward,
                          bool aiHasFlipCards = true, CardEffect aiEffect = CardEffect.None)
        {
            Rank = rank;
            Opponent = opponent;
            TargetScore = targetScore;
            MedalReward = medalReward;
            AiHasFlipCards = aiHasFlipCards;
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
    /// on. STAGE 8 IS NOW THE EMPTY ONE and wants a card of its own; until it has one it deals a
    /// wired card from further down (DealAiHand), so it plays as a harder version of a rung below.
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
        new LadderStep(3, "Ruby Champion",       24,  8, true),                             // 8  NEW CARD PENDING - see below
        new LadderStep(4, "Obsidian Challenger", 22,  8, true),                             // 9  ruleset rolled
        new LadderStep(4, "Obsidian Champion",   25, 10, true),                             // 10 ruleset rolled
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
    public int CurrentTarget => CurrentStep.TargetScore;
    public Rank CurrentRank => Ranks[Mathf.Clamp(CurrentStep.Rank, 0, Ranks.Length - 1)];
    public int MatchNumber => Mathf.Clamp(StepIndex, 0, Ladder.Length - 1) + 1;
    public bool RunComplete => StepIndex >= Ladder.Length;

    /// The target of the rung below this one - what the player has been playing to until now.
    public int PreviousTarget => StepIndex > 0 ? StepAt(StepIndex - 1).TargetScore : CurrentTarget;

    /// True when stepping onto this rung MOVED the target. Difficulty on this ladder is the
    /// target moving away from a comfortable 20, so the one thing the table must not do is change
    /// that number quietly - the player has to be told, on the rung where it happens.
    public bool TargetMovedThisStage => StepIndex > 0 && CurrentTarget != PreviousTarget;

    /// Starts a run at the bottom of the ladder. The LADDER resets; the COLLECTION does not.
    ///
    /// Losing must not wipe singleplayer progress (Alexander, 2026-09-07): every card the player
    /// has unlocked, the deck they built out of it, and their banked medals all survive a lost run
    /// and carry into the next one. What a loss costs is the climb, not the cards.
    public void StartNewRun()
    {
        RunActive = true;
        StepIndex = 0;

        if (Inventory.Count == 0) Inventory.AddRange(StarterCollection);

        // Keep the deck they last built; only fill it in if it is missing or has gone stale.
        SideDeck.RemoveAll(index => index < 0 || index >= Inventory.Count);
        int required = Math.Min(SideDeckSize, Inventory.Count);
        for (int i = 0; SideDeck.Count < required && i < Inventory.Count; i++)
        {
            if (!SideDeck.Contains(i)) SideDeck.Add(i);
        }

        Save();
    }

    public void EndRun()
    {
        RunActive = false;
        Save();
    }

    /// Banks the medals for a won match and moves the player up one rung.
    public void CompleteMatch(int roundsWon, bool won)
    {
        if (!RunActive) return;

        if (won)
        {
            // A medal per round taken, plus the rung's purse - a clean 2-0 is worth keeping.
            Medals += roundsWon + CurrentStep.MedalReward;
            StepIndex++;
            if (StepIndex > FurthestStep) FurthestStep = StepIndex;
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
        StepIndex = Mathf.Clamp(stepIndex, 0, Ladder.Length - 1);
        if (StepIndex > FurthestStep) FurthestStep = StepIndex;
        Save();
    }

    /// Back to a brand new player: no collection, no deck, no medals, no run.
    public void DebugWipeSave()
    {
        RunActive = false;
        Medals = 0;
        StepIndex = 0;
        FurthestStep = 0;
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
    /// Draws MatchHandSize cards at random from the player's side deck. This is the whole point of
    /// the 12-card deck: the deck is chosen, the hand is not.
    public List<Card> DrawMatchHand()
    {
        List<int> pool = new List<int>(SideDeck);
        List<Card> hand = new List<Card>();

        for (int i = 0; i < MatchHandSize && pool.Count > 0; i++)
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
                { "flip", def.IsFlip },
                { "effect", (int)def.Effect },
            });
        }

        Godot.Collections.Array sideDeck = new Godot.Collections.Array();
        foreach (int index in SideDeck) sideDeck.Add(index);

        Godot.Collections.Dictionary data = new Godot.Collections.Dictionary
        {
            { "version", 3 },
            { "active", RunActive },
            { "medals", Medals },
            { "step", StepIndex },
            { "furthest", FurthestStep },
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
                CardEffect effect = (rawEffect > 0 && rawEffect <= (int)CardEffect.Copy)
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
