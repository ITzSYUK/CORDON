using System.Text;
using StalkerModLauncher.Services;
using StalkerModLauncher.Services.Fomod;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class FomodFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FomodFolderTests", Guid.NewGuid().ToString("N"));
    private string InstallRoot => Path.Combine(_root, "installed");
    private const string Config = """
        <config><moduleName>Folder mod</moduleName>
          <requiredInstallFiles><folder source="core" destination="gamedata"/></requiredInstallFiles>
          <installSteps order="Explicit"><installStep name="Options"><optionalFileGroups order="Explicit">
            <group name="Quality" type="SelectExactlyOne"><plugins order="Explicit">
              <plugin name="A"><files><file source="a.txt" destination="gamedata/chosen.txt"/></files><typeDescriptor><type name="Recommended"/></typeDescriptor></plugin>
              <plugin name="B"><files><file source="b.txt" destination="gamedata/chosen.txt"/></files><typeDescriptor><type name="Optional"/></typeDescriptor></plugin>
            </plugins></group>
          </optionalFileGroups></installStep></installSteps>
        </config>
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FolderInstallCopiesOnlyChosenFilesAndLeavesOriginalUntouched(bool wrapped)
    {
        var source = CreatePackage(wrapped ? "wrapper/package" : "package");
        var selectedFolder = wrapped ? Path.GetDirectoryName(source)! : source;
        var before = Snapshot(source);
        var progress = new List<ModArchiveInstallProgress>();
        var result = await ModArchiveInstaller.InstallFomodFolderAsync(selectedFolder, InstallRoot,
            new InlineProgress(progress.Add), request =>
            {
                Assert.Equal(Path.GetFullPath(source), request.PackageRoot);
                using var wizard = new FomodInstallerViewModel(request.Module, request.PackageRoot);
                wizard.CurrentStep!.Groups[0].Options[1].IsSelected = true;
                return wizard.BuildSelection();
            });

        Assert.True(result.FomodApplied);
        Assert.Equal("Folder mod", result.FomodModuleName);
        Assert.Equal(2, result.FileCount);
        Assert.True(result.ExtractedBytes > 0);
        Assert.Equal("B", File.ReadAllText(Path.Combine(result.ModPath, "gamedata/chosen.txt")));
        Assert.Equal("core", File.ReadAllText(Path.Combine(result.ModPath, "gamedata/core.txt")));
        Assert.False(File.Exists(Path.Combine(result.ModPath, "unused.txt")));
        Assert.False(Directory.Exists(Path.Combine(result.ModPath, "fomod")));
        Assert.Equal(before, Snapshot(source));
        Assert.Contains(progress, item => item.Stage == ModArchiveInstallStage.Copying);
        Assert.DoesNotContain(progress, item => item.Stage == ModArchiveInstallStage.Extracting);
        Assert.Equal(ModArchiveInstallStage.Finalizing, progress[^1].Stage);
    }

    [Fact]
    public async Task CancelledFolderChooserDoesNotCreateModOrChangeSource()
    {
        var source = CreatePackage("package");
        var before = Snapshot(source);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Install(source, _ => null));
        Assert.Equal(before, Snapshot(source));
        Assert.False(Directory.Exists(InstallRoot));
    }

    [Fact]
    public async Task RepeatedFolderInstallUsesNewDestinationAndPreservesPreviousInstall()
    {
        var source = CreatePackage("package");
        var first = await Install(source);
        File.WriteAllText(Path.Combine(first.ModPath, "keep.txt"), "existing");
        var planned = ModArchiveInstaller.PlanFomodFolderInstall(source, InstallRoot);
        Assert.True(planned.RequiresConfirmation);
        Assert.Equal("package(1)", planned.PackageDirectoryName);
        var second = await ModArchiveInstaller.InstallFomodFolderAsync(source, InstallRoot,
            new InlineProgress(_ => { }), packageDirectoryName: planned.PackageDirectoryName);
        Assert.NotEqual(first.ModPath, second.ModPath);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(first.ModPath, "keep.txt")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(second.ModPath, "gamedata/chosen.txt")));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("output")]
    public void DestinationInsideSourceIsRejectedBeforeWriting(string destination)
    {
        var source = CreatePackage("package");
        var before = Snapshot(source);
        Assert.Throws<InvalidOperationException>(() => ModArchiveInstaller.PlanFomodFolderInstall(source, Path.Combine(source, destination)));
        Assert.Equal(before, Snapshot(source));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:/outside.txt")]
    public async Task UnsafeDestinationCleansOnlyOwnStaging(string destination)
    {
        var source = CreatePackage("package");
        File.WriteAllText(Path.Combine(source, "fomod/ModuleConfig.xml"),
            $"<config><moduleName>M</moduleName><requiredInstallFiles><file source='a.txt' destination='{destination}'/></requiredInstallFiles></config>");
        var before = Snapshot(source);
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllText(Path.Combine(InstallRoot, "keep.txt"), "keep");
        await Assert.ThrowsAsync<InvalidDataException>(() => Install(source));
        Assert.Equal(before, Snapshot(source));
        Assert.Empty(Directory.EnumerateDirectories(InstallRoot));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(InstallRoot, "keep.txt")));
    }

    [Fact]
    public async Task MissingPayloadLeavesOriginalAndExistingModsUntouched()
    {
        var source = CreatePackage("package");
        File.Delete(Path.Combine(source, "a.txt"));
        var before = Snapshot(source);
        await Assert.ThrowsAsync<InvalidDataException>(() => Install(source));
        Assert.Equal(before, Snapshot(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(InstallRoot));
    }

    [Fact]
    public async Task CancellationDuringCopyingRemovesStaging()
    {
        var source = CreatePackage("package");
        var before = Snapshot(source);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(item =>
        {
            if (item.Stage == ModArchiveInstallStage.Copying) cancellation.Cancel();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModArchiveInstaller.InstallFomodFolderAsync(
            source, InstallRoot, progress, cancellationToken: cancellation.Token));
        Assert.Equal(before, Snapshot(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(InstallRoot));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("csharp")]
    [InlineData("invalid")]
    public async Task NonXmlInstallersFailWithoutChangingSource(string kind)
    {
        var source = CreatePackage("package");
        File.Delete(Path.Combine(source, "fomod/ModuleConfig.xml"));
        if (kind == "csharp") File.WriteAllText(Path.Combine(source, "fomod/script.cs"), "script");
        if (kind == "invalid") File.WriteAllText(Path.Combine(source, "fomod/ModuleConfig.xml"), "not XML");
        var before = Snapshot(source);
        await Assert.ThrowsAsync<InvalidDataException>(() => Install(source));
        Assert.Equal(before, Snapshot(source));
        Assert.False(Directory.Exists(InstallRoot));
    }

    [Fact]
    public void CopyToStagingRefusesExistingDirectoryAndSourceOverlap()
    {
        var source = CreatePackage("package");
        var target = Directory.CreateDirectory(Path.Combine(InstallRoot, "existing")).FullName;
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        Assert.Throws<IOException>(() => FomodMaterializer.CopyToStaging(source, target, InstallRoot, [new("a.txt", "new.txt", 0)]));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.Throws<InvalidDataException>(() => FomodMaterializer.CopyToStaging(source, source, _root, [new("a.txt", "new.txt", 0)]));
        Assert.False(File.Exists(Path.Combine(source, "new.txt")));
    }

    private Task<ModArchiveInstallResult> Install(string source, Func<FomodChoiceRequest, FomodSelector.Selection?>? chooser = null) =>
        ModArchiveInstaller.InstallFomodFolderAsync(source, InstallRoot, new InlineProgress(_ => { }), chooser);

    [Fact]
    public async Task ReadOnlySourcesRemainReadOnlyWhileInstalledCopiesCanBeOverwritten()
    {
        var source = CreatePackage("package");
        File.WriteAllText(Path.Combine(source, "fomod/ModuleConfig.xml"), """
            <config><moduleName>M</moduleName><requiredInstallFiles>
            <file source="a.txt" destination="gamedata/chosen.txt" priority="0"/>
            <file source="b.txt" destination="gamedata/chosen.txt" priority="1"/>
            </requiredInstallFiles></config>
            """);
        var originals = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
        foreach (var file in originals) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        try
        {
            var before = Snapshot(source);
            var result = await Install(source);
            Assert.Equal("B", File.ReadAllText(Path.Combine(result.ModPath, "gamedata/chosen.txt")));
            Assert.Equal(0, (int)(File.GetAttributes(Path.Combine(result.ModPath, "gamedata/chosen.txt")) & FileAttributes.ReadOnly));
            Assert.Equal(before, Snapshot(source));
            Assert.All(originals, file => Assert.NotEqual(0, (int)(File.GetAttributes(file) & FileAttributes.ReadOnly)));
        }
        finally
        {
            foreach (var file in originals) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }
    }

    private string CreatePackage(string name)
    {
        var source = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(source, "fomod"));
        Directory.CreateDirectory(Path.Combine(source, "core"));
        File.WriteAllText(Path.Combine(source, "fomod/ModuleConfig.xml"), Config, Encoding.UTF8);
        File.WriteAllText(Path.Combine(source, "core/core.txt"), "core");
        File.WriteAllText(Path.Combine(source, "a.txt"), "A");
        File.WriteAllText(Path.Combine(source, "b.txt"), "B");
        File.WriteAllText(Path.Combine(source, "unused.txt"), "unused");
        return source;
    }

    private static string[] Snapshot(string source) => Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(source, path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class InlineProgress(Action<ModArchiveInstallProgress> report) : IProgress<ModArchiveInstallProgress>
    {
        public void Report(ModArchiveInstallProgress value) => report(value);
    }
}
