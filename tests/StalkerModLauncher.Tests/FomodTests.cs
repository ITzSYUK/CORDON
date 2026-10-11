using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using StalkerModLauncher.Services;
using StalkerModLauncher.Services.Fomod;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class FomodTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherFomodTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DetectorFindsConfigAtArchiveRootCaseInsensitive()
    {
        var staging = CreateStaging(
            ("FOMOD/ModuleConfig.xml", "<config><moduleName>M</moduleName></config>"),
            ("gamedata/x.txt", "x"));

        var location = FomodDetector.TryLocate(staging);

        Assert.NotNull(location);
        Assert.Equal(staging, location.PackageRoot);
        Assert.NotNull(location.ConfigPath);
        Assert.False(location.HasCSharpScript);
    }

    [Fact]
    public void DetectorFindsConfigInsideSingleWrapper()
    {
        var staging = CreateStaging(
            ("Wrapper/fomod/moduleconfig.xml", "<config><moduleName>M</moduleName></config>"),
            ("Wrapper/gamedata/x.txt", "x"));

        var location = FomodDetector.TryLocate(staging);

        Assert.NotNull(location);
        Assert.EndsWith("Wrapper", location.PackageRoot);
    }

    [Fact]
    public void DetectorReturnsNullWithoutScript()
    {
        var staging = CreateStaging(("gamedata/x.txt", "x"));

        Assert.Null(FomodDetector.TryLocate(staging));
    }

    [Fact]
    public void DetectorReportsCSharpScriptWithoutConfig()
    {
        var staging = CreateStaging(
            ("fomod/script.cs", "code"),
            ("gamedata/x.txt", "x"));

        var location = FomodDetector.TryLocate(staging);

        Assert.NotNull(location);
        Assert.Null(location.ConfigPath);
        Assert.True(location.HasCSharpScript);
    }

    [Fact]
    public void ParserReadsStepsGroupsAndFlags()
    {
        var config = WriteConfig("module.xml", """
            <config>
              <moduleName>Test Mod</moduleName>
              <requiredInstallFiles>
                <folder source="00 Core" destination="" priority="0"/>
              </requiredInstallFiles>
              <installSteps order="Explicit">
                <installStep name="Textures">
                  <optionalFileGroups order="Explicit">
                    <group name="Resolution" type="SelectExactlyOne">
                      <plugins order="Explicit">
                        <plugin name="2K">
                          <description>Balanced</description>
                          <files><folder source="01 2K" destination=""/></files>
                          <conditionFlags><flag name="res">2k</flag></conditionFlags>
                          <typeDescriptor><type name="Recommended"/></typeDescriptor>
                        </plugin>
                        <plugin name="4K">
                          <files><folder source="02 4K" destination=""/></files>
                          <conditionFlags><flag name="res">4k</flag></conditionFlags>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
              <conditionalFileInstalls>
                <patterns>
                  <pattern>
                    <dependencies operator="And">
                      <flagDependency flag="res" value="4k"/>
                    </dependencies>
                    <files><folder source="03 4K patch" destination=""/></files>
                  </pattern>
                </patterns>
              </conditionalFileInstalls>
            </config>
            """);

        var module = FomodParser.Parse(config);

        Assert.Equal("Test Mod", module.Name);
        Assert.Single(module.RequiredFiles);
        Assert.Single(module.Steps);
        Assert.Equal(FomodGroupType.SelectExactlyOne, module.Steps[0].Groups[0].Type);
        Assert.Equal(2, module.Steps[0].Groups[0].Plugins.Count);
        Assert.Single(module.ConditionalPatterns);
    }

    [Fact]
    public void SelectorPicksRecommendedForExactlyOne()
    {
        var module = ParseInline("""
            <config>
              <moduleName>M</moduleName>
              <installSteps order="Explicit">
                <installStep name="S">
                  <optionalFileGroups order="Explicit">
                    <group name="G" type="SelectExactlyOne">
                      <plugins order="Explicit">
                        <plugin name="A">
                          <files><folder source="opt-a" destination=""/></files>
                          <conditionFlags><flag name="pick">a</flag></conditionFlags>
                          <typeDescriptor><type name="Recommended"/></typeDescriptor>
                        </plugin>
                        <plugin name="B">
                          <files><folder source="opt-b" destination=""/></files>
                          <conditionFlags><flag name="pick">b</flag></conditionFlags>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """);

        var selection = FomodSelector.CreateDefaults(module);
        var files = FomodSelector.ResolveFiles(module, selection)
            .Select(file => file.Source)
            .ToArray();

        Assert.Contains("opt-a", files);
        Assert.DoesNotContain("opt-b", files);
        Assert.Equal("a", selection.Flags["pick"]);
    }

    [Fact]
    public void ConditionalPatternFollowsFlags()
    {
        var module = ParseInline("""
            <config>
              <moduleName>M</moduleName>
              <installSteps order="Explicit">
                <installStep name="S">
                  <optionalFileGroups order="Explicit">
                    <group name="G" type="SelectExactlyOne">
                      <plugins order="Explicit">
                        <plugin name="A">
                          <files><folder source="opt-a" destination=""/></files>
                          <conditionFlags><flag name="pick">a</flag></conditionFlags>
                          <typeDescriptor><type name="Recommended"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
              <conditionalFileInstalls>
                <patterns>
                  <pattern>
                    <dependencies operator="And">
                      <flagDependency flag="pick" value="a"/>
                    </dependencies>
                    <files><folder source="patch-a" destination=""/></files>
                  </pattern>
                  <pattern>
                    <dependencies operator="And">
                      <flagDependency flag="pick" value="b"/>
                    </dependencies>
                    <files><folder source="patch-b" destination=""/></files>
                  </pattern>
                </patterns>
              </conditionalFileInstalls>
            </config>
            """);

        var files = FomodSelector.ResolveFiles(module, FomodSelector.CreateDefaults(module))
            .Select(file => file.Source)
            .ToArray();

        Assert.Contains("patch-a", files);
        Assert.DoesNotContain("patch-b", files);
    }

    [Fact]
    public void ResolveFilesSortsByPriority()
    {
        var module = ParseInline("""
            <config>
              <moduleName>M</moduleName>
              <requiredInstallFiles>
                <folder source="base" destination="" priority="5"/>
                <folder source="override" destination="" priority="10"/>
              </requiredInstallFiles>
            </config>
            """);

        var files = FomodSelector.ResolveFiles(module, FomodSelector.CreateDefaults(module))
            .Select(file => file.Source)
            .ToArray();

        Assert.Equal(2, files.Length);
        Assert.Equal("base", files[0]);
        Assert.Equal("override", files[1]);
    }

    [Theory]
    [InlineData("Ascending")]
    [InlineData("Descending")]
    [InlineData("Explicit")]
    [InlineData(null)]
    public void EqualPriorityFilesFollowXmlOrderRegardlessOfDisplayOrder(string? order)
    {
        var names = order == "Descending" ? new[] { "A", "Z" } : new[] { "Z", "A" };
        XElement Ordered(string elementName) => new(elementName,
            order is null ? null : new XAttribute("order", order));
        var steps = Ordered("installSteps");
        var index = 0;
        foreach (var stepName in names)
        {
            var groups = Ordered("optionalFileGroups");
            foreach (var groupName in names)
            {
                var plugins = Ordered("plugins");
                foreach (var pluginName in names)
                {
                    plugins.Add(new XElement("plugin", new XAttribute("name", pluginName),
                        new XElement("description", pluginName),
                        new XElement("files", new XElement("file",
                            new XAttribute("source", $"payload/{index++}.txt"),
                            new XAttribute("destination", "gamedata/chosen.txt"))),
                        new XElement("typeDescriptor", new XElement("type", new XAttribute("name", "Optional")))));
                }
                groups.Add(new XElement("group", new XAttribute("name", groupName),
                    new XAttribute("type", "SelectAll"), plugins));
            }
            steps.Add(new XElement("installStep", new XAttribute("name", stepName), groups));
        }
        var module = ParseInline(new XElement("config", new XElement("moduleName", "M"), steps).ToString());
        var expectedNames = order == "Explicit" ? names : names.Reverse().ToArray();
        Assert.Equal(expectedNames, module.Steps.Select(step => step.Name));
        Assert.All(module.Steps, step =>
        {
            Assert.Equal(expectedNames, step.Groups.Select(group => group.Name));
            Assert.All(step.Groups, group => Assert.Equal(expectedNames, group.Plugins.Select(plugin => plugin.Name)));
        });
        var files = FomodSelector.ResolveFiles(module, FomodSelector.CreateDefaults(module));
        Assert.Equal(Enumerable.Range(0, index).Select(i => $"payload/{i}.txt"), files.Select(file => file.Source));
        var staging = CreateStaging(Enumerable.Range(0, index).Select(i => ($"payload/{i}.txt", i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());
        FomodMaterializer.ApplyToStaging(staging, staging, _root, files);
        Assert.Equal("7", File.ReadAllText(Path.Combine(staging, "gamedata/chosen.txt")));
    }

    [Fact]
    public async Task InstallAsyncAppliesFomodDefaultsIntoProfileInstallPath()
    {
        var archive = CreateZip(
            "fomod-mod.zip",
            ("fomod/ModuleConfig.xml", """
                <config>
                  <moduleName>HD Textures</moduleName>
                  <requiredInstallFiles>
                    <folder source="00 Core" destination=""/>
                  </requiredInstallFiles>
                  <installSteps order="Explicit">
                    <installStep name="Pick">
                      <optionalFileGroups order="Explicit">
                        <group name="Quality" type="SelectExactlyOne">
                          <plugins order="Explicit">
                            <plugin name="2K">
                              <files><folder source="01 2K" destination=""/></files>
                              <typeDescriptor><type name="Recommended"/></typeDescriptor>
                            </plugin>
                            <plugin name="4K">
                              <files><folder source="02 4K" destination=""/></files>
                              <typeDescriptor><type name="Optional"/></typeDescriptor>
                            </plugin>
                          </plugins>
                        </group>
                      </optionalFileGroups>
                    </installStep>
                  </installSteps>
                </config>
                """),
            ("00 Core/gamedata/textures/common.dds", "core"),
            ("01 2K/gamedata/textures/wall.dds", "two-k"),
            ("02 4K/gamedata/textures/wall.dds", "four-k"));

        // Custom per-profile install path must be honored exactly like plain archives.
        var customRoot = Path.Combine(_root, "custom-mods");
        var result = await ModArchiveInstaller.InstallAsync(archive, customRoot);

        Assert.True(result.FomodApplied);
        Assert.Equal("HD Textures", result.FomodModuleName);
        Assert.StartsWith(customRoot, result.ModPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("core", File.ReadAllText(Path.Combine(result.ModPath, "gamedata", "textures", "common.dds")));
        Assert.Equal("two-k", File.ReadAllText(Path.Combine(result.ModPath, "gamedata", "textures", "wall.dds")));
        Assert.False(Directory.Exists(Path.Combine(result.ModPath, "fomod")),
            "FOMOD script folder must not be installed");
    }

    [Fact]
    public async Task InstallAsyncPlainArchiveIsNotMarkedAsFomod()
    {
        var archive = CreateZip("plain.zip", ("gamedata/x.txt", "x"));

        var result = await ModArchiveInstaller.InstallAsync(archive, InstallRoot);

        Assert.False(result.FomodApplied);
        Assert.Equal(string.Empty, result.FomodModuleName);
    }

    [Fact]
    public async Task InstallAsyncRejectsCSharpScriptWithoutConfig()
    {
        var archive = CreateZip(
            "csharp.zip",
            ("fomod/script.cs", "code"),
            ("gamedata/x.txt", "x"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ModArchiveInstaller.InstallAsync(archive, InstallRoot));

        Assert.Contains("C#", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(InstallRoot));
    }

    private string InstallRoot => Path.Combine(_root, "installed");

    [Fact]
    public void ParserReadsDefaultTypeDescendingOrderAndBooleanOne()
    {
        var module = ParseInline("""
            <config><moduleName>M</moduleName><moduleImage path="fomod/header.png"/>
            <installSteps order="Descending">
              <installStep name="A"><optionalFileGroups><group name="G" type="SelectAny"><plugins>
                <plugin name="P"><files><file source="a.txt" alwaysInstall="1"/></files>
                  <typeDescriptor><dependencyType><defaultType name="NotUsable"/><patterns>
                    <pattern><dependencies><flagDependency flag="x" value="on"/></dependencies><type name="Required"/></pattern>
                  </patterns></dependencyType></typeDescriptor>
                </plugin>
              </plugins></group></optionalFileGroups></installStep>
              <installStep name="Z"/>
            </installSteps></config>
            """);
        Assert.Equal("Z", module.Steps[0].Name);
        Assert.Equal("fomod/header.png", module.ImagePath);
        var plugin = module.Steps[1].Groups[0].Plugins[0];
        Assert.Equal(FomodPluginType.NotUsable, plugin.BaseType);
        Assert.True(plugin.Files[0].AlwaysInstall);
        Assert.Equal("a.txt", plugin.Files[0].Destination);
        Assert.Equal(FomodPluginType.Required, FomodSelector.EffectiveType(plugin, new Dictionary<string, string> { ["x"] = "on" }));
    }

    [Theory]
    [InlineData("<unknownDependency/>")]
    [InlineData("<fileDependency file='patch.txt' state='Active'/>")]
    [InlineData("<gameDependency version='1.0'/>")]
    public void UnsatisfiedModuleDependenciesNeverInstall(string dependency)
    {
        if (dependency.Contains("unknown", StringComparison.Ordinal))
        {
            Assert.Throws<InvalidDataException>(() => ParseInline($"<config><moduleName>M</moduleName><moduleDependencies>{dependency}</moduleDependencies></config>"));
            return;
        }
        var module = ParseInline($"<config><moduleName>M</moduleName><moduleDependencies>{dependency}</moduleDependencies></config>");
        Assert.Throws<InvalidDataException>(() => FomodSelector.CreateDefaults(module));
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Inactive")]
    [InlineData("Missing")]
    public void FileDependenciesUseSuppliedProfileState(string state)
    {
        Assert.True(FomodSelector.Evaluate(new FomodFileCondition("gamedata/patch.ltx", state), new Dictionary<string, string>(), file =>
        {
            Assert.Equal("gamedata/patch.ltx", file);
            return state;
        }));
    }

    [Theory]
    [InlineData("<group name='G' type='Unknown'><plugins/></group>")]
    [InlineData("<group name='G' type='SelectAny'><plugins><plugin name='P'><typeDescriptor><type name='Unknown'/></typeDescriptor></plugin></plugins></group>")]
    public void ParserRejectsUnknownSelectionRules(string group)
    {
        Assert.Throws<InvalidDataException>(() => ParseInline($"<config><moduleName>M</moduleName><installSteps><installStep name='S'><optionalFileGroups>{group}</optionalFileGroups></installStep></installSteps></config>"));
    }

    [Fact]
    public void ParserRejectsDtd()
    {
        Assert.Throws<InvalidDataException>(() => ParseInline("<!DOCTYPE config [<!ENTITY name 'M'>]><config><moduleName>&name;</moduleName></config>"));
    }

    [Fact]
    public void MissingFlagsHaveEmptyValuesAndDependencyDepthIsBounded()
    {
        Assert.True(FomodSelector.Evaluate(new FomodFlagCondition("unset", ""), new Dictionary<string, string>()));
        var nested = string.Concat(Enumerable.Repeat("<dependencies>", 70)) + "<flagDependency flag='x' value='on'/>" + string.Concat(Enumerable.Repeat("</dependencies>", 70));
        Assert.Throws<InvalidDataException>(() => ParseInline($"<config><moduleName>M</moduleName><moduleDependencies>{nested}</moduleDependencies></config>"));
    }

    [Theory]
    [InlineData("gamedata/configs/renamed.ltx", "gamedata/configs/renamed.ltx")]
    [InlineData("gamedata/configs/", "gamedata/configs/source.ltx")]
    [InlineData("", "source.ltx")]
    public void MaterializerMapsFileDestinationsAndReleasesSource(string destination, string expected)
    {
        var staging = CreateStaging(("options/source.ltx", "contents"), ("unused.txt", "unused"));
        var count = FomodMaterializer.ApplyToStaging(staging, staging, _root, [new("options/source.ltx", destination, 0)]);
        Assert.Equal(1, count);
        Assert.Equal("contents", File.ReadAllText(Path.Combine(staging, expected)));
        Assert.False(File.Exists(Path.Combine(staging, "unused.txt")));
    }

    [Fact]
    public void MissingDestinationPreservesSourcePath()
    {
        var staging = CreateStaging(("gamedata/configs/test.ltx", "file"));
        var module = ParseInline("<config><moduleName>M</moduleName><requiredInstallFiles><file source='gamedata/configs/test.ltx'/></requiredInstallFiles></config>");
        FomodMaterializer.ApplyToStaging(staging, staging, _root, FomodSelector.ResolveFiles(module, FomodSelector.CreateDefaults(module)));
        Assert.Equal("file", File.ReadAllText(Path.Combine(staging, "gamedata/configs/test.ltx")));
    }

    [Fact]
    public void FolderMappingsMergeWithFileOverridesByPriority()
    {
        var staging = CreateStaging(("base/a.ltx", "base"), ("override/a.ltx", "override"));
        var module = ParseInline("""
            <config><moduleName>M</moduleName><requiredInstallFiles>
            <file source="override/a.ltx" destination="gamedata/a.ltx" priority="10"/>
            <folder source="base" destination="gamedata" priority="0"/>
            </requiredInstallFiles></config>
            """);
        FomodMaterializer.ApplyToStaging(staging, staging, _root, FomodSelector.ResolveFiles(module, FomodSelector.CreateDefaults(module)));
        Assert.Equal("override", File.ReadAllText(Path.Combine(staging, "gamedata/a.ltx")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("//server/share/file")]
    [InlineData("file.txt:stream")]
    [InlineData("nested/.. /outside.txt")]
    public void MaterializerRejectsUnsafePathsWithoutDeletingStaging(string path)
    {
        var staging = CreateStaging(("safe.txt", "safe"));
        Assert.Throws<InvalidDataException>(() => FomodMaterializer.ApplyToStaging(staging, staging, _root, [new("safe.txt", path, 0)]));
        Assert.Throws<InvalidDataException>(() => FomodMaterializer.ApplyToStaging(staging, staging, _root, [new(path, "safe.txt", 0)]));
        Assert.Equal("safe", File.ReadAllText(Path.Combine(staging, "safe.txt")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(_root), directory => Path.GetFileName(directory).Contains("-fomod-", StringComparison.Ordinal));
    }

    [Fact]
    public void CancelledMaterializationPreservesExtractionAndCleansOutput()
    {
        var staging = CreateStaging(("safe.txt", "safe"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => FomodMaterializer.ApplyToStaging(staging, staging, _root, [new("safe.txt", "safe.txt", 0)], cancellation.Token));
        Assert.Equal("safe", File.ReadAllText(Path.Combine(staging, "safe.txt")));
        Assert.Single(Directory.EnumerateDirectories(_root));
    }

    [Fact]
    public void DetectorFindsNestedWrappersAndRejectsMultiplePackages()
    {
        var staging = CreateStaging(("Outer/Inner/fomod/ModuleConfig.xml", "<config><moduleName>M</moduleName></config>"));
        Assert.EndsWith("Inner", FomodDetector.TryLocate(staging)!.PackageRoot);
        staging = CreateStaging(("A/fomod/ModuleConfig.xml", "<config><moduleName>A</moduleName></config>"),
            ("B/fomod/ModuleConfig.xml", "<config><moduleName>B</moduleName></config>"));
        Assert.Throws<InvalidDataException>(() => FomodDetector.TryLocate(staging));
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".fomod")]
    public async Task InstallPreservesDeclaredRootAndOriginalArchive(string extension)
    {
        var archive = CreateZip("declared-root" + extension,
            ("fomod/ModuleConfig.xml", "<config><moduleName>M</moduleName><requiredInstallFiles><folder source='payload' destination='nested'/></requiredInstallFiles></config>"),
            ("payload/gamedata/a.ltx", "contents"));
        var before = await File.ReadAllBytesAsync(archive);
        var result = await ModArchiveInstaller.InstallAsync(archive, InstallRoot);
        Assert.Equal("contents", File.ReadAllText(Path.Combine(result.ModPath, "nested/gamedata/a.ltx")));
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        Assert.False(Directory.Exists(Path.Combine(result.ModPath, "fomod")));
    }

    [Fact]
    public async Task EmptySelectionDoesNotLeaveInstalledMod()
    {
        var archive = CreateZip("empty-choice.zip", ("fomod/ModuleConfig.xml", "<config><moduleName>M</moduleName></config>"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModArchiveInstaller.InstallAsync(archive, InstallRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(InstallRoot));
    }

    private FomodModule ParseInline(string xml) => FomodParser.Parse(WriteConfig(Guid.NewGuid().ToString("N") + ".xml", xml));

    private string WriteConfig(string name, string xml)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, xml, Encoding.UTF8);
        return path;
    }

    private string CreateStaging(params (string Path, string Content)[] entries)
    {
        var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        foreach (var item in entries)
        {
            var path = Path.Combine(staging, item.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, item.Content, Encoding.UTF8);
        }

        return staging;
    }

    private string CreateZip(string name, params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, name);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

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
}
