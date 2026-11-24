using System;
using System.Collections.Generic;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Touch;

namespace SnugWin.Core.Remote;

public sealed record SnugDisplayInfo(uint DisplayId, string? DeviceName, RectD Bounds, DateTimeOffset? OnlineAt = null);

public sealed record RemoteIdentity(
    string LogName,
    string DisplayName,
    IReadOnlyList<string> ProcessNames,
    bool SeesScreensOnlyAtLaunch = false,
    bool OpensOnMainScreen = false,
    Func<ITouchNormalizer>? NormalizerFactory = null)
{
    public bool Equals(RemoteIdentity? other) =>
        other is not null && string.Equals(LogName, other.LogName, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(LogName);

    public ITouchNormalizer CreateNormalizer() => NormalizerFactory?.Invoke() ?? new DirectTouch();
}

public sealed record RemoteObservation(
    bool Present,
    IReadOnlyList<string> Controllers,
    uint? StreamedDisplay = null,
    string? Evidence = null,
    IReadOnlySet<uint>? UnreachableDisplays = null,
    bool ManualOverride = false);

public sealed record RemoteReading(
    RemoteIdentity Identity,
    RemoteObservation Observation,
    DateTimeOffset ObservedAt)
{
    private static readonly IReadOnlySet<uint> EmptyDisplays = new HashSet<uint>();

    public IReadOnlySet<uint> Unseen(IReadOnlySet<uint> snugDisplays)
    {
        if (Observation.UnreachableDisplays is not { Count: > 0 } unreachable || snugDisplays.Count == 0)
            return EmptyDisplays;

        var unseen = new HashSet<uint>();
        foreach (uint displayId in snugDisplays)
        {
            if (unreachable.Contains(displayId))
                unseen.Add(displayId);
        }
        return unseen;
    }

    public static bool IsBlind(
        IReadOnlyList<RemoteReading> readings,
        RemoteIdentity? owner,
        IReadOnlySet<uint> snugDisplays,
        out RemoteIdentity? blindRemote)
    {
        foreach (var reading in readings)
        {
            if (owner is not null && !reading.Identity.Equals(owner))
                continue;

            if (reading.Unseen(snugDisplays).Count > 0)
            {
                blindRemote = reading.Identity;
                return true;
            }
        }

        blindRemote = null;
        return false;
    }
}

public interface IRemoteAdapter
{
    RemoteIdentity Identity { get; }
    bool IsInstalled();
    RemoteReading Sample(DateTimeOffset wallClock, bool wasConnected, IReadOnlyList<SnugDisplayInfo> snugDisplays);
}
