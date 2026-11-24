using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using SnugWin.Core.Remote;
using SnugWin.Core.Session;

namespace SnugWin.Windows.Sensing;

public sealed class UURemoteAdapter : IRemoteAdapter
{
    private readonly UUSensor _sensor;

    public UURemoteAdapter(UUSensor? sensor = null)
    {
        _sensor = sensor ?? new UUSensor();
    }

    public RemoteIdentity Identity => UURemote.Identity;
    public UUSensor Sensor => _sensor;

    public bool ManualOverride
    {
        get => _sensor.ManualOverride;
        set => _sensor.ManualOverride = value;
    }

    public bool IsInstalled()
    {
        if (HasRunningProcess("GameViewer") || HasRunningProcess("GameViewerServer"))
            return true;

        if (HasInstallPathOnDisk() || HasUninstallOrVendorRegistryEntry())
            return true;

        return false;
    }

    public RemoteReading Sample(
        DateTimeOffset wallClock,
        bool wasConnected,
        IReadOnlyList<SnugDisplayInfo> snugDisplays)
    {
        var (signals, observedAt) = _sensor.Sample(wasConnected, wallClock);
        return new RemoteReading(Identity, signals.ToObservation(), observedAt);
    }

    private static bool HasRunningProcess(string name)
    {
        try
        {
            var procs = Process.GetProcessesByName(name);
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

    private static bool HasInstallPathOnDisk()
    {
        var candidates = new List<string>();
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            candidates.Add(Path.Combine(programFiles, "Netease", "GameViewer", "GameViewer.exe"));

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFilesX86))
            candidates.Add(Path.Combine(programFilesX86, "Netease", "GameViewer", "GameViewer.exe"));

        candidates.Add(@"C:\Program Files\Netease\GameViewer\GameViewer.exe");
        candidates.Add(@"C:\Program Files (x86)\Netease\GameViewer\GameViewer.exe");
        candidates.Add(@"D:\Program Files\Netease\GameViewer\GameViewer.exe");
        candidates.Add(@"D:\GameViewer\GameViewer.exe");

        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return true;
        }
        return false;
    }

    private static bool HasUninstallOrVendorRegistryEntry()
    {
        try
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (string subPath in new[]
                {
                    @"SOFTWARE\Netease\GameViewer",
                    @"SOFTWARE\WOW6432Node\Netease\GameViewer"
                })
                {
                    using var vendorKey = root.OpenSubKey(subPath);
                    if (vendorKey is not null)
                        return true;
                }

                foreach (string uninstallPath in new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                })
                {
                    using var uninstall = root.OpenSubKey(uninstallPath);
                    if (uninstall is null) continue;
                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        if (name.Contains("GameViewer", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("UU远程", StringComparison.OrdinalIgnoreCase))
                            return true;

                        using var entry = uninstall.OpenSubKey(name);
                        string? displayName = entry?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrEmpty(displayName)
                            && (displayName.Contains("GameViewer", StringComparison.OrdinalIgnoreCase)
                                || displayName.Contains("UU远程", StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }
                }
            }
        }
        catch
        {
            // ignore registry access errors
        }
        return false;
    }
}
