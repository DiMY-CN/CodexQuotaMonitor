using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexQuotaMonitor.Wpf;

public sealed class ContextReader
{
    public const string SourceLabel = "latest global";
    private static readonly Regex KeyPattern = new(@"([A-Za-z0-9_.-]+)=("".*?""|\S+)", RegexOptions.Compiled);

    private readonly string _codexHome;
    private readonly SimpleLogger _logger;

    static ContextReader()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public ContextReader(string codexHome, SimpleLogger logger)
    {
        _codexHome = codexHome;
        _logger = logger;
    }

    public ContextSnapshot Read()
    {
        try
        {
            var modelWindows = LoadModelWindows(Path.Combine(_codexHome, "models_cache.json"));
            var rows = RecentCompletedRows(Path.Combine(_codexHome, "logs_2.sqlite"), Constants.ContextRowLookback);
            if (rows.Count == 0)
            {
                return new ContextSnapshot(Error: "no response.completed usage row found", UpdatedAt: DateTimeOffset.Now);
            }

            var skipped = 0;
            foreach (var row in rows)
            {
                var snapshot = TryParseContextRow(row, modelWindows, skipped);
                if (snapshot is not null)
                {
                    return snapshot;
                }
                skipped++;
            }

            return new ContextSnapshot(
                Error: $"no valid response.completed usage row found in latest {rows.Count} rows",
                SkippedRows: skipped,
                UpdatedAt: DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            _logger.Warning($"context read failed: {ex.Message}");
            return new ContextSnapshot(Error: Formatting.CompactError(ex), UpdatedAt: DateTimeOffset.Now);
        }
    }

    public static ContextSnapshot? TryParseContextRow(
        string row,
        IReadOnlyDictionary<string, ModelWindow> modelWindows,
        int skippedRows = 0)
    {
        var metadata = ParseKeyValues(row);
        var model = metadata.GetValueOrDefault("model") ?? metadata.GetValueOrDefault("slug");
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var inputTokens = ToInt(metadata.GetValueOrDefault("input_token_count"));
        if (!inputTokens.HasValue || inputTokens < 0)
        {
            return null;
        }

        modelWindows.TryGetValue(model, out var modelWindow);
        var contextWindow = modelWindow?.ContextWindow ?? 0;
        if (contextWindow <= 0)
        {
            contextWindow = 272000;
        }

        var effectivePercent = modelWindow?.EffectiveContextWindowPercent ?? 0;
        var effectiveWindow = effectivePercent > 0
            ? (int)(contextWindow * effectivePercent / 100.0)
            : contextWindow;
        if (effectiveWindow <= 0)
        {
            return null;
        }

        var usedPercent = Math.Clamp(inputTokens.Value * 100.0 / effectiveWindow, 0.0, 100.0);
        return new ContextSnapshot(
            Model: model,
            InputTokens: inputTokens,
            CachedTokens: ToInt(metadata.GetValueOrDefault("cached_token_count")),
            OutputTokens: ToInt(metadata.GetValueOrDefault("output_token_count")),
            ReasoningTokens: ToInt(metadata.GetValueOrDefault("reasoning_token_count")),
            ContextWindow: contextWindow,
            EffectiveWindow: effectiveWindow,
            UsedPercent: usedPercent,
            RemainingPercent: Math.Max(0.0, 100.0 - usedPercent),
            EventTime: metadata.GetValueOrDefault("event.timestamp"),
            ConversationId: metadata.GetValueOrDefault("conversation.id"),
            SourceLabel: SourceLabel,
            SkippedRows: skippedRows,
            UpdatedAt: DateTimeOffset.Now);
    }

    public static Dictionary<string, string> ParseKeyValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in KeyPattern.Matches(text))
        {
            var value = match.Groups[2].Value;
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }
            values[match.Groups[1].Value] = value;
        }
        return values;
    }

    public static Dictionary<string, ModelWindow> LoadModelWindows(string path)
    {
        var result = new Dictionary<string, ModelWindow>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("slug", out var slugElement) ||
                slugElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var slug = slugElement.GetString();
            if (string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            var contextWindow = TryGetInt(model, "context_window") ?? 0;
            var effectivePercent = TryGetDouble(model, "effective_context_window_percent") ?? 0;
            result[slug] = new ModelWindow(contextWindow, effectivePercent);
        }

        return result;
    }

    private static List<string> RecentCompletedRows(string path, int limit)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(path);
        }

        var rows = new List<string>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=1500";
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT feedback_log_body
            FROM logs
            WHERE feedback_log_body LIKE '%event.kind=response.completed%'
              AND feedback_log_body LIKE '%input_token_count=%'
            ORDER BY ts DESC, id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                rows.Add(reader.GetString(0));
            }
        }
        return rows;
    }

    private static int? ToInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static int? TryGetInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
    }

    private static double? TryGetDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) ? parsed : null;
    }
}
