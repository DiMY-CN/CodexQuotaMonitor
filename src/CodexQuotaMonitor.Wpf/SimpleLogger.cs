namespace CodexQuotaMonitor.Wpf;

public sealed class SimpleLogger
{
    private readonly string _path;
    private readonly object _lock = new();

    public SimpleLogger(string path)
    {
        _path = path;
    }

    public void Info(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
    {
        var text = exception is null ? message : $"{message}: {exception}";
        Write("ERROR", text);
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                RotateIfNeeded();
                File.AppendAllText(
                    _path,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the always-on overlay.
        }
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < 512 * 1024)
        {
            return;
        }

        for (var index = 3; index >= 1; index--)
        {
            var source = index == 1 ? _path : $"{_path}.{index - 1}";
            var target = $"{_path}.{index}";
            if (File.Exists(target))
            {
                File.Delete(target);
            }
            if (File.Exists(source))
            {
                File.Move(source, target);
            }
        }
    }
}
