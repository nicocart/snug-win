using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SnugWin.Core.Remotes.CrossDesk;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Sensing;

/// <summary>
/// Windows host inspection and non-locking log tailing for CrossDesk.
/// </summary>
public static class CrossDeskHost
{
    private const string ServiceName = "CrossDeskService";

    public static string DefaultLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrossDesk",
        "logs");

    public static string DefaultLogPath => Path.Combine(DefaultLogDirectory, "crossdesk.log");

    /// <summary>
    /// All Windows log locations CrossDesk may write to:
    /// - Current interactive user's %LOCALAPPDATA% (when GUI is launched by the user)
    /// - LocalSystem 64-bit / 32-bit systemprofile LocalAppData (when CrossDeskService spawns persistent host as SYSTEM)
    /// </summary>
    public static IReadOnlyList<string> CandidateLogPaths()
    {
        var list = new List<string> { DefaultLogPath };
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(winDir))
        {
            list.Add(Path.Combine(winDir, "System32", "config", "systemprofile", "AppData", "Local", "CrossDesk", "logs", "crossdesk.log"));
            list.Add(Path.Combine(winDir, "SysWOW64", "config", "systemprofile", "AppData", "Local", "CrossDesk", "logs", "crossdesk.log"));
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsServiceInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsServiceRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("crossdesk_service");
            try
            {
                return procs.Length > 0;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool IsInstalled()
    {
        if (FindRunningPid() is not null || IsServiceRunning())
            return true;

        return ResolveExecutablePath() is not null;
    }

    public static string? ResolveExecutablePath()
    {
        try
        {
            var procs = Process.GetProcessesByName("CrossDesk");
            try
            {
                foreach (var p in procs)
                {
                    try
                    {
                        string? path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                            return path;
                    }
                    catch { }
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch { }

        try
        {
            using var hkcuKey = Registry.CurrentUser.OpenSubKey(@"Software\CrossDesk");
            if (hkcuKey?.GetValue("InstallDir") is string dir)
            {
                string candidate = Path.Combine(dir, "CrossDesk.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }

        return FindUninstallExe(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
            ?? FindUninstallExe(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
            ?? FindUninstallExe(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
            ?? ResolveDefaultProgramFilesExe();
    }

    private static string? ResolveDefaultProgramFilesExe()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                 })
        {
            if (!string.IsNullOrEmpty(root))
            {
                string candidate = Path.Combine(root, "CrossDesk", "CrossDesk.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static DateTimeOffset? OldestCrossDeskStartTime()
    {
        try
        {
            var procs = Process.GetProcessesByName("CrossDesk");
            try
            {
                if (procs.Length == 0) return null;
                DateTime? oldest = null;
                foreach (var p in procs)
                {
                    try
                    {
                        DateTime start = p.StartTime;
                        if (oldest is null || start < oldest.Value)
                            oldest = start;
                    }
                    catch (Win32Exception)
                    {
                        // Elevated CrossDesk inspected from an unelevated process: don't guess.
                        return null;
                    }
                    catch (InvalidOperationException)
                    {
                        // Process exited during enumeration.
                    }
                }
                return oldest is { } dt ? new DateTimeOffset(dt) : null;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    public static async Task<(bool Ok, string Message)> RestartHostAsync(CancellationToken ct = default, bool silent = false)
    {
        string? exePath = ResolveExecutablePath();
        bool serviceInstalled = IsServiceInstalled();
        bool hadVisibleGui = HasVisibleCrossDeskGui();

        if (serviceInstalled && IsServiceRunning())
        {
            var (stopCode, stopErr) = await RunScAsync($"stop \"{ServiceName}\"", ct).ConfigureAwait(false);
            if (stopCode == 5) // ERROR_ACCESS_DENIED
                return (false, "停止 CrossDeskService 需要管理员权限，请先点击「提权重启」或手动重启 CrossDesk。");

            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (IsServiceRunning() && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(200, ct).ConfigureAwait(false);

            if (stopCode != 0 && stopCode != 1062 && !string.IsNullOrWhiteSpace(stopErr))
            {
                // Non-fatal if we can still terminate remaining processes cleanly.
            }
        }

        foreach (string procName in new[] { "CrossDesk", "crossdesk_session_helper", "crossdesk_privacy_cursor_helper" })
        {
            try
            {
                var procs = Process.GetProcessesByName(procName);
                try
                {
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (!p.HasExited)
                            {
                                p.Kill(entireProcessTree: true);
                                p.WaitForExit(3000);
                            }
                        }
                        catch (Win32Exception)
                        {
                            return (false, "结束高权限 CrossDesk 进程需要管理员权限，请先点击「提权重启」或手动重启 CrossDesk。");
                        }
                        catch (InvalidOperationException) { }
                    }
                }
                finally
                {
                    foreach (var p in procs) p.Dispose();
                }
            }
            catch { }
        }

        await Task.Delay(300, ct).ConfigureAwait(false);

        if (serviceInstalled)
        {
            var (startCode, startErr) = await RunScAsync($"start \"{ServiceName}\"", ct).ConfigureAwait(false);
            if (startCode != 0 && startCode != 1056) // 1056 = already running
            {
                return (false, $"启动 CrossDeskService 失败（退出码 {startCode}）：{startErr}".Trim());
            }
            // Wait briefly for the service persistent host to come up so the user-session GUI attaches cleanly.
            await Task.Delay(700, ct).ConfigureAwait(false);
        }

        // Always launch user-session CrossDesk.exe so it attaches to the persistent host,
        // keeps Windows privacy screen working, and writes live events to %LOCALAPPDATA%\CrossDesk\logs\crossdesk.log.
        // In silent mode (when GUI was not open before), minimize the newly created GUI window as soon as it appears.
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                var launched = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WindowStyle = silent && !hadVisibleGui ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
                });

                if (silent && !hadVisibleGui)
                {
                    int? launchedPid = launched?.Id;
                    _ = Task.Run(async () =>
                    {
                        var deadline = DateTimeOffset.UtcNow.AddSeconds(4.5);
                        while (DateTimeOffset.UtcNow < deadline)
                        {
                            if (TryMinimizeCrossDeskGui(launchedPid))
                                return;
                            await Task.Delay(150).ConfigureAwait(false);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return (false, $"启动 CrossDesk.exe 失败：{ex.Message}");
            }
        }
        else if (!serviceInstalled)
        {
            return (false, "未找到 CrossDesk.exe 安装路径，请手动启动 CrossDesk。");
        }

        return (true, silent
            ? "已在空闲时静默重启 CrossDesk，正在重新识别虚拟屏。"
            : "已重启 CrossDesk，正在等待重新识别屏幕。");
    }

    private static bool TryMinimizeCrossDeskGui(int? preferredPid)
    {
        bool minimizedAny = false;
        try
        {
            var procs = Process.GetProcessesByName("CrossDesk");
            try
            {
                foreach (var p in procs)
                {
                    if (preferredPid is { } pid && p.Id != pid) continue;
                    try
                    {
                        p.Refresh();
                        IntPtr hwnd = p.MainWindowHandle;
                        if (hwnd != IntPtr.Zero && Win32.IsWindowVisible(hwnd))
                        {
                            Win32.ShowWindow(hwnd, Win32.SW_MINIMIZE);
                            minimizedAny = true;
                        }
                    }
                    catch { }
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch { }
        return minimizedAny;
    }

    private static bool HasVisibleCrossDeskGui()
    {
        try
        {
            var procs = Process.GetProcessesByName("CrossDesk");
            try
            {
                return Array.Exists(procs, p =>
                {
                    try { return p.MainWindowHandle != IntPtr.Zero; }
                    catch { return false; }
                });
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Error)> RunScAsync(string arguments, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (proc is null)
            return (-1, "无法启动 sc.exe");

        string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return (proc.ExitCode, combined.Trim());
    }

    public static int? FindRunningPid()
    {
        try
        {
            var procs = Process.GetProcessesByName("CrossDesk");
            try
            {
                if (procs.Length == 0) return null;
                int bestPid = procs[0].Id;
                DateTime bestStart = SafeStartTime(procs[0]);
                for (int i = 1; i < procs.Length; i++)
                {
                    DateTime start = SafeStartTime(procs[i]);
                    if (start < bestStart || (start == bestStart && procs[i].Id < bestPid))
                    {
                        bestStart = start;
                        bestPid = procs[i].Id;
                    }
                }
                return bestPid;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    private static DateTime SafeStartTime(Process p)
    {
        try { return p.StartTime; }
        catch { return DateTime.MinValue; }
    }

    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (Win32Exception)
        {
            // Process exists at a higher privilege level (e.g. elevated CrossDesk vs unelevated test).
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindUninstallExe(RegistryKey root, string subKeyPath)
    {
        try
        {
            using var parent = root.OpenSubKey(subKeyPath);
            if (parent is null) return null;
            foreach (var name in parent.GetSubKeyNames())
            {
                using var sub = parent.OpenSubKey(name);
                if (sub is null) continue;
                var displayName = sub.GetValue("DisplayName") as string;
                var installLoc = sub.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(installLoc))
                {
                    string candidate = Path.Combine(installLoc, "CrossDesk.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                if (!string.IsNullOrEmpty(displayName) && displayName.Contains("CrossDesk", StringComparison.OrdinalIgnoreCase))
                {
                    string? exe = ResolveDefaultProgramFilesExe();
                    if (exe is not null) return exe;
                }
            }
        }
        catch { }
        return null;
    }

    internal static ulong? TryGetFileId(SafeFileHandle handle)
    {
        return GetFileInformationByHandle(handle, out var info)
            ? ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);
}

/// <summary>
/// Non-locking file reader wrapping <see cref="CrossDeskLogFollower"/> for <c>crossdesk.log</c>.
/// </summary>
public sealed class CrossDeskLogTail
{
    private readonly string _logPath;
    private readonly CrossDeskLogFollower _follower = new();

    public CrossDeskLogTail(string? logPath = null)
    {
        _logPath = logPath ?? CrossDeskHost.DefaultLogPath;
    }

    public IReadOnlyList<string> Poll()
    {
        if (!File.Exists(_logPath))
            return Array.Empty<string>();

        using var stream = new FileStream(
            _logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        ulong? fileId = CrossDeskHost.TryGetFileId(stream.SafeFileHandle);
        var range = _follower.NextRange(fileId, stream.Length);
        if (range is null)
            return Array.Empty<string>();

        stream.Seek(range.Value.Offset, SeekOrigin.Begin);
        var buffer = new byte[range.Value.Count];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int n = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (n == 0) break;
            totalRead += n;
        }

        return totalRead == 0
            ? Array.Empty<string>()
            : _follower.Feed(buffer.AsSpan(0, totalRead));
    }

    public void SeekToEnd()
    {
        if (!File.Exists(_logPath)) return;
        try
        {
            using var stream = new FileStream(
                _logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            ulong? fileId = CrossDeskHost.TryGetFileId(stream.SafeFileHandle);
            _follower.SkipToEnd(fileId, stream.Length);
        }
        catch
        {
            // ignore inaccessible candidate
        }
    }

    public void Reset() => _follower.Reset();
}

/// <summary>
/// Tails all candidate CrossDesk log paths (user profile + LocalSystem systemprofile).
/// On initial sync or reset, replays only the most recently modified existing log from offset 0
/// while fast-forwarding older stale logs to EOF so historical sessions never pollute live state,
/// yet subsequent appends to ANY candidate log are observed immediately.
/// </summary>
public sealed class CrossDeskMultiLogTail
{
    private readonly (string Path, CrossDeskLogTail Tail)[] _entries;
    private bool _initialized;

    public CrossDeskMultiLogTail(IEnumerable<string> paths)
    {
        _entries = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (p, new CrossDeskLogTail(p)))
            .ToArray();
    }

    public IReadOnlyList<string> Poll()
    {
        if (_entries.Length == 0)
            return Array.Empty<string>();

        if (!_initialized)
        {
            _initialized = true;
            int newestIndex = -1;
            DateTime newestWriteUtc = DateTime.MinValue;
            for (int i = 0; i < _entries.Length; i++)
            {
                try
                {
                    if (!File.Exists(_entries[i].Path)) continue;
                    var wt = File.GetLastWriteTimeUtc(_entries[i].Path);
                    if (newestIndex < 0 || wt > newestWriteUtc)
                    {
                        newestIndex = i;
                        newestWriteUtc = wt;
                    }
                }
                catch
                {
                    // ignore inaccessible path
                }
            }

            for (int i = 0; i < _entries.Length; i++)
            {
                if (i != newestIndex)
                    _entries[i].Tail.SeekToEnd();
            }
        }

        List<string>? combined = null;
        Exception? lastError = null;
        bool anyReadable = false;

        foreach (var (path, tail) in _entries)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var lines = tail.Poll();
                anyReadable = true;
                if (lines.Count > 0)
                {
                    combined ??= new List<string>(lines.Count);
                    combined.AddRange(lines);
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (!anyReadable && lastError is not null)
            throw lastError;

        return combined ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    public void Reset()
    {
        _initialized = false;
        foreach (var (_, tail) in _entries)
            tail.Reset();
    }
}
