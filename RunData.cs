using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The single piece of state that outlives a scene change: everything about the player's current
/// run down the ladder. Registered as an autoload (see project.godot), so the table, the shop and
/// the armory all read and write the same instance.
///
/// Local 2-player never touches this - that mode stays a self-contained match with the randomized
/// hands dealt by Player.DealRandomModifierHand.
/// </summary>
public partial class RunData : Node
{
    public static RunData Instance { get; private set; }

    public const int SideDeckSize = 12;   // the armory holds exactly this many
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

        public ModifierDef(int value, bool isFlip = false)
        {
            Value = value;
            IsFlip = isFlip;
        }

        public Card ToCard() => new Card(Value, CardType.Modifier, "", IsFlip);

        public string Label => (IsFlip ? "±" : (Value > 0 ? "+" : "-")) + Math.Abs(Value);
    }

    // ------------------------------------------------------------------
    // One rung of the linear ladder. Venues differ by NAME and RULES only - the table keeps one
    // grounded, readable look throughout (Alexander's call, 2026-09-06), so no venue owns art.
    // ------------------------------------------------------------------
    public readonly struct LadderStep
    {
        public readonly string Venue;
        public readonly string Opponent;
        public readonly int TargetScore;
        public readonly int MedalReward;

        public LadderStep(string venue, string opponent, int targetScore, int medalReward)
        {
            Venue = venue;
            Opponent = opponent;
            TargetScore = targetScore;
            MedalReward = medalReward;
        }
    }

    /// The gauntlet. Difficulty escalates by moving the target away from the comfortable 20 - never
    /// by inflating the arithmetic (the 5-to-85 accessibility tenet rules out multiplier math).
    private static readonly LadderStep[] Ladder =
    {
        new LadderStep("Rustbelt Outpost",   "Scrapper",   20, 3),
        new LadderStep("Rustbelt Outpost",   "Foreman",    20, 3),
        new LadderStep("Neon Underground",   "Circuit",    23, 4),
        new LadderStep("Neon Underground",   "Vex",        23, 4),
        new LadderStep("High Roller Airship","Steward",    18, 5),
        new LadderStep("Neon Underground",   "Null",       23, 5),
        new LadderStep("High Roller Airship","Baroness",   18, 6),
        new LadderStep("Rustbelt Outpost",   "The Welder", 24, 6),
        new LadderStep("High Roller Airship","The Captain",18, 8),
        new LadderStep("Neon Underground",   "Overclock",  23, 10),
    };

    public static int LadderLength => Ladder.Length;

    // ------------------------------------------------------------------
    // Run state
    // ------------------------------------------------------------------
    public bool RunActive { get; private set; }
    public int Medals { get; private set; }
    public int StepIndex { get; private set; }              // 0-based rung of the ladder

    /// Every modifier card the player owns. Cards are only ever added (there is no selling), so an
    /// index into this list is a stable id - which is what SideDeck stores.
    public List<ModifierDef> Inventory { get; } = new List<ModifierDef>();

    /// Indices into Inventory. Exactly SideDeckSize of them once the armory has been confirmed.
    public List<int> SideDeck { get; } = new List<int>();

    private readonly Random _random = new Random();

    /// What the player starts a run with: enough cards that the armory is a real choice from the
    /// first visit (15 owned, 12 slotted) without needing the shop to exist yet.
    private static readonly ModifierDef[] StarterCollection =
    {
        new ModifierDef(1), new ModifierDef(1), new ModifierDef(2), new ModifierDef(2),
        new ModifierDef(3), new ModifierDef(3), new ModifierDef(4),
        new ModifierDef(-1), new ModifierDef(-1), new ModifierDef(-2), new ModifierDef(-2),
        new ModifierDef(-3), new ModifierDef(-3), new ModifierDef(-4),
        new ModifierDef(2, isFlip: true),
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
    public int MatchNumber => Mathf.Clamp(StepIndex, 0, Ladder.Length - 1) + 1;
    public bool RunComplete => StepIndex >= Ladder.Length;

    public void StartNewRun()
    {
        RunActive = true;
        Medals = 0;
        StepIndex = 0;

        Inventory.Clear();
        Inventory.AddRange(StarterCollection);

        // A sensible opening deck so the first match is playable before the armory scene exists.
        SideDeck.Clear();
        for (int i = 0; i < SideDeckSize && i < Inventory.Count; i++) SideDeck.Add(i);

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
            // A medal per round taken, plus the venue's purse - a clean 2-0 is worth keeping.
            Medals += roundsWon + CurrentStep.MedalReward;
            StepIndex++;
        }
        else
        {
            RunActive = false; // the run ends on a lost match
        }

        Save();
    }

    public void SpendMedals(int amount) { Medals = Math.Max(0, Medals - amount); Save(); }

    public void AddToInventory(ModifierDef def) { Inventory.Add(def); Save(); }

    // ------------------------------------------------------------------
    // Dealing a match hand
    // ------------------------------------------------------------------
    /// Draws MatchHandSize cards at random from the player's side deck. This is the whole point of
    /// the 12-card armory: the deck is chosen, the hand is not.
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
            });
        }

        Godot.Collections.Array sideDeck = new Godot.Collections.Array();
        foreach (int index in SideDeck) sideDeck.Add(index);

        Godot.Collections.Dictionary data = new Godot.Collections.Dictionary
        {
            { "version", 1 },
            { "active", RunActive },
            { "medals", Medals },
            { "step", StepIndex },
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

        Inventory.Clear();
        if (data.TryGetValue("inventory", out Variant inventoryVariant))
        {
            foreach (Variant entry in inventoryVariant.AsGodotArray())
            {
                Godot.Collections.Dictionary card = entry.AsGodotDictionary();
                int value = card.TryGetValue("value", out Variant v) ? v.AsInt32() : 0;
                bool flip = card.TryGetValue("flip", out Variant f) && f.AsBool();
                if (value != 0) Inventory.Add(new ModifierDef(value, flip));
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
        // run - drop back to "no run" rather than starting a match with an empty hand.
        if (RunActive && (Inventory.Count == 0 || SideDeck.Count == 0)) RunActive = false;
    }
}
