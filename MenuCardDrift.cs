using Godot;
using System;
using System.Collections.Generic;

/// The main menu's background (Alexander, 2026-09-30): Modifier cards scattered at random, each
/// slowly turning front to back as it drifts up the screen - every Modifier in the game, including
/// the ones the player has not unlocked yet, as a quiet preview of what is out there.
///
/// Sits between the menu's board and its panel, ignores input, and only runs while visible.
public partial class MenuCardDrift : Control
{
    private const string ArtDir = "res://assets/aimfor20_art/";
    private const int CardCount = 9;
    private const float CardAspect = 760f / 560f;

    private sealed class Drifter
    {
        public TextureRect View;
        public Texture2D Front;
        public float Phase;      // the turn, in radians: front while cos > 0, back otherwise
        public float TurnSpeed;  // radians per second
        public float Rise;       // pixels per second, upwards
        public float Spin;       // degrees per second
    }

    private readonly List<Texture2D> _fronts = new List<Texture2D>();
    private readonly List<Drifter> _cards = new List<Drifter>();
    private readonly Random _rng = new Random();
    private Texture2D _back;
    private Vector2 _laidOutFor;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        LoadArt();
        Resized += () => Scatter(force: false);
    }

    private void LoadArt()
    {
        var names = new List<string>();
        for (int v = 1; v <= 6; v++)
        {
            names.Add($"cards/mods/plus_{v}.png");
            names.Add($"cards/mods/minus_{v}.png");
            names.Add($"cards/mods/flip_{v}_plus.png");
        }
        foreach (string effect in new[] { "copy", "recall", "shave", "tradehands", "tradetotals", "veto" })
            names.Add($"cards/effect_{effect}.png");
        names.Add("cards/mods/rescue_plus_3.png");
        names.Add("cards/mods/rescue_minus_5.png");

        foreach (string name in names)
        {
            string path = ArtDir + name;
            if (ResourceLoader.Exists(path)) _fronts.Add(GD.Load<Texture2D>(path));
        }
        RefreshBack();
    }

    /// The backs are the player's own deck's, so a deck bought in the Shop shows here too.
    public void RefreshBack()
    {
        string key = RunData.Instance?.SelectedDeck ?? Cosmetics.Default;
        string path = $"{ArtDir}backs/card_back_{key}.png";
        if (!ResourceLoader.Exists(path)) path = ArtDir + "card_back.png";
        _back = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private Vector2 CardSize()
    {
        float w = Mathf.Clamp(Mathf.Min(Size.X, Size.Y) * 0.2f, 90f, 200f);
        return new Vector2(w, w * CardAspect);
    }

    private void Scatter(bool force)
    {
        if (_fronts.Count == 0 || Size.X <= 0 || Size.Y <= 0) return;
        if (!force && _cards.Count > 0 && Size.IsEqualApprox(_laidOutFor)) return;
        _laidOutFor = Size;

        foreach (Drifter old in _cards) old.View.QueueFree();
        _cards.Clear();

        for (int i = 0; i < CardCount; i++)
        {
            Drifter d = new Drifter
            {
                View = new TextureRect
                {
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.Scale,
                    MouseFilter = MouseFilterEnum.Ignore,
                    Modulate = new Color(1f, 1f, 1f, 0.85f),
                },
            };
            AddChild(d.View);
            _cards.Add(d);
            // Spread over the whole height to start with, so the menu never opens empty.
            Respawn(d, (float)_rng.NextDouble() * Size.Y);
        }
    }

    private void Respawn(Drifter d, float y)
    {
        Vector2 size = CardSize() * (0.8f + (float)_rng.NextDouble() * 0.4f);
        d.Front = _fronts[_rng.Next(_fronts.Count)];
        d.Phase = (float)(_rng.NextDouble() * Math.Tau);
        d.TurnSpeed = 0.35f + (float)_rng.NextDouble() * 0.45f;
        d.Rise = 10f + (float)_rng.NextDouble() * 16f;
        d.Spin = ((float)_rng.NextDouble() - 0.5f) * 8f;

        d.View.Size = size;
        d.View.PivotOffset = size / 2f;
        d.View.RotationDegrees = ((float)_rng.NextDouble() - 0.5f) * 36f;
        d.View.Position = new Vector2((float)_rng.NextDouble() * (Size.X - size.X * 0.4f) - size.X * 0.3f, y);
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        if (_cards.Count == 0) { Scatter(force: true); return; }

        float dt = (float)delta;
        foreach (Drifter d in _cards)
        {
            d.Phase += d.TurnSpeed * dt;
            float turn = Mathf.Cos(d.Phase);
            // A card turning about its upright axis: the width follows the cosine, and whichever
            // face is towards us shows.
            d.View.Scale = new Vector2(Mathf.Max(0.02f, Mathf.Abs(turn)), 1f);
            d.View.Texture = turn >= 0f ? d.Front : (_back ?? d.Front);
            d.View.RotationDegrees += d.Spin * dt;
            d.View.Position += new Vector2(0f, -d.Rise * dt);

            // Gone off the top: back in at the bottom, as a different card in a new spot.
            if (d.View.Position.Y + d.View.Size.Y * 1.2f < 0f)
                Respawn(d, Size.Y + d.View.Size.Y * 0.2f);
        }
    }
}
