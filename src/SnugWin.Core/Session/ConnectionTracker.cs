using System;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Remote;
using SnugWin.Core.Support;

namespace SnugWin.Core.Session;

public sealed class ConnectionTracker
{
    public static readonly TimeSpan DisconnectDebounce = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(5);

    public bool IsConnected { get; private set; }
    public IReadOnlyList<string> Controllers { get; private set; } = Array.Empty<string>();

    private DateTimeOffset? _lastPresentAt;
    private DateTimeOffset? _quietUntil;

    public void NoteSelfInducedChange(DateTimeOffset now)
    {
        var until = now + QuietWindow;
        _quietUntil = _quietUntil is { } q && q > until ? q : until;
    }

    public ConnectionEvent? Update(RemoteObservation observation, string remoteName, DateTimeOffset now)
    {
        var names = observation.Controllers;
        if (observation.Present) _lastPresentAt = now;

        if (!IsConnected)
        {
            if (!observation.Present) return null;
            IsConnected = true;
            Controllers = names.ToList();
            LogEvent($"检测到 {remoteName} 连入", observation, now);
            return new ConnectionEvent.Connected(Controllers);
        }

        if (observation.Present)
        {
            if (names.Count == 0 || new HashSet<string>(names).SetEquals(Controllers))
                return null;
            Controllers = names.ToList();
            LogEvent($"控制端变为 {string.Join("、", names)}", observation, now);
            return new ConnectionEvent.ControllersChanged(Controllers);
        }

        if (_lastPresentAt is not { } last || now - last <= DisconnectDebounce || !QuietPassed(now))
            return null;

        LogEvent($"检测到 {remoteName} 断开", observation, now);
        IsConnected = false;
        Controllers = Array.Empty<string>();
        return new ConnectionEvent.Disconnected();
    }

    public ConnectionEvent? Update(UUSignals signals, DateTimeOffset now) =>
        Update(signals.ToObservation(), UURemote.Identity.LogName, now);

    private bool QuietPassed(DateTimeOffset now) => _quietUntil is null || now > _quietUntil.Value;

    private void LogEvent(string what, RemoteObservation observation, DateTimeOffset now)
    {
        var since = _lastPresentAt is { } last ? $"{(now - last).TotalSeconds:0.00}s" : "-";
        var quiet = _quietUntil is { } q && now <= q ? $" 静默期剩 {(q - now).TotalSeconds:0.00}s" : "";
        var evidence = string.IsNullOrEmpty(observation.Evidence) ? "-" : observation.Evidence;
        Log.Info($"{what}（{evidence} 距上次在场 {since}{quiet}）");
    }
}
