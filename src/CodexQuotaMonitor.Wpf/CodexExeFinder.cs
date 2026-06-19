namespace CodexQuotaMonitor.Wpf;

public static class CodexExeFinder
{
    public static string? Find(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var binRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            if (Directory.Exists(binRoot))
            {
                candidates.AddRange(
                    Directory.EnumerateFiles(binRoot, "codex.exe", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc));
            }
            candidates.Add(Path.Combine(
                localAppData,
                "Packages",
                "OpenAI.Codex_2p2nqsd0c76g0",
                "LocalCache",
                "Local",
                "OpenAI",
                "Codex",
                "bin",
                "codex.exe"));
        }

        candidates.AddRange(PathEnvironmentCandidates("codex.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> PathEnvironmentCandidates(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, fileName);
            }
            catch
            {
                continue;
            }
            yield return candidate;
        }
    }
}
