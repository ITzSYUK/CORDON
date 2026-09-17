using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ModArchiveInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherArchiveInstallerTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAsyncFindsNestedXRayRootAndPreservesArchiveFiles()
    {
        var archive = CreateZip(
            "nested.zip",
            ("Wrapper/readme.txt", "notes"),
            ("Wrapper/gamedata/configs/test.ltx", "value"));

        var result = await ModArchiveInstaller.InstallAsync(archive, InstallRoot);

        Assert.Equal("nested", result.ModName);
        Assert.EndsWith(Path.Combine("nested", "Wrapper"), result.ModPath);
        Assert.Equal("value", File.ReadAllText(Path.Combine(result.ModPath, "gamedata", "configs", "test.ltx")));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(InstallRoot, "nested", "Wrapper", "readme.txt")));
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public async Task InstallAsyncRelocatesLooseDatabaseArchivesLikeMo2AnomalyChecker()
    {
        var archive = CreateZip("database.zip", ("addon.db0", "database"));

        var result = await ModArchiveInstaller.InstallAsync(archive, InstallRoot);

        Assert.True(result.DatabaseArchivesRelocated);
        Assert.False(File.Exists(Path.Combine(result.ModPath, "addon.db0")));
        Assert.Equal("database", File.ReadAllText(Path.Combine(result.ModPath, "db", "mods", "addon.db0")));
    }

    [Fact]
    public async Task InstallAsyncRecognizesBinVariantDirectoryAsXRayContentRoot()
    {
        var archive = CreateZip("ogsr.zip", ("bin_OGSR/xrEngine.exe", "engine"));

        var result = await ModArchiveInstaller.InstallAsync(archive, InstallRoot);

        Assert.Equal(Path.Combine(InstallRoot, "ogsr"), result.ModPath);
        Assert.True(File.Exists(Path.Combine(result.ModPath, "bin_OGSR", "xrEngine.exe")));
    }

    [Fact]
    public async Task InstallAsyncRejectsTraversalAndRemovesStagingDirectory()
    {
        var archive = CreateZip(
            "unsafe.zip",
            ("gamedata/configs/valid.ltx", "value"),
            ("../outside.txt", "escape"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ModArchiveInstaller.InstallAsync(archive, InstallRoot));

        Assert.Contains("Небезопасный путь элемента архива", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(InstallRoot));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public async Task InstallAsyncCreatesUniqueFolderForRepeatedInstall()
    {
        var archive = CreateZip("repeat.zip", ("gamedata/test.txt", "value"));
        var first = await ModArchiveInstaller.InstallAsync(archive, InstallRoot);
        var second = await ModArchiveInstaller.InstallAsync(archive, InstallRoot);

        Assert.Equal(Path.Combine(InstallRoot, "repeat"), first.ModPath);
        Assert.Equal(Path.Combine(InstallRoot, "repeat(1)"), second.ModPath);
    }

    [Fact]
    public async Task PlanInstallOffersNumberedFolderWhenArchiveNameAlreadyExists()
    {
        var archive = CreateZip("Weapons_addon.zip", ("gamedata/test.txt", "value"));
        await ModArchiveInstaller.InstallAsync(archive, InstallRoot);

        var destination = ModArchiveInstaller.PlanInstall(archive, InstallRoot);

        Assert.True(destination.RequiresConfirmation);
        Assert.Equal("Weapons_addon", destination.ModName);
        Assert.Equal("Weapons_addon(1)", destination.PackageDirectoryName);
        Assert.Equal(Path.Combine(InstallRoot, "Weapons_addon(1)"), destination.PackagePath);

        var installed = await ModArchiveInstaller.InstallAsync(
            archive,
            InstallRoot,
            destination.PackageDirectoryName,
            new InlineProgress<ModArchiveInstallProgress>(_ => { }));

        Assert.Equal(destination.PackagePath, installed.ModPath);
    }

    [Fact]
    public async Task InstallAsyncExtractsSevenZipArchive()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "seven.7z");
        using (var archiveStream = File.Create(archivePath))
        using (var writer = WriterFactory.OpenWriter(
                   archiveStream,
                   ArchiveType.SevenZip,
                   new SevenZipWriterOptions(CompressionType.LZMA2)))
        {
            using var emptyContent = new MemoryStream();
            writer.Write("gamedata/configs/empty.ltx", emptyContent, DateTime.UtcNow);
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("value"));
            writer.Write("gamedata/configs/test.ltx", content, DateTime.UtcNow);
        }

        var reports = new List<ModArchiveInstallProgress>();
        var result = await ModArchiveInstaller.InstallAsync(
            archivePath,
            InstallRoot,
            new InlineProgress<ModArchiveInstallProgress>(reports.Add));

        Assert.Equal("value", File.ReadAllText(Path.Combine(result.ModPath, "gamedata", "configs", "test.ltx")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(result.ModPath, "gamedata", "configs", "empty.ltx")));
        Assert.Contains(reports, report =>
            report.Stage == ModArchiveInstallStage.Extracting &&
            report.TotalBytes == result.ExtractedBytes);
    }

    [Fact]
    public async Task InstallAsyncReportsByteProgressAndFinalizingStage()
    {
        var archive = CreateZip(
            "progress.zip",
            ("gamedata/configs/large.ltx", new string('x', 2 * 1024 * 1024)));
        var reports = new List<ModArchiveInstallProgress>();
        var progress = new InlineProgress<ModArchiveInstallProgress>(reports.Add);

        var result = await ModArchiveInstaller.InstallAsync(archive, InstallRoot, progress);

        Assert.Equal(ModArchiveInstallStage.Inspecting, reports[0].Stage);
        Assert.Contains(reports, report =>
            report.Stage == ModArchiveInstallStage.Extracting &&
            report.TotalBytes == result.ExtractedBytes &&
            report.ExtractedBytes == result.ExtractedBytes);
        Assert.Equal(ModArchiveInstallStage.Finalizing, reports[^1].Stage);
        Assert.Equal(result.ExtractedBytes, reports[^1].ExtractedBytes);
    }

    private string InstallRoot => Path.Combine(_root, "installed");

    private string CreateZip(string name, params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, name);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(item.Content);
        }

        return archivePath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
