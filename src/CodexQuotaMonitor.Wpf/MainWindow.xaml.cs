using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexQuotaMonitor.Wpf;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly SimpleLogger _logger;
    private readonly QuotaReader _quotaReader;
    private readonly DispatcherTimer _tickTimer = new();
    private readonly DispatcherTimer _topmostTimer = new();
    private readonly DispatcherTimer _placementTimer = new();
    private readonly MetricGaugeBlock _quota5h;
    private readonly MetricGaugeBlock _quotaWeek;
    private readonly RefreshStatusBlock _refreshStatus;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly List<Forms.ToolStripMenuItem> _quotaIntervalItems = new();
    private Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIcon;
    private AppSettings _settings;
    private IntPtr _hwnd;
    private QuotaSnapshot? _lastQuota;
    private string? _quotaLastError;
    private DateTimeOffset? _quotaLastSuccessAt;
    private DateTimeOffset _nextQuotaAt;
    private bool _quotaInFlight;
    private bool _quotaPendingRefresh;
    private bool _isExiting;

    public MainWindow(AppPaths paths, CliOptions options, AppSettings settings, SimpleLogger logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
        _quotaReader = new QuotaReader(paths.ResolveCodexHome(options.CodexHome), options.CodexExe, logger);

        InitializeComponent();
        Width = _settings.WindowWidth;
        Height = Constants.DefaultHeight;

        RootGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        RootGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        RootGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        _quota5h = AddGaugeBlock("5H", "#111A26", 0);
        _quotaWeek = AddGaugeBlock("WK", "#141F2D", 1);
        _refreshStatus = AddRefreshBlock("#111A26", 2);

        BuildMenu();
        SetupTray();
        ConfigureTimers();
        RefreshNow();
    }

    private MetricGaugeBlock AddGaugeBlock(string title, string background, int column)
    {
        var block = new MetricGaugeBlock(title, background, _settings)
        {
            Margin = new Thickness(column == 0 ? 4 : 1.5, 4, 1.5, 4)
        };
        System.Windows.Controls.Grid.SetColumn(block, column);
        RootGrid.Children.Add(block);
        return block;
    }

    private RefreshStatusBlock AddRefreshBlock(string background, int column)
    {
        var block = new RefreshStatusBlock(background)
        {
            Margin = new Thickness(1.5, 4, 4, 4)
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
        StartQuotaRefresh();
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now;
        if (now >= _nextQuotaAt)
        {
            _nextQuotaAt = now.AddSeconds(_settings.QuotaInterval);
            StartQuotaRefresh();
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
            RenderRefreshStatus();
            return;
        }

        _quotaInFlight = true;
        UpdateTitle();
        RenderRefreshStatus();
        _ = Task.Run(async () => await _quotaReader.ReadAsync())
            .ContinueWith(task => Dispatcher.Invoke(() => HandleQuotaResult(task)));
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

    private void Render()
    {
        RenderQuota();
        RenderRefreshStatus();
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

    private void RenderRefreshStatus()
    {
        var now = DateTimeOffset.Now;
        if (_quotaInFlight)
        {
            _refreshStatus.SetStatus(_quotaLastSuccessAt, now, "SYNC", "#F2BD4D");
            return;
        }
        if (_lastQuota is null)
        {
            _refreshStatus.SetStatus(null, now, "WAIT", "#91A0B5");
            return;
        }
        if (_quotaLastError is not null && _quotaLastSuccessAt.HasValue)
        {
            _refreshStatus.SetStatus(_quotaLastSuccessAt, now, "OLD", "#F2BD4D");
            return;
        }
        if (_lastQuota.Error is not null && !_quotaLastSuccessAt.HasValue)
        {
            _refreshStatus.SetStatus(null, now, "ERR", "#FF6678");
            return;
        }
        if (IsStale(_quotaLastSuccessAt, _settings.QuotaInterval))
        {
            _refreshStatus.SetStatus(_quotaLastSuccessAt, now, "STALE", "#F2BD4D");
            return;
        }

        _refreshStatus.SetStatus(_quotaLastSuccessAt, now, "", "#28D989");
    }

    private void UpdateTitle()
    {
        var stamp = _quotaLastSuccessAt.HasValue ? _quotaLastSuccessAt.Value.ToString("HH:mm") : "--:--";
        var parts = new List<string>();
        if (_quotaInFlight) parts.Add("quota reading");
        if (_quotaPendingRefresh) parts.Add("quota pending");
        if (IsStale(_quotaLastSuccessAt, _settings.QuotaInterval)) parts.Add("quota stale");
        if (_quotaLastError is not null) parts.Add("quota last error");
        if (parts.Count == 0) parts.Add("quota ok");

        Title = $"{Constants.WindowTitlePrefix} | updated {stamp} | quota {_settings.QuotaInterval}s | {string.Join(" | ", parts)}";
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

    private void UpdateMenuChecks()
    {
        foreach (var item in _quotaIntervalItems)
        {
            item.Checked = item.Tag is int seconds && seconds == _settings.QuotaInterval;
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
