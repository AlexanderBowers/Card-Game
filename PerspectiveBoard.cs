using Godot;

/// <summary>
/// A board drawn in perspective, lying on the table (pass 39, after Pokemon TCG Pocket's field).
///
/// Still an ordinary GridContainer of nine slots as far as layout goes - its rect, the slots and
/// the card sizes are exactly what the scene says. Only the DRAWING is bent, by
/// board_perspective.gdshader: the far edge narrows and the board shortens toward the edge
/// nearest its player. Every slot and card on the board draws through the same material
/// (use_parent_material), so a card dropped onto it tilts with it.
///
/// [Tool]: runs in the editor, so the scene shows the tilt as the game does. Tune it with
/// Far Scale and Depth in the inspector, or untick Tilt to see the flat grid.
/// </summary>
[Tool]
[GlobalClass]
public partial class PerspectiveBoard : GridContainer
{
    private const string ShaderPath = "res://board_perspective.gdshader";

    /// Width of the far edge, relative to the near edge. 1 = no narrowing.
    [Export(PropertyHint.Range, "0.5,1,0.01")] public float FarScale = 0.8f;

    /// How much of its height the board keeps. 1 = no shortening. The height given up shows as
    /// space on the board's far side - the breathing room.
    [Export(PropertyHint.Range, "0.5,1,0.01")] public float Depth = 0.88f;

    /// Which edge is far from the player who reads this board. The table sets it for Player 2's
    /// board in face-to-face play, where that player sits at the top of the screen.
    [Export] public bool FarAtTop = true;

    /// Off draws the plain flat grid.
    [Export] public bool Tilt = true;

    private ShaderMaterial _material;

    public override void _EnterTree()
    {
        EnsureMaterial();
        GetTree().NodeAdded += OnNodeAdded;
        ShareMaterial(this);
    }

    public override void _ExitTree()
    {
        if (GetTree() != null) GetTree().NodeAdded -= OnNodeAdded;
    }

    public override void _Process(double delta) => PushUniforms();

    private void EnsureMaterial()
    {
        if (Material is ShaderMaterial existing && existing.Shader?.ResourcePath == ShaderPath)
        {
            _material = existing;
            return;
        }
        Shader shader = GD.Load<Shader>(ShaderPath);
        if (shader == null) return;
        // Made here rather than saved in the scene, so each board has its own (the uniforms differ).
        _material = new ShaderMaterial { Shader = shader, ResourceLocalToScene = true };
        Material = _material;
    }

    /// Slots and cards arrive long after the board (a card is dropped in mid-set, its pips are
    /// rebuilt on a resize), so every node that lands anywhere under the board is told to draw
    /// with the board's material.
    private void OnNodeAdded(Node node)
    {
        if (node is CanvasItem item && IsAncestorOf(node)) item.UseParentMaterial = true;
    }

    private static void ShareMaterial(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is CanvasItem item) item.UseParentMaterial = true;
            ShareMaterial(child);
        }
    }

    /// The board's rect in canvas space - from its corners, so a board turned round with Player
    /// 2's side still gives the rect it actually covers.
    public Rect2 CanvasRect()
    {
        Transform2D t = GetGlobalTransform();
        Vector2 a = t * Vector2.Zero, b = t * new Vector2(Size.X, 0), c = t * new Vector2(0, Size.Y), d = t * Size;
        Vector2 min = new Vector2(Mathf.Min(Mathf.Min(a.X, b.X), Mathf.Min(c.X, d.X)), Mathf.Min(Mathf.Min(a.Y, b.Y), Mathf.Min(c.Y, d.Y)));
        Vector2 max = new Vector2(Mathf.Max(Mathf.Max(a.X, b.X), Mathf.Max(c.X, d.X)), Mathf.Max(Mathf.Max(a.Y, b.Y), Mathf.Max(c.Y, d.Y)));
        return new Rect2(min, max - min);
    }

    private void PushUniforms()
    {
        if (_material == null) EnsureMaterial();
        if (_material == null) return;
        Rect2 r = CanvasRect();
        _material.SetShaderParameter("board_pos", r.Position);
        _material.SetShaderParameter("board_size", r.Size);
        _material.SetShaderParameter("far_scale", Tilt ? FarScale : 1f);
        _material.SetShaderParameter("depth", Tilt ? Depth : 1f);
        _material.SetShaderParameter("far_at_top", FarAtTop);
    }

    /// Where a point on the flat board is actually drawn - the same sum as the shader. The card
    /// that flies in from the deck aims here, so it lands where the tilted card appears.
    public Vector2 Warp(Vector2 canvasPoint)
    {
        if (!Tilt) return canvasPoint;
        Rect2 r = CanvasRect();
        float v = (canvasPoint.Y - r.Position.Y) / Mathf.Max(r.Size.Y, 1f);
        float nearness = Mathf.Clamp(FarAtTop ? v : 1f - v, 0f, 1f);
        float s = Mathf.Lerp(FarScale, 1f, nearness);
        float cx = r.Position.X + r.Size.X * 0.5f;
        float nearY = FarAtTop ? r.End.Y : r.Position.Y;
        return new Vector2(cx + (canvasPoint.X - cx) * s, nearY + (canvasPoint.Y - nearY) * Depth);
    }

    /// How much narrower the board is drawn at this point (for the flying card's final size).
    public float WidthScaleAt(Vector2 canvasPoint)
    {
        if (!Tilt) return 1f;
        Rect2 r = CanvasRect();
        float v = (canvasPoint.Y - r.Position.Y) / Mathf.Max(r.Size.Y, 1f);
        return Mathf.Lerp(FarScale, 1f, Mathf.Clamp(FarAtTop ? v : 1f - v, 0f, 1f));
    }
}
