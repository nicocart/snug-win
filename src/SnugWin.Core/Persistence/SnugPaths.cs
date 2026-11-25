using System;
using System.IO;

namespace SnugWin.Core.Persistence;

public sealed record SnugPaths(string DataDirectory)
{
    public static SnugPaths Default()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new SnugPaths(Path.Combine(baseDir, "SnugWin"));
    }

    public string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public string PhoneRecordFile => Path.Combine(DataDirectory, "phone-screen.json");
    public string ZoomBookFile => Path.Combine(DataDirectory, "zoom.json");
    public string LeftBehindFile => Path.Combine(DataDirectory, "left-behind.json");
    public string OwnedDisplaysFile => Path.Combine(DataDirectory, "owned-displays.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string RecordFileForScreen(string? screenId) =>
        string.IsNullOrWhiteSpace(screenId) || string.Equals(screenId, Config.ScreenConfig.LegacyPhoneId, StringComparison.OrdinalIgnoreCase)
            ? PhoneRecordFile
            : Path.Combine(DataDirectory, $"screen-{screenId}.json");

    public string BroughtFileForScreen(string screenId) =>
        Path.Combine(DataDirectory, $"computer-brought-{screenId}.json");
}
