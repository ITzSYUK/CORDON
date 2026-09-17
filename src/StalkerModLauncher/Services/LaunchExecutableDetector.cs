using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed record LaunchExecutableSearchRoot(
    string RootPath,
    string DisplayName,
    int Order,
    bool IsBaseGameRoot = false);

public sealed record LaunchExecutableDetection(
    string FullPath,
    string RelativePath,
    string SourceName,
    string Reason,
    int Score,
    int CandidateCount)
{
    public string Summary => LocalizedText.Format(Strings.Executable_SummaryFormat, RelativePath, Reason, SourceName);
}

public static class LaunchExecutableDetector
{
    public static LaunchExecutableDetection? DetectBest(
        IEnumerable<LaunchExecutableSearchRoot> roots,
        string? requestedRelativePath,
        bool allowDedicated = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequested = Normalize(requestedRelativePath);
        var availableRoots = roots.Where(root => Directory.Exists(root.RootPath)).ToArray();
        if (string.IsNullOrWhiteSpace(normalizedRequested))
        {
            foreach (var root in availableRoots.Where(root => root.IsBaseGameRoot))
            {
                var launcherPath = AnomalyLauncherLocator.TryFind(root.RootPath);
                if (launcherPath is not null)
                {
                    return new LaunchExecutableDetection(
                        launcherPath,
                        Path.GetFileName(launcherPath),
                        root.DisplayName,
                        Strings.Executable_AnomalyLauncher,
                        Score: 0,
                        CandidateCount: 1);
                }

                if (AnomalyLauncherLocator.HasConfiguration(root.RootPath))
                {
                    return null;
                }
            }
        }

        var candidates = new List<Candidate>();
        foreach (var root in availableRoots)
        {
            foreach (var file in EnumerateExecutables(root.RootPath, cancellationToken))
            {
                var relative = Normalize(Path.GetRelativePath(root.RootPath, file));
                if (!allowDedicated && IsDedicatedExecutable(relative))
                {
                    continue;
                }

                var score = GetExecutableScore(relative, normalizedRequested, out var reason);
                candidates.Add(new Candidate(file, relative, root.DisplayName, root.Order, score, reason));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var best = candidates
            .OrderBy(candidate => GetCandidateTier(candidate.Score))
            .ThenByDescending(candidate => candidate.SourceOrder)
            .ThenBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .First();

        return new LaunchExecutableDetection(
            best.FullPath,
            best.RelativePath,
            best.SourceName,
            best.Reason,
            best.Score,
            candidates.Count);
    }

    public static bool IsDedicatedExecutable(string relativePath) =>
        Normalize(relativePath)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Contains("dedicated", StringComparer.OrdinalIgnoreCase);

    private static int GetCandidateTier(int score) => score switch
    {
        0 => 0,
        < 100 => 1,
        _ => 2
    };

    private static IEnumerable<string> EnumerateExecutables(string root, CancellationToken cancellationToken)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.exe", SafeEnumerationOptions);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private static int GetExecutableScore(string relativePath, string requestedRelativePath, out string reason)
    {
        var normalized = Normalize(relativePath);
        var fileName = Path.GetFileName(normalized);

        if (!string.IsNullOrWhiteSpace(requestedRelativePath) &&
            normalized.Equals(requestedRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            reason = Strings.Executable_SelectedPath;
            return 0;
        }

        if (normalized.Equals("AnomalyLauncher.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = Strings.Executable_StandaloneLauncher;
            return 5;
        }

        if (normalized.Equals(@"bin_x64\xrEngine.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = Strings.Executable_MainX64Engine;
            return 10;
        }

        if (IsEngineDirectoryExecutable(normalized))
        {
            reason = Strings.Executable_BinEngine;
            return 11;
        }

        if (normalized.Equals(@"bin\xrEngine.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = Strings.Executable_XrayEngine;
            return 12;
        }

        if (normalized.Equals(@"bin\xr_3da.exe", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(@"bin\XR_3DA.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = Strings.Executable_StalkerBinary;
            return 13;
        }

        if (fileName.Equals("xr_3da.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("XR_3DA.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = Strings.Executable_XrayBinary;
            return 20;
        }

        if (fileName.Contains("xrEngine", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("OGSR", StringComparison.OrdinalIgnoreCase))
        {
            reason = Strings.Executable_XrayLikeName;
            return 25;
        }

        if (fileName.StartsWith("Anomaly", StringComparison.OrdinalIgnoreCase))
        {
            reason = Strings.Executable_AnomalyLikeName;
            return 35;
        }

        if (fileName.Contains("xr", StringComparison.OrdinalIgnoreCase))
        {
            reason = Strings.Executable_XrayBinaryLikeName;
            return 45;
        }

        reason = Strings.Executable_Fallback;
        return 100;
    }

    private static string Normalize(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
    }

    public static bool IsEngineDirectoryExecutable(string relativePath)
    {
        var normalized = Normalize(relativePath);
        var directory = Path.GetDirectoryName(normalized);
        return Path.GetFileName(normalized).Equals("xrEngine.exe", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(directory) &&
               directory.StartsWith("bin_", StringComparison.OrdinalIgnoreCase) &&
               directory.IndexOf(Path.DirectorySeparatorChar) < 0;
    }

    private static EnumerationOptions SafeEnumerationOptions { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private sealed record Candidate(
        string FullPath,
        string RelativePath,
        string SourceName,
        int SourceOrder,
        int Score,
        string Reason);
}
