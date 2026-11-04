using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class WorkspaceBuilderTests : IDisposable
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteOldWorkspacePreservesReadOnlyAttributeOfDisabledOrRemovedMod(bool removed)
    {
        var profile = CreateProfile();
        var workspace = _builder.EnsureProfileWorkspace(profile, _gamePath);
        profile.WorkspacePath = workspace;
        var source = Path.Combine(_root, "disabled", "file.txt");
        CreateFileAtPath(source, "original");
        if (!removed)
            profile.Mods.Add(new ModEntry { SourcePath = Path.GetDirectoryName(source)!, IsEnabled = false });
        Assert.True(CreateHardLink(Path.Combine(workspace, "old-link.txt"), source, IntPtr.Zero));
        File.SetAttributes(source, FileAttributes.ReadOnly);
        try
        {
            _builder.DeleteProfileWorkspace(profile, _gamePath);
            Assert.False(Directory.Exists(workspace));
            Assert.True((File.GetAttributes(source) & FileAttributes.ReadOnly) != 0);
            Assert.Equal("original", File.ReadAllText(source));
        }
        finally { File.SetAttributes(source, FileAttributes.Normal); }
    }

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _gamePath;
    private readonly string _workspaceRoot;
    private readonly WorkspaceBuilder _builder;

    public WorkspaceBuilderTests()
    {
        _gamePath = Path.Combine(_root, "game");
        _workspaceRoot = Path.Combine(_root, "workspaces");
        _builder = new WorkspaceBuilder(new AppPaths(
            Path.Combine(_root, "config"),
            _workspaceRoot,
            preferGameDriveWorkspace: false));

        CreateFile(_gamePath, "bin/xr_3da.exe", "base executable");
        CreateFile(_gamePath, "fsgame.ltx", "$app_data_root$ = true | false | appdata");
        CreateFile(_gamePath, "gamedata/config/shared.ltx", "base");
        CreateFile(_gamePath, "appdata/user.ltx", "base user settings");
    }

    [Fact]
    public void DeleteProfileWorkspaceRestoresMarkerForRenamedLegacyUsvfsWorkspace()
    {
        var profile = CreateProfile();
        var oldName = "Аномали — копия";
        profile.WorkspacePath = Path.Combine(
            _workspaceRoot,
            $"{oldName}-{profile.Id[..8]}");
        CreateFileAtPath(Path.Combine(profile.WorkspacePath, "userdata", "test.txt"), "profile data");
        profile.Name = "Новое имя";

        _builder.DeleteProfileWorkspace(profile, _gamePath);

        Assert.False(Directory.Exists(profile.WorkspacePath));
        Assert.True(File.Exists(Path.Combine(_workspaceRoot, ".stalker-launcher-workspace-root")));
    }

    [Fact]
    public async Task CompletelyEmptyAssignedWorkspaceSwitchesToLocalDataAndPersistsTheNewPath()
    {
        var paths = new AppPaths(Path.Combine(_root, "launcher"));
        var builder = new WorkspaceBuilder(paths);
        var manager = new ProfileManager(paths, builder);
        var profile = CreateProfile();
        profile.GameInstallPath = _gamePath;
        var old = Path.Combine(_root, "StalkerModLauncher", "Workspaces", $"profile-{profile.Id}");
        Directory.CreateDirectory(old);
        profile.WorkspacePath = old;
        var progress = new ProgressLog();

        var workspace = manager.EnsureProfileFolderPath(profile, progress);
        using var store = new SettingsStore(paths);
        await store.SaveAsync(new AppSettings { Profiles = [profile] });

        Assert.Equal(Path.Combine(paths.ConfigDirectory, "Workspaces", $"profile-{profile.Id}"), workspace);
        Assert.Equal(workspace, profile.WorkspacePath);
        Assert.Equal(workspace, Assert.Single((await store.LoadAsync()).Profiles).WorkspacePath);
        Assert.True(File.Exists(Path.Combine(workspace, ".stalker-launcher-workspace")));
        Assert.True(Directory.Exists(old));
        Assert.Empty(Directory.EnumerateFileSystemEntries(old));
        Assert.Contains(progress.Messages, message => message.Contains(old, StringComparison.Ordinal) && message.Contains(workspace, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("hidden")]
    [InlineData("directory")]
    public void AssignedWorkspaceWithAnyContentNeverSwitchesToLocalData(string content)
    {
        var profile = CreateProfile();
        var parent = Path.Combine(_root, "StalkerModLauncher", "Workspaces");
        profile.WorkspacePath = Path.Combine(parent, $"profile-{profile.Id}");
        Directory.CreateDirectory(profile.WorkspacePath);
        var entry = Path.Combine(profile.WorkspacePath, content);
        if (content == "directory") Directory.CreateDirectory(entry);
        else
        {
            File.WriteAllText(entry, "keep");
            if (content == "hidden") File.SetAttributes(entry, FileAttributes.Hidden);
        }

        Assert.Throws<InvalidOperationException>(() => _builder.EnsureProfileWorkspace(profile, _gamePath));

        Assert.True(Path.Exists(entry));
        Assert.False(Directory.Exists(_workspaceRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidUsvfsDestinationDoesNotCreateWorkspaceOrChangeAssignedPath(bool emptyOldWorkspace)
    {
        var paths = new AppPaths(Path.Combine(_root, "Документы", "launcher"));
        var profile = CreateProfile();
        profile.GameInstallPath = _gamePath;
        var old = emptyOldWorkspace ? Path.Combine(_root, "old", $"profile-{profile.Id}") : string.Empty;
        if (emptyOldWorkspace) Directory.CreateDirectory(old);
        profile.WorkspacePath = old;

        var error = Assert.Throws<InvalidOperationException>(() => new ProfileManager(paths, new WorkspaceBuilder(paths)).EnsureProfileFolderPath(profile));

        Assert.Equal(Strings.Error_UsvfsAsciiPath, error.Message);
        Assert.Equal(old, profile.WorkspacePath);
        Assert.False(Directory.Exists(paths.WorkspaceRoot));
        if (emptyOldWorkspace) Assert.Empty(Directory.EnumerateFileSystemEntries(old));
    }

    [Fact]
    public void InvalidLegacyRenameDestinationLeavesOriginalFolderAndDataUntouched()
    {
        var root = Path.Combine(_root, "Документы", "Workspaces");
        var profile = CreateProfile();
        var old = Path.Combine(root, $"old-name-{profile.Id[..8]}");
        CreateFile(old, "userdata/save.dat", "save");
        var builder = new WorkspaceBuilder(new AppPaths(Path.Combine(_root, "config"), root, false));

        Assert.Throws<InvalidOperationException>(() => builder.EnsureProfileWorkspace(profile, _gamePath));

        Assert.Equal("save", File.ReadAllText(Path.Combine(old, "userdata", "save.dat")));
        Assert.False(Directory.Exists(Path.Combine(root, $"profile-{profile.Id}")));
        Assert.False(File.Exists(Path.Combine(root, WorkspaceBuilder.RootMarkerFileName)));
    }

    [Theory]
    [InlineData("Workspace", false, false, false)]
    [InlineData("Workspaces", false, false, false)]
    [InlineData("Workspace", true, false, false)]
    [InlineData("Workspaces", false, true, false)]
    [InlineData("Workspaces", true, true, false)]
    [InlineData("Workspaces", false, false, true)]
    public void LaunchKeepsAssignedLegacyWorkspaceAndRestoresMarkers(string rootName, bool profileMarker, bool rootMarker, bool otherDrive)
    {
        var profile = CreateProfile();
        profile.GameInstallPath = _gamePath;
        var legacyRoot = Path.Combine(_root, "StalkerModLauncher", rootName);
        var workspace = Path.Combine(legacyRoot, $"profile-{profile.Id}");
        profile.WorkspacePath = workspace;
        CreateFile(workspace, "userdata/savedgames/old.sav", "save");
        CreateFile(workspace, "userdata/user.ltx", "user settings");
        CreateFile(workspace, "userdata/writable-game-files/AnomalyLauncher.cfg", "launcher settings");
        if (profileMarker) CreateFile(workspace, ".stalker-launcher-workspace", "managed");
        if (rootMarker) CreateFile(legacyRoot, WorkspaceBuilder.RootMarkerFileName, "managed");
        CreateFile(legacyRoot, "profile-sibling/userdata/valuable.sav", "keep sibling");
        var preferred = otherDrive ? Path.Combine(GetMissingDrive(), "StalkerModLauncher", "Workspaces") : _workspaceRoot;
        var paths = new AppPaths(Path.Combine(_root, "config"), preferred, false);
        var builder = new WorkspaceBuilder(paths);
        var manager = new ProfileManager(paths, builder);

        Assert.Equal(workspace, manager.EnsureProfileFolderPath(profile));

        Assert.Equal(workspace, profile.WorkspacePath);
        Assert.Equal(Path.Combine(workspace, "userdata", "savedgames"), WorkspaceBuilder.GetSavedGamesPath(profile));
        Assert.Equal("save", File.ReadAllText(Path.Combine(workspace, "userdata", "savedgames", "old.sav")));
        Assert.Equal("user settings", File.ReadAllText(Path.Combine(workspace, "userdata", "user.ltx")));
        Assert.Equal("launcher settings", File.ReadAllText(Path.Combine(workspace, "userdata", "writable-game-files", "AnomalyLauncher.cfg")));
        Assert.True(File.Exists(Path.Combine(workspace, ".stalker-launcher-workspace")));
        Assert.True(File.Exists(Path.Combine(legacyRoot, WorkspaceBuilder.RootMarkerFileName)));
        Assert.False(Directory.Exists(_workspaceRoot));
        builder.DeleteProfileWorkspace(profile, _gamePath);
        Assert.False(Directory.Exists(workspace));
        Assert.Equal("keep sibling", File.ReadAllText(Path.Combine(legacyRoot, "profile-sibling", "userdata", "valuable.sav")));
        Assert.Equal("base", File.ReadAllText(Path.Combine(_gamePath, "gamedata", "config", "shared.ltx")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingAssignedWorkspaceNeverFallsBackOrCreatesAnEmptyReplacement(bool otherDrive)
    {
        var profile = CreateProfile();
        var missingDrive = GetMissingDrive();
        var old = otherDrive ? Path.Combine(missingDrive, "StalkerModLauncher", "Workspace", $"profile-{profile.Id}")
            : Path.Combine(_workspaceRoot, $"profile-{profile.Id}");
        profile.WorkspacePath = old;
        CreateFile(_workspaceRoot, $"other-name-{profile.Id[..8]}/userdata/old.sav", "unrelated fallback");

        var error = Assert.Throws<DirectoryNotFoundException>(() => _builder.EnsureProfileWorkspace(profile, _gamePath));

        Assert.Contains(old, error.Message);
        Assert.Equal(old, profile.WorkspacePath);
        Assert.False(Directory.Exists(Path.Combine(_workspaceRoot, $"profile-{profile.Id}")));
        Assert.True(Directory.Exists(Path.Combine(_workspaceRoot, $"other-name-{profile.Id[..8]}")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void UnverifiedLegacyFolderCannotBeAdoptedOrDeleted(bool matchingId, bool recognizedParent)
    {
        var profile = CreateProfile();
        var parent = Path.Combine(_root, recognizedParent ? "StalkerModLauncher" : "Unrelated", "Workspaces");
        profile.WorkspacePath = Path.Combine(parent, $"profile-{(matchingId ? profile.Id : Guid.NewGuid().ToString("N"))}");
        CreateFile(profile.WorkspacePath, "userdata/savedgames/valuable.sav", "keep");

        Assert.Throws<InvalidOperationException>(() => _builder.EnsureProfileWorkspace(profile, _gamePath));
        Assert.Throws<InvalidOperationException>(() => _builder.DeleteProfileWorkspace(profile, _gamePath));

        Assert.False(File.Exists(Path.Combine(parent, WorkspaceBuilder.RootMarkerFileName)));
        Assert.False(File.Exists(Path.Combine(profile.WorkspacePath, ".stalker-launcher-workspace")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(profile.WorkspacePath, "userdata", "savedgames", "valuable.sav")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssignedLegacyNameIsKeptInsteadOfRenamingOrUsingAnotherMatchingFolder(bool missingData)
    {
        var profile = CreateProfile();
        var parent = Path.Combine(_root, "StalkerModLauncher", "Workspaces");
        profile.WorkspacePath = Path.Combine(parent, $"old-name-{profile.Id[..8]}");
        CreateFile(profile.WorkspacePath, missingData ? "valuable.txt" : "userdata/savedgames/old.sav", "keep");
        CreateFile(_workspaceRoot, $"profile-{profile.Id}/userdata/other.sav", "other");

        if (missingData)
        {
            Assert.Throws<InvalidOperationException>(() => _builder.EnsureProfileWorkspace(profile, _gamePath));
            Assert.False(File.Exists(Path.Combine(parent, WorkspaceBuilder.RootMarkerFileName)));
        }
        else Assert.Equal(profile.WorkspacePath, _builder.EnsureProfileWorkspace(profile, _gamePath));

        Assert.True(Directory.Exists(profile.WorkspacePath));
        Assert.False(Directory.Exists(Path.Combine(parent, $"profile-{profile.Id}")));
        Assert.Equal("other", File.ReadAllText(Path.Combine(_workspaceRoot, $"profile-{profile.Id}", "userdata", "other.sav")));
    }

    [Fact]
    public void LegacyAdoptionRejectsJunctionsWithoutAuthorizingDeletionOfTheirTargets()
    {
        var profile = CreateProfile();
        var parent = Path.Combine(_root, "StalkerModLauncher", "Workspace");
        profile.WorkspacePath = Path.Combine(parent, $"profile-{profile.Id}");
        Directory.CreateDirectory(profile.WorkspacePath);
        var target = Path.Combine(_root, "external");
        CreateFile(target, "userdata/savedgames/valuable.sav", "keep");
        AnomalyAppDataAlias.Prepare(profile.WorkspacePath, FileLayerPlan.CreateForProfile(_gamePath, profile, target));
        var junction = Path.Combine(profile.WorkspacePath, "userdata");
        Directory.Move(Path.Combine(profile.WorkspacePath, "appdata"), junction);
        try
        {
            Assert.Throws<InvalidOperationException>(() => _builder.EnsureProfileWorkspace(profile, _gamePath));
            Assert.Throws<InvalidOperationException>(() => _builder.DeleteProfileWorkspace(profile, _gamePath));
            Assert.False(File.Exists(Path.Combine(parent, WorkspaceBuilder.RootMarkerFileName)));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "userdata", "savedgames", "valuable.sav")));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void NewAndDuplicatedProfilesCreateTheirOwnWorkspaceOnFirstPreparation()
    {
        var manager = new ProfileManager(new AppPaths(Path.Combine(_root, "config"), _workspaceRoot, false), _builder);
        var profile = manager.Create([]);
        profile.GameInstallPath = _gamePath;
        var first = manager.EnsureProfileFolderPath(profile);
        var duplicate = manager.Duplicate([profile], profile);
        Assert.Empty(duplicate.WorkspacePath);

        var second = manager.EnsureProfileFolderPath(duplicate);

        Assert.NotEqual(first, second);
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }

    private static string GetMissingDrive() => Enumerable.Range('D', 'Z' - 'D' + 1).Select(value => $"{(char)value}:\\")
        .First(root => !Directory.GetLogicalDrives().Contains(root, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void EnsureProfileWorkspaceMigratesLegacyCyrillicFolderToAsciiPath()
    {
        var profile = CreateProfile();
        var legacyWorkspace = Path.Combine(_workspaceRoot, $"Старое имя-{profile.Id[..8]}");
        CreateFileAtPath(Path.Combine(legacyWorkspace, "userdata", "save.dat"), "save");
        profile.Name = "Новое имя";

        var recovered = _builder.EnsureProfileWorkspace(profile, _gamePath);

        Assert.Equal(Path.Combine(_workspaceRoot, $"profile-{profile.Id}"), recovered);
        Assert.False(Directory.Exists(legacyWorkspace));
        Assert.All(recovered, character => Assert.True(character <= 0x7F));
        Assert.True(File.Exists(Path.Combine(recovered, ".stalker-launcher-workspace")));
        Assert.Equal("save", File.ReadAllText(Path.Combine(recovered, "userdata", "save.dat")));
    }

    [Fact]
    public void DeleteProfileWorkspaceRemovesAllLegacyUsvfsFoldersWithSameProfileId()
    {
        var profile = CreateProfile();
        var first = Path.Combine(_workspaceRoot, $"Профиль 1-{profile.Id[..8]}");
        var second = Path.Combine(_workspaceRoot, $"Профиль 2-{profile.Id[..8]}");
        CreateFileAtPath(Path.Combine(first, "userdata", "first.dat"), "first");
        CreateFileAtPath(Path.Combine(second, "userdata", "second.dat"), "second");
        profile.WorkspacePath = string.Empty;

        _builder.DeleteProfileWorkspace(profile, _gamePath);

        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
    }

    [Fact]
    public void DeleteProfileWorkspaceRejectsUnmanagedDirectoryEvenWithMarker()
    {
        var outside = Path.Combine(_root, "outside");
        CreateFile(outside, ".stalker-launcher-workspace", "fake marker");
        CreateFile(outside, "valuable.txt", "keep");
        var profile = new ModProfile { WorkspacePath = outside };

        Assert.Throws<InvalidOperationException>(() => _builder.DeleteProfileWorkspace(profile, _gamePath));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "valuable.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteLegacyWorkspaceOutsideCurrentRootRestoresMissingProfileMarker(bool legacyName)
    {
        var root = Path.Combine(_root, "old-storage", "Workspaces");
        var profile = CreateProfile();
        var name = legacyName ? $"old-name-{profile.Id[..8]}" : $"profile-{profile.Id}";
        profile.WorkspacePath = Path.Combine(root, name);
        CreateFile(root, WorkspaceBuilder.RootMarkerFileName, "managed root");
        CreateFileAtPath(Path.Combine(profile.WorkspacePath, "userdata", "save.dat"), "save");
        CreateFile(root, "other-profile/valuable.txt", "keep");

        _builder.DeleteProfileWorkspace(profile, _gamePath);

        Assert.False(Directory.Exists(profile.WorkspacePath));
        Assert.True(File.Exists(Path.Combine(root, WorkspaceBuilder.RootMarkerFileName)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "other-profile", "valuable.txt")));
        Assert.Equal("base", File.ReadAllText(Path.Combine(_gamePath, "gamedata", "config", "shared.ltx")));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void DeleteOutsideCurrentRootRequiresParentMarkerAndMatchingProfileId(bool parentMarker, bool matchingId)
    {
        var root = Path.Combine(_root, "outside");
        var profile = CreateProfile();
        profile.WorkspacePath = Path.Combine(root, $"profile-{(matchingId ? profile.Id : Guid.NewGuid().ToString("N"))}");
        if (parentMarker) CreateFile(root, WorkspaceBuilder.RootMarkerFileName, "managed root");
        CreateFileAtPath(Path.Combine(profile.WorkspacePath, "valuable.txt"), "keep");

        Assert.Throws<InvalidOperationException>(() => _builder.DeleteProfileWorkspace(profile, _gamePath));

        Assert.Equal("keep", File.ReadAllText(Path.Combine(profile.WorkspacePath, "valuable.txt")));
        Assert.False(File.Exists(Path.Combine(profile.WorkspacePath, ".stalker-launcher-workspace")));
    }

    private static ModProfile CreateProfile(params string[] modPaths)
    {
        var profile = new ModProfile
        {
            Name = "Test profile",
            ExecutableRelativePath = @"bin\xr_3da.exe"
        };

        for (var index = 0; index < modPaths.Length; index++)
        {
            profile.Mods.Add(new ModEntry
            {
                Name = $"Mod {index + 1}",
                SourcePath = modPaths[index],
                IsEnabled = true,
                Order = index + 1
            });
        }

        return profile;
    }

    private string CreateMod(string name, string sharedContent)
    {
        var path = Path.Combine(_root, "mods", name);
        CreateFile(path, "gamedata/config/shared.ltx", sharedContent);
        return path;
    }

    private static void CreateFile(string root, string relativePath, string content)
    {
        CreateFileAtPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)), content);
    }

    private static void CreateFileAtPath(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ProgressLog(Action<string>? onReport = null) : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value)
        {
            Messages.Add(value);
            onReport?.Invoke(value);
        }
    }
}
