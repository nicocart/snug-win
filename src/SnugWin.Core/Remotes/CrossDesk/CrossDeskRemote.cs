using System;
using System.Collections.Generic;
using SnugWin.Core.Remote;

namespace SnugWin.Core.Remotes.CrossDesk;

/// <summary>
/// CrossDesk identity and observation builder.
/// Presence on Windows is driven by <c>crossdesk.log</c>'s symmetric power-inhibitor lifecycle
/// (<see cref="CrossDeskLogEvent.SharingBegan"/> / <see cref="CrossDeskLogEvent.SharingEnded"/>)
/// gated by live CrossDesk process presence, because Windows offers no unprivileged API
/// to query per-process power requests without spawning elevated <c>powercfg</c>.
/// </summary>
public static class CrossDeskRemote
{
    public const string VerifiedVersion = "v1.7.7-20261124";
    public const string SharingAssertion = "CrossDesk is sharing the desktop";

    public static readonly IReadOnlyList<string> ProcessNames =
    [
        "CrossDesk.exe",
        "crossdesk_service.exe",
        "crossdesk_session_helper.exe",
        "crossdesk_privacy_cursor_helper.exe",
        "crossdesk_updater.exe"
    ];

    public static readonly RemoteIdentity Identity = new(
        LogName: "CrossDesk",
        DisplayName: "CrossDesk",
        ProcessNames: ProcessNames,
        SeesScreensOnlyAtLaunch: true,
        OpensOnMainScreen: false,
        NormalizerFactory: static () => new CrossDeskTouch());

    public static RemoteObservation Observation(
        bool running,
        bool sharing,
        IReadOnlySet<uint>? unreachable = null,
        string? slotSummary = null,
        bool logReadable = true)
    {
        bool present = running && sharing;
        string state = !running
            ? "CrossDesk进程=未运行"
            : !logReadable
                ? "CrossDesk日志=读不到"
                : sharing
                    ? "共享抑制=有"
                    : "共享抑制=无";
        string evidence = string.IsNullOrEmpty(slotSummary) ? state : $"{state} 槽位=[{slotSummary}]";

        return new RemoteObservation(
            Present: present,
            Controllers: Array.Empty<string>(),
            StreamedDisplay: null,
            Evidence: evidence,
            UnreachableDisplays: unreachable);
    }
}

/// <summary>
/// Guardrails for silent idle auto-restart when CrossDesk cannot see newly created virtual screens:
/// waits for screen stabilization (debounce), never restarts during an active remote episode,
/// enforces a cooldown, and caps consecutive retries until unseen state clears or screen set changes.
/// </summary>
public sealed class IdleAutoRestartPolicy
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(45);
    public const int DefaultMaxAttempts = 2;

    private readonly TimeSpan _debounce;
    private readonly TimeSpan _cooldown;
    private readonly int _maxAttempts;

    private DateTimeOffset? _unseenSince;
    private DateTimeOffset _lastAttemptAt = DateTimeOffset.MinValue;
    private string _lastScreenSignature = string.Empty;
    private int _attempts;

    public IdleAutoRestartPolicy(
        TimeSpan? debounce = null,
        TimeSpan? cooldown = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        _debounce = debounce ?? DefaultDebounce;
        _cooldown = cooldown ?? DefaultCooldown;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    public int Attempts => _attempts;

    public bool ShouldRestart(
        DateTimeOffset now,
        bool enabled,
        bool elevated,
        bool unseenCrossDesk,
        bool anyRemoteActive,
        bool anyScreenReady,
        bool restartInFlight,
        string screenSignature)
    {
        if (!string.Equals(screenSignature, _lastScreenSignature, StringComparison.Ordinal))
        {
            _lastScreenSignature = screenSignature;
            _attempts = 0;
            _unseenSince = null;
        }

        if (!unseenCrossDesk)
        {
            _unseenSince = null;
            _attempts = 0;
            return false;
        }

        if (!enabled || !elevated || anyRemoteActive || !anyScreenReady || restartInFlight)
        {
            _unseenSince = null;
            return false;
        }

        _unseenSince ??= now;
        if (now - _unseenSince.Value < _debounce)
            return false;
        if (_attempts >= _maxAttempts)
            return false;
        if (_lastAttemptAt != DateTimeOffset.MinValue && now - _lastAttemptAt < _cooldown)
            return false;

        return true;
    }

    public void RecordAttempt(DateTimeOffset now)
    {
        _attempts++;
        _lastAttemptAt = now;
        _unseenSince = null;
    }
}
