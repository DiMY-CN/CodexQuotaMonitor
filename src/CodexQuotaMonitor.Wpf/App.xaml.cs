using System.Text;
using System.Text.Json;
using System.Windows;

namespace CodexQuotaMonitor.Wpf;

public partial class App : System.Windows.Application
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private SingleInstanceGuard? _instanceGuard;
    private SimpleLogger? _logger;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths paths;
        CliOptions options;
        AppSettings settings;
        try
        {
            options = CliOptions.Parse(e.Args);
            paths = AppPaths.Discover();
            _logger = new SimpleLogger(paths.LogPath);
            settings = SettingsStore.ApplyCliOverrides(SettingsStore.Load(paths.SettingsPath, _logger), options);
        }
        catch (Exception ex)
        {
            AttachConsoleIfPossible();
            Console.Error.WriteLine($"startup failed: {ex.Message}");
            Shutdown(2);
            return;
        }

        try
        {
            _logger.Info($"startup root={paths.RootDirectory} check={options.Check} once={options.Once}");

            if (options.Check)
            {
                AttachConsoleIfPossible();
                PrintCheck(paths, options, settings);
                Shutdown(0);
                return;
            }

            if (options.Once)
            {
                AttachConsoleIfPossible();
                var exitCode = PrintOnce(paths, options);
                Shutdown(exitCode);
                return;
            }

            SettingsStore.EnsureDefault(paths.SettingsPath, _logger);
            _instanceGuard = new SingleInstanceGuard();
            if (!_instanceGuard.Acquire(Constants.MutexName))
            {
                _logger.Info("second instance detected; activating existing window");
                if (!ActivateExistingWindow(settings))
                {
                    _logger.Warning("second instance detected, but no existing overlay window was found");
                    System.Windows.MessageBox.Show(
                        "Codex 用量监控原生版已经在运行，但没有找到可激活的浮窗。\n\n请在任务管理器中结束 CodexQuotaMonitor.Wpf.exe 后重新启动。",
                        Constants.AppName,
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
                Shutdown(0);
                return;
            }

            var window = new MainWindow(paths, options, settings, _logger);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _logger.Error("startup failed", ex);
            AttachConsoleIfPossible();
            Console.Error.WriteLine($"startup failed: {ex.Message}");
            Shutdown(2);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _instanceGuard?.Dispose();
        _logger?.Info($"exit code={e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private static void AttachConsoleIfPossible()
    {
        try
        {
            NativeMethods.AttachConsole(AttachParentProcess);
            Console.OutputEncoding = Encoding.UTF8;
            Console.Error.WriteLine();
        }
        catch
        {
            // Console attachment is best effort for a WPF executable.
        }
    }

    private static void PrintCheck(AppPaths paths, CliOptions options, AppSettings settings)
    {
        var codexHome = paths.ResolveCodexHome(options.CodexHome);
        var codexExe = CodexExeFinder.Find(options.CodexExe) ?? "";
        Console.WriteLine($"root={paths.RootDirectory}");
        Console.WriteLine($"settings={paths.SettingsPath}");
        Console.WriteLine($"log={paths.LogPath}");
        Console.WriteLine($"codex_home={codexHome}");
        Console.WriteLine($"codex_exe={(string.IsNullOrWhiteSpace(codexExe) ? "not found" : codexExe)}");
        Console.WriteLine($"quota_interval={settings.QuotaInterval}");
        Console.WriteLine($"tray={(!settings.NoTray)}");

        if (NativeMethods.TryGetTaskbarRect(out var edge, out var rect))
        {
            var placement = TaskbarPlacementCalculator.Compute(
                edge,
                rect,
                settings.WindowWidth,
                Constants.DefaultHeight,
                (int)SystemParameters.PrimaryScreenWidth,
                (int)SystemParameters.PrimaryScreenHeight);
            Console.WriteLine($"taskbar=edge:{edge} rect:{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
            Console.WriteLine($"placement={placement.X},{placement.Y},{placement.Width}x{placement.Height}");
        }
        else
        {
            Console.WriteLine("taskbar=unavailable");
        }
    }

    private static int PrintOnce(AppPaths paths, CliOptions options)
    {
        var logger = new SimpleLogger(paths.LogPath);
        var codexHome = paths.ResolveCodexHome(options.CodexHome);
        var quotaReader = new QuotaReader(codexHome, options.CodexExe, logger);
        var quota = Task.Run(() => quotaReader.ReadAsync()).GetAwaiter().GetResult();
        var payload = new
        {
            quota = new
            {
                quota.Error,
                quota.LimitId,
                quota.LimitName,
                quota.PlanType,
                primary = quota.Primary,
                secondary = quota.Secondary,
                quota.RateLimitReachedType
            }
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return quota.Error is null ? 0 : 2;
    }

    private static bool ActivateExistingWindow(AppSettings settings)
    {
        foreach (var hwnd in NativeMethods.FindWindowsByTitlePrefix(Constants.WindowTitlePrefix))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.ApplyOverlayStyles(hwnd);
            var placement = ExistingWindowPlacement(settings);
            NativeMethods.SetTopmostPosition(hwnd, placement.X, placement.Y, placement.Width, placement.Height);
            NativeMethods.SetTopmostNoActivate(hwnd);
            return true;
        }
        return false;
    }

    private static TaskbarPlacement ExistingWindowPlacement(AppSettings settings)
    {
        var width = settings.WindowWidth <= 0 ? Constants.DefaultWidth : settings.WindowWidth;
        var screenWidth = (int)SystemParameters.PrimaryScreenWidth;
        var screenHeight = (int)SystemParameters.PrimaryScreenHeight;
        if (NativeMethods.TryGetTaskbarRect(out var edge, out var rect))
        {
            return TaskbarPlacementCalculator.Compute(
                edge,
                rect,
                width,
                Constants.DefaultHeight,
                screenWidth,
                screenHeight);
        }

        return TaskbarPlacementCalculator.Fallback(width, Constants.DefaultHeight, screenWidth, screenHeight);
    }
}
