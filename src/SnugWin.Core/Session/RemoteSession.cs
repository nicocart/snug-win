using System;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Remote;
using SnugWin.Core.Support;

namespace SnugWin.Core.Session;

public abstract record SessionTransition
{
    public sealed record Began(IReadOnlyList<string> Controllers) : SessionTransition;
    public sealed record Resumed(IReadOnlyList<string> Controllers) : SessionTransition;
    public sealed record ControllersChanged(IReadOnlyList<string> Controllers) : SessionTransition;
    public sealed record Interrupted : SessionTransition;
    public sealed record Ended : SessionTransition;
}

public sealed class RemoteSession
{
    internal const string EndedNowLine = "会话：立即结束（不等重连窗口）";

    public ConnectionTracker Tracker { get; } = new();
    public SessionEpisode Episode { get; } = new();

    private List<string> _group = new();
    private readonly Dictionary<string, DateTimeOffset> _departed = new();
    private readonly HashSet<string> _notedOthers = new(StringComparer.OrdinalIgnoreCase);

    public RemoteIdentity? Owner { get; private set; }
    public RemoteReading? Tracked { get; private set; }
    public DateTimeOffset? EpisodeBeganAt { get; private set; }
    public bool IsConnected => Tracker.IsConnected;
    public IReadOnlyList<string> Controllers => Tracker.Controllers;
    public bool InEpisode => Episode.IsActive;
    public bool IsInReconnectGrace => Episode.IsInterrupted;

    public IReadOnlyList<string> EpisodeControllers(DateTimeOffset now)
    {
        var lingering = _departed
            .Where(kv => now - kv.Value <= SessionEpisode.ReconnectWindow)
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal);
        return _group.Concat(lingering).ToList();
    }

    public IReadOnlyList<SessionTransition> Update(IReadOnlyList<RemoteReading> readings, DateTimeOffset now)
    {
        var transitions = new List<SessionTransition>();
        if (Episode.Advance(now) == EpisodeChange.Ended)
        {
            transitions.Add(new SessionTransition.Ended());
            ClearEpisodeState();
        }

        RemoteReading? mine = Owner is { } currentOwner
            ? readings.FirstOrDefault(r => r.Identity.Equals(currentOwner))
            : null;

        Tracked = Tracker.IsConnected
            ? mine
            : readings.FirstOrDefault(r => r.Observation.Present)
              ?? mine
              ?? (readings.Count > 0 ? readings[0] : null);

        if (Tracked is { } tracked)
            Feed(tracked, now, transitions);

        foreach (var transition in transitions)
            Log.Info(FormatTransitionLog(transition));

        if (Tracker.IsConnected && Owner is { } activeOwner)
            NoteOthers(readings, activeOwner);

        return transitions;
    }

    public IReadOnlyList<SessionTransition> Update(UUSignals signals, DateTimeOffset now) =>
        Update(new[] { new RemoteReading(UURemote.Identity, signals.ToObservation(), now) }, now);

    public SessionTransition? EndNow()
    {
        if (Episode.EndNow() != EpisodeChange.Ended)
            return null;
        ClearEpisodeState();
        Log.Info(EndedNowLine);
        var ended = new SessionTransition.Ended();
        Log.Info(FormatTransitionLog(ended));
        return ended;
    }

    public void NoteSelfInducedChange(DateTimeOffset now) => Tracker.NoteSelfInducedChange(now);

    internal static string OtherPresentLine(RemoteReading other, RemoteIdentity owner)
    {
        var evidence = string.IsNullOrEmpty(other.Observation.Evidence) ? "-" : other.Observation.Evidence;
        return $"另有 {other.Identity.LogName} 报告连接，本次远程归 {owner.LogName}（{evidence}）";
    }

    internal static string FormatTransitionLog(SessionTransition transition)
    {
        static string FormatNames(IReadOnlyList<string> controllers) =>
            controllers.Count == 0 ? "读不到名字" : string.Join("、", controllers);

        return transition switch
        {
            SessionTransition.Began b => $"会话：开始（控制端 {FormatNames(b.Controllers)}）",
            SessionTransition.Resumed r => $"会话：重连上，仍是同一次远程（控制端 {FormatNames(r.Controllers)}）",
            SessionTransition.ControllersChanged c => $"会话：控制端变为 {FormatNames(c.Controllers)}",
            SessionTransition.Interrupted => $"会话：断开，等 {(int)SessionEpisode.ReconnectWindow.TotalSeconds}s 看会不会重连",
            SessionTransition.Ended => "会话：结束",
            _ => transition.ToString() ?? string.Empty
        };
    }

    private void Feed(RemoteReading reading, DateTimeOffset now, List<SessionTransition> transitions)
    {
        switch (Tracker.Update(reading.Observation, reading.Identity.LogName, now))
        {
            case ConnectionEvent.Connected c:
                Owner = reading.Identity;
                if (Episode.Connected(now) == EpisodeChange.Began)
                {
                    EpisodeBeganAt = now;
                    _group = c.Controllers.ToList();
                    _departed.Clear();
                    transitions.Add(new SessionTransition.Began(_group));
                }
                else
                {
                    if (c.Controllers.Count > 0) Regroup(c.Controllers, now);
                    transitions.Add(new SessionTransition.Resumed(c.Controllers));
                }
                break;

            case ConnectionEvent.ControllersChanged ch:
                Regroup(ch.Controllers, now);
                transitions.Add(new SessionTransition.ControllersChanged(ch.Controllers));
                break;

            case ConnectionEvent.Disconnected:
                if (Episode.Disconnected(now) == EpisodeChange.Interrupted)
                    transitions.Add(new SessionTransition.Interrupted());
                break;
        }
    }

    private void NoteOthers(IReadOnlyList<RemoteReading> readings, RemoteIdentity owner)
    {
        foreach (var other in readings)
        {
            if (other.Identity.Equals(owner) || !other.Observation.Present)
                continue;
            if (_notedOthers.Add(other.Identity.LogName))
                Log.Info(OtherPresentLine(other, owner));
        }
    }

    private void ClearEpisodeState()
    {
        _group.Clear();
        _departed.Clear();
        EpisodeBeganAt = null;
        Owner = null;
        _notedOthers.Clear();
    }

    private void Regroup(IReadOnlyList<string> names, DateTimeOffset now)
    {
        var nextSet = new HashSet<string>(names);
        foreach (var old in _group)
        {
            if (!nextSet.Contains(old))
                _departed[old] = now;
        }
        foreach (var n in names)
            _departed.Remove(n);
        _group = names.ToList();
    }
}
