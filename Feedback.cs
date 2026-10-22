using Godot;
using System.Collections.Generic;

/// The game's touch feedback: the little sounds and buzzes that make it feel good to touch
/// (2026-10-22, after an audio engineer's playtest - Ian: "a lot of audio for mobile games is all
/// about making the UX feel satisfying and fun to touch, plus giving you little dopamine hits for
/// the little wins along the way").
///
/// An autoload, so the menus, the shops and the table all reach the same players. Three jobs:
///
///  * Every button anywhere taps on the PRESS (ButtonDown, not Pressed - the release can be a
///    long way from the touch), with a tiny buzz. Buttons are found as they enter the tree, so a
///    new screen needs no wiring. "It needs to happen as close to the actual touch as possible,
///    so it makes you feel like you are really touching the thing."
///  * Named one-shot sounds for the game's moments (Play): a set or match won or lost, a Modifier
///    picked up, the medal counter.
///  * Haptics (Buzz), behind Options > Haptics.
///
/// Every sound is a file in assets/sfx/ named after its Sound below (tools/sfxgen makes the
/// placeholders), so a hand-made sound is a drop-in replacement - same name, no code change.
public partial class Feedback : Node
{
    public static Feedback Instance { get; private set; }

    public enum Sound { Tap, ModifierPick, ModifierFlip, SetWin, SetLose, MatchWin, MatchLose, MedalTick, MedalDone }

    private static readonly Dictionary<Sound, string> Files = new Dictionary<Sound, string>
    {
        [Sound.Tap] = "ui_tap",
        [Sound.ModifierPick] = "modifier_pick",
        [Sound.ModifierFlip] = "modifier_flip",
        [Sound.SetWin] = "set_win",
        [Sound.SetLose] = "set_lose",
        [Sound.MatchWin] = "match_win",
        [Sound.MatchLose] = "match_lose",
        [Sound.MedalTick] = "medal_tick",
        [Sound.MedalDone] = "medal_done",
    };

    private readonly Dictionary<Sound, AudioStreamPlayer> _players = new Dictionary<Sound, AudioStreamPlayer>();

    /// Buttons that should not tap: the press is already its own, bigger sound (none yet - the
    /// group is here so a screen can opt a button out with AddToGroup).
    public const string SilentGroup = "no_tap";

    public override void _EnterTree()
    {
        Instance = this;
        GameSettings.EnsureLoaded();
        foreach (KeyValuePair<Sound, string> pair in Files)
        {
            string path = $"res://assets/sfx/{pair.Value}.wav";
            AudioStream stream = ResourceLoader.Exists(path) ? GD.Load<AudioStream>(path) : null;
            if (stream == null) continue;
            AudioStreamPlayer player = new AudioStreamPlayer
            {
                Stream = stream,
                Bus = GameSettings.SfxBus,
                VolumeDb = -2f,
                MaxPolyphony = pair.Key == Sound.Tap || pair.Key == Sound.MedalTick ? 6 : 2,
            };
            AddChild(player);
            _players[pair.Key] = player;
        }

        GetTree().NodeAdded += OnNodeAdded;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (GetTree() != null) GetTree().NodeAdded -= OnNodeAdded;
    }

    private void OnNodeAdded(Node node)
    {
        if (node is not BaseButton button || button.HasMeta(WiredMeta)) return;
        button.SetMeta(WiredMeta, true);
        button.ButtonDown += () =>
        {
            if (!GodotObject.IsInstanceValid(button) || button.Disabled || button.IsInGroup(SilentGroup)) return;
            Play(Sound.Tap);
            Buzz(Haptic.Tap);
        };
    }

    private const string WiredMeta = "feedbackWired";

    /// Plays a sound. `pitch` above 1 raises it (the medal counter climbs).
    public static void Play(Sound sound, float pitch = 1f)
    {
        if (Instance == null || !Instance._players.TryGetValue(sound, out AudioStreamPlayer player)) return;
        player.PitchScale = pitch;
        player.Play();
    }

    // ------------------------------------------------------------------
    // Haptics
    // ------------------------------------------------------------------

    /// How hard a buzz is: a tap is barely there, a win is felt.
    public enum Haptic { Tap, Light, Medium, Strong }

    public static void Buzz(Haptic kind)
    {
        if (!GameSettings.Haptics) return;
        if (!OS.HasFeature("android") && !OS.HasFeature("ios")) return;
        (int ms, float amplitude) = kind switch
        {
            Haptic.Tap => (8, 0.25f),
            Haptic.Light => (14, 0.4f),
            Haptic.Medium => (24, 0.65f),
            _ => (40, 1f),
        };
        Input.VibrateHandheld(ms, amplitude);
    }

    /// A short run of buzzes - a win is felt as a little drum roll rather than one long hum.
    public static void BuzzPattern(params (float At, Haptic Kind)[] beats)
    {
        if (Instance == null || !GameSettings.Haptics) return;
        foreach ((float at, Haptic kind) in beats)
        {
            if (at <= 0f) { Buzz(kind); continue; }
            Instance.GetTree().CreateTimer(at).Timeout += () => Buzz(kind);
        }
    }
}
