namespace CodexQuotaMonitor.Wpf;

public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        SettingsPath = Path.Combine(RootDirectory, "settings.json");
        LogDirectory = Path.Combine(RootDirectory, "logs");
        LogPath = Path.Combine(LogDirectory, "codex_quota_monitor.log");
    }

    public string RootDirectory { get; }
    public string SettingsPath { get; }
    public string LogDirectory { get; }
    public string LogPath { get; }

    public static AppPaths Discover()
    {
        var env = Environment.GetEnvironmentVariable("CODEX_QUOTA_MONITOR_NATIVE_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new AppPaths(env);
        }

        var baseDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDir);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Start-CodexQuotaMonitorNative.cmd")) ||
                File.Exists(Path.Combine(current.FullName, "settings.example.json")))
            {
                return new AppPaths(current.FullName);
            }
            current = current.Parent;
        }

        var localState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexQuotaMonitor");
        return new AppPaths(localState);
    }

    public string ResolveCodexHome(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath));
        }

        var env = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(env));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
    }
}
