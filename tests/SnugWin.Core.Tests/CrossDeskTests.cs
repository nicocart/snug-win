using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Remote;
using SnugWin.Core.Remotes.CrossDesk;
using SnugWin.Core.Touch;
using SnugWin.Windows.Sensing;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class CrossDeskTests
{
    private static string LogLine(string clock, string message, string? source = "x.cpp:1") =>
        source is null
            ? $"[2026-11-24 {clock}] [crossdesk] [info] {message}"
            : $"[2026-11-24 {clock}] [crossdesk] [info] [{source}] {message}";

    [Fact]
    public void LogLine_ParsesWindowsDxgiWgcAndSessionEvents()
    {
        var lines = new[]
        {
            LogLine("20:20:27.732", "Logger initialized", source: null),
            LogLine("20:20:27.732", "Build identity: CrossDesk=abc MiniRTC=def", source: null),
            LogLine("20:20:29.198", "DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;2560,1600), selected=true", "screen_capturer_dxgi.cpp:223"),
            LogLine("20:20:29.198", @"DXGI: session display slot=1 name='\\.\DISPLAY2' available=true bounds=(-540,0;540,950), selected=false", "screen_capturer_dxgi.cpp:223"),
            LogLine("20:20:30.000", "Requested display and system idle-sleep inhibition for screen sharing", "screen_sharing_power.cpp:238"),
            LogLine("20:25:00.000", "Last remote controller disconnected; recovering privacy, reason=connection_closed", "connection_runtime.cpp:236"),
            LogLine("20:25:09.500", "Released screen sharing power inhibitors", "screen_sharing_power.cpp:241"),
            LogLine("20:26:00.000", "Windows capturer ignoring unregistered display after topology change: display='DISPLAY3', index=2", "screen_capturer_win.cpp:400"),
            LogLine("20:26:01.000", @"index: 0, display name: \\.\DISPLAY1, is primary: 1, bounds: (0, 0) - (1920, 1080)", "screen_capturer_wgc.cpp:120"),
            LogLine("21:01:37.431", "Privacy: state=2, overlay=false, input_blocked=false, reason=Enabling privacy on connection", "privacy_controller.cpp:64"),
            LogLine("21:01:37.531", "System audio capture started (attempt 1)", "speaker_capture_controller.cpp:122"),
            LogLine("21:01:38.061", "Local display [1:DISPLAY1]", "peer_event_handler.cpp:665"),
            LogLine("21:01:38.860", "Privacy: state=5, overlay=false, input_blocked=false, reason=A visible window may overlap privacy coverage", "privacy_controller.cpp:64"),
            LogLine("21:37:37.353", "GUI attached to persistent host; no second signaling login", "gui_runtime.cpp:388")
        };

        var events = lines.Select(l => CrossDeskLogLine.Parse(l)?.Event).ToArray();
        Assert.IsType<CrossDeskLogEvent.Started>(events[0]);
        Assert.Null(events[1]);

        var d0 = Assert.IsType<CrossDeskLogEvent.DisplaySlot>(events[2]);
        Assert.Equal(0, d0.Slot);
        Assert.Equal("DISPLAY1", d0.Name);
        Assert.True(d0.Available);
        Assert.True(d0.Selected);
        Assert.Equal(new RectD(0, 0, 2560, 1600), d0.Bounds);

        var d1 = Assert.IsType<CrossDeskLogEvent.DisplaySlot>(events[3]);
        Assert.Equal(1, d1.Slot);
        Assert.Equal("DISPLAY2", d1.Name);
        Assert.Equal(new RectD(-540, 0, 540, 950), d1.Bounds);

        Assert.IsType<CrossDeskLogEvent.SharingBegan>(events[4]);
        Assert.IsType<CrossDeskLogEvent.LastControllerLeft>(events[5]);
        Assert.IsType<CrossDeskLogEvent.SharingEnded>(events[6]);

        var ignored = Assert.IsType<CrossDeskLogEvent.UnregisteredDisplayIgnored>(events[7]);
        Assert.Equal("DISPLAY3", ignored.Name);
        Assert.Equal(2, ignored.Index);

        var wgc = Assert.IsType<CrossDeskLogEvent.DisplaySlot>(events[8]);
        Assert.Equal(0, wgc.Slot);
        Assert.Equal("DISPLAY1", wgc.Name);
        Assert.Equal(new RectD(0, 0, 1920, 1080), wgc.Bounds);

        Assert.IsType<CrossDeskLogEvent.ControllerConnected>(events[9]);
        Assert.IsType<CrossDeskLogEvent.ControllerConnected>(events[10]);
        Assert.IsType<CrossDeskLogEvent.ControllerConnected>(events[11]);
        Assert.Null(events[12]);
        Assert.IsType<CrossDeskLogEvent.AttachedToPersistentHost>(events[13]);
    }

    [Theory]
    [InlineData("[2026-11-24 20:20:27.732] [minirtc] [info] Logger initialized")]
    [InlineData("[2026-11-24 20:20:27] [crossdesk] [info] Logger initialized")]
    [InlineData("2026-11-24 20:20:27.732 [crossdesk] [info] Logger initialized")]
    [InlineData("[2026-11-24 20:20:27.732] [crossdesk] [info] [x.cpp:1] Requested display and system idle-sleep inhibition for scr")]
    [InlineData("[2026-11-24 20:20:27.732] [crossdesk] [info] [x.cpp:1] DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;2560,")]
    public void LogLine_RejectsMalformedAndForeignLines(string line)
    {
        Assert.Null(CrossDeskLogLine.Parse(line));
    }

    [Fact]
    public void LogLine_LastInstanceSlicesFromLatestStarted()
    {
        var parsed = new[]
        {
            CrossDeskLogLine.Parse(LogLine("20:00:00.000", "Logger initialized", null))!,
            CrossDeskLogLine.Parse(LogLine("20:00:01.000", "Requested display and system idle-sleep inhibition for screen sharing"))!,
            CrossDeskLogLine.Parse(LogLine("20:10:00.000", "Logger initialized", null))!,
            CrossDeskLogLine.Parse(LogLine("20:10:01.000", "DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;1920,1080), selected=true"))!
        };

        var last = CrossDeskLogLine.LastInstance(parsed);
        Assert.Equal(2, last.Count);
        Assert.IsType<CrossDeskLogEvent.Started>(last[0].Event);
        Assert.IsType<CrossDeskLogEvent.DisplaySlot>(last[1].Event);
    }

    [Fact]
    public void LogFollower_HandlesInitialWindowPartialLinesAndRotation()
    {
        var follower = new CrossDeskLogFollower();
        long bigSize = CrossDeskLogFollower.InitialWindow + 100;
        Assert.Equal((100L, (int)CrossDeskLogFollower.InitialWindow), follower.NextRange(7, bigSize));
        Assert.Equal(new[] { "first", "second" }, follower.Feed(Encoding.UTF8.GetBytes("partial\r\nfirst\r\nsecond\r\n")));

        var small = new CrossDeskLogFollower();
        var part1 = Encoding.UTF8.GetBytes("one\r\nhalf");
        var part2 = Encoding.UTF8.GetBytes("-line\r\n");
        Assert.Null(small.NextRange(1, 0));
        Assert.Equal((0L, part1.Length), small.NextRange(1, part1.Length));
        Assert.Equal(new[] { "one" }, small.Feed(part1));
        Assert.Equal((part1.Length, part2.Length), small.NextRange(1, part1.Length + part2.Length));
        Assert.Equal(new[] { "half-line" }, small.Feed(part2));

        // File ID change (rotation) resets offset & discards pending bytes
        _ = small.NextRange(1, part1.Length + part2.Length + 3);
        _ = small.Feed(Encoding.UTF8.GetBytes("old"));
        Assert.Equal((0L, 4), small.NextRange(2, 4));
        Assert.Equal(new[] { "new" }, small.Feed(Encoding.UTF8.GetBytes("new\n")));

        // Truncation resets offset
        Assert.Equal((0L, 2), small.NextRange(2, 2));
    }

    [Fact]
    public void Slots_TracksStartupDisplaysAndComputesUnreachable()
    {
        var slots = new CrossDeskSlots();
        var snug2 = new SnugDisplayInfo(2, @"\\.\DISPLAY2", new RectD(2560, 0, 540, 950));
        var snug3 = new SnugDisplayInfo(3, @"\\.\DISPLAY3", new RectD(3100, 0, 540, 950));

        // Unknown until startup slots observed
        Assert.Null(slots.Unreachable([snug2], ["DISPLAY1", "DISPLAY2"]));

        slots.Take(LogLine("20:00:00.000", "Logger initialized", null));
        slots.Take(LogLine("20:00:00.100", "DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;2560,1600), selected=true"));
        slots.Take(LogLine("20:00:00.100", "DXGI: session display slot=1 name='DISPLAY2' available=true bounds=(2560,0;540,950), selected=false"));

        // Match existing slot -> reachable
        Assert.Empty(slots.Unreachable([snug2], ["DISPLAY1", "DISPLAY2"])!);

        // New screen created after CrossDesk launched (DISPLAY3), DISPLAY1+DISPLAY2 still online -> unreachable
        var unreachable = slots.Unreachable([snug2, snug3], ["DISPLAY1", "DISPLAY2", "DISPLAY3"])!;
        Assert.Equal([3u], unreachable);

        // Recreated Snug screen moved from DISPLAY2 to DISPLAY3 (DISPLAY2 went offline -> 1 vacant slot) -> reachable
        Assert.Empty(slots.Unreachable([snug3], ["DISPLAY1", "DISPLAY3"])!);

        // New instance resets slots
        slots.Take(LogLine("20:10:00.000", "Logger initialized", null));
        Assert.Null(slots.Displays);
        slots.Take(LogLine("20:10:00.100", "DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;2560,1600), selected=true"));
        Assert.Equal([2u], slots.Unreachable([snug2], ["DISPLAY1", "DISPLAY2"])!);
    }

    [Fact]
    public void CrossDeskTouch_NormalizesTapHoldDragAndVirtualButtons()
    {
        // Tap: move + leftDown + leftUp within 20ms
        var tap = RunNormalizer([
            Ev(PointerEventKind.Move, 200, 300, 1.0),
            Ev(PointerEventKind.LeftDown, 200, 300, 1.0005),
            Ev(PointerEventKind.LeftUp, 200, 300, 1.002)
        ]);
        Assert.Equal([TouchInputKind.Press, TouchInputKind.Release], tap.Inputs.Select(i => i.Kind).ToArray());
        Assert.Empty(tap.Problems);

        // Hold + release: >= 0.15s after LeftDown -> Recognized(Hold)
        var holdRelease = RunNormalizer([
            Ev(PointerEventKind.Move, 200, 300, 1.0),
            Ev(PointerEventKind.LeftDown, 200, 300, 1.0005),
            Ev(PointerEventKind.LeftUp, 200, 300, 1.16)
        ]);
        Assert.Equal(
            [TouchInputKind.Press, TouchInputKind.Recognized, TouchInputKind.Release],
            holdRelease.Inputs.Select(i => i.Kind).ToArray());
        Assert.Equal(TouchGesture.Hold, holdRelease.Inputs[1].Gesture);
        Assert.Empty(holdRelease.Problems);

        // Hold + drag: first drag emits Recognized(Hold)
        var holdDrag = RunNormalizer([
            Ev(PointerEventKind.Move, 200, 300, 1.0),
            Ev(PointerEventKind.LeftDown, 200, 300, 1.0005),
            Ev(PointerEventKind.LeftDrag, 210, 300, 1.05),
            Ev(PointerEventKind.LeftDrag, 240, 300, 1.07),
            Ev(PointerEventKind.LeftUp, 240, 300, 1.3)
        ]);
        Assert.Equal(
            [TouchInputKind.Press, TouchInputKind.Recognized, TouchInputKind.Drag, TouchInputKind.Drag, TouchInputKind.Release],
            holdDrag.Inputs.Select(i => i.Kind).ToArray());
        Assert.Equal(TouchGesture.Hold, holdDrag.Inputs[1].Gesture);
        Assert.Empty(holdDrag.Problems);

        // Virtual mouse button (no preceding same-place move within 20ms) passes through
        var virt = RunNormalizer([
            Ev(PointerEventKind.LeftDown, 200, 300, 1.0),
            Ev(PointerEventKind.LeftDrag, 230, 300, 1.2),
            Ev(PointerEventKind.LeftUp, 230, 300, 1.4)
        ]);
        Assert.Empty(virt.Inputs);
    }

    [Fact]
    public void CrossDeskTouch_ZeroPixelDragBeforeLeftUp_StaysLeftClickNotRightClick()
    {
        var region = new TouchRegion(new RectD(2560, 0, 1080, 1900), 1.0);
        var pipeline = new TouchPipeline([static () => new CrossDeskTouch()]);

        var pt = new PointD(3100, 617);
        var move = pipeline.Take(Ev(PointerEventKind.Move, pt.X, pt.Y, 1.000), 0, [region]);
        Assert.True(move.PassOriginal);

        var down = pipeline.Take(Ev(PointerEventKind.LeftDown, pt.X, pt.Y, 1.001), 0, [region]);
        Assert.False(down.PassOriginal);

        // Windows SendInput(MOVE | LEFTUP) emits a 0-pixel drag at the same coordinate right before LeftUp.
        var zeroDrag = pipeline.Take(Ev(PointerEventKind.LeftDrag, pt.X, pt.Y, 1.050), 0, [region]);
        Assert.True(zeroDrag.PassOriginal);
        Assert.Empty(zeroDrag.Actions);

        var up = pipeline.Take(Ev(PointerEventKind.LeftUp, pt.X, pt.Y, 1.051), 0, [region]);
        Assert.False(up.PassOriginal);
        var cmds = up.Actions.OfType<TouchAction.Perform>().Select(a => a.Command).ToArray();
        Assert.Equal(2, cmds.Length);
        Assert.IsType<TouchCommand.ReplayPress>(cmds[0]);
        Assert.IsType<TouchCommand.ReplayCurrent>(cmds[1]);
    }

    [Fact]
    public void CrossDeskTouch_AbsoluteSlideProducesScrollAndCursorFeedPassesMoves()
    {
        var region = new TouchRegion(new RectD(0, 0, 540, 950), 1.0);
        var pipeline = new TouchPipeline([static () => new CrossDeskTouch()]);

        // Warm up finger mode: tap at (100, 100), then move >= 80px away at (300, 400)
        var warmUp = new[]
        {
            Ev(PointerEventKind.Move, 100, 100, 0.0),
            Ev(PointerEventKind.LeftDown, 100, 100, 0.0005),
            Ev(PointerEventKind.LeftUp, 100, 100, 0.001),
            Ev(PointerEventKind.Move, 300, 400, 1.0),
            Ev(PointerEventKind.Move, 300, 440, 1.016),
            Ev(PointerEventKind.Move, 300, 490, 1.032)
        };

        var scrolls = new List<ScrollStep>();
        foreach (var ev in warmUp)
        {
            var step = pipeline.Take(ev, 0, [region]);
            scrolls.AddRange(step.Actions.OfType<TouchAction.Perform>()
                .Select(a => a.Command)
                .OfType<TouchCommand.Scroll>()
                .Select(s => s.Step));
        }

        Assert.Contains(scrolls, s => s.Phase == ScrollPhase.Began);
        Assert.Contains(scrolls, s => s.Phase == ScrollPhase.Changed);
        Assert.NotNull(pipeline.Deadline);

        // Tick past slideEnd -> Release + ScrollEnded + momentum
        var tick = pipeline.Tick(pipeline.Deadline!.Value + 0.01, [region]);
        var endScrolls = tick.Actions.OfType<TouchAction.Perform>()
            .Select(a => a.Command)
            .OfType<TouchCommand.Scroll>()
            .Select(s => s.Step)
            .ToArray();
        Assert.Contains(endScrolls, s => s.Phase == ScrollPhase.Ended);
        Assert.True(pipeline.IsCoasting);

        // Relative mouse: 3 consecutive gestures starting at same point -> Cursor feed, moves ignored
        var cursorTouch = new CrossDeskTouch();
        var cursorEvents = new List<PointerEvent>();
        foreach (double t in new[] { 1.0, 2.0, 3.0, 4.0 })
        {
            cursorEvents.Add(Ev(PointerEventKind.Move, 300, 400, t));
            cursorEvents.Add(Ev(PointerEventKind.LeftDown, 300, 400, t + 0.0005));
            cursorEvents.Add(Ev(PointerEventKind.LeftUp, 300, 400, t + 0.001));
        }
        var cursorRun = RunNormalizer(cursorEvents, cursorTouch);
        Assert.Equal(CrossDeskTouch.FeedKind.Cursor, cursorTouch.Feed);
        Assert.Empty(cursorRun.Problems);

        int prevCount = cursorRun.Inputs.Count;
        Assert.Null(cursorTouch.Normalize(Ev(PointerEventKind.Move, 300, 400, 5.0)).Current);
        Assert.Null(cursorTouch.Normalize(Ev(PointerEventKind.Move, 350, 400, 5.02)).Current);
        Assert.Equal(prevCount, cursorRun.Inputs.Count);
    }

    [Fact]
    public void RemoteAdapter_TailsLogTracksSharingAndResetsOnExit()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"snug-crossdesk-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "crossdesk.log");
        try
        {
            File.WriteAllLines(logPath,
            [
                LogLine("20:00:00.000", "Logger initialized", null),
                LogLine("20:00:00.100", "DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;2560,1600), selected=true"),
                LogLine("20:01:00.000", "Requested display and system idle-sleep inhibition for screen sharing")
            ]);

            int? currentPid = 1234;
            var online = new[] { "DISPLAY1", "DISPLAY2" };
            var adapter = new CrossDeskRemoteAdapter(
                logPath,
                findPid: () => currentPid,
                isAlive: pid => currentPid == pid,
                isInstalled: () => true,
                onlineDeviceNames: () => online);

            var snug2 = new SnugDisplayInfo(2, @"\\.\DISPLAY2", new RectD(2560, 0, 540, 950));
            var t0 = DateTimeOffset.Now;

            // Bootstraps active sharing session and detects unseen Snug display
            var r1 = adapter.Sample(t0, wasConnected: false, [snug2]);
            Assert.True(r1.Observation.Present);
            Assert.Equal([2u], r1.Observation.UnreachableDisplays);

            // Sharing ends in log
            using (var sw = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)))
            {
                sw.WriteLine(LogLine("20:02:00.000", "Released screen sharing power inhibitors"));
            }

            var r2 = adapter.Sample(t0.AddSeconds(1), wasConnected: true, [snug2]);
            Assert.False(r2.Observation.Present);

            // Sharing begins again, then CrossDesk process dies -> must drop Present immediately
            using (var sw = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)))
            {
                sw.WriteLine(LogLine("20:03:00.000", "Requested display and system idle-sleep inhibition for screen sharing"));
            }

            var r3 = adapter.Sample(t0.AddSeconds(2), wasConnected: false, [snug2]);
            Assert.True(r3.Observation.Present);

            currentPid = null;
            var r4 = adapter.Sample(t0.AddSeconds(3), wasConnected: true, [snug2]);
            Assert.False(r4.Observation.Present);
            Assert.Null(r4.Observation.UnreachableDisplays);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RemoteAdapter_RecognizesWindowsV177ConnectAndDisconnectAcrossMultipleSessions()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"snug-crossdesk-v177-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "crossdesk.log");
        try
        {
            // Startup sequence from Windows CrossDesk v1.7.7-20261124 (no idle-sleep inhibitor lines)
            File.WriteAllLines(logPath,
            [
                LogLine("21:01:08.936", "Logger initialized", null),
                LogLine("21:01:08.968", "Privacy: state=1, overlay=false, input_blocked=false, reason=High-band privacy screen available", "privacy_controller.cpp:64"),
                LogLine("21:01:09.536", "Init screen capturer with 60 fps", "session_device_manager.cpp:191"),
                LogLine("21:01:09.543", "DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;2560,1600), selected=true", "screen_capturer_dxgi.cpp:223"),
                LogLine("21:01:09.543", "DXGI: session display slot=1 name='DISPLAY19' available=true bounds=(2560,0;3640,1900), selected=false", "screen_capturer_dxgi.cpp:223"),
                LogLine("21:01:09.543", "Init screen capturer success", "session_device_manager.cpp:303"),
                LogLine("21:01:09.543", "GUI attached to persistent host; no second signaling login", "gui_runtime.cpp:388")
            ]);

            int? currentPid = 32008;
            var online = new[] { "DISPLAY1", "DISPLAY19" };
            var adapter = new CrossDeskRemoteAdapter(
                logPath,
                findPid: () => currentPid,
                isAlive: pid => currentPid == pid,
                isInstalled: () => true,
                onlineDeviceNames: () => online);

            var snug1 = new SnugDisplayInfo(0, @"\\.\DISPLAY19", new RectD(2560, 0, 1080, 1900));
            var t0 = DateTimeOffset.Now;

            var idle = adapter.Sample(t0, wasConnected: false, [snug1]);
            Assert.False(idle.Observation.Present);

            void AppendLines(params string[] lines)
            {
                using var sw = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                foreach (var l in lines) sw.WriteLine(l);
            }

            // Connect #1: privacy-on-connect + audio capture + Local display + overlap fallback (overlay=false while still connected)
            AppendLines(
                LogLine("21:01:37.431", "Privacy: state=2, overlay=false, input_blocked=false, reason=Enabling privacy on connection", "privacy_controller.cpp:64"),
                LogLine("21:01:37.531", "System audio capture started (attempt 1)", "speaker_capture_controller.cpp:122"),
                LogLine("21:01:38.061", "Local display [1:DISPLAY1]", "peer_event_handler.cpp:665"),
                LogLine("21:01:38.061", "Local display [2:DISPLAY19]", "peer_event_handler.cpp:665"),
                LogLine("21:01:38.860", "Privacy: state=5, overlay=false, input_blocked=false, reason=A visible window may overlap privacy coverage", "privacy_controller.cpp:64"));

            var c1 = adapter.Sample(t0.AddSeconds(1), wasConnected: false, [snug1]);
            Assert.True(c1.Observation.Present);

            // Disconnect #1: Last remote controller disconnected;
            AppendLines(
                LogLine("21:02:04.335", "Last remote controller disconnected; recovering privacy, reason=connection_closed", "connection_runtime.cpp:246"),
                LogLine("21:02:06.086", "Privacy: state=0, overlay=false, input_blocked=false, reason=Privacy screen requires the unlocked Default desktop", "privacy_controller.cpp:64"),
                LogLine("21:02:08.086", "Privacy: state=1, overlay=false, input_blocked=false, reason=High-band privacy screen available", "privacy_controller.cpp:64"));

            var d1 = adapter.Sample(t0.AddSeconds(2), wasConnected: true, [snug1]);
            Assert.False(d1.Observation.Present);

            // Connect #2: without Local display lines
            AppendLines(
                LogLine("21:02:20.945", "Privacy: state=2, overlay=false, input_blocked=false, reason=Enabling privacy on connection", "privacy_controller.cpp:64"),
                LogLine("21:02:21.044", "System audio capture started (attempt 1)", "speaker_capture_controller.cpp:122"),
                LogLine("21:02:22.311", "Privacy: state=5, overlay=false, input_blocked=false, reason=A visible window may overlap privacy coverage", "privacy_controller.cpp:64"));

            var c2 = adapter.Sample(t0.AddSeconds(3), wasConnected: false, [snug1]);
            Assert.True(c2.Observation.Present);

            // Disconnect #2
            AppendLines(
                LogLine("21:02:43.010", "Last remote controller disconnected; recovering privacy, reason=connection_closed", "connection_runtime.cpp:246"));

            var d2 = adapter.Sample(t0.AddSeconds(4), wasConnected: true, [snug1]);
            Assert.False(d2.Observation.Present);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RemoteAdapter_AttachedGuiSlotsRollbackToPersistentHostInSameLog()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"snug-crossdesk-gui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "crossdesk.log");
        try
        {
            // Persistent host started at 22:36:52 with only DISPLAY1.
            // Later, a GUI window opens at 22:58:00 (after DISPLAY57 came online), logs DISPLAY57, and attaches to host.
            File.WriteAllLines(logPath,
            [
                LogLine("22:36:52.000", "Logger initialized", null),
                LogLine("22:36:52.200", "DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;2560,1600), selected=true", "screen_capturer_dxgi.cpp:223"),
                LogLine("22:58:00.000", "Logger initialized", null),
                LogLine("22:58:00.100", "DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;2560,1600), selected=true", "screen_capturer_dxgi.cpp:223"),
                LogLine("22:58:00.100", "DXGI: session display slot=1 name='DISPLAY57' available=true bounds=(2560,0;540,950), selected=false", "screen_capturer_dxgi.cpp:223"),
                LogLine("22:58:00.150", "GUI attached to persistent host; no second signaling login", "gui_runtime.cpp:388")
            ]);

            int? pid = 43652;
            var t0 = DateTimeOffset.Now;
            DateTimeOffset? oldestStart = t0.AddMinutes(-21);
            var online = new[] { "DISPLAY1", "DISPLAY57" };
            var adapter = new CrossDeskRemoteAdapter(
                logPath,
                findPid: () => pid,
                isAlive: p => pid == p,
                isInstalled: () => true,
                onlineDeviceNames: () => online,
                oldestStartTime: () => oldestStart);

            var snug57 = new SnugDisplayInfo(0, @"\\.\DISPLAY57", new RectD(2560, 0, 540, 950), OnlineAt: t0.AddSeconds(-30));

            // GUI's DISPLAY57 must be rolled back to persistent host's DISPLAY1 -> DISPLAY57 is unreachable!
            var r1 = adapter.Sample(t0, wasConnected: false, [snug57]);
            Assert.Equal([0u], r1.Observation.UnreachableDisplays);
            Assert.Contains("DISPLAY1", r1.Observation.Evidence);
            Assert.DoesNotContain("DISPLAY57", r1.Observation.Evidence);

            // Restart CrossDesk (new PID + fresh host startup sees DISPLAY57 without AttachedToPersistentHost)
            pid = 51000;
            oldestStart = t0.AddSeconds(5);
            using (var sw = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)))
            {
                sw.WriteLine(LogLine("22:59:00.000", "Logger initialized", null));
                sw.WriteLine(LogLine("22:59:00.200", "DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;2560,1600), selected=true", "screen_capturer_dxgi.cpp:223"));
                sw.WriteLine(LogLine("22:59:00.200", "DXGI: session display slot=1 name='DISPLAY57' available=true bounds=(2560,0;540,950), selected=false", "screen_capturer_dxgi.cpp:223"));
            }

            var r2 = adapter.Sample(t0.AddSeconds(6), wasConnected: false, [snug57]);
            Assert.NotNull(r2.Observation.UnreachableDisplays);
            Assert.Empty(r2.Observation.UnreachableDisplays!);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RemoteAdapter_OldestCrossDeskStartTimeBeforeOnlineAtMarksDisplayUnreachable()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"snug-crossdesk-time-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "crossdesk.log");
        try
        {
            // Log only contains a GUI attach block (host startup rotated out), so slot map is null.
            File.WriteAllLines(logPath,
            [
                LogLine("22:58:00.000", "Logger initialized", null),
                LogLine("22:58:00.100", "DXGI: session display slot=0 name='DISPLAY1' available=true bounds=(0,0;2560,1600), selected=true", "screen_capturer_dxgi.cpp:223"),
                LogLine("22:58:00.100", "DXGI: session display slot=1 name='DISPLAY57' available=true bounds=(2560,0;540,950), selected=false", "screen_capturer_dxgi.cpp:223"),
                LogLine("22:58:00.150", "GUI attached to persistent host; no second signaling login", "gui_runtime.cpp:388")
            ]);

            int? pid = 43652;
            var t0 = DateTimeOffset.Now;
            DateTimeOffset? oldestStart = t0.AddMinutes(-20);
            var adapter = new CrossDeskRemoteAdapter(
                logPath,
                findPid: () => pid,
                isAlive: p => pid == p,
                isInstalled: () => true,
                onlineDeviceNames: () => ["DISPLAY1", "DISPLAY57"],
                oldestStartTime: () => oldestStart);

            var snug57 = new SnugDisplayInfo(0, @"\\.\DISPLAY57", new RectD(2560, 0, 540, 950), OnlineAt: t0.AddMinutes(-1));

            // Even with null committed slots, oldestStart < OnlineAt marks display #0 unreachable.
            var r1 = adapter.Sample(t0, wasConnected: false, [snug57]);
            Assert.Equal([0u], r1.Observation.UnreachableDisplays);

            // Once CrossDesk restarts AFTER display OnlineAt, timestamp guard clears.
            pid = 52000;
            oldestStart = t0.AddSeconds(5);
            var r2 = adapter.Sample(t0.AddSeconds(6), wasConnected: false, [snug57]);
            Assert.True(r2.Observation.UnreachableDisplays is null || r2.Observation.UnreachableDisplays.Count == 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static PointerEvent Ev(PointerEventKind kind, double x, double y, double t) =>
        new(kind, new PointD(x, y), t);

    private static (List<TouchInput> Inputs, List<string> Problems) RunNormalizer(
        IEnumerable<PointerEvent> events,
        CrossDeskTouch? touch = null)
    {
        touch ??= new CrossDeskTouch();
        var grammar = new TouchGrammar();
        var inputs = new List<TouchInput>();
        var problems = new List<string>();

        void Take(IEnumerable<TouchInput> batch, bool synthesized)
        {
            foreach (var item in batch)
            {
                inputs.Add(item);
                if (grammar.Check(item, synthesized) is { } err)
                    problems.Add(err);
            }
        }

        foreach (var ev in events)
        {
            while (touch.Deadline is { } due && due <= ev.Time)
                Take(touch.Tick(due), synthesized: true);

            var n = touch.Normalize(ev);
            Take(n.Before, synthesized: true);
            if (n.Current is { } current)
                Take([current], synthesized: false);
        }

        return (inputs, problems);
    }
}
