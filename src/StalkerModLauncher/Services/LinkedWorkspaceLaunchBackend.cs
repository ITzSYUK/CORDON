using System.Text;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed class LinkedWorkspaceLaunchBackend : IProfileLaunchBackend
{
    private const string AutoLoadBeginMarker = "-- STALKER_MOD_LAUNCHER_AUTOLOAD_BEGIN";
    private const string AutoLoadEndMarker = "-- STALKER_MOD_LAUNCHER_AUTOLOAD_END";
    private readonly WorkspaceBuilder _workspaceBuilder;

    public LinkedWorkspaceLaunchBackend(WorkspaceBuilder workspaceBuilder)
    {
        _workspaceBuilder = workspaceBuilder;
    }

    public LaunchBackendKind Kind => LaunchBackendKind.LinkedWorkspace;

    public async Task<LaunchPlan> PrepareAsync(
        ProfileLaunchBackendContext context,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        var profile = context.Profile;
        var workspace = await _workspaceBuilder.BuildAsync(
            context.GamePath,
            profile,
            progress,
            context.FileLayerPlan,
            cancellationToken);
        profile.WorkspacePath = workspace.ProfileWorkspacePath;
        profile.ExecutableRelativePath = workspace.ExecutableRelativePath;
        profile.WorkingDirectoryRelative = workspace.WorkingDirectoryRelative;
        RemoveLegacyScriptAutoloadPatch(workspace, profile, progress);

        return ProfileLaunchPlanResolver.CreatePreparedPlan(LaunchBackendKind.LinkedWorkspace, profile, workspace);
    }

    private static void RemoveLegacyScriptAutoloadPatch(WorkspaceBuildResult workspace, ModProfile profile, IProgress<string> progress)
    {
        if (profile.IsStandalone)
        {
            return;
        }

        var scriptRoot = string.IsNullOrWhiteSpace(profile.WorkingDirectoryRelative)
            ? workspace.WorkspaceRoot
            : Path.Combine(workspace.WorkspaceRoot, profile.WorkingDirectoryRelative);
        var scriptPath = Path.Combine(scriptRoot, "gamedata", "scripts", "ui_main_menu.script");
        if (!File.Exists(scriptPath))
        {
            return;
        }

        var text = File.ReadAllText(scriptPath, Encoding.Default);
        var cleanedText = RemoveAutoloadBlock(text);

        if (cleanedText == text)
        {
            return;
        }

        // This file can be linked from the source mod. Delete the workspace entry first so
        // the cleanup never writes back into the mod folder.
        File.Delete(scriptPath);
        File.WriteAllText(scriptPath, cleanedText, Encoding.Default);
        progress.Report(Strings.Progress_LegacySaveHookRemoved);
    }

    private static string RemoveAutoloadBlock(string text)
    {
        while (true)
        {
            var begin = text.IndexOf(AutoLoadBeginMarker, StringComparison.Ordinal);
            if (begin < 0)
            {
                return text;
            }

            var end = text.IndexOf(AutoLoadEndMarker, begin, StringComparison.Ordinal);
            if (end < 0)
            {
                return text[..begin];
            }

            end += AutoLoadEndMarker.Length;
            text = text.Remove(begin, end - begin);
        }
    }
}
