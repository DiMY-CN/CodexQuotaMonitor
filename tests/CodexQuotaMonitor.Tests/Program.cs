using System.Text.Json;
using CodexQuotaMonitor.Wpf;

var tests = new (string Name, Action Body)[]
{
    ("quota JSON-RPC response parsing", TestQuotaParsing),
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

static void TestSettings()
{
    var tempDir = Path.Combine(Path.GetTempPath(), "codex-quota-native-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    var path = Path.Combine(tempDir, "settings.json");
    try
    {
        var defaults = SettingsStore.Load(path);
        Equal(180, defaults.QuotaInterval, "default quota interval");

        File.WriteAllText(path, """
            {
              "quota_interval": 60,
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
    Equal("abc", Formatting.Truncate("abc", 10), "truncate short");
    Equal("abcdefg...", Formatting.Truncate("abcdefghijk", 10), "truncate long");
    Equal("--", Formatting.RemainingText(null), "remaining missing");
    Equal("43", Formatting.RemainingText(42.75), "remaining percent");
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
        "--codex-home", "C:\\CodexHome\\.codex",
        "--codex-exe", "C:\\Tools\\codex.exe",
        "--quota-interval", "600",
        "--no-tray"
    ]);
    Equal(true, options.Check, "check flag");
    Equal(false, options.Once, "once flag");
    Equal("C:\\CodexHome\\.codex", options.CodexHome, "codex home");
    Equal("C:\\Tools\\codex.exe", options.CodexExe, "codex exe");
    Equal(600, options.QuotaInterval, "quota interval");
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

static void Near(double expected, double actual, double tolerance, string label)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}
