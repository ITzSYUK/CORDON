using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class UsvfsMappingPlanBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherUsvfsMappingTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildUsesPhysicalBaseGameAndMapsOnlyModsToVirtualRootInPriorityOrder()
    {
        var game = CreateDirectory("game");
        var firstMod = CreateDirectory("mod1");
        var patch = CreateDirectory("mod2");
        var workspace = CreateDirectory("workspace");
        var profile = new ModProfile
        {
            Name = "Layered",
            GameInstallPath = game
        };
        profile.Mods.Add(new ModEntry
        {
            Id = "mod1",
            Name = "Main mod",
            SourcePath = firstMod,
            IsEnabled = true,
            Order = 1
        });
        profile.Mods.Add(new ModEntry
        {
            Id = "mod2",
            Name = "Patch",
            SourcePath = patch,
            IsEnabled = true,
            Order = 2
        });
        var layerPlan = FileLayerPlan.CreateLinkedWorkspace(game, profile, workspace);
        var manifest = OverlayManifestBuilder.BuildLinkedWorkspace(profile, layerPlan, workspace);

        var plan = UsvfsMappingPlanBuilder.Build(layerPlan, manifest);

        Assert.Equal(Path.GetFullPath(game), plan.VirtualRoot);
        Assert.Equal(
            [Path.GetFullPath(firstMod), Path.GetFullPath(patch)],
            plan.Operations
                .Where(operation => operation.SourceName != Strings.Layer_ProfileOverwrite)
                .Select(operation => operation.SourcePath)
                .ToArray());
        Assert.DoesNotContain(
            plan.Operations,
            operation => string.Equals(operation.SourcePath, plan.VirtualRoot, StringComparison.OrdinalIgnoreCase));
        Assert.All(
            plan.Operations.Where(operation => operation.SourceName != Strings.Layer_ProfileOverwrite),
            operation => Assert.Equal(Path.GetFullPath(game), operation.DestinationPath));
    }

    [Fact]
    public void BuildAddsProfileOverwriteAsCreateTargetAtHighestPriority()
    {
        var game = CreateDirectory("game");
        var workspace = CreateDirectory("workspace");
        var profile = new ModProfile
        {
            Name = "Layered",
            GameInstallPath = game
        };
        var layerPlan = FileLayerPlan.CreateLinkedWorkspace(game, profile, workspace);
        var manifest = OverlayManifestBuilder.BuildLinkedWorkspace(profile, layerPlan, workspace);

        var plan = UsvfsMappingPlanBuilder.Build(layerPlan, manifest);

        var overwrite = Assert.Single(plan.Operations, operation => operation.SourceName == Strings.Layer_ProfileOverwrite);
        Assert.Equal(UsvfsMappingKind.DirectoryStatic, overwrite.Kind);
        Assert.Equal(Path.GetFullPath(manifest.WriteOverlayRoot), overwrite.SourcePath);
        Assert.Equal(Path.GetFullPath(game), overwrite.DestinationPath);
        Assert.True(overwrite.MonitorChanges);
        Assert.True(overwrite.CreateTarget);
    }

    [Fact]
    public void BuildMapsBaseGameWhenUsingSeparateBootstrapVirtualRoot()
    {
        var game = CreateDirectory("game");
        var mod = CreateDirectory("mod");
        var workspace = CreateDirectory("workspace");
        var virtualRoot = CreateDirectory("bootstrap-root");
        var profile = new ModProfile { Name = "Layered", GameInstallPath = game };
        profile.Mods.Add(new ModEntry
        {
            Id = "mod",
            Name = "Mod",
            SourcePath = mod,
            IsEnabled = true,
            Order = 1
        });
        var layerPlan = FileLayerPlan.CreateLinkedWorkspace(game, profile, workspace);
        var manifest = OverlayManifestBuilder.BuildLinkedWorkspace(profile, layerPlan, workspace);

        var plan = UsvfsMappingPlanBuilder.Build(layerPlan, manifest, virtualRoot);

        Assert.Equal(Path.GetFullPath(virtualRoot), plan.VirtualRoot);
        Assert.Equal(
            [Path.GetFullPath(game), Path.GetFullPath(mod), Path.GetFullPath(manifest.WriteOverlayRoot)],
            plan.Operations.Select(operation => operation.SourcePath).ToArray());
        Assert.All(plan.Operations, operation => Assert.Equal(Path.GetFullPath(virtualRoot), operation.DestinationPath));
    }

    [Fact]
    public void BuildMapsWritableStoreWhenModHasNoConfigsDirectory()
    {
        var game = CreateDirectory("game");
        var mod = CreateDirectory("mod");
        var workspace = CreateDirectory("workspace");
        Directory.CreateDirectory(Path.Combine(mod, "gamedata", "textures"));
        var profile = new ModProfile
        {
            Name = "Layered",
            GameInstallPath = game
        };
        profile.Mods.Add(new ModEntry
        {
            Id = "mod",
            Name = "No configs",
            SourcePath = mod,
            IsEnabled = true,
            Order = 1
        });
        var layerPlan = FileLayerPlan.CreateLinkedWorkspace(game, profile, workspace);
        var manifest = OverlayManifestBuilder.BuildLinkedWorkspace(profile, layerPlan, workspace);
        var writableFile = manifest.WritableFiles.Single(file =>
            file.RelativePath == Path.Combine("gamedata", "configs", "localization.ltx"));
        Directory.CreateDirectory(Path.GetDirectoryName(writableFile.StoragePath)!);
        File.WriteAllText(writableFile.StoragePath, "language = rus");

        var plan = UsvfsMappingPlanBuilder.Build(layerPlan, manifest);

        var knownWritable = Assert.Single(
            plan.Operations,
            operation => operation.SourceName == Strings.Layer_ProfileWritableData);
        Assert.Equal(UsvfsMappingKind.DirectoryStatic, knownWritable.Kind);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(workspace), ProfileWritableGameFiles.WritableGameFilesRootRelativePath),
            knownWritable.SourcePath);
        Assert.Equal(Path.GetFullPath(game), knownWritable.DestinationPath);
        Assert.True(knownWritable.Order < plan.Operations.Single(operation => operation.SourceName == Strings.Layer_ProfileOverwrite).Order);
        Assert.False(Directory.Exists(Path.Combine(mod, "gamedata", "configs")));
    }

    [Fact]
    public void BuildDoesNotMapProfileUserdataWhenGameDataIsShared()
    {
        var game = CreateDirectory("shared-game");
        var workspace = CreateDirectory("shared-workspace");
        File.WriteAllText(Path.Combine(game, "fsgame.ltx"), "$app_data_root$ = true| false| $fs_root$| userdata\\");
        var profile = new ModProfile
        {
            Name = "Shared data",
            GameInstallPath = game,
            UseBaseGameData = true
        };
        var layerPlan = FileLayerPlan.CreateLinkedWorkspace(game, profile, workspace);
        var manifest = OverlayManifestBuilder.BuildLinkedWorkspace(profile, layerPlan, workspace);
        Directory.CreateDirectory(Path.Combine(manifest.WriteOverlayRoot, "userdata", "savedgames"));
        File.WriteAllText(Path.Combine(manifest.WriteOverlayRoot, "fsgame.ltx"), "profile config");

        var plan = UsvfsMappingPlanBuilder.Build(layerPlan, manifest);

        Assert.DoesNotContain(plan.Operations, operation =>
            operation.DestinationPath.Equals(Path.Combine(game, "userdata"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Operations, operation =>
            operation.DestinationPath.Equals(Path.Combine(game, "fsgame.ltx"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildRestoresPreviousProviderForExcludedConflictFile()
    {
        var game = CreateDirectory("excluded-game");
        var first = CreateDirectory("excluded-first");
        var patch = CreateDirectory("excluded-patch");
        var workspace = CreateDirectory("excluded-workspace");
        File.WriteAllText(Path.Combine(game, "shared.ltx"), "base");
        var firstFile = Path.Combine(first, "shared.ltx");
        File.WriteAllText(firstFile, "first");
        File.WriteAllText(Path.Combine(patch, "shared.ltx"), "patch");
        var profile = new ModProfile { GameInstallPath = game };
        profile.Mods.Add(new ModEntry { Id = "first", Name = "First", SourcePath = first, Order = 1 });
        profile.Mods.Add(new ModEntry
        {
            Id = "patch",
            Name = "Patch",
            SourcePath = patch,
            Order = 2,
            ExcludedFiles = ["shared.ltx"]
        });
        var layerPlan = FileLayerPlan.CreateLinkedWorkspace(game, profile, workspace);
        var manifest = OverlayManifestBuilder.BuildLinkedWorkspace(profile, layerPlan, workspace);

        var plan = UsvfsMappingPlanBuilder.Build(layerPlan, manifest);

        var fallback = Assert.Single(
            plan.Operations,
            operation => operation.SourceName == LocalizedText.Format(
                Strings.Layer_ExcludedFallbackFormat,
                LocalizedText.Format(Strings.Layer_ModFormat, "First")));
        Assert.Equal(UsvfsMappingKind.File, fallback.Kind);
        Assert.Equal(Path.GetFullPath(firstFile), fallback.SourcePath);
        Assert.Equal(Path.Combine(Path.GetFullPath(game), "shared.ltx"), fallback.DestinationPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }
}
