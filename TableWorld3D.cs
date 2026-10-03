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
    private MeshInstance3D _mat;
    private ShaderMaterial _boardMaterial;   // board_3d.gdshader: the art, its shading and its live layer
    private Vector2 _matSize;                   // the size the board mesh was last built at
    private Texture2D _boardTexture;

    /// How deep the board is, in world units (one unit = PixelsPerUnit layout pixels, so 0.5 is
    /// about half a card's width). A mat lying on the floor read as paper (2026-10-02); 0.8 read
    /// as a heavy wall once the board was rounded and light.
    private const float TableThickness = 0.5f;

    /// The board's corner radius as a fraction of its short side, and the radius of its rounded
    /// top edge. MatCorner must match BOARD_CORNER in tools/cardgen/cardgen.py, which paints the
    /// playmat with exactly this outline (and the room colour outside it).
    private const float MatCorner = 0.10f;
    private const float EdgeRadius = 0.14f;


    /// The room round the board: near-white, like the playmats' corners (playtest, 2026-10-02:
    /// "I don't like that grey background ... more of a whiter background").
    public static readonly Color RoomColor = new Color(0.933f, 0.945f, 0.965f);
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
        public MeshInstance3D Shadow;            // cards and decks: the soft blob on the table
        public StandardMaterial3D ShadowMaterial;
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

        // No light and no shadow map (2026-10-02). Everything on the table is unshaded - the
        // renders carry their own light, the board's edge is shaded by vertex colour - and each
        // card or deck casts a soft blob shadow of its own (see BlobShadow). The shadow map drew
        // a huge black wedge across the board on the S25's GPU, and it cost battery anyway.

        Godot.Environment environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = RoomColor,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
            AmbientLightEnergy = 0.5f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        _env = new WorldEnvironment { Environment = environment };
        AddChild(_env);

        // The board: one rounded slab whose top is the playmat (BuildBoard). Its rounded edge and
        // sides take their colour from the mat's own rim, so every board's edge matches its art.
        _boardMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://board_3d.gdshader") };
        _mat = new MeshInstance3D { MaterialOverride = _boardMaterial };
        _mat.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_mat);

        // The room: a flat, unlit near-white floor the same colour as the background, so there is
        // no horizon - the board just sits in a bright space.
        StandardMaterial3D floorMaterial = new StandardMaterial3D
        {
            AlbedoColor = RoomColor,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        MeshInstance3D floor = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(400f, 400f) },
            MaterialOverride = floorMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        floor.Position = new Vector3(0f, -TableThickness - 0.02f, 0f);
        AddChild(floor);

        // Slots are inked, not white: the boards are light (2026-10-02).
        _slotOutline = Outline(new Vector2I(140, 190), 14f, 3f, new Color(0.29f, 0.37f, 0.47f, 0.4f), new Color(0.29f, 0.37f, 0.47f, 0.06f));
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
        // The playmat the 2D table would have shown (ApplyRankTheme picks it).
        TextureRect bg = layout.Background;
        if (bg != null && bg.Texture != _boardTexture)
        {
            _boardTexture = bg.Texture;
            _boardMaterial.SetShaderParameter("board_tex", _boardTexture);
        }
        // The board is the screen's size, so its rim runs along the screen's edges where the table
        // is at layout scale; tilted, the far corners come into view with the room beyond them.
        Vector2 size = view / PixelsPerUnit;
        if (!size.IsEqualApprox(_matSize))
        {
            _matSize = size;
            _mat.Mesh = BuildBoard(size.X, size.Y);
            _boardMaterial.SetShaderParameter("board_size", size);
        }
    }

    /// The board: a rounded rectangle (MatCorner) with a rounded top edge (EdgeRadius) and straight
    /// sides down to TableThickness, top at y = 0. The top is UV-mapped to the whole playmat; the
    /// edge and sides reuse the UV just inside the rim, so they wear the rim's colour.
    private static ArrayMesh BuildBoard(float w, float h)
    {
        const int cornerSegments = 12;
        const int edgeSegments = 5;
        float corner = MatCorner * Mathf.Min(w, h);
        float edge = Mathf.Min(EdgeRadius, corner * 0.5f);

        // The outline, as corner centres + outward directions, counter-clockwise from +X.
        List<(Vector2 centre, Vector2 dir)> outline = new List<(Vector2, Vector2)>();
        Vector2[] centres =
        {
            new Vector2(w / 2 - corner, h / 2 - corner), new Vector2(-w / 2 + corner, h / 2 - corner),
            new Vector2(-w / 2 + corner, -h / 2 + corner), new Vector2(w / 2 - corner, -h / 2 + corner),
        };
        for (int c = 0; c < 4; c++)
        {
            for (int i = 0; i <= cornerSegments; i++)
            {
                float a = Mathf.DegToRad(c * 90f + 90f * i / cornerSegments);
                outline.Add((centres[c], new Vector2(Mathf.Cos(a), Mathf.Sin(a))));
            }
        }

        Vector2 Uv(Vector2 p) => new Vector2(p.X / w + 0.5f, p.Y / h + 0.5f);
        // The rim's colour: a little inside the outline, past the anti-aliased edge of the art.
        float rimInset = 0.03f * Mathf.Min(w, h); // inside the rim band (cardgen BOARD_RIM is 0.045)

        SurfaceTool st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        // Top: a fan from the middle to the outline inset by the edge radius.
        int n = outline.Count;
        Vector2 Ring(int i, float inset) => outline[i].centre + outline[i].dir * (corner - inset);
        for (int i = 0; i < n; i++)
        {
            Vector2 a = Ring(i, edge), b = Ring((i + 1) % n, edge);
            foreach (Vector2 p in new[] { Vector2.Zero, b, a })
            {
                st.SetColor(Colors.White);
                st.SetNormal(Vector3.Up);
                st.SetUV(Uv(p));
                st.AddVertex(new Vector3(p.X, 0f, p.Y));
            }
        }

        // The rounded edge (a quarter circle) and then the straight side.
        List<(float inset, float drop, float up)> profile = new List<(float, float, float)>();
        for (int k = 0; k <= edgeSegments; k++)
        {
            float t = Mathf.Pi / 2f * k / edgeSegments;
            profile.Add((edge * (1f - Mathf.Sin(t)), edge * (1f - Mathf.Cos(t)), Mathf.Cos(t)));
        }
        profile.Add((0f, TableThickness, 0f));

        for (int r = 0; r < profile.Count - 1; r++)
        {
            var (i0, d0, u0) = profile[r];
            var (i1, d1, u1) = profile[r + 1];
            if (r == profile.Count - 2) u0 = 0f; // the side is straight down: flat normals
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Vector2 pa0 = Ring(i, i0), pb0 = Ring(j, i0), pa1 = Ring(i, i1), pb1 = Ring(j, i1);
                Vector3 Normal(int idx, float up) =>
                    new Vector3(outline[idx].dir.X * Mathf.Sqrt(1f - up * up), up, outline[idx].dir.Y * Mathf.Sqrt(1f - up * up));
                void V(Vector2 p, float drop, int idx, float up)
                {
                    // The board is unshaded, so its shape is in the vertex colour: full on top,
                    // shading off round the edge, and the sides a step darker.
                    float shade = Mathf.Lerp(0.8f, 1f, up);
                    st.SetColor(new Color(shade, shade, shade));
                    st.SetNormal(Normal(idx, up));
                    st.SetUV(Uv(Ring(idx, rimInset)));
                    st.AddVertex(new Vector3(p.X, -drop, p.Y));
                }
                V(pa0, d0, i, u0); V(pb0, d0, j, u0); V(pb1, d1, j, u1);
                V(pa0, d0, i, u0); V(pb1, d1, j, u1); V(pa1, d1, i, u1);
            }
        }

        return st.Commit();
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
        if (width < 0.001f || height < 0.001f)
        {
            piece.Mesh.Visible = false;
            if (piece.Shadow != null) piece.Shadow.Visible = false;
            return;
        }

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

        if (piece.Shadow != null) PlaceShadow(piece, position, angle, width, height, tint.A);

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
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mesh);

        Piece piece = new Piece { Mesh = mesh, Material = material };
        if (kind is Kind.Card or Kind.Deck) MakeShadow(piece);
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

    private static void FreePiece(Piece piece)
    {
        piece.Mesh?.QueueFree();
        piece.Shadow?.QueueFree();
    }

    // ------------------------------------------------------------------
    // Blob shadows (2026-10-02, replacing the shadow map)
    //
    // Each card and deck lays a soft rounded shadow on the table, a little down and to the right
    // of it. The higher a card flies, the further the shadow falls, the wider and fainter it gets
    // - which is all a shadow map was doing for us, without a map to go wrong on a phone GPU.
    // ------------------------------------------------------------------
    private const float ShadowPad = 0.2f;        // blob texture margin, as a fraction of each side
    private static ImageTexture _blob;

    private void MakeShadow(Piece piece)
    {
        _blob ??= BlobTexture();
        piece.ShadowMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoTexture = _blob,
            AlbedoColor = new Color(0.12f, 0.16f, 0.24f, 0f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        };
        piece.Shadow = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = piece.ShadowMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(piece.Shadow);
    }

    private static void PlaceShadow(Piece piece, Vector3 position, float angle, float width, float height, float alpha)
    {
        float lift = Mathf.Max(0f, position.Y);
        float spread = (1f + lift * 0.45f) / (1f - 2f * ShadowPad);
        Vector3 at = new Vector3(position.X + 0.03f + lift * 0.28f, 0.008f, position.Z + 0.05f + lift * 0.4f);
        Basis basis = new Basis(Vector3.Up, -angle) * new Basis(Vector3.Right, -Mathf.Pi / 2f)
                      * Basis.FromScale(new Vector3(width * spread, height * spread, 1f));
        Transform3D next = new Transform3D(basis, at);
        if (!next.IsEqualApprox(piece.Shadow.Transform)) piece.Shadow.Transform = next;
        piece.Shadow.Visible = true;
        float strength = Mathf.Lerp(0.34f, 0.12f, Mathf.Clamp(lift / 1.6f, 0f, 1f)) * alpha;
        Color c = piece.ShadowMaterial.AlbedoColor;
        if (!Mathf.IsEqualApprox(c.A, strength)) piece.ShadowMaterial.AlbedoColor = new Color(c, strength);
    }

    /// A soft rounded rectangle in the middle of a transparent margin (ShadowPad each side).
    private static ImageTexture BlobTexture()
    {
        const int w = 112, h = 144;
        Image image = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        Vector2 core = new Vector2(w, h) * (0.5f - ShadowPad);
        float radius = w * (1f - 2f * ShadowPad) * 0.14f;
        float soft = w * ShadowPad * 0.9f;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Vector2 p = new Vector2(x + 0.5f - w / 2f, y + 0.5f - h / 2f);
                Vector2 q = new Vector2(Mathf.Abs(p.X), Mathf.Abs(p.Y)) - core + new Vector2(radius, radius);
                float d = new Vector2(Mathf.Max(q.X, 0f), Mathf.Max(q.Y, 0f)).Length()
                          + Mathf.Min(Mathf.Max(q.X, q.Y), 0f) - radius;
                float a = 1f - Mathf.SmoothStep(-soft * 0.35f, soft, d);
                image.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

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
