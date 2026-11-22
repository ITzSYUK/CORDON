using System.Security.Cryptography;
using System.Text;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class UsvfsBootstrapFileStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("CordonBootstrapFiles-").FullName;

    [Fact]
    public void CapturesNewFilesBeforeClearAndKeepsBothVersionsOfConflicts()
    {
        var bootstrap = Path.Combine(_root, ".usvfs-bootstrap");
        var overwrite = Path.Combine(_root, ProfileWritableGameFiles.DefaultOverwriteRootRelativePath);
        var working = Path.Combine(bootstrap, "working-tree");
        Directory.CreateDirectory(working);
        Directory.CreateDirectory(overwrite);
        File.WriteAllText(Path.Combine(bootstrap, "game.exe"), "prepared image");
        UsvfsBootstrapFileStore.Record(bootstrap, hasSeparateWorkingTree: true);
        File.WriteAllText(Path.Combine(working, "notes.txt"), "from working tree");
        File.WriteAllText(Path.Combine(bootstrap, "conflict.txt"), "from bootstrap");
        File.WriteAllText(Path.Combine(overwrite, "conflict.txt"), "from overwrite");

        UsvfsBootstrapFileStore.PreserveBeforeClear(bootstrap, _root, default);
        UsvfsBootstrapFileStore.Capture(bootstrap, _root);
        UsvfsExecutableBootstrapper.Clear(_root, bootstrap);

        Assert.Equal("from working tree", File.ReadAllText(Path.Combine(overwrite, "notes.txt")));
        Assert.False(File.Exists(Path.Combine(overwrite, "game.exe")));
        Assert.Equal("from overwrite", File.ReadAllText(Path.Combine(overwrite, "conflict.txt")));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_root, "diagnostics"), "conflict.txt.conflict-*.bak"));
        Assert.Equal("from bootstrap", File.ReadAllText(backup));
    }

    [Fact]
    public void FailedCaptureLeavesBootstrapAvailableForRetry()
    {
        var bootstrap = Path.Combine(_root, ".usvfs-bootstrap");
        var overwrite = Path.Combine(_root, ProfileWritableGameFiles.DefaultOverwriteRootRelativePath);
        Directory.CreateDirectory(bootstrap);
        UsvfsBootstrapFileStore.Record(bootstrap, hasSeparateWorkingTree: false);
        var source = Path.Combine(bootstrap, "notes.txt");
        File.WriteAllText(source, "keep this");
        Directory.CreateDirectory(Path.Combine(overwrite, "notes.txt"));

        Assert.NotNull(Record.Exception(() => UsvfsBootstrapFileStore.PreserveBeforeClear(bootstrap, _root, default)));
        Assert.Equal("keep this", File.ReadAllText(source));
    }

    [Fact]
    public void LaunchCleanupArchivesLegacyBootstrapWithoutAnInventory()
    {
        const string profileId = "legacy-profile";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId)))[..16].ToLowerInvariant();
        var legacy = Path.Combine(_root, ".usvfs-bootstrap", key);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "notes.txt"), "older session");
        var workspace = Path.Combine(_root, "profile-legacy");
        Directory.CreateDirectory(workspace);

        UsvfsBootstrapPathResolver.DeleteLegacySharedProfile(workspace, string.Empty, profileId, preserveFiles: true);

        Assert.False(Directory.Exists(legacy));
        var recovery = Assert.Single(Directory.GetFiles(Path.Combine(workspace, "diagnostics"), "notes.txt", SearchOption.AllDirectories));
        Assert.Equal("older session", File.ReadAllText(recovery));
        Assert.False(Directory.Exists(Path.Combine(workspace, ProfileWritableGameFiles.DefaultOverwriteRootRelativePath)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
