using SnugWin.Core.Persistence;

namespace SnugWin.Windows.Display;

internal sealed record PendingArrival(uint DriverId, Guid[] PriorKnownContainers);

internal sealed class DisplayOwnership
{
    // Old files contain only DisplayIds, which were inferred from GDI names and
    // may belong to the user. Only persist/consume per-arrival receipts.
    private sealed record Receipts
    {
        public CherryMonitor[] Monitors { get; init; } = [];
        public PendingArrival[] Pending { get; init; } = [];
    }

    private readonly JsonFileStore<Receipts>? _store;
    private readonly HashSet<CherryMonitor> _owned;
    private readonly Dictionary<uint, HashSet<Guid>> _pending;
    private bool _dirty;

    public DisplayOwnership(string? path)
    {
        if (path is not null) _store = new(path);
        var loaded = _store?.LoadOrQuarantine();
        _owned = (loaded?.Monitors ?? []).ToHashSet();
        _pending = new Dictionary<uint, HashSet<Guid>>();
        foreach (var p in loaded?.Pending ?? [])
            _pending[p.DriverId] = (p.PriorKnownContainers ?? []).ToHashSet();
    }

    public IReadOnlyList<CherryMonitor> All() => _owned.ToArray();

    public IReadOnlyList<(uint DriverId, HashSet<Guid> PriorKnownContainers)> PendingAll() =>
        _pending.Select(kv => (kv.Key, kv.Value.ToHashSet())).ToArray();

    public void Record(CherryMonitor target)
    {
        bool removedPending = _pending.Remove(target.DriverId);
        if (_owned.Add(target) || removedPending || _dirty) Save();
    }

    public void Release(CherryMonitor target)
    {
        if (_owned.Remove(target) || _dirty) Save();
    }

    public void RecordPending(uint driverId, IEnumerable<Guid> priorKnownContainers)
    {
        _pending[driverId] = priorKnownContainers.ToHashSet();
        Save();
    }

    public void ReleasePending(uint driverId)
    {
        if (_pending.Remove(driverId) || _dirty) Save();
    }

    private void Save()
    {
        _dirty = true;
        if (_owned.Count == 0 && _pending.Count == 0) _store?.Delete();
        else _store?.Save(new Receipts
        {
            Monitors = _owned.ToArray(),
            Pending = _pending.Select(kv => new PendingArrival(kv.Key, kv.Value.ToArray())).ToArray()
        });
        _dirty = false;
    }
}
