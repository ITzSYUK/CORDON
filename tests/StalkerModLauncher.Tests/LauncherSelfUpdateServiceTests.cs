using System.IO.Compression;
using System.Security.Cryptography;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class LauncherSelfUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"CORDON-UpdateTests-{Guid.NewGuid():N}");

    [Fact]
    public void ExtractAndValidateAcceptsReleaseAndChecksEveryFile()
    {
        var archivePath = CreateReleaseArchive(LauncherReleasePackage.Standalone);
        var stagingDirectory = Path.Combine(_root, "staging");

        LauncherSelfUpdateService.ExtractAndValidate(
            archivePath,
            stagingDirectory,
            LauncherReleasePackage.Standalone);

        Assert.True(File.Exists(Path.Combine(stagingDirectory, "CORDON-Standalone.exe")));
        Assert.False(Directory.Exists(Path.Combine(stagingDirectory, "Data")));
    }

    [Fact]
    public void ExtractAndValidateRejectsDataDirectory()
    {
        var archivePath = CreateReleaseArchive(LauncherReleasePackage.Minimal, archive =>
        {
            var entry = archive.CreateEntry("Data/settings.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("do not replace");
        });

        var error = Assert.Throws<InvalidDataException>(() =>
            LauncherSelfUpdateService.ExtractAndValidate(
                archivePath,
                Path.Combine(_root, "staging-data"),
                LauncherReleasePackage.Minimal));

        Assert.Contains("Data", error.Message);
    }

    [Fact]
    public void ApplyStagedFilesPreservesDataAndRemovesOldPackageExecutable()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(_root, "apply-staging")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "Data"));
        File.WriteAllText(Path.Combine(target, "Data", "settings.json"), "keep");
        File.WriteAllText(Path.Combine(target, "CORDON.exe"), "old executable");
        File.WriteAllText(Path.Combine(target, "user-file.txt"), "keep too");
        File.WriteAllText(Path.Combine(staging, "CORDON-Standalone.exe"), "new executable");
        File.WriteAllText(Path.Combine(staging, "usvfs_x64.dll"), "new runtime");

        LauncherSelfUpdateService.ApplyStagedFiles(
            staging,
            target,
            LauncherReleasePackage.Standalone);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "Data", "settings.json")));
        Assert.Equal("keep too", File.ReadAllText(Path.Combine(target, "user-file.txt")));
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(target, "CORDON-Standalone.exe")));
        Assert.False(File.Exists(Path.Combine(target, "CORDON.exe")));
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
            """{"tag_name":"v1.4.5","assets":[{"name":"CORDON-v1.4.5-win-x64-standalone.zip","digest":"sha256:333F37A1E87F56883E045CD06163EA294FB640936293A7E305220C4506FC81A0"}]}""");

        var hash = LauncherReleaseDownloadService.GetReleaseAssetSha256(
            document.RootElement,
            "v1.4.5",
            "CORDON-v1.4.5-win-x64-standalone.zip");

        Assert.Equal("333F37A1E87F56883E045CD06163EA294FB640936293A7E305220C4506FC81A0", hash);
    }

    [Fact]
    public void TryParseRequestRejectsInvalidPathsWithoutThrowing()
    {
        Assert.False(LauncherSelfUpdateService.TryParseRequest(
            ["--apply-launcher-update", "1", "invalid\0path", _root, "0"],
            out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateReleaseArchive(
        LauncherReleasePackage package,
        Action<ZipArchive>? addEntries = null)
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        var files = new Dictionary<string, byte[]>
        {
            [LauncherSelfUpdateService.GetExecutableName(package)] = "launcher"u8.ToArray(),
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
}
