using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace CodexQuotaMonitor.Wpf;

public sealed class RefreshStatusBlock : FrameworkElement
{
    private DateTimeOffset? _refreshedAt;
    private DateTimeOffset _currentTime = DateTimeOffset.Now;
    private string _status = "WAIT";
    private Color _accentColor = Formatting.ColorFromHex("#8794A2");

    public RefreshStatusBlock()
    {
        SnapsToDevicePixels = true;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
    }

    public void SetStatus(DateTimeOffset? refreshedAt, DateTimeOffset currentTime, string status, string accentHex)
    {
        _refreshedAt = refreshedAt;
        _currentTime = currentTime;
        _status = status;
        _accentColor = Formatting.ColorFromHex(accentHex);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = Math.Max(34.0, ActualWidth);
        var height = Math.Max(22.0, ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var accent = new SolidColorBrush(_accentColor);
        var current = new SolidColorBrush(Formatting.ColorFromHex("#F4F6F8"));
        var muted = new SolidColorBrush(Formatting.ColorFromHex("#8F9BA8"));
        var refresh = new SolidColorBrush(Formatting.ColorFromHex("#58B8FF"));
        var labelSize = Math.Clamp(height * 0.21, 7.0, 9.2);

        DrawText(dc, "REF", 4, 1, labelSize, FontWeights.Bold, current, dpi);
        dc.DrawEllipse(accent, null, new Point(width - 5, 5), 2.2, 2.2);
        DrawTimePair(dc, FormatTime(_refreshedAt), FormatTime(_currentTime), width, height, refresh, current, muted, dpi);

        if (!string.IsNullOrWhiteSpace(_status))
        {
            var statusSize = Math.Clamp(height * 0.18, 7.0, 8.0);
            DrawCenteredText(dc, _status, new Rect(0, height - statusSize - 2, width, statusSize + 2), statusSize, FontWeights.Bold, accent, dpi);
        }
    }

    private static string FormatTime(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToString("HH:mm", CultureInfo.CurrentCulture) : "--:--";
    }

    private static void DrawTimePair(
        DrawingContext dc,
        string refreshTime,
        string currentTime,
        double width,
        double height,
        Brush refreshBrush,
        Brush currentBrush,
        Brush separatorBrush,
        double dpi)
    {
        var size = Math.Clamp(height * 0.285, 8.8, 12.5);
        var refresh = MakeText(refreshTime, size, FontWeights.SemiBold, refreshBrush, dpi);
        var slash = MakeText("/", size - 0.6, FontWeights.Normal, separatorBrush, dpi);
        var current = MakeText(currentTime, size, FontWeights.SemiBold, currentBrush, dpi);
        var total = refresh.WidthIncludingTrailingWhitespace + slash.WidthIncludingTrailingWhitespace + current.WidthIncludingTrailingWhitespace;
        var x = Math.Max(1.0, (width - total) / 2.0);
        var y = Math.Max(8.0, height * 0.43 - size / 2.0);

        dc.DrawText(refresh, new Point(x, y));
        x += refresh.WidthIncludingTrailingWhitespace;
        dc.DrawText(slash, new Point(x, y + 0.4));
        x += slash.WidthIncludingTrailingWhitespace;
        dc.DrawText(current, new Point(x, y));
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size, FontWeight weight, Brush brush, double dpi)
    {
        dc.DrawText(MakeText(text, size, weight, brush, dpi), new Point(x, y));
    }

    private static void DrawCenteredText(DrawingContext dc, string text, Rect bounds, double size, FontWeight weight, Brush brush, double dpi)
    {
        var formatted = MakeText(text, size, weight, brush, dpi);
        var textBounds = formatted.BuildGeometry(new Point(0, 0)).Bounds;
        var x = bounds.Left + (bounds.Width - textBounds.Width) / 2.0 - textBounds.Left;
        var y = bounds.Top + (bounds.Height - textBounds.Height) / 2.0 - textBounds.Top;
        dc.DrawText(formatted, new Point(x, y));
    }

    private static FormattedText MakeText(string text, double size, FontWeight weight, Brush brush, double dpi)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            dpi);
    }
}
