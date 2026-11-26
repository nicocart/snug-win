using System;
using System.IO;
using System.Linq;
using SnugWin.Core.Config;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Remotes.CrossDesk;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class AutoAdaptationAndRestartTests
{
    [Fact]
    public void IdleAutoRestartPolicy_DebouncesEnforcesCooldownCapsRetriesAndResets()
    {
        var policy = new IdleAutoRestartPolicy(
            debounce: TimeSpan.FromSeconds(2.5),
            cooldown: TimeSpan.FromSeconds(45),
            maxAttempts: 2);
        var t0 = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

        // First observation starts debounce timer -> false
        Assert.False(policy.ShouldRestart(t0, true, true, true, false, true, false, "sig1"));
        Assert.False(policy.ShouldRestart(t0.AddSeconds(2.0), true, true, true, false, true, false, "sig1"));

        // Never restarts while remote is active or unelevated
        Assert.False(policy.ShouldRestart(t0.AddSeconds(3.0), true, true, true, anyRemoteActive: true, true, false, "sig1"));
        // Remote activity cleared debounce, so wait 2.5s idle again
        Assert.False(policy.ShouldRestart(t0.AddSeconds(4.0), true, true, true, false, true, false, "sig1"));
        Assert.True(policy.ShouldRestart(t0.AddSeconds(6.6), true, true, true, false, true, false, "sig1"));

        policy.RecordAttempt(t0.AddSeconds(6.6));
        Assert.Equal(1, policy.Attempts);

        // Cooldown blocks immediate second retry even after debounce
        Assert.False(policy.ShouldRestart(t0.AddSeconds(10), true, true, true, false, true, false, "sig1"));
        Assert.False(policy.ShouldRestart(t0.AddSeconds(40), true, true, true, false, true, false, "sig1"));

        // After 45s cooldown while continuously unseen -> 2nd attempt allowed right after 51.6s
        Assert.False(policy.ShouldRestart(t0.AddSeconds(51.0), true, true, true, false, true, false, "sig1"));
        Assert.True(policy.ShouldRestart(t0.AddSeconds(52.0), true, true, true, false, true, false, "sig1"));
        policy.RecordAttempt(t0.AddSeconds(52.0));
        Assert.Equal(2, policy.Attempts);

        // Max attempts (2) reached -> blocked
        Assert.False(policy.ShouldRestart(t0.AddSeconds(120), true, true, true, false, true, false, "sig1"));
        Assert.False(policy.ShouldRestart(t0.AddSeconds(125), true, true, true, false, true, false, "sig1"));

        // Clearing unseen state resets attempts
        Assert.False(policy.ShouldRestart(t0.AddSeconds(126), true, true, unseenCrossDesk: false, false, true, false, "sig1"));
        Assert.Equal(0, policy.Attempts);

        // Changing screen signature also resets attempts
        Assert.False(policy.ShouldRestart(t0.AddSeconds(130), true, true, true, false, true, false, "sig2"));
        Assert.True(policy.ShouldRestart(t0.AddSeconds(133), true, true, true, false, true, false, "sig2"));
    }

    [Fact]
    public void PhoneCatalog_MatchControllerName_MatchesMostSpecificPhoneOrTablet()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "device-models.json");
        var catalog = PhoneCatalog.LoadFromFile(path);

        var iphoneMax = catalog.MatchControllerName("明明的 iPhone 16 Pro Max");
        Assert.NotNull(iphoneMax);
        Assert.Equal(DeviceKind.Phone, iphoneMax!.Kind);
        Assert.Contains("16 Pro Max", iphoneMax.Name, StringComparison.OrdinalIgnoreCase);

        var xiaomi = catalog.MatchControllerName("Xiaomi 15 Ultra");
        Assert.NotNull(xiaomi);
        Assert.Equal(DeviceKind.Phone, xiaomi!.Kind);
        Assert.Contains("15 Ultra", xiaomi.Name, StringComparison.OrdinalIgnoreCase);

        var ipad = catalog.MatchControllerName("Leon's iPad Pro 11");
        Assert.NotNull(ipad);
        Assert.Equal(DeviceKind.Tablet, ipad!.Kind);

        Assert.Null(catalog.MatchControllerName("DESKTOP-9X8K2L"));
        Assert.Null(catalog.MatchControllerName("MacBook Pro"));
        Assert.Null(catalog.MatchControllerName("PC"));
    }

    [Fact]
    public void GenericPresets_AndConfigDefaults_AreValid()
    {
        Assert.All(PhoneCatalog.GenericPresets, p => Assert.True(p.Screen.IsValid));
        Assert.Contains(PhoneCatalog.GenericPresets, p => p.Kind == DeviceKind.Phone);
        Assert.Contains(PhoneCatalog.GenericPresets, p => p.Kind == DeviceKind.Tablet);
        Assert.Contains(PhoneCatalog.GenericPresets, p => p.Kind == DeviceKind.Computer);

        var cfg = new AppConfig();
        Assert.True(cfg.AutoRestartUnseenRemote);
        Assert.True(cfg.AutoMatchControllerModel);
    }
}
