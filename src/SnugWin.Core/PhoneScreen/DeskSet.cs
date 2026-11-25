using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SnugWin.Core.Config;
using SnugWin.Core.Persistence;
using SnugWin.Core.Support;

namespace SnugWin.Core.PhoneScreen;

public sealed class DeskSet
{
    private readonly IPhoneDisplays _displays;
    private readonly IWindowControl _windows;
    private readonly IAppControl _apps;
    private readonly SnugPaths _paths;
    private readonly ITimeline _timeline;
    private readonly SerialLane _lane = new();
    private readonly JsonFileStore<ZoomBook> _zoomFile;
    private readonly ZoomBook _zoomBook;
    private readonly JsonFileStore<LeftBehindBook> _leftBehindFile;
    private readonly LeftBehindBook _leftBehind;

    private readonly Dictionary<string, PhoneDesk> _desks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScreenWish> _wishes = new();
    private bool _remote;
    private int _defaultZoom;

    public event Action? StateChanged;
    public event Action<DeskGone>? Gone;
    public event Action? TopologyChanging;

    public IReadOnlyList<ScreenWish> Wishes => _wishes;

    public DeskSet(
        IPhoneDisplays displays,
        IWindowControl windows,
        IAppControl apps,
        SnugPaths paths,
        ITimeline? timeline = null,
        int defaultZoom = 2)
    {
        _displays = displays;
        _windows = windows;
        _apps = apps;
        _paths = paths;
        _timeline = timeline ?? new SystemTimeline();
        _defaultZoom = defaultZoom;
        _zoomFile = new JsonFileStore<ZoomBook>(paths.ZoomBookFile);
        _zoomBook = _zoomFile.LoadOrQuarantine() ?? new ZoomBook();
        _leftBehindFile = new JsonFileStore<LeftBehindBook>(paths.LeftBehindFile);
        _leftBehind = _leftBehindFile.LoadOrQuarantine() ?? new LeftBehindBook();
    }

    public PhoneDeskState? StateOf(string screenId) =>
        _desks.TryGetValue(screenId, out var desk) ? desk.State : null;

    public IReadOnlyDictionary<string, PhoneDeskState> AllStates =>
        _desks.ToDictionary(kv => kv.Key, kv => kv.Value.State, StringComparer.OrdinalIgnoreCase);

    public bool IsAnyReady => _desks.Values.Any(d => d.State.ScreenPhase == PhoneScreenPhase.Ready);
    public bool IsAnyTouchReady => _desks.Values.Any(d => d.Behavior == ScreenBehavior.TouchScreen && d.State.ScreenPhase == PhoneScreenPhase.Ready);
    public bool HasAnyDismissedTouch => _desks.Values.Any(d => d.Behavior == ScreenBehavior.TouchScreen && d.State.Dismissed);

    public PhoneDesk? DeskOfDisplay(uint displayId) =>
        _desks.Values.FirstOrDefault(d => d.State.DisplayId == displayId);

    public PhoneDesk? DeskForScreen(string? screenId)
    {
        if (!string.IsNullOrWhiteSpace(screenId) && _desks.TryGetValue(screenId, out var exact))
            return exact;
        return _desks.Values.FirstOrDefault(d => d.Behavior == ScreenBehavior.TouchScreen) ?? _desks.Values.FirstOrDefault();
    }

    public void SetWishes(IReadOnlyList<ScreenWish> nextWishes)
    {
        _wishes.Clear();
        _wishes.AddRange(nextWishes);

        var nextById = nextWishes.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        var toRemove = _desks.Keys.Where(id => !nextById.ContainsKey(id)).ToList();

        foreach (string id in toRemove)
        {
            if (_desks.TryGetValue(id, out var dead))
            {
                _desks.Remove(id);
                _ = dead.ShutdownAsync();
            }
        }

        foreach (var wish in nextWishes)
        {
            if (_desks.TryGetValue(wish.Id, out var existing))
            {
                existing.UpdateWish(wish.Name, wish.Fit, wish.Behavior, wish.Prepared, wish.StaysOnDismiss);
                existing.SetWanted(true);
            }
            else
            {
                var desk = new PhoneDesk(
                    _displays,
                    _windows,
                    _apps,
                    _paths,
                    _timeline,
                    _defaultZoom,
                    wish.Fit,
                    screenId: wish.Id,
                    screenName: wish.Name,
                    behavior: wish.Behavior,
                    sharedLane: _lane,
                    sharedZoomBook: _zoomBook,
                    sharedZoomFile: _zoomFile,
                    sharedLeftBehind: _leftBehind,
                    sharedLeftBehindFile: _leftBehindFile,
                    prepared: wish.Prepared,
                    staysOnDismiss: wish.StaysOnDismiss);
                desk.StateChanged += _ => StateChanged?.Invoke();
                desk.Gone += g => Gone?.Invoke(g);
                desk.TopologyChanging += () => TopologyChanging?.Invoke();
                desk.SetRemote(_remote);
                desk.SetWanted(true);
                _desks[wish.Id] = desk;
            }
        }

        StateChanged?.Invoke();
    }

    public void SetRemote(bool on)
    {
        bool wasRemote = _remote;
        _remote = on;
        foreach (var desk in _desks.Values)
        {
            if (!wasRemote && on)
                desk.ResetCircuitBreaker();
            desk.SetRemote(on);
        }
        if (wasRemote && !on)
        {
            foreach (var desk in _desks.Values)
                desk.DismissNotice();
            _ = _lane.Run(EndSessionAsync);
            SettleDisplays();
        }
    }

    private async Task EndSessionAsync()
    {
        await Task.Yield();
        if (_remote) return;
        foreach (var wish in _wishes.ToList())
        {
            if (_remote) return;
            if (!wish.Prepared || wish.IsMain) continue;
            if (!_desks.TryGetValue(wish.Id, out var desk)) continue;
            await desk.EndSessionKeepScreenAsync();
        }
        foreach (var desk in _desks.Values)
            desk.DismissNotice();
    }

    public void ResetCircuitBreaker()
    {
        foreach (var desk in _desks.Values)
            desk.ResetCircuitBreaker();
    }

    public void SetDefaultZoom(int steps)
    {
        _defaultZoom = steps;
        foreach (var desk in _desks.Values)
            desk.SetDefaultZoom(steps);
    }

    public void Recover()
    {
        foreach (var desk in _desks.Values)
            desk.Recover();
    }

    public void VerifyPlacement()
    {
        foreach (var desk in _desks.Values)
            desk.VerifyPlacement();
    }

    public void CheckReturns()
    {
        foreach (var desk in _desks.Values)
            desk.CheckReturns();
    }

    public void SettleDisplays()
    {
        foreach (var desk in _desks.Values)
            desk.SettleDisplays();
    }

    public void DisplaysChanged()
    {
        foreach (var desk in _desks.Values)
            desk.DisplaysChanged();
    }

    public void ShowList(string? screenId = null) => DeskForScreen(screenId)?.ShowList();
    public void ReturnToPlaced(string? screenId = null) => DeskForScreen(screenId)?.ReturnToPlaced();
    public void PutBack(string? screenId = null) => DeskForScreen(screenId)?.PutBack();
    public void PutBackBrought(string screenId, WindowKey key) => DeskForScreen(screenId)?.PutBackBrought(key);
    public void Zoom(string screenId, int delta) => DeskForScreen(screenId)?.Zoom(delta);
    public void Open(string screenId, AppInfo app) => DeskForScreen(screenId)?.Open(app);
    public void NewWindow(string screenId, RunningApp app) => DeskForScreen(screenId)?.NewWindow(app);
    public void Close(string screenId, WindowInfo window) => DeskForScreen(screenId)?.Close(window);
    public void DismissScreen(string screenId) => DeskForScreen(screenId)?.DismissScreen();
    public void DismissNotice(string screenId) => DeskForScreen(screenId)?.DismissNotice();

    public void Focus(string screenId, WindowInfo window)
    {
        if (!_desks.TryGetValue(screenId, out var desk)) return;
        if (desk.State.Quitting.Contains(window.Key.Pid) || desk.State.Closing.Contains(window.Key))
        {
            Log.Info($"{window.AppName} 正在退出或关窗口，不切换到 {window.Label}");
            return;
        }
        desk.DismissNotice();
        _ = _lane.Run(async () =>
        {
            if (!await desk.FocusAsync(window))
            {
                Log.Info($"切换到 {window.Label}：已不在屏（{screenId}）上，改成放过来");
                Pull(screenId, window);
            }
        });
    }

    public void ReenterAllDismissedTouch()
    {
        foreach (var desk in _desks.Values.Where(d => d.Behavior == ScreenBehavior.TouchScreen && d.State.Dismissed))
            desk.ReenterScreen();
    }

    public void ReenterScreen(string? screenId = null)
    {
        if (screenId is not null)
        {
            DeskForScreen(screenId)?.ReenterScreen();
            return;
        }
        ReenterAllDismissedTouch();
    }

    public void Pull(string targetScreenId, WindowInfo window)
    {
        var target = DeskForScreen(targetScreenId);
        if (target is null) return;

        target.Pull(window, key =>
        {
            foreach (var other in _desks.Values)
            {
                if (ReferenceEquals(other, target)) continue;
                if (other.PlacedWindowKey == key)
                    return other.DetachPlacementForTransfer();
                if (other.DetachBroughtForTransfer(key) is { } broughtRecord)
                    return broughtRecord;
            }
            return null;
        });
    }

    public async void Quit(string screenId, RunningApp app)
    {
        foreach (var desk in _desks.Values)
        {
            if (desk.PlacedWindowKey is { } k && k.Pid == app.Pid && !string.Equals(desk.ScreenId, screenId, StringComparison.OrdinalIgnoreCase))
                await desk.ReturnWindowIfPlacedAsync(app.Pid).ConfigureAwait(false);
            await desk.ReturnBroughtIfPidAsync(app.Pid).ConfigureAwait(false);
        }
        DeskForScreen(screenId)?.Quit(app);
    }

    public void ForceQuit(string screenId, int pid, string appName) =>
        DeskForScreen(screenId)?.ForceQuit(pid, appName);

    public void ClosePlaced(ulong windowHandle)
    {
        foreach (var desk in _desks.Values)
        {
            if (desk.PlacedWindowKey?.Handle == windowHandle)
            {
                desk.ClosePlaced(windowHandle);
                return;
            }
        }
    }

    public async Task ShutdownAsync()
    {
        var list = _desks.Values.ToList();
        _desks.Clear();
        _wishes.Clear();
        foreach (var desk in list)
            await desk.ShutdownAsync().ConfigureAwait(false);
    }

    internal async Task IdleAsync()
    {
        await _lane.DrainAsync();
        foreach (var desk in _desks.Values.ToList())
            await desk.IdleAsync();
        await _lane.DrainAsync();
    }
}
