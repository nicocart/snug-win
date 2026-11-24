using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class PhoneDeskTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnugPaths _paths;

    public PhoneDeskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "snugwin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new SnugPaths(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class FakeTimeline : ITimeline
    {
        public double Now { get; set; }
        public Task SleepAsync(double seconds, CancellationToken ct = default)
        {
            Now += Math.Max(0, seconds);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDisplays : IPhoneDisplays
    {
        public uint? ActiveId { get; set; }
        public bool RejectReshape { get; set; }
        public bool DelayRetirement { get; set; }
        public bool ThrowSafetyAbortOnCreate { get; set; }
        public bool AlwaysFailCreate { get; set; }
        public int CreateCount { get; private set; }
        public int CurrentWidth { get; private set; } = 540;
        public ScreenFit? CurrentFit { get; private set; }
        public RectD PhoneBounds => new(2560, 0, CurrentWidth * 2, (CurrentFit ?? ScreenFit.Standard).Size(CurrentWidth).Height * 2);

        public Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default)
        {
            CreateCount++;
            if (AlwaysFailCreate)
                throw new PhoneScreenException(PhoneScreenErrorKind.CreateFailed, "Attach failed");
            if (ThrowSafetyAbortOnCreate)
                throw new PhoneScreenException(PhoneScreenErrorKind.CreateFailed, "原主屏脱离", isSafetyAbort: true);
            uint id = (uint)(1000 + CreateCount);
            ActiveId = id;
            CurrentFit = fit;
            CurrentWidth = fit.BaseWidth;
            return Task.FromResult(id);
        }

        public Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default)
        {
            if (RejectReshape || ActiveId != displayId) return Task.FromResult(false);
            CurrentFit = fit;
            CurrentWidth = fit.BaseWidth;
            return Task.FromResult(true);
        }

        public ScreenFit? FitOf(uint displayId) => ActiveId == displayId ? CurrentFit : null;

        public Task<bool> RetireAsync(uint displayId, CancellationToken ct = default)
        {
            RetireCount++;
            if (ActiveId == displayId && !DelayRetirement) ActiveId = null;
            return Task.FromResult(ActiveId != displayId);
        }

        public bool Detached { get; set; }
        public bool RemoteExclusive { get; set; }
        public bool FailReactivation { get; set; }
        public int Reactivations { get; private set; }
        public int RetireCount { get; private set; }
        public bool IsOnline(uint displayId) => ActiveId == displayId && !Detached;
        public bool IsRemoteExclusiveDisplayActive() => RemoteExclusive;
        public Task<bool> TryReactivateAsync(uint displayId, int width, CancellationToken ct = default)
        {
            Reactivations++;
            if (FailReactivation || ActiveId != displayId || RemoteExclusive) return Task.FromResult(false);
            Detached = false;
            CurrentWidth = width;
            return Task.FromResult(true);
        }
        public int? WidthOf(uint displayId) => ActiveId == displayId ? CurrentWidth : null;

        public Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default)
        {
            if (ActiveId == displayId) CurrentWidth = width;
            return Task.CompletedTask;
        }

        public RectD? BoundsOf(uint displayId) => ActiveId == displayId ? PhoneBounds : null;

        public RectD? WindowAreaOf(uint displayId)
        {
            if (ActiveId != displayId) return null;
            var b = PhoneBounds;
            double bar = ScreenFit.HomeBarHeight(CurrentWidth) * 2;
            return new RectD(b.X, b.Y, b.Width, b.Height - bar);
        }

        public RectD? MainArea() => new(0, 0, 1920, 1040);
        public IReadOnlyList<OtherDisplay> OtherDisplays() => Array.Empty<OtherDisplay>();
        public Task SetModeAsync(uint displayId, DisplayModeInfo mode, bool permanently, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeWindows : IWindowControl
    {
        public Dictionary<WindowKey, RectD> Frames { get; } = new();
        public List<(WindowKey Window, RectD Frame)> Moves { get; } = new();
        public bool BlockMoves { get; set; }
        public int ZoomDeltaTotal { get; private set; }
        public double MinLogicalWidth { get; set; }

        public Task<bool> ExistsAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<bool> IsShownAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));

        public Task<WindowLook?> LookAsync(WindowKey window) =>
            Task.FromResult(Frames.TryGetValue(window, out var r) ? new WindowLook(r) : null);

        public Task<RectD?> MoveAsync(WindowKey window, RectD frame)
        {
            Moves.Add((window, frame));
            if (BlockMoves || !Frames.ContainsKey(window)) return Task.FromResult<RectD?>(null);
            double clampedW = Math.Max(frame.Width, MinLogicalWidth * 2);
            var got = new RectD(frame.X, frame.Y, clampedW, frame.Height);
            Frames[window] = got;
            return Task.FromResult<RectD?>(got);
        }

        public Task SetFullScreenAsync(WindowKey window, bool on) => Task.CompletedTask;
        public Task SetMinimizedAsync(WindowKey window, bool on) => Task.CompletedTask;
        public Task FocusAsync(WindowKey window) => Task.CompletedTask;

        public Task<bool> ZoomAsync(WindowKey window, int steps)
        {
            ZoomDeltaTotal += steps;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WindowInfo>> ShownWindowsAsync(int pid) =>
            Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());

        public Task<WindowInfo?> AskingWindowAsync(int pid) => Task.FromResult<WindowInfo?>(null);
        public Task<bool> CloseAsync(WindowKey window) => Task.FromResult(Frames.Remove(window));
        public Task<RectD?> CloseButtonAsync(WindowKey window) => Task.FromResult<RectD?>(null);
        public Task<Dictionary<WindowKey, RectD>> FramesAsync(int pid) => Task.FromResult(new Dictionary<WindowKey, RectD>(Frames));
        public Task<Dictionary<WindowKey, RectD>> SnapshotAsync() => Task.FromResult(new Dictionary<WindowKey, RectD>(Frames));
        public Task RestoreAsync(IReadOnlyDictionary<WindowKey, RectD> snapshot, WindowKey? except) => Task.CompletedTask;
    }

    private sealed class FakeApps : IAppControl
    {
        public Task<int?> PidOfAsync(AppInfo app) => Task.FromResult<int?>(null);
        public Task<IReadOnlyList<int>> PidsAsync(string appId) => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
        public Task<int?> OpenAsync(AppInfo app) => Task.FromResult<int?>(null);
        public Task<bool> NewWindowAsync(int pid) => Task.FromResult(false);
        public Task<bool> QuitAsync(int pid) => Task.FromResult(true);
        public Task<bool> ForceQuitAsync(int pid) => Task.FromResult(true);
        public Task<bool> IsRunningAsync(int pid) => Task.FromResult(true);
    }

    [Fact]
    public async Task Pull_StepsUpWidthWhenClamped_ZoomsAndRestoresOnPutBack()
    {
        var displays = new FakeDisplays();
        var windows = new FakeWindows();
        var timeline = new FakeTimeline();
        var desk = new PhoneDesk(displays, windows, new FakeApps(), _paths, timeline, defaultZoom: 2);

        var winKey = new WindowKey(42, 0x100);
        var original = new RectD(100, 100, 1500, 700);
        windows.Frames[winKey] = original;
        windows.MinLogicalWidth = 700; // clamps 540 -> triggers step-up to 700

        desk.SetWanted(true);
        await desk.IdleAsync();
        Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);

        var info = new WindowInfo(winKey, "VS Code", "Code.exe", "README.md");
        desk.Pull(info);
        await desk.IdleAsync();

        Assert.Equal(700, displays.CurrentWidth);
        Assert.Equal(2, windows.ZoomDeltaTotal);
        Assert.False(desk.State.ListVisible);
        Assert.NotNull(desk.State.Placed);

        desk.PutBack();
        await desk.IdleAsync();

        Assert.Null(desk.State.Placed);
        Assert.True(desk.State.ListVisible);
        Assert.Equal(540, displays.CurrentWidth);
        Assert.Equal(0, windows.ZoomDeltaTotal);
        Assert.Equal(original, windows.Frames[winKey]);
        Assert.False(File.Exists(_paths.PhoneRecordFile));
    }

    [Fact]
    public async Task DisplaysChanged_ReactivatesBoundDisplayAfterTopologyLoss()
    {
        var displays = new FakeDisplays();
        var desk = new PhoneDesk(displays, new FakeWindows(), new FakeApps(), _paths, new FakeTimeline());

        desk.SetWanted(true);
        await desk.IdleAsync();
        Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);

        displays.Detached = true;
        desk.DisplaysChanged();
        await desk.IdleAsync();

        Assert.True(displays.IsOnline(1001));
        Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);
        Assert.Equal(1, displays.Reactivations);
        Assert.Equal(0, displays.RetireCount);
    }

    [Fact]
    public async Task OfflineDisplayWaitsWhileUuOwnsDesktopThenResumes()
    {
        var displays = new FakeDisplays();
        var desk = new PhoneDesk(displays, new FakeWindows(), new FakeApps(), _paths, new FakeTimeline());

        desk.SetWanted(true);
        await desk.IdleAsync();
        displays.Detached = true;
        displays.RemoteExclusive = true;

        desk.DisplaysChanged();
        await desk.IdleAsync();

        Assert.Equal(PhoneScreenPhase.Suspended, desk.State.ScreenPhase);
        Assert.Equal(0, displays.Reactivations);
        Assert.Equal(0, displays.RetireCount);

        displays.RemoteExclusive = false;
        desk.DisplaysChanged();
        await desk.IdleAsync();

        Assert.True(displays.IsOnline(1001));
        Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);
    }

    [Fact]
    public async Task OfflineDisplay_RebuildsAtMostOncePerConnectionWhenDetachmentRepeats()
    {
        var displays = new FakeDisplays { FailReactivation = true };
        var timeline = new FakeTimeline();
        var desk = new PhoneDesk(displays, new FakeWindows(), new FakeApps(), _paths, timeline);

        desk.SetWanted(true);
        await desk.IdleAsync();
        Assert.Equal(1, displays.CreateCount);

        // 1st call records _offlineSince + 1s debounce, next 3 exhaust backoff (5, 15, 30s), 5th triggers rebuild.
        displays.Detached = true;
        for (int i = 0; i < 5; i++)
        {
            timeline.Now += 40;
            desk.SettleDisplays();
            await desk.IdleAsync();
        }
        Assert.Equal(1, displays.RetireCount);
        Assert.Equal(2, displays.CreateCount);

        // If the rebuilt display also detaches and fails reactivation, it must NOT rebuild a second time.
        displays.Detached = true;
        for (int i = 0; i < 5; i++)
        {
            timeline.Now += 40;
            desk.SettleDisplays();
            await desk.IdleAsync();
        }
        Assert.Equal(1, displays.RetireCount);
        Assert.Equal(2, displays.CreateCount);
        Assert.Equal(PhoneScreenPhase.Suspended, desk.State.ScreenPhase);
    }

    private static readonly WindowInfo Editor = new(new WindowKey(42, 0x100), "VS Code", "Code.exe", "README.md");
    private static readonly RectD EditorOriginal = new(-1400, 90, 960, 640);

    private async Task<PhoneDesk> PlaceEditorAsync(FakeDisplays displays, FakeWindows windows)
    {
        windows.Frames[Editor.Key] = EditorOriginal;
        var desk = new PhoneDesk(displays, windows, new FakeApps(), _paths, new FakeTimeline());
        desk.SetWanted(true);
        await desk.IdleAsync();
        desk.Pull(Editor);
        await desk.IdleAsync();
        Assert.Equal(Editor.Key, desk.PlacedWindowKey);
        windows.Moves.Clear();
        return desk;
    }

    [Fact]
    public async Task Reshape_RejectedWhileOnline_KeepsOldDisplayAndPlacedWindow()
    {
        var displays = new FakeDisplays { RejectReshape = true };
        var windows = new FakeWindows();
        var desk = await PlaceEditorAsync(displays, windows);
        var before = desk.State;

        desk.SetFit(new ScreenFit(1.0));
        await desk.IdleAsync();

        Assert.IsType<DeskNotice.ReshapeFailed>(desk.State.Notice);
        Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);
        Assert.Equal(before.DisplayId, desk.State.DisplayId);
        Assert.Equal(ScreenFit.Standard, displays.CurrentFit);
        Assert.Equal(before.Placed, desk.State.Placed);
        Assert.False(desk.State.ListVisible);
        Assert.Empty(windows.Moves);
        Assert.Equal(2, windows.ZoomDeltaTotal);
        Assert.True(File.Exists(_paths.PhoneRecordFile));
        Assert.Equal(1, displays.CreateCount);
    }

    [Fact]
    public async Task Reshape_OldDisplayLost_RestoresOnlyPlacedWindowAndWaitsForReenable()
    {
        var displays = new FakeDisplays();
        var windows = new FakeWindows();
        var desk = await PlaceEditorAsync(displays, windows);
        var other = new WindowInfo(new WindowKey(77, 0x200), "Notepad", "notepad.exe", "Draft");
        windows.Frames[other.Key] = new RectD(20, 40, 800, 600);
        var oldId = displays.ActiveId;
        displays.ActiveId = null;
        var fit = new ScreenFit(1.0);

        desk.SetFit(fit);
        await desk.IdleAsync();

        Assert.IsType<DeskNotice.ReshapeFailed>(desk.State.Notice);
        Assert.Equal(PhoneScreenPhase.Failed, desk.State.ScreenPhase);
        Assert.Null(desk.State.DisplayId);
        Assert.Null(desk.State.Placed);
        Assert.Null(desk.PlacedWindowKey);
        Assert.True(desk.State.ListVisible);
        Assert.Equal(EditorOriginal, windows.Frames[Editor.Key]);
        Assert.Equal(0, windows.ZoomDeltaTotal);
        Assert.False(File.Exists(_paths.PhoneRecordFile));
        desk.DisplaysChanged();
        desk.Pull(other);
        await desk.IdleAsync();
        Assert.IsType<DeskNotice.ScreenNotReady>(desk.State.Notice);
        Assert.Equal((Editor.Key, EditorOriginal), Assert.Single(windows.Moves));
        Assert.Equal(1, displays.CreateCount);

        desk.SetWanted(false);
        await desk.IdleAsync();
        desk.SetWanted(true);
        await desk.IdleAsync();
        Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);
        Assert.NotEqual(oldId, desk.State.DisplayId);
        Assert.Equal(displays.ActiveId, desk.State.DisplayId);
        Assert.Equal(fit, displays.CurrentFit);
        Assert.Null(desk.State.Placed);
        Assert.Equal(2, displays.CreateCount);
    }

    [Fact]
    public async Task Reshape_OldDisplayLost_BlockedReturnKeepsRecordForRecovery()
    {
        var displays = new FakeDisplays();
        var windows = new FakeWindows();
        var desk = await PlaceEditorAsync(displays, windows);
        displays.ActiveId = null;
        windows.BlockMoves = true;

        desk.SetFit(new ScreenFit(1.0));
        await desk.IdleAsync();

        var ledger = new RestoreRecordStore<PhoneRecord>(_paths.PhoneRecordFile);
        var pending = Assert.IsType<PhoneRecord>(ledger.Pending());
        Assert.Equal(PhoneScreenPhase.Failed, desk.State.ScreenPhase);
        Assert.Null(desk.State.Placed);
        Assert.Equal(EditorOriginal, pending.Original);
        Assert.Equal(0, pending.Zoom);
        windows.BlockMoves = false;
        desk.Recover();
        await desk.IdleAsync();
        Assert.Equal(EditorOriginal, windows.Frames[Editor.Key]);
        Assert.Equal(0, windows.ZoomDeltaTotal);
        Assert.Null(ledger.Pending());
    }

    [Fact]
    public async Task Retire_Unconfirmed_ReportsOfflineOnlyAfterItIsObserved()
    {
        var displays = new FakeDisplays { DelayRetirement = true };
        var desk = new PhoneDesk(displays, new FakeWindows(), new FakeApps(), _paths, new FakeTimeline());
        desk.SetWanted(true);
        await desk.IdleAsync();
        var id = displays.ActiveId;
        var messages = new ConcurrentQueue<string>();
        void Capture(LogLevel _, string message, DateTimeOffset __) => messages.Enqueue(message);
        Log.Logged += Capture;
        try
        {
            desk.SetWanted(false);
            await desk.IdleAsync();
            desk.SettleDisplays();
            await desk.IdleAsync();
            Assert.Equal(id, displays.ActiveId);
            Assert.Contains($"手机屏 #{id} 尚未下线，稍后自动善后", messages);
            Assert.DoesNotContain($"已撤掉手机屏 #{id}", messages);
            Assert.DoesNotContain($"撤掉的手机屏 #{id} 已下线", messages);

            displays.ActiveId = null;
            desk.SettleDisplays();
            await desk.IdleAsync();
            Assert.Contains($"撤掉的手机屏 #{id} 已下线", messages);
        }
        finally { Log.Logged -= Capture; }
    }

    [Fact]
    public async Task Create_SafetyAbort_StopsRetryingImmediately()
    {
        var displays = new FakeDisplays { ThrowSafetyAbortOnCreate = true };
        var timeline = new FakeTimeline();
        var desk = new PhoneDesk(displays, new FakeWindows(), new FakeApps(), _paths, timeline);

        desk.SetWanted(true);
        await desk.IdleAsync();

        Assert.Equal(PhoneScreenPhase.Failed, desk.State.ScreenPhase);
        Assert.Equal(1, displays.CreateCount);
        Assert.Equal(0, timeline.Now);
    }

    [Fact]
    public async Task Create_ContinuousFailures_TripsCircuitBreakerAndStopsRetries()
    {
        var displays = new FakeDisplays { AlwaysFailCreate = true };
        var timeline = new FakeTimeline();
        var desk = new PhoneDesk(displays, new FakeWindows(), new FakeApps(), _paths, timeline);

        desk.SetWanted(true);
        await desk.IdleAsync();

        Assert.Equal(PhoneScreenPhase.Failed, desk.State.ScreenPhase);
        Assert.IsType<DeskNotice.CircuitBreakerTripped>(desk.State.Notice);
        Assert.Equal(4, displays.CreateCount);

        // Further SettleDisplays should NO-OP because circuit breaker is tripped
        desk.SettleDisplays();
        await desk.IdleAsync();
        Assert.Equal(4, displays.CreateCount);

        // DisplaysChanged should also NO-OP
        desk.DisplaysChanged();
        await desk.IdleAsync();
        Assert.Equal(4, displays.CreateCount);

        // Resetting via ResetCircuitBreaker should reset circuit breaker and allow retrying
        desk.ResetCircuitBreaker();
        await desk.IdleAsync();
        Assert.True(displays.CreateCount > 4);
    }

    [Fact]
    public async Task Recover_RestoresLeftoverRecordAfterSimulatedCrash()
    {
        var displays = new FakeDisplays();
        var windows = new FakeWindows();
        var timeline = new FakeTimeline();

        var winKey = new WindowKey(88, 0x200);
        var original = new RectD(50, 60, 800, 600);
        windows.Frames[winKey] = new RectD(2560, 0, 1080, 1800);

        var ledger = new RestoreRecordStore<PhoneRecord>(_paths.PhoneRecordFile);
        ledger.Record(new PhoneRecord
        {
            Window = winKey,
            App = "Windows Terminal",
            Label = "Terminal",
            Original = original,
            Zoom = 2,
            AppId = "WindowsTerminal.exe"
        });

        var desk = new PhoneDesk(displays, windows, new FakeApps(), _paths, timeline);
        desk.Recover();
        await desk.IdleAsync();

        Assert.Equal(original, windows.Frames[winKey]);
        Assert.Equal(-2, windows.ZoomDeltaTotal);
        Assert.Null(ledger.Pending());
    }

    private sealed class SingleInstanceElectronApps : IAppControl
    {
        public int? ExistingPid { get; set; }
        public int LauncherPid { get; set; } = 9999;
        public bool LauncherAlive { get; set; }
        public IReadOnlyList<WindowInfo> Windows { get; set; } = Array.Empty<WindowInfo>();

        public Task<int?> PidOfAsync(AppInfo app) => Task.FromResult(ExistingPid);
        public Task<IReadOnlyList<int>> PidsAsync(string appId) =>
            Task.FromResult<IReadOnlyList<int>>(ExistingPid is { } p ? [p] : []);
        public Task<IReadOnlyList<WindowInfo>> AppWindowsAsync(AppInfo app, int? launchedPid = null) =>
            Task.FromResult(Windows);
        public Task<int?> OpenAsync(AppInfo app) => Task.FromResult<int?>(LauncherPid);
        public Task<bool> NewWindowAsync(int pid) => Task.FromResult(false);
        public Task<bool> QuitAsync(int pid) => Task.FromResult(true);
        public Task<bool> ForceQuitAsync(int pid) => Task.FromResult(true);
        public Task<bool> IsRunningAsync(int pid) => Task.FromResult(pid == LauncherPid ? LauncherAlive : ExistingPid == pid);
    }

    [Fact]
    public async Task Open_SingleInstanceElectronHandoff_PlacesExistingWindowInsteadOfTimingOut()
    {
        var displays = new FakeDisplays();
        var windows = new FakeWindows();
        var timeline = new FakeTimeline();
        var existingKey = new WindowKey(500, 0xABC);
        windows.Frames[existingKey] = new RectD(50, 50, 1200, 800);
        var existingWin = new WindowInfo(existingKey, "Electron", "Code.exe", "Workspace");
        var apps = new SingleInstanceElectronApps
        {
            ExistingPid = 500,
            LauncherPid = 9999,
            LauncherAlive = false,
            Windows = [existingWin]
        };

        var desk = new PhoneDesk(displays, windows, apps, _paths, timeline);
        desk.SetWanted(true);
        await desk.IdleAsync();

        desk.Open(new AppInfo("Code.exe", "Electron", @"C:\Apps\Code.exe"));
        await desk.IdleAsync();

        Assert.NotNull(desk.State.Placed);
        Assert.Equal(existingKey, desk.State.Placed!.Window.Key);
        Assert.Null(desk.State.Notice);
    }
}
