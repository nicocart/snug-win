using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Windows;

public sealed class AppService : IAppControl
{
    private readonly WindowService _windows;

    public AppService(WindowService windows)
    {
        _windows = windows;
    }

    public Task<int?> PidOfAsync(AppInfo app)
    {
        string targetExe = Path.GetFileNameWithoutExtension(app.ExecutablePath ?? app.AppId);
        var procs = Process.GetProcessesByName(targetExe);
        try
        {
            var withWin = procs.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? procs.FirstOrDefault();
            return Task.FromResult<int?>(withWin?.Id);
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public Task<IReadOnlyList<int>> PidsAsync(string appId)
    {
        string name = Path.GetFileNameWithoutExtension(appId);
        var procs = Process.GetProcessesByName(name);
        try
        {
            return Task.FromResult<IReadOnlyList<int>>(procs.Select(p => p.Id).ToList());
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public Task<IReadOnlyList<WindowInfo>> AppWindowsAsync(AppInfo app, int? launchedPid = null)
    {
        bool isUwp = app.AppId.Contains('!');
        string targetExe = isUwp ? string.Empty : Path.GetFileName(app.ExecutablePath ?? app.AppId);
        string? targetDir = !string.IsNullOrEmpty(app.ExecutablePath) ? Path.GetDirectoryName(app.ExecutablePath) : null;
        var all = WindowService.EnumerateStandardWindows(pidFilter: null, includeMinimized: false);
        var matched = all.Where(w =>
        {
            if (launchedPid is { } lp && lp > 0 && w.Key.Pid == lp) return true;
            if (!string.IsNullOrEmpty(targetExe) && !string.IsNullOrEmpty(w.AppId)
                && w.AppId.Equals(targetExe, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(targetDir))
            {
                string? img = Win32.GetProcessImagePath(w.Key.Pid);
                if (!string.IsNullOrEmpty(img) && img.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (isUwp && (w.Title.Contains(app.Name, StringComparison.OrdinalIgnoreCase)
                          || w.AppName.Contains(app.Name, StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }).ToList();
        return Task.FromResult<IReadOnlyList<WindowInfo>>(matched);
    }

    public async Task<int?> OpenAsync(AppInfo app)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = app.LaunchTarget ?? app.ExecutablePath ?? app.AppId,
                Arguments = app.Arguments ?? string.Empty,
                WorkingDirectory = app.WorkingDirectory ?? string.Empty,
                UseShellExecute = true
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                // Single-instance apps (Electron/Chromium/etc.) often spawn a short-lived launcher
                // that hands off to an existing main process within ~300ms.
                await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(350)).ConfigureAwait(false);
                if (!proc.HasExited)
                    return proc.Id;
            }

            if (app.AppId.Contains('!'))
                return 0;

            // Launcher/shell-execute handed off to an existing process; resolve main PID by exe name.
            string exeName = Path.GetFileNameWithoutExtension(app.ExecutablePath ?? app.AppId);
            var procs = Process.GetProcessesByName(exeName);
            try
            {
                var found = procs.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? procs.FirstOrDefault();
                return found?.Id;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"启动 {app.Name} 失败：{ex.Message}");
            return null;
        }
    }

    public async Task<bool> NewWindowAsync(int pid)
    {
        var shown = await _windows.ShownWindowsAsync(pid).ConfigureAwait(false);
        var target = shown.FirstOrDefault();
        if (target is null) return false;

        var hwnd = (IntPtr)target.Key.Handle;
        await _windows.FocusAsync(target.Key).ConfigureAwait(false);
        await Task.Delay(80).ConfigureAwait(false);

        if (UiaHelper.TryInvokeNewWindowMenu(hwnd))
            return true;

        string? appId = target.AppId;
        bool shiftN = appId is not null && (
            appId.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase)
            || appId.Equals("wt.exe", StringComparison.OrdinalIgnoreCase)
            || appId.Equals("Code.exe", StringComparison.OrdinalIgnoreCase)
            || appId.Equals("Cursor.exe", StringComparison.OrdinalIgnoreCase)
            || appId.Equals("Windsurf.exe", StringComparison.OrdinalIgnoreCase)
            || appId.Equals("Zed.exe", StringComparison.OrdinalIgnoreCase));

        WindowService.SendCtrlShortcut(Win32.VK_N, withShift: shiftN);
        return true;
    }

    public async Task<bool> QuitAsync(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return true;
            if (p.MainWindowHandle != IntPtr.Zero)
                p.CloseMainWindow();
            else
            {
                var windows = await _windows.ShownWindowsAsync(pid).ConfigureAwait(false);
                foreach (var w in windows)
                    Win32.PostMessageW((IntPtr)w.Key.Handle, Win32.WM_CLOSE, 0, 0);
            }
            return true;
        }
        catch
        {
            return true;
        }
    }

    public Task<bool> ForceQuitAsync(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> IsRunningAsync(int pid)
    {
        if (pid <= 0) return Task.FromResult(false);
        try
        {
            using var p = Process.GetProcessById(pid);
            return Task.FromResult(!p.HasExited);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public IReadOnlyList<RunningApp> GetRunningApps()
    {
        var allWindows = WindowService.EnumerateStandardWindows(pidFilter: null, includeMinimized: true);
        var zOrder = WindowService.GetOnScreenWindowZOrder().Select(x => x.Handle).ToList();
        var byApp = allWindows.GroupBy(w => w.AppId ?? w.Key.Pid.ToString(), StringComparer.OrdinalIgnoreCase);
        var list = new List<RunningApp>();
        foreach (var grp in byApp)
        {
            var first = grp.First();
            string? path = Win32.GetProcessImagePath(first.Key.Pid);
            var appInfo = new AppInfo(
                first.AppId ?? first.AppName,
                first.AppName,
                path);
            list.Add(new RunningApp(appInfo, first.Key.Pid, grp.ToList()));
        }
        return AppList.Ordered(list, zOrder);
    }

    public static IReadOnlyList<AppInfo> ScanInstalledApps()
    {
        var map = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Scan Start Menu shortcuts (.lnk)
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };

        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (string lnk in links)
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                if (IsIgnoredShortcutName(name)) continue;

                string? target = ResolveShortcutTarget(lnk);
                if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(target)) continue;

                string exe = Path.GetFileName(target);
                if (WindowService.IsExcludedApp(exe) || IsInstallerOrHelper(exe, name)) continue;

                if (!map.ContainsKey(exe))
                {
                    map[exe] = new AppInfo(exe, name, target, lnk);
                }
            }
        }

        // 2. Scan Registry App Paths (HKLM & HKCU)
        ScanAppPaths(Registry.LocalMachine, map);
        ScanAppPaths(Registry.CurrentUser, map);

        // 3. Scan shell:AppsFolder (captures Microsoft Store / UWP apps like Windows Terminal, Calculator, Settings)
        ScanAppsFolder(map);

        return AppList.Sorted(map.Values);
    }

    private static void ScanAppsFolder(Dictionary<string, AppInfo> map)
    {
        var existingNames = map.Values
            .Select(a => a.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var entries = new List<(string Name, string Target)>();

        void Enumerate()
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return;
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell is null) return;
                try
                {
                    dynamic? folder = shell.NameSpace("shell:AppsFolder");
                    if (folder is null) return;
                    dynamic items = folder.Items();
                    foreach (dynamic item in items)
                    {
                        string? name = item.Name as string;
                        string? path = item.Path as string;
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
                            entries.Add((name.Trim(), path.Trim()));
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(shell);
                }
            }
            catch
            {
                // ignore shell enumeration failures
            }
        }

        var t = new System.Threading.Thread(Enumerate);
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join(4000);

        foreach (var (name, target) in entries)
        {
            if (IsIgnoredShortcutName(name) || existingNames.Contains(name)) continue;

            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            {
                string exe = Path.GetFileName(target);
                if (WindowService.IsExcludedApp(exe) || IsInstallerOrHelper(exe, name)) continue;
                if (map.TryAdd(exe, new AppInfo(exe, name, target, target)))
                    existingNames.Add(name);
            }
            else if (target.Contains('!'))
            {
                // UWP AUMID (e.g. Microsoft.WindowsTerminal_8wekyb3d8bbwe!App)
                if (map.TryAdd(target, new AppInfo(
                        target,
                        name,
                        null,
                        "explorer.exe",
                        $@"shell:AppsFolder\{target}")))
                {
                    existingNames.Add(name);
                }
            }
        }
    }

    private static void ScanAppPaths(RegistryKey root, Dictionary<string, AppInfo> map)
    {
        try
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (key is null) return;
            foreach (string sub in key.GetSubKeyNames())
            {
                if (!sub.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (WindowService.IsExcludedApp(sub) || IsInstallerOrHelper(sub, sub)) continue;
                using var sk = key.OpenSubKey(sub);
                string? rawPath = sk?.GetValue(null)?.ToString()?.Trim('"');
                if (string.IsNullOrEmpty(rawPath) || !File.Exists(rawPath)) continue;
                if (map.ContainsKey(sub)) continue;

                string name = Path.GetFileNameWithoutExtension(rawPath);
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(rawPath);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription)) name = vi.FileDescription.Trim();
                }
                catch { }
                map[sub] = new AppInfo(sub, name, rawPath, rawPath);
            }
        }
        catch
        {
            // ignore registry access errors
        }
    }

    private static bool IsIgnoredShortcutName(string name) =>
        name.Contains("卸载", StringComparison.Ordinal)
        || name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("帮助", StringComparison.Ordinal)
        || name.Contains("Help", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Readme", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Website", StringComparison.OrdinalIgnoreCase);

    private static bool IsInstallerOrHelper(string exe, string name) =>
        exe.Contains("unins", StringComparison.OrdinalIgnoreCase)
        || exe.Contains("setup", StringComparison.OrdinalIgnoreCase)
        || exe.Contains("update", StringComparison.OrdinalIgnoreCase)
        || name.Equals("SnugWin", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            dynamic? shell = Activator.CreateInstance(t);
            if (shell is null) return null;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                string? target = shortcut.TargetPath as string;
                Marshal.FinalReleaseComObject(shortcut);
                return target;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            return null;
        }
    }
}
