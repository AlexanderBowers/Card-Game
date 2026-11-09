using Godot;

/// The background music (2026-11-08): a lounge loop for the menus and one for the table, both 60 s
/// at 96 BPM on the same chart (tools/musicgen), so switching between them is a crossfade at the
/// same playback position rather than a cut.
///
/// Both loops play all the time and only their volumes move. That keeps them sample-locked to
/// each other, so a crossfade never drifts a beat.
///
/// The music is also the board's clock. board_fx.gdshaderinc sends a ring out of the emblem every
/// 5 s (2 bars) and runs its rim gleam on 15 s laps (6 bars); ShineDriver reads Clock instead of
/// counting frames, so the ring leaves exactly on the downbeat even after a hitch or a pause.
/// Muting the Music slider only silences the bus, so the clock (and the board) keeps going.
///
/// An autoload. Mood is set by the start menu: shown = Menu, hidden (a match starting) = Table.
public partial class Music : Node
{
    public enum Mood { Menu, Table }

    public static Music Instance { get; private set; }

    public const double LoopSeconds = 60.0;
    public const double Bpm = 96.0;

    private const string MenuPath = "res://assets/music/music_menu.ogg";
    private const string TablePath = "res://assets/music/music_table.ogg";
    private const float FadeSeconds = 1.6f;
    private const float SilentDb = -60f;
    private const float FullDb = -3f;

    private AudioStreamPlayer _menu;
    private AudioStreamPlayer _table;
    private Mood _mood = Mood.Menu;
    private float _menuMix = 1f; // 1 = all menu, 0 = all table

    /// True once both loops are playing; until then ShineDriver keeps its own clock.
    public bool Running => _menu != null && _menu.Playing;

    /// Where the music is in its 60 s loop, as heard: the mixer's position, plus the time since
    /// it last mixed, minus what is still on its way to the speaker.
    public double Clock
    {
        get
        {
            if (!Running) return 0;
            double t = _menu.GetPlaybackPosition() + AudioServer.GetTimeSinceLastMix() - AudioServer.GetOutputLatency();
            t %= LoopSeconds;
            return t < 0 ? t + LoopSeconds : t;
        }
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        GameSettings.EnsureLoaded();
        _menu = MakePlayer(MenuPath);
        _table = MakePlayer(TablePath);
        if (_menu == null || _table == null) return;
        Apply();
        _menu.Play();
        _table.Play();
    }

    private AudioStreamPlayer MakePlayer(string path)
    {
        if (!ResourceLoader.Exists(path)) return null;
        AudioStream stream = GD.Load<AudioStream>(path);
        if (stream is AudioStreamOggVorbis ogg) ogg.Loop = true;
        AudioStreamPlayer player = new AudioStreamPlayer { Stream = stream, Bus = GameSettings.MusicBus, VolumeDb = SilentDb };
        AddChild(player);
        return player;
    }

    public void SetMood(Mood mood) => _mood = mood;

    public override void _Process(double delta)
    {
        if (!Running) return;
        float want = _mood == Mood.Menu ? 1f : 0f;
        if (Mathf.IsEqualApprox(_menuMix, want)) return;
        _menuMix = Mathf.MoveToward(_menuMix, want, (float)delta / FadeSeconds);
        Apply();

        // The two streams are started together and never seek, but if a device ever lets them
        // drift (a decoder hiccup on one), pull the table back onto the menu's position.
        double drift = _table.GetPlaybackPosition() - _menu.GetPlaybackPosition();
        if (Mathf.Abs(drift) > 0.05 && Mathf.Abs(drift) < LoopSeconds - 0.05) _table.Seek(_menu.GetPlaybackPosition());
    }

    /// Equal-power crossfade, so the middle of the fade is not a dip.
    private void Apply()
    {
        float menuGain = Mathf.Sin(_menuMix * Mathf.Pi / 2f);
        float tableGain = Mathf.Cos(_menuMix * Mathf.Pi / 2f);
        _menu.VolumeDb = menuGain < 0.001f ? SilentDb : FullDb + Mathf.LinearToDb(menuGain);
        _table.VolumeDb = tableGain < 0.001f ? SilentDb : FullDb + Mathf.LinearToDb(tableGain);
    }
}
