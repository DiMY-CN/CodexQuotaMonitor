namespace CodexQuotaMonitor.Wpf;

public static class TaskbarPlacementCalculator
{
    public static TaskbarPlacement Compute(
        uint edge,
        NativeMethods.RECT taskbar,
        int preferredWidth,
        int fallbackHeight,
        int screenWidth,
        int screenHeight)
    {
        var taskbarWidth = Math.Max(1, taskbar.Width);
        var taskbarHeight = Math.Max(1, taskbar.Height);
        int x;
        int y;
        int width;
        int height;

        if (edge is 1 or 3)
        {
            width = Math.Min(preferredWidth, Math.Max(210, (int)(taskbarWidth * 0.18)));
            height = taskbarHeight;
            x = taskbar.Left;
            y = taskbar.Top;
        }
        else
        {
            width = taskbarWidth;
            height = Math.Min(preferredWidth, Math.Max(140, (int)(taskbarHeight * 0.18)));
            x = taskbar.Left;
            y = taskbar.Top;
        }

        x = Math.Clamp(x, 0, Math.Max(0, screenWidth - width));
        y = Math.Clamp(y, 0, Math.Max(0, screenHeight - height));
        return new TaskbarPlacement(x, y, width, height);
    }

    public static TaskbarPlacement Fallback(int preferredWidth, int height, int screenWidth, int screenHeight)
    {
        return new TaskbarPlacement(0, Math.Max(0, screenHeight - height), preferredWidth, height);
    }
}
