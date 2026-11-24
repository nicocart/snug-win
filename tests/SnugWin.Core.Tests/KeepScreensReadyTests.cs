using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Config;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class KeepScreensReadyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnugPaths _paths;

    public KeepScreensReadyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "snugwin-keepready-" + Guid.NewGuid().ToString("N"));
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
        private uint _nextId = 3000;
        public Dictionary<uint, ScreenFit> ActiveFits { get; } = new();
        public Dictionary<uint, int> Widths { get; } = new();
        public int RetireCount { get; private set; }
        public int CreateCount { get; private set; }
        public bool AlwaysFailCreate { get; set; }

        public Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default) =>
            CreateAsync(fit, null, ct);

        public Task<uint> CreateAsync(ScreenFit fit, string? displayName, CancellationToken ct = default)
        {
            CreateCount++;
            if (AlwaysFailCreate)
                throw new PhoneScreenException(PhoneScreenErrorKind.CreateFailed, "Attach failed");
            uint id = _nextId++;
            ActiveFits[id] = fit;
            Widths[id] = fit.BaseWidth;
            return Task.FromResult(id);
        }

        public Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default)
        {
            if (!ActiveFits.ContainsKey(displayId)) return Task.FromResult(false);
            ActiveFits[displayId] = fit;
            Widths[displayId] = fit.BaseWidth;
            return Task.FromResult(true);
        }

        public ScreenFit? FitOf(uint displayId) =>
            ActiveFits.TryGetValue(displayId, out var fit) ? fit : null;

        public Task<bool> RetireAsync(uint displayId, CancellationToken ct = default)
        {
            RetireCount++;
            ActiveFits.Remove(displayId);
            Widths.Remove(displayId);
            return Task.FromResult(true);
        }

        public bool IsOnline(uint displayId) => ActiveFits.ContainsKey(displayId);
        public int? WidthOf(uint displayId) => Widths.TryGetValue(displayId, out int w) ? w : null;

        public Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default)
        {
            if (ActiveFits.ContainsKey(displayId)) Widths[displayId] = width;
            return Task.CompletedTask;
        }

        public RectD? BoundsOf(uint displayId)
        {
            if (!ActiveFits.TryGetValue(displayId, out var fit)) return null;
            int w = Widths.TryGetValue(displayId, out int cur) ? cur : fit.BaseWidth;
            var sz = fit.Size(w);
            double xOffset = 3000 + (displayId - 3000) * 2000;
            return new RectD(xOffset, 0, sz.Width * 2, sz.Height * 2);
        }

        public RectD? WindowAreaOf(uint displayId)
        {
            if (BoundsOf(displayId) is not { } b || !ActiveFits.TryGetValue(displayId, out var fit))
                return null;
            if (fit.Kind == DeviceKind.Computer) return b;
            int w = Widths[displayId];
            double bar = ScreenFit.HomeBarHeight(fit.Size(w)) * 2;
            return new RectD(b.X, b.Y, b.Width, b.Height - bar);
        }

        public RectD? MainArea() => new(0, 0, 1920, 1040);
        public IReadOnlyList<OtherDisplay> OtherDisplays() => Array.Empty<OtherDisplay>();
        public Task SetModeAsync(uint displayId, DisplayModeInfo mode, bool permanently, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeWindows : IWindowControl
    {
        public Dictionary<WindowKey, RectD> Frames { get; } = new();
        public int ZoomDeltaTotal { get; private set; }
        public double MinLogicalWidth { get; set; }

        public Task<bool> ExistsAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<bool> IsShownAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<WindowLook?> LookAsync(WindowKey window) =>
            Task.FromResult(Frames.TryGetValue(window, out var r) ? new WindowLook(r) : null);

        public Task<RectD?> MoveAsync(WindowKey window, RectD frame)
        {
            if (!Frames.ContainsKey(window)) return Task.FromResult<RectD?>(null);
            double clampedW = frame.X >= 3000 ? Math.Max(frame.Width, MinLogicalWidth * 2) : frame.Width;
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

    private static AppConfig ConfigWithTwoEnabledScreens()
    {
        var ipad = new PhoneChoice("Apple iPad", new PhoneScreenSpecData(1640, 2360, 820), DeviceKind.Tablet);
        var air = new PhoneChoice("Laptop", new PhoneScreenSpecData(1920, 1080), DeviceKind.Computer);
        return new AppConfig()
            .WithScreenAdded(ScreenOrientation.Landscape, ipad, "平板屏")
            .WithScreenAdded(ScreenOrientation.Landscape, air, "电脑屏")
            .WithScreenUpdated(ScreenConfig.LegacyPhoneId, s => s with { Enabled = true });
    }

    [Fact]
    public void AppConfig_KeepScreensReady_DefaultsFalseOmittedInJsonAndRoundTripsWhenTrue()
    {
        var cfg = new AppConfig();
        Assert.False(cfg.KeepScreensReady);
        Assert.DoesNotContain("KeepScreensReady", JsonSerializer.Serialize(cfg), StringComparison.OrdinalIgnoreCase);

        var enabled = cfg.WithKeepScreensReady(true);
        Assert.True(enabled.KeepScreensReady);

        var store = new JsonFileStore<AppConfig>(_paths.ConfigFile);
        store.Save(enabled);
        var loaded = store.LoadOrQuarantine();
        Assert.NotNull(loaded);
        Assert.True(loaded.Sanitize().KeepScreensReady);

        var parsed = AppConfig.FromJsonOrDefault(File.ReadAllText(_paths.ConfigFile));
        Assert.True(parsed.KeepScreensReady);
    }

    [Fact]
    public void ScreenPlan_WishesAndWithheld_HandleKeepScreensReadyAndLaunchOnlyRemotes()
    {
        var tablet = new PhoneChoice("Apple iPad", new PhoneScreenSpecData(1640, 2360, 820), DeviceKind.Tablet);
        var cfg = new AppConfig()
            .WithScreenAdded(ScreenOrientation.Landscape, tablet, "平板屏");
        var allIds = cfg.EffectiveScreens().Where(s => s.Enabled).Select(s => s.Id).ToList();
        Assert.Equal(2, allIds.Count);

        // Standby without KeepScreensReady or MainScreen -> no wishes, nothing withheld
        Assert.Empty(ScreenPlan.Wishes(cfg, remote: false));
        Assert.Empty(ScreenPlan.Withheld(cfg, remote: true, seesNewScreens: true));
        Assert.Empty(ScreenPlan.Withheld(cfg, remote: false, seesNewScreens: false));

        // Launch-only remote without KeepScreensReady -> all non-main screens withheld
        Assert.Empty(ScreenPlan.Wishes(cfg, remote: true, seesNewScreens: false));
        Assert.Equal(allIds, ScreenPlan.Withheld(cfg, remote: true, seesNewScreens: false));

        // With MainScreen set, only non-main screens are withheld; main stays resident + StaysOnDismiss
        cfg = cfg.WithMainScreen(allIds[0]);
        var mainOnly = ScreenPlan.Wishes(cfg, remote: true, seesNewScreens: false);
        var mainWish = Assert.Single(mainOnly);
        Assert.Equal(allIds[0], mainWish.Id);
        Assert.True(mainWish.IsMain);
        Assert.False(mainWish.Prepared);
        Assert.True(mainWish.IsResident);
        Assert.True(mainWish.StaysOnDismiss);
        Assert.Equal([allIds[1]], ScreenPlan.Withheld(cfg, remote: true, seesNewScreens: false));

        // If only MainScreen is enabled, Withheld is empty
        cfg = cfg.WithScreenUpdated(allIds[1], s => s with { Enabled = false });
        Assert.Empty(ScreenPlan.Withheld(cfg, remote: true, seesNewScreens: false));
        cfg = cfg.WithScreenUpdated(allIds[1], s => s with { Enabled = true });

        // With KeepScreensReady=true, idle and launch-only remote both get all enabled screens
        cfg = cfg.WithMainScreen(null).WithKeepScreensReady(true);
        var idlePrepared = ScreenPlan.Wishes(cfg, remote: false);
        Assert.Equal(allIds, idlePrepared.Select(w => w.Id));
        Assert.All(idlePrepared, w =>
        {
            Assert.False(w.IsMain);
            Assert.True(w.Prepared);
            Assert.True(w.IsResident);
            Assert.False(w.StaysOnDismiss);
        });

        var launchOnlyPrepared = ScreenPlan.Wishes(cfg, remote: true, seesNewScreens: false);
        Assert.Empty(ScreenPlan.Withheld(cfg, remote: true, seesNewScreens: false));
        Assert.All(launchOnlyPrepared, w =>
        {
            Assert.True(w.Prepared);
            Assert.True(w.IsResident);
            Assert.True(w.StaysOnDismiss);
        });

        // Preview never marks wishes as Prepared and ignores seesNewScreens
        var previewWishes = ScreenPlan.Wishes(cfg, remote: false, preview: true, seesNewScreens: false);
        Assert.Equal(2, previewWishes.Count);
        Assert.All(previewWishes, w => Assert.False(w.Prepared));
        Assert.Empty(ScreenPlan.Withheld(cfg, remote: true, seesNewScreens: false, preview: true));
    }

    [Fact]
    public void ScreenPlan_ForController_ExemptsPreparedAndMainResidentScreens()
    {
        var cfg = ConfigWithTwoEnabledScreens().WithKeepScreensReady(true);
        var preparedWishes = ScreenPlan.Wishes(cfg, remote: true);
        Assert.Equal(3, preparedWishes.Count); // 2 touch + 1 computer

        // All prepared screens are resident -> none are dropped regardless of wantsTouch
        Assert.Equal(3, ScreenPlan.ForController(preparedWishes, wantsTouch: false, remote: true).Count);
        Assert.Equal(3, ScreenPlan.ForController(preparedWishes, wantsTouch: true, remote: true).Count);

        // Non-prepared wishes still filter non-resident screens by controller capability
        var nonPrepared = ScreenPlan.Wishes(cfg.WithKeepScreensReady(false), remote: true);
        Assert.Equal(2, ScreenPlan.ForController(nonPrepared, wantsTouch: true, remote: true).Count);
        Assert.Single(ScreenPlan.ForController(nonPrepared, wantsTouch: false, remote: true));
    }

    [Fact]
    public async Task PhoneDesk_DismissScreen_WithStaysOnDismiss_KeepsDisplayOnlineAndReturnsWindows()
    {
        var displays = new FakeDisplays();
        var windows = new FakeWindows();
        var timeline = new FakeTimeline();

        var desk = new PhoneDesk(
            displays,
            windows,
            new FakeApps(),
            _paths,
            timeline,
            defaultZoom: 2,
            prepared: true,
            staysOnDismiss: true);

        var placedKey = new WindowKey(101, 0x1001);
        var placedOrig = new RectD(80, 90, 1200, 720);
        windows.Frames[placedKey] = placedOrig;
        windows.MinLogicalWidth = 700; // step up from 540 to 700

        desk.SetRemote(true);
        desk.SetWanted(true);
        await desk.IdleAsync();

        Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);
        Assert.NotNull(desk.State.DisplayId);
        uint displayId = desk.State.DisplayId!.Value;
        Assert.True(desk.State.StaysOnDismiss);

        // Place a window (triggers zoom + width step-up)
        desk.Pull(new WindowInfo(placedKey, "Code", "Code.exe", "Program.cs"));
        await desk.IdleAsync();
        Assert.Equal(700, displays.WidthOf(displayId));
        Assert.False(desk.State.ListVisible);
        Assert.NotNull(desk.State.Placed);

        // Add a stray user window onto the virtual display bounds
        var strayKey = new WindowKey(202, 0x2002);
        var screenBounds = displays.BoundsOf(displayId)!.Value;
        windows.Frames[strayKey] = new RectD(screenBounds.X + 40, screenBounds.Y + 40, 400, 300);

        // Dismiss -> stays online, restores placed window, evacuates stray window, resets width, sets KeptOnDismiss
        desk.DismissScreen();
        await desk.IdleAsync();

        Assert.False(desk.State.Dismissed);
        Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);
        Assert.Equal(displayId, desk.State.DisplayId);
        Assert.Equal(0, displays.RetireCount);
        Assert.True(displays.IsOnline(displayId));
        Assert.True(desk.State.ListVisible);
        Assert.Null(desk.State.Placed);
        Assert.IsType<DeskNotice.KeptOnDismiss>(desk.State.Notice);
        Assert.Equal(540, displays.WidthOf(displayId));
        Assert.Equal(0, windows.ZoomDeltaTotal);
        Assert.Equal(placedOrig, windows.Frames[placedKey]);

        var mainArea = displays.MainArea()!.Value;
        Assert.True(mainArea.Contains(windows.Frames[strayKey].Center));
    }

    [Fact]
    public async Task DeskSet_SetRemoteFalse_CleansPreparedNonMainScreensButLeavesMainUntouched()
    {
        var displays = new FakeDisplays();
        var windows = new FakeWindows();
        var timeline = new FakeTimeline();
        var desks = new DeskSet(displays, windows, new FakeApps(), _paths, timeline, defaultZoom: 2);

        var tablet = new PhoneChoice("iPad Air", new PhoneScreenSpecData(1640, 2360, 820), DeviceKind.Tablet);
        var cfg = new AppConfig()
            .WithScreenAdded(ScreenOrientation.Landscape, tablet, "平板屏")
            .WithMainScreen(ScreenConfig.LegacyPhoneId)
            .WithKeepScreensReady(true);

        var preparedId = cfg.EffectiveScreens()[1].Id;
        var mainId = ScreenConfig.LegacyPhoneId;

        var remoteWishes = ScreenPlan.Wishes(cfg, remote: true);
        desks.SetRemote(true);
        desks.SetWishes(remoteWishes);
        await desks.IdleAsync();

        Assert.NotNull(desks.StateOf(mainId)?.DisplayId);
        Assert.NotNull(desks.StateOf(preparedId)?.DisplayId);
        uint mainDisp = desks.StateOf(mainId)!.DisplayId!.Value;
        uint prepDisp = desks.StateOf(preparedId)!.DisplayId!.Value;

        // Place window on persistent MainScreen
        var mainWinKey = new WindowKey(301, 0x3001);
        var mainWinOrig = new RectD(50, 50, 800, 600);
        windows.Frames[mainWinKey] = mainWinOrig;
        desks.Pull(mainId, new WindowInfo(mainWinKey, "MainApp", "MainApp.exe", "Doc"));
        await desks.IdleAsync();

        // Place window + stray window on Prepared non-main screen (step up width via MinLogicalWidth)
        var prepWinKey = new WindowKey(302, 0x3002);
        var prepWinOrig = new RectD(120, 140, 900, 650);
        windows.Frames[prepWinKey] = prepWinOrig;
        windows.MinLogicalWidth = 1200;
        desks.Pull(preparedId, new WindowInfo(prepWinKey, "PrepApp", "PrepApp.exe", "Work"));
        await desks.IdleAsync();
        Assert.True(displays.WidthOf(prepDisp) > displays.FitOf(prepDisp)!.BaseWidth);

        var prepBounds = displays.BoundsOf(prepDisp)!.Value;
        var strayKey = new WindowKey(303, 0x3003);
        windows.Frames[strayKey] = new RectD(prepBounds.X + 20, prepBounds.Y + 20, 400, 300);

        // End remote session -> SetWishes(remote: false) + SetRemote(false)
        desks.SetWishes(ScreenPlan.Wishes(cfg, remote: false));
        desks.SetRemote(false);
        await desks.IdleAsync();

        // Both displays remain online
        Assert.Equal(0, displays.RetireCount);
        Assert.True(displays.IsOnline(mainDisp));
        Assert.True(displays.IsOnline(prepDisp));

        // Prepared non-main screen is cleaned up
        var prepState = desks.StateOf(preparedId)!;
        Assert.Null(prepState.Placed);
        Assert.True(prepState.ListVisible);
        Assert.Null(prepState.Notice);
        Assert.Equal(prepWinOrig, windows.Frames[prepWinKey]);
        Assert.Equal(displays.FitOf(prepDisp)!.BaseWidth, displays.WidthOf(prepDisp));
        Assert.True(displays.MainArea()!.Value.Contains(windows.Frames[strayKey].Center));

        // Persistent IsMain screen keeps its placed window untouched
        var mainState = desks.StateOf(mainId)!;
        Assert.Equal(mainWinKey, mainState.Placed?.Window.Key);
        Assert.False(mainState.ListVisible);
        Assert.NotEqual(mainWinOrig, windows.Frames[mainWinKey]);

        await desks.ShutdownAsync();
    }

    [Fact]
    public async Task DeskSet_SetRemoteTrueBeforeCleanupRuns_AbortsPreparedCleanupAndResetsCircuitBreaker()
    {
        var displays = new FakeDisplays { AlwaysFailCreate = true };
        var windows = new FakeWindows();
        var timeline = new FakeTimeline();
        var desks = new DeskSet(displays, windows, new FakeApps(), _paths, timeline, defaultZoom: 2);

        var cfg = new AppConfig().WithKeepScreensReady(true);
        var wishes = ScreenPlan.Wishes(cfg, remote: false);

        // Trip circuit breaker on initial creation failure
        desks.SetWishes(wishes);
        await desks.IdleAsync();
        Assert.IsType<DeskNotice.CircuitBreakerTripped>(desks.StateOf(ScreenConfig.LegacyPhoneId)?.Notice);

        // SetRemote(true) resets circuit breaker and creates display once healthy
        displays.AlwaysFailCreate = false;
        desks.SetRemote(true);
        await desks.IdleAsync();
        Assert.Equal(PhoneScreenPhase.Ready, desks.StateOf(ScreenConfig.LegacyPhoneId)?.ScreenPhase);

        var winKey = new WindowKey(401, 0x4001);
        var orig = new RectD(60, 60, 800, 600);
        windows.Frames[winKey] = orig;
        desks.Pull(ScreenConfig.LegacyPhoneId, new WindowInfo(winKey, "App", "App.exe", "Win"));
        await desks.IdleAsync();

        // Transition remote off then immediately back on before lane executes EndSessionAsync
        desks.SetRemote(false);
        desks.SetRemote(true);
        await desks.IdleAsync();

        // Cleanup aborted -> placed window remains on the prepared screen
        var state = desks.StateOf(ScreenConfig.LegacyPhoneId)!;
        Assert.Equal(winKey, state.Placed?.Window.Key);
        Assert.False(state.ListVisible);
        Assert.NotEqual(orig, windows.Frames[winKey]);

        await desks.ShutdownAsync();
    }

    [Fact]
    public void CursorHome_Edge_ClampsToNearestVisibleInteriorPixel()
    {
        var main = new RectD(0, 0, 1512, 982);
        var external = new RectD(1512, -200, 1920, 1080);

        // Slid from right edge of main onto hidden virtual screen -> clamp to last interior pixel (1511, 400)
        Assert.Equal(new PointD(1511, 400), CursorHome.Edge(new PointD(1530, 400), [main]));
        // Exact boundary pixel x=1512 is outside half-open main [0..1512) -> clamp to 1511
        Assert.Equal(new PointD(1511, 400), CursorHome.Edge(new PointD(1512, 400), [main]));

        // Multiple visible displays -> picks closest visible rect
        Assert.Equal(new PointD(1600, -200), CursorHome.Edge(new PointD(1600, -300), [main, external]));

        // Already inside a visible rect -> null
        Assert.Null(CursorHome.Edge(new PointD(100, 100), [main]));
        Assert.Null(CursorHome.Edge(new PointD(1511, 981), [main]));

        // Empty or zero-sized visible list -> null
        Assert.Null(CursorHome.Edge(new PointD(3000, 3000), []));
        Assert.Null(CursorHome.Edge(new PointD(3000, 3000), [RectD.Zero]));
    }
}
