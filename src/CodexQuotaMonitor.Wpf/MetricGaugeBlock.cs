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
    private readonly Brush _background;
    private readonly Pen _borderPen;
    private string _title;
    private string _detail = "wait";
    private double? _remaining;
    private AppSettings _settings;

    public MetricGaugeBlock(string title, string background, AppSettings settings)
    {
        _title = title;
        _background = new SolidColorBrush(Formatting.ColorFromHex(background));
        _borderPen = new Pen(new SolidColorBrush(Formatting.ColorFromHex("#273449")), 1);
        _settings = settings;
        SnapsToDevicePixels = true;
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
        var width = Math.Max(50, ActualWidth);
        var height = Math.Max(34, ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(_background, _borderPen, new Rect(0.5, 0.5, width - 1, height - 1));

        var accentColor = Formatting.ColorForRemaining(_remaining, _settings);
        var accent = new SolidColorBrush(accentColor);
        var soft = new SolidColorBrush(Formatting.ColorFromHex("#D8E3F0"));
        var muted = new SolidColorBrush(Formatting.ColorFromHex("#91A0B5"));
        var track = new SolidColorBrush(Formatting.ColorFromHex("#2A3548"));

        DrawText(dc, _title, 6, 3.5, 7, FontWeights.Bold, soft, dpi, TextAlignment.Left);
        DrawText(dc, _detail, 6, height - 15.5, 7, FontWeights.Normal, muted, dpi, TextAlignment.Left);

        var gaugeSize = Math.Min(34.0, Math.Max(28.0, height - 5.0));
        var gaugeX = width - gaugeSize - 4.0;
        var gaugeY = Math.Max(0.0, (height - gaugeSize) / 2.0 - 1.0);
        var center = new Point(gaugeX + gaugeSize / 2.0, gaugeY + gaugeSize / 2.0);
        var radius = gaugeSize / 2.0 - 4.0;

        dc.DrawEllipse(null, new Pen(track, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, center, radius, radius);
        if (_remaining.HasValue)
        {
            DrawArc(dc, center, radius, Math.Clamp(_remaining.Value, 0.0, 100.0), accentColor);
        }

        DrawCenteredText(
            dc,
            Formatting.RemainingText(_remaining),
            new Rect(gaugeX, gaugeY, gaugeSize, gaugeSize),
            11,
            FontWeights.Bold,
            accent,
            dpi);
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double percent, Color color)
    {
        if (percent <= 0)
        {
            return;
        }

        var angle = percent / 100.0 * 360.0;
        var start = PointOnCircle(center, radius, -90);
        var end = PointOnCircle(center, radius, -90 + angle);
        var largeArc = angle > 180;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, largeArc, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        var pen = new Pen(new SolidColorBrush(color), 3.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static void DrawText(
        DrawingContext dc,
        string text,
        double x,
        double y,
        double size,
        FontWeight weight,
        Brush brush,
        double pixelsPerDip,
        TextAlignment alignment)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            pixelsPerDip)
        {
            TextAlignment = alignment
        };
        dc.DrawText(formatted, new Point(x, y));
    }

    private static void DrawCenteredText(
        DrawingContext dc,
        string text,
        Rect bounds,
        double size,
        FontWeight weight,
        Brush brush,
        double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            pixelsPerDip)
        {
            TextAlignment = TextAlignment.Center
        };

        var textGeometry = formatted.BuildGeometry(new Point(0, 0));
        var textBounds = textGeometry.Bounds;
        if (textBounds.IsEmpty)
        {
            dc.DrawText(formatted, new Point(bounds.Left, bounds.Top));
            return;
        }

        var x = bounds.Left + (bounds.Width - textBounds.Width) / 2.0 - textBounds.Left;
        var y = bounds.Top + (bounds.Height - textBounds.Height) / 2.0 - textBounds.Top;
        dc.DrawText(formatted, new Point(x, y));
    }
}
