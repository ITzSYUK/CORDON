using System.IO.Enumeration;
using StalkerModLauncher.Models;

using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class WorkspaceSourceScanner
{
    public static WorkspaceSourceSnapshot Capture(FileLayerPlan plan, CancellationToken cancellationToken)
    {
        var game = CaptureDirectory(plan.BaseGame.RootPath, cancellationToken, plan.UsesAnomalyAppDataAlias);
        var mods = new Dictionary<string, DirectorySnapshot>();
        foreach (var layer in plan.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(layer.RootPath))
            {
                throw new DirectoryNotFoundException(LocalizedText.Format(Strings.Error_ModFolderNotFoundFormat, layer.RootPath));
            }

            mods.Add(layer.Id, CaptureDirectory(layer.RootPath, cancellationToken, plan.UsesAnomalyAppDataAlias));
        }

        return new WorkspaceSourceSnapshot(game, mods);
    }

    public static WorkspaceSourceSnapshot Capture(string gamePath, ModProfile profile, CancellationToken cancellationToken)
    {
        var game = CaptureDirectory(gamePath, cancellationToken);
        var mods = new Dictionary<string, DirectorySnapshot>();
        foreach (var mod in profile.Mods.Where(mod => mod.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(mod.SourcePath))
            {
                throw new DirectoryNotFoundException(LocalizedText.Format(Strings.Error_ModFolderNotFoundFormat, mod.SourcePath));
            }

            mods.Add(mod.Id, CaptureDirectory(mod.SourcePath, cancellationToken));
        }

        return new WorkspaceSourceSnapshot(game, mods);
    }

    private static DirectorySnapshot CaptureDirectory(string directoryPath, CancellationToken cancellationToken, bool skipAppData = false)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        var directories = new List<string>();
        var files = new List<SourceFileSnapshot>();
        var root = new DirectoryInfo(fullRoot);

        var entries = skipAppData
            ? new FileSystemEnumerable<FileSystemInfo>(fullRoot,
                (ref FileSystemEntry entry) => entry.ToFileSystemInfo(), SafeEnumerationOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    entry.Directory.Length != fullRoot.Length || !entry.FileName.Equals("appdata", StringComparison.OrdinalIgnoreCase),
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                    entry.Directory.Length != fullRoot.Length || !entry.FileName.Equals("appdata", StringComparison.OrdinalIgnoreCase)
            }
            : root.EnumerateFileSystemInfos("*", SafeEnumerationOptions);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(fullRoot, entry.FullName);
            if (entry is DirectoryInfo)
            {
                directories.Add(relativePath);
                continue;
            }

            if (entry is FileInfo file)
            {
                files.Add(new SourceFileSnapshot(
                    file.FullName,
                    relativePath,
                    file.Length,
                    file.LastWriteTimeUtc.Ticks));
            }
        }

        return new DirectorySnapshot(
            fullRoot,
            directories,
            files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static EnumerationOptions SafeEnumerationOptions { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

}

internal sealed record WorkspaceSourceSnapshot(
    DirectorySnapshot Game,
    IReadOnlyDictionary<string, DirectorySnapshot> Mods);

internal sealed record DirectorySnapshot(
    string RootPath,
    IReadOnlyList<string> Directories,
    IReadOnlyList<SourceFileSnapshot> Files);

internal sealed record SourceFileSnapshot(
    string FullPath,
    string RelativePath,
    long Length,
    long LastWriteTimeUtcTicks);
