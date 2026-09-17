using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed partial class MainViewModel
{
    private void RecalculateModOverlayInfo()
    {
        _conflictAnalysisCancellation?.Cancel();
        _conflictAnalysisCancellation?.Dispose();
        _conflictAnalysisCancellation = null;
        _conflictAnalysisDebounce.Schedule();
    }

    private void CalculateModOverlayInfo()
    {
        if (_disposed)
        {
            return;
        }

        var profile = SelectedProfile;
        if (profile is null)
        {
            return;
        }

        var plan = TryCreateConflictAnalysisPlan(profile);
        var inputs = plan is null
            ? profile.Mods
                .OrderBy(mod => mod.Order)
                .Select(ModConflictInput.FromMod)
                .ToArray()
            : Array.Empty<ModConflictInput>();
        var cancellation = new CancellationTokenSource();
        _conflictAnalysisCancellation = cancellation;
        _ = ApplyConflictAnalysisAsync(profile, plan, inputs, cancellation.Token);
    }

    private async Task ApplyConflictAnalysisAsync(
        ModProfile profile,
        FileLayerPlan? plan,
        IReadOnlyList<ModConflictInput> inputs,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = plan is not null
                ? await _modConflictAnalyzer.AnalyzeAsync(
                    plan,
                    profile.ExecutableRelativePath,
                    profile.ExecutableSourcePath,
                    cancellationToken)
                : await _modConflictAnalyzer.AnalyzeAsync(
                    inputs,
                    profile.ExecutableRelativePath,
                    profile.ExecutableSourcePath,
                    cancellationToken);
            await InvokeOnUiAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || SelectedProfile != profile)
                {
                    return;
                }

                foreach (var mod in profile.Mods)
                {
                    ApplyConflictState(mod, result.GetValueOrDefault(mod.Id), profile.ExecutableRelativePath);
                }

                UpdateRelatedModHighlights();
                FilteredMods?.Refresh();
            });
        }
        catch (OperationCanceledException)
        {
            // A newer profile or mod state superseded this analysis.
        }
    }

    private static FileLayerPlan? TryCreateConflictAnalysisPlan(ModProfile profile)
    {
        if (profile.IsStandalone || string.IsNullOrWhiteSpace(profile.GameInstallPath))
        {
            return null;
        }

        var workspaceRoot = string.IsNullOrWhiteSpace(profile.WorkspacePath)
            ? Path.Combine(AppPaths.Current.TempDirectory, "analysis", profile.Id)
            : profile.WorkspacePath;
        return FileLayerPlan.CreateLinkedWorkspace(profile.GameInstallPath, profile, workspaceRoot);
    }

    private static void ApplyConflictState(ModEntry mod, ModConflictState? state, string executableRelativePath)
    {
        mod.ConflictKind = state?.ConflictKind ?? (mod.IsEnabled ? ModConflictKind.None : ModConflictKind.Disabled);
        mod.HasOverlapsAbove = state?.HasOverlapsAbove ?? false;
        mod.OverwrittenFileCount = state?.OverwrittenFileCount ?? 0;
        mod.OverwrittenModCount = state?.OverwrittenModNames.Count ?? 0;
        mod.OverwrittenByFileCount = state?.OverwrittenByFileCount ?? 0;
        mod.OverwrittenByModCount = state?.OverwrittenByModNames.Count ?? 0;
        mod.OverwrittenByBinaryCount = state?.OverwrittenByBinaryCount ?? 0;
        mod.RelatedModIds = state?.RelatedModIds ?? [];
        mod.ProvidesLaunchExecutable = state?.ProvidesLaunchExecutable ?? false;
        mod.OverwrittenConfigurationCount = state?.OverwrittenConfigurationCount ?? 0;
        mod.OverwrittenBinaryCount = state?.OverwrittenBinaryCount ?? 0;
        var details = new List<string> { mod.ConflictDisplay };
        if (state is { OverwrittenModNames.Count: > 0 })
        {
            details.Add(LocalizedText.Format(Strings.ModAnalysis_OverwritesFormat, string.Join(", ", state.OverwrittenModNames)));
            details.Add(LocalizedText.Format(Strings.ModAnalysis_OverwriteCountsFormat, state.OverwrittenConfigurationCount, state.OverwrittenBinaryCount));
        }

        if (state is { OverwrittenByModNames.Count: > 0 })
        {
            details.Add(LocalizedText.Format(Strings.ModAnalysis_OverwrittenByFormat, string.Join(", ", state.OverwrittenByModNames)));
            details.Add(LocalizedText.Format(Strings.ModAnalysis_OverwrittenCountsFormat, state.OverwrittenByConfigurationCount, state.OverwrittenByBinaryCount));
        }

        if (state?.ProvidesLaunchExecutable == true)
        {
            details.Add(LocalizedText.Format(Strings.ModAnalysis_FinalExecutableFormat, executableRelativePath));
        }

        mod.OverlayDetails = string.Join(Environment.NewLine, details);
    }
}
