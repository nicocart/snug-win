using System.Collections.Generic;
using System.Runtime.InteropServices;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Core.Touch;

namespace SnugWin.Windows.Input;

/// <summary>
/// Moves the Windows mouse pointer safely back onto a remaining display when virtual screens
/// retire or enter reconnect grace (upstream Snug v2.7.0/v2.7.1 CursorHome / PR #29 #36).
/// </summary>
public static class CursorHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    public static PointD? GetPosition() =>
        GetCursorPos(out var pt) ? new PointD(pt.X, pt.Y) : null;

    public static bool BringHomeIfOutside(IReadOnlyList<RectD> kept, RectD main)
    {
        if (GetPosition() is not { } cur) return false;
        if (CursorHome.Landing(cur, kept, main) is not { } target) return false;
        return SetCursorPos((int)System.Math.Round(target.X), (int)System.Math.Round(target.Y));
    }

    public static bool KeepOff(IReadOnlyList<RectD> visible)
    {
        if (GetPosition() is not { } cur) return false;
        if (CursorHome.Edge(cur, visible) is not { } target) return false;
        return SetCursorPos((int)System.Math.Round(target.X), (int)System.Math.Round(target.Y));
    }
}
