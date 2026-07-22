using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexQuotaMonitor.Wpf;

public sealed class MetricGaugeBlock : FrameworkElement
{
    private readonly string _title;
    private string _detail = "wait";
    private double? _remaining;
    private AppSettings _settings;

    public MetricGaugeBlock(string title, AppSettings settings)
    {
        _title = title;
        _settings = settings;
        SnapsToDevicePixels = true;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
    }

    public void SetMetric(double? remaining, string detail, AppSettings settings)
    {
        _remaining = remaining;
        _detail = Formatting.Truncate(detail, 8);
        _settings = settings;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = Math.Max(28.0, ActualWidth);
        var height = Math.Max(22.0, ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var accentColor = Formatting.ColorForRemaining(_remaining, _settings);
        var accent = new SolidColorBrush(accentColor);
        var primary = new SolidColorBrush(Formatting.ColorFromHex("#F4F6F8"));
        var muted = new SolidColorBrush(Formatting.ColorFromHex("#A2ACB7"));
        var track = new SolidColorBrush(Formatting.ColorFromHex("#6A56616C"));

        var labelSize = Math.Clamp(height * 0.21, 7.0, 9.2);
        var detailSize = Math.Clamp(height * 0.195, 6.8, 8.6);
        DrawText(dc, _title, 4, 1, labelSize, FontWeights.Bold, primary, dpi);
        DrawText(dc, _detail, 4, height - detailSize - 2, detailSize, FontWeights.Normal, muted, dpi);

        var gaugeSize = Math.Clamp(height - 3, 22.0, 38.0);
        var gaugeX = width - gaugeSize - 2.0;
        var gaugeY = (height - gaugeSize) / 2.0;
        var center = new Point(gaugeX + gaugeSize / 2.0, gaugeY + gaugeSize / 2.0);
        var stroke = Math.Clamp(gaugeSize * 0.09, 2.0, 3.2);
        var radius = Math.Max(5.0, gaugeSize / 2.0 - stroke - 0.8);
        var trackPen = new Pen(track, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        if (_remaining.HasValue)
        {
            var percent = Math.Clamp(_remaining.Value, 0.0, 100.0);
            if (percent >= 99.95)
            {
                dc.DrawEllipse(null, new Pen(accent, stroke), center, radius, radius);
            }
            else
            {
                DrawArc(dc, center, radius, percent, accentColor, stroke);
            }
        }

        DrawCenteredText(
            dc,
            Formatting.RemainingText(_remaining),
            new Rect(gaugeX, gaugeY, gaugeSize, gaugeSize),
            Math.Clamp(gaugeSize * 0.43, 10.0, 16.0),
            FontWeights.Bold,
            _remaining.HasValue ? accent : muted,
            dpi);
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double percent, Color color, double stroke)
    {
        if (percent <= 0)
        {
            return;
        }

        var angle = percent / 100.0 * 360.0;
        var start = PointOnCircle(center, radius, -90);
        var end = PointOnCircle(center, radius, -90 + angle);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), stroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
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
