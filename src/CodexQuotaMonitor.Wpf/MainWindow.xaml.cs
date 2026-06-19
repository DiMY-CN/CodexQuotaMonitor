using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexQuotaMonitor.Wpf;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly CliOptions _options;
    private readonly SimpleLogger _logger;
    private readonly QuotaReader _quotaReader;
    private readonly ContextReader _contextReader;
    private readonly DispatcherTimer _tickTimer = new();
    private readonly DispatcherTimer _topmostTimer = new();
    private readonly DispatcherTimer _placementTimer = new();
    private readonly MetricGaugeBlock _quota5h;
    private readonly MetricGaugeBlock _quotaWeek;
    private readonly MetricGaugeBlock _context;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly List<Forms.ToolStripMenuItem> _quotaIntervalItems = new();
    private readonly List<Forms.ToolStripMenuItem> _contextIntervalItems = new();
    private Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIcon;
    private AppSettings _settings;
    private IntPtr _hwnd;
    private QuotaSnapshot? _lastQuota;
    private ContextSnapshot? _lastContext;
    private string? _quotaLastError;
    private string? _contextLastError;
    private DateTimeOffset? _quotaLastSuccessAt;
    private DateTimeOffset? _contextLastSuccessAt;
    private DateTimeOffset _nextQuotaAt;
    private DateTimeOffset _nextContextAt;
    private bool _quotaInFlight;
    private bool _contextInFlight;
    private bool _quotaPendingRefresh;
    private bool _contextPendingRefresh;
    private bool _isExiting;

    public MainWindow(AppPaths paths, CliOptions options, AppSettings settings, SimpleLogger logger)
    {
        _paths = paths;
        _options = options;
        _settings = settings;
        _logger = logger;
        _quotaReader = new QuotaReader(paths.ResolveCodexHome(options.CodexHome), options.CodexExe, logger);
        _contextReader = new ContextReader(paths.ResolveCodexHome(options.CodexHome), logger);

        InitializeComponent();
        Width = _settings.WindowWidth;
        Height = Constants.DefaultHeight;

        RootGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        RootGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        RootGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        _quota5h = AddBlock("5H", "#111A26", 0);
        _quotaWeek = AddBlock("WK", "#141F2D", 1);
        _context = AddBlock("CTX", "#111A26", 2);

        BuildMenu();
        SetupTray();
        ConfigureTimers();
        RefreshNow();
    }

    private MetricGaugeBlock AddBlock(string title, string background, int column)
    {
        var block = new MetricGaugeBlock(title, background, _settings)
        {
            Margin = new Thickness(column == 0 ? 4 : 1.5, 4, column == 2 ? 4 : 1.5, 4)
        };
        System.Windows.Controls.Grid.SetColumn(block, column);
        RootGrid.Children.Add(block);
        return block;
    }

    private void ConfigureTimers()
    {
        _tickTimer.Interval = TimeSpan.FromSeconds(1);
        _tickTimer.Tick += (_, _) => Tick();
        _tickTimer.Start();

        _topmostTimer.Interval = TimeSpan.FromMilliseconds(500);
        _topmostTimer.Tick += (_, _) => ForceTopmost();
        _topmostTimer.Start();

        _placementTimer.Interval = TimeSpan.FromSeconds(1);
        _placementTimer.Tick += (_, _) => SnapToTaskbar();
        _placementTimer.Start();
    }

    private void BuildMenu()
    {
        _menu.Items.Add("Refresh now", null, (_, _) => RefreshNow());
        _menu.Items.Add("Snap to taskbar left", null, (_, _) => SnapToTaskbar());
        _menu.Items.Add(new Forms.ToolStripMenuItem(_settings.NoTray ? "Tray icon: off" : "Tray icon: on") { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var quotaMenu = new Forms.ToolStripMenuItem("Quota interval");
        foreach (var (label, seconds) in new[] { ("1 min", 60), ("3 min", 180), ("5 min", 300), ("10 min", 600), ("15 min", 900) })
        {
            var item = new Forms.ToolStripMenuItem(label) { Tag = seconds, CheckOnClick = false };
            item.Click += (_, _) => SetQuotaInterval(seconds);
            quotaMenu.DropDownItems.Add(item);
            _quotaIntervalItems.Add(item);
        }
        _menu.Items.Add(quotaMenu);

        var contextMenu = new Forms.ToolStripMenuItem("Context interval");
        foreach (var (label, seconds) in new[] { ("5 sec", 5), ("15 sec", 15), ("30 sec", 30), ("1 min", 60), ("2 min", 120) })
        {
            var item = new Forms.ToolStripMenuItem(label) { Tag = seconds, CheckOnClick = false };
            item.Click += (_, _) => SetContextInterval(seconds);
            contextMenu.DropDownItems.Add(item);
            _contextIntervalItems.Add(item);
        }
        _menu.Items.Add(contextMenu);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => RequestExit());
        UpdateMenuChecks();
    }

    private void SetupTray()
    {
        if (_settings.NoTray)
        {
            return;
        }

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = Constants.AppName,
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                SnapToTaskbar();
                ForceTopmost();
                RefreshNow();
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(SnapToTaskbar, DispatcherPriority.ApplicationIdle);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyOverlayStyles(_hwnd);
        ForceTopmost();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _tickTimer.Stop();
        _topmostTimer.Stop();
        _placementTimer.Stop();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Icon = null;
            _notifyIcon.Dispose();
        }
        _trayIcon?.Dispose();
        _menu.Dispose();

        if (!_isExiting)
        {
            _isExiting = true;
            Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown(), DispatcherPriority.ApplicationIdle);
        }
    }

    private void RequestExit()
    {
        _isExiting = true;
        System.Windows.Application.Current.Shutdown();
    }

    private System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                if (_trayIcon is not null)
                {
                    return _trayIcon;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"failed to load tray icon from executable: {ex.Message}");
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void OnMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ForceTopmost();
        _menu.Show(Forms.Control.MousePosition);
    }

    private void RefreshNow()
    {
        _nextQuotaAt = DateTimeOffset.Now.AddSeconds(_settings.QuotaInterval);
        _nextContextAt = DateTimeOffset.Now.AddSeconds(_settings.ContextInterval);
        StartQuotaRefresh();
        StartContextRefresh();
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now;
        if (now >= _nextQuotaAt)
        {
            _nextQuotaAt = now.AddSeconds(_settings.QuotaInterval);
            StartQuotaRefresh();
        }
        if (now >= _nextContextAt)
        {
            _nextContextAt = now.AddSeconds(_settings.ContextInterval);
            StartContextRefresh();
        }
        Render();
    }

    private void StartQuotaRefresh(bool pendingIfBusy = true)
    {
        if (_quotaInFlight)
        {
            if (pendingIfBusy)
            {
                _quotaPendingRefresh = true;
            }
            UpdateTitle();
            return;
        }

        _quotaInFlight = true;
        UpdateTitle();
        _ = Task.Run(async () => await _quotaReader.ReadAsync())
            .ContinueWith(task => Dispatcher.Invoke(() => HandleQuotaResult(task)));
    }

    private void StartContextRefresh(bool pendingIfBusy = true)
    {
        if (_contextInFlight)
        {
            if (pendingIfBusy)
            {
                _contextPendingRefresh = true;
            }
            UpdateTitle();
            return;
        }

        _contextInFlight = true;
        UpdateTitle();
        _ = Task.Run(() => _contextReader.Read())
            .ContinueWith(task => Dispatcher.Invoke(() => HandleContextResult(task)));
    }

    private void HandleQuotaResult(Task<QuotaSnapshot> task)
    {
        _quotaInFlight = false;
        var value = task.IsCompletedSuccessfully
            ? task.Result
            : new QuotaSnapshot(Error: task.Exception?.GetBaseException().Message ?? "quota worker failed", UpdatedAt: DateTimeOffset.Now);
        if (value.Error is not null)
        {
            _quotaLastError = value.Error;
            if (_lastQuota is null || _lastQuota.Error is not null)
            {
                _lastQuota = value;
            }
        }
        else
        {
            _lastQuota = value;
            _quotaLastError = null;
            _quotaLastSuccessAt = value.UpdatedAt ?? DateTimeOffset.Now;
        }

        if (_quotaPendingRefresh)
        {
            _quotaPendingRefresh = false;
            _nextQuotaAt = DateTimeOffset.Now.AddSeconds(_settings.QuotaInterval);
            StartQuotaRefresh(false);
        }
        Render();
    }

    private void HandleContextResult(Task<ContextSnapshot> task)
    {
        _contextInFlight = false;
        var value = task.IsCompletedSuccessfully
            ? task.Result
            : new ContextSnapshot(Error: task.Exception?.GetBaseException().Message ?? "context worker failed", UpdatedAt: DateTimeOffset.Now);
        if (value.Error is not null)
        {
            _contextLastError = value.Error;
            if (_lastContext is null || _lastContext.Error is not null)
            {
                _lastContext = value;
            }
        }
        else
        {
            _lastContext = value;
            _contextLastError = null;
            _contextLastSuccessAt = value.UpdatedAt ?? DateTimeOffset.Now;
        }

        if (_contextPendingRefresh)
        {
            _contextPendingRefresh = false;
            _nextContextAt = DateTimeOffset.Now.AddSeconds(_settings.ContextInterval);
            StartContextRefresh(false);
        }
        Render();
    }

    private void Render()
    {
        RenderQuota();
        RenderContext();
        UpdateTitle();
    }

    private void RenderQuota()
    {
        if (_lastQuota is null)
        {
            _quota5h.SetMetric(null, "Quota wait", _settings);
            _quotaWeek.SetMetric(null, "Quota wait", _settings);
            return;
        }
        if (_lastQuota.Error is not null && _lastQuota.Primary is null)
        {
            _quota5h.SetMetric(null, "unavail", _settings);
            _quotaWeek.SetMetric(null, "refresh", _settings);
            return;
        }

        var primary = _lastQuota.Primary ?? new LimitWindow("5h");
        var secondary = _lastQuota.Secondary ?? new LimitWindow("Week");
        _quota5h.SetMetric(primary.RemainingPercent, Formatting.Countdown(primary.ResetsAt), _settings);
        _quotaWeek.SetMetric(secondary.RemainingPercent, Formatting.Countdown(secondary.ResetsAt), _settings);
    }

    private void RenderContext()
    {
        if (_lastContext is null)
        {
            _context.SetMetric(null, "Ctx wait", _settings);
            return;
        }
        if (_lastContext.Error is not null && !_lastContext.RemainingPercent.HasValue)
        {
            _context.SetMetric(null, "unavail", _settings);
            return;
        }
        var detail = Formatting.TokenPair(_lastContext.InputTokens, _lastContext.EffectiveWindow);
        _context.SetMetric(_lastContext.RemainingPercent, detail, _settings);
    }

    private void UpdateTitle()
    {
        var updates = new[] { _quotaLastSuccessAt, _contextLastSuccessAt }
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToArray();
        var stamp = updates.Length > 0 ? updates.Max().ToString("HH:mm") : "--:--";
        var parts = new List<string> { "CTX latest global" };
        if (_quotaInFlight) parts.Add("quota reading");
        if (_contextInFlight) parts.Add("ctx reading");
        if (_quotaPendingRefresh) parts.Add("quota pending");
        if (_contextPendingRefresh) parts.Add("ctx pending");
        if (IsStale(_quotaLastSuccessAt, _settings.QuotaInterval)) parts.Add("quota stale");
        if (IsStale(_contextLastSuccessAt, _settings.ContextInterval)) parts.Add("ctx stale");
        if (_quotaLastError is not null) parts.Add("quota last error");
        if (_contextLastError is not null) parts.Add("ctx last error");

        Title = $"{Constants.WindowTitlePrefix} | updated {stamp} | quota {_settings.QuotaInterval}s | context {_settings.ContextInterval}s | {string.Join(" | ", parts)}";
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = Formatting.Truncate(Title, 120);
        }
    }

    private static bool IsStale(DateTimeOffset? timestamp, int intervalSeconds)
    {
        if (!timestamp.HasValue)
        {
            return false;
        }
        var age = DateTimeOffset.Now - timestamp.Value;
        return age.TotalSeconds > Math.Max(intervalSeconds * 2.0, intervalSeconds + 5.0);
    }

    private void SetQuotaInterval(int seconds)
    {
        _settings.QuotaInterval = seconds;
        _settings.Normalize();
        SettingsStore.Save(_paths.SettingsPath, _settings, _logger);
        _nextQuotaAt = DateTimeOffset.Now.AddSeconds(_settings.QuotaInterval);
        UpdateMenuChecks();
        UpdateTitle();
    }

    private void SetContextInterval(int seconds)
    {
        _settings.ContextInterval = seconds;
        _settings.Normalize();
        SettingsStore.Save(_paths.SettingsPath, _settings, _logger);
        _nextContextAt = DateTimeOffset.Now.AddSeconds(_settings.ContextInterval);
        UpdateMenuChecks();
        UpdateTitle();
    }

    private void UpdateMenuChecks()
    {
        foreach (var item in _quotaIntervalItems)
        {
            item.Checked = item.Tag is int seconds && seconds == _settings.QuotaInterval;
        }
        foreach (var item in _contextIntervalItems)
        {
            item.Checked = item.Tag is int seconds && seconds == _settings.ContextInterval;
        }
    }

    private void ForceTopmost()
    {
        Topmost = true;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ApplyOverlayStyles(_hwnd);
            NativeMethods.SetTopmostNoActivate(_hwnd);
        }
    }

    private void SnapToTaskbar()
    {
        var screenWidth = (int)SystemParameters.PrimaryScreenWidth;
        var screenHeight = (int)SystemParameters.PrimaryScreenHeight;
        TaskbarPlacement placement;
        if (NativeMethods.TryGetTaskbarRect(out var edge, out var rect))
        {
            placement = TaskbarPlacementCalculator.Compute(edge, rect, _settings.WindowWidth, Constants.DefaultHeight, screenWidth, screenHeight);
        }
        else
        {
            placement = TaskbarPlacementCalculator.Fallback(_settings.WindowWidth, Constants.DefaultHeight, screenWidth, screenHeight);
        }

        Left = placement.X;
        Top = placement.Y;
        Width = placement.Width;
        Height = placement.Height;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetTopmostPosition(_hwnd, placement.X, placement.Y, placement.Width, placement.Height);
        }
    }
}
