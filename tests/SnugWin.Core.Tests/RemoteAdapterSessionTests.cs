using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Remote;
using SnugWin.Core.Session;
using SnugWin.Core.Support;
using SnugWin.Core.Touch;
using SnugWin.Windows.Windows;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class RemoteAdapterSessionTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static RemoteIdentity CreateTestIdentity(
        string logName,
        string? displayName = null,
        IReadOnlyList<string>? processNames = null,
        bool seesScreensOnlyAtLaunch = false,
        bool opensOnMainScreen = false,
        Func<ITouchNormalizer>? normalizerFactory = null) =>
        new(
            LogName: logName,
            DisplayName: displayName ?? logName,
            ProcessNames: processNames ?? new[] { $"{logName}.exe" },
            SeesScreensOnlyAtLaunch: seesScreensOnlyAtLaunch,
            OpensOnMainScreen: opensOnMainScreen,
            NormalizerFactory: normalizerFactory);

    private static RemoteReading Reading(
        RemoteIdentity identity,
        bool present,
        DateTimeOffset observedAt,
        string? evidence = null,
        IReadOnlySet<uint>? unreachableDisplays = null,
        params string[] controllers) =>
        new(
            identity,
            new RemoteObservation(
                Present: present,
                Controllers: controllers,
                StreamedDisplay: null,
                Evidence: evidence,
                UnreachableDisplays: unreachableDisplays),
            observedAt);

    [Fact]
    public void RemoteIdentity_EqualityAndHashCodeByLogNameOrdinalIgnoreCase()
    {
        var a = CreateTestIdentity("CrossDesk", "CrossDesk Desktop", new[] { "CrossDesk.exe" }, seesScreensOnlyAtLaunch: true);
        var b = CreateTestIdentity("crossdesk", "Different Display", new[] { "other.exe" }, opensOnMainScreen: true);
        var c = CreateTestIdentity("UU", "UU远程");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.IsType<DirectTouch>(a.CreateNormalizer());
    }

    [Fact]
    public void RemoteReading_UnseenAndIsBlind_RespectsOwnerAndSnugDisplays()
    {
        var uu = UURemote.Identity;
        var crossDesk = CreateTestIdentity("CrossDesk", seesScreensOnlyAtLaunch: true);
        var snugDisplays = new HashSet<uint> { 10u, 20u };

        var uuReading = Reading(uu, present: true, BaseTime, unreachableDisplays: null);
        var cdReading = Reading(crossDesk, present: false, BaseTime, unreachableDisplays: new HashSet<uint> { 20u, 99u });

        Assert.Empty(uuReading.Unseen(snugDisplays));
        Assert.Equal(new uint[] { 20u }, cdReading.Unseen(snugDisplays).ToArray());

        // When owner is null, any blind reading triggers IsBlind
        Assert.True(RemoteReading.IsBlind(new[] { uuReading, cdReading }, owner: null, snugDisplays, out var blindWhenIdle));
        Assert.Equal(crossDesk, blindWhenIdle);

        // When owner is UU, blind non-owner CrossDesk is ignored
        Assert.False(RemoteReading.IsBlind(new[] { uuReading, cdReading }, owner: uu, snugDisplays, out var blindForUu));
        Assert.Null(blindForUu);

        // When owner is CrossDesk, its unreachable Snug display is reported
        Assert.True(RemoteReading.IsBlind(new[] { uuReading, cdReading }, owner: crossDesk, snugDisplays, out var blindForCd));
        Assert.Equal(crossDesk, blindForCd);
    }

    [Fact]
    public void RemoteSession_ArbitratesOwnerByRegistrationOrderAndLocksWhileConnected()
    {
        var uu = UURemote.Identity;
        var crossDesk = CreateTestIdentity("CrossDesk");
        var session = new RemoteSession();

        // Both present at start: first in registration order (UU) wins
        var t0 = BaseTime;
        var began = session.Update(new[]
        {
            Reading(uu, true, t0, "uu-ev", controllers: "iPhone 17"),
            Reading(crossDesk, true, t0, "cd-ev-1", controllers: "MacBook")
        }, t0);

        Assert.IsType<SessionTransition.Began>(Assert.Single(began));
        Assert.Equal(uu, session.Owner);
        Assert.Equal(new[] { "iPhone 17" }, session.Controllers);

        // While UU stays connected, CrossDesk cannot steal ownership even if UU has unreadable controllers
        var t1 = t0.AddSeconds(2);
        var noChange = session.Update(new[]
        {
            Reading(uu, true, t1, "uu-ev"),
            Reading(crossDesk, true, t1, "cd-ev-2", controllers: "MacBook")
        }, t1);

        Assert.Empty(noChange);
        Assert.Equal(uu, session.Owner);
        Assert.Equal(new[] { "iPhone 17" }, session.Controllers);

        // If registration order puts CrossDesk first when idle, CrossDesk wins
        var cdFirstSession = new RemoteSession();
        cdFirstSession.Update(new[]
        {
            Reading(crossDesk, true, t0, "cd-ev"),
            Reading(uu, true, t0, "uu-ev")
        }, t0);
        Assert.Equal(crossDesk, cdFirstSession.Owner);
    }

    [Fact]
    public void RemoteSession_NoteOthersLogsOncePerEpisodeAndResetsAfterEnd()
    {
        var uu = UURemote.Identity;
        var crossDesk = CreateTestIdentity("CrossDesk");
        var session = new RemoteSession();
        var logs = new ConcurrentQueue<string>();
        void Capture(LogLevel _, string message, DateTimeOffset __) => logs.Enqueue(message);

        Log.Logged += Capture;
        try
        {
            var token1 = "ep1-" + Guid.NewGuid().ToString("N");
            var t0 = BaseTime;
            session.Update(new[]
            {
                Reading(uu, true, t0, "uu-1"),
                Reading(crossDesk, true, t0, token1)
            }, t0);
            session.Update(new[]
            {
                Reading(uu, true, t0.AddSeconds(1), "uu-2"),
                Reading(crossDesk, true, t0.AddSeconds(1), token1)
            }, t0.AddSeconds(1));

            Assert.Single(logs, m => m.Contains(token1, StringComparison.Ordinal));

            // Disconnect past debounce and expire 15s reconnect window -> Ended clears Owner & _notedOthers
            session.Update(new[]
            {
                Reading(uu, false, t0.AddSeconds(4), "uu-off"),
                Reading(crossDesk, false, t0.AddSeconds(4), "cd-off")
            }, t0.AddSeconds(4));
            var ended = session.Update(new[]
            {
                Reading(uu, false, t0.AddSeconds(20), "uu-off"),
                Reading(crossDesk, false, t0.AddSeconds(20), "cd-off")
            }, t0.AddSeconds(20));

            Assert.IsType<SessionTransition.Ended>(Assert.Single(ended));
            Assert.Null(session.Owner);

            // New episode logs NoteOthers again
            var token2 = "ep2-" + Guid.NewGuid().ToString("N");
            var tNew = t0.AddSeconds(25);
            session.Update(new[]
            {
                Reading(uu, true, tNew, "uu-3"),
                Reading(crossDesk, true, tNew, token2)
            }, tNew);

            Assert.Single(logs, m => m.Contains(token2, StringComparison.Ordinal));
        }
        finally
        {
            Log.Logged -= Capture;
        }
    }

    [Fact]
    public void RemoteSession_ReconnectGraceHandoffFromUUToCrossDeskWithin15s()
    {
        var uu = UURemote.Identity;
        var crossDesk = CreateTestIdentity("CrossDesk");
        var session = new RemoteSession();

        var t0 = BaseTime;
        var began = session.Update(new[]
        {
            Reading(uu, true, t0, "uu-on", controllers: "iPhone 17"),
            Reading(crossDesk, false, t0, "cd-off")
        }, t0);
        Assert.IsType<SessionTransition.Began>(Assert.Single(began));
        Assert.Equal(uu, session.Owner);
        Assert.Equal(t0, session.EpisodeBeganAt);

        // UU drops past 1.5s debounce -> Interrupted (in 15s reconnect grace)
        var tDrop = t0.AddSeconds(2);
        var interrupted = session.Update(new[]
        {
            Reading(uu, false, tDrop, "uu-off"),
            Reading(crossDesk, false, tDrop, "cd-off")
        }, tDrop);
        Assert.IsType<SessionTransition.Interrupted>(Assert.Single(interrupted));
        Assert.True(session.IsInReconnectGrace);
        Assert.True(session.InEpisode);

        // Within 15s, CrossDesk connects -> Resumed in same episode with Owner switched to CrossDesk
        var tHandoff = tDrop.AddSeconds(6);
        var resumed = session.Update(new[]
        {
            Reading(uu, false, tHandoff, "uu-off"),
            Reading(crossDesk, true, tHandoff, "cd-on", controllers: "iPad Pro")
        }, tHandoff);

        var r = Assert.IsType<SessionTransition.Resumed>(Assert.Single(resumed));
        Assert.Equal(new[] { "iPad Pro" }, r.Controllers);
        Assert.Equal(crossDesk, session.Owner);
        Assert.True(session.IsConnected);
        Assert.True(session.InEpisode);
        Assert.False(session.IsInReconnectGrace);
        Assert.Equal(t0, session.EpisodeBeganAt);
    }

    [Fact]
    public void RemoteSession_EndNow_ClearsOwnerAndNotedOthers()
    {
        var uu = UURemote.Identity;
        var crossDesk = CreateTestIdentity("CrossDesk");
        var session = new RemoteSession();
        var logs = new ConcurrentQueue<string>();
        void Capture(LogLevel _, string message, DateTimeOffset __) => logs.Enqueue(message);

        Log.Logged += Capture;
        try
        {
            var token1 = "endnow1-" + Guid.NewGuid().ToString("N");
            var t0 = BaseTime;
            session.Update(new[]
            {
                Reading(uu, true, t0, "uu-on"),
                Reading(crossDesk, true, t0, token1)
            }, t0);
            Assert.Single(logs, m => m.Contains(token1, StringComparison.Ordinal));

            // Disconnect into reconnect grace
            session.Update(new[]
            {
                Reading(uu, false, t0.AddSeconds(2), "uu-off"),
                Reading(crossDesk, false, t0.AddSeconds(2), "cd-off")
            }, t0.AddSeconds(2));
            Assert.True(session.IsInReconnectGrace);
            Assert.Equal(uu, session.Owner);

            var ended = session.EndNow();
            Assert.IsType<SessionTransition.Ended>(ended);
            Assert.Null(session.Owner);
            Assert.Null(session.EpisodeBeganAt);
            Assert.False(session.InEpisode);
            Assert.False(session.IsInReconnectGrace);

            // Re-connecting logs NoteOthers again because _notedOthers was cleared
            var token2 = "endnow2-" + Guid.NewGuid().ToString("N");
            var tNext = t0.AddSeconds(5);
            session.Update(new[]
            {
                Reading(uu, true, tNext, "uu-on"),
                Reading(crossDesk, true, tNext, token2)
            }, tNext);
            Assert.Single(logs, m => m.Contains(token2, StringComparison.Ordinal));
        }
        finally
        {
            Log.Logged -= Capture;
        }
    }

    [Fact]
    public void UURemote_IdentityAndToObservation_PreserveExpectedContract()
    {
        Assert.Equal("UU", UURemote.Identity.LogName);
        Assert.Equal("UU远程", UURemote.Identity.DisplayName);
        Assert.False(UURemote.Identity.SeesScreensOnlyAtLaunch);
        Assert.True(UURemote.Identity.OpensOnMainScreen);
        Assert.Contains("GameViewer.exe", UURemote.Identity.ProcessNames);
        Assert.Contains("GameViewerServer.exe", UURemote.Identity.ProcessNames);
        Assert.Contains("GameViewerHealthd.exe", UURemote.Identity.ProcessNames);

        var signals = new UUSignals(false, ControllerTable.FromNames("iPhone 17"), true) { ManualOverride = true };
        var obs = signals.ToObservation(streamedDisplay: 42u);

        Assert.True(obs.Present);
        Assert.Equal(new[] { "iPhone 17" }, obs.Controllers);
        Assert.Equal(42u, obs.StreamedDisplay);
        Assert.True(obs.ManualOverride);
        Assert.Equal(signals.ToString(), obs.Evidence);
    }

    [Fact]
    public void WindowService_SetExcludedProcessesAndOverlayClass_WorkAsConfigured()
    {
        try
        {
            WindowService.ResetExcludedProcessesToDefaults();
            Assert.True(WindowService.IsExcludedApp("SnugWin.App.exe"));
            Assert.True(WindowService.IsExcludedApp("GameViewer.exe"));
            Assert.True(WindowService.IsExcludedApp("GameViewerServer.exe"));
            Assert.True(WindowService.IsExcludedApp("CrossDesk.exe"));
            Assert.True(WindowService.IsExcludedApp("crossdesk_service.exe"));
            Assert.False(WindowService.IsExcludedApp("notepad.exe"));

            WindowService.SetExcludedProcesses(new[] { "CustomRemote.exe", "TestRemote_*" });
            Assert.True(WindowService.IsExcludedApp("SnugWin.App.exe"));
            Assert.True(WindowService.IsExcludedApp("CustomRemote.exe"));
            Assert.True(WindowService.IsExcludedApp("TestRemote_helper.exe"));
            Assert.False(WindowService.IsExcludedApp("GameViewer.exe"));

            Assert.True(WindowService.IsExcludedOverlayClass("CrossDesk.PrivacyWindow"));
            Assert.True(WindowService.IsExcludedOverlayClass("crossdesk_privacy_overlay"));
            Assert.False(WindowService.IsExcludedOverlayClass("Notepad"));
        }
        finally
        {
            WindowService.ResetExcludedProcessesToDefaults();
        }
    }
}
