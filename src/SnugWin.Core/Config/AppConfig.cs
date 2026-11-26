using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppThemeMode
{
    Light = 0,
    Dark = 1,
    Auto = 2
}

public sealed record AppConfig
{
    public const int MinZoomSteps = 0;
    public const int MaxZoomSteps = 4;

    public bool PhoneScreen { get; init; } = true;
    public bool Gestures { get; init; } = true;
    public bool GestureScroll { get; init; } = true;
    public bool GestureLongPressRightClick { get; init; } = true;
    public bool CopyAfterSelection { get; init; } = true;
    public bool GesturesInjectedOnly { get; init; } = true;
    public bool Screenshot { get; init; } = false;
    public bool AutoCheckUpdates { get; init; } = true;
    public int ZoomSteps { get; init; } = 2;
    public AppThemeMode Theme { get; init; } = AppThemeMode.Light;
    public PhoneChoice Phone { get; init; } = PhoneChoice.Standard;
    public IReadOnlyList<ScreenConfig>? Screens { get; init; }
    public string? MainScreen { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepScreensReady { get; init; } = false;
    public bool AutoRestartUnseenRemote { get; init; } = true;
    public bool AutoMatchControllerModel { get; init; } = true;
    public bool Welcomed { get; init; } = false;
    public string? LastVersion { get; init; }

    public IReadOnlyList<ScreenConfig> EffectiveScreens()
    {
        if (Screens is not null)
            return Screens;
        return [ScreenConfig.LegacyPhone(Phone, PhoneScreen)];
    }

    public ScreenConfig? PrimaryTouchScreen()
    {
        var list = EffectiveScreens();
        return list.FirstOrDefault(s => s.Orientation == ScreenOrientation.Portrait && s.Device.Kind == DeviceKind.Phone)
            ?? list.FirstOrDefault(s => s.Device.Kind.IsTouch())
            ?? list.FirstOrDefault();
    }

    public abstract record OtherOrientationResult
    {
        public sealed record Add(ScreenOrientation Orientation) : OtherOrientationResult;
        public sealed record Existing(ScreenConfig Screen) : OtherOrientationResult;
        public sealed record LimitReached : OtherOrientationResult;
    }

    /// <summary>
    /// Finds or proposes the opposite-orientation counterpart for a touch screen of the same device model.
    /// </summary>
    public OtherOrientationResult? OtherOrientation(ScreenConfig screen)
    {
        if (!screen.Device.Kind.IsTouch()) return null;
        var direction = screen.Orientation == ScreenOrientation.Portrait
            ? ScreenOrientation.Landscape
            : ScreenOrientation.Portrait;
        var list = EffectiveScreens();
        var matches = list.Where(s => s.Device == screen.Device && s.Orientation == direction).ToList();
        var existing = matches.FirstOrDefault(s => s.Enabled) ?? matches.FirstOrDefault();
        if (existing is not null) return new OtherOrientationResult.Existing(existing);
        return list.Count < ScreenConfig.Limit
            ? new OtherOrientationResult.Add(direction)
            : new OtherOrientationResult.LimitReached();
    }

    public AppConfig Sanitize()
    {
        var phone = Phone is { Screen.IsValid: true } ? Phone : PhoneChoice.Standard;
        var theme = Enum.IsDefined(Theme) ? Theme : AppThemeMode.Light;
        var sanitizedScreens = SanitizeScreens(phone);

        // Preserve legacy v2.5/1.x writeback for portrait phones, or a legacy-migrated portrait iPad.
        var legacyPhone = sanitizedScreens.FirstOrDefault(s =>
                s.Orientation == ScreenOrientation.Portrait && s.Device.Kind == DeviceKind.Phone)
            ?? sanitizedScreens.FirstOrDefault(s =>
                string.Equals(s.Id, ScreenConfig.LegacyPhoneId, StringComparison.OrdinalIgnoreCase)
                && s.Orientation == ScreenOrientation.Portrait
                && s.Device.Kind == DeviceKind.Tablet);

        string? mainScreen = !string.IsNullOrWhiteSpace(MainScreen)
            && sanitizedScreens.Any(s => string.Equals(s.Id, MainScreen, StringComparison.OrdinalIgnoreCase) && s.Enabled)
            ? sanitizedScreens.First(s => string.Equals(s.Id, MainScreen, StringComparison.OrdinalIgnoreCase)).Id
            : null;

        return this with
        {
            ZoomSteps = ZoomSteps.Clamp(MinZoomSteps, MaxZoomSteps),
            Theme = theme,
            Phone = legacyPhone?.Device ?? phone,
            PhoneScreen = legacyPhone?.Enabled ?? sanitizedScreens.Any(s => s.Enabled),
            Screens = sanitizedScreens,
            MainScreen = mainScreen
        };
    }

    private IReadOnlyList<ScreenConfig> SanitizeScreens(PhoneChoice phone)
    {
        if (Screens is null) return [ScreenConfig.LegacyPhone(phone, PhoneScreen)];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ScreenConfig>();
        foreach (var raw in Screens)
        {
            if (raw is not { Device.Screen.IsValid: true }) continue;
            var clean = raw.Sanitize();
            if (!seen.Add(clean.Id)) continue;
            list.Add(clean);
            if (list.Count >= ScreenConfig.Limit) break;
        }
        return list;
    }

    public AppConfig WithScreenAdded(ScreenOrientation orientation, PhoneChoice device, string? name = null)
    {
        var list = EffectiveScreens().ToList();
        if (list.Count >= ScreenConfig.Limit) return this;
        var cleanOrientation = device.Kind == DeviceKind.Computer ? ScreenOrientation.Landscape : orientation;
        var screen = new ScreenConfig
        {
            Id = Guid.NewGuid().ToString("D"),
            Orientation = cleanOrientation,
            Device = device,
            Enabled = true,
            Name = string.IsNullOrWhiteSpace(name) ? ScreenConfig.SuggestedName(device, cleanOrientation) : name.Trim()
        }.Sanitize();
        list.Add(screen);
        return (this with { Screens = list }).Sanitize();
    }

    public AppConfig WithScreenRemoved(string id)
    {
        var list = EffectiveScreens().Where(s => !string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        string? main = string.Equals(MainScreen, id, StringComparison.OrdinalIgnoreCase) ? null : MainScreen;
        return (this with { Screens = list, MainScreen = main }).Sanitize();
    }

    public AppConfig WithMainScreen(string? screenId) =>
        (this with { MainScreen = screenId }).Sanitize();

    public AppConfig WithKeepScreensReady(bool on) =>
        (this with { KeepScreensReady = on }).Sanitize();

    public AppConfig WithScreenUpdated(string id, Func<ScreenConfig, ScreenConfig> update)
    {
        var list = EffectiveScreens().Select(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase) ? update(s) : s).ToList();
        return (this with { Screens = list }).Sanitize();
    }

    public AppConfig ResolveCatalog(PhoneCatalog catalog)
    {
        var phone = catalog.Resolve(Phone);
        var screens = EffectiveScreens()
            .Select(s => s with { Device = catalog.Resolve(s.Device) })
            .ToList();
        return (this with { Phone = phone, Screens = screens }).Sanitize();
    }

    public static AppConfig FromJsonOrDefault(string json)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return (cfg ?? new AppConfig()).Sanitize();
        }
        catch
        {
            return new AppConfig();
        }
    }
}
