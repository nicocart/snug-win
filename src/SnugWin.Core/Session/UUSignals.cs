using System;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Remote;

namespace SnugWin.Core.Session;

public static class UURemote
{
    public static readonly RemoteIdentity Identity = new(
        LogName: "UU",
        DisplayName: "UU远程",
        ProcessNames: new[] { "GameViewer.exe", "GameViewerServer.exe", "GameViewerHealthd.exe" },
        SeesScreensOnlyAtLaunch: false,
        OpensOnMainScreen: true);
}

public enum ControllerKind
{
    Phone,
    Tablet,
    Desktop,
    Unknown
}

public static class ControllerClassifier
{
    public static ControllerKind Classify(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return ControllerKind.Unknown;
        var lower = deviceName.ToLowerInvariant();
        // Check desktop identifiers before tablet ("pad") so "ThinkPad Windows" is classified as Desktop.
        if (lower.Contains("mac") || lower.Contains("windows") || lower.Contains("pc") || lower.Contains("desktop")
            || lower.Contains("laptop") || lower.Contains("book") || lower.Contains("thinkpad")
            || lower.Contains("surface") || lower.Contains("电脑") || lower.Contains("win"))
            return ControllerKind.Desktop;
        if (lower.Contains("ipad") || lower.Contains("pad") || lower.Contains("tablet") || lower.Contains("平板"))
            return ControllerKind.Tablet;
        if (lower.Contains("iphone") || lower.Contains("phone") || lower.Contains("android") || lower.Contains("手机")
            || lower.Contains("xiaomi") || lower.Contains("huawei") || lower.Contains("oppo") || lower.Contains("vivo")
            || lower.Contains("redmi") || lower.Contains("oneplus") || lower.Contains("pixel") || lower.Contains("samsung"))
            return ControllerKind.Phone;
        return ControllerKind.Unknown;
    }

    public static bool WantsPhoneScreen(IEnumerable<string> controllers)
    {
        var list = controllers.ToList();
        if (list.Count == 0) return true; // unknown controller defaults to phone screen
        return list.Any(name => Classify(name) != ControllerKind.Desktop);
    }
}

public sealed record ControllerTable(IReadOnlyList<string> Names, bool Readable)
{
    public static readonly ControllerTable Unreadable = new(Array.Empty<string>(), false);
    public static readonly ControllerTable Unrecognized = new(Array.Empty<string>(), true);
    public static ControllerTable FromNames(params string[] names) => new(names, true);

    public override string ToString() => Readable
        ? (Names.Count == 0 ? "[]" : $"[{string.Join(", ", Names)}]")
        : "不可读";
}

public sealed record UUSignals(
    bool OverlayVisible,
    ControllerTable Controllers,
    bool? CaptureActive)
{
    public bool ManualOverride { get; init; }
    public bool IsPresent => ManualOverride || OverlayVisible || Controllers.Names.Count > 0 || CaptureActive == true;

    public RemoteObservation ToObservation(uint? streamedDisplay = null) =>
        new(
            Present: IsPresent,
            Controllers: Controllers.Names,
            StreamedDisplay: streamedDisplay,
            Evidence: ToString(),
            UnreachableDisplays: null,
            ManualOverride: ManualOverride);

    public override string ToString()
    {
        var cap = CaptureActive switch { true => "是", false => "否", null => "未知" };
        return $"overlay={(OverlayVisible ? "是" : "否")} 控制端={Controllers} capture={cap}{(ManualOverride ? " 手动开启" : "")}";
    }
}

public abstract record ConnectionEvent
{
    public sealed record Connected(IReadOnlyList<string> Controllers) : ConnectionEvent;
    public sealed record ControllersChanged(IReadOnlyList<string> Controllers) : ConnectionEvent;
    public sealed record Disconnected : ConnectionEvent;
}
