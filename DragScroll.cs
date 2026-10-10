using Godot;

/// A vertical ScrollContainer you can drag with a finger anywhere on its content, buttons
/// included (size check, 2026-10-06: a menu taller than the screen scrolls instead of shrinking,
/// so the text stays the size it was made bigger for).
///
/// Buttons swallow the touch before a ScrollContainer sees it, so the drag is read in _Input -
/// the same fix the deck screen and the cosmetic shop use. A press that turned into a drag must
/// not also press the button under the finger (the content moves WITH the finger, so the release
/// lands on the same button): that release is swallowed and replayed far off screen, so the
/// button sees its press cancelled - released outside - and draws itself unpressed again.
public partial class DragScroll : ScrollContainer
{
    private const float DragThreshold = 12f;
    private static readonly Vector2 Away = new Vector2(-100000f, -100000f);

    /// Is this one of our own replayed events? Never compared exactly: on a phone Godot rescales
    /// event positions to the stretched canvas (and turns a replayed touch into an emulated mouse
    /// event at the rescaled spot), so a replay arrives at roughly -66000, not -100000. The exact
    /// test missed it, the replay was replayed, and Options froze on the S25 the moment it was
    /// scrolled (2026-10-10: 10,000 replays in under a second, found with a logcat trace).
    private static bool IsAway(Vector2 p) => p.X < -10000f || p.Y < -10000f;

    /// Set while a replay is being fed back in, in case the engine hands it straight back.
    private static bool _replaying;

    private bool _dragging;
    private bool _moved;
    private float _distance;

    /// True from the moment a press turns into a drag until the next press.
    public bool DragMoved => _moved;

    public DragScroll()
    {
        HorizontalScrollMode = ScrollMode.Disabled;
        VerticalScrollMode = ScrollMode.Auto;
        ScrollDeadzone = 8;
        MouseFilter = MouseFilterEnum.Pass;
    }

    private bool CanScroll => GetVScrollBar().MaxValue - GetVScrollBar().Page > 1.0;

    public override void _Input(InputEvent e)
    {
        if (!IsVisibleInTree()) return;
        bool touch = DisplayServer.IsTouchscreenAvailable();
        switch (e)
        {
            case InputEventScreenTouch t when touch:
                if (_replaying || IsAway(t.Position)) return;
                if (Press(t.Pressed, t.Position)) Replay(t);
                break;
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
                if (_replaying || IsAway(mb.Position)) return;
                if (touch) { if (!mb.Pressed && _moved) Replay(mb); return; } // the emulated twin
                if (Press(mb.Pressed, mb.Position)) Replay(mb);
                break;
            case InputEventScreenDrag d when touch && _dragging:
                Drag(d.Relative.Y);
                break;
            case InputEventMouseMotion mm when !touch && _dragging && (mm.ButtonMask & MouseButtonMask.Left) != 0:
                Drag(mm.Relative.Y);
                break;
        }
    }

    /// Returns true when this is the release of a drag, which must not reach the button.
    private bool Press(bool down, Vector2 at)
    {
        if (down)
        {
            _dragging = CanScroll && GetGlobalRect().HasPoint(at) && !InsideInnerScroll(at);
            _moved = false;
            _distance = 0f;
            return false;
        }
        bool wasDrag = _dragging && _moved;
        _dragging = false;
        return wasDrag;
    }

    private void Drag(float dy)
    {
        _distance += Mathf.Abs(dy);
        if (_distance > DragThreshold) _moved = true;
        if (_moved) ScrollVertical -= Mathf.RoundToInt(dy);
    }

    /// A scroll area of its own inside this one (a list in a panel) keeps its own drags.
    private bool InsideInnerScroll(Vector2 at)
    {
        foreach (Node n in FindChildren("*", nameof(ScrollContainer), true, false))
        {
            if (n is ScrollContainer inner && inner != this && inner.IsVisibleInTree()
                && inner.GetGlobalRect().HasPoint(at)
                && inner.GetVScrollBar().MaxValue - inner.GetVScrollBar().Page > 1.0)
                return true;
        }
        return false;
    }

    private void Replay(InputEvent e)
    {
        DebugLog.Count("DragScroll.Replay", e.GetType().Name);
        GetViewport().SetInputAsHandled();
        InputEvent copy = (InputEvent)e.Duplicate();
        if (copy is InputEventScreenTouch t) t.Position = Away;
        if (copy is InputEventMouseButton mb) { mb.Position = Away; mb.GlobalPosition = Away; }
        _replaying = true;
        try { Input.ParseInputEvent(copy); }
        finally { _replaying = false; }
    }
}
