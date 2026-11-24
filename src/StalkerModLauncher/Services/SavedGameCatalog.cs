using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal sealed record SavedGameCatalog(string DirectoryPath, string? Extension, IReadOnlyList<string>? VirtualDirectories = null,
    Func<string, bool>? IsVisibleVirtualFile = null)
{
    public IEnumerable<FileInfo> Enumerate() => (VirtualDirectories ?? [DirectoryPath])
        .Where(Directory.Exists)
        .SelectMany(directory => new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }))
        .Where(file => (Extension is not null ? Extension.Equals(file.Extension, StringComparison.OrdinalIgnoreCase)
            : file.Extension.Equals(".sav", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".scop", StringComparison.OrdinalIgnoreCase)) &&
            (IsVisibleVirtualFile is null || IsVisibleVirtualFile(file.FullName)))
        .OrderByDescending(file => file.LastWriteTimeUtc)
        .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase);

    public bool Contains(string path) =>
        (VirtualDirectories ?? [DirectoryPath]).Contains(Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase) &&
        File.Exists(path) && (IsVisibleVirtualFile is null || IsVisibleVirtualFile(path));

    public static SavedGameCatalog Preview(ModProfile profile)
    {
        if (profile.IsStandalone)
        {
            var plan = RequirePlan(ProfileLaunchPlanResolver.PreviewStandalone(profile));
            return ForPhysicalPlan(plan, SavedGameLaunch.ResolvePhysicalEngine(plan, allowUnconfiguredLauncher: true));
        }

        var layerPlan = FileLayerPlan.CreateForProfile(profile.GameInstallPath, profile, profile.WorkspacePath);
        if (profile.LaunchBackendKind == LaunchBackendKind.VirtualFileSystem)
        {
            var resolution = ProfileLaunchPlanResolver.PreviewVirtualFileSystem(profile, layerPlan);
            _ = RequirePlan(resolution);
            var target = AnomalyUsvfsLaunchTargetResolver.Resolve(profile, layerPlan, resolution, forSavedGame: true, allowUnconfiguredLauncher: true);
            return ForVirtualTarget(layerPlan, target);
        }

        var workspace = ProfileLaunchPlanResolver.PreviewLinkedWorkspace(profile, layerPlan, profile.WorkspacePath);
        var planPreview = RequirePlan(workspace);
        var relativeExe = workspace.Executable!.RelativePath;
        var sourceExe = workspace.Executable.FullPath;
        var current = Path.Combine(profile.WorkspacePath, "current");
        var relativeExeDirectory = Path.GetDirectoryName(relativeExe)!;
        if (AnomalyLauncherLocator.IsLauncherExecutable(sourceExe) ||
            AnomalyLauncherLocator.IsBaseGameLauncher(profile.GameInstallPath, sourceExe))
        {
            var engine = SavedGameLaunch.TryResolveAnomalyEngine(path =>
                AnomalyUsvfsLaunchTargetResolver.FindProfileFile(layerPlan, Path.Combine(relativeExeDirectory, path)));
            if (engine is not null && layerPlan.FindFinalFile(Path.Combine(relativeExeDirectory, engine)) is { } engineSource)
            {
                sourceExe = engineSource.FullPath;
                relativeExe = engineSource.RelativePath;
            }
        }

        var source = layerPlan.FindFsgameSource() ?? throw new FileNotFoundException(ProfileDataConfigurator.MissingFsgameMessage);
        return Create(source.FullPath, planPreview.WorkingDirectory, layerPlan.GameDataRoot, sourceExe,
            layerPlan.FindFinalFile(Path.Combine(Path.GetDirectoryName(relativeExe)!, "xrGame.dll"))?.FullPath,
            planPreview.Arguments,
            path => FindLayeredFile(layerPlan, current, path, useOverwrite: false));
    }

    public static SavedGameCatalog ForPhysicalPlan(LaunchPlan plan, string executable)
    {
        var configArgument = FileLayerPlan.ResolveFsgameLaunchArgument(plan.Arguments, allowAbsolute: plan.BackendKind == LaunchBackendKind.Standalone);
        var config = Path.Combine(plan.WorkingDirectory, configArgument ?? "fsgame.ltx");
        return Create(config, plan.WorkingDirectory, null, executable,
            ExistingFile(Path.Combine(Path.GetDirectoryName(executable)!, "xrGame.dll")), plan.Arguments, ExistingFile);
    }

    public static SavedGameCatalog ForVirtualTarget(FileLayerPlan layerPlan, UsvfsLaunchTarget target, string? preparedConfig = null)
    {
        var config = preparedConfig ?? layerPlan.FindFsgameSource()?.FullPath
            ?? throw new FileNotFoundException(ProfileDataConfigurator.MissingFsgameMessage);
        var baseRoot = layerPlan.BaseGame.RootPath;
        var module = Path.Combine(baseRoot, Path.GetDirectoryName(target.ExecutableRelativePath)!, "xrGame.dll");
        string? FindFile(string path) => FindLayeredFile(layerPlan, baseRoot, path, useOverwrite: true);
        var catalog = Create(config, target.WorkingDirectory, layerPlan.GameDataRoot, target.ExecutablePath, FindFile(module),
            target.Arguments, FindFile);
        if (layerPlan.UsesAnomalyAppDataAlias && layerPlan.UsesSharedGameData &&
            FileSystemSafety.IsDirectoryInside(catalog.DirectoryPath, Path.Combine(baseRoot, "appdata")))
        {
            var relative = Path.GetRelativePath(Path.Combine(baseRoot, "appdata"), catalog.DirectoryPath);
            return catalog with { DirectoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(relative, layerPlan.GameDataRoot)) };
        }
        if (FileSystemSafety.IsDirectoryInside(catalog.DirectoryPath, layerPlan.GameDataRoot) ||
            !FileSystemSafety.IsDirectoryInside(catalog.DirectoryPath, baseRoot))
        {
            return catalog;
        }

        var virtualRelative = Path.GetRelativePath(baseRoot, catalog.DirectoryPath);
        var overwrite = GetOverwriteRoot(layerPlan);
        var directories = layerPlan.SourceLayers.Select(layer => Path.GetFullPath(virtualRelative, layer.RootPath))
            .Append(Path.GetFullPath(virtualRelative, overwrite)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return catalog with { VirtualDirectories = directories, IsVisibleVirtualFile = IsVisible };

        bool IsVisible(string path)
        {
            var effective = FindFile(Path.Combine(catalog.DirectoryPath, Path.GetFileName(path)));
            return effective is not null && string.Equals(effective, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string GetOverwriteRoot(FileLayerPlan layers) =>
        Path.Combine(Path.GetDirectoryName(layers.UserData.RootPath)!, ProfileWritableGameFiles.DefaultOverwriteRootRelativePath);

    private static string? FindLayeredFile(FileLayerPlan layers, string root, string path, bool useOverwrite)
    {
        if (!FileSystemSafety.IsDirectoryInside(path, root)) return ExistingFile(path);

        var relative = Path.GetRelativePath(root, path);
        return (useOverwrite ? ExistingFile(Path.Combine(GetOverwriteRoot(layers), relative)) : null)
            ?? layers.FindFinalFile(relative)?.FullPath;
    }

    private static SavedGameCatalog Create(
        string config, string fsRoot, string? appDataRoot, string exe, string? module, string arguments, Func<string, string?> findFile)
    {
        var aliases = ProfileAppDataSourceLocator.ReadAliases(config);
        var directory = ProfileAppDataSourceLocator.ResolveAlias(aliases, "$game_saves$", fsRoot, appDataRoot);
        return new SavedGameCatalog(Path.TrimEndingDirectorySeparator(directory),
            SavedGameLaunch.ResolveSaveExtension(exe, module, arguments, FindOpenXrayConfig));

        string? FindOpenXrayConfig() => aliases.ContainsKey("$game_config$")
            ? findFile(Path.Combine(ProfileAppDataSourceLocator.ResolveAlias(aliases, "$game_config$", fsRoot, appDataRoot), "openxray.ltx"))
            : null;
    }

    private static LaunchPlan RequirePlan(LaunchPlanResolution resolution) => resolution.IsReady && resolution.Plan is { } plan
        ? plan
        : throw new InvalidOperationException(resolution.Error ?? Strings.Error_UsvfsLaunchPlanNotReady);

    internal static string? ExistingFile(string path) => File.Exists(path) ? path : null;
}
