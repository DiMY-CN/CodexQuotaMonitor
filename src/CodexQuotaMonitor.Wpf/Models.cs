namespace CodexQuotaMonitor.Wpf;

public sealed record LimitWindow(
    string Label,
    double? UsedPercent = null,
    double? RemainingPercent = null,
    int? WindowMins = null,
    long? ResetsAt = null);

public sealed record QuotaSnapshot(
    string? LimitId = null,
    string? LimitName = null,
    string? PlanType = null,
    LimitWindow? Primary = null,
    LimitWindow? Secondary = null,
    string? RateLimitReachedType = null,
    DateTimeOffset? UpdatedAt = null,
    string? Error = null);

public sealed record TaskbarPlacement(int X, int Y, int Width, int Height);
