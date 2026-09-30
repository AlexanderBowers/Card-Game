using Godot;

/// <summary>
/// Feeds card_shine.gdshader's global card_tilt every frame (pass 57).
///
/// On a phone it is how the phone is being tilted RIGHT NOW: gravity measured against a slowly
/// drifting "at rest" reading, so holding the phone at any angle is neutral and moving it is what
/// makes the shine slide - the way Pocket's cards catch the light. On top of that, a slow idle
/// sway keeps the shine alive when the phone is still (and on desktop, which has no sensor).
/// Battery saver turns the sensor off; the sway stays.
/// </summary>
public partial class ShineDriver : Node
{
    public const string TiltParam = "card_tilt";

    private Vector3 _rest;
    private bool _haveRest;
    private Vector2 _tilt;
    private double _time;

    public override void _Process(double delta)
    {
        _time += delta;
        Vector2 sensed = Vector2.Zero;

        if (!GameSettings.BatterySaver && OS.HasFeature("mobile"))
        {
            Vector3 g = Input.GetGravity();
            if (g.LengthSquared() > 0.01f)
            {
                if (!_haveRest) { _rest = g; _haveRest = true; }
                _rest = _rest.Lerp(g, (float)Mathf.Min(1.0, delta * 0.6)); // drifts to the new hold in ~2s
                Vector3 moved = (g - _rest) / 3.5f;
                sensed = new Vector2(Mathf.Clamp(moved.X, -1f, 1f), Mathf.Clamp(-moved.Y, -1f, 1f));
            }
        }

        Vector2 sway = new Vector2(Mathf.Sin((float)_time * 0.55f) * 0.22f, Mathf.Cos((float)_time * 0.41f) * 0.14f);
        Vector2 want = sensed + sway;
        _tilt = _tilt.Lerp(want, (float)Mathf.Min(1.0, delta * 8.0));
        RenderingServer.GlobalShaderParameterSet(TiltParam, _tilt);
    }
}
