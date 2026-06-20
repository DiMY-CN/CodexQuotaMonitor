namespace CodexQuotaMonitor.Wpf;

public sealed class CliOptions
{
    public bool Check { get; private set; }
    public bool Once { get; private set; }
    public string? CodexHome { get; private set; }
    public string? CodexExe { get; private set; }
    public int? QuotaInterval { get; private set; }
    public bool? NoTray { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--check":
                    options.Check = true;
                    break;
                case "--once":
                    options.Once = true;
                    break;
                case "--no-tray":
                    options.NoTray = true;
                    break;
                case "--tray":
                    options.NoTray = false;
                    break;
                case "--codex-home":
                    options.CodexHome = TakeValue(args, ref index, arg);
                    break;
                case "--codex-exe":
                    options.CodexExe = TakeValue(args, ref index, arg);
                    break;
                case "--quota-interval":
                    options.QuotaInterval = int.Parse(TakeValue(args, ref index, arg));
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }
        return options;
    }

    private static string TakeValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {name}");
        }
        index++;
        return args[index];
    }
}
