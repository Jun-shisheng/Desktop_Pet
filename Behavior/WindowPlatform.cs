using System;
using System.Collections.Generic;
using System.Windows;
using Point = System.Windows.Point;

namespace DeepSeekFishPet.Behavior;

/// <summary>
/// Read-only screen-pixel snapshot of an application window. Occluders are
/// windows higher in the Z order; the pet only lands on an exposed top edge.
/// </summary>
public readonly record struct WindowPlatform(
    IntPtr Handle, Rect Bounds, bool CanStand,
    IReadOnlyList<Rect>? OccluderBounds = null)
{
    public bool HasExposedTopAt(double footX)
    {
        if (!CanStand || Bounds.IsEmpty ||
            footX < Bounds.Left + 8 || footX > Bounds.Right - 8)
            return false;

        var point = new Point(footX, Bounds.Top + 2);
        if (OccluderBounds is not null)
            foreach (Rect blocker in OccluderBounds)
                if (blocker.Contains(point)) return false;
        return true;
    }

    public bool Covers(double x, double y) => Bounds.Contains(new Point(x, y));
}
