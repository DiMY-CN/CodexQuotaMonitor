namespace CodexQuotaMonitor.Wpf;

public static class TaskbarPlacementCalculator
{
    private const uint EdgeLeft = 0;
    private const uint EdgeTop = 1;
    private const uint EdgeRight = 2;
    private const uint EdgeBottom = 3;

    public static TaskbarPlacement Compute(
        uint edge,
        NativeMethods.RECT taskbar,
        int preferredWidth,
        int fallbackHeight,
        int screenWidth,
        int screenHeight)
    {
        if (edge is EdgeTop or EdgeBottom && taskbar.Width > 0 && taskbar.Height > 0)
        {
            var width = Math.Min(Math.Max(1, preferredWidth), taskbar.Width);
            return Clamp(new TaskbarPlacement(taskbar.Left, taskbar.Top, width, taskbar.Height), screenWidth, screenHeight);
        }

        // A vertical taskbar has no meaningful horizontal "taskbar height". Keep the
        // compact overlay at the lower-left of the primary screen in that layout.
        if (edge is EdgeLeft or EdgeRight)
        {
            return Fallback(preferredWidth, fallbackHeight, screenWidth, screenHeight);
        }

        return Fallback(preferredWidth, fallbackHeight, screenWidth, screenHeight);
    }

    public static TaskbarPlacement Fallback(int preferredWidth, int height, int screenWidth, int screenHeight)
    {
        return Clamp(
            new TaskbarPlacement(0, Math.Max(0, screenHeight - height), preferredWidth, height),
            screenWidth,
            screenHeight);
    }

    private static TaskbarPlacement Clamp(TaskbarPlacement placement, int screenWidth, int screenHeight)
    {
        var width = Math.Clamp(placement.Width, 1, Math.Max(1, screenWidth));
        var height = Math.Clamp(placement.Height, 1, Math.Max(1, screenHeight));
        var x = Math.Clamp(placement.X, 0, Math.Max(0, screenWidth - width));
        var y = Math.Clamp(placement.Y, 0, Math.Max(0, screenHeight - height));
        return new TaskbarPlacement(x, y, width, height);
    }
}
