using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Display;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class DisplayArrivalAndRetirementTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartiallyIdentifiedArrival_IsPolledUntilBothFieldsAreAvailable(bool edidFirst)
    {
        var rig = new DisplayOwnershipFixture();
        var container = Guid.NewGuid();
        int samples = 0;
        rig.Arrive = id => rig.DuringInspection = scan =>
        {
            if (++samples == 1) scan.ObservePresent(edidFirst ? id : null, edidFirst ? Guid.Empty : container);
            else scan.ObservePresent(id, container);
        };
        var svc = rig.Service();
        Assert.Equal(0u, await svc.CreateAsync(ScreenFit.Standard));
        Assert.Equal(container, Assert.Single(rig.Attachments).ContainerId);
        Assert.True(samples >= 2);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task PreexistingUnknownId_TimesOutWithoutSendingAnyDriverMutation()
    {
        var rig = new DisplayOwnershipFixture();
        var container = Guid.NewGuid();
        int samples = 0;
        rig.DuringInspection = scan => { samples++; scan.ObservePresent(null, container); };
        var ex = await Assert.ThrowsAsync<PhoneScreenException>(() => rig.Service().CreateAsync(ScreenFit.Standard));
        Assert.Equal(PhoneScreenErrorKind.CreateFailed, ex.Kind);
        Assert.True(samples >= 2);
        Assert.Empty(rig.Adds);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task KnownIdWithoutContainer_IsReservedAndDoesNotBlockOtherSlots()
    {
        var rig = new DisplayOwnershipFixture { DuringInspection = scan => scan.ObservePresent(0, Guid.Empty) };
        Assert.Equal(1u, await rig.Service().CreateAsync(ScreenFit.Standard));
        Assert.Equal([1u], rig.Adds);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task PreexistingContainerSeenWhileWaiting_CannotLaterBeClaimed()
    {
        var rig = new DisplayOwnershipFixture();
        var old = new CherryMonitor(0, Guid.NewGuid());
        rig.DuringInspection = scan => { scan.ObservePresent(null, old.ContainerId); rig.DuringInspection = null; };
        rig.Arrive = _ => rig.Present.Add(old);
        await Assert.ThrowsAsync<PhoneScreenException>(() => rig.Service().CreateAsync(ScreenFit.Standard));
        Assert.Equal([0u], rig.Adds);
        Assert.Empty(rig.Removes);
        Assert.Empty(rig.Attachments);
    }

    [Fact]
    public async Task ReusedContainerId_FromDisconnectedOrPhantomDevice_CanBeClaimedOnNewArrival()
    {
        var rig = new DisplayOwnershipFixture();
        var reused = new CherryMonitor(0, Guid.NewGuid());
        rig.Arrive = _ => rig.Present.Add(reused);
        var svc = rig.Service();
        Assert.Equal(0u, await svc.CreateAsync(ScreenFit.Standard));
        Assert.Equal(reused.ContainerId, Assert.Single(rig.Attachments).ContainerId);
    }

    [Fact]
    public async Task Reshape_ReusingSameContainerId_SucceedsViaExpectedContainer()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snug-reshape-same-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new SnugWin.Core.Persistence.SnugPaths(dir);
            var rig = new DisplayOwnershipFixture();
            var monitor = new CherryMonitor(0, Guid.NewGuid());
            rig.Arrive = _ => rig.Present.Add(monitor);
            var svc = rig.Service(paths);
            uint id = await svc.CreateAsync(ScreenFit.Standard);
            Assert.Equal(0u, id);

            var newFit = new ScreenFit(2);
            Assert.True(await svc.ReshapeAsync(id, newFit));
            Assert.Equal(newFit, svc.FitOf(id));
            Assert.Equal([monitor], new DisplayOwnership(paths.OwnedDisplaysFile).All());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryAfterPipeFailure_RechecksIdentityAndRefusesPrimaryOrSoleRemoval(bool replaced)
    {
        var rig = new DisplayOwnershipFixture();
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        rig.Desktop[0] = rig.Desktop[0] with { IsPrimary = true };
        Assert.True(svc.IsOnline(id));
        Assert.Null(svc.BoundsOf(id));
        rig.PipeUp = false;
        Assert.False(await svc.RetireAsync(id));
        Assert.True(svc.IsOnline(id));
        Assert.Empty(rig.Removes);
        rig.PipeUp = true;
        // Safety guard: while still sole/primary active output, retirement must refuse deletion.
        Assert.False(await svc.RetireAsync(id));
        Assert.Empty(rig.Removes);

        if (replaced)
        {
            rig.Unplug(id);
            rig.Plug(id);
        }
        else
        {
            rig.Desktop.Insert(0, DisplayOwnershipFixture.Monitor("physical-primary", primary: true));
            rig.Desktop[1] = rig.Desktop[1] with { IsPrimary = false };
        }
        Assert.True(await svc.RetireAsync(id));
        Assert.Equal(replaced ? [] : new[] { id }, rig.Removes);
        Assert.False(svc.IsOnline(id));
    }

    [Fact]
    public async Task ReplaceFailure_WhenRollbackCannotRemove_TracksLiveDisplayInRetiring()
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = false };
        rig.Desktop.Add(DisplayOwnershipFixture.Monitor("physical-primary", primary: true));
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        var original = Assert.Single(rig.Present);

        // During ReplaceScreenAsync: new arrival never replaces cleanly (same old monitor remains detached)
        // and the pipe drops before rollback removal can execute.
        rig.Arrive = _ =>
        {
            rig.Present.Add(original);
            rig.PipeUp = false;
        };
        Assert.False(await svc.ReshapeAsync(id, new ScreenFit(2)));
        Assert.Null(svc.FitOf(id));
        Assert.True(svc.IsOnline(id));

        rig.PipeUp = true;
        Assert.True(await svc.RetireAsync(id));
        Assert.Equal([id], rig.Removes);
        Assert.False(svc.IsOnline(id));
    }

    [Fact]
    public async Task Create_Requires60HzRefreshRateEvenWhenDimensionsMatch()
    {
        var rig = new DisplayOwnershipFixture();
        rig.AttachAs = target =>
        {
            var m = DisplayOwnershipFixture.Monitor(@"\\.\DISPLAY80", target);
            var (pw, ph) = ScreenFit.Standard.PixelSize(ScreenFit.Standard.BaseWidth);
            rig.Desktop.Add(m with { Mode = m.Mode with { Width = pw, Height = ph, RefreshRate = 144, DpiScalePercent = 200 } });
            return m.DeviceName;
        };
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        Assert.Contains(@"\\.\DISPLAY80", rig.Writes);
        Assert.Equal(60.0, Assert.Single(rig.Desktop).Mode.RefreshRate);
        Assert.NotNull(svc.BoundsOf(id));
    }

    [Fact]
    public async Task Retire_WhileIdentityIsPending_NeitherRemovesNorLosesReceipt()
    {
        var rig = new DisplayOwnershipFixture();
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        var owned = Assert.Single(rig.Present);
        rig.Unplug(id);
        rig.DuringInspection = scan => scan.ObservePresent(null, owned.ContainerId);
        Assert.False(await svc.RetireAsync(id));
        Assert.True(svc.IsOnline(id));
        Assert.Empty(rig.Removes);
        rig.DuringInspection = null;
        rig.Present.Add(owned);
        Assert.True(await svc.RetireAsync(id));
        Assert.Equal([id], rig.Removes);
    }

    [Fact]
    public async Task V2Arrival_WithDifferentModeTable_IsNotClaimedOrRolledBack()
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = false };
        rig.Arrive = id =>
        {
            rig.Plug(id);
            rig.Config[0] = rig.Config[0] with { Modes = [new(1920, 1080, [60])] };
        };
        var svc = rig.Service();
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(ScreenFit.Standard));
        Assert.Null(svc.FitOf(0));
        Assert.Empty(rig.Attachments);
        Assert.Empty(rig.Writes);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task TryReactivate_ReattachesBoundCherryDisplayAndYieldsToSuperScreen()
    {
        var rig = new DisplayOwnershipFixture();
        var physical = DisplayOwnershipFixture.Monitor("physical-primary", primary: true);
        rig.Desktop.Add(physical);
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        Assert.True(svc.IsAttachedToDesktop(id));

        // Simulate UU super-screen taking over exclusively: physical detached + GameViewer adapter active.
        rig.Desktop.Clear();
        rig.Desktop.Add(DisplayOwnershipFixture.Monitor("uu-super", adapter: @"Root\GameViewerIddDriver", primary: true)
            with { DeviceString = "GameViewer Virtual Display Adapter" });
        Assert.False(svc.IsAttachedToDesktop(id));
        Assert.True(svc.IsRemoteExclusiveDisplayActive());
        Assert.False(await svc.TryReactivateAsync(id, ScreenFit.Standard.BaseWidth));
        Assert.False(svc.IsAttachedToDesktop(id));

        // Layout returns to normal with physical display back: TryReactivateAsync reattaches exact Cherry display.
        rig.Desktop.Clear();
        rig.Desktop.Add(physical);
        Assert.False(svc.IsRemoteExclusiveDisplayActive());
        Assert.True(await svc.TryReactivateAsync(id, ScreenFit.Standard.BaseWidth));
        Assert.True(svc.IsAttachedToDesktop(id));
        Assert.NotNull(svc.BoundsOf(id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LateArrivalAfterTimeoutAndRollbackGrace_IsRetiredOnRetryInsteadOfStrandingSlot0(bool legacy)
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = legacy };
        rig.Desktop.Add(DisplayOwnershipFixture.Monitor(@"\\.\DISPLAY1", primary: true));
        // Attempt #1: driver never exposes #0 within timeout + 1s rollback grace.
        rig.Arrive = _ => { };
        var svc = rig.Service();

        var ex = await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(ScreenFit.Standard));
        Assert.Equal(PhoneScreenErrorKind.NeverOnline, ex.Kind);
        Assert.Equal([0u], rig.Adds);
        Assert.Empty(rig.Removes);

        // Slot #0 arrives late after rollback grace and Windows leaves it attached as an unowned black screen.
        var late0 = rig.Plug(0);
        rig.Desktop.Add(DisplayOwnershipFixture.Monitor(@"\\.\DISPLAY19", late0));
        rig.Arrive = null;

        // Retry cleans up late #0 before allocating, then creates #0 cleanly without stranding #0 alongside #1.
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        Assert.Equal(0u, id);
        Assert.Equal([0u], rig.Removes);
        Assert.Equal([0u, 0u], rig.Adds);
        var current = Assert.Single(rig.Present);
        Assert.Equal(0u, current.DriverId);
        Assert.NotEqual(late0.ContainerId, current.ContainerId);
        Assert.True(svc.IsOnline(0));
        Assert.False(svc.IsOnline(1));
        Assert.Equal(2, rig.Desktop.Count); // physical-primary + #0 only (no stranded DISPLAY19)
    }

    [Fact]
    public async Task LateArrivalAfterTimeout_WhenPrimaryOrSole_IsNotRemovedOnRetry()
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = true };
        rig.Arrive = _ => { };
        var svc = rig.Service();

        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(ScreenFit.Standard));

        var late0 = rig.Plug(0);
        rig.Desktop.Add(DisplayOwnershipFixture.Monitor(@"\\.\DISPLAY19", late0, primary: true));
        rig.Arrive = null;

        uint id = await svc.CreateAsync(ScreenFit.Standard);
        Assert.Equal(1u, id);
        Assert.Empty(rig.Removes);
        Assert.Equal(2, rig.Present.Count);
    }

    [Fact]
    public async Task PersistedPendingArrival_IsCleanedUpOnStartupAcrossRestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snug-pending-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new SnugWin.Core.Persistence.SnugPaths(dir);
            var rig = new DisplayOwnershipFixture { IsLegacyProtocol = true };
            rig.Desktop.Add(DisplayOwnershipFixture.Monitor(@"\\.\DISPLAY1", primary: true));
            rig.Arrive = _ => { };

            var svc1 = rig.Service(paths);
            await Assert.ThrowsAsync<PhoneScreenException>(() => svc1.CreateAsync(ScreenFit.Standard));
            Assert.Empty(rig.Removes);

            // Simulate process exit & restart, with late #0 arriving after svc1 exited.
            var late0 = rig.Plug(0);
            rig.Desktop.Add(DisplayOwnershipFixture.Monitor(@"\\.\DISPLAY19", late0));
            rig.Arrive = null;

            var svc2 = rig.Service(paths);
            await svc2.CleanupOrphansAsync();

            Assert.Equal([0u], rig.Removes);
            Assert.Empty(rig.Present);
            Assert.Single(rig.Desktop); // only DISPLAY1 remains
            Assert.False(File.Exists(paths.OwnedDisplaysFile));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LateArrivalOnEarlierSlot_WhileRetryCreatesSlot2_IsSweptAfterCreate()
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = true };
        rig.Desktop.Add(DisplayOwnershipFixture.Monitor(@"\\.\DISPLAY1", primary: true));
        rig.Plug(0); // preexisting #0 (e.g. from earlier run)

        // Attempt #1 tries #1 and times out before #1 arrives.
        rig.Arrive = id => { if (id == 2) rig.Plug(2); };
        var svc = rig.Service();
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(ScreenFit.Standard));

        // On retry (#2), suppose #1 arrives right as #2 is created.
        rig.Arrive = id =>
        {
            rig.Plug(id);
            if (id == 2)
            {
                var late1 = rig.Plug(1);
                rig.Desktop.Add(DisplayOwnershipFixture.Monitor(@"\\.\DISPLAY20", late1));
            }
        };

        uint created = await svc.CreateAsync(ScreenFit.Standard);
        Assert.Equal(2u, created);
        Assert.Equal([1u], rig.Removes); // #1 was swept immediately after #2 succeeded
        Assert.DoesNotContain(rig.Present, m => m.DriverId == 1);
    }

    [Fact]
    public async Task FindArrival_LegacyPipeNegotiatedDuringStateRead_ConfirmsSlot0WithoutTimeout()
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = true };
        rig.Desktop.Add(DisplayOwnershipFixture.Monitor(@"\\.\DISPLAY1", primary: true));
        // Simulate legacy pipe dropping IsLegacyProtocol to false right after Add (server closed pipe),
        // then recovering IsLegacyProtocol = true when FindArrivalAsync's fallback loop calls DriverStateAsync().
        rig.Arrive = id =>
        {
            rig.Plug(id);
            rig.IsLegacyProtocol = false;
        };
        rig.BeforeStateRead = readCount =>
        {
            if (readCount >= 2)
                rig.IsLegacyProtocol = true;
        };

        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);

        Assert.Equal(0u, id);
        Assert.Equal([0u], rig.Adds);
        Assert.Empty(rig.Removes);
        Assert.True(svc.IsOnline(0));
    }

    [Fact]
    public async Task VirtualDisplayIpcClient_LegacyPipeRemainsLatchedAfterServerClosesPipe()
    {
        string pipeName = $"snug-test-vdd-{Guid.NewGuid():N}";
        using var server = new System.IO.Pipes.NamedPipeServerStream(
            pipeName,
            System.IO.Pipes.PipeDirection.In,
            1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);

        var acceptTask = server.WaitForConnectionAsync();
        await using var client = new VirtualDisplayIpcClient(pipeName);

        Assert.False(client.IsLegacyProtocol);
        var state = await client.ReadStateAsync();
        await acceptTask;

        Assert.Null(state);
        Assert.True(client.IsLegacyProtocol);

        // Even after v0.3.1 server disconnects/closes the pipe right after processing Add,
        // IsLegacyProtocol must stay latched true so FindArrivalAsync does not spin in v2 polling.
        server.Disconnect();
        Assert.True(client.IsLegacyProtocol);
    }
}
