using System;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Remote;
using SnugWin.Core.Touch;
using SnugWin.Windows.Sensing;

namespace SnugWin.App.Controllers;

/// <summary>
/// Central registry of supported remote-control adapters.
/// Order defines session ownership priority when multiple remotes report present simultaneously.
/// </summary>
internal sealed class Remotes
{
    public UURemoteAdapter UU { get; } = new();
    public CrossDeskRemoteAdapter CrossDesk { get; } = new();
    public IReadOnlyList<IRemoteAdapter> All { get; }
    public IReadOnlyList<string> AllProcessNames { get; }
    public IReadOnlyList<Func<ITouchNormalizer>> NormalizerFactories { get; }

    public Remotes()
    {
        All = [UU, CrossDesk];
        AllProcessNames = All
            .SelectMany(a => a.Identity.ProcessNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Slot 0 is DirectTouch fallback for idle / manual override / unknown owner;
        // slots 1..N correspond to All[0..N-1].
        var factories = new List<Func<ITouchNormalizer>>(All.Count + 1)
        {
            static () => new DirectTouch()
        };
        foreach (var adapter in All)
        {
            var identity = adapter.Identity;
            factories.Add(() => identity.CreateNormalizer());
        }
        NormalizerFactories = factories;
    }

    public int SourceIndexFor(RemoteIdentity? owner, bool manualOverride)
    {
        if (manualOverride || owner is null)
            return 0;
        for (int i = 0; i < All.Count; i++)
        {
            if (All[i].Identity.Equals(owner))
                return i + 1;
        }
        return 0;
    }
}
