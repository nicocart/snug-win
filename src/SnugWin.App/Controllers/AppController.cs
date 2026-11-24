using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using SnugWin.App.Views;
using SnugWin.Core.Config;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Remote;
using SnugWin.Core.Remotes.CrossDesk;
using SnugWin.Core.Session;
using SnugWin.Core.Support;
using SnugWin.Core.Touch;
using SnugWin.Windows.Display;
using SnugWin.Windows.Input;
using SnugWin.Windows.Sensing;
using SnugWin.Windows.Support;
using SnugWin.Windows.Windows;

namespace SnugWin.App.Controllers;

public sealed class AppController : ILogSink
{
    private readonly Dispatcher _dispatcher;
    private readonly SnugPaths _paths;
    private readonly JsonFileStore<AppConfig> _configStore;
    private readonly PhoneDisplayService _displays;
    private readonly WindowService _windows;
    private readonly AppService _apps;
    private readonly TouchHookService _touchHook;
    private readonly Remotes _remotes = new();
    private readonly RemoteSession _session = new();
    private readonly DeskSet _desks;
    private readonly Screenshots _screenshots;
    private readonly DispatcherTimer _tickTimer;
    private readonly List<string> _recentLogs = new();
    private readonly object _logGate = new();

    private AppConfig _config;
    private DateTimeOffset _lastVerifyAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCatalogRefresh = DateTimeOffset.MinValue;
    private IReadOnlyList<AppInfo> _installedApps = Array.Empty<AppInfo>();
    private IReadOnlyList<RunningApp> _runningApps = Array.Empty<RunningApp>();
    private IReadOnlyList<string> _launchOnlyRemotes = Array.Empty<string>();
    private string? _screensUnseenBy;
    private string? _screensWithheldBy;
    private bool _loggedWithheld;
    private RemoteIdentity? _lastHookOwner;
    private bool _lastHookManual;
    private bool _catalogLoaded;
    private bool _reorderRunningApps = true;
    private bool _wasAnyListShowing;
    private readonly Dictionary<string, PhoneHomeWindow> _screenHomeWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HomeBarWindow> _screenHomeBars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PhoneDeskState> _lastSyncedScreenStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RectD> _lastSyncedScreenBounds = new(StringComparer.OrdinalIgnoreCase);
    private PhoneHomeWindow? _previewHomeWindow;
    private ScreenshotOverlayWindow? _screenshotOverlayWindow;
    private CropWindow? _cropWindow;
    private CopySelectionBubbleWindow? _copyBubbleWindow;
    private PhoneReentryWindow? _reentryWindow;
    private bool _reentryPillHidden;
    private CapturedImage? _cropWindowImage;
    private string? _screenshotScreenId;
    private bool _shuttingDown;
    private int _sampling;
    private readonly AppUpdateService _updater = new();
    private UpdateCheckResult? _latestUpdate;
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DisplaySettingsChangeDebounce = TimeSpan.FromSeconds(2);

    private ScreenshotOverlay? _lastSyncedOverlay;
    private bool _lastSyncedScreenshotSetting;
    private string _lastTouchRegionsSig = string.Empty;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private DateTimeOffset _lastDisplayChangeHandled = DateTimeOffset.MinValue;

    public PhoneCatalog Catalog { get; }
    public AppIconCache Icons { get; } = new();
    public AppConfig Config => _config;
    public IReadOnlyList<ScreenConfig> ConfiguredScreens => _config.EffectiveScreens();
    public PhoneDeskState DeskState => _desks.DeskForScreen(PrimaryTouchScreenId)?.State ?? new PhoneDeskState();
    public string PrimaryTouchScreenId => _config.PrimaryTouchScreen()?.Id ?? ScreenConfig.LegacyPhoneId;

    public PhoneDeskState GetScreenState(string? screenId) =>
        _desks.DeskForScreen(screenId ?? PrimaryTouchScreenId)?.State ?? new PhoneDeskState();

    public ScreenConfig? GetScreenConfig(string? screenId)
    {
        string targetId = screenId ?? PrimaryTouchScreenId;
        return _config.EffectiveScreens().FirstOrDefault(s => string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase))
               ?? _config.PrimaryTouchScreen();
    }
    public bool ManualOverride => _remotes.UU.ManualOverride;
    public bool KeepScreensReady => _config.KeepScreensReady;
    public IReadOnlyList<string> LaunchOnlyRemotes => _launchOnlyRemotes;
    public string? ScreensUnseenBy => _screensUnseenBy;
    public string? ScreensWithheldBy => _screensWithheldBy;
    public string RemoteNames => _session.Owner?.DisplayName ?? string.Join("、", _remotes.All.Select(a => a.Identity.DisplayName));
    public IReadOnlyList<string> MainScreenRemotes => _remotes.All.Where(a => a.Identity.OpensOnMainScreen).Select(a => a.Identity.DisplayName).ToList();
    public bool CatalogLoaded => _catalogLoaded;
    public string CurrentVersion => AppUpdateService.CurrentVersion;
    public ReleasePackageKind PackageKind => AppUpdateService.CurrentPackageKind;
    public UpdateCheckResult? LatestUpdate => _latestUpdate;
    public bool IsAutoStartEnabled => StartupTaskService.IsEnabled();
    public string? BundledDriverInstallerScript => LocateBundledDriverScript();
    public bool RequireInjectedInput
    {
        get => _touchHook.RequireInjectedFlag;
        set => _touchHook.RequireInjectedFlag = value;
    }

    public bool IsDriverPipeAvailable => VirtualDisplayIpcClient.IsDriverPipeAvailable();
    public bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public string LogDirectory => _paths.LogDirectory;
    public bool IsPhoneScreenReady => _desks.IsAnyReady;
    public bool CanReenterPhoneScreen => (_session.InEpisode || ManualOverride) && _desks.HasAnyDismissedTouch;
    public AppThemeMode ThemeMode => _config.Theme;
    public bool IsEffectiveDarkTheme => ResolveIsDarkTheme(_config.Theme);
    public IReadOnlyList<RunningApp> RunningApps => _runningApps;
    public IReadOnlyList<AppInfo> InstalledApps => _installedApps;

    public event Action<string>? StateSummaryChanged;
    public event Action? UiStateChanged;
    public event Action<string>? LogAppended;
    public event Action<UpdateCheckResult?>? UpdateStatusChanged;
    public event Action<bool>? ThemeChanged;

    public AppController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _paths = SnugPaths.Default();
        Directory.CreateDirectory(_paths.DataDirectory);
        Directory.CreateDirectory(_paths.LogDirectory);
        Log.SetSink(this);

        _configStore = new JsonFileStore<AppConfig>(_paths.ConfigFile);
        _config = (_configStore.LoadOrQuarantine() ?? new AppConfig()).Sanitize();

        Catalog = LoadPhoneCatalog();
        _config = _config.ResolveCatalog(Catalog);
        _configStore.Save(_config);

        _displays = new PhoneDisplayService(paths: _paths);
        _windows = new WindowService();
        _apps = new AppService(_windows);
        _touchHook = new TouchHookService();
        WindowService.SetExcludedProcesses(_remotes.AllProcessNames);
        _touchHook.SetNormalizers(_remotes.NormalizerFactories);
        _touchHook.SetActiveSource(0);
        RefreshRemoteInstalls();

        _desks = new DeskSet(
            _displays,
            _windows,
            _apps,
            _paths,
            defaultZoom: _config.ZoomSteps);
        _screenshots = new Screenshots(new ScreenshotService());
        _screenshots.Changed += OnScreenshotChanged;

        _desks.StateChanged += OnDesksStateChanged;
        _desks.TopologyChanging += () => _session.NoteSelfInducedChange(DateTimeOffset.Now);
        _desks.Gone += OnDeskGone;
        _touchHook.CloseWindowRequested += handle => _dispatcher.BeginInvoke(() => _desks.ClosePlaced(handle));
        _touchHook.SelectionCompleted += pt => _dispatcher.BeginInvoke(() => ShowCopySelectionBubble(pt));
        _touchHook.SelectionDismissed += () => _dispatcher.BeginInvoke(() => _copyBubbleWindow?.HideBubble());
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplyThemeResources(IsEffectiveDarkTheme);

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tickTimer.Tick += (_, _) => OnTick();
    }

    public async void Start()
    {
        Log.Info($"SnugWin 启动（权限：{(IsElevated ? "管理员" : "普通用户")}，驱动管道：{(IsDriverPipeAvailable ? "就绪" : "未检测到")}）");
        await _displays.CleanupOrphansAsync();
        _ = Task.Run(() =>
        {
            var scanned = AppService.ScanInstalledApps();
            Icons.Preload(scanned);
            _dispatcher.BeginInvoke(() =>
            {
                _installedApps = scanned;
                UiStateChanged?.Invoke();
            });
        });

        // Recover any leftover record files from configured screens before starting session loop
        foreach (var scr in _config.EffectiveScreens())
        {
            string recFile = _paths.RecordFileForScreen(scr.Id);
            if (File.Exists(recFile))
            {
                var recDesk = new PhoneDesk(_displays, _windows, _apps, _paths, screenId: scr.Id, screenName: scr.Name);
                recDesk.Recover();
            }
        }
        _tickTimer.Start();
        NotifySummary();

        _ = Task.Run(StartupTaskService.RefreshPathIfEnabled);
        if (_config.AutoCheckUpdates)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3500).ConfigureAwait(false);
                if (_shuttingDown) return;
                try
                {
                    await CheckForUpdateAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"启动检查更新失败：{ex.Message}");
                }
            });
        }
    }

    private async void OnTick()
    {
        if (_shuttingDown || System.Threading.Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            _screenshots.Expire();
            bool wasConnected = _session.IsConnected;
            var snugBounds = new List<(uint DisplayId, RectD Bounds, DateTimeOffset? OnlineAt)>();
            foreach (var st in _desks.AllStates.Values)
            {
                if (st.DisplayId is { } id && _displays.BoundsOf(id) is { } b)
                    snugBounds.Add((id, b, _displays.OnlineAtOf(id)));
            }

            var (readings, activeMonitors, now) = await Task.Run(() =>
            {
                var monitors = DisplayConfigService.GetActiveMonitors();
                var snugInfos = new List<SnugDisplayInfo>(snugBounds.Count);
                foreach (var (id, bounds, onlineAt) in snugBounds)
                {
                    var match = monitors.FirstOrDefault(m => m.Bounds.Matches(bounds, tolerance: 2.0));
                    snugInfos.Add(new SnugDisplayInfo(id, match?.DeviceName, bounds, onlineAt));
                }

                var sampleNow = DateTimeOffset.Now;
                var list = new List<RemoteReading>(_remotes.All.Count);
                foreach (var adapter in _remotes.All)
                    list.Add(adapter.Sample(sampleNow, wasConnected, snugInfos));
                return (list, monitors, sampleNow);
            });
            if (_shuttingDown) return;

            var transitions = _session.Update(readings, now);
            bool interrupted = transitions.Any(t => t is SessionTransition.Interrupted);
            bool ended = transitions.Any(t => t is SessionTransition.Ended);
            if (ended)
                _touchHook.RemoteEnded();
            if (interrupted || ended)
                BringCursorHome(includePersistentMain: true);

            SyncHookNormalizerSource();

            var snugIds = new HashSet<uint>(snugBounds.Select(s => s.DisplayId));
            NoteUnseenScreens(readings, snugIds);

            bool remote = _session.InEpisode || ManualOverride;
            bool inStartupGrace = now - _startedAt < StartupGracePeriod;
            if (inStartupGrace && !remote)
            {
                SyncTouchRegion();
                NotifySummary();
                return;
            }

            bool seesNewScreens = !(_session.Owner?.SeesScreensOnlyAtLaunch == true && !ManualOverride);
            var rawWishes = ScreenPlan.Wishes(_config, remote, preview: false, seesNewScreens);
            bool wantsTouch = ManualOverride
                              || ControllerClassifier.WantsPhoneScreen(_session.EpisodeControllers(now));
            _desks.SetWishes(ScreenPlan.ForController(rawWishes, wantsTouch, remote));
            _desks.SetRemote(remote);

            UpdateWithheldStatus(remote, seesNewScreens);
            KeepPointerOffPreparedScreens(activeMonitors);

            if ((now - _lastVerifyAt).TotalSeconds >= 2.0)
            {
                _lastVerifyAt = now;
                _desks.VerifyPlacement();
                _desks.CheckReturns();
                _desks.SettleDisplays();
            }

            bool watching = remote || (_previewHomeWindow is { IsVisible: true });
            if (ShouldRefreshRunningApps(now, watching))
            {
                _lastCatalogRefresh = now;
                await RefreshRunningAppsAsync();
            }

            SyncTouchRegion();
            SyncPhoneWindows(forceSyncUi: false);
            SyncReentryWindow();
            NotifySummary();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private void SyncHookNormalizerSource()
    {
        var owner = _session.Owner;
        bool manual = ManualOverride;
        if (Equals(owner, _lastHookOwner) && manual == _lastHookManual)
            return;
        _lastHookOwner = owner;
        _lastHookManual = manual;
        _touchHook.SetActiveSource(_remotes.SourceIndexFor(owner, manual));
    }

    private void NoteUnseenScreens(IReadOnlyList<RemoteReading> readings, IReadOnlySet<uint> snugIds)
    {
        string? next = RemoteReading.IsBlind(readings, _session.Owner, snugIds, out var blindRemote)
            ? blindRemote?.DisplayName
            : null;
        if (string.Equals(next, _screensUnseenBy, StringComparison.Ordinal))
            return;

        if (blindRemote is not null)
        {
            var unseen = readings.First(r => r.Identity.Equals(blindRemote)).Unseen(snugIds).OrderBy(x => x);
            Log.Info($"{blindRemote.LogName} 看不到 Snug 的屏 {string.Join("、", unseen.Select(id => $"#{id}"))}：重启它才选得到");
        }
        else if (_screensUnseenBy is { } gone)
        {
            Log.Info($"{gone} 看得到 Snug 的屏了（或不再需要）");
        }

        _screensUnseenBy = next;
        UiStateChanged?.Invoke();
    }

    private void UpdateWithheldStatus(bool remote, bool seesNewScreens)
    {
        var withheld = ScreenPlan.Withheld(_config, remote, seesNewScreens, preview: false);
        string? next = withheld.Count > 0 ? _session.Owner?.DisplayName : null;
        if (next is not null && !_loggedWithheld && _session.Owner is { } owner)
        {
            _loggedWithheld = true;
            Log.Info($"{owner.LogName} 只在启动时认屏、没开「提前准备虚拟屏」：这次远程不建屏");
        }
        else if (next is null)
        {
            _loggedWithheld = false;
        }

        if (!string.Equals(next, _screensWithheldBy, StringComparison.Ordinal))
        {
            _screensWithheldBy = next;
            UiStateChanged?.Invoke();
        }
    }

    private void KeepPointerOffPreparedScreens(IReadOnlyList<MonitorSnapshot> activeMonitors)
    {
        if (_session.IsConnected || _session.InEpisode || ManualOverride || _previewHomeWindow is { IsVisible: true })
            return;
        if (!_config.KeepScreensReady || !_desks.IsAnyReady)
            return;

        var hiddenBounds = new List<RectD>();
        foreach (var (screenId, st) in _desks.AllStates)
        {
            if (string.Equals(screenId, _config.MainScreen, StringComparison.OrdinalIgnoreCase))
                continue;
            if (st.DisplayId is { } did && _displays.BoundsOf(did) is { Width: > 0, Height: > 0 } b)
                hiddenBounds.Add(b);
        }
        if (hiddenBounds.Count == 0)
            return;

        var visibleBounds = activeMonitors
            .Where(m => !hiddenBounds.Any(hb => m.Bounds.Matches(hb, tolerance: 2.0)))
            .Select(m => m.Bounds)
            .Where(b => b.Width > 0 && b.Height > 0)
            .ToList();
        if (visibleBounds.Count == 0)
            return;

        if (CursorHelper.GetPosition() is { } pos && hiddenBounds.Any(hb => hb.Contains(pos)))
            CursorHelper.KeepOff(visibleBounds);
    }

    private bool ShouldRefreshRunningApps(DateTimeOffset now, bool watching) =>
        watching && (now - _lastCatalogRefresh).TotalSeconds >= 1.5 && _desks.Wishes.Any(w =>
            w.IsTouch
            && _desks.StateOf(w.Id) is { ScreenPhase: PhoneScreenPhase.Ready } dst
            && (dst.ListVisible || dst.Placed is null)
            && dst.Work is null);

    public void RefreshRunningApps() => _ = RefreshRunningAppsAsync();

    private async Task RefreshRunningAppsAsync()
    {
        var fresh = await Task.Run(() =>
        {
            var list = _apps.GetRunningApps();
            Icons.Preload(list);
            return list;
        });
        bool reorder = _reorderRunningApps;
        _reorderRunningApps = false;
        var next = reorder ? fresh : AppList.KeepingOrder(_runningApps, fresh);
        bool changed = !RunningAppsEqual(_runningApps, next);
        _runningApps = next;
        bool loadedChanged = !_catalogLoaded;
        _catalogLoaded = true;
        if (changed || loadedChanged)
            UiStateChanged?.Invoke();
    }

    private static bool RunningAppsEqual(IReadOnlyList<RunningApp> a, IReadOnlyList<RunningApp> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Pid != b[i].Pid || !string.Equals(a[i].App.AppId, b[i].App.AppId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (a[i].Windows.Count != b[i].Windows.Count) return false;
            for (int j = 0; j < a[i].Windows.Count; j++)
            {
                if (a[i].Windows[j] != b[i].Windows[j]) return false;
            }
        }
        return true;
    }

    private void OnDesksStateChanged()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_desks.IsAnyReady)
            {
                _screenshots.Dismiss();
                _screenshotScreenId = null;
                _catalogLoaded = false;
                _reorderRunningApps = true;
                _runningApps = Array.Empty<RunningApp>();
            }
            else if (_desks.Wishes.Any(w => w.IsTouch && _desks.StateOf(w.Id) is { ScreenPhase: PhoneScreenPhase.Ready } dst && (dst.ListVisible || dst.Placed is null)))
            {
                RefreshRunningApps();
            }
            SyncTouchRegion();
            SyncPhoneWindows(forceSyncUi: true);
            NotifySummary();
            UiStateChanged?.Invoke();
        });
    }

    private void OnScreenshotChanged(ScreenshotOverlay? overlay)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (overlay is null) _screenshotScreenId = null;
            SyncTouchRegion();
            SyncPhoneWindows(forceSyncUi: false);
            UiStateChanged?.Invoke();
        });
    }

    private void OnDeskGone(DeskGone gone)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (gone is DeskGone.App appGone)
                _runningApps = _runningApps.Where(a => a.Pid != appGone.Pid).ToList();
            else if (gone is DeskGone.Window winGone)
                _runningApps = _runningApps
                    .Select(a => a with { Windows = a.Windows.Where(w => w.Key != winGone.Key).ToList() })
                    .Where(a => a.Windows.Count > 0)
                    .ToList();
            UiStateChanged?.Invoke();
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now;
        if (now - _lastDisplayChangeHandled < DisplaySettingsChangeDebounce) return;
        _lastDisplayChangeHandled = now;
        _dispatcher.BeginInvoke(() => _desks.DisplaysChanged());
    }

    private void SyncTouchRegion()
    {
        bool allowGestures = _config.Gestures;
        bool anyWindowPlaced = _desks.Wishes.Any(w =>
            w.IsTouch
            && _desks.StateOf(w.Id) is { ScreenPhase: PhoneScreenPhase.Ready, ListVisible: false });

        _touchHook.RequireInjectedFlag = _config.GesturesInjectedOnly && !ManualOverride;
        _touchHook.Options = new TouchOptions(
            AllowScroll: _config.GestureScroll,
            AllowRightClick: _config.GestureLongPressRightClick,
            NotifySelectionEnded: _config.CopyAfterSelection && anyWindowPlaced);

        if (!allowGestures)
        {
            _lastTouchRegionsSig = string.Empty;
            _touchHook.SetRegions([]);
            return;
        }

        string? croppingScreenId = _screenshots.Overlay?.IsCropping == true ? _screenshotScreenId : null;
        var regions = new List<TouchRegion>();
        var sigParts = new List<string>();
        foreach (var wish in _desks.Wishes)
        {
            if (!wish.IsTouch) continue;
            if (croppingScreenId is not null && string.Equals(wish.Id, croppingScreenId, StringComparison.OrdinalIgnoreCase))
                continue;
            var st = _desks.StateOf(wish.Id);
            if (st is not { ScreenPhase: PhoneScreenPhase.Ready, DisplayId: { } id }) continue;
            if (_displays.BoundsOf(id) is not { } bounds) continue;
            var closeBtn = st.ListVisible ? null : st.CloseButton;
            regions.Add(TouchRegion.ForPhoneScreen(bounds, closeBtn));
            sigParts.Add($"{wish.Id}:{bounds.Short}");
        }

        string sig = string.Join(";", sigParts);
        if (sig != _lastTouchRegionsSig)
        {
            _lastTouchRegionsSig = sig;
            if (regions.Count > 0)
                Log.Info($"触控手势区域更新：{string.Join(" | ", sigParts)}");
        }
        _touchHook.SetRegions(regions);
    }

    private void ShowCopySelectionBubble(PointD selectionEnd)
    {
        if (!_config.CopyAfterSelection) return;
        foreach (var wish in _desks.Wishes)
        {
            if (!wish.IsTouch) continue;
            var st = _desks.StateOf(wish.Id);
            if (st is not { ScreenPhase: PhoneScreenPhase.Ready, ListVisible: false, DisplayId: { } id })
                continue;
            if (_displays.BoundsOf(id) is not { } bounds || !bounds.Contains(selectionEnd))
                continue;

            _copyBubbleWindow ??= new CopySelectionBubbleWindow(this);
            int logicalW = _displays.WidthOf(id) ?? ScreenFit.DesignWidth;
            double scale = ScreenFit.Scale(wish.Fit.Size(logicalW));
            double dpiRatio = Math.Max(1.0, bounds.Width / Math.Max(1.0, logicalW));
            _copyBubbleWindow.ShowAtSelection(selectionEnd, bounds, scale, dpiRatio);
            _touchHook.SetSelectionBubbleHandle(_copyBubbleWindow.Handle);
            return;
        }
    }

    public Task CopySelectionAtAsync(PointD point) =>
        PerformSelectionActionAsync(point, SelectionBubbleAction.Copy);

    public async Task PerformSelectionActionAsync(PointD point, SelectionBubbleAction action)
    {
        await _windows.PerformSelectionActionAsync(point, action);
    }

    public void SyncSubPageActivation(string? screenId, bool focus)
    {
        string targetId = screenId ?? PrimaryTouchScreenId;
        if (!_screenHomeWindows.TryGetValue(targetId, out var win)) return;
        var st = _desks.StateOf(targetId);
        if (st is not { ScreenPhase: PhoneScreenPhase.Ready, DisplayId: { } id } || _displays.BoundsOf(id) is not { } bounds)
            return;
        win.ShowOnDisplay(bounds, focusOnShow: focus);
    }

    private void SyncPhoneWindows(bool forceSyncUi)
    {
        SyncReentryWindow();
        var overlay = _screenshots.Overlay;
        var activeTouchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool anyListShowing = false;
        bool anyChanged = forceSyncUi
                          || !Equals(overlay, _lastSyncedOverlay)
                          || _config.Screenshot != _lastSyncedScreenshotSetting;

        foreach (var wish in _desks.Wishes)
        {
            if (!wish.IsTouch) continue;
            var st = _desks.StateOf(wish.Id);
            if (st is not { ScreenPhase: PhoneScreenPhase.Ready, DisplayId: { } id } || _displays.BoundsOf(id) is not { } bounds)
                continue;

            activeTouchIds.Add(wish.Id);
            bool stateChanged = anyChanged
                                || !_lastSyncedScreenStates.TryGetValue(wish.Id, out var prevSt) || !Equals(st, prevSt)
                                || !_lastSyncedScreenBounds.TryGetValue(wish.Id, out var prevB) || !Equals(bounds, prevB);

            bool showList = st.ListVisible || st.Placed is null;
            if (showList) anyListShowing = true;
            if (!stateChanged) continue;

            _lastSyncedScreenStates[wish.Id] = st;
            _lastSyncedScreenBounds[wish.Id] = bounds;

            if (showList)
            {
                if (_screenHomeBars.TryGetValue(wish.Id, out var hb)) hb.Hide();
                if (!_screenHomeWindows.TryGetValue(wish.Id, out var homeWin) || !homeWin.IsLoaded)
                {
                    homeWin = new PhoneHomeWindow(this, previewMode: false, screenId: wish.Id, kind: wish.Device.Kind);
                    _screenHomeWindows[wish.Id] = homeWin;
                }
                else
                {
                    homeWin.ConfigureTouchScreen(wish.Id, wish.Device.Kind);
                }
                homeWin.ShowOnDisplay(bounds, focusOnShow: false);
                homeWin.SyncState();
            }
            else
            {
                if (_screenHomeWindows.TryGetValue(wish.Id, out var homeWin))
                {
                    homeWin.CloseSubPage();
                    homeWin.Hide();
                }
                bool croppingThisScreen = overlay?.IsCropping == true
                    && string.Equals(_screenshotScreenId, wish.Id, StringComparison.OrdinalIgnoreCase);
                if (croppingThisScreen)
                {
                    if (_screenHomeBars.TryGetValue(wish.Id, out var hb)) hb.Hide();
                }
                else if (_displays.HomeBarAreaOf(id) is { } barArea)
                {
                    if (!_screenHomeBars.TryGetValue(wish.Id, out var hb) || !hb.IsLoaded)
                    {
                        hb = new HomeBarWindow(this, wish.Id);
                        _screenHomeBars[wish.Id] = hb;
                    }
                    int logicalW = _displays.WidthOf(id) ?? ScreenFit.DesignWidth;
                    double scale = ScreenFit.Scale(wish.Fit.Size(logicalW));
                    hb.ShowAtBounds(barArea, scale, _config.Screenshot);
                }
            }
        }

        // Clean up windows for inactive/retired/disabled touch screens
        foreach (string id in _screenHomeWindows.Keys.Where(k => !activeTouchIds.Contains(k)).ToList())
        {
            if (_screenHomeWindows.TryGetValue(id, out var hw))
            {
                hw.CloseSubPage();
                hw.Hide();
            }
            if (_screenHomeBars.TryGetValue(id, out var hb))
                hb.Hide();
            _lastSyncedScreenStates.Remove(id);
            _lastSyncedScreenBounds.Remove(id);
        }

        if (activeTouchIds.Count > 0 && _previewHomeWindow is { IsLoaded: true })
        {
            _previewHomeWindow.Close();
            _previewHomeWindow = null;
        }

        if (anyListShowing != _wasAnyListShowing)
        {
            _wasAnyListShowing = anyListShowing;
            if (anyListShowing) _reorderRunningApps = true;
        }

        _lastSyncedOverlay = overlay;
        _lastSyncedScreenshotSetting = _config.Screenshot;
        SyncScreenshotWindows(overlay);
    }

    private void SyncScreenshotWindows(ScreenshotOverlay? overlay)
    {
        if (overlay is null)
        {
            HideAndDisposeScreenshotWindows();
            return;
        }

        string? targetId = _screenshotScreenId ?? _desks.Wishes.FirstOrDefault(w => w.IsTouch)?.Id;
        if (targetId is null || _desks.StateOf(targetId) is not { ScreenPhase: PhoneScreenPhase.Ready, DisplayId: { } displayId, ListVisible: false }
            || _displays.BoundsOf(displayId) is not { } bounds)
        {
            HideAndDisposeScreenshotWindows();
            return;
        }

        var wish = _desks.Wishes.FirstOrDefault(w => string.Equals(w.Id, targetId, StringComparison.OrdinalIgnoreCase));
        int logicalW = _displays.WidthOf(displayId) ?? ScreenFit.DesignWidth;
        var logicalSize = wish?.Fit.Size(logicalW) ?? new SizeD(logicalW, logicalW);
        double scale = ScreenFit.Scale(logicalSize);
        double dpiRatio = Math.Max(1.0, bounds.Width / Math.Max(1.0, logicalW));

        if (overlay.IsCropping)
        {
            _screenshotOverlayWindow?.Close();
            _screenshotOverlayWindow = null;
        }
        else if (_displays.HomeBarAreaOf(displayId) is { } barArea)
        {
            _screenshotOverlayWindow ??= new ScreenshotOverlayWindow(this);
            _screenshotOverlayWindow.UpdateAndShow(overlay, barArea, scale, dpiRatio);
        }

        if (overlay is ScreenshotOverlay.Cropping crop)
        {
            if (_cropWindow is null || !Equals(_cropWindowImage, crop.Image))
            {
                _cropWindow?.Close();
                _cropWindowImage = crop.Image;
                _cropWindow = new CropWindow(this, crop.Image);
            }
            _cropWindow.ShowOnDisplay(bounds);
        }
        else
        {
            _cropWindow?.Close();
            _cropWindow = null;
            _cropWindowImage = null;
        }
    }

    private void HideAndDisposeScreenshotWindows()
    {
        _copyBubbleWindow?.HideBubble();
        _screenshotOverlayWindow?.Close();
        _screenshotOverlayWindow = null;
        _cropWindow?.Close();
        _cropWindow = null;
        _cropWindowImage = null;
    }

    public void SetPhoneScreenEnabled(bool enabled)
    {
        SetScreenEnabled(PrimaryTouchScreenId, enabled);
    }

    public void SetGesturesEnabled(bool enabled)
    {
        _config = _config with { Gestures = enabled };
        _configStore.Save(_config);
        SyncTouchRegion();
        UiStateChanged?.Invoke();
    }

    public void SetGestureScrollEnabled(bool enabled)
    {
        _config = _config with { GestureScroll = enabled };
        _configStore.Save(_config);
        SyncTouchRegion();
        UiStateChanged?.Invoke();
    }

    public void SetGestureLongPressRightClickEnabled(bool enabled)
    {
        _config = _config with { GestureLongPressRightClick = enabled };
        _configStore.Save(_config);
        SyncTouchRegion();
        UiStateChanged?.Invoke();
    }

    public void SetCopyAfterSelectionEnabled(bool enabled)
    {
        _config = _config with { CopyAfterSelection = enabled };
        _configStore.Save(_config);
        if (!enabled) _copyBubbleWindow?.HideBubble();
        SyncTouchRegion();
        UiStateChanged?.Invoke();
    }

    public void SetGesturesInjectedOnlyEnabled(bool enabled)
    {
        _config = _config with { GesturesInjectedOnly = enabled };
        _configStore.Save(_config);
        SyncTouchRegion();
        UiStateChanged?.Invoke();
    }

    public void SetScreenshotEnabled(bool enabled)
    {
        _config = _config with { Screenshot = enabled };
        _configStore.Save(_config);
        if (!enabled) _screenshots.Dismiss();
        SyncPhoneWindows(forceSyncUi: true);
        UiStateChanged?.Invoke();
    }

    public void SetAutoCheckUpdatesEnabled(bool enabled)
    {
        _config = _config with { AutoCheckUpdates = enabled };
        _configStore.Save(_config);
        UiStateChanged?.Invoke();
    }

    public void SetAutoStartEnabled(bool enabled)
    {
        StartupTaskService.SetEnabled(enabled);
        Log.Info(enabled ? "已开启开机静默自启动（任务计划）" : "已关闭开机静默自启动");
        UiStateChanged?.Invoke();
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var result = await _updater.CheckForUpdateAsync(ct).ConfigureAwait(false);
        _latestUpdate = result;
        _ = _dispatcher.BeginInvoke(() =>
        {
            UpdateStatusChanged?.Invoke(result);
            UiStateChanged?.Invoke();
        });
        return result;
    }

    public Task<PreparedUpdate> DownloadAndPrepareUpdateAsync(
        UpdateCheckResult check,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        _updater.DownloadAndPrepareAsync(check, progress, ct);

    public async Task ApplyPreparedUpdateAndRestartAsync(PreparedUpdate update)
    {
        bool minimized = Array.Exists(
            Environment.GetCommandLineArgs(),
            a => a.Equals(StartupTaskDefinition.MinimizedArg, StringComparison.OrdinalIgnoreCase));
        await ShutdownAsync().ConfigureAwait(false);
        AppUpdateService.LaunchApplyScript(update, minimized);
        _ = _dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }

    public async Task<(bool Ready, string Message)> InstallDriverAsync()
    {
        string? script = BundledDriverInstallerScript;
        if (string.IsNullOrEmpty(script))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/MolotovCherry/virtual-display-rs/releases/tag/v0.3.1",
                UseShellExecute = true
            });
            return (false, "未找到随包附带的驱动脚本，已打开官方下载页面。");
        }

        Log.Info($"开始安装随包附带的 virtual-display-rs 驱动：{script}");
        try
        {
            var psi = IsElevated
                ? new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Action Install",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
                : new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Action Install",
                    UseShellExecute = true,
                    Verb = "runas"
                };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "无法启动 PowerShell 驱动安装进程。");

            if (psi.RedirectStandardOutput)
            {
                string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);

                foreach (string rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    Log.Info($"[DriverInstaller] {rawLine.Trim()}");
                foreach (string rawLine in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    Log.Warn($"[DriverInstaller] {rawLine.Trim()}");
            }
            else
            {
                await proc.WaitForExitAsync().ConfigureAwait(false);
            }

            if (proc.ExitCode != 0)
            {
                string failMsg = $"驱动安装脚本退出码 {proc.ExitCode}，请查看「诊断与日志」详情。";
                Log.Warn(failMsg);
                _ = _dispatcher.BeginInvoke(() =>
                {
                    NotifySummary();
                    UiStateChanged?.Invoke();
                });
                return (false, failMsg);
            }

            for (int i = 0; i < 10 && !IsDriverPipeAvailable; i++)
                await Task.Delay(300).ConfigureAwait(false);

            bool ready = IsDriverPipeAvailable;
            Log.Info(ready
                ? "驱动安装完成，虚拟显示器命名管道已就绪。"
                : "驱动安装已结束，但命名管道暂未就绪（可能需要重启设备或电脑）。");

            _ = _dispatcher.BeginInvoke(() =>
            {
                NotifySummary();
                UiStateChanged?.Invoke();
            });

            return ready
                ? (true, "virtual-display-rs 驱动安装成功并已就绪。")
                : (false, "驱动已安装但命名管道暂未就绪，请尝试重启「Cherry Virtual Display」设备或重启电脑。");
        }
        catch (Exception ex)
        {
            Log.Error($"驱动安装异常：{ex.Message}");
            return (false, $"启动驱动安装失败：{ex.Message}");
        }
    }

    public void LaunchDriverInstaller() => _ = InstallDriverAsync();

    private static string? LocateBundledDriverScript()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "driver", "Install-SnugDriver.ps1");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public void SetDefaultZoomSteps(int steps)
    {
        _config = _config with { ZoomSteps = steps.Clamp(AppConfig.MinZoomSteps, AppConfig.MaxZoomSteps) };
        _configStore.Save(_config);
        _desks.SetDefaultZoom(_config.ZoomSteps);
        UiStateChanged?.Invoke();
    }

    public void SetThemeMode(AppThemeMode mode)
    {
        var sanitized = Enum.IsDefined(mode) ? mode : AppThemeMode.Light;
        _config = _config with { Theme = sanitized };
        _configStore.Save(_config);
        bool isDark = ResolveIsDarkTheme(sanitized);
        ApplyThemeResources(isDark);
        ThemeChanged?.Invoke(isDark);
        UiStateChanged?.Invoke();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_config.Theme != AppThemeMode.Auto) return;
        _ = _dispatcher.BeginInvoke(() =>
        {
            bool isDark = ResolveIsDarkTheme(AppThemeMode.Auto);
            ApplyThemeResources(isDark);
            ThemeChanged?.Invoke(isDark);
            UiStateChanged?.Invoke();
        });
    }

    public static bool ResolveIsDarkTheme(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => IsSystemAppsDarkTheme()
    };

    private static bool IsSystemAppsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
                return val == 0;
        }
        catch
        {
        }
        return false;
    }

    public static void ApplyThemeResources(bool isDark)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        var uri = new Uri(
            isDark
                ? "pack://application:,,,/SnugWin.App;component/Themes/ThemeDark.xaml"
                : "pack://application:,,,/SnugWin.App;component/Themes/ThemeLight.xaml",
            UriKind.Absolute);
        var dict = new System.Windows.ResourceDictionary { Source = uri };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0)
            merged[0] = dict;
        else
            merged.Insert(0, dict);
    }

    public void SelectPhone(PhoneChoice choice) =>
        SelectScreenDevice(PrimaryTouchScreenId, choice);

    public void SelectScreenDevice(string screenId, PhoneChoice choice)
    {
        var resolved = Catalog.Resolve(choice);
        _config = _config.WithScreenUpdated(screenId, s =>
        {
            var nextOrientation = resolved.Kind == DeviceKind.Computer
                ? SnugWin.Core.PhoneScreen.ScreenOrientation.Landscape
                : s.Orientation;
            return s with { Device = resolved, Orientation = nextOrientation };
        });
        _configStore.Save(_config);
        OnTick();
        if (_previewHomeWindow is { IsLoaded: true })
        {
            var target = _config.EffectiveScreens().FirstOrDefault(s => string.Equals(s.Id, screenId, StringComparison.OrdinalIgnoreCase))
                         ?? _config.PrimaryTouchScreen();
            if (target is not null && target.Device.Kind.IsTouch())
            {
                var wa = System.Windows.SystemParameters.WorkArea;
                _previewHomeWindow.ApplyPreviewLayout(
                    new SizeD(wa.Width, wa.Height),
                    ScreenPlan.FitOf(target),
                    target.Device);
            }
        }
        UiStateChanged?.Invoke();
    }

    public void SetScreenEnabled(string screenId, bool enabled)
    {
        _config = _config.WithScreenUpdated(screenId, s => s with { Enabled = enabled });
        _configStore.Save(_config);
        OnTick();
        UiStateChanged?.Invoke();
    }

    public bool AddScreen(SnugWin.Core.PhoneScreen.ScreenOrientation orientation, PhoneChoice device, string? name = null)
    {
        if (ConfiguredScreens.Count >= ScreenConfig.Limit) return false;
        var resolved = Catalog.Resolve(device);
        _config = _config.WithScreenAdded(orientation, resolved, name);
        _configStore.Save(_config);
        OnTick();
        UiStateChanged?.Invoke();
        return true;
    }

    public void RemoveScreen(string screenId)
    {
        _config = _config.WithScreenRemoved(screenId);
        _configStore.Save(_config);
        OnTick();
        UiStateChanged?.Invoke();
    }

    public bool IsInReconnectGrace => _session.IsInReconnectGrace;
    public string? MainScreenId => _config.MainScreen;

    public void EndSessionNow()
    {
        if (_session.EndNow() is null) return;
        _touchHook.RemoteEnded();
        SyncHookNormalizerSource();
        _screensWithheldBy = null;
        _loggedWithheld = false;
        BringCursorHome(includePersistentMain: true);
        bool remote = _session.InEpisode || ManualOverride;
        _desks.SetWishes(ScreenPlan.ForController(ScreenPlan.Wishes(_config, remote), ManualOverride, remote));
        _desks.SetRemote(_session.InEpisode);
        NotifySummary();
        UiStateChanged?.Invoke();
    }

    public async Task<(bool Ok, string Message)> RestartUnseenRemoteAsync()
    {
        string? target = _screensUnseenBy;
        if (string.IsNullOrWhiteSpace(target))
            return (false, "当前无需重启远控。");

        if (!string.Equals(target, CrossDeskRemote.Identity.DisplayName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target, CrossDeskRemote.Identity.LogName, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"暂不支持自动重启 {target}，请手动重启。");
        }

        Log.Info($"正在一键重启 {target} 以重新识别 Snug 虚拟屏...");
        var result = await Task.Run(() => CrossDeskHost.RestartHostAsync()).ConfigureAwait(false);
        if (result.Ok)
        {
            Log.Info(result.Message);
            _ = _dispatcher.BeginInvoke(() =>
            {
                OnTick();
                NotifySummary();
                UiStateChanged?.Invoke();
            });
        }
        else
        {
            Log.Warn(result.Message);
        }
        return result;
    }

    public void SetKeepScreensReady(bool on)
    {
        _config = _config.WithKeepScreensReady(on);
        _configStore.Save(_config);
        Log.Info($"提前准备虚拟屏 → {(on ? "开" : "关")}");
        OnTick();
        UiStateChanged?.Invoke();
    }

    public void RefreshRemoteInstalls()
    {
        var next = _remotes.All
            .Where(a => a.Identity.SeesScreensOnlyAtLaunch && a.IsInstalled())
            .Select(a => a.Identity.DisplayName)
            .ToList();
        if (!next.SequenceEqual(_launchOnlyRemotes, StringComparer.Ordinal))
        {
            _launchOnlyRemotes = next;
            UiStateChanged?.Invoke();
        }
    }

    public void SetMainScreen(string? screenId)
    {
        _config = _config.WithMainScreen(screenId);
        _configStore.Save(_config);
        OnTick();
        UiStateChanged?.Invoke();
    }

    private void BringCursorHome(bool includePersistentMain)
    {
        try
        {
            var kept = new List<RectD>();
            var main = _displays.MainArea() ?? default;
            if (main.Width > 0 && main.Height > 0)
                kept.Add(main);

            foreach (var other in _displays.OtherDisplays())
            {
                var b = _displays.BoundsOf(other.DisplayId);
                if (b is { Width: > 0, Height: > 0 })
                    kept.Add(b.Value);
            }

            if (includePersistentMain && !string.IsNullOrWhiteSpace(_config.MainScreen))
            {
                var st = _desks.StateOf(_config.MainScreen);
                if (st?.DisplayId is { } mainVirtId && _displays.BoundsOf(mainVirtId) is { Width: > 0, Height: > 0 } mb)
                    kept.Add(mb);
            }

            if (CursorHelper.BringHomeIfOutside(kept, main))
                Log.Info("已将鼠标指针带回保留显示器中央");
        }
        catch (Exception ex)
        {
            Log.Warn($"归位鼠标指针失败：{ex.Message}");
        }
    }

    public void RenameScreen(string screenId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _config = _config.WithScreenUpdated(screenId, s => s with { Name = name.Trim() });
        _configStore.Save(_config);
        OnTick();
        UiStateChanged?.Invoke();
    }

    public void OpenPhonePreview() => OpenScreenPreview(null);

    public void OpenScreenPreview(string? screenId)
    {
        string targetId = screenId ?? PrimaryTouchScreenId;
        var targetScreen = _config.EffectiveScreens().FirstOrDefault(s => string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase))
                           ?? _config.PrimaryTouchScreen();
        if (targetScreen is null) return;

        if (_desks.StateOf(targetScreen.Id)?.ScreenPhase == PhoneScreenPhase.Ready)
        {
            _desks.ShowList(targetScreen.Id);
            _ = RefreshRunningAppsAsync();
            return;
        }

        _ = RefreshRunningAppsAsync();
        if (_previewHomeWindow is null || !_previewHomeWindow.IsLoaded)
        {
            _previewHomeWindow = new PhoneHomeWindow(this, previewMode: true, screenId: targetScreen.Id, kind: targetScreen.Device.Kind);
            _previewHomeWindow.Closed += (_, _) => _previewHomeWindow = null;
        }
        else
        {
            _previewHomeWindow.ConfigureTouchScreen(targetScreen.Id, targetScreen.Device.Kind);
        }

        var wa = System.Windows.SystemParameters.WorkArea;
        _previewHomeWindow.ApplyPreviewLayout(
            new SizeD(wa.Width, wa.Height),
            ScreenPlan.FitOf(targetScreen),
            targetScreen.Device);
        _previewHomeWindow.SyncState();
        if (!_previewHomeWindow.IsVisible)
            _previewHomeWindow.Show();
        if (_previewHomeWindow.WindowState == System.Windows.WindowState.Minimized)
            _previewHomeWindow.WindowState = System.Windows.WindowState.Normal;
        _previewHomeWindow.Activate();
    }

    public void SetManualOverride(bool on)
    {
        _remotes.UU.ManualOverride = on;
        if (on) _desks.ResetCircuitBreaker();
        OnTick();
        UiStateChanged?.Invoke();
    }

    public void PullWindow(WindowInfo w, string? screenId = null) =>
        _desks.Pull(screenId ?? PrimaryTouchScreenId, w);

    public void OpenApp(AppInfo app, string? screenId = null)
    {
        string target = screenId ?? PrimaryTouchScreenId;
        switch (AppList.ActionFor(app, _runningApps))
        {
            case AppAction.Pull p:
                _desks.Pull(target, p.Window);
                break;
            case AppAction.Open o:
                _desks.Open(target, o.App);
                break;
        }
    }

    public void NewWindow(RunningApp app, string? screenId = null) =>
        _desks.NewWindow(screenId ?? PrimaryTouchScreenId, app);

    public void QuitApp(RunningApp app, string? screenId = null) =>
        _desks.Quit(screenId ?? PrimaryTouchScreenId, app);

    public void ForceQuitApp(int pid, string appName, string? screenId = null) =>
        _desks.ForceQuit(screenId ?? PrimaryTouchScreenId, pid, appName);

    public void CloseWindow(WindowInfo w, string? screenId = null) =>
        _desks.Close(screenId ?? PrimaryTouchScreenId, w);

    public void ShowList(string? screenId = null) =>
        _desks.ShowList(screenId ?? PrimaryTouchScreenId);

    public void ReturnToPlaced(string? screenId = null) =>
        _desks.ReturnToPlaced(screenId ?? PrimaryTouchScreenId);

    public void PutBack(string? screenId = null) =>
        _desks.PutBack(screenId ?? PrimaryTouchScreenId);

    public void ZoomBy(int delta, string? screenId = null) =>
        _desks.Zoom(screenId ?? PrimaryTouchScreenId, delta);

    public void DismissNotice(string? screenId = null) =>
        _desks.DismissNotice(screenId ?? PrimaryTouchScreenId);

    public void DismissScreen(string? screenId = null) =>
        _desks.DismissScreen(screenId ?? PrimaryTouchScreenId);

    public void ReenterScreen()
    {
        _reentryPillHidden = false;
        _reentryWindow?.Hide();
        _desks.ReenterAllDismissedTouch();
        NotifySummary();
        UiStateChanged?.Invoke();
    }

    public void HideReentryPill()
    {
        _reentryPillHidden = true;
        _reentryWindow?.Hide();
    }

    private void SyncReentryWindow()
    {
        if (!CanReenterPhoneScreen)
        {
            _reentryPillHidden = false;
            _reentryWindow?.Hide();
            return;
        }
        if (_reentryPillHidden || _shuttingDown)
        {
            _reentryWindow?.Hide();
            return;
        }
        if (_displays.MainArea() is not { } mainArea)
            return;

        _reentryWindow ??= new PhoneReentryWindow(this);
        _reentryWindow.ShowAtBottomRight(mainArea);
    }

    public void TakeScreenshot(string? screenId = null)
    {
        string target = screenId ?? PrimaryTouchScreenId;
        if (_desks.StateOf(target)?.Placed is not { } placed) return;
        _screenshotScreenId = target;
        _ = _screenshots.TakeAsync(placed.Window);
    }

    public void OpenCrop() => _screenshots.OpenCrop();
    public void CancelCrop() => _screenshots.CancelCrop();
    public void FinishCrop(RectD selection) => _screenshots.FinishCrop(selection);

    public IReadOnlyList<string> GetRecentLogs()
    {
        lock (_logGate) return _recentLogs.ToList();
    }

    public string GetStatusSummary()
    {
        if (!IsDriverPipeAvailable) return "未检测到虚拟屏驱动";
        if (_screensUnseenBy is { } unseenRemote) return $"重启 {unseenRemote} 以识别新屏幕";
        if (_screensWithheldBy is { } withheldRemote) return $"请开启「提前准备虚拟屏」并重启 {withheldRemote}";
        var readyCount = _desks.Wishes.Count(w => _desks.StateOf(w.Id)?.ScreenPhase == PhoneScreenPhase.Ready);
        if (readyCount > 0)
        {
            if (readyCount == 1)
                return _session.IsConnected ? "远程中（虚拟屏已就绪）" : "虚拟屏就绪";
            return _session.IsConnected ? $"远程中（{readyCount} 块屏就绪）" : $"{readyCount} 块屏就绪";
        }
        if (_desks.Wishes.Any(w => _desks.StateOf(w.Id)?.ScreenPhase == PhoneScreenPhase.Preparing))
            return "正在创建虚拟屏...";
        if (_desks.Wishes.Any(w => _desks.StateOf(w.Id)?.ScreenPhase == PhoneScreenPhase.Failed))
            return "建屏失败（重试中）";
        int suspendedCount = _desks.Wishes.Count(w => _desks.StateOf(w.Id)?.ScreenPhase == PhoneScreenPhase.Suspended);
        if (suspendedCount > 0)
            return $"远程中（{suspendedCount} 块屏等待显示布局恢复）";
        if (_desks.HasAnyDismissedTouch && _session.InEpisode)
            return "远程中（已退出触屏）";
        return _session.IsConnected ? "远程中" : "待机中";
    }

    private void NotifySummary() => StateSummaryChanged?.Invoke(GetStatusSummary());

    private static PhoneCatalog LoadPhoneCatalog()
    {
        string primary = Path.Combine(AppContext.BaseDirectory, "Assets", "device-models.json");
        string legacy = Path.Combine(AppContext.BaseDirectory, "Assets", "phone-models.json");
        string? path = File.Exists(primary) ? primary : (File.Exists(legacy) ? legacy : null);
        if (path is null) return PhoneCatalog.Empty;
        try
        {
            return PhoneCatalog.LoadFromFile(path);
        }
        catch (Exception ex)
        {
            Log.Error($"加载机型库失败：{ex.Message}");
            return PhoneCatalog.Empty;
        }
    }

    public void Write(LogLevel level, string message, DateTimeOffset timestamp)
    {
        string line = $"[{timestamp:HH:mm:ss}] [{level}] {message}";
        lock (_logGate)
        {
            _recentLogs.Add(line);
            if (_recentLogs.Count > 300)
                _recentLogs.RemoveRange(0, _recentLogs.Count - 300);
            try
            {
                string logFile = Path.Combine(_paths.LogDirectory, $"snug-{timestamp:yyyyMMdd}.log");
                File.AppendAllText(logFile, line + Environment.NewLine);
            }
            catch { }
        }
        LogAppended?.Invoke(line);
    }

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _tickTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _touchHook.Dispose();
        await _desks.ShutdownAsync();
        await _displays.CleanupOrphansAsync();
        await _displays.DisposeAsync();
        HideAndDisposeScreenshotWindows();
        _reentryWindow?.Close();
        _copyBubbleWindow?.Close();
        _previewHomeWindow?.Close();
        foreach (var hw in _screenHomeWindows.Values)
            hw.Close();
        _screenHomeWindows.Clear();
        foreach (var hb in _screenHomeBars.Values)
            hb.Close();
        _screenHomeBars.Clear();
    }
}
