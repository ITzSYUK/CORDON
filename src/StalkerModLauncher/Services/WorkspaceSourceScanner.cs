using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using StalkerModLauncher.Models;

using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class WorkspaceSourceScanner
{
    public static WorkspaceSourceSnapshot Capture(FileLayerPlan plan, CancellationToken cancellationToken)
    {
        var game = CaptureDirectory(plan.BaseGame.RootPath, cancellationToken);
        var mods = new Dictionary<string, DirectorySnapshot>();
        foreach (var layer in plan.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(layer.RootPath))
            {
                throw new DirectoryNotFoundException(LocalizedText.Format(Strings.Error_ModFolderNotFoundFormat, layer.RootPath));
            }

            mods.Add(layer.Id, CaptureDirectory(layer.RootPath, cancellationToken));
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

    public static WorkspaceBuildFingerprint CreateBuildFingerprint(
        string formatVersion,
        ModProfile profile,
        WorkspaceSourceSnapshot snapshot,
        FileLayerPlan plan)
    {
        var fingerprint = new WorkspaceBuildFingerprint
        {
            FormatVersion = formatVersion,
            ExecutableRelativePath = profile.ExecutableRelativePath,
            ExecutableSourcePath = profile.ExecutableSourcePath,
            FsgameSourcePath = profile.FsgameSourcePath,
            FsgameLaunchRelativePath = plan.UsesFsgameLaunchArgument ? plan.FsgameLaunchRelativePath : string.Empty,
            ProfileMode = profile.IsStandalone ? "standalone" : "overlay",
            WorkspacePath = Path.GetDirectoryName(plan.UserData.RootPath)!,
            Layers = plan.SourceLayers
                .Select(layer => new WorkspaceBuildLayerFingerprint
                {
                    Kind = layer.Kind.ToString(),
                    Id = layer.Id,
                    DisplayName = FileLayerPlan.GetDisplayName(layer),
                    RootPath = layer.RootPath,
                    Order = layer.Order
                })
                .ToList()
        };

        fingerprint.Sources.Add(CreateSourceFingerprint(
            plan.BaseGame,
            snapshot.Game,
            excludedFiles: []));
        foreach (var layer in plan.Mods)
        {
            var mod = layer.Mod!;
            if (snapshot.Mods.TryGetValue(mod.Id, out var modSnapshot))
            {
                fingerprint.Sources.Add(CreateSourceFingerprint(
                    layer,
                    modSnapshot,
                    mod.ExcludedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)));
            }
        }

        fingerprint.Signature = ComputeSignature(fingerprint);
        return fingerprint;
    }

    private static WorkspaceBuildSourceFingerprint CreateSourceFingerprint(
        FileLayer layer,
        DirectorySnapshot snapshot,
        IEnumerable<string> excludedFiles)
    {
        return new WorkspaceBuildSourceFingerprint
        {
            LayerId = layer.Id,
            DisplayName = FileLayerPlan.GetDisplayName(layer),
            RootPath = snapshot.RootPath,
            ExcludedFiles = excludedFiles.ToList(),
            Files = snapshot.Files
                .Select(file => new WorkspaceBuildFileFingerprint
                {
                    RelativePath = file.RelativePath,
                    Length = file.Length,
                    LastWriteTimeUtcTicks = file.LastWriteTimeUtcTicks
                })
                .ToList()
        };
    }

    private static string ComputeSignature(WorkspaceBuildFingerprint fingerprint)
    {
        var builder = new StringBuilder();
        builder.AppendLine(fingerprint.FormatVersion);
        foreach (var layer in fingerprint.Layers)
        {
            builder.Append(layer.Kind).Append('|')
                .Append(layer.Order).Append('|')
                .Append(layer.Id).Append('|')
                .Append(layer.RootPath).AppendLine();
        }

        builder.AppendLine(fingerprint.ExecutableRelativePath);
        builder.AppendLine(fingerprint.ExecutableSourcePath);
        builder.AppendLine(fingerprint.FsgameSourcePath);
        builder.AppendLine(fingerprint.FsgameLaunchRelativePath);
        builder.AppendLine(fingerprint.ProfileMode);
        builder.AppendLine(fingerprint.WorkspacePath);

        foreach (var source in fingerprint.Sources)
        {
            builder.Append(source.LayerId).Append('|')
                .Append(source.RootPath).AppendLine();
            foreach (var excluded in source.ExcludedFiles)
            {
                builder.Append("excluded|").AppendLine(excluded);
            }

            foreach (var file in source.Files)
            {
                builder.Append(file.RelativePath).Append('|')
                    .Append(file.Length).Append('|')
                    .Append(file.LastWriteTimeUtcTicks).AppendLine();
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static DirectorySnapshot CaptureDirectory(string directoryPath, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(directoryPath);
        var directories = new List<string>();
        var files = new List<SourceFileSnapshot>();
        var root = new DirectoryInfo(fullRoot);

        foreach (var entry in root.EnumerateFileSystemInfos("*", SafeEnumerationOptions))
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

internal sealed class WorkspaceBuildFingerprint
{
    [JsonIgnore]
    public string Signature { get; set; } = string.Empty;
    public string FormatVersion { get; set; } = string.Empty;
    public string ExecutableRelativePath { get; set; } = string.Empty;
    public string ExecutableSourcePath { get; set; } = string.Empty;
    public string FsgameSourcePath { get; set; } = string.Empty;
    public string FsgameLaunchRelativePath { get; set; } = string.Empty;
    public string ProfileMode { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public List<WorkspaceBuildLayerFingerprint> Layers { get; set; } = [];
    public List<WorkspaceBuildSourceFingerprint> Sources { get; set; } = [];
}

internal sealed class WorkspaceBuildLayerFingerprint
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public int Order { get; set; }
}

internal sealed class WorkspaceBuildSourceFingerprint
{
    public string LayerId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public List<string> ExcludedFiles { get; set; } = [];
    public List<WorkspaceBuildFileFingerprint> Files { get; set; } = [];
}

internal sealed class WorkspaceBuildFileFingerprint
{
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
}
