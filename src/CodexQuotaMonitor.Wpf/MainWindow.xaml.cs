using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexQuotaMonitor.Wpf;

public partial class MainWindow : Window
{
    private static readonly int[] MouseVirtualKeys = [0x01, 0x02, 0x04];

    private readonly AppPaths _paths;
    private readonly SimpleLogger _logger;
    private readonly QuotaReader _quotaReader;
    private readonly DispatcherTimer _tickTimer = new();
    private readonly DispatcherTimer _topmostTimer = new();
    private readonly DispatcherTimer _placementTimer = new();
    private readonly DispatcherTimer _menuDismissTimer = new();
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
    private bool _menuVisible;
    private bool _mouseButtonWasDown;
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

        RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.08, GridUnitType.Star) });
        _quota5h = AddGaugeBlock("5H", 0);
        _quotaWeek = AddGaugeBlock("WK", 1);
        _refreshStatus = AddRefreshBlock(2);
        AddSeparator(0);
        AddSeparator(1);

        BuildMenu();
        SetupTray();
        ConfigureTimers();
        RefreshNow();
    }

    private MetricGaugeBlock AddGaugeBlock(string title, int column)
    {
        var block = new MetricGaugeBlock(title, _settings)
        {
            Margin = new Thickness(column == 0 ? 0 : 3, 0, 2, 0)
        };
        Grid.SetColumn(block, column);
        RootGrid.Children.Add(block);
        return block;
    }

    private RefreshStatusBlock AddRefreshBlock(int column)
    {
        var block = new RefreshStatusBlock { Margin = new Thickness(3, 0, 0, 0) };
        Grid.SetColumn(block, column);
        RootGrid.Children.Add(block);
        return block;
    }

    private void AddSeparator(int column)
    {
        var separator = new Border
        {
            Width = 1,
            Margin = new Thickness(0, 5, 0, 5),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Formatting.ColorFromHex("#38FFFFFF")),
            IsHitTestVisible = false
        };
        Grid.SetColumn(separator, column);
        RootGrid.Children.Add(separator);
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

        _menuDismissTimer.Interval = TimeSpan.FromMilliseconds(15);
        _menuDismissTimer.Tick += (_, _) => DismissMenuAfterOutsideClick();
    }

    private void BuildMenu()
    {
        _menu.Opening += (_, _) =>
        {
            _menuVisible = true;
        };
        _menu.Opened += (_, _) =>
        {
            ResetMouseClickState();
            _menuDismissTimer.Start();
        };
        _menu.Closed += (_, _) =>
        {
            _menuDismissTimer.Stop();
            _menuVisible = false;
            ForceTopmost();
        };
        _menu.AutoClose = true;
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
        if (!NativeMethods.EnableFrostedBackdrop(_hwnd))
        {
            _logger.Warning("native frosted backdrop is unavailable; using translucent WPF fallback");
        }
        SnapToTaskbar();
        ForceTopmost();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _tickTimer.Stop();
        _topmostTimer.Stop();
        _placementTimer.Stop();
        _menuDismissTimer.Stop();
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

    private void ResetMouseClickState()
    {
        ReadMouseState(out _, out _mouseButtonWasDown);
    }

    private void DismissMenuAfterOutsideClick()
    {
        if (!_menu.Visible)
        {
            return;
        }

        ReadMouseState(out var clickedSinceLastTick, out var anyButtonDown);
        var newPress = clickedSinceLastTick || (anyButtonDown && !_mouseButtonWasDown);
        _mouseButtonWasDown = anyButtonDown;
        if (!newPress)
        {
            return;
        }

        var cursor = Forms.Control.MousePosition;
        if (!IsPointInsideOpenMenu(_menu, cursor))
        {
            _menu.Close(Forms.ToolStripDropDownCloseReason.AppClicked);
        }
    }

    private static void ReadMouseState(out bool clickedSinceLastTick, out bool anyButtonDown)
    {
        clickedSinceLastTick = false;
        anyButtonDown = false;
        foreach (var virtualKey in MouseVirtualKeys)
        {
            var state = NativeMethods.GetAsyncKeyState(virtualKey);
            clickedSinceLastTick |= (state & 0x0001) != 0;
            anyButtonDown |= (state & 0x8000) != 0;
        }
    }

    private static bool IsPointInsideOpenMenu(Forms.ToolStripDropDown menu, System.Drawing.Point point)
    {
        if (menu.Visible && menu.Bounds.Contains(point))
        {
            return true;
        }

        foreach (Forms.ToolStripItem item in menu.Items)
        {
            if (item is Forms.ToolStripDropDownItem dropDownItem &&
                dropDownItem.HasDropDownItems &&
                dropDownItem.DropDown.Visible &&
                IsPointInsideOpenMenu(dropDownItem.DropDown, point))
            {
                return true;
            }
        }

        return false;
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
            _quota5h.SetMetric(null, "wait", _settings);
            _quotaWeek.SetMetric(null, "wait", _settings);
            return;
        }
        if (_lastQuota.Error is not null && _lastQuota.FiveHour is null && _lastQuota.Weekly is null)
        {
            _quota5h.SetMetric(null, "unavail", _settings);
            _quotaWeek.SetMetric(null, "refresh", _settings);
            return;
        }

        var fiveHour = _lastQuota.FiveHour;
        var weekly = _lastQuota.Weekly;
        _quota5h.SetMetric(fiveHour?.RemainingPercent, fiveHour is null ? "inactive" : Formatting.Countdown(fiveHour.ResetsAt), _settings);
        _quotaWeek.SetMetric(weekly?.RemainingPercent, weekly is null ? "unavail" : Formatting.Countdown(weekly.ResetsAt), _settings);
    }

    private void RenderRefreshStatus()
    {
        var now = DateTimeOffset.Now;
        if (_quotaInFlight)
        {
            _refreshStatus.SetStatus(_quotaLastSuccessAt, now, "SYNC", "#FFC857");
            return;
        }
        if (_lastQuota is null)
        {
            _refreshStatus.SetStatus(null, now, "WAIT", "#8794A2");
            return;
        }
        if (_quotaLastError is not null && _quotaLastSuccessAt.HasValue)
        {
            _refreshStatus.SetStatus(_quotaLastSuccessAt, now, "OLD", "#FFC857");
            return;
        }
        if (_lastQuota.Error is not null && !_quotaLastSuccessAt.HasValue)
        {
            _refreshStatus.SetStatus(null, now, "ERR", "#FF6B81");
            return;
        }
        if (IsStale(_quotaLastSuccessAt, _settings.QuotaInterval))
        {
            _refreshStatus.SetStatus(_quotaLastSuccessAt, now, "STALE", "#FFC857");
            return;
        }

        _refreshStatus.SetStatus(_quotaLastSuccessAt, now, "", "#2DD4A8");
    }

    private void UpdateTitle()
    {
        var stamp = _quotaLastSuccessAt.HasValue ? _quotaLastSuccessAt.Value.ToString("HH:mm") : "--:--";
        var parts = new List<string>();
        if (_quotaInFlight) parts.Add("quota reading");
        if (_quotaPendingRefresh) parts.Add("quota pending");
        if (IsStale(_quotaLastSuccessAt, _settings.QuotaInterval)) parts.Add("quota stale");
        if (_quotaLastError is not null) parts.Add("quota last error");
        if (_lastQuota?.Error is null && _lastQuota?.FiveHour is null) parts.Add("5h unavailable");
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
        if (_menuVisible)
        {
            return;
        }

        Topmost = true;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ApplyOverlayStyles(_hwnd);
            NativeMethods.SetTopmostNoActivate(_hwnd);
        }
    }

    private void SnapToTaskbar()
    {
        var placement = ResolveTaskbarPlacement(_settings.WindowWidth);
        if (_hwnd == IntPtr.Zero)
        {
            Left = placement.X;
            Top = placement.Y;
            return;
        }

        if (NativeMethods.GetWindowRect(_hwnd, out var current) &&
            current.Left == placement.X && current.Top == placement.Y &&
            current.Width == placement.Width && current.Height == placement.Height)
        {
            return;
        }

        NativeMethods.SetTopmostPosition(_hwnd, placement.X, placement.Y, placement.Width, placement.Height);
    }

    public static TaskbarPlacement ResolveTaskbarPlacement(int preferredWidth)
    {
        var bounds = Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(
            0,
            0,
            (int)SystemParameters.PrimaryScreenWidth,
            (int)SystemParameters.PrimaryScreenHeight);
        if (NativeMethods.TryGetTaskbarRect(out var edge, out var rect))
        {
            return TaskbarPlacementCalculator.Compute(
                edge,
                rect,
                preferredWidth,
                Constants.DefaultHeight,
                bounds.Width,
                bounds.Height);
        }

        return TaskbarPlacementCalculator.Fallback(preferredWidth, Constants.DefaultHeight, bounds.Width, bounds.Height);
    }
}
