using System;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Remote;

namespace SnugWin.Core.Remotes.CrossDesk;

public sealed record CrossDeskDisplaySlot(int Slot, string Name, bool Available, RectD Bounds);

/// <summary>
/// Tracks which displays the running CrossDesk instance registered at startup (its video slots)
/// and determines which currently online Snug virtual displays cannot be reached without restarting CrossDesk.
/// </summary>
public sealed class CrossDeskSlots
{
    private Dictionary<int, CrossDeskDisplaySlot>? _slots;

    public IReadOnlyDictionary<int, CrossDeskDisplaySlot>? Displays => _slots;

    public bool SawUnregisteredDisplayIgnored { get; private set; }

    public string? Summary => _slots is null
        ? null
        : string.Join("、", _slots.OrderBy(kv => kv.Key).Select(kv => $"第 {kv.Key} 格 {kv.Value.Name}"));

    public void Take(string line)
    {
        var parsed = CrossDeskLogLine.Parse(line);
        if (parsed is not null)
            Take(parsed);
    }

    public void Take(CrossDeskLogLine line)
    {
        switch (line.Event)
        {
            case CrossDeskLogEvent.Started:
                _slots = null;
                SawUnregisteredDisplayIgnored = false;
                break;

            case CrossDeskLogEvent.DisplaySlot ds:
                _slots ??= new Dictionary<int, CrossDeskDisplaySlot>();
                _slots[ds.Slot] = new CrossDeskDisplaySlot(ds.Slot, ds.Name, ds.Available, ds.Bounds);
                break;

            case CrossDeskLogEvent.UnregisteredDisplayIgnored:
                SawUnregisteredDisplayIgnored = true;
                break;
        }
    }

    public void Reset()
    {
        _slots = null;
        SawUnregisteredDisplayIgnored = false;
    }

    /// <summary>
    /// Returns the set of online Snug display IDs that CrossDesk cannot see/stream.
    /// Returns <c>null</c> if CrossDesk's startup display slots have not been observed yet.
    /// </summary>
    public IReadOnlySet<uint>? Unreachable(
        IReadOnlyList<SnugDisplayInfo> snugDisplays,
        IReadOnlyCollection<string>? onlineDeviceNames = null)
    {
        if (_slots is null)
            return null;

        if (snugDisplays.Count == 0)
            return new HashSet<uint>();

        var availableSlots = _slots.Values.Where(s => s.Available).ToList();
        var availableNames = new HashSet<string>(
            availableSlots.Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);

        var matchedSlotNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new HashSet<uint>();

        foreach (var snug in snugDisplays)
        {
            string? normName = snug.DeviceName is null ? null : CrossDeskLogLine.NormalizeDisplayName(snug.DeviceName);
            if (!string.IsNullOrEmpty(normName) && availableNames.Contains(normName))
            {
                matchedSlotNames.Add(normName);
                continue;
            }

            // Fallback match by bounds if device name was missing in tests/virtual topologies
            var byBounds = availableSlots.FirstOrDefault(s =>
                !matchedSlotNames.Contains(s.Name)
                && s.Bounds.Width > 0
                && s.Bounds.Matches(snug.Bounds));
            if (byBounds is not null)
            {
                matchedSlotNames.Add(byBounds.Name);
                continue;
            }

            fresh.Add(snug.DisplayId);
        }

        if (fresh.Count == 0)
            return fresh;

        // If onlineDeviceNames is supplied, count slots whose original display is now offline (vacated).
        // If not supplied (minimal unit tests), treat any unmatched slot as vacant only if explicitly offline;
        // default to 0 vacant when online names are unknown unless caller passes them.
        int vacant = 0;
        if (onlineDeviceNames is not null)
        {
            var onlineNorm = new HashSet<string>(
                onlineDeviceNames.Select(CrossDeskLogLine.NormalizeDisplayName),
                StringComparer.OrdinalIgnoreCase);
            vacant = availableSlots.Count(s => !onlineNorm.Contains(s.Name));
        }

        return fresh.Count <= vacant ? new HashSet<uint>() : fresh;
    }
}
