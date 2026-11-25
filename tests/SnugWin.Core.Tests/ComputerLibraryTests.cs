using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Config;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class ComputerLibraryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnugPaths _paths;

    public ComputerLibraryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "snugwin-comp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new SnugPaths(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void WindowSpot_Resolve_ClassifiesShownFullScreenAndHidden()
    {
        var displays = new List<(uint Id, RectD Bounds)>
        {
            (1, new RectD(0, 0, 1920, 1080)),
            (2, new RectD(1920, 0, 2560, 1440))
        };

        var shown = WindowSpot.Resolve(new RectD(2000, 100, 800, 600), shown: true, displays);
        Assert.Equal(new WindowSpot.Shown(2), shown);

        var full = WindowSpot.Resolve(new RectD(1920, 0, 2560, 1440), shown: false, displays);
        Assert.Equal(new WindowSpot.FullScreen(2), full);

        var hiddenOther = WindowSpot.Resolve(new RectD(2000, 100, 800, 600), shown: false, displays);
        Assert.Equal(WindowSpot.Hidden.Instance, hiddenOther);

        var offscreen = WindowSpot.Resolve(new RectD(-5000, -5000, 400, 300), shown: true, displays);
        Assert.Equal(WindowSpot.Hidden.Instance, offscreen);
    }

    [Fact]
    public void ComputerList_Groups_SplitsHereElsewhereWindowlessAndSearchUnopened()
    {
        var termApp = new AppInfo("WindowsTerminal.exe", "终端", null);
        var codeApp = new AppInfo("Code.exe", "VS Code", null);
        var idleApp = new AppInfo("Slack.exe", "Slack", null);
        var notesApp = new AppInfo("Notes.exe", "备忘录", null);

        var wHere = new WindowInfo(new WindowKey(10, 101), "终端", termApp.AppId, "PowerShell", false);
        var wOther = new WindowInfo(new WindowKey(20, 201), "VS Code", codeApp.AppId, "snug-win", false);
        var wMin = new WindowInfo(new WindowKey(20, 202), "VS Code", codeApp.AppId, "README.md", true);

        var running = new List<RunningApp>
        {
            new(termApp, 10, [wHere]),
            new(codeApp, 20, [wOther, wMin]),
            new(idleApp, 30, Array.Empty<WindowInfo>())
        };
        var installed = new List<AppInfo> { termApp, codeApp, idleApp, notesApp };
        var spots = new Dictionary<WindowKey, WindowSpot>
        {
            [wHere.Key] = new WindowSpot.Shown(200),
            [wOther.Key] = new WindowSpot.Shown(100),
            [wMin.Key] = WindowSpot.Hidden.Instance
        };

        var allGroups = ComputerList.Groups(200, "", running, installed, spots);
        Assert.Equal([wHere], allGroups.Here);
        Assert.Equal([wOther, wMin], allGroups.Elsewhere);
        Assert.Equal([idleApp], allGroups.Windowless);
        Assert.Empty(allGroups.Unopened);

        // Search by Pinyin initials "bwl" finds unopened "备忘录"
        var bwlSearch = ComputerList.Groups(200, "bwl", running, installed, spots);
        Assert.Empty(bwlSearch.Here);
        Assert.Empty(bwlSearch.Elsewhere);
        Assert.Empty(bwlSearch.Windowless);
        Assert.Equal([notesApp], bwlSearch.Unopened);

        // Search by window title matches only that specific window
        var titleSearch = ComputerList.Groups(200, "snug-win", running, installed, spots);
        Assert.Empty(titleSearch.Here);
        Assert.Equal([wOther], titleSearch.Elsewhere);
    }

    [Fact]
    public void ComputerList_ActionFor_FocusesHerePullsElsewhereOrOpens()
    {
        var appA = new AppInfo("a.exe", "AppA", null);
        var appB = new AppInfo("b.exe", "AppB", null);
        var appC = new AppInfo("c.exe", "AppC", null);

        var wA = new WindowInfo(new WindowKey(1, 11), "AppA", appA.AppId, "Main", false);
        var wB = new WindowInfo(new WindowKey(2, 22), "AppB", appB.AppId, "Editor", false);

        var running = new List<RunningApp>
        {
            new(appA, 1, [wA]),
            new(appB, 2, [wB])
        };
        var spots = new Dictionary<WindowKey, WindowSpot>
        {
            [wA.Key] = new WindowSpot.Shown(500),
            [wB.Key] = new WindowSpot.Shown(100)
        };

        Assert.Equal(new ComputerAppAction.Focus(wA), ComputerList.ActionFor(appA, 500, running, spots));
        Assert.Equal(new ComputerAppAction.Pull(wB), ComputerList.ActionFor(appB, 500, running, spots));
        Assert.Equal(new ComputerAppAction.Open(appC), ComputerList.ActionFor(appC, 500, running, spots));
    }

    [Fact]
    public async Task DeskSet_ComputerScreen_PullFocusRestoreAndRetireWorkEndToEnd()
    {
        var displays = new FakeComputerDisplays();
        var windows = new FakeComputerWindows();
        var timeline = new FakeTimeline();
        var desks = new DeskSet(displays, windows, new FakeComputerApps(), _paths, timeline, defaultZoom: 2);

        var compChoice = new PhoneChoice("1080p", new PhoneScreenSpecData(1080, 1920), DeviceKind.Computer);
        var cfg = new AppConfig
        {
            Screens =
            [
                new ScreenConfig
                {
                    Id = "comp-1",
                    Name = "电脑屏",
                    Device = compChoice,
                    Orientation = ScreenOrientation.Landscape,
                    Enabled = true
                }
            ]
        }.Sanitize();

        var wishes = ScreenPlan.Wishes(cfg, remote: true);
        Assert.Single(wishes);
        Assert.Equal(ScreenBehavior.Computer, wishes[0].Behavior);

        desks.SetRemote(true);
        desks.SetWishes(wishes);
        await desks.IdleAsync();

        var compState = desks.StateOf("comp-1");
        Assert.Equal(PhoneScreenPhase.Ready, compState?.ScreenPhase);
        uint compDisplayId = compState!.DisplayId!.Value;
        var compBounds = displays.BoundsOf(compDisplayId)!.Value;

        var key1 = new WindowKey(101, 1001);
        var key2 = new WindowKey(102, 1002);
        var orig1 = new RectD(100, 120, 900, 650);
        var orig2 = new RectD(200, 150, 800, 600);
        windows.Frames[key1] = orig1;
        windows.Frames[key2] = orig2;
        var w1 = new WindowInfo(key1, "Code", "Code.exe", "Program.cs", false);
        var w2 = new WindowInfo(key2, "Terminal", "WindowsTerminal.exe", "pwsh", false);

        // Pull two windows to computer screen: both centered, no zoom changes, both tracked in Brought
        desks.Pull("comp-1", w1);
        desks.Pull("comp-1", w2);
        await desks.IdleAsync();

        Assert.Equal(0, windows.ZoomDeltaTotal);
        Assert.True(compBounds.Contains(windows.Frames[key1].Center));
        Assert.True(compBounds.Contains(windows.Frames[key2].Center));
        Assert.Equal(orig1.Width, windows.Frames[key1].Width);
        Assert.Equal(orig1.Height, windows.Frames[key1].Height);
        Assert.Contains(key1, desks.StateOf("comp-1")!.Brought);
        Assert.Contains(key2, desks.StateOf("comp-1")!.Brought);
        Assert.True(File.Exists(_paths.BroughtFileForScreen("comp-1")));

        // Focus a window already on the computer screen: focuses in place without moving
        int movesBefore = windows.MoveCount;
        desks.Focus("comp-1", w1);
        await desks.IdleAsync();
        Assert.Equal(key1, windows.LastFocused);
        Assert.Equal(movesBefore, windows.MoveCount);

        // Restore w1 individually: w1 returns to orig1, w2 stays on computer screen
        desks.PutBackBrought("comp-1", key1);
        await desks.IdleAsync();
        Assert.Equal(orig1, windows.Frames[key1]);
        Assert.DoesNotContain(key1, desks.StateOf("comp-1")!.Brought);
        Assert.Contains(key2, desks.StateOf("comp-1")!.Brought);

        // Focus w1 after it left the computer screen: automatically pulls it back
        desks.Focus("comp-1", w1);
        await desks.IdleAsync();
        Assert.True(compBounds.Contains(windows.Frames[key1].Center));
        Assert.Contains(key1, desks.StateOf("comp-1")!.Brought);

        // Shutdown restores all remaining brought windows and cleans up brought ledger file
        await desks.ShutdownAsync();
        Assert.Equal(orig1, windows.Frames[key1]);
        Assert.Equal(orig2, windows.Frames[key2]);
        Assert.False(File.Exists(_paths.BroughtFileForScreen("comp-1")));
    }

    private sealed class FakeTimeline : ITimeline
    {
        public double Now { get; private set; }
        public Task SleepAsync(double seconds, CancellationToken ct = default)
        {
            Now += seconds;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeComputerDisplays : IPhoneDisplays
    {
        private uint _nextId = 3000;
        public Dictionary<uint, ScreenFit> ActiveFits { get; } = new();
        public Dictionary<uint, int> Widths { get; } = new();

        public Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default)
        {
            uint id = _nextId++;
            ActiveFits[id] = fit;
            Widths[id] = fit.BaseWidth;
            return Task.FromResult(id);
        }

        public Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default)
        {
            ActiveFits[displayId] = fit;
            Widths[displayId] = fit.BaseWidth;
            return Task.FromResult(true);
        }

        public ScreenFit? FitOf(uint displayId) => ActiveFits.TryGetValue(displayId, out var f) ? f : null;
        public Task<bool> RetireAsync(uint displayId, CancellationToken ct = default)
        {
            ActiveFits.Remove(displayId);
            Widths.Remove(displayId);
            return Task.FromResult(true);
        }
        public bool IsOnline(uint displayId) => ActiveFits.ContainsKey(displayId);
        public int? WidthOf(uint displayId) => Widths.TryGetValue(displayId, out int w) ? w : null;
        public Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default)
        {
            Widths[displayId] = width;
            return Task.CompletedTask;
        }
        public RectD? BoundsOf(uint displayId) =>
            ActiveFits.ContainsKey(displayId) ? new RectD(4000, 0, 1920, 1080) : null;
        public RectD? WindowAreaOf(uint displayId) => BoundsOf(displayId);
        public RectD? MainArea() => new(0, 0, 1920, 1040);
        public IReadOnlyList<OtherDisplay> OtherDisplays() => Array.Empty<OtherDisplay>();
        public Task SetModeAsync(uint displayId, DisplayModeInfo mode, bool permanently, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeComputerWindows : IWindowControl
    {
        public Dictionary<WindowKey, RectD> Frames { get; } = new();
        public WindowKey? LastFocused { get; private set; }
        public int MoveCount { get; private set; }
        public int ZoomDeltaTotal { get; private set; }

        public Task<bool> ExistsAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<bool> IsShownAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<WindowLook?> LookAsync(WindowKey window) =>
            Task.FromResult(Frames.TryGetValue(window, out var r) ? new WindowLook(r) : null);
        public Task<RectD?> MoveAsync(WindowKey window, RectD frame)
        {
            if (!Frames.ContainsKey(window)) return Task.FromResult<RectD?>(null);
            MoveCount++;
            Frames[window] = frame;
            return Task.FromResult<RectD?>(frame);
        }
        public Task SetFullScreenAsync(WindowKey window, bool on) => Task.CompletedTask;
        public Task SetMinimizedAsync(WindowKey window, bool on) => Task.CompletedTask;
        public Task FocusAsync(WindowKey window)
        {
            LastFocused = window;
            return Task.CompletedTask;
        }
        public Task<bool> ZoomAsync(WindowKey window, int steps)
        {
            ZoomDeltaTotal += steps;
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<WindowInfo>> ShownWindowsAsync(int pid) => Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());
        public Task<WindowInfo?> AskingWindowAsync(int pid) => Task.FromResult<WindowInfo?>(null);
        public Task<bool> CloseAsync(WindowKey window) => Task.FromResult(Frames.Remove(window));
        public Task<RectD?> CloseButtonAsync(WindowKey window) => Task.FromResult<RectD?>(null);
        public Task<Dictionary<WindowKey, RectD>> FramesAsync(int pid) => Task.FromResult(new Dictionary<WindowKey, RectD>(Frames));
        public Task<Dictionary<WindowKey, RectD>> SnapshotAsync() => Task.FromResult(new Dictionary<WindowKey, RectD>(Frames));
        public Task RestoreAsync(IReadOnlyDictionary<WindowKey, RectD> snapshot, WindowKey? except) => Task.CompletedTask;
    }

    private sealed class FakeComputerApps : IAppControl
    {
        public Task<int?> PidOfAsync(AppInfo app) => Task.FromResult<int?>(null);
        public Task<IReadOnlyList<int>> PidsAsync(string appId) => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
        public Task<int?> OpenAsync(AppInfo app) => Task.FromResult<int?>(null);
        public Task<bool> NewWindowAsync(int pid) => Task.FromResult(false);
        public Task<bool> QuitAsync(int pid) => Task.FromResult(true);
        public Task<bool> ForceQuitAsync(int pid) => Task.FromResult(true);
        public Task<bool> IsRunningAsync(int pid) => Task.FromResult(true);
    }
}
