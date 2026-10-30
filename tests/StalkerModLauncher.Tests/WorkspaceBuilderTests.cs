using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class WorkspaceBuilderTests : IDisposable
{
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
