using Godot;

/// The autoload that owns the live RunData: loads it from disk when the game boots and writes it
/// back whenever it changes. This is the only part of the profile that touches the filesystem,
/// which is what lets RunData itself - save format included - be plain, testable C#.
public partial class RunStore : Node
{
    private const string SavePath = "user://run.json";

    public override void _Ready()
    {
        // The first autoload, so the theme is finished before any screen is built.
        ToggleIcons.Apply();

        RunData run = new RunData();
        string saved = Read();
        if (saved != null && !run.LoadSaveJson(saved))
            GD.PushWarning("The run save could not be read; starting from a fresh profile.");
        run.Changed += () => Write(run);
        RunData.Instance = run;
    }

    private static string Read()
    {
        if (!FileAccess.FileExists(SavePath)) return null;

        using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
        return file?.GetAsText();
    }

    private static void Write(RunData run)
    {
        using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            GD.PushWarning($"Could not write the run save: {FileAccess.GetOpenError()}");
            return;
        }
        file.StoreString(run.ToSaveJson());
    }
}
