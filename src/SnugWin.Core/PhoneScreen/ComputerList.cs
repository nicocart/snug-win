using System;
using System.Collections.Generic;
using System.Linq;

namespace SnugWin.Core.PhoneScreen;

public abstract record ComputerLocation
{
    private ComputerLocation() { }

    public sealed record Here(bool FullScreen) : ComputerLocation;
    public sealed record Minimized : ComputerLocation { public static readonly Minimized Instance = new(); }
    public sealed record Shown(uint DisplayId) : ComputerLocation;
    public sealed record Hidden : ComputerLocation { public static readonly Hidden Instance = new(); }

    public bool IsHere => this is Here;
}

public sealed class ComputerGroups
{
    public List<WindowInfo> Here { get; } = new();
    public List<WindowInfo> Elsewhere { get; } = new();
    public List<AppInfo> Windowless { get; } = new();

    /// <summary>Populated only during search: installed apps that are not currently running.</summary>
    public List<AppInfo> Unopened { get; } = new();

    public bool IsEmpty =>
        Here.Count == 0 && Elsewhere.Count == 0 && Windowless.Count == 0 && Unopened.Count == 0;
}

public abstract record ComputerAppAction
{
    private ComputerAppAction() { }

    public sealed record Focus(WindowInfo Window) : ComputerAppAction;
    public sealed record Pull(WindowInfo Window) : ComputerAppAction;
    public sealed record Open(AppInfo App) : ComputerAppAction;
}

/// <summary>
/// Pure grouping and click-action logic for the computer-screen App Library
/// (1:1 port of upstream Snug v2.9.0 ComputerList.swift).
/// </summary>
public static class ComputerList
{
    public static ComputerLocation LocationOf(WindowInfo window, WindowSpot? spot, uint displayId)
    {
        if (window.Minimized) return ComputerLocation.Minimized.Instance;
        return spot switch
        {
            WindowSpot.Shown s when s.DisplayId == displayId => new ComputerLocation.Here(FullScreen: false),
            WindowSpot.FullScreen f when f.DisplayId == displayId => new ComputerLocation.Here(FullScreen: true),
            WindowSpot.Shown s => new ComputerLocation.Shown(s.DisplayId),
            _ => ComputerLocation.Hidden.Instance
        };
    }

    public static ComputerGroups Groups(
        uint displayId,
        string query,
        IReadOnlyList<RunningApp> running,
        IReadOnlyList<AppInfo> installed,
        IReadOnlyDictionary<WindowKey, WindowSpot> spots)
    {
        string trimmed = query?.Trim() ?? string.Empty;
        AppSearchResult? found = string.IsNullOrEmpty(trimmed)
            ? null
            : AppList.Search(trimmed, installed, running);

        HashSet<string>? matchedAppIds = found is null
            ? null
            : new HashSet<string>(found.Apps.Select(a => a.AppId), StringComparer.OrdinalIgnoreCase);
        HashSet<WindowKey> matchedWindows = found is null
            ? new HashSet<WindowKey>()
            : new HashSet<WindowKey>(found.Windows.Select(w => w.Key));

        var groups = new ComputerGroups();
        foreach (var entry in running)
        {
            bool appMatches = matchedAppIds is null || matchedAppIds.Contains(entry.App.AppId);
            if (entry.Windows.Count == 0 && appMatches)
                groups.Windowless.Add(entry.App);

            foreach (var window in entry.Windows)
            {
                if (!appMatches && !matchedWindows.Contains(window.Key))
                    continue;
                spots.TryGetValue(window.Key, out var spot);
                if (LocationOf(window, spot, displayId).IsHere)
                    groups.Here.Add(window);
                else
                    groups.Elsewhere.Add(window);
            }
        }

        if (found is not null)
        {
            var runningIds = new HashSet<string>(running.Select(r => r.App.AppId), StringComparer.OrdinalIgnoreCase);
            foreach (var app in found.Apps)
            {
                if (!runningIds.Contains(app.AppId))
                    groups.Unopened.Add(app);
            }
        }

        return groups;
    }

    public static ComputerAppAction ActionFor(
        AppInfo app,
        uint displayId,
        IReadOnlyList<RunningApp> running,
        IReadOnlyDictionary<WindowKey, WindowSpot> spots)
    {
        var match = running.FirstOrDefault(r =>
            string.Equals(r.App.AppId, app.AppId, StringComparison.OrdinalIgnoreCase));
        var windows = match?.Windows ?? Array.Empty<WindowInfo>();

        var here = windows.FirstOrDefault(w =>
        {
            spots.TryGetValue(w.Key, out var spot);
            return LocationOf(w, spot, displayId).IsHere;
        });
        if (here is not null)
            return new ComputerAppAction.Focus(here);

        if (windows.Count > 0)
            return new ComputerAppAction.Pull(windows[0]);

        return new ComputerAppAction.Open(app);
    }
}
