using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SnugWin.Core.Remotes.CrossDesk;

namespace SnugWin.Windows.Sensing;

/// <summary>
/// Windows host inspection and non-locking log tailing for CrossDesk.
/// </summary>
public static class CrossDeskHost
{
    public static string DefaultLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrossDesk",
        "logs");

    public static string DefaultLogPath => Path.Combine(DefaultLogDirectory, "crossdesk.log");

    public static bool IsInstalled()
    {
        if (FindRunningPid() is not null)
            return true;

        try
        {
            using var hkcuKey = Registry.CurrentUser.OpenSubKey(@"Software\CrossDesk");
            if (hkcuKey?.GetValue("InstallDir") is string dir
                && File.Exists(Path.Combine(dir, "CrossDesk.exe")))
                return true;
        }
        catch { }

        if (HasUninstallEntry(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
            || HasUninstallEntry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
            || HasUninstallEntry(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
        {
            return true;
        }

        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf86) && File.Exists(Path.Combine(pf86, "CrossDesk", "CrossDesk.exe")))
            return true;

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf) && File.Exists(Path.Combine(pf, "CrossDesk", "CrossDesk.exe")))
            return true;

        return false;
    }

    public static int? FindRunningPid()
    {
        try
        {
            var procs = Process.GetProcessesByName("CrossDesk");
            try
            {
                return procs.Length > 0 ? procs[0].Id : null;
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

    private static bool HasUninstallEntry(RegistryKey root, string subKeyPath)
    {
        try
        {
            using var parent = root.OpenSubKey(subKeyPath);
            if (parent is null) return false;
            foreach (var name in parent.GetSubKeyNames())
            {
                using var sub = parent.OpenSubKey(name);
                if (sub is null) continue;
                var displayName = sub.GetValue("DisplayName") as string;
                var installLoc = sub.GetValue("InstallLocation") as string;
                if ((!string.IsNullOrEmpty(displayName) && displayName.Contains("CrossDesk", StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(installLoc) && File.Exists(Path.Combine(installLoc, "CrossDesk.exe"))))
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
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

    public void Reset() => _follower.Reset();
}
