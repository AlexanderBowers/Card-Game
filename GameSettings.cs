using Godot;
using System;

/// <summary>
/// Player options: volume, battery, and the debug-button switch. Saved to user://settings.cfg,
/// apart from run.json, so Wipe Save never resets someone's volume.
///
/// Static rather than an autoload: nothing here needs the scene tree except AudioServer and
/// Engine, which are singletons anyway. EnsureLoaded() is safe to call from every _Ready.
/// </summary>
public static class GameSettings
{
    private const string Path = "user://settings.cfg";

    /// Volume is set in notches, not a continuous 0-100: five positions, the way the old
    /// RuneScape sliders work. A notch is easy to hit with a thumb and easy to read at a glance.
    public const int VolumeSteps = 4; // levels 0..4

    public enum Channel { Master, Music, Sfx }

    // The bus each channel drives. Music has no tracks yet; the bus exists so the slider is real
    // the day a track is added.
    public const string MusicBus = "Music";
    public const string SfxBus = "SFX";

    private static readonly int[] Levels = { 3, 3, 3 };
    private static readonly bool[] Muted = { false, false, false };

    /// Off = cards appear in place instead of flying from the deck, and a vetoed card vanishes
    /// instead of burning. Fewer frames drawn, and kinder to anyone bothered by motion.
    public static bool CardAnimations { get; private set; } = true;

    /// On = 30 fps cap and Godot's low-processor mode, which only redraws when something changes.
    /// A card game spends most of its life waiting for a tap, so this is where the battery goes.
    public static bool BatterySaver { get; private set; }

    /// The debug rows on the table. Only offered in debug builds; an exported build never builds
    /// those rows, so this setting cannot turn them on there.
    public static bool ShowDebugButtons { get; private set; } = true;

    /// Debug preview (pass 21): portrait boards drawn as an overlapping stack instead of 3x3.
    /// Only read in debug builds.
    public static bool StackedBoardPortrait { get; private set; }

    /// Raised after any change, so open screens can react (the table shows or hides debug rows).
    public static event Action Changed;

    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        ConfigFile cfg = new ConfigFile();
        if (cfg.Load(Path) == Error.Ok)
        {
            for (int i = 0; i < Levels.Length; i++)
            {
                string name = ((Channel)i).ToString().ToLowerInvariant();
                Levels[i] = Mathf.Clamp(cfg.GetValue("audio", name, Levels[i]).AsInt32(), 0, VolumeSteps);
                Muted[i] = cfg.GetValue("audio", name + "_muted", false).AsBool();
            }
            CardAnimations = cfg.GetValue("battery", "card_animations", true).AsBool();
            BatterySaver = cfg.GetValue("battery", "battery_saver", false).AsBool();
            ShowDebugButtons = cfg.GetValue("debug", "show_buttons", true).AsBool();
            StackedBoardPortrait = cfg.GetValue("debug", "stacked_board", false).AsBool();
        }

        Apply();
    }

    public static int GetLevel(Channel channel) { EnsureLoaded(); return Levels[(int)channel]; }
    public static bool IsMuted(Channel channel) { EnsureLoaded(); return Muted[(int)channel]; }

    public static void SetLevel(Channel channel, int level)
    {
        EnsureLoaded();
        level = Mathf.Clamp(level, 0, VolumeSteps);
        Levels[(int)channel] = level;
        // Dragging a muted slider up is a request to hear it. Leaving it muted would look broken.
        if (level > 0) Muted[(int)channel] = false;
        Commit();
    }

    public static void ToggleMute(Channel channel)
    {
        EnsureLoaded();
        Muted[(int)channel] = !Muted[(int)channel];
        Commit();
    }

    public static void SetCardAnimations(bool on) { EnsureLoaded(); CardAnimations = on; Commit(); }
    public static void SetBatterySaver(bool on) { EnsureLoaded(); BatterySaver = on; Commit(); }
    public static void SetShowDebugButtons(bool on) { EnsureLoaded(); ShowDebugButtons = on; Commit(); }
    public static void SetStackedBoardPortrait(bool on) { EnsureLoaded(); StackedBoardPortrait = on; Commit(); }

    private static void Commit()
    {
        Apply();
        Save();
        Changed?.Invoke();
    }

    private static void Apply()
    {
        ApplyBus("Master", Channel.Master);
        ApplyBus(MusicBus, Channel.Music);
        ApplyBus(SfxBus, Channel.Sfx);

        Engine.MaxFps = BatterySaver ? 30 : 0;
        OS.LowProcessorUsageMode = BatterySaver;
    }

    private static void ApplyBus(string busName, Channel channel)
    {
        int bus = EnsureBus(busName);
        int level = Levels[(int)channel];
        bool silent = Muted[(int)channel] || level == 0;
        AudioServer.SetBusMute(bus, silent);
        if (!silent) AudioServer.SetBusVolumeDb(bus, Mathf.LinearToDb(level / (float)VolumeSteps));
    }

    /// The project has no bus layout file, so Music and SFX are made here, both feeding Master.
    private static int EnsureBus(string busName)
    {
        int index = AudioServer.GetBusIndex(busName);
        if (index >= 0) return index;

        AudioServer.AddBus();
        index = AudioServer.BusCount - 1;
        AudioServer.SetBusName(index, busName);
        AudioServer.SetBusSend(index, "Master");
        return index;
    }

    private static void Save()
    {
        ConfigFile cfg = new ConfigFile();
        for (int i = 0; i < Levels.Length; i++)
        {
            string name = ((Channel)i).ToString().ToLowerInvariant();
            cfg.SetValue("audio", name, Levels[i]);
            cfg.SetValue("audio", name + "_muted", Muted[i]);
        }
        cfg.SetValue("battery", "card_animations", CardAnimations);
        cfg.SetValue("battery", "battery_saver", BatterySaver);
        cfg.SetValue("debug", "show_buttons", ShowDebugButtons);
        cfg.SetValue("debug", "stacked_board", StackedBoardPortrait);

        Error err = cfg.Save(Path);
        if (err != Error.Ok) GD.PushWarning($"Could not save settings: {err}");
    }
}
