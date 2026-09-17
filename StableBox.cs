using Godot;
using System.Collections.Generic;

/// <summary>
/// A container that keeps ONE size while what is inside it changes (Alexander, 2026-09-16:
/// "previewing a Modifier is shifting other elements").
///
/// Every child is laid over the same rect, the way a stack of cards sits in one place. The slot's
/// minimum size is the largest minimum any child has ever asked for - hidden children included -
/// so swapping Draw Card / Hold for Play / Flip Value / Put back, or a score growing from 7/20 to
/// 24/20, no longer pushes the rest of the side around.
///
/// The high-water mark only ever grows. ResetAll() clears it, and the table calls that on a real
/// resize or rotation, where the old size means nothing any more.
/// </summary>
public partial class StableBox : Container
{
    private static readonly List<StableBox> Live = new List<StableBox>();

    private Vector2 _highWater = Vector2.Zero;

    public override void _EnterTree() => Live.Add(this);
    public override void _ExitTree() => Live.Remove(this);

    public static void ResetAll()
    {
        foreach (StableBox box in Live)
        {
            if (!IsInstanceValid(box)) continue;
            box._highWater = Vector2.Zero;
            box.UpdateMinimumSize();
        }
    }

    public override Vector2 _GetMinimumSize()
    {
        Vector2 need = Vector2.Zero;
        foreach (Node node in GetChildren())
        {
            if (node is Control child) need = Bigger(need, child.GetCombinedMinimumSize());
        }
        _highWater = Bigger(_highWater, need);
        return _highWater;
    }

    private static Vector2 Bigger(Vector2 a, Vector2 b) =>
        new Vector2(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y));

    public override void _Notification(int what)
    {
        if (what != NotificationSortChildren) return;
        foreach (Node node in GetChildren())
        {
            if (node is Control child) FitChildInRect(child, new Rect2(Vector2.Zero, Size));
        }
    }
}
