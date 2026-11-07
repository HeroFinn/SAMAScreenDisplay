using System;
using System.IO;
using System.Text.Json;

namespace SamaDisplay.Core;

public sealed class AppSettings
{
    public string? Port { get; set; }
    public int Brightness { get; set; } = 170;
    public int IntervalSeconds { get; set; } = 3;
    public string Theme { get; set; } = "Dark";
    public bool ShowClock { get; set; } = true;
    public bool Fahrenheit { get; set; }
    public int Rotate { get; set; } = 0;
    public string? DeviceVideo { get; set; }
    public int JpegQuality { get; set; } = 40;
    public bool AutoStartDisplay { get; set; } = true;
    public bool StartMinimized { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public static AppSettings Load(string path, AppSettings? fallback = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var result = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (result != null) return Normalize(result, fallback);
            }
        }
        catch { }
        return fallback?.Clone() ?? new AppSettings();
    }

    public void Save(string path)
    {
        try
        {
            AppPaths.EnsureDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }

    public static AppSettings Normalize(AppSettings s, AppSettings? fallback = null)
    {
        var defaults = fallback ?? new AppSettings();
        if (s.Brightness < 0) s.Brightness = 0;
        if (s.Brightness > 255) s.Brightness = 255;
        if (s.IntervalSeconds < 1) s.IntervalSeconds = 1;
        if (s.IntervalSeconds > 60) s.IntervalSeconds = 60;
        if (s.JpegQuality < 25) s.JpegQuality = 25;
        if (s.JpegQuality > 95) s.JpegQuality = 95;
        if (s.Rotate < 0 || s.Rotate > 3) s.Rotate = defaults.Rotate;
        if (string.IsNullOrWhiteSpace(s.Theme)) s.Theme = defaults.Theme;
        return s;
    }
}

