using Godot;

/// The autoload that owns the live RunData: loads it from disk when the game boots and writes it
/// back whenever it changes. This is the only part of the profile that touches the filesystem,
/// which is what lets RunData itself be plain, testable C#.
public partial class RunStore : Node
{
    private const string SavePath = "user://run.json";

    public override void _Ready()
    {
        RunData run = new RunData();
        Godot.Collections.Dictionary saved = Read();
        if (saved != null) run.LoadSaveData(saved);
        run.Changed += () => Write(run);
        RunData.Instance = run;
    }

    private static Godot.Collections.Dictionary Read()
    {
        if (!FileAccess.FileExists(SavePath)) return null;

        using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
        if (file == null) return null;

        Variant parsed = Json.ParseString(file.GetAsText());
        return parsed.VariantType == Variant.Type.Dictionary ? parsed.AsGodotDictionary() : null;
    }

    private static void Write(RunData run)
    {
        using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            GD.PushWarning($"Could not write the run save: {FileAccess.GetOpenError()}");
            return;
        }
        file.StoreString(Json.Stringify(run.ToSaveData()));
    }
}
