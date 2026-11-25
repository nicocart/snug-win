using System;
using System.Collections.Generic;

namespace SnugWin.Core.PhoneScreen;

/// <summary>
/// Describes where a window currently lives across online displays for the computer-screen App Library.
/// </summary>
public abstract record WindowSpot
{
    private WindowSpot() { }

    /// <summary>Visible on the desktop of the given display.</summary>
    public sealed record Shown(uint DisplayId) : WindowSpot;

    /// <summary>Occupying full-screen on the given display.</summary>
    public sealed record FullScreen(uint DisplayId) : WindowSpot;

    /// <summary>Minimized, cloaked/hidden on another virtual desktop, or center outside any online display.</summary>
    public sealed record Hidden : WindowSpot
    {
        public static readonly Hidden Instance = new();
    }

    public static WindowSpot Resolve(
        RectD frame,
        bool shown,
        IReadOnlyList<(uint Id, RectD Bounds)> displays)
    {
        var center = frame.Center;
        for (int i = 0; i < displays.Count; i++)
        {
            var (id, bounds) = displays[i];
            if (!bounds.Contains(center)) continue;
            if (shown)
                return new Shown(id);
            if (frame.Matches(bounds, tolerance: 2.0))
                return new FullScreen(id);
            return Hidden.Instance;
        }
        return Hidden.Instance;
    }
}
