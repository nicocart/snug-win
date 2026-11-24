using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Config;

namespace SnugWin.Core.PhoneScreen;

public enum ScreenBehavior
{
    TouchScreen,
    Computer
}

public sealed record ScreenWish(
    string Id,
    string Name,
    PhoneChoice Device,
    ScreenOrientation Orientation,
    ScreenFit Fit,
    ScreenBehavior Behavior,
    bool IsMain = false,
    bool Prepared = false,
    bool StaysOnDismiss = false)
{
    public bool IsTouch => Behavior == ScreenBehavior.TouchScreen;
    public bool IsResident => IsMain || Prepared;
}

public static class ScreenPlan
{
    public static ScreenBehavior BehaviorOf(DeviceKind kind) =>
        kind.IsTouch() ? ScreenBehavior.TouchScreen : ScreenBehavior.Computer;

    public static ScreenFit FitOf(ScreenConfig screen) =>
        screen is { FixedNativePixels: true, Device.Kind: DeviceKind.Phone, Orientation: ScreenOrientation.Portrait }
            ? ScreenFit.FromNativePhone(screen.Device.Screen)
            : ScreenFit.FromDevice(screen.Device.Screen, screen.Device.Kind, screen.Orientation);

    /// <summary>
    /// Keep only the screens that suit the current controller during a remote session.
    /// Exempts resident screens (IsMain || Prepared) and non-remote standby so persistent
    /// and prepared screens are never torn down mid-session.
    /// </summary>
    public static IReadOnlyList<ScreenWish> ForController(IReadOnlyList<ScreenWish> wishes, bool wantsTouch, bool remote = true)
    {
        if (!remote || wishes.Count == 0) return wishes;
        return wishes.Where(w => w.IsResident || w.IsTouch == wantsTouch).ToList();
    }

    public static IReadOnlyList<string> Withheld(AppConfig config, bool remote, bool seesNewScreens, bool preview = false)
    {
        if (!remote || seesNewScreens || preview || config.KeepScreensReady)
            return [];

        return config.EffectiveScreens()
            .Where(s => s.Enabled)
            .Take(ScreenConfig.Limit)
            .Where(s => !string.Equals(s.Id, config.MainScreen, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Id)
            .ToList();
    }

    public static IReadOnlyList<ScreenWish> Wishes(
        AppConfig config,
        bool remote,
        bool preview = false,
        bool seesNewScreens = true)
    {
        var held = new HashSet<string>(
            Withheld(config, remote, seesNewScreens, preview),
            StringComparer.OrdinalIgnoreCase);
        bool prepared = config.KeepScreensReady && !preview;

        return config.EffectiveScreens()
            .Where(s => s.Enabled)
            .Take(ScreenConfig.Limit)
            .Where(s =>
            {
                bool isMain = string.Equals(s.Id, config.MainScreen, StringComparison.OrdinalIgnoreCase);
                return (remote || preview || config.KeepScreensReady || isMain) && !held.Contains(s.Id);
            })
            .Select(s =>
            {
                bool isMain = string.Equals(s.Id, config.MainScreen, StringComparison.OrdinalIgnoreCase);
                bool isResident = isMain || prepared;
                return new ScreenWish(
                    s.Id,
                    s.Name,
                    s.Device,
                    s.Orientation,
                    FitOf(s),
                    BehaviorOf(s.Device.Kind),
                    IsMain: isMain,
                    Prepared: prepared,
                    StaysOnDismiss: isResident && !seesNewScreens);
            })
            .ToList();
    }
}
