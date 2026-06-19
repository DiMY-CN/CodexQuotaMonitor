namespace CodexQuotaMonitor.Wpf;

public static class Constants
{
    public const string AppName = "Codex Quota Monitor Native";
    public const string WindowTitlePrefix = "Codex Quota Monitor Native";
    public const string MutexName = "Local\\CodexQuotaMonitorNative";
    public const int DefaultQuotaIntervalSeconds = 180;
    public const int DefaultContextIntervalSeconds = 15;
    public const int DefaultWidth = 260;
    public const int DefaultHeight = 48;
    public const int ContextRowLookback = 20;
    public const double DefaultRedThreshold = 15.0;
    public const double DefaultAmberThreshold = 30.0;
}
