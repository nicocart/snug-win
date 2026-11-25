using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Persistence;
using SnugWin.Core.Support;
using SnugWin.Core.Touch;

namespace SnugWin.Core.PhoneScreen;

public enum PhoneScreenPhase
{
    Off,
    Preparing,
    Ready,
    Suspended,
    Failed
}

public enum DeskWorkKind
{
    Opening,
    Placing,
    PuttingBack,
    Zooming,
    Reshaping,
    Dismissing
}

public sealed record DeskWork(DeskWorkKind Kind, string? Label = null);

public abstract record DeskNotice
{
    public sealed record WindowGone(string App) : DeskNotice;
    public sealed record WindowUnresponsive(string App) : DeskNotice;
    public sealed record RecordUnwritable : DeskNotice;
    public sealed record ScreenNotReady : DeskNotice;
    public sealed record OpenFailed(string App) : DeskNotice;
    public sealed record NoNewWindow(string App) : DeskNotice;
    public sealed record NoWindow(string App) : DeskNotice;
    public sealed record QuitStalled(string App, int Pid) : DeskNotice;
    public sealed record Reshaped : DeskNotice;
    public sealed record ReshapeFailed : DeskNotice;
    public sealed record ZoomLeftBehind(string App, int Steps) : DeskNotice;
    public sealed record CircuitBreakerTripped : DeskNotice;
    public sealed record KeptOnDismiss : DeskNotice;
}

public abstract record DeskGone
{
    public sealed record App(int Pid) : DeskGone;
    public sealed record Window(WindowKey Key) : DeskGone;
}

public sealed record PlacedWindow(WindowInfo Window, int Zoom)
{
    public bool Zoomable => ZoomBook.IsZoomable(Window.AppId);
}

public sealed record PhoneDeskState
{
    public PhoneScreenPhase ScreenPhase { get; init; } = PhoneScreenPhase.Off;
    public uint? DisplayId { get; init; }
    public PlacedWindow? Placed { get; init; }
    public bool ListVisible { get; init; } = true;
    public DeskWork? Work { get; init; }
    public DeskNotice? Notice { get; init; }
    public IReadOnlySet<int> Quitting { get; init; } = new HashSet<int>();
    public IReadOnlySet<WindowKey> Closing { get; init; } = new HashSet<WindowKey>();
    public IReadOnlySet<WindowKey> Brought { get; init; } = new HashSet<WindowKey>();
    public bool Dismissed { get; init; }
    public bool StaysOnDismiss { get; init; }
    public CloseButton? CloseButton { get; init; }
}

public sealed class PhoneDesk
{
    private const double RecheckDelay = 1.5;
    private const double TopologySettle = 1.0;
    private const double SettleDelay = 0.3;
    private static readonly double[] RetryDelays = [5, 15, 30];
    private const double WindowPoll = 0.1;
    private const double LaunchWait = 30.0;
    private const double WindowWait = 10.0;
    private const double WatchPoll = 0.25;
    private const double QuitWait = 10.0;
    private const double CloseWait = 5.0;
    private const double AnswerGrace = 2.0;
    private const double StalledFollow = 60.0;
    private const double StalledPoll = 1.0;
    private const double ReplaceWait = 10.0;
    private const double DisplayRecoveryDebounce = 1.0;
    private static readonly double[] DisplayRecoveryDelays = [5.0, 15.0, 30.0];

    private readonly IPhoneDisplays _displays;
    private readonly IWindowControl _windows;
    private readonly IAppControl _apps;
    private readonly ITimeline _timeline;
    private readonly RestoreRecordStore<PhoneRecord> _ledger;
    private readonly JsonFileStore<List<PhoneRecord>> _broughtFile;
    private readonly Dictionary<WindowKey, PhoneRecord> _brought = new();
    private readonly JsonFileStore<ZoomBook> _zoomFile;
    private readonly ZoomBook _zoomBook;
    private readonly JsonFileStore<LeftBehindBook> _leftBehindFile;
    private readonly LeftBehindBook _leftBehind;
    private readonly SerialLane _lane;
    private readonly string _screenId;
    private string _screenName;
    private ScreenBehavior _behavior;

    private PhoneDeskState _state = new();
    private bool _wanted;
    private bool _remote;
    private bool _prepared;
    private (uint Id, double BornAt)? _display;
    private Retiring? _retiring;
    private bool _persistOwed;
    private bool _settling;
    private Placement? _placement;
    private int _recheckGeneration;
    private volatile Task? _pendingRecheckTask;
    private double? _offlineSince;
    private double _offlineRetryAt;
    private int _offlineAttempts;
    private bool _offlineRebuildUsed;
    private bool _recoveringDisplay;
    private CancellationTokenSource? _pendingRetryCts;
    private volatile Task? _pendingRetryTask;
    private int _failures;
    private bool _circuitBreakerTripped;
    private bool _verifying;
    private bool _checkingReturns;
    private (int Pid, HashSet<WindowKey> Known, double Until)? _replaced;
    private readonly Dictionary<WatchedKey, CancellationTokenSource> _watches = new();

    public string ScreenId => _screenId;
    public string ScreenName => _screenName;
    public ScreenBehavior Behavior => _behavior;
    public bool Prepared => _prepared;
    public WindowKey? PlacedWindowKey => _placement?.Window.Key;

    public PhoneDeskState State
    {
        get => _state;
        private set
        {
            if (Equals(_state, value)) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    public int DefaultZoom { get; private set; }
    public ScreenFit Fit { get; private set; }

    public event Action<PhoneDeskState>? StateChanged;
    public event Action<DeskGone>? Gone;
    public event Action? TopologyChanging;

    public PhoneDesk(
        IPhoneDisplays displays,
        IWindowControl windows,
        IAppControl apps,
        SnugPaths paths,
        ITimeline? timeline = null,
        int defaultZoom = 2,
        ScreenFit? fit = null,
        string? screenId = null,
        string? screenName = null,
        ScreenBehavior behavior = ScreenBehavior.TouchScreen,
        SerialLane? sharedLane = null,
        ZoomBook? sharedZoomBook = null,
        JsonFileStore<ZoomBook>? sharedZoomFile = null,
        LeftBehindBook? sharedLeftBehind = null,
        JsonFileStore<LeftBehindBook>? sharedLeftBehindFile = null,
        bool prepared = false,
        bool staysOnDismiss = false)
    {
        _displays = displays;
        _windows = windows;
        _apps = apps;
        _timeline = timeline ?? new SystemTimeline();
        DefaultZoom = defaultZoom;
        Fit = fit ?? ScreenFit.Standard;
        _screenId = screenId ?? Config.ScreenConfig.LegacyPhoneId;
        _screenName = string.IsNullOrWhiteSpace(screenName) ? PhoneScreenSpec.Name : screenName;
        _behavior = behavior;
        _prepared = prepared;
        _state = new PhoneDeskState { StaysOnDismiss = staysOnDismiss };
        _lane = sharedLane ?? new SerialLane();
        _ledger = new RestoreRecordStore<PhoneRecord>(paths.RecordFileForScreen(_screenId));
        _broughtFile = new JsonFileStore<List<PhoneRecord>>(paths.BroughtFileForScreen(_screenId));
        _zoomFile = sharedZoomFile ?? new JsonFileStore<ZoomBook>(paths.ZoomBookFile);
        _zoomBook = sharedZoomBook ?? _zoomFile.LoadOrQuarantine() ?? new ZoomBook();
        _leftBehindFile = sharedLeftBehindFile ?? new JsonFileStore<LeftBehindBook>(paths.LeftBehindFile);
        _leftBehind = sharedLeftBehind ?? _leftBehindFile.LoadOrQuarantine() ?? new LeftBehindBook();
    }

    public void SetWanted(bool on)
    {
        if (on == _wanted) return;
        _wanted = on;
        _circuitBreakerTripped = false;
        if (!on)
        {
            CancelRetry();
            State = State with { Dismissed = false };
        }
        else
        {
            _failures = 0;
            if (State.ScreenPhase == PhoneScreenPhase.Failed)
                State = State with { ScreenPhase = PhoneScreenPhase.Preparing, Notice = null };
        }
        _ = _lane.Run(ReconcileAsync);
    }

    public void ResetCircuitBreaker()
    {
        _circuitBreakerTripped = false;
        _failures = 0;
        if (State.Notice is DeskNotice.CircuitBreakerTripped)
            State = State with { Notice = null };
        if (_wanted && !State.Dismissed && _display is null)
            _ = _lane.Run(ReconcileAsync);
    }

    public void DismissScreen()
    {
        if (!_wanted || State.Dismissed) return;
        if (State.StaysOnDismiss)
        {
            Begin(new DeskWork(DeskWorkKind.Dismissing));
            Log.Info($"手机上退出了屏（{_screenName}）：这次远程的远控只在启动时认屏，撤了再建它就选不到，只还原窗口、屏留着");
            _ = _lane.Run(async () =>
            {
                try
                {
                    await KeepOnDismissAsync();
                }
                finally
                {
                    State = State with { Work = null };
                }
            });
            return;
        }
        State = State with { Dismissed = true, Notice = null, Work = new DeskWork(DeskWorkKind.Dismissing) };
        Log.Info("手机上退出了手机屏：放回窗口、撤屏，这次远程不再加");
        _ = _lane.Run(async () =>
        {
            await ReconcileAsync();
            State = State with { Work = null };
        });
    }

    public void ReenterScreen()
    {
        if (!_wanted || !State.Dismissed) return;
        _circuitBreakerTripped = false;
        _failures = 0;
        State = State with { Dismissed = false, Notice = null };
        Log.Info("同会话重新进入手机屏：恢复建屏");
        _ = _lane.Run(ReconcileAsync);
    }

    public void Pull(WindowInfo window) => Pull(window, null);

    public void Pull(WindowInfo window, Func<WindowKey, PhoneRecord?>? transferFromOtherScreen)
    {
        if (State.Quitting.Contains(window.Key.Pid))
        {
            Log.Info($"{window.AppName} 正在退出，不放 {window.Label}");
            return;
        }
        Begin(new DeskWork(DeskWorkKind.Placing, window.Label));
        _ = _lane.Run(async () =>
        {
            try
            {
                if (_behavior == ScreenBehavior.Computer)
                    await PullToComputerAsync(window, transferFromOtherScreen);
                else
                    await PlaceAsync(window, zoom: true, transferFromOtherScreen);
            }
            finally
            {
                State = State with { Work = null };
            }
        });
    }

    internal async Task<bool> FocusAsync(WindowInfo window)
    {
        if (ReadyDisplay($"切换到 {window.Label}") is not { } id || _displays.BoundsOf(id) is not { } bounds)
        {
            State = State with { Notice = new DeskNotice.ScreenNotReady() };
            return true;
        }
        if (await ReachAsync(window) is not { } look) return true;
        if (look.Minimized || !bounds.Contains(look.Frame.Center))
            return false;
        await _windows.FocusAsync(window.Key);
        Log.Info($"切换到 {window.Label}{(look.FullScreen ? "（全屏）" : "")}");
        return true;
    }

    public void PutBackBrought(WindowKey key)
    {
        if (!_brought.ContainsKey(key)) return;
        Begin(new DeskWork(DeskWorkKind.PuttingBack));
        _ = _lane.Run(async () =>
        {
            try
            {
                await PutBackBroughtAsync(key);
            }
            finally
            {
                State = State with { Work = null };
            }
        });
    }

    internal PhoneRecord? DetachPlacementForTransfer()
    {
        if (_placement is not { } p) return null;
        var transferred = p.Record;
        SetPlacement(null);
        State = State with { ListVisible = true };
        return transferred;
    }

    internal PhoneRecord? DetachBroughtForTransfer(WindowKey key)
    {
        if (!_brought.Remove(key, out var record)) return null;
        SaveBrought();
        SyncBroughtState();
        return record;
    }

    public void UpdateWish(
        string name,
        ScreenFit fit,
        ScreenBehavior behavior,
        bool prepared = false,
        bool staysOnDismiss = false)
    {
        if (!string.IsNullOrWhiteSpace(name))
            _screenName = name;
        _behavior = behavior;
        _prepared = prepared;
        State = State with { StaysOnDismiss = staysOnDismiss };
        SetFit(fit);
    }

    internal Task ReturnWindowIfPlacedAsync(int pid)
    {
        return _lane.Run(async () =>
        {
            if (_placement is { } p && p.Window.Key.Pid == pid)
            {
                await ReturnToOriginalAsync(p);
                await MatchWidthAsync();
            }
        });
    }

    internal Task ReturnBroughtIfPidAsync(int pid)
    {
        return _lane.Run(async () =>
        {
            var matching = _brought.Keys.Where(k => k.Pid == pid).ToList();
            foreach (var key in matching)
                await PutBackBroughtAsync(key);
        });
    }

    public void Open(AppInfo app)
    {
        Begin(new DeskWork(DeskWorkKind.Opening, app.Name));
        _ = _lane.Run(async () =>
        {
            await OpenWindowAsync(app, newWindowIn: null);
            State = State with { Work = null };
        });
    }

    public void NewWindow(RunningApp app)
    {
        if (State.Quitting.Contains(app.Pid))
        {
            Log.Info($"{app.App.Name} 正在退出，不新建窗口");
            return;
        }
        Begin(new DeskWork(DeskWorkKind.Opening, app.App.Name));
        _ = _lane.Run(async () =>
        {
            await OpenWindowAsync(app.App, newWindowIn: app.Pid);
            State = State with { Work = null };
        });
    }

    public void Quit(RunningApp app)
    {
        if (State.Quitting.Contains(app.Pid)) return;
        var quitting = new HashSet<int>(State.Quitting) { app.Pid };
        State = State with { Notice = null, Quitting = quitting };

        _ = _lane.Run(async () =>
        {
            if (_placement is { } p && p.Window.Key.Pid == app.Pid)
            {
                if (!await ReturnToOriginalAsync(p))
                {
                    Log.Warn($"{p.Window.Label} 放不回去，先不退出 {app.App.Name}");
                    RemoveQuitting(app.Pid);
                    State = State with { Notice = new DeskNotice.WindowUnresponsive(p.Window.AppName) };
                    return;
                }
                await MatchWidthAsync();
            }

            Log.Info($"请 {app.App.Name} 退出");
            if (!await _apps.QuitAsync(app.Pid))
            {
                RemoveQuitting(app.Pid);
                State = State with { Notice = new DeskNotice.WindowUnresponsive(app.App.Name) };
                return;
            }
            WatchQuit(app);
        });
    }

    public void ForceQuit(int pid, string appName)
    {
        Unwatch(WatchedKey.ForQuit(pid));
        State = State with { Notice = null };
        Log.Warn($"强制退出 {appName}（pid {pid}）");
        _ = _lane.Run(async () =>
        {
            if (!await _apps.ForceQuitAsync(pid))
                State = State with { Notice = new DeskNotice.WindowUnresponsive(appName) };
        });
    }

    public void Close(WindowInfo window)
    {
        var key = WatchedKey.ForClose(window.Key);
        if (_watches.ContainsKey(key)) return;
        var closing = new HashSet<WindowKey>(State.Closing) { window.Key };
        State = State with { Notice = null, Closing = closing };

        _ = _lane.Run(async () =>
        {
            if (_placement is { } p && p.Window.Key == window.Key)
            {
                if (!await ReturnToOriginalAsync(p, restoringState: false))
                {
                    Log.Warn($"{p.Window.Label} 放不回去，先不关");
                    RemoveClosing(window.Key);
                    State = State with { Notice = new DeskNotice.WindowUnresponsive(p.Window.AppName) };
                    return;
                }
                await MatchWidthAsync();
            }

            if ((await _windows.LookAsync(window.Key))?.FullScreen == true)
                await _windows.SetFullScreenAsync(window.Key, false);

            bool wasShown = await _windows.IsShownAsync(window.Key);
            if (!await _windows.CloseAsync(window.Key))
            {
                var notice = await UnreachableAsync(window);
                Log.Warn($"关不了 {window.Label}");
                RemoveClosing(window.Key);
                State = State with { Notice = notice };
                return;
            }
            Log.Info($"关闭 {window.Label}");
            WatchClose(window, hides: wasShown);
        });
    }

    public void ClosePlaced(ulong windowHandle)
    {
        if (_placement is not { } p || p.Window.Key.Handle != windowHandle) return;
        Log.Info($"手机上点了 {p.Window.Label} 的关闭按钮：先放回原位再关");
        Close(p.Window);
    }

    public void DismissNotice()
    {
        if (State.Notice is DeskNotice.QuitStalled(_, int pid))
            Unwatch(WatchedKey.ForQuit(pid));
        State = State with { Notice = null };
    }

    public void ShowList()
    {
        State = State with { ListVisible = true };
        _ = _lane.Run(MatchWidthAsync);
    }

    public void ReturnToPlaced()
    {
        if (_placement is not { } p) return;
        Begin(new DeskWork(DeskWorkKind.Placing, p.Window.Label));
        _ = _lane.Run(async () =>
        {
            await RevealAsync();
            State = State with { Work = null };
        });
    }

    public void PutBack()
    {
        if (_placement is null) return;
        Begin(new DeskWork(DeskWorkKind.PuttingBack));
        _ = _lane.Run(async () =>
        {
            if (_placement is { } p && !await ReturnToOriginalAsync(p))
                State = State with { Notice = new DeskNotice.WindowUnresponsive(p.Window.AppName) };
            await MatchWidthAsync();
            State = State with { Work = null };
        });
    }

    public void Zoom(int delta)
    {
        if (State.Placed is not { } placed || !placed.Zoomable) return;
        int clamped = (placed.Zoom + delta).Clamp(ZoomBook.MinSteps, ZoomBook.MaxSteps);
        if (clamped == placed.Zoom) return;

        Begin(new DeskWork(DeskWorkKind.Zooming));
        _ = _lane.Run(async () =>
        {
            if (await ChangeZoomAsync(delta) && _placement is { } p && p.Window.AppId is { } appId)
            {
                _zoomBook.Remember(p.Record.Zoom, appId);
                SaveZoomBook();
            }
            State = State with { Work = null };
        });
    }

    public void SetDefaultZoom(int steps)
    {
        DefaultZoom = steps;
        if (_zoomBook.Remembered.Count == 0) return;
        _zoomBook.ForgetAll();
        SaveZoomBook();
    }

    public void SetFit(ScreenFit fit)
    {
        if (Equals(fit, Fit)) return;
        Fit = fit;
        if (State.ScreenPhase == PhoneScreenPhase.Off) return;
        if (State.Work is null && _display is not null)
            Begin(new DeskWork(DeskWorkKind.Reshaping));

        _ = _lane.Run(async () =>
        {
            if (_display is { } d && !Equals(_displays.FitOf(d.Id), Fit))
            {
                Begin(new DeskWork(DeskWorkKind.Reshaping));
                await ReshapeAsync(d.Id);
            }
            if (State.Work?.Kind == DeskWorkKind.Reshaping)
                State = State with { Work = null };
        });
    }

    public void DisplaysChanged()
    {
        if (_circuitBreakerTripped) return;
        if (_display is not null)
            SettleDisplays();
        else if (State.ScreenPhase != PhoneScreenPhase.Suspended)
            return;

        int gen = ++_recheckGeneration;
        _pendingRecheckTask = Task.Run(async () =>
        {
            await _timeline.SleepAsync(RecheckDelay);
            if (gen != _recheckGeneration || _circuitBreakerTripped) return;
            await _lane.Run(RecheckAsync);
        });
    }

    public void VerifyPlacement()
    {
        if ((_placement is null && _brought.Count == 0) || State.Work is not null || _verifying) return;
        _verifying = true;
        _ = _lane.Run(async () =>
        {
            try
            {
                if (_brought.Count > 0)
                    await PruneMissingBroughtAsync();
                if (_placement is not { } p) return;
                if (!await _windows.ExistsAsync(p.Window.Key))
                {
                    Log.Info($"手机屏上的 {p.Window.Label} 在外部关闭，回到 App 资源库");
                    SetPlacement(null);
                    State = State with { ListVisible = true };
                    var known = new HashSet<WindowKey>(p.Known) { p.Window.Key };
                    _replaced = (p.Window.Key.Pid, known, _timeline.Now + ReplaceWait);
                    await LeaveBehindAsync(p.Record);
                    await MatchWidthAsync();
                    return;
                }
                if (!State.ListVisible && !await _windows.IsShownAsync(p.Window.Key))
                {
                    Log.Info($"手机屏上的 {p.Window.Label} 不在屏上了，回到 App 资源库");
                    State = State with { ListVisible = true };
                    await MatchWidthAsync();
                }
            }
            finally
            {
                _verifying = false;
            }
        });
    }

    public void SetRemote(bool on)
    {
        if (on == _remote) return;
        _remote = on;
        if (!on) SettleDisplays();
    }

    public void SettleDisplays()
    {
        if (_circuitBreakerTripped) return;
        if (_wanted && !State.Dismissed && !_recoveringDisplay)
        {
            bool retryCreate = _display is null
                && State.ScreenPhase == PhoneScreenPhase.Suspended
                && !_displays.IsRemoteExclusiveDisplayActive();
            bool checkDisplay = _display is not null
                && State.ScreenPhase is PhoneScreenPhase.Ready or PhoneScreenPhase.Suspended;
            if (retryCreate || checkDisplay)
            {
                _recoveringDisplay = true;
                _ = _lane.Run(async () =>
                {
                    try
                    {
                        if (_display is null) await ReconcileAsync();
                        else await RecoverOfflineDisplayAsync();
                    }
                    finally { _recoveringDisplay = false; }
                });
            }
        }

        if ((_retiring is null && !_persistOwed) || _settling) return;
        _settling = true;
        _ = _lane.Run(async () =>
        {
            try
            {
                if (_retiring is { } r)
                {
                    if (_displays.IsOnline(r.Id)) return;
                    _retiring = null;
                    Log.Info($"撤掉的手机屏 #{r.Id} 已下线");
                    await SettleAsync(r.Before, "撤屏", r.Permanently);
                }

                if (!_persistOwed || _remote || _display is not null) return;
                var others = _displays.OtherDisplays();
                if (others.Any(o => o.Asleep)) return;
                _persistOwed = false;
                foreach (var other in others)
                    await _displays.SetModeAsync(other.DisplayId, other.Mode, permanently: true);
            }
            finally
            {
                _settling = false;
            }
        });
    }

    public void Recover() => _ = _lane.Run(PutBackLeftoverAsync);

    public void CheckReturns()
    {
        if ((_replaced is null && _leftBehind.Entries.Count == 0) || State.Work is not null || _checkingReturns)
            return;
        _checkingReturns = true;
        _ = _lane.Run(async () =>
        {
            try
            {
                await FollowReplacementAsync();
                await TakeBackReturnedAsync();
            }
            finally
            {
                _checkingReturns = false;
            }
        });
    }

    public async Task ShutdownAsync()
    {
        _wanted = false;
        CancelRetry();
        await _lane.Run(ReconcileAsync);
    }

    internal async Task IdleAsync()
    {
        while (true)
        {
            var retry = _pendingRetryTask;
            if (retry is not null)
            {
                try { await retry; } catch { }
            }
            var recheck = _pendingRecheckTask;
            if (recheck is not null)
            {
                try { await recheck; } catch { }
            }
            await _lane.DrainAsync();
            if (ReferenceEquals(_pendingRetryTask, retry)
                && ReferenceEquals(_pendingRecheckTask, recheck)
                && _watches.Count == 0)
                break;
        }
    }

    private async Task ReconcileAsync()
    {
        if (_circuitBreakerTripped) return;
        if (!_wanted || State.Dismissed)
        {
            if (State.ScreenPhase != PhoneScreenPhase.Off) await RemoveScreenAsync();
        }
        else if (_display is null)
        {
            await AddScreenAsync();
        }
    }

    private async Task RecoverOfflineDisplayAsync()
    {
        if (!_wanted || State.Dismissed || _display is not { } current
            || State.ScreenPhase is not (PhoneScreenPhase.Ready or PhoneScreenPhase.Suspended)) return;

        bool online = _displays.IsAttachedToDesktop(current.Id);
        if (online && _offlineSince is null) return;

        if (!online)
        {
            if (_offlineSince is null)
            {
                _offlineSince = _timeline.Now;
                Log.Warn($"虚拟屏「{_screenName}」#{current.Id} 已脱离活动桌面，暂停使用");
                State = State with { ScreenPhase = PhoneScreenPhase.Suspended };
            }
            if (_displays.IsRemoteExclusiveDisplayActive()
                || _timeline.Now - _offlineSince.Value < DisplayRecoveryDebounce
                || _timeline.Now < _offlineRetryAt
                || _offlineAttempts > DisplayRecoveryDelays.Length)
                return;

            try
            {
                await _displays.TryReactivateAsync(current.Id, WantedWidth);
                online = _displays.IsAttachedToDesktop(current.Id);
            }
            catch (Exception ex)
            {
                Log.Warn($"虚拟屏「{_screenName}」#{current.Id} 恢复失败：{ex.Message}");
            }
            if (!_wanted || State.Dismissed) return;
            if (!online)
            {
                if (_displays.IsRemoteExclusiveDisplayActive()) return;
                if (_offlineAttempts < DisplayRecoveryDelays.Length)
                {
                    double delay = DisplayRecoveryDelays[_offlineAttempts++];
                    _offlineRetryAt = _timeline.Now + delay;
                    Log.Warn($"虚拟屏「{_screenName}」#{current.Id} 尚未恢复；{delay:0} 秒后重试");
                }
                else if (!_offlineRebuildUsed)
                {
                    _offlineRebuildUsed = true;
                    Log.Warn($"虚拟屏「{_screenName}」#{current.Id} 无法回接，撤掉旧记录并重新创建");
                    await RemoveScreenAsync(resetRebuildBudget: false);
                    if (_wanted && !State.Dismissed) await AddScreenAsync();
                }
                else
                {
                    _offlineAttempts++;
                    Log.Warn($"虚拟屏「{_screenName}」#{current.Id} 未能恢复，本次连接停止重试");
                }
                return;
            }
        }

        _offlineSince = null;
        _offlineAttempts = 0;
        _offlineRetryAt = 0;
        State = State with { ScreenPhase = PhoneScreenPhase.Ready };
        Log.Info($"虚拟屏「{_screenName}」#{current.Id} 已恢复到活动桌面");
        if (State.ListVisible) await MatchWidthAsync();
        else await RefillAsync("虚拟屏恢复后重新铺满");
    }

    private async Task AddScreenAsync()
    {
        if (_displays.IsRemoteExclusiveDisplayActive())
        {
            if (State.ScreenPhase != PhoneScreenPhase.Suspended)
                Log.Info("UU 超级屏独占桌面，暂停创建虚拟屏");
            State = State with { ScreenPhase = PhoneScreenPhase.Suspended, DisplayId = null };
            return;
        }
        _offlineSince = null;
        _offlineAttempts = 0;
        _offlineRetryAt = 0;
        State = State with { ScreenPhase = PhoneScreenPhase.Preparing };
        if (_retiring is { } old)
        {
            _retiring = null;
            Log.Warn($"上一块手机屏 #{old.Id} 还没下线就又要加屏，放弃旧的善后记录");
        }
        var before = await CaptureSurroundingsAsync("加屏");
        TopologyChanging?.Invoke();
        try
        {
            uint id = await _displays.CreateAsync(Fit, _screenName);
            _display = (id, _timeline.Now);
            _failures = 0;
            await SettleAsync(before, "加屏", permanently: false);
            State = State with
            {
                Notice = null,
                ScreenPhase = PhoneScreenPhase.Ready,
                DisplayId = id
            };
            Log.Info($"虚拟屏「{_screenName}」#{id} 已就绪（{_displays.WidthOf(id)?.ToString() ?? "?"} 宽）");
        }
        catch (Exception ex)
        {
            State = State with { ScreenPhase = PhoneScreenPhase.Failed, DisplayId = null };
            Log.Error($"加手机屏失败：{ex.Message}");
            var nowOtherIds = _displays.OtherDisplays().Select(o => o.DisplayId).ToHashSet();
            bool lostOther = before.Others.Any(o => !nowOtherIds.Contains(o.DisplayId));
            if (lostOther || ex is PhoneScreenException { IsSafetyAbort: true })
            {
                Log.Error("检测到显示拓扑异常（原有显示器丢失或身份安全中止），停止本次会话加屏重试");
                return;
            }
            ScheduleRetry();
        }
    }

    private void ScheduleRetry()
    {
        _pendingRetryCts?.Cancel();
        _pendingRetryCts = null;
        if (_failures >= RetryDelays.Length)
        {
            _pendingRetryTask = null;
            _circuitBreakerTripped = true;
            Log.Error($"加手机屏连续失败 {_failures + 1} 次，已触发安全熔断保护，停止自动重试以防屏幕闪烁");
            State = State with
            {
                ScreenPhase = PhoneScreenPhase.Failed,
                Notice = new DeskNotice.CircuitBreakerTripped()
            };
            return;
        }
        double delay = RetryDelays[_failures++];
        Log.Info($"{(int)delay} 秒后重试加手机屏（第 {_failures} 次重试）");
        var cts = new CancellationTokenSource();
        _pendingRetryCts = cts;
        _pendingRetryTask = Task.Run(async () =>
        {
            await _timeline.SleepAsync(delay, cts.Token);
            if (cts.IsCancellationRequested || _circuitBreakerTripped) return;
            // A reshape can invalidate the retry while it waits on the shared lane.
            await _lane.Run(() => cts.IsCancellationRequested || _circuitBreakerTripped ? Task.CompletedTask : ReconcileAsync());
        });
    }

    private void CancelRetry(bool resetFailures = true)
    {
        _pendingRetryCts?.Cancel();
        _pendingRetryCts = null;
        _pendingRetryTask = null;
        if (resetFailures) _failures = 0;
    }

    private async Task RemoveScreenAsync(bool resetRebuildBudget = true)
    {
        _offlineSince = null;
        _offlineAttempts = 0;
        _offlineRetryAt = 0;
        if (resetRebuildBudget) _offlineRebuildUsed = false;
        if (_placement is { } p) await PutBackAsync(p.Record);
        SetPlacement(null);
        await ReturnAllBroughtAsync();
        if (_behavior == ScreenBehavior.Computer)
            await EvacuateStrayWindowsAsync();
        await RetireDisplayAsync();
        foreach (var cts in _watches.Values) cts.Cancel();
        _watches.Clear();
        State = State with
        {
            ScreenPhase = PhoneScreenPhase.Off,
            DisplayId = null,
            ListVisible = true,
            Notice = null,
            Quitting = new HashSet<int>(),
            Closing = new HashSet<WindowKey>(),
            Brought = new HashSet<WindowKey>(_brought.Keys)
        };
    }

    private async Task KeepOnDismissAsync()
    {
        if (_placement is { } p)
            _ = await ReturnToOriginalAsync(p);
        await ReturnAllBroughtAsync();
        await EvacuateStrayWindowsAsync();
        await MatchWidthAsync();
        State = State with
        {
            ListVisible = true,
            Notice = new DeskNotice.KeptOnDismiss()
        };
    }

    internal async Task EndSessionKeepScreenAsync()
    {
        if (_remote || !_wanted || !_prepared || _display is not { } d || !_displays.IsOnline(d.Id))
            return;
        if (_placement is { } p)
            _ = await ReturnToOriginalAsync(p);
        await ReturnAllBroughtAsync();
        await EvacuateStrayWindowsAsync();
        await MatchWidthAsync();
        State = State with { ListVisible = true };
    }

    private async Task EvacuateStrayWindowsAsync()
    {
        if (_display is not { } d) return;
        if (_displays.BoundsOf(d.Id) is not { } bounds || _displays.MainArea() is not { } main) return;
        if (Intersects(bounds, main))
        {
            Log.Warn($"清场时主屏区域与虚拟屏「{_screenName}」#{d.Id} 重叠，跳过挪回");
            return;
        }

        var placedKey = _placement?.Window.Key;
        var snapshot = await _windows.SnapshotAsync();
        foreach (var (key, frame) in snapshot.OrderBy(kv => kv.Key.Pid).ThenBy(kv => kv.Key.Handle))
        {
            if (placedKey is { } pk && key == pk) continue;
            if (!bounds.Contains(frame.Center)) continue;
            double w = Math.Min(frame.Width, main.Width);
            double h = Math.Min(frame.Height, main.Height);
            double x = main.X + Math.Max(0, (main.Width - w) / 2.0);
            double y = main.Y + Math.Max(0, (main.Height - h) / 2.0);
            _ = await _windows.MoveAsync(key, new RectD(x, y, w, h));
        }
    }

    private static bool Intersects(RectD a, RectD b) =>
        a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0
        && a.MinX < b.MaxX && a.MaxX > b.MinX
        && a.MinY < b.MaxY && a.MaxY > b.MinY;

    private async Task RetireDisplayAsync()
    {
        if (_display is not { } d) return;
        double age = _timeline.Now - d.BornAt;
        if (age < PhoneScreenSpec.MinimumLifetime.TotalSeconds)
            await _timeline.SleepAsync(PhoneScreenSpec.MinimumLifetime.TotalSeconds - age);

        var goneBounds = _displays.BoundsOf(d.Id);
        var before = await CaptureSurroundingsAsync("撤屏");
        TopologyChanging?.Invoke();
        bool offline = await _displays.RetireAsync(d.Id);
        _display = null;

        if (goneBounds is { } screen)
        {
            var filtered = new Dictionary<WindowKey, RectD>();
            foreach (var kv in before.Windows)
            {
                if (!screen.Contains(kv.Value.Center))
                    filtered[kv.Key] = kv.Value;
            }
            before = before with { Windows = filtered };
        }

        if (_remote) _persistOwed = true;
        var pending = new Retiring(d.Id, before, !_remote);
        if (offline)
        {
            await SettleAsync(pending.Before, "撤屏", pending.Permanently);
            Log.Info($"已撤掉手机屏 #{d.Id}");
        }
        else
        {
            _retiring = pending;
            Log.Info($"手机屏 #{d.Id} 尚未下线，稍后自动善后");
        }
    }

    private async Task<Surroundings> CaptureSurroundingsAsync(string beforeWhat)
    {
        var others = _displays.OtherDisplays();
        Log.Info($"{beforeWhat}前别的屏：{(others.Count == 0 ? "没有" : string.Join("、", others.Select(o => $"{o.FriendlyName} {o.Mode}")))}");
        return new Surroundings(await _windows.SnapshotAsync(), others);
    }

    private async Task SettleAsync(Surroundings before, string afterWhat, bool permanently)
    {
        await _timeline.SleepAsync(TopologySettle);
        var nowMap = _displays.OtherDisplays().ToDictionary(o => o.DisplayId, o => o.Mode);
        var drifted = new List<(OtherDisplay Was, DisplayModeInfo Now)>();
        foreach (var was in before.Others)
        {
            if (!nowMap.TryGetValue(was.DisplayId, out var cur))
                Log.Warn($"{afterWhat}后原显示器 {was.FriendlyName} 不在活动桌面中");
            else if (!cur.IsSameAs(was.Mode))
                drifted.Add((was, cur));
        }

        if (drifted.Count > 0)
        {
            Log.Info($"{afterWhat}时系统改变了其它屏幕模式，恢复中");
            TopologyChanging?.Invoke();
            foreach (var (was, _) in drifted)
                await _displays.SetModeAsync(was.DisplayId, was.Mode, permanently);
            await _timeline.SleepAsync(TopologySettle);
        }
        await _windows.RestoreAsync(before.Windows, _placement?.Window.Key);
    }

    private async Task ReshapeAsync(uint id)
    {
        if (!await _displays.ReshapeAsync(id, Fit))
        {
            await HandleReshapeFailureAsync(id);
            return;
        }
        State = State with { Notice = new DeskNotice.Reshaped() };
        if (_placement is not { } p) return;
        p.Width = Fit.BaseWidth;
        p.Settled = null;
        if (!State.ListVisible)
        {
            Log.Info($"换了比例，{p.Window.Label} 按新比例重新铺满");
            await FillAsync(p, id);
        }
        SetPlacement(p);
    }

    private async Task HandleReshapeFailureAsync(uint id)
    {
        var notice = new DeskNotice.ReshapeFailed();
        if (_displays.IsOnline(id) && _displays.FitOf(id) is not null && _displays.BoundsOf(id) is not null)
        {
            Log.Warn($"手机屏 #{id} 没能换成新比例（{Fit}），下次加屏时按新的建");
            State = State with { Notice = notice };
            return;
        }

        Log.Warn($"手机屏 #{id} 换比例失败，原屏已不在线或不可用，重新启用时再建");
        CancelRetry();
        var record = _placement?.Record;
        if (_displays.IsOnline(id))
            await RetireDisplayAsync();
        _display = null;
        _placement = null;
        _replaced = null;
        State = State with
        {
            ScreenPhase = PhoneScreenPhase.Failed,
            DisplayId = null,
            Placed = null,
            CloseButton = null,
            ListVisible = true,
            Notice = notice
        };
        if (record is not null) await RecoverRecordAsync(record);
    }

    private async Task RecheckAsync()
    {
        if (_circuitBreakerTripped) return;
        if (_wanted && !State.Dismissed)
        {
            if (_display is null && State.ScreenPhase == PhoneScreenPhase.Suspended)
            {
                await ReconcileAsync();
                return;
            }
            if (_display is { } current
                && (!_displays.IsAttachedToDesktop(current.Id) || State.ScreenPhase == PhoneScreenPhase.Suspended))
            {
                await RecoverOfflineDisplayAsync();
                return;
            }
        }
        if (State.ListVisible) await MatchWidthAsync();
        else await RefillAsync("屏幕配置变了");
    }

    private async Task RefillAsync(string reason)
    {
        if (_display is not { } d || _placement is not { } p) return;
        var look = await _windows.LookAsync(p.Window.Key);
        if (look is null) return;
        bool moved = p.Settled is null || !look.Frame.Matches(p.Settled.Value);
        if (!moved && _displays.WidthOf(d.Id) == p.Width) return;
        Log.Info($"{reason}，{p.Window.Label} 重新铺满");
        await FillAsync(p, d.Id);
        SetPlacement(p);
    }

    private ScreenFit CurrentScreenFit =>
        _display is { } d && _displays.FitOf(d.Id) is { } f ? f : Fit;

    private int WantedWidth =>
        _placement is { } p && !State.ListVisible ? p.Width : CurrentScreenFit.BaseWidth;

    private Task MatchWidthAsync()
    {
        if (_display is not { } d) return Task.CompletedTask;
        return SwitchWidthAsync(d.Id, WantedWidth);
    }

    private async Task<bool> SwitchWidthAsync(uint id, int width)
    {
        if (_displays.WidthOf(id) == width) return true;
        try
        {
            await _displays.SetWidthAsync(id, width);
            return true;
        }
        catch (PhoneScreenException ex)
        {
            Log.Warn($"手机屏 #{id} 切换 {width} 宽失败：{ex.Message}");
            return false;
        }
    }

    private async Task<RectD?> FillAsync(Placement p, uint id)
    {
        if (!await SwitchWidthAsync(id, p.Width)) return null;
        if (_displays.WindowAreaOf(id) is not { } area) return null;
        if (await _windows.MoveAsync(p.Window.Key, area) is not { } got) return null;

        var bounds = _displays.BoundsOf(id) ?? area;
        if (!OriginOnScreen(got, bounds) && _displays.MainArea() is { } mainArea)
        {
            Log.Info($"{p.Window.Label} 首次未能落到目标屏（实际 {got.Short}），经主屏中转再放");
            var relay = new RectD(
                mainArea.X + Math.Max(0, (mainArea.Width - Math.Min(mainArea.Width, area.Width)) / 2),
                mainArea.Y + Math.Max(0, (mainArea.Height - Math.Min(mainArea.Height, area.Height)) / 2),
                Math.Min(mainArea.Width, Math.Max(400, got.Width)),
                Math.Min(mainArea.Height, Math.Max(300, got.Height)));
            _ = await _windows.MoveAsync(p.Window.Key, relay);
            if (await _windows.MoveAsync(p.Window.Key, area) is not { } retried) return null;
            got = retried;
        }

        if (!OriginOnScreen(got, bounds))
        {
            Log.Info($"{p.Window.Label} 原点未能落到目标屏（实际 {got.Short}，目标 {bounds.Short}），放弃放置");
            return null;
        }

        p.Settled = got;
        p.Record.Filled = got;
        WriteRecord(p.Record);

        if (p.Asking)
        {
            State = State with { CloseButton = null };
        }
        else
        {
            var cbRect = await _windows.CloseButtonAsync(p.Window.Key);
            State = State with
            {
                CloseButton = cbRect is { } r ? new CloseButton(r, p.Window.Key.Handle, got) : null
            };
        }

        bool placedAtOrigin = Math.Abs(got.MinX - area.MinX) < 2 && Math.Abs(got.MinY - area.MinY) < 2;
        if (!placedAtOrigin || _displays.WidthOf(id) is not { } current) return got;
        int nextWidth = CurrentScreenFit.WidthToFit(got.Size, area.Size, current);
        if (nextWidth <= current || nextWidth == p.Width) return got;

        Log.Info($"{p.Window.Label} 在 {current} 宽铺不满（实际 {got.Short}），换到 {nextWidth} 宽");
        p.Width = nextWidth;
        return await FillAsync(p, id);
    }

    private static bool OriginOnScreen(RectD frame, RectD screenBounds) =>
        frame.X >= screenBounds.MinX - 2.0
        && frame.X < screenBounds.MaxX + 2.0
        && frame.Y >= screenBounds.MinY - 2.0
        && frame.Y < screenBounds.MaxY + 2.0;

    private async Task RevealAsync()
    {
        if (_display is not { } d || _placement is not { } p) return;
        await SwitchWidthAsync(d.Id, p.Width);
        await RefillAsync("回到窗口");
        State = State with { ListVisible = false };
        await _windows.FocusAsync(p.Window.Key);
    }

    private async Task PlaceAsync(WindowInfo window, bool zoom = true, Func<WindowKey, PhoneRecord?>? transferFromOtherScreen = null)
    {
        double started = _timeline.Now;
        if (ReadyDisplay($"放 {window.Label}") is not { } id)
        {
            State = State with { Notice = new DeskNotice.ScreenNotReady() };
            return;
        }
        if (_placement?.Window.Key == window.Key)
        {
            await RevealAsync();
            return;
        }
        if (await ReachAsync(window) is null) return;

        if (_placement is { } cur && !await ReturnToOriginalAsync(cur))
        {
            Log.Warn($"{cur.Window.Label} 放不回去，先不换成 {window.Label}");
            State = State with { Notice = new DeskNotice.WindowUnresponsive(cur.Window.AppName) };
            return;
        }
        if (await PutBackLeftoverAsync() is { } stuck)
        {
            Log.Warn($"{stuck.Label} 还放不回去，先不放 {window.Label}");
            State = State with { Notice = new DeskNotice.WindowUnresponsive(stuck.App) };
            return;
        }
        if (await ReachAsync(window) is not { } look) return;

        var known = (await _windows.ShownWindowsAsync(window.Key.Pid))
            .Select(w => w.Key)
            .Where(k => k != window.Key)
            .ToHashSet();

        PhoneRecord? transferredRecord = transferFromOtherScreen?.Invoke(window.Key);
        int? owedIdx = transferredRecord is null ? _leftBehind.Match(window, look.Frame) : null;
        PhoneRecord record;
        if (transferredRecord is not null)
        {
            record = transferredRecord with { Window = window.Key };
            Log.Info($"{window.Label} 直接跨屏转移，沿用原始还原坐标");
        }
        else if (owedIdx is { } idx)
        {
            record = _leftBehind.Entries[idx].ReturnedAs(window.Key);
        }
        else
        {
            record = new PhoneRecord
            {
                Window = window.Key,
                App = window.AppName,
                Label = window.Label,
                Original = OriginalFrame(look.Frame, id),
                WasFullScreen = look.FullScreen,
                WasMinimized = look.Minimized,
                AppId = window.AppId,
                Title = window.Title
            };
        }

        var next = new Placement(window, record, CurrentScreenFit.BaseWidth, known, asking: !zoom);
        if (!WriteRecord(next.Record))
        {
            State = State with { Notice = new DeskNotice.RecordUnwritable() };
            return;
        }
        if (owedIdx is { } removeIdx)
        {
            Log.Info($"{window.Label} 带着手机屏上的样子回来了，接着放");
            _leftBehind.RemoveAt(removeIdx);
            SaveLeftBehind();
        }
        if (look.Minimized) await _windows.SetMinimizedAsync(window.Key, false);
        if (look.FullScreen)
        {
            await _windows.SetFullScreenAsync(window.Key, false);
            if ((await _windows.LookAsync(window.Key))?.Frame is { } windowed)
            {
                next.Record.Original = OriginalFrame(windowed, id);
                WriteRecord(next.Record);
            }
        }

        SetPlacement(next);
        if (await FillAsync(next, id) is not { } got)
        {
            var notice = await UnreachableAsync(window);
            Log.Warn($"{window.Label} 挪不上手机屏，放回去");
            SetPlacement(null);
            await PutBackAsync(next.Record);
            await MatchWidthAsync();
            State = State with { Notice = notice };
            return;
        }

        SetPlacement(next);
        await _windows.FocusAsync(window.Key);
        State = State with { ListVisible = false };
        Log.Info($"{window.Label} 放上手机屏（{_timeline.Now - started:0.0}s）：原位 {next.Record.Original.Short}，当前 {got.Short}");

        int steps = _zoomBook.StepsFor(window.AppId, DefaultZoom) - next.Record.Zoom;
        if (!zoom || steps == 0) return;
        State = State with { Work = new DeskWork(DeskWorkKind.Zooming) };
        await _timeline.SleepAsync(SettleDelay);
        await ChangeZoomAsync(steps);
    }

    private RectD OriginalFrame(RectD frame, uint phoneDisplayId)
    {
        if (_displays.BoundsOf(phoneDisplayId) is { } phone
            && phone.Contains(frame.Center)
            && _displays.MainArea() is { } main)
        {
            double w = Math.Min(frame.Width, main.Width);
            double h = Math.Min(frame.Height, main.Height);
            return new RectD(Math.Round(main.MidX - w / 2), Math.Round(main.MidY - h / 2), w, h);
        }
        return frame;
    }

    private async Task OpenWindowAsync(AppInfo app, int? newWindowIn)
    {
        double started = _timeline.Now;
        if (ReadyDisplay($"打开 {app.Name}") is null)
        {
            State = State with { Notice = new DeskNotice.ScreenNotReady() };
            return;
        }

        int? knownPid = newWindowIn ?? await _apps.PidOfAsync(app);
        if (knownPid is { } kp && State.Quitting.Contains(kp))
        {
            Log.Info($"{app.Name} 正在退出，不打开");
            return;
        }

        var before = new HashSet<WindowKey>();
        if (knownPid is { } kp2)
        {
            foreach (var w in await _windows.ShownWindowsAsync(kp2))
                before.Add(w.Key);
        }
        if (newWindowIn is null)
        {
            foreach (var w in await _apps.AppWindowsAsync(app, knownPid))
                before.Add(w.Key);
        }

        int pid;
        if (newWindowIn is { } runningPid)
        {
            if (!await _apps.NewWindowAsync(runningPid))
            {
                Log.Warn($"{app.Name} 无法新建窗口");
                State = State with { Notice = new DeskNotice.NoNewWindow(app.Name) };
                return;
            }
            pid = runningPid;
        }
        else
        {
            if (await _apps.OpenAsync(app) is not { } openedPid)
            {
                Log.Warn($"打不开 {app.Name}");
                State = State with { Notice = new DeskNotice.OpenFailed(app.Name) };
                return;
            }
            pid = openedPid;
        }

        double wait = knownPid is null ? LaunchWait : WindowWait;
        double deadline = _timeline.Now + wait;
        double exitGraceDeadline = _timeline.Now + 3.0;
        while (_wanted)
        {
            var pidWindows = await _windows.ShownWindowsAsync(pid);
            var appWindows = newWindowIn is null
                ? await _apps.AppWindowsAsync(app, pid)
                : Array.Empty<WindowInfo>();
            var appeared = pidWindows.Concat(appWindows).FirstOrDefault(w => !before.Contains(w.Key));
            if (appeared is not null)
            {
                Log.Info($"{app.Name}（{_timeline.Now - started:0.1}s）出现 {appeared.Label}，放上{_screenName}");
                State = State with { Work = new DeskWork(DeskWorkKind.Placing, appeared.Label) };
                if (_behavior == ScreenBehavior.Computer)
                    await PullToComputerAsync(appeared);
                else
                    await PlaceAsync(appeared);
                return;
            }
            bool running = await _apps.IsRunningAsync(pid);
            if (newWindowIn is null
                && appWindows.Count > 0
                && ((!running && _timeline.Now >= exitGraceDeadline) || (knownPid is not null && _timeline.Now >= started + 2.0)))
            {
                var existing = appWindows[0];
                Log.Info($"{app.Name}（{_timeline.Now - started:0.1}s）沿用已有窗口 {existing.Label}，放上{_screenName}");
                State = State with { Work = new DeskWork(DeskWorkKind.Placing, existing.Label) };
                if (_behavior == ScreenBehavior.Computer)
                    await PullToComputerAsync(existing);
                else
                    await PlaceAsync(existing);
                return;
            }
            if (!running && _timeline.Now >= exitGraceDeadline && appWindows.Count == 0)
            {
                Log.Warn($"{app.Name} 打开后已退出");
                State = State with { Notice = new DeskNotice.OpenFailed(app.Name) };
                return;
            }
            if (_timeline.Now >= deadline)
            {
                Log.Warn($"{app.Name} 打开超时无窗口");
                State = State with { Notice = new DeskNotice.NoWindow(app.Name) };
                return;
            }
            await _timeline.SleepAsync(WindowPoll);
        }
    }

    private void WatchQuit(RunningApp app)
    {
        Watch(WatchedKey.ForQuit(app.Pid), async ct =>
        {
            try
            {
                var answer = await AwaitAnswerAsync(app.Pid, QuitWait, async () => !await _apps.IsRunningAsync(app.Pid), ct);
                if (ct.IsCancellationRequested) return;
                switch (answer)
                {
                    case AnswerKind.Done:
                        Log.Info($"{app.App.Name} 已退出");
                        Gone?.Invoke(new DeskGone.App(app.Pid));
                        break;
                    case AnswerKind.Canceled:
                        Log.Info($"{app.App.Name} 没退出（用户取消）");
                        break;
                    case AnswerKind.Stalled:
                        Log.Warn($"{app.App.Name} 退出超时");
                        var stalled = new DeskNotice.QuitStalled(app.App.Name, app.Pid);
                        State = State with { Notice = stalled };
                        RemoveQuitting(app.Pid);
                        double until = _timeline.Now + StalledFollow;
                        while (!ct.IsCancellationRequested && _timeline.Now < until && await _apps.IsRunningAsync(app.Pid))
                            await _timeline.SleepAsync(StalledPoll, ct);
                        if (!ct.IsCancellationRequested && !await _apps.IsRunningAsync(app.Pid))
                        {
                            Log.Info($"{app.App.Name} 后来自己退出了");
                            Gone?.Invoke(new DeskGone.App(app.Pid));
                            if (Equals(State.Notice, stalled))
                                State = State with { Notice = null };
                        }
                        break;
                }
            }
            finally
            {
                RemoveQuitting(app.Pid);
            }
        });
    }

    private void WatchClose(WindowInfo window, bool hides)
    {
        Watch(WatchedKey.ForClose(window.Key), async ct =>
        {
            try
            {
                var answer = await AwaitAnswerAsync(window.Key.Pid, CloseWait, async () =>
                {
                    if (!await _windows.ExistsAsync(window.Key)) return true;
                    return hides && !await _windows.IsShownAsync(window.Key);
                }, ct);
                if (ct.IsCancellationRequested) return;
                switch (answer)
                {
                    case AnswerKind.Done:
                        Log.Info($"{window.Label} 已关闭");
                        Gone?.Invoke(new DeskGone.Window(window.Key));
                        break;
                    case AnswerKind.Canceled:
                        Log.Info($"{window.Label} 没关（用户取消）");
                        break;
                    case AnswerKind.Stalled:
                        Log.Warn($"{window.Label} 关闭超时");
                        State = State with { Notice = new DeskNotice.WindowUnresponsive(window.AppName) };
                        break;
                }
            }
            finally
            {
                RemoveClosing(window.Key);
            }
        });
    }

    private void Watch(WatchedKey key, Func<CancellationToken, Task> body)
    {
        if (_watches.TryGetValue(key, out var prev)) prev.Cancel();
        var cts = new CancellationTokenSource();
        _watches[key] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await body(cts.Token);
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                    _watches.Remove(key);
            }
        });
    }

    private void Unwatch(WatchedKey key)
    {
        if (_watches.Remove(key, out var cts)) cts.Cancel();
    }

    private enum AnswerKind { Done, Canceled, Stalled }

    private async Task<AnswerKind> AwaitAnswerAsync(int pid, double seconds, Func<Task<bool>> done, CancellationToken ct)
    {
        double deadline = _timeline.Now + seconds;
        WindowKey? asked = null;
        double? answeredAt = null;
        while (!ct.IsCancellationRequested)
        {
            if (await done()) return AnswerKind.Done;
            if (await _windows.AskingWindowAsync(pid) is { } asking)
            {
                answeredAt = null;
                if (asking.Key != asked)
                {
                    asked = asking.Key;
                    Log.Info($"{asking.AppName} 在询问，把 {asking.Label} 放上手机屏");
                    await PlaceAskingAsync(asking, ct);
                }
            }
            else if (asked is not null)
            {
                answeredAt ??= _timeline.Now;
                if (_timeline.Now - answeredAt.Value >= AnswerGrace) return AnswerKind.Canceled;
            }
            else if (_timeline.Now >= deadline)
            {
                return AnswerKind.Stalled;
            }
            await _timeline.SleepAsync(WatchPoll, ct);
        }
        return AnswerKind.Canceled;
    }

    private async Task PlaceAskingAsync(WindowInfo window, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        await _lane.Run(async () =>
        {
            State = State with { Notice = null, Work = new DeskWork(DeskWorkKind.Placing, window.Label) };
            await PlaceAsync(window, zoom: false);
            State = State with { Work = null };
        });
    }

    private async Task<bool> ReturnToOriginalAsync(Placement p, bool restoringState = true)
    {
        if (await PutBackAsync(p.Record, restoringState) is { } left)
        {
            p.Record = left;
            SetPlacement(p);
            return false;
        }
        SetPlacement(null);
        State = State with { ListVisible = true };
        return true;
    }

    private async Task<PhoneRecord?> PutBackAsync(PhoneRecord record, bool restoringState = true)
    {
        int zoom = record.Zoom;
        if (zoom != 0)
        {
            if (!await _windows.ZoomAsync(record.Window, -zoom))
                return await StuckOrGoneAsync(record, "缩不回字号");
            record.Zoom = 0;
            WriteRecord(record);
        }
        var got = await _windows.MoveAsync(record.Window, record.Original);
        if (got is null)
            return await StuckOrGoneAsync(record, "挪不动");

        if (restoringState && record.WasFullScreen)
        {
            await _timeline.SleepAsync(SettleDelay);
            await _windows.SetFullScreenAsync(record.Window, true);
        }
        else if (restoringState && record.WasMinimized)
        {
            await _windows.SetMinimizedAsync(record.Window, true);
        }
        SettleRecord();
        Log.Info($"{record.Label} 已放回原位");
        return null;
    }

    private async Task PullToComputerAsync(WindowInfo window, Func<WindowKey, PhoneRecord?>? transferFromOtherScreen = null)
    {
        double started = _timeline.Now;
        if (ReadyDisplay($"把 {window.Label} 挪到电脑屏") is not { } id || _displays.WindowAreaOf(id) is not { } area)
        {
            State = State with { Notice = new DeskNotice.ScreenNotReady() };
            return;
        }
        if (await ReachAsync(window) is not { } look) return;

        PhoneRecord? existingRecord = _brought.TryGetValue(window.Key, out var prevBrought) ? prevBrought : null;
        PhoneRecord? transferredRecord = existingRecord is null ? transferFromOtherScreen?.Invoke(window.Key) : null;
        PhoneRecord record = existingRecord
            ?? (transferredRecord is not null
                ? transferredRecord with { Window = window.Key }
                : new PhoneRecord
                {
                    Window = window.Key,
                    App = window.AppName,
                    Label = window.Label,
                    Original = OriginalFrame(look.Frame, id),
                    WasFullScreen = look.FullScreen,
                    WasMinimized = look.Minimized,
                    Zoom = 0,
                    AppId = window.AppId,
                    Title = window.Title
                });

        _brought[window.Key] = record;
        if (!SaveBrought())
        {
            if (existingRecord is null) _brought.Remove(window.Key);
            State = State with { Notice = new DeskNotice.RecordUnwritable() };
            return;
        }
        SyncBroughtState();

        if (look.Minimized) await _windows.SetMinimizedAsync(window.Key, false);
        if (look.FullScreen)
        {
            await _windows.SetFullScreenAsync(window.Key, false);
            if (existingRecord is null && transferredRecord is null && (await _windows.LookAsync(window.Key))?.Frame is { } windowed)
            {
                record.Original = OriginalFrame(windowed, id);
                SaveBrought();
            }
        }

        var currentFrame = (await _windows.LookAsync(window.Key))?.Frame ?? look.Frame;
        double targetW = Math.Clamp(currentFrame.Width > 10 ? currentFrame.Width : area.Width * 0.8, 320, area.Width);
        double targetH = Math.Clamp(currentFrame.Height > 10 ? currentFrame.Height : area.Height * 0.8, 240, area.Height);
        var targetRect = new RectD(
            Math.Round(area.X + Math.Max(0, (area.Width - targetW) / 2.0)),
            Math.Round(area.Y + Math.Max(0, (area.Height - targetH) / 2.0)),
            Math.Round(targetW),
            Math.Round(targetH));

        var bounds = _displays.BoundsOf(id) ?? area;
        var got = await _windows.MoveAsync(window.Key, targetRect);
        if (got is not null && !bounds.Contains(got.Value.Center) && _displays.MainArea() is { } mainArea)
        {
            var relay = new RectD(
                mainArea.X + Math.Max(0, (mainArea.Width - Math.Min(mainArea.Width, targetW)) / 2),
                mainArea.Y + Math.Max(0, (mainArea.Height - Math.Min(mainArea.Height, targetH)) / 2),
                Math.Min(mainArea.Width, targetW),
                Math.Min(mainArea.Height, targetH));
            _ = await _windows.MoveAsync(window.Key, relay);
            got = await _windows.MoveAsync(window.Key, targetRect);
        }

        if (got is null || !bounds.Contains(got.Value.Center))
        {
            var notice = await UnreachableAsync(window);
            Log.Warn($"{window.Label} 挪不到电脑屏「{_screenName}」，放回原位");
            await PutBackBroughtAsync(window.Key);
            State = State with { Notice = notice };
            return;
        }

        await _windows.FocusAsync(window.Key);
        Log.Info($"{window.Label} 已移至电脑屏「{_screenName}」（{_timeline.Now - started:0.0}s）：原位 {record.Original.Short}，当前 {got.Value.Short}");
    }

    private async Task PutBackBroughtAsync(WindowKey key)
    {
        if (!_brought.TryGetValue(key, out var record)) return;
        if ((await _windows.LookAsync(key))?.FullScreen == true)
            await _windows.SetFullScreenAsync(key, false);

        if (await PutBackAsync(record) is { } left)
        {
            _brought[key] = left;
            State = State with { Notice = new DeskNotice.WindowUnresponsive(left.App) };
        }
        else
        {
            _brought.Remove(key);
        }
        SaveBrought();
        SyncBroughtState();
    }

    private async Task ReturnAllBroughtAsync()
    {
        if (_brought.Count == 0) return;
        foreach (var key in _brought.Keys.ToList())
            await PutBackBroughtAsync(key);
    }

    private async Task PruneMissingBroughtAsync()
    {
        bool changed = false;
        foreach (var key in _brought.Keys.ToList())
        {
            if (!await _windows.ExistsAsync(key))
            {
                _brought.Remove(key);
                changed = true;
            }
        }
        if (changed)
        {
            SaveBrought();
            SyncBroughtState();
        }
    }

    private bool SaveBrought()
    {
        try
        {
            if (_brought.Count == 0)
                _broughtFile.Delete();
            else
                _broughtFile.Save(_brought.Values.ToList());
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"电脑屏还原记录保存失败：{ex.Message}");
            return false;
        }
    }

    private void SyncBroughtState()
    {
        State = State with { Brought = new HashSet<WindowKey>(_brought.Keys) };
    }

    private async Task<PhoneRecord?> PutBackLeftoverAsync()
    {
        if (_brought.Count == 0 && _broughtFile.LoadOrQuarantine() is { Count: > 0 } savedBrought)
        {
            foreach (var rec in savedBrought)
                _brought[rec.Window] = rec;
            await ReturnAllBroughtAsync();
        }
        return _placement is null && _ledger.Pending() is { } record
            ? await RecoverRecordAsync(record)
            : null;
    }

    private async Task<PhoneRecord?> RecoverRecordAsync(PhoneRecord record)
    {
        if (!await _windows.ExistsAsync(record.Window))
        {
            Log.Info($"还原记录里的 {record.Label} 已经不在了，等它回来再还");
            await LeaveBehindAsync(record);
            return null;
        }
        Log.Info($"按还原记录把 {record.Label} 放回 {record.Original.Short}");
        return await PutBackAsync(record);
    }

    private async Task<PhoneRecord?> StuckOrGoneAsync(PhoneRecord record, string what)
    {
        if (await _windows.ExistsAsync(record.Window))
        {
            Log.Warn($"{record.Label} 还在，但{what}，还原记录留着，稍后再放回");
            return record;
        }
        Log.Info($"{record.Label} 已经关了，等它回来再还");
        await LeaveBehindAsync(record);
        return null;
    }

    private async Task LeaveBehindAsync(PhoneRecord record)
    {
        if (record.Zoom != 0)
        {
            switch (ZoomBook.ScopeOf(record.AppId))
            {
                case ZoomScope.App:
                    if (!await _apps.IsRunningAsync(record.Window.Pid) || await RepayAppZoomAsync(record))
                        record.Zoom = 0;
                    break;
                case ZoomScope.Workspace:
                    State = State with { Notice = new DeskNotice.ZoomLeftBehind(record.App, record.Zoom) };
                    break;
            }
        }
        _leftBehind.Add(new LeftBehindEntry { Record = record, Since = DateTimeOffset.Now });
        _leftBehind.Prune(DateTimeOffset.Now);
        SaveLeftBehind();
        SettleRecord();
    }

    private async Task<bool> RepayAppZoomAsync(PhoneRecord record)
    {
        var other = (await _windows.ShownWindowsAsync(record.Window.Pid))
            .FirstOrDefault(w => w.Key != record.Window);
        if (other is null) return false;
        return await _windows.ZoomAsync(other.Key, -record.Zoom);
    }

    private async Task RepayAppWideZoomAsync()
    {
        bool changed = false;
        for (int i = 0; i < _leftBehind.Entries.Count; i++)
        {
            var e = _leftBehind.Entries[i];
            if (e.Scope != ZoomScope.App || e.Record.Zoom == 0) continue;
            if (await _apps.IsRunningAsync(e.Record.Window.Pid))
            {
                if ((await _windows.ShownWindowsAsync(e.Record.Window.Pid)).Count == 0 || !await RepayAppZoomAsync(e.Record))
                    continue;
            }
            e.Record.Zoom = 0;
            changed = true;
        }
        if (changed) SaveLeftBehind();
    }

    private async Task<bool> PlaceOnItsOwnAsync(WindowInfo window)
    {
        if (State.Work is not null) return false;
        State = State with { Notice = null, Work = new DeskWork(DeskWorkKind.Placing, window.Label) };
        await PlaceAsync(window);
        if (State.Work?.Kind is DeskWorkKind.Placing or DeskWorkKind.Zooming)
            State = State with { Work = null };
        return true;
    }

    private async Task FollowReplacementAsync()
    {
        if (_replaced is not { } r) return;
        if (_placement is not null || _timeline.Now >= r.Until)
        {
            _replaced = null;
            return;
        }
        if (_display is null) return;
        var next = (await _windows.ShownWindowsAsync(r.Pid)).FirstOrDefault(w => !r.Known.Contains(w.Key));
        if (next is null) return;
        Log.Info($"{next.AppName} 换了个窗口，接着放上手机屏：{next.Label}");
        if (await PlaceOnItsOwnAsync(next)) _replaced = null;
    }

    private async Task TakeBackReturnedAsync()
    {
        _leftBehind.Prune(DateTimeOffset.Now);
        await RepayAppWideZoomAsync();
        var skip = new HashSet<WindowKey>();
        if (_placement is { } pl) skip.Add(pl.Window.Key);
        if (_ledger.Pending() is { } pend) skip.Add(pend.Window);

        var appIds = _leftBehind.Entries.Select(e => e.Record.AppId).Where(id => id is not null).Distinct().ToList();
        foreach (var appId in appIds)
        {
            foreach (int pid in await _apps.PidsAsync(appId!))
            {
                var frames = await _windows.FramesAsync(pid);
                var candidates = frames.Where(kv => !skip.Contains(kv.Key) && _leftBehind.Awaits(appId!, kv.Value.Size)).ToDictionary(kv => kv.Key, kv => kv.Value);
                if (candidates.Count == 0) continue;
                foreach (var win in await _windows.ShownWindowsAsync(pid))
                {
                    if (!candidates.TryGetValue(win.Key, out var frame)) continue;
                    if (_leftBehind.Match(win, frame) is not { } idx) continue;
                    await TakeBackAsync(idx, win, frame);
                    return;
                }
            }
        }
    }

    private async Task TakeBackAsync(int index, WindowInfo window, RectD frame)
    {
        if (_wanted && _placement is null && _display is { } d && _displays.BoundsOf(d.Id)?.Contains(frame.Center) == true)
        {
            await PlaceOnItsOwnAsync(window);
            return;
        }
        var owed = _leftBehind.Entries[index].ReturnedAs(window.Key);
        if (owed.Zoom != 0)
        {
            if (!await _windows.ZoomAsync(window.Key, -owed.Zoom)) return;
            _leftBehind.Entries[index].Record.Zoom = 0;
            SaveLeftBehind();
        }
        if (await _windows.MoveAsync(window.Key, owed.Original) is not { } got) return;
        _leftBehind.RemoveAt(index);
        SaveLeftBehind();
        Log.Info($"{window.Label} 带着手机屏上的样子回来了，放回原位 {got.Short}");
    }

    private async Task<bool> ChangeZoomAsync(int delta)
    {
        if (_placement is not { } p) return false;
        int beforeZoom = p.Record.Zoom;
        int target = (beforeZoom + delta).Clamp(ZoomBook.MinSteps, ZoomBook.MaxSteps);
        if (target == beforeZoom) return false;

        p.Record.Zoom = target;
        if (!WriteRecord(p.Record))
        {
            p.Record.Zoom = beforeZoom;
            State = State with { Notice = new DeskNotice.RecordUnwritable() };
            return false;
        }
        if (!await _windows.ZoomAsync(p.Window.Key, target - beforeZoom))
        {
            p.Record.Zoom = beforeZoom;
            WriteRecord(p.Record);
            State = State with { Notice = await UnreachableAsync(p.Window) };
            return false;
        }
        SetPlacement(p);
        Log.Info($"{p.Window.Label} 字号 {(target > 0 ? "+" : "")}{target} 级");
        await _timeline.SleepAsync(SettleDelay);
        if (!State.ListVisible) await RefillAsync("字号改了");
        return true;
    }

    private void Begin(DeskWork work)
    {
        State = State with { Notice = null, Work = work };
    }

    private void SetPlacement(Placement? p)
    {
        var oldKey = _placement?.Window.Key;
        _placement = p;
        State = State with
        {
            Placed = p is null ? null : new PlacedWindow(p.Window, p.Record.Zoom),
            CloseButton = p?.Window.Key != oldKey ? null : State.CloseButton
        };
    }

    private void RemoveQuitting(int pid)
    {
        var set = new HashSet<int>(State.Quitting);
        set.Remove(pid);
        State = State with { Quitting = set };
    }

    private void RemoveClosing(WindowKey key)
    {
        var set = new HashSet<WindowKey>(State.Closing);
        set.Remove(key);
        State = State with { Closing = set };
    }

    private uint? ReadyDisplay(string forWhat)
    {
        if (_display is not { } d)
        {
            Log.Warn($"{forWhat}：没有手机屏（{State.ScreenPhase}）");
            return null;
        }
        if (_displays.WindowAreaOf(d.Id) is null)
        {
            Log.Warn($"{forWhat}：读不到手机屏 #{d.Id} 的可放区域");
            return null;
        }
        return d.Id;
    }

    private async Task<WindowLook?> ReachAsync(WindowInfo window)
    {
        if (await _windows.LookAsync(window.Key) is { } look) return look;
        State = State with { Notice = await UnreachableAsync(window) };
        return null;
    }

    private async Task<DeskNotice> UnreachableAsync(WindowInfo window) =>
        await _windows.ExistsAsync(window.Key)
            ? new DeskNotice.WindowUnresponsive(window.AppName)
            : new DeskNotice.WindowGone(window.AppName);

    private bool WriteRecord(PhoneRecord record)
    {
        try
        {
            _ledger.Record(record);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"还原记录写不进去：{ex.Message}");
            return false;
        }
    }

    private void SettleRecord()
    {
        try { _ledger.Settle(); } catch (Exception ex) { Log.Error($"还原记录删不掉：{ex.Message}"); }
    }

    private void SaveLeftBehind()
    {
        try { _leftBehindFile.Save(_leftBehind); } catch (Exception ex) { Log.Error($"left-behind.json 保存失败：{ex.Message}"); }
    }

    private void SaveZoomBook()
    {
        try { _zoomFile.Save(_zoomBook); } catch (Exception ex) { Log.Error($"zoom.json 保存失败：{ex.Message}"); }
    }

    private sealed record Surroundings(Dictionary<WindowKey, RectD> Windows, IReadOnlyList<OtherDisplay> Others);
    private sealed record Retiring(uint Id, Surroundings Before, bool Permanently);

    private sealed class Placement
    {
        public WindowInfo Window { get; }
        public PhoneRecord Record { get; set; }
        public int Width { get; set; }
        public RectD? Settled { get; set; }
        public HashSet<WindowKey> Known { get; }
        public bool Asking { get; }

        public Placement(WindowInfo window, PhoneRecord record, int width, HashSet<WindowKey> known, bool asking)
        {
            Window = window;
            Record = record;
            Width = width;
            Known = known;
            Asking = asking;
        }
    }

    private readonly record struct WatchedKey(bool IsQuit, int Pid, WindowKey Window)
    {
        public static WatchedKey ForQuit(int pid) => new(true, pid, default);
        public static WatchedKey ForClose(WindowKey key) => new(false, 0, key);
    }
}
