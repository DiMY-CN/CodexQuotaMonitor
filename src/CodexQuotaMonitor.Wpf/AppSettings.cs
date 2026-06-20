using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaMonitor.Wpf;

public sealed class AppSettings
{
    [JsonPropertyName("quota_interval")]
    public int QuotaInterval { get; set; } = Constants.DefaultQuotaIntervalSeconds;

    [JsonPropertyName("no_tray")]
    public bool NoTray { get; set; }

    [JsonPropertyName("window_width")]
    public int WindowWidth { get; set; } = Constants.DefaultWidth;

    [JsonPropertyName("red_threshold")]
    public double RedThreshold { get; set; } = Constants.DefaultRedThreshold;

    [JsonPropertyName("amber_threshold")]
    public double AmberThreshold { get; set; } = Constants.DefaultAmberThreshold;

    public AppSettings Clone() => new()
    {
        QuotaInterval = QuotaInterval,
        NoTray = NoTray,
        WindowWidth = WindowWidth,
        RedThreshold = RedThreshold,
        AmberThreshold = AmberThreshold
    };

    public void Normalize()
    {
        QuotaInterval = Math.Max(30, QuotaInterval);
        WindowWidth = Math.Clamp(WindowWidth, 210, 520);
        RedThreshold = Math.Clamp(RedThreshold, 0.0, 100.0);
        AmberThreshold = Math.Clamp(AmberThreshold, RedThreshold, 100.0);
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public static AppSettings Load(string path, SimpleLogger? logger = null)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                           ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            logger?.Warning($"failed to load settings from {path}: {ex.Message}");
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings, SimpleLogger? logger = null)
    {
        try
        {
            settings.Normalize();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            logger?.Warning($"failed to save settings to {path}: {ex.Message}");
        }
    }

    public static AppSettings ApplyCliOverrides(AppSettings settings, CliOptions options)
    {
        var merged = settings.Clone();
        if (options.QuotaInterval.HasValue)
        {
            merged.QuotaInterval = options.QuotaInterval.Value;
        }
        if (options.NoTray.HasValue)
        {
            merged.NoTray = options.NoTray.Value;
        }
        merged.Normalize();
        return merged;
    }

    public static void EnsureDefault(string path, SimpleLogger? logger = null)
    {
        if (!File.Exists(path))
        {
            Save(path, new AppSettings(), logger);
        }
    }
}
