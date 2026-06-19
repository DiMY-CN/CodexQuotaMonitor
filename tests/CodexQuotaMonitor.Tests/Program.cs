using System.Text.Json;
using CodexQuotaMonitor.Wpf;

var tests = new (string Name, Action Body)[]
{
    ("quota JSON-RPC response parsing", TestQuotaParsing),
    ("context malformed row skip and valid row parse", TestContextParsing),
    ("settings defaults, JSON load, CLI override, corrupt fallback", TestSettings),
    ("formatting helpers", TestFormatting),
    ("taskbar placement", TestTaskbarPlacement),
    ("argument handling", TestArguments)
};

var passed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
        return 1;
    }
}

Console.WriteLine($"{passed}/{tests.Length} tests passed");
return 0;

static void TestQuotaParsing()
{
    using var document = JsonDocument.Parse("""
        {
          "rateLimits": {
            "limitId": "test-limit",
            "limitName": "Test Limit",
            "planType": "plus",
            "primary": {
              "usedPercent": 57.25,
              "windowDurationMins": 300,
              "resetsAt": 4102444800
            },
            "secondary": {
              "usedPercent": 21,
              "windowDurationMins": 10080,
              "resetsAt": 4102448400
            }
          }
        }
        """);

    var snapshot = QuotaReader.ParseRateLimitResult(document.RootElement);
    Equal(null, snapshot.Error, "quota error");
    Equal("test-limit", snapshot.LimitId, "limit id");
    Near(42.75, snapshot.Primary!.RemainingPercent!.Value, 0.001, "primary remaining");
    Near(79.0, snapshot.Secondary!.RemainingPercent!.Value, 0.001, "secondary remaining");
}

static void TestContextParsing()
{
    var windows = new Dictionary<string, ModelWindow>
    {
        ["gpt-5"] = new(200000, 50)
    };

    var malformed = ContextReader.TryParseContextRow(
        "event.kind=response.completed model=gpt-5 input_token_count=oops",
        windows);
    Equal(null, malformed, "malformed row");

    var valid = ContextReader.TryParseContextRow(
        "event.kind=response.completed event.timestamp=\"2026-06-19T12:00:00Z\" conversation.id=abc model=gpt-5 input_token_count=25000 cached_token_count=1000 output_token_count=10",
        windows,
        skippedRows: 1);
    True(valid is not null, "valid context row");
    Equal("gpt-5", valid!.Model, "model");
    Equal(25000, valid.InputTokens, "input tokens");
    Equal(100000, valid.EffectiveWindow, "effective window");
    Near(75.0, valid.RemainingPercent!.Value, 0.001, "remaining context");
    Equal("latest global", valid.SourceLabel, "source label");
    Equal(1, valid.SkippedRows, "skipped rows");
}

static void TestSettings()
{
    var tempDir = Path.Combine(Path.GetTempPath(), "codex-quota-native-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    var path = Path.Combine(tempDir, "settings.json");
    try
    {
        var defaults = SettingsStore.Load(path);
        Equal(180, defaults.QuotaInterval, "default quota interval");
        Equal(15, defaults.ContextInterval, "default context interval");

        File.WriteAllText(path, """
            {
              "quota_interval": 60,
              "context_interval": 30,
              "no_tray": true,
              "window_width": 320,
              "red_threshold": 10,
              "amber_threshold": 25
            }
            """);
        var loaded = SettingsStore.Load(path);
        Equal(60, loaded.QuotaInterval, "loaded quota interval");
        Equal(true, loaded.NoTray, "loaded no tray");

        var cli = CliOptions.Parse(["--quota-interval", "300", "--tray"]);
        var merged = SettingsStore.ApplyCliOverrides(loaded, cli);
        Equal(300, merged.QuotaInterval, "cli quota override");
        Equal(false, merged.NoTray, "cli tray override");

        File.WriteAllText(path, "{ broken json");
        var fallback = SettingsStore.Load(path);
        Equal(180, fallback.QuotaInterval, "corrupt JSON fallback");
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

static void TestFormatting()
{
    Equal("129/258", Formatting.TokenPair(129000, 258400), "token pair");
    Equal("abc", Formatting.Truncate("abc", 10), "truncate short");
    Equal("abcdefg...", Formatting.Truncate("abcdefghijk", 10), "truncate long");
}

static void TestTaskbarPlacement()
{
    var rect = new NativeMethods.RECT
    {
        Left = 0,
        Top = 1032,
        Right = 1920,
        Bottom = 1080
    };
    var placement = TaskbarPlacementCalculator.Compute(3, rect, 260, 48, 1920, 1080);
    Equal(0, placement.X, "bottom x");
    Equal(1032, placement.Y, "bottom y");
    Equal(260, placement.Width, "bottom width");
    Equal(48, placement.Height, "bottom height");
}

static void TestArguments()
{
    var options = CliOptions.Parse([
        "--check",
        "--codex-home", "C:\\Users\\example\\.codex",
        "--codex-exe", "C:\\Tools\\codex.exe",
        "--quota-interval", "600",
        "--context-interval", "20",
        "--no-tray"
    ]);
    Equal(true, options.Check, "check flag");
    Equal(false, options.Once, "once flag");
    Equal("C:\\Users\\example\\.codex", options.CodexHome, "codex home");
    Equal("C:\\Tools\\codex.exe", options.CodexExe, "codex exe");
    Equal(600, options.QuotaInterval, "quota interval");
    Equal(20, options.ContextInterval, "context interval");
    Equal(true, options.NoTray, "no tray");

    var tray = CliOptions.Parse(["--tray"]);
    Equal(false, tray.NoTray, "tray override");
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}

static void True(bool value, string label)
{
    if (!value)
    {
        throw new InvalidOperationException($"{label}: expected true");
    }
}

static void Near(double expected, double actual, double tolerance, string label)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}
