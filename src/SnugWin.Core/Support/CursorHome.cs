using System;
using System.Collections.Generic;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Touch;

namespace SnugWin.Core.Support;

/// <summary>
/// Pure helper computing where to place the mouse cursor when virtual screens retire,
/// enter reconnect grace, or stay hidden while idle so the pointer never gets stranded
/// or wanders onto an invisible display.
/// </summary>
public static class CursorHome
{
    public static PointD? Landing(PointD pointer, IReadOnlyList<RectD> kept, RectD main)
    {
        foreach (var rect in kept)
        {
            if (rect.Width > 0 && rect.Height > 0 && rect.Contains(pointer))
                return null;
        }

        RectD target = main.Width > 0 && main.Height > 0
            ? main
            : FirstNonEmpty(kept);

        if (target.Width <= 0 || target.Height <= 0)
            return null;

        return new PointD(target.X + target.Width / 2.0, target.Y + target.Height / 2.0);
    }

    /// <summary>
    /// When the pointer crosses into a hidden virtual screen while idle, clamp it back
    /// to the nearest interior pixel [MinX..MaxX-1, MinY..MaxY-1] of the closest visible rect.
    /// Returns null if the pointer is already inside a visible rect or no non-empty visible rect exists.
    /// </summary>
    public static PointD? Edge(PointD pointer, IReadOnlyList<RectD> visible)
    {
        foreach (var rect in visible)
        {
            // Half-open [MinX, MaxX) x [MinY, MaxY) matches Windows pixel bounds and upstream CGRect.contains.
            if (rect.Width > 0 && rect.Height > 0 && ContainsHalfOpen(rect, pointer))
                return null;
        }

        PointD? best = null;
        double bestDist = double.PositiveInfinity;
        foreach (var rect in visible)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;

            double maxX = Math.Max(rect.MinX, rect.MaxX - 1.0);
            double maxY = Math.Max(rect.MinY, rect.MaxY - 1.0);
            var clamped = new PointD(
                Math.Clamp(pointer.X, rect.MinX, maxX),
                Math.Clamp(pointer.Y, rect.MinY, maxY));
            double dist = clamped.DistanceTo(pointer);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = clamped;
            }
        }

        return best;
    }

    private static bool ContainsHalfOpen(RectD rect, PointD p) =>
        p.X >= rect.MinX && p.X < rect.MaxX && p.Y >= rect.MinY && p.Y < rect.MaxY;

    private static RectD FirstNonEmpty(IReadOnlyList<RectD> rects)
    {
        foreach (var r in rects)
        {
            if (r.Width > 0 && r.Height > 0)
                return r;
        }
        return default;
    }
}
