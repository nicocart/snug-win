using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.Core.Remotes.CrossDesk;

public abstract record CrossDeskLogEvent
{
    private CrossDeskLogEvent() { }

    /// <summary>First line written by a CrossDesk process instance ("Logger initialized").</summary>
    public sealed record Started : CrossDeskLogEvent;

    /// <summary>Display enumerated by Windows DXGI/WGC capturer at startup.</summary>
    public sealed record DisplaySlot(int Slot, string Name, bool Available, RectD Bounds, bool Selected) : CrossDeskLogEvent;

    /// <summary>Screen sharing power inhibitor acquired (inbound controller + active capture).</summary>
    public sealed record SharingBegan : CrossDeskLogEvent;

    /// <summary>Screen sharing power inhibitor released.</summary>
    public sealed record SharingEnded : CrossDeskLogEvent;

    /// <summary>Last remote controller disconnected.</summary>
    public sealed record LastControllerLeft : CrossDeskLogEvent;

    /// <summary>Capturer ignored a display added after startup.</summary>
    public sealed record UnregisteredDisplayIgnored(string Name, int Index) : CrossDeskLogEvent;
}

/// <summary>
/// Parses Windows CrossDesk spdlog lines from <c>%LOCALAPPDATA%\CrossDesk\logs\crossdesk.log</c>.
/// Format: <c>[yyyy-MM-dd HH:mm:ss.SSS] [crossdesk] [level] ([source:line] )?message</c>.
/// </summary>
public sealed record CrossDeskLogLine(string Stamp, CrossDeskLogEvent Event)
{
    public const string StartedPhrase = "Logger initialized";
    public const string SharingBeganPhrase = "Requested display and system idle-sleep inhibition for screen sharing";
    public const string SharingEndedPhrase = "Released screen sharing power inhibitors";
    public const string LastControllerLeftPhrase = "Last remote controller disconnected;";
    public const string DxgiDisplayPrefix = "DXGI: session display slot=";
    public const string UnregisteredDisplayPrefix = "Windows capturer ignoring unregistered display after topology change:";

    public static readonly IReadOnlyList<string> Phrases =
    [
        StartedPhrase,
        DxgiDisplayPrefix,
        SharingBeganPhrase,
        SharingEndedPhrase,
        LastControllerLeftPhrase,
        UnregisteredDisplayPrefix
    ];

    private const string LoggerTag = " [crossdesk] [";

    private static readonly Regex DxgiSlotRegex = new(
        @"^DXGI: session display slot=(?<slot>\d+) name='(?<name>[^']*)' available=(?<avail>true|false) bounds=\((?<x>-?\d+),(?<y>-?\d+);(?<w>\d+),(?<h>\d+)\)(?:, selected=(?<sel>true|false))?$",
        RegexOptions.Compiled);

    private static readonly Regex WgcSlotRegex = new(
        @"^index:\s*(?<slot>\d+),\s*display name:\s*(?<name>[^,]+),\s*is primary:\s*(?<pri>\d+|true|false),\s*bounds:\s*\((?<x1>-?\d+),\s*(?<y1>-?\d+)\)\s*-\s*\((?<x2>-?\d+),\s*(?<y2>-?\d+)\)$",
        RegexOptions.Compiled);

    private static readonly Regex UnregisteredRegex = new(
        @"^Windows capturer ignoring unregistered display after topology change:\s*display='(?<name>[^']*)',\s*index=(?<idx>-?\d+)$",
        RegexOptions.Compiled);

    public static CrossDeskLogLine? Parse(ReadOnlySpan<char> line)
    {
        if (line.Length < 25 || line[0] != '[')
            return null;

        int close = line.IndexOf(']');
        if (close != 24)
            return null;

        var stampSpan = line.Slice(1, 23);
        if (!TryParseStamp(stampSpan, TimeZoneInfo.Local, out _))
            return null;

        var rest = line[(close + 1)..];
        if (!rest.StartsWith(LoggerTag, StringComparison.Ordinal))
            return null;

        rest = rest[LoggerTag.Length..];
        int levelEnd = rest.IndexOf("] ", StringComparison.Ordinal);
        if (levelEnd < 0)
            return null;

        rest = rest[(levelEnd + 2)..];
        if (rest.Length > 0 && rest[0] == '[')
        {
            int sourceEnd = rest.IndexOf("] ", StringComparison.Ordinal);
            if (sourceEnd >= 0)
                rest = rest[(sourceEnd + 2)..];
        }

        var ev = ParseEvent(rest);
        return ev is null ? null : new CrossDeskLogLine(stampSpan.ToString(), ev);
    }

    public static CrossDeskLogLine? Parse(string? line) =>
        string.IsNullOrEmpty(line) ? null : Parse(line.AsSpan());

    public DateTimeOffset? GetTimestamp(TimeZoneInfo? timeZone = null) =>
        TryParseStamp(Stamp, timeZone ?? TimeZoneInfo.Local, out var dto) ? dto : null;

    public static DateTimeOffset? ParseLineTimestamp(ReadOnlySpan<char> line, TimeZoneInfo? timeZone = null)
    {
        if (line.Length < 25 || line[0] != '[')
            return null;
        int close = line.IndexOf(']');
        if (close != 24)
            return null;
        return TryParseStamp(line.Slice(1, 23), timeZone ?? TimeZoneInfo.Local, out var dto) ? dto : null;
    }

    public static IReadOnlyList<CrossDeskLogLine> LastInstance(IReadOnlyList<CrossDeskLogLine> lines)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Event is CrossDeskLogEvent.Started)
                return lines.Skip(i).ToArray();
        }
        return lines;
    }

    public static string NormalizeDisplayName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith(@"\\.\", StringComparison.Ordinal) ? trimmed[4..] : trimmed;
    }

    private static CrossDeskLogEvent? ParseEvent(ReadOnlySpan<char> message)
    {
        if (message.Equals(StartedPhrase, StringComparison.Ordinal))
            return new CrossDeskLogEvent.Started();
        if (message.Equals(SharingBeganPhrase, StringComparison.Ordinal))
            return new CrossDeskLogEvent.SharingBegan();
        if (message.Equals(SharingEndedPhrase, StringComparison.Ordinal))
            return new CrossDeskLogEvent.SharingEnded();
        if (message.StartsWith(LastControllerLeftPhrase, StringComparison.Ordinal))
            return new CrossDeskLogEvent.LastControllerLeft();

        if (message.StartsWith(DxgiDisplayPrefix, StringComparison.Ordinal))
        {
            var m = DxgiSlotRegex.Match(message.ToString());
            if (!m.Success) return null;
            int slot = int.Parse(m.Groups["slot"].Value, CultureInfo.InvariantCulture);
            string name = NormalizeDisplayName(m.Groups["name"].Value);
            bool avail = m.Groups["avail"].Value == "true";
            double x = double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture);
            double y = double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            double w = double.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);
            double h = double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            bool sel = m.Groups["sel"].Success && m.Groups["sel"].Value == "true";
            return new CrossDeskLogEvent.DisplaySlot(slot, name, avail, new RectD(x, y, w, h), sel);
        }

        if (message.StartsWith("index:", StringComparison.Ordinal))
        {
            var m = WgcSlotRegex.Match(message.ToString());
            if (!m.Success) return null;
            int slot = int.Parse(m.Groups["slot"].Value, CultureInfo.InvariantCulture);
            string name = NormalizeDisplayName(m.Groups["name"].Value);
            double x1 = double.Parse(m.Groups["x1"].Value, CultureInfo.InvariantCulture);
            double y1 = double.Parse(m.Groups["y1"].Value, CultureInfo.InvariantCulture);
            double x2 = double.Parse(m.Groups["x2"].Value, CultureInfo.InvariantCulture);
            double y2 = double.Parse(m.Groups["y2"].Value, CultureInfo.InvariantCulture);
            bool pri = m.Groups["pri"].Value is "1" or "true";
            return new CrossDeskLogEvent.DisplaySlot(slot, name, true, new RectD(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1)), pri);
        }

        if (message.StartsWith(UnregisteredDisplayPrefix, StringComparison.Ordinal))
        {
            var m = UnregisteredRegex.Match(message.ToString());
            if (!m.Success) return null;
            string name = NormalizeDisplayName(m.Groups["name"].Value);
            int idx = int.Parse(m.Groups["idx"].Value, CultureInfo.InvariantCulture);
            return new CrossDeskLogEvent.UnregisteredDisplayIgnored(name, idx);
        }

        return null;
    }

    private static bool TryParseStamp(ReadOnlySpan<char> stamp, TimeZoneInfo timeZone, out DateTimeOffset result)
    {
        result = default;
        if (stamp.Length != 23) return false;
        if (!DateTime.TryParseExact(
                stamp,
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dt))
            return false;

        if (timeZone.IsInvalidTime(dt))
            return false;

        var offset = timeZone.IsAmbiguousTime(dt)
            ? timeZone.GetAmbiguousTimeOffsets(dt).Max()
            : timeZone.GetUtcOffset(dt);
        result = new DateTimeOffset(dt, offset);
        return true;
    }
}
