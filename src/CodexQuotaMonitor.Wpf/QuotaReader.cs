using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexQuotaMonitor.Wpf;

public sealed class QuotaReader
{
    private readonly string _codexHome;
    private readonly string? _codexExe;
    private readonly SimpleLogger _logger;

    public QuotaReader(string codexHome, string? codexExe, SimpleLogger logger)
    {
        _codexHome = codexHome;
        _codexExe = CodexExeFinder.Find(codexExe);
        _logger = logger;
    }

    public string? CodexExe => _codexExe;

    public async Task<QuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_codexExe))
        {
            return new QuotaSnapshot(Error: "codex.exe not found", UpdatedAt: DateTimeOffset.Now);
        }

        try
        {
            var result = await CallAppServerAsync("account/rateLimits/read", null, cancellationToken).ConfigureAwait(false);
            return ParseRateLimitResult(result);
        }
        catch (Exception ex)
        {
            _logger.Warning($"quota read failed: {ex.Message}");
            return new QuotaSnapshot(Error: Formatting.CompactError(ex), UpdatedAt: DateTimeOffset.Now);
        }
    }

    public static QuotaSnapshot ParseRateLimitResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("rateLimits", out var rateLimits) ||
            rateLimits.ValueKind != JsonValueKind.Object)
        {
            return new QuotaSnapshot(Error: "missing rateLimits", UpdatedAt: DateTimeOffset.Now);
        }

        var primary = ParseWindow(GetProperty(rateLimits, "primary"));
        var secondary = ParseWindow(GetProperty(rateLimits, "secondary"));
        var (fiveHour, weekly) = ClassifyWindows(primary, secondary);

        return new QuotaSnapshot(
            LimitId: GetString(rateLimits, "limitId"),
            LimitName: GetString(rateLimits, "limitName"),
            PlanType: GetString(rateLimits, "planType"),
            FiveHour: fiveHour,
            Weekly: weekly,
            RateLimitReachedType: GetString(rateLimits, "rateLimitReachedType"),
            UpdatedAt: DateTimeOffset.Now);
    }

    private async Task<JsonElement> CallAppServerAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _codexExe!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");
        startInfo.Environment["CODEX_HOME"] = _codexHome;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start app-server");
        try
        {
            await SendAsync(process, 0, "initialize", new
            {
                clientInfo = new { name = "codex-quota-monitor-native", version = "0.1.0" },
                capabilities = new { experimentalApi = true, optOutNotificationMethods = Array.Empty<string>() }
            }, cancellationToken).ConfigureAwait(false);

            var init = await ReadResponseAsync(process, 0, TimeSpan.FromSeconds(18), cancellationToken).ConfigureAwait(false);
            ThrowIfRpcError(init);

            await SendAsync(process, 1, method, parameters, cancellationToken).ConfigureAwait(false);
            var response = await ReadResponseAsync(process, 1, TimeSpan.FromSeconds(18), cancellationToken).ConfigureAwait(false);
            ThrowIfRpcError(response);
            return response.RootElement.GetProperty("result").Clone();
        }
        finally
        {
            StopProcess(process);
        }
    }

    private static async Task SendAsync(Process process, int id, string method, object? parameters, CancellationToken cancellationToken)
    {
        if (process.StandardInput is null)
        {
            throw new InvalidOperationException("app-server stdin unavailable");
        }

        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };
        var json = JsonSerializer.Serialize(payload);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        Process process,
        int expectedId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var lineTask = process.StandardOutput.ReadLineAsync();
            var delayTask = Task.Delay(remaining, cancellationToken);
            var completed = await Task.WhenAny(lineTask, delayTask).ConfigureAwait(false);
            if (completed != lineTask)
            {
                break;
            }

            var line = await lineTask.ConfigureAwait(false);

            if (line is null)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"app-server exited with code {process.ExitCode}");
                }
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (document.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == expectedId)
            {
                return document;
            }
            document.Dispose();
        }

        throw new TimeoutException("app-server JSON-RPC timeout");
    }

    private static void ThrowIfRpcError(JsonDocument document)
    {
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(error.GetRawText());
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static (LimitWindow? FiveHour, LimitWindow? Weekly) ClassifyWindows(
        LimitWindow? primary,
        LimitWindow? secondary)
    {
        var windows = new[] { primary, secondary }.Where(window => window is not null).Cast<LimitWindow>().ToArray();
        var fiveHour = windows.FirstOrDefault(window => IsDuration(window.WindowMins, 300, 5));
        var weekly = windows.FirstOrDefault(window => IsDuration(window.WindowMins, 10_080, 60));

        // Older app-server builds did not always return windowDurationMins.
        // Positional fallback is used only when both windows exist and neither can be classified.
        if (fiveHour is null && weekly is null && primary is not null && secondary is not null &&
            primary.WindowMins is null && secondary.WindowMins is null)
        {
            fiveHour = primary;
            weekly = secondary;
        }

        return (
            fiveHour is null ? null : fiveHour with { Label = "5h" },
            weekly is null ? null : weekly with { Label = "Week" });
    }

    private static bool IsDuration(int? actual, int expected, int tolerance)
    {
        return actual.HasValue && Math.Abs(actual.Value - expected) <= tolerance;
    }

    private static LimitWindow? ParseWindow(JsonElement? element)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var used = GetDouble(element.Value, "usedPercent");
        double? remaining = used.HasValue ? Math.Clamp(100.0 - used.Value, 0.0, 100.0) : null;
        return new LimitWindow(
            "unclassified",
            used,
            remaining,
            GetInt(element.Value, "windowDurationMins"),
            GetLong(element.Value, "resetsAt"));
    }

    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) ? value : null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;
    }
}
