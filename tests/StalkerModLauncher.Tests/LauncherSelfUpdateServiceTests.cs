using System.IO.Compression;
using System.Security.Cryptography;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class LauncherSelfUpdateServiceTests : IDisposable
{
    [Fact]
    public void DownloadUsesTheSingleFullReleaseArchive()
    {
        var uri = LauncherReleaseDownloadService.BuildDownloadUri(
            "https://github.com/ITzSYUK/CORDON/releases/tag/v1.5.5", "v1.5.5");

        Assert.Equal("https://github.com/ITzSYUK/CORDON/releases/download/v1.5.5/CORDON-v1.5.5-win-x64.zip", uri.AbsoluteUri);
        Assert.Throws<InvalidDataException>(() => LauncherReleaseDownloadService.BuildDownloadUri(
            "https://example.com/releases/tag/v1.5.5", "v1.5.5"));
    }

    [Fact]
    public void UpdateTemporaryFilesStayInLauncherData()
    {
        Assert.Equal(Path.Combine(_root, "Data", "CordonModLauncher", "Temp"),
            LauncherSelfUpdateService.GetUpdateTempDirectory(_root));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"CORDON-UpdateTests-{Guid.NewGuid():N}");

    [Fact]
    public void ExtractAndValidateAcceptsReleaseAndChecksEveryFile()
    {
        var archivePath = CreateReleaseArchive();
        var stagingDirectory = Path.Combine(_root, "staging");

        LauncherSelfUpdateService.ExtractAndValidate(
            archivePath,
            stagingDirectory);

        Assert.True(File.Exists(Path.Combine(stagingDirectory, "CORDON.exe")));
        Assert.False(Directory.Exists(Path.Combine(stagingDirectory, "Data")));
    }

    [Fact]
    public void ExtractAndValidateRejectsDataDirectory()
    {
        var archivePath = CreateReleaseArchive(archive =>
        {
            var entry = archive.CreateEntry("Data/settings.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("do not replace");
        });

        var error = Assert.Throws<InvalidDataException>(() =>
            LauncherSelfUpdateService.ExtractAndValidate(
                archivePath,
                Path.Combine(_root, "staging-data")));

        Assert.Contains("Data", error.Message);
    }

    [Theory]
    [InlineData("CORDON.exe")]
    [InlineData("CORDON-Standalone.exe")]
    public void ApplyStagedFilesPreservesDataAndRemovesOldPackageExecutable(string previousExecutable)
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(_root, "apply-staging")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "Data"));
        File.WriteAllText(Path.Combine(target, "Data", "settings.json"), "keep");
        File.WriteAllText(Path.Combine(target, previousExecutable), "old executable");
        File.WriteAllText(Path.Combine(target, "user-file.txt"), "keep too");
        File.WriteAllText(Path.Combine(staging, "CORDON.exe"), "new executable");
        File.WriteAllText(Path.Combine(staging, "usvfs_x64.dll"), "new runtime");

        LauncherSelfUpdateService.ApplyStagedFiles(
            staging,
            target);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "Data", "settings.json")));
        Assert.Equal("keep too", File.ReadAllText(Path.Combine(target, "user-file.txt")));
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(target, "CORDON.exe")));
        Assert.False(File.Exists(Path.Combine(target, "CORDON-Standalone.exe")));
    }

    [Fact]
    public void VerifyArchiveChecksumRejectsChangedArchive()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "release.zip");
        File.WriteAllText(archivePath, "download");
        var checksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)));
        File.AppendAllText(archivePath, " changed");

        Assert.Throws<InvalidDataException>(() =>
            LauncherReleaseDownloadService.VerifyArchiveChecksum(
                checksum,
                archivePath));
    }

    [Fact]
    public void GetReleaseAssetSha256UsesGitHubAssetDigest()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"tag_name":"v1.4.5","assets":[{"name":"CORDON-v1.4.5-win-x64.zip","digest":"sha256:333F37A1E87F56883E045CD06163EA294FB640936293A7E305220C4506FC81A0"}]}""");

        var hash = LauncherReleaseDownloadService.GetReleaseAssetSha256(
            document.RootElement,
            "v1.4.5",
            "CORDON-v1.4.5-win-x64.zip");

        Assert.Equal("333F37A1E87F56883E045CD06163EA294FB640936293A7E305220C4506FC81A0", hash);
    }

    [Fact]
    public void TryParseRequestRejectsInvalidPathsWithoutThrowing()
    {
        Assert.False(LauncherSelfUpdateService.TryParseRequest(
            ["--apply-launcher-update", "1", "invalid\0path", _root, ReadyEventName()],
            out _));
    }

    [Fact]
    public void TryParseRequestAcceptsOneTimeReadyEvent()
    {
        var eventName = ReadyEventName();

        Assert.True(LauncherSelfUpdateService.TryParseRequest(
            ["--apply-launcher-update", "1", _root, _root, eventName],
            out var request));

        Assert.Equal(eventName, request!.ReadyEventName);
    }

    [Fact]
    public async Task WaitForParentExitAsyncRejectsAlreadyExitedParent()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LauncherSelfUpdateService.WaitForParentExitAsync(int.MaxValue, _root, ReadyEventName()));
    }

    [Fact]
    public void CalculateRequiredFreeSpaceCombinesRequirementsOnOneVolume()
    {
        var required = LauncherSelfUpdateService.CalculateRequiredFreeSpace(
            _root,
            Path.Combine(_root, "staging"),
            stagingBytes: 100,
            backupBytes: 200,
            largestReplacementBytes: 300);
        var root = Path.GetPathRoot(Path.GetFullPath(_root))!;

        Assert.Equal(64L * 1024 * 1024 + 600, required[root]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateReleaseArchive(
        Action<ZipArchive>? addEntries = null)
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        var files = new Dictionary<string, byte[]>
        {
            [LauncherSelfUpdateService.ExecutableFileName] = "launcher"u8.ToArray(),
            ["StalkerModLauncher.UsvfsX86Host.exe"] = "host"u8.ToArray(),
            ["usvfs_x64.dll"] = "x64 dll"u8.ToArray(),
            ["usvfs_proxy_x64.exe"] = "x64 proxy"u8.ToArray(),
            ["usvfs_x86.dll"] = "x86 dll"u8.ToArray(),
            ["usvfs_proxy_x86.exe"] = "x86 proxy"u8.ToArray(),
            ["LICENSE.txt"] = "license"u8.ToArray(),
            ["THIRD-PARTY-NOTICES.txt"] = "notices"u8.ToArray()
        };

        var checksums = string.Join(
            Environment.NewLine,
            files.Select(file => $"{Convert.ToHexString(SHA256.HashData(file.Value))}  {file.Key}"));

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var file in files.Append(new KeyValuePair<string, byte[]>("checksums.txt", System.Text.Encoding.ASCII.GetBytes(checksums))))
        {
            var entry = archive.CreateEntry(file.Key);
            using var stream = entry.Open();
            stream.Write(file.Value);
        }

        addEntries?.Invoke(archive);
        return archivePath;
    }

    private static string ReadyEventName() => $"Local\\CORDON-Update-Ready-{Guid.NewGuid():N}";
}
