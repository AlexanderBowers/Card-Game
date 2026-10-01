using Godot;

/// <summary>
/// A small frame-rate readout in the top-left corner (Options > Graphics > "Show frame rate"),
/// for checking the 3D table on a real phone: frames per second, how long a frame takes, and
/// whether the table is drawing in 3D or 2D. Off by default; costs nothing while hidden.
/// </summary>
public partial class FpsCounter : CanvasLayer
{
    private Label _label;
    private double _sinceUpdate;

    public override void _Ready()
    {
        Layer = 120; // above every overlay
        _label = new Label
        {
            Position = new Vector2(12, 40), // below a status bar or notch
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _label.AddThemeFontSizeOverride("font_size", 18);
        _label.AddThemeColorOverride("font_color", Colors.White);
        _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        _label.AddThemeConstantOverride("outline_size", 5);
        AddChild(_label);
    }

    public override void _Process(double delta)
    {
        bool show = GameSettings.ShowFps;
        if (_label.Visible != show) _label.Visible = show;
        if (!show) return;

        _sinceUpdate += delta;
        if (_sinceUpdate < 0.5) return; // twice a second is readable; every frame is a blur
        _sinceUpdate = 0;

        double fps = Performance.GetMonitor(Performance.Monitor.TimeFps);
        double ms = fps > 0 ? 1000.0 / fps : 0;
        string mode = GameSettings.Table3D ? "3D" : "2D";
        string saver = GameSettings.BatterySaver ? ", saver" : "";
        _label.Text = $"{fps:0} fps  {ms:0.0} ms  ({mode}{saver})";
    }
}
