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
