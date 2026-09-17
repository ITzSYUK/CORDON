using System.Text.Json;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

internal static class WorkspaceManifestStore
{
    private const string ManifestFileName = "build-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string? TryGetCachedExecutable(
        string workspaceRoot,
        string currentWorkspace,
        ModProfile profile,
        WorkspaceBuildFingerprint buildFingerprint,
        IProgress<string> progress)
    {
        var manifestPath = Path.Combine(workspaceRoot, ManifestFileName);
        if (!Directory.Exists(currentWorkspace))
        {
            progress.Report(Strings.WorkspaceManifest_CurrentMissing);
            return null;
        }

        if (!File.Exists(manifestPath))
        {
            progress.Report(Strings.WorkspaceManifest_CacheMissing);
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<WorkspaceBuildManifest>(File.ReadAllText(manifestPath));
            if (!string.Equals(manifest?.Signature, buildFingerprint.Signature, StringComparison.Ordinal))
            {
                ReportFingerprintChanges(manifest?.Fingerprint, buildFingerprint, progress);
                return null;
            }

            var executablePath = Path.Combine(currentWorkspace, profile.ExecutableRelativePath);
            if (!File.Exists(executablePath))
            {
                progress.Report(Strings.WorkspaceManifest_ExecutableMissing);
                return null;
            }

            progress.Report(Strings.WorkspaceManifest_Current);
            return executablePath;
        }
        catch
        {
            progress.Report(Strings.WorkspaceManifest_CacheUnreadable);
            return null;
        }
    }

    public static void Write(
        string workspaceRoot,
        WorkspaceBuildFingerprint buildFingerprint,
        WorkspaceBuildStats stats)
    {
        var manifest = new WorkspaceBuildManifest
        {
            Signature = buildFingerprint.Signature,
            Fingerprint = buildFingerprint,
            BuiltAtUtc = DateTime.UtcNow,
            FileCount = stats.FileCount,
            HardLinkCount = stats.LinkedFiles,
            SymbolicLinkCount = stats.SymbolicLinkedFiles,
            LocalFileCount = stats.ProtectedCopies,
            LogicalSizeBytes = stats.LogicalSizeBytes,
            PhysicalSizeBytes = stats.PhysicalSizeBytes,
            HasStatistics = true
        };
        File.WriteAllText(
            Path.Combine(workspaceRoot, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static void ReportFingerprintChanges(
        WorkspaceBuildFingerprint? previous,
        WorkspaceBuildFingerprint current,
        IProgress<string> progress)
    {
        var changes = DescribeFingerprintChanges(previous, current);
        progress.Report(LocalizedText.Format(Strings.WorkspaceManifest_RebuildReasonFormat, changes[0]));
        foreach (var change in changes.Skip(1))
        {
            progress.Report(LocalizedText.Format(Strings.WorkspaceManifest_AdditionalReasonFormat, change));
        }
    }

    internal static IReadOnlyList<string> DescribeFingerprintChanges(
        WorkspaceBuildFingerprint? previous,
        WorkspaceBuildFingerprint current)
    {
        if (previous is null)
        {
            return [Strings.WorkspaceManifest_DiagnosticFormatChanged];
        }

        var changes = new List<string>();
        if (!string.Equals(previous.FormatVersion, current.FormatVersion, StringComparison.Ordinal))
        {
            changes.Add(LocalizedText.Format(Strings.WorkspaceManifest_FormatChangedFormat, previous.FormatVersion, current.FormatVersion));
        }

        if (!string.Equals(
                previous.ExecutableRelativePath,
                current.ExecutableRelativePath,
                StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(LocalizedText.Format(Strings.WorkspaceManifest_ExecutableChangedFormat, DisplayValue(previous.ExecutableRelativePath), DisplayValue(current.ExecutableRelativePath)));
        }

        if (!string.Equals(
                previous.ExecutableSourcePath,
                current.ExecutableSourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(LocalizedText.Format(Strings.WorkspaceManifest_SourceChangedFormat, DisplayValue(previous.ExecutableSourcePath, Strings.WorkspaceManifest_Auto), DisplayValue(current.ExecutableSourcePath, Strings.WorkspaceManifest_Auto)));
        }

        if (!string.Equals(previous.ProfileMode, current.ProfileMode, StringComparison.Ordinal))
        {
            changes.Add(LocalizedText.Format(Strings.WorkspaceManifest_ProfileModeChangedFormat, previous.ProfileMode, current.ProfileMode));
        }

        AddLayerChanges(previous.Layers, current.Layers, changes);
        AddSourceChanges(previous.Sources, current.Sources, changes);
        if (changes.Count == 0)
        {
            changes.Add(Strings.WorkspaceManifest_InputsChanged);
        }

        const int maximumReportedChanges = 6;
        if (changes.Count <= maximumReportedChanges)
        {
            return changes;
        }

        return
        [
            .. changes.Take(maximumReportedChanges),
            LocalizedText.Format(Strings.WorkspaceManifest_MoreChangesFormat, changes.Count - maximumReportedChanges)
        ];
    }

    private static void AddLayerChanges(
        IReadOnlyList<WorkspaceBuildLayerFingerprint> previous,
        IReadOnlyList<WorkspaceBuildLayerFingerprint> current,
        List<string> changes)
    {
        var previousIds = previous.Select(layer => layer.Id).ToArray();
        var currentIds = current.Select(layer => layer.Id).ToArray();
        if (!previousIds.ToHashSet(StringComparer.Ordinal).SetEquals(currentIds))
        {
            changes.Add(Strings.WorkspaceManifest_LayersChanged);
            return;
        }

        if (!previousIds.SequenceEqual(currentIds, StringComparer.Ordinal))
        {
            changes.Add(Strings.WorkspaceManifest_ModOrderChanged);
        }

        var previousById = previous.ToDictionary(layer => layer.Id, StringComparer.Ordinal);
        foreach (var layer in current)
        {
            if (!previousById.TryGetValue(layer.Id, out var oldLayer))
            {
                continue;
            }

            if (!string.Equals(oldLayer.RootPath, layer.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(LocalizedText.Format(Strings.WorkspaceManifest_LayerPathChangedFormat, layer.DisplayName, oldLayer.RootPath, layer.RootPath));
            }
            else if (oldLayer.Order != layer.Order)
            {
                changes.Add(LocalizedText.Format(Strings.WorkspaceManifest_LayerPriorityChangedFormat, layer.DisplayName, oldLayer.Order, layer.Order));
            }
        }
    }

    private static void AddSourceChanges(
        IReadOnlyList<WorkspaceBuildSourceFingerprint> previous,
        IReadOnlyList<WorkspaceBuildSourceFingerprint> current,
        List<string> changes)
    {
        var previousSources = previous.ToDictionary(source => source.LayerId, StringComparer.Ordinal);
        foreach (var source in current)
        {
            if (!previousSources.TryGetValue(source.LayerId, out var oldSource))
            {
                continue;
            }

            if (!oldSource.ExcludedFiles.SequenceEqual(source.ExcludedFiles, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(LocalizedText.Format(Strings.WorkspaceManifest_ExclusionsChangedFormat, source.DisplayName));
            }

            AddFileChanges(oldSource, source, changes);
        }
    }

    private static void AddFileChanges(
        WorkspaceBuildSourceFingerprint previous,
        WorkspaceBuildSourceFingerprint current,
        List<string> changes)
    {
        var oldFiles = previous.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var newFiles = current.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var file in current.Files)
        {
            if (!oldFiles.TryGetValue(file.RelativePath, out var oldFile))
            {
                changes.Add(LocalizedText.Format(Strings.WorkspaceManifest_FileAddedFormat, current.DisplayName, file.RelativePath));
            }
            else if (oldFile.Length != file.Length ||
                     oldFile.LastWriteTimeUtcTicks != file.LastWriteTimeUtcTicks)
            {
                changes.Add(LocalizedText.Format(Strings.WorkspaceManifest_FileChangedFormat, current.DisplayName, file.RelativePath));
            }
        }

        foreach (var file in previous.Files)
        {
            if (!newFiles.ContainsKey(file.RelativePath))
            {
                changes.Add(LocalizedText.Format(Strings.WorkspaceManifest_FileRemovedFormat, current.DisplayName, file.RelativePath));
            }
        }
    }

    private static string DisplayValue(string value, string? emptyValue = null) =>
        string.IsNullOrWhiteSpace(value) ? emptyValue ?? Strings.WorkspaceManifest_NotSet : value;
}

internal sealed class WorkspaceBuildManifest
{
    public string Signature { get; set; } = string.Empty;
    public WorkspaceBuildFingerprint? Fingerprint { get; set; }
    public DateTime BuiltAtUtc { get; set; }
    public bool HasStatistics { get; set; }
    public int FileCount { get; set; }
    public int HardLinkCount { get; set; }
    public int SymbolicLinkCount { get; set; }
    public int LocalFileCount { get; set; }
    public long LogicalSizeBytes { get; set; }
    public long PhysicalSizeBytes { get; set; }
}
