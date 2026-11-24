using System.Collections.Concurrent;
using System.Diagnostics;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;

namespace SnugWin.Windows.Display;

// A null state means the pipe has been negotiated as write-only legacy Add.
// On v2 Notify is a full replacement, including disabled monitors.
internal interface IVirtualDisplayState
{
    Task<IReadOnlyList<VddMonitor>?> ReadStateAsync(CancellationToken ct);
}

public sealed class PhoneDisplayService : IPhoneDisplays, IAsyncDisposable
{
    private sealed record Screen(CherryMonitor Target, ScreenFit Fit, string Name, DateTimeOffset OnlineAt);
    private sealed record UnconfirmedRequest(VddMonitor Monitor, HashSet<Guid> PriorKnownContainers);

    private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(15);
    private readonly IVirtualDisplayIpc _ipc;
    private readonly DisplayOwnership _ownership;
    private readonly Func<bool> _isPipeAvailable;
    private readonly PhoneDisplayTopology _topology;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly Dictionary<uint, UnconfirmedRequest> _unconfirmed = [];
    private readonly ConcurrentDictionary<uint, Screen> _ready = new();
    private readonly ConcurrentDictionary<uint, CherryMonitor> _retiring = new();
    private readonly HashSet<string> _startupPhysicalDevices;

    public PhoneDisplayService(VirtualDisplayIpcClient? ipc = null, SnugPaths? paths = null)
        : this(ipc ?? new VirtualDisplayIpcClient(), paths, () => VirtualDisplayIpcClient.IsDriverPipeAvailable(), new()) { }

    internal PhoneDisplayService(IVirtualDisplayIpc ipc, SnugPaths? paths, Func<bool> isPipeAvailable,
        PhoneDisplayTopology topology, TimeSpan? timeout = null)
    {
        _ipc = ipc;
        _ownership = new(paths?.OwnedDisplaysFile);
        _isPipeAvailable = isPipeAvailable;
        _topology = topology;
        _timeout = timeout ?? OnlineTimeout;
        _startupPhysicalDevices = _topology.Active()
            .Where(m => m.Cherry is null && !IsVirtualDisplayAdapter(m))
            .Select(m => m.DeviceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task CleanupOrphansAsync(CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_isPipeAvailable()) return;
            foreach (var receipt in _ownership.All())
            {
                if (!_ready.ContainsKey(receipt.DriverId))
                    await RemoveOwnedAsync(receipt, ct).ConfigureAwait(false);
            }
            if (_unconfirmed.Count > 0 || _ownership.PendingAll().Count > 0)
            {
                var state = await DriverStateAsync(ct).ConfigureAwait(false);
                var inventory = _topology.Inspect();
                if (_unconfirmed.Count > 0)
                    await ReconcileUnconfirmedAsync(inventory, state, ct).ConfigureAwait(false);
                await ReconcilePersistedPendingAsync(inventory, state, ct).ConfigureAwait(false);
            }
        }
        finally { _changes.Release(); }
    }

    public Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default) => CreateAsync(fit, null, ct);

    public async Task<uint> CreateAsync(ScreenFit fit, string? displayName, CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_isPipeAvailable()) throw Failed("未检测到 virtual-display-rs 驱动管道，未更改显示配置");
            var (state, connectionId) = await DriverStateSessionAsync(ct).ConfigureAwait(false);
            var before = await ReadBaselineAsync(ct).ConfigureAwait(false);
            if (await ReconcileUnconfirmedAsync(before, state, ct).ConfigureAwait(false))
            {
                (state, connectionId) = await DriverStateSessionAsync(ct).ConfigureAwait(false);
                before = await ReadBaselineAsync(ct).ConfigureAwait(false);
            }
            var baselineNonCherry = SnapshotNonCherryDisplays();
            var occupied = before.OccupiedIds
                .Concat(_ready.Keys)
                .Concat(_retiring.Keys)
                .Concat(_unconfirmed.Keys)
                .Concat(_topology.Active().Where(m => m.Cherry is not null).Select(m => m.Cherry!.DriverId))
                .ToHashSet();
            occupied.UnionWith(state is null ? _topology.LegacyProfileIds() : state.Select(m => m.Id));
            uint id = PhoneScreenSpec.CandidateIds.FirstOrDefault(i => !occupied.Contains(i), uint.MaxValue);
            if (id == uint.MaxValue) throw Failed("没有可安全使用的 Cherry 显示器编号");
            string name = string.IsNullOrWhiteSpace(displayName) ? PhoneScreenSpec.Name : displayName;
            var monitor = new VddMonitor(id, name, true, BuildModes(fit));
            Log.Info($"请求创建 Cherry 手机屏 #{id}（occupied=[{string.Join(",", occupied.OrderBy(x => x))}], legacy={_ipc.IsLegacyProtocol}）");
            ct.ThrowIfCancellationRequested();
            await SubmitAsync(monitor, state, connectionId, replace: false, before.KnownContainers, ct).ConfigureAwait(false);
            await CompleteArrivalAsync(monitor, fit, before, baselineNonCherry, ct).ConfigureAwait(false);
            if (_unconfirmed.Count > 0)
                await ReconcileUnconfirmedAsync(_topology.Inspect(), await DriverStateAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return id;
        }
        finally { _changes.Release(); }
    }

    private IReadOnlyList<(uint StableId, bool IsPrimary)> SnapshotNonCherryDisplays() =>
        _topology.Active()
            .Where(m => m.Cherry is null)
            .Select(m => (m.StableId, m.IsPrimary))
            .ToArray();

    private static readonly TimeSpan MinModesetInterval = TimeSpan.FromSeconds(2.0);
    private DateTimeOffset _lastModesetAt = DateTimeOffset.MinValue;

    private async Task ThrottleModesetAsync(CancellationToken ct)
    {
        var elapsed = DateTimeOffset.Now - _lastModesetAt;
        if (elapsed < MinModesetInterval)
        {
            await Task.Delay(MinModesetInterval - elapsed, ct).ConfigureAwait(false);
        }
        _lastModesetAt = DateTimeOffset.Now;
    }

    private void EnsureNonCherryIntact(IReadOnlyList<(uint StableId, bool IsPrimary)> baseline)
    {
        if (baseline.Count == 0) return;
        var active = _topology.Active();
        var activeIds = active.Where(m => m.Cherry is null).Select(m => m.StableId).ToHashSet();
        foreach (var (stableId, wasPrimary) in baseline)
        {
            if (!activeIds.Contains(stableId))
                throw Failed(wasPrimary
                    ? "建屏过程中原主显示器脱离活动桌面，已中止以保护桌面输出"
                    : "建屏过程中原有非 Cherry 显示器脱离活动桌面，已中止", isSafetyAbort: true);
        }
    }

    private async Task<CherryInventory> ReadBaselineAsync(CancellationToken ct)
    {
        var known = new HashSet<Guid>();
        var occupied = new HashSet<uint>();
        CherryInventory? Sample()
        {
            var scan = _topology.Inspect();
            known.UnionWith(scan.KnownContainers);
            occupied.UnionWith(scan.OccupiedIds);
            if (scan.HasUnknownId) return null;
            scan.KnownContainers.UnionWith(known);
            scan.OccupiedIds.UnionWith(occupied);
            return scan;
        }
        return await PollAsync(Sample, _timeout, ct).ConfigureAwait(false)
            ?? throw Failed("仍有 Cherry 显示器的编号未知，未更改驱动配置", isSafetyAbort: true);
    }

    private async Task<IReadOnlyList<VddMonitor>?> DriverStateAsync(CancellationToken ct) =>
        (await DriverStateSessionAsync(ct).ConfigureAwait(false)).State;

    private async Task<(IReadOnlyList<VddMonitor>? State, long ConnectionId)> DriverStateSessionAsync(CancellationToken ct)
    {
        if (_ipc is IVirtualDisplayStateSession session)
        {
            var (state, connId) = await session.ReadStateSessionAsync(ct).ConfigureAwait(false);
            if (state is not null || _ipc.IsLegacyProtocol) return (state, connId);
        }
        else if (_ipc is IVirtualDisplayState reader)
        {
            var state = await reader.ReadStateAsync(ct).ConfigureAwait(false);
            if (state is not null || _ipc.IsLegacyProtocol) return (state, 0);
        }
        else if (_ipc.IsLegacyProtocol) return (null, 0);
        throw Failed("无法确认驱动现有配置，为保护已有屏幕，未发送 Notify");
    }

    private async Task ReconcilePersistedPendingAsync(CherryInventory inventory, IReadOnlyList<VddMonitor>? state, CancellationToken ct)
    {
        foreach (var (id, priorKnown) in _ownership.PendingAll())
        {
            if (_ready.ContainsKey(id) || _unconfirmed.ContainsKey(id))
                continue;
            var current = inventory.Unique(id);
            if (current is null || priorKnown.Contains(current.ContainerId))
            {
                _ownership.ReleasePending(id);
                continue;
            }
            if (state is not null && !state.Any(m => m.Id == id && m.Enabled))
            {
                _ownership.ReleasePending(id);
                continue;
            }
            Log.Info($"检测到上次运行残留的 Cherry 手机屏 #{id}，清理中");
            _ownership.Record(current);
            if (await RemoveOwnedAsync(current, ct).ConfigureAwait(false))
                _ownership.ReleasePending(id);
        }
    }

    private async Task<bool> ReconcileUnconfirmedAsync(CherryInventory before, IReadOnlyList<VddMonitor>? state, CancellationToken ct)
    {
        if (_unconfirmed.Count == 0) return false;
        bool removedAny = false;
        foreach (var (id, request) in _unconfirmed.ToArray())
        {
            if (_ready.ContainsKey(id))
            {
                _unconfirmed.Remove(id);
                continue;
            }
            var current = before.Unique(id);
            if (current is null || request.PriorKnownContainers.Contains(current.ContainerId)) continue;
            if (state is not null)
            {
                var actual = state.Where(m => m.Id == id).ToArray();
                if (VirtualDisplayIpcClient.SerializeNotify(actual) != VirtualDisplayIpcClient.SerializeNotify([request.Monitor]))
                    continue;
            }
            Log.Info($"检测到超时后延迟上线的 Cherry 手机屏 #{id}，先清理再重试");
            _ownership.Record(current);
            if (await RemoveOwnedAsync(current, ct).ConfigureAwait(false))
            {
                _unconfirmed.Remove(id);
                _ownership.ReleasePending(id);
                removedAny = true;
            }
        }
        return removedAny;
    }

    private async Task SubmitAsync(VddMonitor monitor, IReadOnlyList<VddMonitor>? state, long connectionId, bool replace,
        HashSet<Guid> priorKnownContainers, CancellationToken ct)
    {
        if (state is not null)
            (state, connectionId) = await RefreshBeforeNotifyAsync(state, monitor.Id, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // Once sent, finish the bounded write (including legacy Remove + Add).
        // Observe caller cancellation in the arrival/rollback path, not mid-write.
        using var write = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _unconfirmed[monitor.Id] = new(monitor, priorKnownContainers.ToHashSet()); // the write-only driver may silently ignore an occupied slot
        _ownership.RecordPending(monitor.Id, priorKnownContainers);
        if (replace && state is null)
            await _ipc.ReplaceLegacyAsync([monitor.Id], [monitor], write.Token).ConfigureAwait(false);
        else
        {
            var payload = state is null ? [monitor] : (IReadOnlyList<VddMonitor>)[.. state.Where(m => m.Id != monitor.Id), monitor];
            if (state is not null && _ipc is IVirtualDisplayStateSession session)
                await session.NotifyOnConnectionAsync(payload, connectionId, write.Token).ConfigureAwait(false);
            else
                await _ipc.NotifyAsync(payload, write.Token).ConfigureAwait(false);
        }
    }

    private async Task<(IReadOnlyList<VddMonitor> State, long ConnectionId)> RefreshBeforeNotifyAsync(IReadOnlyList<VddMonitor> baseline, uint id, CancellationToken ct)
    {
        var (latest, connectionId) = await DriverStateSessionAsync(ct).ConfigureAwait(false);
        if (latest is null) throw Failed("驱动协议发生变化");
        var before = baseline.Where(m => m.Id == id).ToArray();
        var current = latest.Where(m => m.Id == id).ToArray();
        // Comparing the wire representation includes nested mode and refresh-rate lists.
        if (VirtualDisplayIpcClient.SerializeNotify(before) != VirtualDisplayIpcClient.SerializeNotify(current))
            throw Failed($"Cherry 显示器 #{id} 在建屏期间已被其他程序更改");
        return (latest, connectionId);
    }

    private async Task<CherryMonitor> CompleteArrivalAsync(VddMonitor monitor, ScreenFit fit, CherryInventory before,
        IReadOnlyList<(uint StableId, bool IsPrimary)> baselineNonCherry, CancellationToken ct, Guid? expectedContainer = null)
    {
        CherryMonitor? proof = null;
        try
        {
            proof = await FindArrivalAsync(monitor, before, _timeout, ct, expectedContainer).ConfigureAwait(false)
                ?? throw new PhoneScreenException(PhoneScreenErrorKind.NeverOnline);
            _unconfirmed.Remove(proof.DriverId);
            _ownership.Record(proof);
            await WaitForDesktopAsync(proof, fit, baselineNonCherry, ct).ConfigureAwait(false);
            EnsureNonCherryIntact(baselineNonCherry);
            await ApplyWidthAsync(proof, fit, fit.BaseWidth, ct).ConfigureAwait(false);
            EnsureNonCherryIntact(baselineNonCherry);
            ct.ThrowIfCancellationRequested();
            _ready[monitor.Id] = new(proof, fit, monitor.Name!, DateTimeOffset.Now);
            Log.Info($"Cherry 手机屏 #{monitor.Id} 已按 EDID 确认并切到 {fit}");
            return proof;
        }
        catch
        {
            await RollbackArrivalAsync(proof, monitor, before, expectedContainer).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<CherryMonitor?> FindArrivalAsync(VddMonitor monitor, CherryInventory before, TimeSpan timeout, CancellationToken ct, Guid? expectedContainer = null)
    {
        CherryMonitor? FreshTarget()
        {
            var current = _topology.Inspect().Unique(monitor.Id);
            if (current is null) return null;
            if (expectedContainer.HasValue && current.ContainerId == expectedContainer.Value) return current;
            return !before.KnownContainers.Contains(current.ContainerId) ? current : null;
        }
        var target = await PollAsync(FreshTarget, timeout, ct).ConfigureAwait(false);
        if (target is null)
        {
            var snap = _topology.Inspect();
            Log.Warn($"Cherry 手机屏 #{monitor.Id} 到达超时（present=[{string.Join(",", snap.Present.Select(p => $"#{p.DriverId}:{p.ContainerId}"))}], legacy={_ipc.IsLegacyProtocol}）");
            return null;
        }
        if (_ipc.IsLegacyProtocol) return target;

        var expectedJson = VirtualDisplayIpcClient.SerializeNotify([monitor]);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var state = await DriverStateAsync(ct).ConfigureAwait(false);
            if (_ipc.IsLegacyProtocol) return target;
            if (state is not null)
            {
                var actual = state.Where(m => m.Id == monitor.Id).ToArray();
                if (VirtualDisplayIpcClient.SerializeNotify(actual) == expectedJson) return target;
            }
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining.TotalMilliseconds < 50 ? remaining : TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
        }
        return null;
    }

    private async Task RollbackArrivalAsync(CherryMonitor? proof, VddMonitor monitor, CherryInventory before, Guid? expectedContainer = null)
    {
        try
        {
            // Cancellation can win just after the write, before PnP delivers the arrival.
            var grace = TimeSpan.FromMilliseconds(Math.Min(1000, _timeout.TotalMilliseconds));
            proof ??= await FindArrivalAsync(monitor, before, grace, CancellationToken.None, expectedContainer).ConfigureAwait(false);
            if (proof is null) return; // a successful pipe write alone is not ownership
            _unconfirmed.Remove(proof.DriverId);
            _ownership.Record(proof);
            if (await RemoveOwnedAsync(proof, CancellationToken.None).ConfigureAwait(false))
            {
                _ownership.ReleasePending(proof.DriverId);
            }
            else if (!_ready.ContainsKey(proof.DriverId))
            {
                _retiring[proof.DriverId] = proof;
            }
        }
        catch (Exception ex) { Log.Warn($"回滚未完成：{ex.Message}"); }
    }

    private async Task WaitForDesktopAsync(CherryMonitor target, ScreenFit fit,
        IReadOnlyList<(uint StableId, bool IsPrimary)> baselineNonCherry, CancellationToken ct)
    {
        var (w, h) = fit.PixelSize(fit.BaseWidth);
        string? attachedName = null;
        var sinceAttempt = Stopwatch.StartNew();
        bool attempted = false;
        MonitorSnapshot? Observe()
        {
            EnsureNonCherryIntact(baselineNonCherry);
            var online = _topology.Online(target, attachedName);
            if (online is not null) return online;
            if (attachedName is not null) return null;
            if (attempted && sinceAttempt.ElapsedMilliseconds < 1500) return null;
            attempted = true;
            sinceAttempt.Restart();
            attachedName = _topology.Attach(target, w, h);
            return attachedName is null ? null : _topology.Online(target, attachedName);
        }
        await ThrottleModesetAsync(ct).ConfigureAwait(false);
        if (await PollAsync(Observe, _timeout, ct).ConfigureAwait(false) is null)
        {
            EnsureNonCherryIntact(baselineNonCherry);
            var activeCherry = _topology.Active()
                .Where(m => m.Cherry is not null)
                .Select(m => $"{m.DeviceName}:#{m.Cherry!.DriverId}:pri={m.IsPrimary}")
                .ToArray();
            Log.Warn($"Cherry 手机屏 #{target.DriverId} 桌面上线超时（attachedName={attachedName ?? "null"}, activeCherry=[{string.Join(";", activeCherry)}]）");
            throw new PhoneScreenException(PhoneScreenErrorKind.NeverOnline);
        }
    }

    public async Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_ready.TryGetValue(displayId, out var old)) return false;
            var (state, connectionId) = await DriverStateSessionAsync(ct).ConfigureAwait(false);
            var before = await ReadBaselineAsync(ct).ConfigureAwait(false);
            if (before.Unique(displayId) != old.Target || _topology.Online(old.Target) is null) return false;
            if (state is not null && !state.Any(m => m.Id == displayId && m.Enabled)) return false;
            if (SameModes(old.Fit, fit))
            {
                await ApplyWidthAsync(old.Target, fit, fit.BaseWidth, ct).ConfigureAwait(false);
                _ready[displayId] = old with { Fit = fit };
            }
            else await ReplaceScreenAsync(old, fit, state, connectionId, before, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Cherry 手机屏 #{displayId} 换比例失败：{ex.Message}");
            return false;
        }
        finally { _changes.Release(); }
    }

    private async Task ReplaceScreenAsync(Screen old, ScreenFit fit, IReadOnlyList<VddMonitor>? state, long connectionId, CherryInventory before, CancellationToken ct)
    {
        uint id = old.Target.DriverId;
        var monitor = new VddMonitor(id, old.Name, true, BuildModes(fit));
        var baselineNonCherry = SnapshotNonCherryDisplays();
        _ready.TryRemove(id, out _);
        try
        {
            await SubmitAsync(monitor, state, connectionId, replace: true, before.KnownContainers, ct).ConfigureAwait(false);
            var next = await CompleteArrivalAsync(monitor, fit, before, baselineNonCherry, ct, old.Target.ContainerId).ConfigureAwait(false);
            if (next != old.Target)
                _ownership.Release(old.Target);
        }
        catch
        {
            if (_topology.Online(old.Target) is { } previous && WidthIn(old.Fit, previous) is not null)
            {
                _unconfirmed.Remove(id);
                _ready[id] = old;
            }
            else if (!await RemoveOwnedAsync(old.Target, CancellationToken.None).ConfigureAwait(false))
                _retiring.TryAdd(id, old.Target);
            throw;
        }
    }

    public async Task<bool> RetireAsync(uint displayId, CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready.TryGetValue(displayId, out var screen))
            {
                _retiring[displayId] = screen.Target;
                _ready.TryRemove(displayId, out _);
            }
            if (!_retiring.TryGetValue(displayId, out var target)) return false;
            if (!await RemoveOwnedAsync(target, ct).ConfigureAwait(false)) return false;
            _retiring.TryRemove(displayId, out _);
            if (_unconfirmed.Count > 0)
                await ReconcileUnconfirmedAsync(_topology.Inspect(), await DriverStateAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            _topology.RestoreCursorIfStranded();
            return true;
        }
        finally { _changes.Release(); }
    }

    private async Task<bool> RemoveOwnedAsync(CherryMonitor target, CancellationToken ct)
    {
        try
        {
            // Never remove a display that has become the sole active or primary desktop output.
            var active = _topology.Active();
            if (active.Any(m => m.Cherry == target && (m.IsPrimary || active.Count == 1)))
            {
                Log.Warn($"拒绝移除 Cherry 手机屏 #{target.DriverId}：当前为唯一活动输出或主显示器");
                return false;
            }

            // Connect before rechecking the receipt, not after a potentially slow pipe negotiation.
            var state = await DriverStateAsync(ct).ConfigureAwait(false);
            var inventory = _topology.Inspect();
            if (inventory.Unique(target.DriverId) == target)
            {
                if (state is not null && !state.Any(m => m.Id == target.DriverId && m.Enabled)) return false;
                await _ipc.RemoveAsync([target.DriverId], ct).ConfigureAwait(false);
                CherryMonitor? Gone() => MayStillExist(_topology.Inspect(), target) ? null : target;
                if (await PollAsync(Gone, _timeout, ct).ConfigureAwait(false) is null) return false;
            }
            else if (MayStillExist(inventory, target)) return false;
            _ownership.Release(target);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"未确认 Cherry 手机屏 #{target.DriverId} 已移除：{ex.Message}");
            return false;
        }
    }

    public ScreenFit? FitOf(uint displayId) => Confirmed(displayId)?.Fit;

    public DateTimeOffset? OnlineAtOf(uint displayId) => _ready.TryGetValue(displayId, out var screen) ? screen.OnlineAt : null;

    public bool IsOnline(uint displayId)
    {
        var target = _ready.GetValueOrDefault(displayId)?.Target ?? _retiring.GetValueOrDefault(displayId);
        return target is not null && MayStillExist(_topology.Inspect(), target);
    }

    public bool IsAttachedToDesktop(uint displayId)
    {
        if (!_ready.TryGetValue(displayId, out var screen)) return false;
        return _topology.Active().Any(m => m.Cherry == screen.Target);
    }

    private static bool IsVirtualDisplayAdapter(MonitorSnapshot m) =>
        m.Cherry is not null
        || m.DeviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || m.DeviceString.Contains("VirtuDisplay", StringComparison.OrdinalIgnoreCase)
        || m.AdapterHardwareId.StartsWith(@"Root\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True only when UU's own virtual display is active AND every real monitor present at
    /// startup has been detached (UU 超级屏). SnugWin then waits instead of re-adding screens.
    /// </summary>
    public bool IsRemoteExclusiveDisplayActive()
    {
        if (_startupPhysicalDevices.Count == 0) return false;
        var active = _topology.Active();
        bool uuActive = active.Any(m =>
            m.DeviceString.Contains("GameViewer Virtual Display Adapter", StringComparison.OrdinalIgnoreCase)
            || m.AdapterHardwareId.Contains("GameViewer", StringComparison.OrdinalIgnoreCase));
        bool realStillHere = active.Any(m => m.Cherry is null && !IsVirtualDisplayAdapter(m));
        bool startupDisplaysGone = _startupPhysicalDevices.All(name =>
            active.All(m => !m.DeviceName.Equals(name, StringComparison.OrdinalIgnoreCase)));
        return uuActive && !realStillHere && startupDisplaysGone;
    }

    /// <summary>
    /// Re-attach one already-bound SnugWin display after a topology change using its CherryMonitor
    /// identity and CCD target path, re-checking remote-exclusive status before each attach attempt.
    /// </summary>
    public async Task<bool> TryReactivateAsync(uint displayId, int width, CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_ready.TryGetValue(displayId, out var screen)) return false;
            if (IsRemoteExclusiveDisplayActive()) return false;

            var target = screen.Target;
            var fit = screen.Fit;
            int requestedWidth = fit.Widths.Contains(width) ? width : fit.BaseWidth;

            if (_topology.Online(target) is not null)
            {
                await ApplyWidthAsync(target, fit, requestedWidth, ct).ConfigureAwait(false);
                return true;
            }

            if (_topology.Inspect().Unique(target.DriverId) != target) return false;

            var (state, connectionId) = await DriverStateSessionAsync(ct).ConfigureAwait(false);
            if (state is not null && !state.Any(m => m.Id == displayId && m.Enabled))
            {
                if (IsRemoteExclusiveDisplayActive()) return false;
                var monitor = new VddMonitor(displayId, screen.Name, true, BuildModes(fit));
                IReadOnlyList<VddMonitor> payload = [.. state.Where(m => m.Id != displayId), monitor];
                if (_ipc is IVirtualDisplayStateSession session)
                    await session.NotifyOnConnectionAsync(payload, connectionId, ct).ConfigureAwait(false);
                else
                    await _ipc.NotifyAsync(payload, ct).ConfigureAwait(false);
            }

            if (IsRemoteExclusiveDisplayActive()) return false;
            var (pxW, pxH) = fit.PixelSize(requestedWidth);
            string? attachedName = null;
            var sinceAttempt = Stopwatch.StartNew();
            bool attempted = false;
            MonitorSnapshot? Observe()
            {
                if (IsRemoteExclusiveDisplayActive()) return null;
                var online = _topology.Online(target, attachedName);
                if (online is not null || attachedName is not null) return online;
                if (attempted && sinceAttempt.ElapsedMilliseconds < 500) return null;
                attempted = true;
                sinceAttempt.Restart();
                attachedName = _topology.Attach(target, pxW, pxH);
                return attachedName is null ? null : _topology.Online(target, attachedName);
            }

            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < _timeout && !ct.IsCancellationRequested)
            {
                if (IsRemoteExclusiveDisplayActive()) return false;
                if (Observe() is not null)
                {
                    await ApplyWidthAsync(target, fit, requestedWidth, ct).ConfigureAwait(false);
                    return true;
                }
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Cherry 手机屏 #{displayId} 恢复失败：{ex.Message}");
            return false;
        }
        finally { _changes.Release(); }
    }

    private bool MayStillExist(CherryInventory inventory, CherryMonitor target) =>
        inventory.MayContain(target) || _topology.Active().Any(m => m.Cherry == target);

    public async Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_ready.TryGetValue(displayId, out var screen)) throw Failed($"手机屏 #{displayId} 未就绪");
            await ApplyWidthAsync(screen.Target, screen.Fit, width, ct).ConfigureAwait(false);
        }
        finally { _changes.Release(); }
    }

    private async Task ApplyWidthAsync(CherryMonitor target, ScreenFit fit, int width, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (w, h) = fit.PixelSize(width);
        var current = _topology.Online(target) ?? throw Failed($"未找到独立的 Cherry 手机屏 #{target.DriverId}");
        bool Sized(MonitorSnapshot? m) => m?.Mode.Width == w && m.Mode.Height == h && Math.Abs(m.Mode.RefreshRate - 60.0) < 1.0;
        MonitorSnapshot? ReadSize() => _topology.Online(target) is { } m && Sized(m) ? m : null;
        if (!Sized(current))
        {
            if (!_topology.SetResolution(current, w, h, 60)) throw Failed($"Cherry 手机屏 #{target.DriverId} 切换 {w}x{h} @ 60Hz 失败");
            current = await PollAsync(ReadSize, _timeout, ct).ConfigureAwait(false) ?? throw Failed($"未确认手机屏已切换 {w}x{h} @ 60Hz");
        }
        await ApplyScaleAsync(current, fit.TargetDpiPercent(width), ReadSize, ct).ConfigureAwait(false);
    }

    private async Task ApplyScaleAsync(MonitorSnapshot current, int requested, Func<MonitorSnapshot?> readSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int dpi = current.ResolveDpiScalePercent(requested) ?? throw Failed("无法确认手机屏支持的缩放档位");
        if (dpi != requested) Log.Info($"Cherry 手机屏 #{current.Cherry!.DriverId} 请求 {requested}% 缩放，最接近的支持档位为 {dpi}%");
        if (current.Mode.DpiScalePercent == dpi) return;
        if (!_topology.SetScale(current, dpi)) throw Failed($"Cherry 手机屏 #{current.Cherry!.DriverId} 设置 {dpi}% 缩放失败");
        MonitorSnapshot? ReadDpi() => readSize() is { } m && m.Mode.DpiScalePercent == dpi ? m : null;
        if (await PollAsync(ReadDpi, _timeout, ct).ConfigureAwait(false) is null) throw Failed($"未确认手机屏缩放为 {dpi}%");
    }

    private (MonitorSnapshot Monitor, ScreenFit Fit, int Width)? Confirmed(uint id)
    {
        if (!_ready.TryGetValue(id, out var screen) || _topology.Online(screen.Target) is not { } mon) return null;
        return WidthIn(screen.Fit, mon) is { } width ? (mon, screen.Fit, width) : null;
    }

    private static int? WidthIn(ScreenFit fit, MonitorSnapshot mon)
    {
        foreach (int width in fit.Widths)
            if (fit.PixelSize(width) == (mon.Mode.Width, mon.Mode.Height)) return width;
        return null;
    }

    public int? WidthOf(uint displayId) => Confirmed(displayId)?.Width;
    public RectD? BoundsOf(uint displayId) => Confirmed(displayId)?.Monitor.Bounds;

    public RectD? WindowAreaOf(uint displayId) => Confirmed(displayId) is { } screen ? WindowArea(screen) : null;

    public RectD? HomeBarAreaOf(uint displayId)
    {
        if (Confirmed(displayId) is not { } screen || screen.Fit.Kind == DeviceKind.Computer) return null;
        var work = screen.Monitor.WorkArea;
        var area = WindowArea(screen);
        return new RectD(work.X, area.MaxY, work.Width, Math.Max(28, work.MaxY - area.MaxY));
    }

    private static RectD WindowArea((MonitorSnapshot Monitor, ScreenFit Fit, int Width) screen)
    {
        var work = screen.Monitor.WorkArea;
        if (screen.Fit.Kind == DeviceKind.Computer) return work;
        double scale = Math.Max(1.0, screen.Monitor.Mode.Width / (double)screen.Width);
        double bar = Math.Round(ScreenFit.HomeBarHeight(screen.Fit.Size(screen.Width)) * scale);
        return new(work.X, work.Y, work.Width, Math.Max(100, work.Height - bar));
    }

    public RectD? MainArea()
    {
        var others = _topology.Active().Where(m => m.Cherry is null).ToList();
        return (others.FirstOrDefault(m => m.IsPrimary) ?? others.FirstOrDefault())?.WorkArea;
    }

    public IReadOnlyList<OtherDisplay> OtherDisplays() => _topology.Active().Where(PhoneDisplayTopology.IsRestorable)
        .Select(m => new OtherDisplay(m.StableId, m.DeviceName, m.FriendlyName, m.Mode)).ToList();

    public Task SetModeAsync(uint displayId, DisplayModeInfo mode, bool permanently, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        MonitorSnapshot? Other() => _topology.Active().FirstOrDefault(m => m.StableId == displayId && PhoneDisplayTopology.IsRestorable(m));
        if (Other() is not { } target) return Task.CompletedTask;
        if (target.Mode.Width != mode.Width || target.Mode.Height != mode.Height || Math.Abs(target.Mode.RefreshRate - mode.RefreshRate) >= 1.0)
            _topology.SetResolution(target, mode.Width, mode.Height, mode.RefreshRate);
        if (Other() is { } after && mode.DpiScalePercent is { } wantDpi && after.Mode.DpiScalePercent != wantDpi)
            _topology.SetScale(after, wantDpi);
        return Task.CompletedTask;
    }

    private static List<VddMode> BuildModes(ScreenFit fit) => fit.RegistrationOrder.Select(w =>
    {
        var (pw, ph) = fit.PixelSize(w);
        return new VddMode((uint)pw, (uint)ph, [60]);
    }).ToList();

    private static bool SameModes(ScreenFit first, ScreenFit second) =>
        first.RegistrationOrder.Select(first.PixelSize).SequenceEqual(second.RegistrationOrder.Select(second.PixelSize));

    private static PhoneScreenException Failed(string detail, bool isSafetyAbort = false) =>
        new(PhoneScreenErrorKind.CreateFailed, detail, isSafetyAbort);

    private static async Task<T?> PollAsync<T>(Func<T?> read, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (read() is { } result) return result;
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) return null;
            await Task.Delay(remaining.TotalMilliseconds < 100 ? remaining : TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _ipc.DisposeAsync();
}
