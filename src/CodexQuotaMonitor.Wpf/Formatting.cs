namespace CodexQuotaMonitor.Wpf;

public static class Formatting
{
    public static string Countdown(long? epochSeconds)
    {
        if (!epochSeconds.HasValue)
        {
            return "reset --";
        }

        var seconds = epochSeconds.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (seconds <= 0)
        {
            return "reset now";
        }

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }
        return $"{Math.Max(0, span.Minutes)}m";
    }

    public static string CompactError(Exception ex)
    {
        var text = ex.Message.Trim();
        return text.Length <= 180 ? text : text[..177] + "...";
    }

    public static string Truncate(string text, int maxChars)
    {
        var clean = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length <= maxChars)
        {
            return clean;
        }
        return clean[..Math.Max(0, maxChars - 3)].TrimEnd() + "...";
    }

    public static string RemainingText(double? remaining)
    {
        return remaining.HasValue ? $"{remaining.Value:0}" : "--";
    }

    public static System.Windows.Media.Color ColorForRemaining(double? remaining, AppSettings settings)
    {
        if (!remaining.HasValue)
        {
            return ColorFromHex("#91A0B5");
        }
        if (remaining.Value < settings.RedThreshold)
        {
            return ColorFromHex("#FF6678");
        }
        if (remaining.Value < settings.AmberThreshold)
        {
            return ColorFromHex("#F2BD4D");
        }
        return ColorFromHex("#28D989");
    }

    public static System.Windows.Media.Color ColorFromHex(string hex)
    {
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }
}
