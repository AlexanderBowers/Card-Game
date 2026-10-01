using Godot;
using System;

/// How cards MOVE on the table: a drawn card flying face down off its owner's deck, a Modifier
/// lifted out of the hand and slammed onto the board, the ring and the jolt when it lands.
///
/// TableUi decides which slot a card goes into and when; this only animates the trip there. With
/// Options > Battery's card animations off, every trip is skipped and the card simply appears,
/// with its sound.
public sealed class CardMotion
{
    private readonly TableUi _ui;

    /// The scene node the flights are parented to, so they draw above everything.
    private readonly Node _root;

    private readonly AudioStreamPlayer _sfxSlide;
    private readonly AudioStreamPlayer _sfxPlace;
    private readonly AudioStreamPlayer _sfxImpact;  // a Modifier slammed down from the hand

    public CardMotion(TableUi ui, Node root, AudioStreamPlayer slide, AudioStreamPlayer place, AudioStreamPlayer impact)
    {
        _ui = ui;
        _root = root;
        _sfxSlide = slide;
        _sfxPlace = place;
        _sfxImpact = impact;
    }

    private TableLayout L => _ui.Layout;
    private CardViews Cards => _ui.Cards;
    private TableWorld3D World3D => _ui.World3D;
    private Color DeckBackTint => _ui.DeckBackTint;

    // ------------------------------------------------------------------
    // Playing a Modifier (release playtest, 2026-09-29)
    //
    // Drawing is a face-down card sliding off the deck. Playing a Modifier is a decision, so it
    // should feel like one: the card lifts out of the hand face up, swings over the board,
    // hangs there for a beat, then slams down - a thump, a squash, a ring on the table and the
    // board jolting under it. About half a second from tap to impact.
    // ------------------------------------------------------------------
    public readonly struct HandSpot
    {
        public readonly Vector2 Centre;
        public readonly Vector2 Size;
        public readonly float Rotation;

        public HandSpot(Vector2 centre, Vector2 size, float rotation)
        {
            Centre = centre;
            Size = size;
            Rotation = rotation;
        }
    }

    /// Where a card is drawn in either hand right now - through the full transform, because a
    /// side may be turned round and a picked-up card is lifted and scaled. Falls back to the
    /// middle of the owner's hand when the card's own view cannot be found.
    public HandSpot? HandSpotOf(Card card)
    {
        foreach (Control hand in new Control[] { L?.P1Hand, L?.P2Hand })
        {
            if (hand == null) continue;
            TextureRect view = FindViewIn(hand, card);
            if (view == null) continue;
            Transform2D t = view.GetGlobalTransform();
            Vector2 centre = t * (view.Size / 2f);
            Vector2 size = view.Size * t.Scale.Abs();
            view.Modulate = new Color(1, 1, 1, 0); // it has left the hand: no double while it flies
            return new HandSpot(centre, size, Mathf.RadToDeg(t.Rotation));
        }
        return null;
    }

    private static TextureRect FindViewIn(Node parent, Card card)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is TextureRect view && view.HasMeta("cardId") && view.GetMeta("cardId").AsInt32() == card.Id)
                return view;
            TextureRect deeper = FindViewIn(child, card);
            if (deeper != null) return deeper;
        }
        return null;
    }

    public void AnimateModifierPlay(Card card, Control realCard, float delay, Vector2 cardSize,
                                     HandSpot from, Control board)
    {
        if (!GodotObject.IsInstanceValid(realCard)) return;
        if (!GameSettings.CardAnimations)
        {
            RevealWithoutFlight(realCard);
            _sfxImpact?.Play();
            return;
        }

        // The card itself, face up, flying - not a face-down stand-in.
        TextureRect flier = Cards.CreateCardView(card, cardSize, board == L?.P2Board);
        flier.MouseFilter = Control.MouseFilterEnum.Ignore;
        flier.Size = cardSize;
        flier.PivotOffset = cardSize / 2f;
        flier.SetMeta(TableWorld3D.FlyingMeta, true);
        _root.AddChild(flier);

        Vector2 startScale = new Vector2(from.Size.X / Mathf.Max(1f, cardSize.X), from.Size.Y / Mathf.Max(1f, cardSize.Y));
        flier.GlobalPosition = from.Centre - cardSize / 2f;
        flier.Scale = startScale;
        flier.RotationDegrees = from.Rotation;

        Vector2 target = realCard.GetGlobalTransform() * (realCard.Size / 2f);
        float targetRotation = Mathf.RadToDeg(realCard.GetGlobalTransform().Rotation);
        Vector2 landScale = Vector2.One;
        if (FindBoardOf(realCard) is PerspectiveBoard tilted)
        {
            Vector2 flat = target;
            target = tilted.Warp(flat);
            float w = tilted.Tilt ? tilted.WidthScaleAt(flat) : 1f;
            landScale = new Vector2(w, tilted.Tilt ? tilted.Depth : 1f);
        }

        // Lift out of the hand toward the table, then hang above the slot, bigger (closer to you).
        Vector2 toward = (target - from.Centre).Normalized();
        Vector2 lifted = from.Centre + toward * 60f;
        Vector2 hover = target - toward * 24f;
        Vector2 big = landScale * 1.38f;
        float tiltIn = targetRotation + (toward.X >= 0f ? 7f : -7f); // a little swing into the throw
        bool effect = card.Effect != CardEffect.None;

        Tween tween = _root.GetTree().CreateTween();
        if (delay > 0f) tween.TweenInterval(delay);

        // 1. Lift (0.12s)
        tween.TweenCallback(Callable.From(() => _sfxSlide?.Play()));
        tween.SetParallel(true);
        tween.TweenProperty(flier, "global_position", lifted - cardSize / 2f, 0.12f)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(flier, "scale", startScale * 1.15f, 0.12f)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);

        // 2. Swing over the slot (0.20s)
        tween.Chain().TweenProperty(flier, "global_position", hover - cardSize / 2f, 0.20f)
             .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        tween.TweenProperty(flier, "scale", big, 0.20f)
             .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(flier, "rotation_degrees", tiltIn, 0.20f)
             .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        // 3. A beat in the air, then the slam (0.07s, accelerating)
        tween.Chain().TweenInterval(0.05f);
        tween.Chain().TweenProperty(flier, "global_position", target - cardSize / 2f, 0.07f)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tween.TweenProperty(flier, "scale", landScale * 0.92f, 0.07f)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tween.TweenProperty(flier, "rotation_degrees", targetRotation, 0.07f)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);

        // 4. Impact: the real card takes over, squashed, and springs back.
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            flier.QueueFree();
            _sfxPlace?.Play();
            _sfxImpact?.Play();
            if (!GodotObject.IsInstanceValid(realCard)) return;
            realCard.Modulate = Colors.White;
            realCard.PivotOffset = realCard.Size / 2f;
            realCard.Scale = new Vector2(1.08f, 0.9f);
            Tween settle = realCard.CreateTween();
            settle.TweenProperty(realCard, "scale", Vector2.One, 0.22f)
                  .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            ImpactRing(target, cardSize * landScale, effect);
            JoltBoard(board, effect ? 9f : 6f);
        }));
    }

    /// A rounded outline that bursts out from under the card and fades - the shockwave.
    private void ImpactRing(Vector2 centre, Vector2 size, bool effect)
    {
        StyleBoxFlat ringStyle = new StyleBoxFlat
        {
            DrawCenter = false,
            BorderColor = effect ? new Color(1f, 0.82f, 0.35f, 0.95f) : new Color(1f, 1f, 1f, 0.85f),
        };
        ringStyle.SetBorderWidthAll(4);
        ringStyle.SetCornerRadiusAll(Mathf.RoundToInt(size.X * 0.14f));
        Panel ring = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Size = size, PivotOffset = size / 2f };
        ring.AddThemeStyleboxOverride("panel", ringStyle);
        ring.SetMeta(TableWorld3D.FlyingMeta, true);
        _root.AddChild(ring);
        ring.GlobalPosition = centre - size / 2f;

        Tween t = ring.CreateTween().SetParallel();
        t.TweenProperty(ring, "scale", new Vector2(1.55f, 1.55f), 0.32f)
         .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        t.TweenProperty(ring, "modulate:a", 0f, 0.32f)
         .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        t.Chain().TweenCallback(Callable.From(ring.QueueFree));
    }

    /// The board jumps under the card: a few quick, shrinking nudges, then exactly back.
    private void JoltBoard(Control board, float strength)
    {
        if (board == null || !GodotObject.IsInstanceValid(board)) return;
        if (board.HasMeta("jolting")) return; // one at a time; a second would drift the rest spot
        World3D?.Shake(strength * 0.006f);       // on the 3D table the camera takes the hit too
        board.SetMeta("jolting", true);
        Vector2 rest = board.Position;
        Tween t = board.CreateTween();
        float[] ys = { strength, -strength * 0.6f, strength * 0.3f, 0f };
        foreach (float y in ys)
            t.TweenProperty(board, "position", rest + new Vector2(0f, y), 0.045f)
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        t.TweenCallback(Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(board)) return;
            board.Position = rest;
            board.RemoveMeta("jolting");
        }));
    }

    public void AnimateCardDrop(Control realCard, float delay, Vector2 cardSize, Control fromDeck)
    {
        // Fallback in case the deck isn't assigned in the inspector
        if (fromDeck == null || !GodotObject.IsInstanceValid(realCard))
        {
            if (GodotObject.IsInstanceValid(realCard)) realCard.Modulate = Colors.White;
            return;
        }

        // Options > Battery: no flight. The card appears in place, with its sound, after the
        // same stagger - so a two-card opening still lands one card then the other.
        if (!GameSettings.CardAnimations)
        {
            if (delay <= 0f) { RevealWithoutFlight(realCard); return; }
            _root.GetTree().CreateTimer(delay).Timeout += () => RevealWithoutFlight(realCard);
            return;
        }

        // 1. A face-down card that flies from the deck to the slot
        TextureRect fakeCard = new TextureRect
        {
            Texture = (fromDeck as TextureRect)?.Texture ?? Cards.CardBack,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Size = cardSize,
            PivotOffset = cardSize / 2f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SelfModulate = (fromDeck as TextureRect)?.SelfModulate ?? DeckBackTint,
        };
        fakeCard.SetMeta(TableWorld3D.FlyingMeta, true);
        _root.AddChild(fakeCard); // on the scene root so it draws above everything

        // 2. Start on the deck, small and upside down
        Vector2 deckCenter = fromDeck.GetGlobalTransform() * (fromDeck.Size / 2f);
        fakeCard.GlobalPosition = deckCenter - cardSize / 2f;
        fakeCard.RotationDegrees = -180f;
        fakeCard.Scale = new Vector2(0.5f, 0.5f);

        // Target the slot's visual centre. Player 2's side may be rotated 180 degrees, so
        // go through the full global transform instead of GlobalPosition.
        Vector2 targetCenter = realCard.GetGlobalTransform() * (realCard.Size / 2f);
        float targetRotation = Mathf.RadToDeg(realCard.GetGlobalTransform().Rotation);

        // A tilted board draws the card somewhere else than its slot's flat rect (pass 39), and
        // narrower: aim for where it will be SEEN, at the size it will be seen.
        Vector2 landScale = Vector2.One;
        if (FindBoardOf(realCard) is PerspectiveBoard tilted)
        {
            Vector2 flatCenter = targetCenter;
            targetCenter = tilted.Warp(flatCenter);
            float s = tilted.Tilt ? tilted.WidthScaleAt(flatCenter) : 1f;
            landScale = new Vector2(s, tilted.Tilt ? tilted.Depth : 1f);
        }

        // 3. Fly, spin and grow at the same time, then reveal the real card
        Tween tween = _root.GetTree().CreateTween();
        tween.SetParallel(true);

        // The stagger sits in its own step, and Chain() forces the flight into the NEXT one.
        // Without the Chain the first property tweener would join the interval's step - that is
        // what SetParallel(true) means - and the card would fly during the pause instead of after.
        if (delay > 0f)
        {
            tween.TweenInterval(delay);
            tween.Chain();
        }

        // The slide belongs to the moment the card LEAVES the deck, not the moment the tween is
        // built. Played up front, a staggered card announces itself before it moves.
        tween.TweenCallback(Callable.From(() => { if (_sfxSlide != null) _sfxSlide.Play(); }));
        tween.TweenProperty(fakeCard, "global_position", targetCenter - cardSize / 2f, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(fakeCard, "rotation_degrees", targetRotation, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(fakeCard, "scale", landScale, 0.35f)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            fakeCard.QueueFree();
            if (GodotObject.IsInstanceValid(realCard)) realCard.Modulate = Colors.White;
            if (_sfxPlace != null) _sfxPlace.Play();
        }));
    }

    private static Control FindBoardOf(Node node)
    {
        for (Node at = node?.GetParent(); at != null; at = at.GetParent())
            if (at is GridContainer grid) return grid;
        return null;
    }

    private void RevealWithoutFlight(Control realCard)
    {
        if (!GodotObject.IsInstanceValid(realCard)) return;
        realCard.Modulate = Colors.White;
        if (_sfxPlace != null) _sfxPlace.Play();
    }
}
