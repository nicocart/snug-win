using System;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Remote;
using SnugWin.Core.Remotes.CrossDesk;
using SnugWin.Core.Session;
using SnugWin.Core.Support;
using SnugWin.Windows.Display;

namespace SnugWin.Windows.Sensing;

/// <summary>
/// CrossDesk remote adapter for Windows:
/// watches CrossDesk process liveness via <see cref="UUProcessWatch"/>,
/// tails <c>%LOCALAPPDATA%\CrossDesk\logs\crossdesk.log</c> for sharing power-inhibitor state
/// and startup display slot enumeration, and reports any online Snug displays that CrossDesk
/// cannot see without a restart.
/// </summary>
public sealed class CrossDeskRemoteAdapter : IRemoteAdapter
{
    private readonly UUProcessWatch _processWatch;
    private readonly CrossDeskLogTail _tail;
    private readonly CrossDeskSlots _slots = new();
    private readonly Func<bool> _isInstalled;
    private readonly Func<IReadOnlyList<string>> _onlineDeviceNames;

    private int? _lastPid;
    private bool _hasObservedProcess;
    private bool _sharing;
    private bool _tailFailed;

    public CrossDeskRemoteAdapter()
        : this(
            logPath: null,
            findPid: CrossDeskHost.FindRunningPid,
            isAlive: CrossDeskHost.IsProcessAlive,
            isInstalled: CrossDeskHost.IsInstalled,
            onlineDeviceNames: static () => DisplayConfigService.GetActiveMonitors().Select(m => m.DeviceName).ToArray())
    {
    }

    internal CrossDeskRemoteAdapter(
        string? logPath,
        Func<int?> findPid,
        Func<int, bool> isAlive,
        Func<bool> isInstalled,
        Func<IReadOnlyList<string>> onlineDeviceNames)
    {
        _processWatch = new UUProcessWatch(findPid, isAlive);
        _tail = new CrossDeskLogTail(logPath);
        _isInstalled = isInstalled;
        _onlineDeviceNames = onlineDeviceNames;
    }

    public RemoteIdentity Identity => CrossDeskRemote.Identity;

    public bool IsInstalled() => _isInstalled();

    public RemoteReading Sample(
        DateTimeOffset wallClock,
        bool wasConnected,
        IReadOnlyList<SnugDisplayInfo> snugDisplays)
    {
        int? pid = _processWatch.Resolve(wallClock);

        if (!_hasObservedProcess)
        {
            _hasObservedProcess = true;
            _lastPid = pid;
            if (pid is not null)
                Log.Info($"CrossDesk 进程：{pid}");
        }
        else if (pid != _lastPid)
        {
            _lastPid = pid;
            _tail.Reset();
            _slots.Reset();
            _sharing = false;
            _tailFailed = false;
            Log.Info(pid is { } activePid ? $"CrossDesk 进程：{activePid}" : "CrossDesk 进程：没在运行");
        }

        if (pid is null)
        {
            return new RemoteReading(
                Identity,
                CrossDeskRemote.Observation(running: false, sharing: false),
                wallClock);
        }

        try
        {
            string? beforeSummary = _slots.Summary;
            var newLines = _tail.Poll();
            foreach (var raw in newLines)
            {
                var parsed = CrossDeskLogLine.Parse(raw);
                if (parsed is null) continue;
                _slots.Take(parsed);
                switch (parsed.Event)
                {
                    case CrossDeskLogEvent.Started:
                        _sharing = false;
                        break;
                    case CrossDeskLogEvent.SharingBegan:
                        _sharing = true;
                        break;
                    case CrossDeskLogEvent.SharingEnded:
                        _sharing = false;
                        break;
                }
            }

            if (_tailFailed)
            {
                Log.Info("CrossDesk 日志又读得到了");
                _tailFailed = false;
            }

            string? afterSummary = _slots.Summary;
            if (!string.IsNullOrEmpty(afterSummary) && afterSummary != beforeSummary)
                Log.Info($"CrossDesk 各格推的屏：{afterSummary}");
        }
        catch (Exception ex)
        {
            if (!_tailFailed)
            {
                Log.Error($"读不了 CrossDesk 日志（{ex.Message}）：看不出在场状态与认屏情况");
                _tailFailed = true;
            }
        }

        IReadOnlySet<uint>? unreachable = null;
        if (_slots.Displays is not null)
        {
            var onlineNames = _onlineDeviceNames();
            unreachable = _slots.Unreachable(snugDisplays, onlineNames);
        }

        return new RemoteReading(
            Identity,
            CrossDeskRemote.Observation(
                running: true,
                sharing: _sharing,
                unreachable: unreachable,
                slotSummary: _slots.Summary,
                logReadable: !_tailFailed),
            wallClock);
    }
}
