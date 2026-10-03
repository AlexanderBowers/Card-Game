using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The 3D table (prototype, 2026-09-30: "Let's prototype it! Also having the camera swing in
/// before a match like Pocket TCG").
///
/// The 2D layout stays the source of truth - it still lays out the boards, runs every animation
/// and takes every tap. This node is a PRESENTATION layer on top of it: each frame it reads where
/// the table pieces are in the 2D layout (playmat, slots, board cards, decks, the centre line and
/// ring, cards in flight, impact rings), lays that picture flat on a real 3D table, and hides the
/// 2D versions of exactly those pieces. A perspective camera looks down at it, a light casts the
/// cards' shadows onto the felt, and a card in flight is lifted off the table. The hands, buttons,
/// scores and menus stay 2D, drawn over the top - the same split Pokemon TCG Pocket uses.
///
/// Mapping: one layout pixel is 1/PixelsPerUnit of a world unit on the XZ plane, the middle of
/// the screen at the origin, screen-down = +Z (towards the camera). The camera distance is
/// solved so the middle of the table is drawn at the same scale as the 2D layout, which keeps the
/// boards roughly where the 2D buttons and scores expect them.
///
/// Switched by GameSettings.Table3D (Options > Graphics). Off = the 2D table exactly as before.
/// Battery saver also turns the shadows off.
/// </summary>
public partial class TableWorld3D : Node3D
{
    private const float PixelsPerUnit = 100f;
    private const float FieldOfView = 35f;      // vertical, degrees
    private const float RestPitch = 30f;        // camera, degrees away from straight down
    private const uint HiddenLayer = 1u << 19;  // 2D pieces drawn in 3D instead are moved here

    private const float SwingSeconds = 1.8f;
    private const float SwingStartYaw = 48f;
    private const float SwingStartPitch = 64f;
    private const float SwingStartDistance = 1.75f;

    /// Set by TableUi straight after it makes this node.
    public TableUi Ui { get; set; }
    private TableUi _ui => Ui;
    private Camera3D _camera;
    private DirectionalLight3D _sun;
    private MeshInstance3D _mat;
    private StandardMaterial3D _matMaterial;
    private MeshInstance3D _slab;

    /// How deep the tabletop is under the playmat, in world units (one unit = PixelsPerUnit layout
    /// pixels, so 0.8 is about a card's width). It only shows from the side, which is
    /// exactly what the swing-in looks at: a mat lying on the floor read as paper (2026-10-02).
    private const float TableThickness = 0.8f;
    private WorldEnvironment _env;

    private bool _active;
    private float _swing = 1f;                  // 0 = start of the swing-in, 1 = at rest
    private Action _swingDone;

    /// The table in the scene, for the tutorial's spotlight (TryScreenRect). Null when there is none.
    public static TableWorld3D Instance { get; private set; }

    private const string ShinePath = "res://card_shine_3d.gdshader";
    private Shader _shineShader;

    // Camera shake on a Modifier's impact (TableUi.JoltBoard): a short, decaying jitter.
    private const float ShakeSeconds = 0.28f;
    private float _shake;
    private float _shakeStrength;
    private readonly Random _rng = new Random();

    private readonly Dictionary<ulong, Piece> _pieces = new Dictionary<ulong, Piece>();
    private readonly HashSet<ulong> _seen = new HashSet<ulong>();

    private ImageTexture _slotOutline;
    private readonly Dictionary<Vector2I, ImageTexture> _ringOutlines = new Dictionary<Vector2I, ImageTexture>();

    private sealed class Piece
    {
        public MeshInstance3D Mesh;
        public StandardMaterial3D Material;
        public StandardMaterial3D StackMaterial; // decks: the cards under the top one; cards: the edge
        public ShaderMaterial Shine;             // cards only: the additive gloss and foil overlay
    }

    private enum Kind { Card, Slot, Deck, Line, Ring }

    /// Marks a 2D node on the scene root as a table piece in flight (TableUi's flying cards and
    /// impact rings). Anything else on the root - coach marks, overlays - is left alone.
    public const string FlyingMeta = "table3d";

    public override void _Ready()
    {
        Instance = this;
        _shineShader = ResourceLoader.Exists(ShinePath) ? GD.Load<Shader>(ShinePath) : null;

        _camera = new Camera3D { Fov = FieldOfView, Near = 0.05f, Far = 200f };
        AddChild(_camera);

        _sun = new DirectionalLight3D
        {
            LightEnergy = 0.75f,
            ShadowEnabled = true,
            ShadowBlur = 2.5f,
            DirectionalShadowMaxDistance = 40f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
        };
        _sun.RotationDegrees = new Vector3(-62f, -28f, 0f);
        AddChild(_sun);

        Godot.Environment environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.07f, 0.09f, 0.13f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
            AmbientLightEnergy = 0.5f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        _env = new WorldEnvironment { Environment = environment };
        AddChild(_env);

        // The playmat, the tabletop under it, and a darker floor round it that only shows during the
        // swing-in.
        _matMaterial = new StandardMaterial3D
        {
            Roughness = 0.9f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        _mat = new MeshInstance3D { Mesh = new PlaneMesh { Size = Vector2.One }, MaterialOverride = _matMaterial };
        _mat.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_mat);

        // The tabletop under the mat: a dark wooden slab whose sides show while the camera swings
        // in. A unit-square box, scaled with the mat in PlaceMat; its top sits a hair under the
        // mat so the two never fight over the same depth.
        StandardMaterial3D slabMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.46f, 0.29f, 0.17f), Roughness = 0.6f };
        _slab = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1f, TableThickness, 1f) },
            MaterialOverride = slabMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _slab.Position = new Vector3(0f, -TableThickness / 2f - 0.002f, 0f);
        AddChild(_slab);

        StandardMaterial3D floorMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.1f, 0.09f), Roughness = 0.8f };
        MeshInstance3D floor = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) },
            MaterialOverride = floorMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        floor.Position = new Vector3(0f, -TableThickness - 0.02f, 0f);
        AddChild(floor);

        _slotOutline = Outline(new Vector2I(140, 190), 14f, 3f, new Color(1f, 1f, 1f, 0.42f), new Color(1f, 1f, 1f, 0.07f));
        SetActive(GameSettings.Table3D);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        // Leaving mid swing-in (Restart, back to the menu, quitting): the scene is going away, so
        // whatever the landing was going to start (the stage intro) must not run. It used to, and
        // tried to add the intro to a root that was busy tearing down - an error and a
        // NullReferenceException on every exit during the swing.
        _swingDone = null;
        if (_active) SetActive(false);
    }

    // ------------------------------------------------------------------
    // On / off
    // ------------------------------------------------------------------

    private void SetActive(bool on)
    {
        _active = on;
        Visible = on;
        if (_camera != null) _camera.Current = on;

        Viewport viewport = GetViewport();
        if (viewport != null)
        {
            viewport.CanvasCullMask = on ? (viewport.CanvasCullMask & ~HiddenLayer) : (viewport.CanvasCullMask | HiddenLayer);
        }

        TableLayout layout = _ui.Layout;
        if (layout != null)
        {
            if (layout.Background != null) layout.Background.Visible = !on;
            SetTilt(layout, !on);
        }

        if (!on)
        {
            foreach (Piece piece in _pieces.Values) FreePiece(piece);
            _pieces.Clear();
            FinishSwing();
        }
    }

    private static void SetTilt(TableLayout layout, bool tilt)
    {
        if (layout.P1Board is PerspectiveBoard p1) p1.Tilt = tilt;
        if (layout.P2Board is PerspectiveBoard p2) p2.Tilt = tilt;
    }

    // ------------------------------------------------------------------
    // The swing-in
    // ------------------------------------------------------------------

    /// Whether a match start should play the swing-in (the 3D table is on).
    public bool CanSwing => _active;

    /// The camera sweeps in from high and to the side and settles on the table. The 2D layer
    /// (hands, buttons, scores) fades in once it lands; `done` runs then.
    public void SwingIn(Action done)
    {
        if (!_active)
        {
            done?.Invoke();
            return;
        }
        _swing = 0f;
        _swingDone = done;
        Control canvas = _ui.Layout?.Canvas;
        if (canvas != null) canvas.Modulate = new Color(1f, 1f, 1f, 0f);
    }

    private void FinishSwing()
    {
        _swing = 1f;
        Control canvas = _ui.Layout?.Canvas;
        if (canvas != null && canvas.IsInsideTree() && canvas.Modulate.A < 1f)
        {
            Tween fade = canvas.CreateTween();
            fade.TweenProperty(canvas, "modulate:a", 1f, 0.35f);
        }
        Action done = _swingDone;
        _swingDone = null;
        done?.Invoke();
    }

    // ------------------------------------------------------------------
    // Every frame
    // ------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_ui == null) return;
        bool want = GameSettings.Table3D;
        if (want != _active) SetActive(want);
        if (!_active) return;

        // Nothing to draw before a match is dealt (the start menu covers the screen): stop
        // rendering the table rather than spend the battery on it behind an opaque menu.
        bool live = _ui.MatchLive;
        if (Visible != live) Visible = live;
        if (!live) return;

        TableLayout layout = _ui.Layout;
        if (layout == null || !layout.IsInsideTree()) return;

        Vector2 view = layout.GetViewportRect().Size;
        if (view.X <= 0f || view.Y <= 0f) return;

        if (layout.Background != null && layout.Background.Visible) layout.Background.Visible = false;
        SetTilt(layout, false);
        _sun.ShadowEnabled = !GameSettings.BatterySaver;

        if (_swing < 1f)
        {
            _swing = Mathf.Min(1f, _swing + (float)delta / SwingSeconds);
            if (_swing >= 1f) FinishSwing();
        }
        PlaceCamera(view);
        PlaceMat(layout, view);

        _seen.Clear();
        MirrorBoard(layout.P1Board, view, layout);
        MirrorBoard(layout.P2Board, view, layout);
        if (layout.Deck != null) Mirror(layout.Deck, Kind.Deck, view, layout);
        if (layout.P2Deck != null) Mirror(layout.P2Deck, Kind.Deck, view, layout);

        if (layout.Canvas != null)
        {
            foreach (string name in new[] { "CentreLineLeft", "CentreLineRight" })
                if (layout.Canvas.GetNodeOrNull<Control>(name) is Control line) Mirror(line, Kind.Line, view, layout);
            if (layout.Canvas.GetNodeOrNull<Control>("CentreRing") is Control ring) Mirror(ring, Kind.Ring, view, layout);
        }

        // Cards in flight and impact rings live on the scene root, above everything else.
        Node root = GetParent();
        if (root != null)
        {
            foreach (Node child in root.GetChildren())
            {
                if (child is not Control piece || !piece.HasMeta(FlyingMeta) || !piece.IsVisibleInTree()) continue;
                if (piece is TextureRect flier)
                {
                    if (IsPrerendered(flier)) Mirror(flier, Kind.Card, view, layout, flying: true);
                }
                else if (piece is Panel ring) Mirror(ring, Kind.Ring, view, layout, flying: true);
            }
        }

        // Anything not seen this frame has gone (a card freed, a set cleared, a layout swapped).
        List<ulong> gone = null;
        foreach (KeyValuePair<ulong, Piece> pair in _pieces)
        {
            if (_seen.Contains(pair.Key)) continue;
            (gone ??= new List<ulong>()).Add(pair.Key);
        }
        if (gone != null)
        {
            foreach (ulong id in gone)
            {
                FreePiece(_pieces[id]);
                _pieces.Remove(id);
            }
        }
    }

    private void PlaceCamera(Vector2 view)
    {
        float restDistance = view.Y / (2f * PixelsPerUnit * Mathf.Tan(Mathf.DegToRad(FieldOfView) / 2f));
        float t = EaseOutCubic(_swing);
        float yaw = Mathf.DegToRad(Mathf.Lerp(SwingStartYaw, 0f, t));
        float pitch = Mathf.DegToRad(Mathf.Lerp(SwingStartPitch, RestPitch, t));
        float distance = restDistance * Mathf.Lerp(SwingStartDistance, 1f, t);

        Vector3 offset = new Vector3(Mathf.Sin(pitch) * Mathf.Sin(yaw), Mathf.Cos(pitch), Mathf.Sin(pitch) * Mathf.Cos(yaw)) * distance;
        Vector3 jitter = Vector3.Zero;
        if (_shake > 0f)
        {
            _shake = Mathf.Max(0f, _shake - (float)GetProcessDeltaTime());
            float k = _shakeStrength * (_shake / ShakeSeconds);
            jitter = new Vector3((float)_rng.NextDouble() - 0.5f, 0f, (float)_rng.NextDouble() - 0.5f) * 2f * k;
        }
        Transform3D next = new Transform3D(Basis.Identity, offset + jitter).LookingAt(jitter, Vector3.Up);
        if (!next.IsEqualApprox(_camera.Transform)) _camera.Transform = next;
    }

    /// A Modifier landing: the camera jolts with the board (TableUi.JoltBoard calls this).
    public void Shake(float strength)
    {
        if (!_active || GameSettings.BatterySaver) return;
        _shake = ShakeSeconds;
        _shakeStrength = Mathf.Max(_shakeStrength * (_shake / ShakeSeconds), strength);
    }

    /// Where a 2D piece the table draws in 3D actually appears on screen - its 3D quad's corners
    /// through the camera - so the tutorial's spotlight rings the card the player sees, not the
    /// flat rect the layout keeps underneath. False when the piece is not drawn in 3D.
    public bool TryScreenRect(Control control, out Rect2 rect)
    {
        rect = default;
        if (!_active || !Visible || control == null || _camera == null) return false;
        if (!_pieces.TryGetValue(control.GetInstanceId(), out Piece piece) || !piece.Mesh.Visible) return false;

        Transform3D xf = piece.Mesh.GlobalTransform;
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
        foreach (Vector3 corner in new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                                           new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) })
        {
            Vector3 world = xf * corner;
            if (_camera.IsPositionBehind(world)) return false;
            Vector2 p = _camera.UnprojectPosition(world);
            min = new Vector2(Mathf.Min(min.X, p.X), Mathf.Min(min.Y, p.Y));
            max = new Vector2(Mathf.Max(max.X, p.X), Mathf.Max(max.Y, p.Y));
        }
        rect = new Rect2(min, max - min);
        return true;
    }

    private static float EaseOutCubic(float x) => 1f - Mathf.Pow(1f - Mathf.Clamp(x, 0f, 1f), 3f);

    private void PlaceMat(TableLayout layout, Vector2 view)
    {
        // The playmat the 2D table would have shown (ApplyRankTheme picks it), a little larger than
        // the screen so the far corners stay covered once the camera tilts.
        TextureRect bg = layout.Background;
        if (bg != null && _matMaterial.AlbedoTexture != bg.Texture) _matMaterial.AlbedoTexture = bg.Texture;
        if (bg != null) _matMaterial.AlbedoColor = bg.SelfModulate;
        Vector3 scale = new Vector3(view.X / PixelsPerUnit * 1.3f, 1f, view.Y / PixelsPerUnit * 1.3f);
        if (!_mat.Scale.IsEqualApprox(scale)) _mat.Scale = scale;
        if (!_slab.Scale.IsEqualApprox(scale)) _slab.Scale = scale; // Y stays 1: the box carries its own depth
    }

    private void MirrorBoard(Control board, Vector2 view, TableLayout layout)
    {
        if (board == null || !board.IsVisibleInTree()) return;
        foreach (Node slotNode in board.GetChildren())
        {
            if (slotNode is not Control slot || !slot.IsVisibleInTree()) continue;
            if (slot.IsInGroup("editor_preview")) continue;
            Mirror(slot, Kind.Slot, view, layout);
            foreach (Node child in slot.GetChildren())
            {
                if (child is TextureRect card && card.HasMeta("cardId") && card.IsVisibleInTree() && IsPrerendered(card))
                    Mirror(card, Kind.Card, view, layout);
            }
        }
    }

    /// A card view whose whole face is one rendered texture can be drawn in 3D. A card that fell
    /// back to the old built-up face (a blank frame plus number and pip nodes - when its render is
    /// missing or not imported yet) cannot: drawing only its texture shows a blank card
    /// (playtest, 2026-09-30). Those stay 2D, numbers and all. The draw-flight's face-down card
    /// carries no cardId and is always just its texture.
    private static bool IsPrerendered(TextureRect view) =>
        !view.HasMeta("cardId") || view.HasMeta("prerendered");

    // ------------------------------------------------------------------
    // One piece
    // ------------------------------------------------------------------

    private void Mirror(Control control, Kind kind, Vector2 view, TableLayout layout, bool flying = false)
    {
        ulong id = control.GetInstanceId();
        _seen.Add(id);

        // The 2D original stops drawing (it still lays out, animates and takes input).
        if (control.VisibilityLayer != HiddenLayer) control.VisibilityLayer = HiddenLayer;

        if (!_pieces.TryGetValue(id, out Piece piece))
        {
            piece = MakePiece(kind);
            _pieces[id] = piece;
        }

        // Where the 2D layout draws it: centre, size and turn, through its full transform.
        Transform2D xf = control.GetGlobalTransform();
        Vector2 centre = xf * (control.Size / 2f);
        Vector2 across = xf.X * control.Size.X;
        Vector2 down = xf.Y * control.Size.Y;
        float width = across.Length() / PixelsPerUnit;
        float height = down.Length() / PixelsPerUnit;
        float angle = Mathf.Atan2(across.Y, across.X);
        if (width < 0.001f || height < 0.001f) { piece.Mesh.Visible = false; return; }

        // Lift: a card in flight rides above the table, higher the bigger the 2D animation draws
        // it (the Modifier's hover); everything else lies on the felt in a fixed stacking order.
        float scale = Mathf.Sqrt(Mathf.Abs(xf.Determinant()));
        float y = kind switch
        {
            Kind.Line => 0.002f,
            Kind.Ring => 0.004f,
            Kind.Slot => 0.006f,
            Kind.Deck => 0.14f,
            _ => 0.012f,
        };
        if (flying && kind == Kind.Card) y = 0.35f + Mathf.Max(0f, scale - 1f) * 1.6f;
        else if (kind == Kind.Card) y += Mathf.Max(0f, scale - 1f) * 0.8f;

        Vector3 position = new Vector3((centre.X - view.X / 2f) / PixelsPerUnit, y, (centre.Y - view.Y / 2f) / PixelsPerUnit);
        Basis basis = new Basis(Vector3.Up, -angle) * new Basis(Vector3.Right, -Mathf.Pi / 2f) * Basis.FromScale(new Vector3(width, height, 1f));
        Transform3D next = new Transform3D(basis, position);
        if (!next.IsEqualApprox(piece.Mesh.Transform)) piece.Mesh.Transform = next;
        piece.Mesh.Visible = true;


        // What it looks like: the same texture and tint the 2D piece would have drawn.
        Color tint = ModulateUpTo(control, layout.Canvas);
        switch (control)
        {
            case TextureRect rect:
                if (piece.Material.AlbedoTexture != rect.Texture) piece.Material.AlbedoTexture = rect.Texture;
                tint *= rect.SelfModulate;
                break;
            case ColorRect colour:
                tint *= colour.Color;
                break;
            case Panel panel when kind == Kind.Ring:
                ImageTexture outline = RingOutline(control.Size);
                if (piece.Material.AlbedoTexture != outline) piece.Material.AlbedoTexture = outline;
                if (panel.GetThemeStylebox("panel") is StyleBoxFlat style)
                    tint *= style.DrawCenter ? new Color(style.BorderColor, 1f) : style.BorderColor;
                break;
        }
        // Scissored edges while solid (cheap, casts a clean shadow), blended while fading.
        BaseMaterial3D.TransparencyEnum mode = tint.A < 0.99f || kind is Kind.Line or Kind.Ring or Kind.Slot
            ? BaseMaterial3D.TransparencyEnum.Alpha
            : BaseMaterial3D.TransparencyEnum.AlphaScissor;
        if (piece.Material.Transparency != mode) piece.Material.Transparency = mode;
        if (piece.Material.AlbedoColor != tint) piece.Material.AlbedoColor = tint;
        if (piece.StackMaterial != null)
        {
            if (piece.StackMaterial.AlbedoTexture != piece.Material.AlbedoTexture) piece.StackMaterial.AlbedoTexture = piece.Material.AlbedoTexture;
            Color under = tint * (kind == Kind.Card ? new Color(0.45f, 0.45f, 0.5f, 1f) : new Color(0.62f, 0.62f, 0.66f, 1f));
            if (piece.StackMaterial.AlbedoColor != under) piece.StackMaterial.AlbedoColor = under;
            if (piece.StackMaterial.Transparency != mode) piece.StackMaterial.Transparency = mode;
        }

        if (piece.Shine != null)
        {
            // The gloss reads the same face texture for its shape, and the 2D card's own foil
            // strength (TableUi.AddShine) - so effect, rescue and +/- cards shimmer as they do in 2D.
            Texture2D face = piece.Material.AlbedoTexture;
            if (piece.Shine.GetShaderParameter("card_face").AsGodotObject() != face)
                piece.Shine.SetShaderParameter("card_face", face);
            float foil = 0f;
            if (control.GetNodeOrNull<CanvasItem>("Shine")?.Material is ShaderMaterial flat)
                foil = flat.GetShaderParameter("foil_strength").AsSingle();
            piece.Shine.SetShaderParameter("foil_strength", foil);
            piece.Shine.SetShaderParameter("fade", tint.A);
            // A card turning or rising catches the light differently: its turn and lift add tilt.
            Vector2 extra = new Vector2(Mathf.Sin(angle) * 0.6f, Mathf.Max(0f, y - 0.05f) * 0.9f);
            piece.Shine.SetShaderParameter("extra_tilt", extra);
        }

    }

    /// The tint the 2D piece inherits, stopping at the layout's canvas - so fading the 2D layer
    /// in after the swing does not fade the 3D table with it.
    private static Color ModulateUpTo(Control control, Control stop)
    {
        Color c = control.Modulate;
        Node n = control.GetParent();
        while (n is CanvasItem item && n != stop)
        {
            c *= item.Modulate;
            n = n.GetParent();
        }
        return c;
    }

    private Piece MakePiece(Kind kind)
    {
        StandardMaterial3D material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, // the renders already carry their light
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
            AlphaScissorThreshold = 0.5f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        if (kind == Kind.Slot) material.AlbedoTexture = _slotOutline;
        if (kind == Kind.Line) material.AlbedoTexture = null;

        MeshInstance3D mesh = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = material,
            CastShadow = kind is Kind.Card or Kind.Deck
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mesh);

        Piece piece = new Piece { Mesh = mesh, Material = material };
        if (kind == Kind.Card)
        {
            // A card has an edge: a darker copy of its face a hair underneath, so a card on the
            // table or in flight reads as a thing with thickness, rounded corners and all.
            piece.StackMaterial = (StandardMaterial3D)material.Duplicate();
            MeshInstance3D edge = new MeshInstance3D
            {
                Mesh = mesh.Mesh,
                MaterialOverride = piece.StackMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            edge.Position = new Vector3(0f, 0f, -CardThickness);
            mesh.AddChild(edge);

            if (_shineShader != null)
            {
                piece.Shine = new ShaderMaterial { Shader = _shineShader };
                MeshInstance3D shine = new MeshInstance3D
                {
                    Mesh = mesh.Mesh,
                    MaterialOverride = piece.Shine,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                shine.Position = new Vector3(0f, 0f, 0.002f);
                mesh.AddChild(shine);
            }
        }
        if (kind == Kind.Deck)
        {
            // The deck is a stack: more copies of the back under the top one, darker, each a
            // hair lower - rounded corners all the way down, and an edge that catches the eye
            // as the camera swings past. Children of the top card, so they follow it for free
            // (its local +Z is the table's up).
            piece.StackMaterial = (StandardMaterial3D)material.Duplicate();
            for (int i = 1; i <= DeckLayers; i++)
            {
                MeshInstance3D layer = new MeshInstance3D
                {
                    Mesh = mesh.Mesh,
                    MaterialOverride = piece.StackMaterial,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                layer.Position = new Vector3(0f, 0f, -i * DeckLayerGap);
                mesh.AddChild(layer);
            }
        }
        return piece;
    }

    private const float CardThickness = 0.016f;
    private const int DeckLayers = 6;
    private const float DeckLayerGap = 0.022f;

    private static void FreePiece(Piece piece) => piece.Mesh?.QueueFree();

    // ------------------------------------------------------------------
    // Outlines (the empty slot, the centre ring, the impact ring), drawn once into textures
    // ------------------------------------------------------------------

    private ImageTexture RingOutline(Vector2 size)
    {
        // One texture per shape: the centre ring is a capsule, an impact ring a rounded card.
        Vector2I px = new Vector2I(Mathf.Clamp((int)size.X, 16, 512), Mathf.Clamp((int)size.Y, 16, 512));
        if (_ringOutlines.TryGetValue(px, out ImageTexture cached)) return cached;
        float radius = size.X > size.Y * 1.3f ? px.Y / 2f - 1f : px.X * 0.14f;
        ImageTexture made = Outline(px, radius, 4f, Colors.White, new Color(1, 1, 1, 0));
        _ringOutlines[px] = made;
        return made;
    }

    private static ImageTexture Outline(Vector2I size, float radius, float thickness, Color line, Color fill)
    {
        Image image = Image.CreateEmpty(size.X, size.Y, false, Image.Format.Rgba8);
        Vector2 half = new Vector2(size.X, size.Y) / 2f;
        for (int y = 0; y < size.Y; y++)
        {
            for (int x = 0; x < size.X; x++)
            {
                // Signed distance to a rounded rectangle inset by half the line.
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f) - half;
                Vector2 q = new Vector2(Mathf.Abs(p.X), Mathf.Abs(p.Y)) - (half - new Vector2(radius + 1f, radius + 1f));
                float outside = new Vector2(Mathf.Max(q.X, 0f), Mathf.Max(q.Y, 0f)).Length();
                float d = outside + Mathf.Min(Mathf.Max(q.X, q.Y), 0f) - radius;
                float lineA = Mathf.Clamp(thickness / 2f + 0.5f - Mathf.Abs(d + thickness / 2f), 0f, 1f);
                float fillA = d < 0f ? 1f : 0f;
                Color c = fill * new Color(1, 1, 1, fillA);
                c = c.Lerp(line, lineA);
                if (lineA <= 0f && fillA <= 0f) c = new Color(0, 0, 0, 0);
                image.SetPixel(x, y, c);
            }
        }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }
}
