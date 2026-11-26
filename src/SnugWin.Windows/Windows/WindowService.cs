using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Core.Touch;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Windows;

public sealed class WindowService : IWindowControl
{
    private const int MoveRetries = 6;
    private const int RetryDelayMs = 120;
    private const string SelfProcessExe = "SnugWin.App.exe";
    private static readonly string[] DefaultExcludedProcesses =
    {
        SelfProcessExe,
        "GameViewer*",
        "CrossDesk*",
        "crossdesk_*"
    };
    private static readonly object ExclusionGate = new();
    private static string[] _excludedProcessPatterns = DefaultExcludedProcesses;

    public Task<bool> ExistsAsync(WindowKey window)
    {
        var hwnd = (IntPtr)window.Handle;
        if (!Win32.IsWindow(hwnd)) return Task.FromResult(false);
        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        return Task.FromResult(pid == window.Pid);
    }

    public Task<bool> IsShownAsync(WindowKey window)
    {
        var hwnd = (IntPtr)window.Handle;
        return Task.FromResult(IsWindowOnScreen(hwnd, window.Pid));
    }

    public Task<WindowLook?> LookAsync(WindowKey window)
    {
        var hwnd = (IntPtr)window.Handle;
        if (!Win32.IsWindow(hwnd)) return Task.FromResult<WindowLook?>(null);
        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid != window.Pid) return Task.FromResult<WindowLook?>(null);

        bool minimized = Win32.IsIconic(hwnd);
        RectD bounds;
        if (minimized)
        {
            if (!TryGetNormalBounds(hwnd, out bounds) && !TryGetVisibleBounds(hwnd, out bounds))
                return Task.FromResult<WindowLook?>(null);
        }
        else if (!TryGetVisibleBounds(hwnd, out bounds))
        {
            return Task.FromResult<WindowLook?>(null);
        }

        bool fullScreen = !minimized && IsLikelyFullScreen(hwnd, bounds);
        return Task.FromResult<WindowLook?>(new WindowLook(bounds, fullScreen, minimized));
    }

    public async Task<RectD?> MoveAsync(WindowKey window, RectD targetFrame)
    {
        var hwnd = (IntPtr)window.Handle;
        if (!Win32.IsWindow(hwnd)) return null;

        if (Win32.IsIconic(hwnd))
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        if (Win32.IsZoomed(hwnd))
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

        RectD? lastBounds = null;
        for (int attempt = 0; attempt < MoveRetries; attempt++)
        {
            var (leftPad, topPad, rightPad, bottomPad) = GetShadowMargins(hwnd);
            int x = (int)Math.Round(targetFrame.X - leftPad);
            int y = (int)Math.Round(targetFrame.Y - topPad);
            int w = (int)Math.Round(targetFrame.Width + leftPad + rightPad);
            int h = (int)Math.Round(targetFrame.Height + topPad + bottomPad);

            Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);

            await Task.Delay(RetryDelayMs).ConfigureAwait(false);
            if (TryGetVisibleBounds(hwnd, out var got))
            {
                if (got.Matches(targetFrame, tolerance: 4.0))
                    return got;
                if (attempt >= 1 && lastBounds is { } prev && got.Matches(prev, tolerance: 1.5))
                    return got;
                lastBounds = got;
            }
        }
        return lastBounds;
    }

    public async Task SetFullScreenAsync(WindowKey window, bool on)
    {
        var hwnd = (IntPtr)window.Handle;
        if (!Win32.IsWindow(hwnd)) return;

        if (!on && Win32.IsZoomed(hwnd))
        {
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    public Task SetMinimizedAsync(WindowKey window, bool on)
    {
        var hwnd = (IntPtr)window.Handle;
        if (!Win32.IsWindow(hwnd)) return Task.CompletedTask;
        Win32.ShowWindow(hwnd, on ? Win32.SW_MINIMIZE : Win32.SW_RESTORE);
        return Task.CompletedTask;
    }

    public Task FocusAsync(WindowKey window)
    {
        var hwnd = (IntPtr)window.Handle;
        if (!Win32.IsWindow(hwnd)) return Task.CompletedTask;
        if (Win32.IsIconic(hwnd))
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

        IntPtr fg = Win32.GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? Win32.GetWindowThreadProcessId(fg, out _) : 0;
        uint targetThread = Win32.GetWindowThreadProcessId(hwnd, out _);
        uint curThread = Win32.GetCurrentThreadId();

        bool attachedFg = fgThread != 0 && fgThread != curThread && Win32.AttachThreadInput(curThread, fgThread, true);
        bool attachedTarget = targetThread != 0 && targetThread != curThread && Win32.AttachThreadInput(curThread, targetThread, true);
        try
        {
            Win32.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget) Win32.AttachThreadInput(curThread, targetThread, false);
            if (attachedFg) Win32.AttachThreadInput(curThread, fgThread, false);
        }
        return Task.CompletedTask;
    }

    public async Task<bool> ZoomAsync(WindowKey window, int steps)
    {
        if (steps == 0) return true;
        var hwnd = (IntPtr)window.Handle;
        if (!Win32.IsWindow(hwnd)) return false;

        await FocusAsync(window).ConfigureAwait(false);
        await Task.Delay(60).ConfigureAwait(false);

        ushort vk = steps > 0 ? Win32.VK_OEM_PLUS : Win32.VK_OEM_MINUS;
        int count = Math.Abs(steps);
        for (int i = 0; i < count; i++)
        {
            SendCtrlShortcut(vk);
            await Task.Delay(70).ConfigureAwait(false);
        }
        return true;
    }

    public Task<IReadOnlyList<WindowInfo>> ShownWindowsAsync(int pid)
    {
        var list = EnumerateStandardWindows(pidFilter: pid, includeMinimized: false);
        return Task.FromResult<IReadOnlyList<WindowInfo>>(list);
    }

    public Task<WindowInfo?> AskingWindowAsync(int pid)
    {
        WindowInfo? found = null;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd)) return true;
            Win32.GetWindowThreadProcessId(hwnd, out int wPid);
            if (wPid != pid) return true;

            string cls = Win32.GetWindowClass(hwnd);
            IntPtr owner = Win32.GetWindow(hwnd, Win32.GW_OWNER);
            if (owner != IntPtr.Zero || cls == "#32770")
            {
                string title = WindowInfo.VisibleTitle(Win32.GetWindowTitle(hwnd));
                if (!string.IsNullOrEmpty(title))
                {
                    var appInfo = GetProcessAppMeta(pid);
                    found = new WindowInfo(new WindowKey(pid, (ulong)hwnd), appInfo.Name, appInfo.AppId, title);
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        return Task.FromResult(found);
    }

    public Task<bool> CloseAsync(WindowKey window)
    {
        var hwnd = (IntPtr)window.Handle;
        if (!Win32.IsWindow(hwnd)) return Task.FromResult(false);
        bool ok = Win32.PostMessageW(hwnd, Win32.WM_CLOSE, 0, 0);
        return Task.FromResult(ok);
    }

    public Task<RectD?> CloseButtonAsync(WindowKey window)
    {
        var hwnd = (IntPtr)window.Handle;
        if (!Win32.IsWindow(hwnd)) return Task.FromResult<RectD?>(null);
        return Task.FromResult(UiaHelper.FindCloseButtonRect(hwnd));
    }

    public Task<Dictionary<WindowKey, RectD>> FramesAsync(int pid)
    {
        var map = new Dictionary<WindowKey, RectD>();
        foreach (var w in EnumerateStandardWindows(pidFilter: pid, includeMinimized: false))
        {
            if (TryGetVisibleBounds((IntPtr)w.Key.Handle, out var rect))
                map[w.Key] = rect;
        }
        return Task.FromResult(map);
    }

    public Task<Dictionary<WindowKey, RectD>> SnapshotAsync()
    {
        var map = new Dictionary<WindowKey, RectD>();
        foreach (var w in EnumerateStandardWindows(pidFilter: null, includeMinimized: false))
        {
            if (TryGetVisibleBounds((IntPtr)w.Key.Handle, out var rect))
                map[w.Key] = rect;
        }
        return Task.FromResult(map);
    }

    public async Task RestoreAsync(IReadOnlyDictionary<WindowKey, RectD> snapshot, WindowKey? except)
    {
        foreach (var (key, saved) in snapshot)
        {
            if (except is { } ex && key == ex) continue;
            var hwnd = (IntPtr)key.Handle;
            if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd)) continue;
            if (!TryGetVisibleBounds(hwnd, out var now) || now.Matches(saved)) continue;
            var got = await MoveAsync(key, saved).ConfigureAwait(false);
            Log.Info($"恢复窗口 {key}：{now.Short} -> {saved.Short}（实际 {got?.Short ?? "?"}）");
        }
    }

    public static IReadOnlyList<(ulong Handle, RectD Frame)> GetOnScreenWindowZOrder()
    {
        var list = new List<(ulong, RectD)>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd))
                return true;
            if (IsExcludedOverlayClass(Win32.GetWindowClass(hwnd)))
                return true;
            if (Win32.GetWindowRect(hwnd, out var rc) && rc.Width > 1 && rc.Height > 1)
                list.Add(((ulong)hwnd, new RectD(rc.Left, rc.Top, rc.Width, rc.Height)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IReadOnlyList<WindowInfo> EnumerateStandardWindows(int? pidFilter, bool includeMinimized)
    {
        int selfPid = Environment.ProcessId;
        var results = new List<WindowInfo>();
        var metaCache = new Dictionary<int, (string Name, string? AppId)>();

        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd)) return true;
            Win32.GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == selfPid) return true;
            if (pidFilter is { } filter && pid != filter) return true;

            bool minimized = Win32.IsIconic(hwnd);
            if (!includeMinimized && minimized) return true;

            int cloaked = GetCloakedFlags(hwnd);
            if (cloaked != 0)
            {
                // Minimized UWP/Store windows (ApplicationFrameWindow) are shell-cloaked by Windows.
                bool allowMinimizedUwp = minimized
                    && includeMinimized
                    && cloaked == Win32.DWM_CLOAKED_SHELL
                    && Win32.GetWindowClass(hwnd) == "ApplicationFrameWindow";
                if (!allowMinimizedUwp) return true;
            }

            if (!IsStandardAppWindow(hwnd, minimized)) return true;

            string title = WindowInfo.VisibleTitle(Win32.GetWindowTitle(hwnd));
            if (string.IsNullOrEmpty(title)) return true;

            if (!metaCache.TryGetValue(pid, out var meta))
            {
                meta = GetProcessAppMeta(pid);
                metaCache[pid] = meta;
            }

            // Skip SnugWin and registered remote control processes
            if (IsExcludedApp(meta.AppId)) return true;

            results.Add(new WindowInfo(new WindowKey(pid, (ulong)hwnd), meta.Name, meta.AppId, title, minimized));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    public static void SetExcludedProcesses(IEnumerable<string> processNames)
    {
        var updated = new List<string> { SelfProcessExe };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SelfProcessExe };
        foreach (string raw in processNames)
        {
            string trimmed = raw?.Trim() ?? string.Empty;
            if (trimmed.Length == 0) continue;
            if (seen.Add(trimmed))
                updated.Add(trimmed);
        }

        lock (ExclusionGate)
        {
            _excludedProcessPatterns = updated.ToArray();
        }
    }

    internal static void ResetExcludedProcessesToDefaults()
    {
        lock (ExclusionGate)
        {
            _excludedProcessPatterns = DefaultExcludedProcesses;
        }
    }

    internal static bool IsExcludedApp(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;

        string candidate = Path.GetFileName(appId);
        string[] patterns;
        lock (ExclusionGate)
        {
            patterns = _excludedProcessPatterns;
        }

        foreach (string pattern in patterns)
        {
            if (pattern.EndsWith('*'))
            {
                string prefix = pattern[..^1];
                if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (candidate.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool IsExcludedOverlayClass(string? className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        return className.Equals("CrossDesk.PrivacyWindow", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith("crossdesk_privacy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStandardAppWindow(IntPtr hwnd, bool minimized = false)
    {
        long exStyle = Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0) return false;
        IntPtr owner = Win32.GetWindow(hwnd, Win32.GW_OWNER);
        if (owner != IntPtr.Zero && (exStyle & Win32.WS_EX_APPWINDOW) == 0) return false;

        if (minimized)
        {
            // Minimized windows report an off-screen iconic bar (e.g. 160x28 at -32000,-32000) via GetWindowRect;
            // validate against rcNormalPosition so minimized apps remain visible in Running Apps.
            if (TryGetNormalBounds(hwnd, out var normalRc) && (normalRc.Width <= 40 || normalRc.Height <= 40))
                return false;
        }
        else
        {
            if (!Win32.GetWindowRect(hwnd, out var rc)) return false;
            if (rc.Width <= 40 || rc.Height <= 40) return false;
        }

        string cls = Win32.GetWindowClass(hwnd);
        if (IsExcludedOverlayClass(cls)) return false;
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow")
        {
            // Allow ApplicationFrameWindow only if it has a non-empty title (UWP apps like Settings/Calculator)
            if (cls != "ApplicationFrameWindow") return false;
        }
        return true;
    }

    private static bool IsWindowOnScreen(IntPtr hwnd, int expectedPid)
    {
        if (!Win32.IsWindow(hwnd)) return false;
        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid != expectedPid) return false;
        return Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd) && !IsCloaked(hwnd);
    }

    private static int GetCloakedFlags(IntPtr hwnd)
    {
        int cloaked = 0;
        return Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 ? cloaked : 0;
    }

    private static bool IsCloaked(IntPtr hwnd) => GetCloakedFlags(hwnd) != 0;

    internal static bool TryGetNormalBounds(IntPtr hwnd, out RectD bounds)
    {
        var wp = new Win32.WINDOWPLACEMENT { length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
        if (Win32.GetWindowPlacement(hwnd, ref wp)
            && wp.rcNormalPosition.Width > 0
            && wp.rcNormalPosition.Height > 0)
        {
            bounds = new RectD(
                wp.rcNormalPosition.Left,
                wp.rcNormalPosition.Top,
                wp.rcNormalPosition.Width,
                wp.rcNormalPosition.Height);
            return true;
        }
        bounds = default;
        return false;
    }

    public static bool TryGetVisibleBounds(IntPtr hwnd, out RectD bounds)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT ext, Marshal.SizeOf<Win32.RECT>()) == 0
            && ext.Width > 0 && ext.Height > 0)
        {
            bounds = new RectD(ext.Left, ext.Top, ext.Width, ext.Height);
            return true;
        }
        if (Win32.GetWindowRect(hwnd, out var rc))
        {
            bounds = new RectD(rc.Left, rc.Top, rc.Width, rc.Height);
            return true;
        }
        bounds = default;
        return false;
    }

    private static (double Left, double Top, double Right, double Bottom) GetShadowMargins(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var raw)) return (0, 0, 0, 0);
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT ext, Marshal.SizeOf<Win32.RECT>()) != 0)
            return (0, 0, 0, 0);

        return (
            Math.Max(0, ext.Left - raw.Left),
            Math.Max(0, ext.Top - raw.Top),
            Math.Max(0, raw.Right - ext.Right),
            Math.Max(0, raw.Bottom - ext.Bottom));
    }

    private static bool IsLikelyFullScreen(IntPtr hwnd, RectD bounds)
    {
        IntPtr mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;
        var mi = new Win32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFOEX>() };
        if (!Win32.GetMonitorInfoW(mon, ref mi)) return false;

        var monRect = new RectD(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Width, mi.rcMonitor.Height);
        long style = Win32.GetWindowLongPtrW(hwnd, Win32.GWL_STYLE);
        bool hasCaption = (style & Win32.WS_CAPTION) == Win32.WS_CAPTION;
        return bounds.Matches(monRect, tolerance: 4) && !hasCaption;
    }

    internal static (string Name, string? AppId) GetProcessAppMeta(int pid)
    {
        string? path = Win32.GetProcessImagePath(pid);
        if (!string.IsNullOrEmpty(path))
        {
            string exe = Path.GetFileName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                    name = info.FileDescription.Trim();
            }
            catch { }
            return (name, exe);
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            return (p.ProcessName, p.ProcessName + ".exe");
        }
        catch
        {
            return ($"PID {pid}", null);
        }
    }

    internal static void SendCtrlShortcut(ushort vk, bool withShift = false) =>
        SendKeystroke(new SelectionKeystroke(vk, WithCtrl: true, WithShift: withShift));

    internal static void SendKeystroke(SelectionKeystroke key)
    {
        var inputs = new List<Win32.INPUT>(6);
        if (key.WithCtrl) inputs.Add(KeyInput(Win32.VK_CONTROL, down: true));
        if (key.WithShift) inputs.Add(KeyInput(Win32.VK_SHIFT, down: true));
        inputs.Add(KeyInput(key.VirtualKey, down: true));
        inputs.Add(KeyInput(key.VirtualKey, down: false));
        if (key.WithShift) inputs.Add(KeyInput(Win32.VK_SHIFT, down: false));
        if (key.WithCtrl) inputs.Add(KeyInput(Win32.VK_CONTROL, down: false));

        var arr = inputs.ToArray();
        Win32.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32.INPUT>());
    }

    public Task CopySelectionAtAsync(PointD point) =>
        PerformSelectionActionAsync(point, SelectionBubbleAction.Copy);

    public async Task PerformSelectionActionAsync(PointD point, SelectionBubbleAction action)
    {
        IntPtr hit = Win32.WindowFromPoint(new Win32.POINT { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) });
        IntPtr root = hit != IntPtr.Zero ? Win32.GetAncestor(hit, Win32.GA_ROOT) : IntPtr.Zero;
        if (root == IntPtr.Zero) root = hit;
        if (root == IntPtr.Zero) return;

        Win32.GetWindowThreadProcessId(root, out int pid);
        string cls = Win32.GetWindowClass(root);
        string? exe = pid > 0 ? Path.GetFileName(Win32.GetProcessImagePath(pid)) : null;

        await FocusAsync(new WindowKey(pid, (ulong)root)).ConfigureAwait(false);
        await Task.Delay(35).ConfigureAwait(false);
        SendKeystroke(SelectionShortcutResolver.Resolve(action, cls, exe));
    }

    private static Win32.INPUT KeyInput(ushort vk, bool down) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.INPUTUNION
        {
            ki = new Win32.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = down ? 0 : Win32.KEYEVENTF_KEYUP,
                dwExtraInfo = 0x534E5547
            }
        }
    };
}
