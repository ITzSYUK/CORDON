using System.IO.Compression;
using System.Text;
using StalkerModLauncher.Services;
using StalkerModLauncher.Services.Fomod;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class FomodWizardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "StalkerModLauncherFomodWizardTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultsPreselectRecommendedOption()
    {
        var viewModel = CreateViewModel("""
            <config>
              <moduleName>M</moduleName>
              <installSteps order="Explicit">
                <installStep name="S">
                  <optionalFileGroups order="Explicit">
                    <group name="G" type="SelectExactlyOne">
                      <plugins order="Explicit">
                        <plugin name="A">
                          <files><folder source="opt-a" destination=""/></files>
                          <typeDescriptor><type name="Recommended"/></typeDescriptor>
                        </plugin>
                        <plugin name="B">
                          <files><folder source="opt-b" destination=""/></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """);

        var options = viewModel.VisibleSteps[0].Groups[0].Options;

        Assert.True(options[0].IsSelected);
        Assert.False(options[1].IsSelected);
        Assert.EndsWith("1", viewModel.StepCounterText, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleChoiceSelectionIsExclusive()
    {
        var viewModel = CreateViewModel("""
            <config>
              <moduleName>M</moduleName>
              <installSteps order="Explicit">
                <installStep name="S">
                  <optionalFileGroups order="Explicit">
                    <group name="G" type="SelectExactlyOne">
                      <plugins order="Explicit">
                        <plugin name="A">
                          <files><folder source="opt-a" destination=""/></files>
                          <typeDescriptor><type name="Recommended"/></typeDescriptor>
                        </plugin>
                        <plugin name="B">
                          <files><folder source="opt-b" destination=""/></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """);

        var options = viewModel.VisibleSteps[0].Groups[0].Options;
        options[1].IsSelected = true;

        Assert.False(options[0].IsSelected);
        Assert.True(options[1].IsSelected);
    }

    [Fact]
    public void MultiChoiceSelectionAllowsSeveralOptions()
    {
        var viewModel = CreateViewModel("""
            <config>
              <moduleName>M</moduleName>
              <installSteps order="Explicit">
                <installStep name="S">
                  <optionalFileGroups order="Explicit">
                    <group name="G" type="SelectAny">
                      <plugins order="Explicit">
                        <plugin name="A">
                          <files><folder source="opt-a" destination=""/></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                        <plugin name="B">
                          <files><folder source="opt-b" destination=""/></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """);

        var options = viewModel.VisibleSteps[0].Groups[0].Options;
        options[0].IsSelected = true;
        options[1].IsSelected = true;

        Assert.True(options[0].IsSelected);
        Assert.True(options[1].IsSelected);
    }

    [Fact]
    public void LaterStepVisibilityFollowsEarlierFlags()
    {
        var viewModel = CreateViewModel("""
            <config>
              <moduleName>M</moduleName>
              <installSteps order="Explicit">
                <installStep name="First">
                  <optionalFileGroups order="Explicit">
                    <group name="G" type="SelectExactlyOne">
                      <plugins order="Explicit">
                        <plugin name="Plain">
                          <files><folder source="opt-plain" destination=""/></files>
                          <conditionFlags><flag name="mode">plain</flag></conditionFlags>
                          <typeDescriptor><type name="Recommended"/></typeDescriptor>
                        </plugin>
                        <plugin name="Extra">
                          <files><folder source="opt-extra" destination=""/></files>
                          <conditionFlags><flag name="mode">extra</flag></conditionFlags>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
                <installStep name="Second">
                  <visible>
                    <flagDependency flag="mode" value="extra"/>
                  </visible>
                  <optionalFileGroups order="Explicit">
                    <group name="G2" type="SelectAny">
                      <plugins order="Explicit">
                        <plugin name="Bonus">
                          <files><folder source="opt-bonus" destination=""/></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """);

        Assert.Single(viewModel.VisibleSteps);

        viewModel.VisibleSteps[0].Groups[0].Options[1].IsSelected = true;

        Assert.Equal(2, viewModel.VisibleSteps.Count);
        Assert.Equal("Second", viewModel.VisibleSteps[1].Name);
    }

    [Fact]
    public void AtLeastOneBlocksFinishWhenNothingSelected()
    {
        var viewModel = CreateViewModel("""
            <config>
              <moduleName>M</moduleName>
              <installSteps order="Explicit">
                <installStep name="S">
                  <optionalFileGroups order="Explicit">
                    <group name="Quality" type="SelectAtLeastOne">
                      <plugins order="Explicit">
                        <plugin name="A">
                          <files><folder source="opt-a" destination=""/></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """);

        // Defaults pre-select the single option to satisfy the constraint.
        Assert.True(viewModel.CanFinish());

        viewModel.VisibleSteps[0].Groups[0].Options[0].IsSelected = false;

        Assert.False(viewModel.CanFinish());
        Assert.Contains("Quality", viewModel.ValidationError, StringComparison.Ordinal);

        viewModel.VisibleSteps[0].Groups[0].Options[0].IsSelected = true;

        Assert.True(viewModel.CanFinish());
    }

    [Fact]
    public void BuildSelectionResolvesOnlyChosenFiles()
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
                          <typeDescriptor><type name="Recommended"/></typeDescriptor>
                        </plugin>
                        <plugin name="B">
                          <files><folder source="opt-b" destination=""/></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """, out var packageRoot);
        var viewModel = new FomodInstallerViewModel(module, packageRoot);
        viewModel.VisibleSteps[0].Groups[0].Options[1].IsSelected = true;

        var files = FomodSelector.ResolveFiles(module, viewModel.BuildSelection())
            .Select(file => file.Source)
            .ToArray();

        Assert.Contains("opt-b", files);
        Assert.DoesNotContain("opt-a", files);
    }

    [Fact]
    public void RequiresInteractionSkipsRequiredOnlyModules()
    {
        var requiredOnly = ParseInline("""
            <config>
              <moduleName>M</moduleName>
              <requiredInstallFiles>
                <folder source="core" destination=""/>
              </requiredInstallFiles>
            </config>
            """, out var firstRoot);
        var withChoice = ParseInline("""
            <config>
              <moduleName>M</moduleName>
              <installSteps order="Explicit">
                <installStep name="S">
                  <optionalFileGroups order="Explicit">
                    <group name="G" type="SelectExactlyOne">
                      <plugins order="Explicit">
                        <plugin name="A">
                          <files><folder source="opt-a" destination=""/></files>
                          <typeDescriptor><type name="Recommended"/></typeDescriptor>
                        </plugin>
                        <plugin name="B">
                          <files><folder source="opt-b" destination=""/></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """, out var secondRoot);

        Assert.False(FomodInstallerViewModel.RequiresInteraction(requiredOnly, firstRoot));
        Assert.True(FomodInstallerViewModel.RequiresInteraction(withChoice, secondRoot));
    }

    [Fact]
    public async Task InstallAsyncUsesChooserSelectionInsteadOfDefaults()
    {
        var archive = CreateZip(
            "wizard-choice.zip",
            ("fomod/ModuleConfig.xml", """
                <config>
                  <moduleName>Wizard Mod</moduleName>
                  <installSteps order="Explicit">
                    <installStep name="S">
                      <optionalFileGroups order="Explicit">
                        <group name="G" type="SelectExactlyOne">
                          <plugins order="Explicit">
                            <plugin name="A">
                              <files><folder source="opt-a" destination=""/></files>
                              <typeDescriptor><type name="Recommended"/></typeDescriptor>
                            </plugin>
                            <plugin name="B">
                              <files><folder source="opt-b" destination=""/></files>
                              <typeDescriptor><type name="Optional"/></typeDescriptor>
                            </plugin>
                          </plugins>
                        </group>
                      </optionalFileGroups>
                    </installStep>
                  </installSteps>
                </config>
                """),
            ("opt-a/gamedata/a.txt", "a"),
            ("opt-b/gamedata/b.txt", "b"));

        var result = await ModArchiveInstaller.InstallAsync(
            archive,
            InstallRoot,
            new InlineProgress<ModArchiveInstallProgress>(_ => { }),
            fomodChooser: request =>
            {
                var wizard = new FomodInstallerViewModel(request.Module, request.PackageRoot);
                wizard.VisibleSteps[0].Groups[0].Options[1].IsSelected = true;
                return wizard.BuildSelection();
            });

        Assert.True(result.FomodApplied);
        Assert.Equal("b", File.ReadAllText(Path.Combine(result.ModPath, "gamedata", "b.txt")));
        Assert.False(File.Exists(Path.Combine(result.ModPath, "gamedata", "a.txt")));
    }

    [Fact]
    public async Task InstallAsyncCancelledChooserLeavesNoModBehind()
    {
        var archive = CreateZip(
            "wizard-cancel.zip",
            ("fomod/ModuleConfig.xml", """
                <config>
                  <moduleName>Cancelled</moduleName>
                  <installSteps order="Explicit">
                    <installStep name="S">
                      <optionalFileGroups order="Explicit">
                        <group name="G" type="SelectAny">
                          <plugins order="Explicit">
                            <plugin name="A">
                              <files><folder source="opt-a" destination=""/></files>
                              <typeDescriptor><type name="Optional"/></typeDescriptor>
                            </plugin>
                          </plugins>
                        </group>
                      </optionalFileGroups>
                    </installStep>
                  </installSteps>
                </config>
                """),
            ("opt-a/gamedata/a.txt", "a"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => ModArchiveInstaller.InstallAsync(
            archive,
            InstallRoot,
            new InlineProgress<ModArchiveInstallProgress>(_ => { }),
            fomodChooser: _ => null));

        Assert.Empty(Directory.EnumerateFileSystemEntries(InstallRoot));
    }

    private string InstallRoot => Path.Combine(_root, "installed");

    [Theory]
    [InlineData(FomodPluginType.Optional)]
    [InlineData(FomodPluginType.Recommended)]
    [InlineData(FomodPluginType.CouldBeUsable)]
    [InlineData(FomodPluginType.Required)]
    [InlineData(FomodPluginType.NotUsable)]
    public void SelectAllLabelsUsableOptionsAsRequiredWithoutChangingScriptType(FomodPluginType type)
    {
        using var wizard = CreateGroupWizard(FomodGroupType.SelectAll, type);
        var option = wizard.CurrentStep!.Groups[0].Options[0];
        Assert.Equal(type, option.EffectiveType);
        Assert.Equal(type == FomodPluginType.NotUsable ? StalkerModLauncher.Resources.Strings.Fomod_NotUsable
            : StalkerModLauncher.Resources.Strings.Fomod_Required, option.TypeText);
        Assert.Equal(type != FomodPluginType.NotUsable, option.IsSelected);
        Assert.False(option.IsEnabled);
    }

    [Fact]
    public void AtMostOneAllowsClearingAllChoices()
    {
        var wizard = CreateGroupWizard(FomodGroupType.SelectAtMostOne, FomodPluginType.Recommended, FomodPluginType.Optional);
        var options = wizard.CurrentStep!.Groups[0].Options;
        Assert.True(options[0].IsMultiChoice);
        options[1].IsSelected = true;
        Assert.False(options[0].IsSelected);
        options[1].IsSelected = false;
        Assert.True(wizard.CanFinish());
        Assert.Empty(wizard.BuildSelection().Plugins);
    }

    [Theory]
    [InlineData(FomodGroupType.SelectExactlyOne)]
    [InlineData(FomodGroupType.SelectAtMostOne)]
    public void RequiredSingleChoiceCannotBeReplaced(FomodGroupType groupType)
    {
        var wizard = CreateGroupWizard(groupType, FomodPluginType.Required, FomodPluginType.Optional);
        var options = wizard.CurrentStep!.Groups[0].Options;
        options[0].IsSelected = false;
        options[1].IsSelected = true;
        Assert.True(options[0].IsSelected);
        Assert.False(options[1].IsSelected);
        Assert.False(options[0].IsEnabled);
        Assert.False(options[1].IsEnabled);
        Assert.True(wizard.CanFinish());
    }

    [Theory]
    [InlineData(FomodGroupType.SelectExactlyOne)]
    [InlineData(FomodGroupType.SelectAtLeastOne)]
    public void ImpossibleRequiredChoiceBlocksInstall(FomodGroupType groupType)
    {
        var wizard = CreateGroupWizard(groupType, FomodPluginType.NotUsable);
        wizard.InstallCommand.Execute(null);
        Assert.False(wizard.Completion.IsCompleted);
        Assert.True(wizard.HasValidationError);
    }

    [Fact]
    public void ContradictoryRequiredSingleChoicesAreRejected()
    {
        var wizard = CreateGroupWizard(FomodGroupType.SelectExactlyOne, FomodPluginType.Required, FomodPluginType.Required);
        Assert.False(wizard.CanFinish());
    }

    [Fact]
    public void NewlyVisibleSelectAllChoosesAvailableOptions()
    {
        var first = new FomodPlugin("Enable", "", FomodPluginType.Optional, [], [], new Dictionary<string, string> { ["show"] = "yes" });
        var module = new FomodModule("M", [],
        [
            new("First", null, [new("G", FomodGroupType.SelectAny, [first])]),
            new("Second", new FomodFlagCondition("show", "yes"), [new("All", FomodGroupType.SelectAll,
            [
                new("A", "", FomodPluginType.Optional, [], [], new Dictionary<string, string>()),
                new("B", "", FomodPluginType.NotUsable, [], [], new Dictionary<string, string>())
            ])])
        ], []);
        var wizard = new FomodInstallerViewModel(module, _root);
        wizard.CurrentStep!.Groups[0].Options[0].IsSelected = true;
        Assert.Equal(2, wizard.VisibleSteps.Count);
        var options = wizard.VisibleSteps[1].Groups[0].Options;
        Assert.True(options[0].IsSelected);
        Assert.False(options[0].IsEnabled);
        Assert.False(options[1].IsSelected);
        Assert.True(wizard.CanFinish());
        wizard.CurrentStep.Groups[0].Options[0].IsSelected = false;
        Assert.Single(wizard.VisibleSteps);
        Assert.False(wizard.BuildSelection().IsSelected(1, 0, 0));
    }

    [Fact]
    public void ResolutionUsesEarlierFlagsAndIgnoresStaleHiddenSelections()
    {
        var flagsOn = new Dictionary<string, string> { ["mode"] = "on" };
        var flagsOff = new Dictionary<string, string> { ["mode"] = "off" };
        var empty = new Dictionary<string, string>();
        var module = new FomodModule("M", [],
        [
            new("First", null, [new("Mode", FomodGroupType.SelectExactlyOne,
            [
                new("On", "", FomodPluginType.Recommended, [], [], flagsOn),
                new("Off", "", FomodPluginType.Optional, [], [], flagsOff)
            ])]),
            new("Second", new FomodFlagCondition("mode", "on"), [new("Payload", FomodGroupType.SelectAny,
            [new("Payload", "", FomodPluginType.Recommended, [], [new("chosen.txt", "chosen.txt", 0)], empty)])]),
            new("Third", null, [new("Reset", FomodGroupType.SelectAll,
            [new("Reset", "", FomodPluginType.Required, [], [], flagsOff)])])
        ], []);
        var wizard = new FomodInstallerViewModel(module, _root);
        Assert.Single(FomodSelector.ResolveFiles(module, wizard.BuildSelection()));
        wizard.CurrentStep!.Groups[0].Options[1].IsSelected = true;
        var selection = wizard.BuildSelection();
        // A stale selection from a hidden page must not contribute files or flags.
        selection.Plugins[(1, 0)] = [0];
        Assert.Empty(FomodSelector.ResolveFiles(module, selection));
    }

    [Fact]
    public void LaterRequiredTypeReplacesEarlierOptionalChoice()
    {
        var empty = new Dictionary<string, string>();
        var module = new FomodModule("M", [],
        [
            new("First", null, [new("Mode", FomodGroupType.SelectAny,
            [new("Enable", "", FomodPluginType.Optional, [], [], new Dictionary<string, string> { ["required"] = "yes" })])]),
            new("Second", null, [new("Choice", FomodGroupType.SelectExactlyOne,
            [
                new("Default", "", FomodPluginType.Recommended, [], [], empty),
                new("Conditional", "", FomodPluginType.Optional,
                    [new(new FomodFlagCondition("required", "yes"), FomodPluginType.Required)], [], empty)
            ])])
        ], []);
        var wizard = new FomodInstallerViewModel(module, _root);
        wizard.CurrentStep!.Groups[0].Options[0].IsSelected = true;
        var options = wizard.VisibleSteps[1].Groups[0].Options;
        Assert.False(options[0].IsSelected);
        Assert.True(options[1].IsSelected);
        Assert.True(wizard.CanFinish());
    }

    [Fact]
    public async Task CompletionIsSingleAndCancellationDoesNotBecomeInstallation()
    {
        var wizard = CreateGroupWizard(FomodGroupType.SelectAny, FomodPluginType.Optional);
        var completed = 0;
        wizard.Completed += (_, _) => completed++;
        wizard.CancelCommand.Execute(null);
        wizard.InstallCommand.Execute(null);
        wizard.Dispose();
        Assert.Null(await wizard.Completion);
        Assert.Equal(1, completed);
    }

    [Fact]
    public void FinishFindsInvalidEarlierPageAfterNavigatingBack()
    {
        var wizard = CreateGroupWizard(FomodGroupType.SelectAtLeastOne, FomodPluginType.Optional);
        wizard.CurrentStep!.Groups[0].Options[0].IsSelected = false;
        Assert.False(wizard.MoveNext());
        Assert.True(wizard.HasValidationError);
        wizard.CurrentStep.Groups[0].Options[0].IsSelected = true;
        Assert.False(wizard.HasValidationError);
    }

    private FomodInstallerViewModel CreateGroupWizard(FomodGroupType type, params FomodPluginType[] types) =>
        new(new FomodModule("M", [], [new("S", null, [new("G", type, types.Select((pluginType, index) =>
            new FomodPlugin(index.ToString(System.Globalization.CultureInfo.InvariantCulture), "", pluginType, [], [], new Dictionary<string, string>())).ToArray())])], []), _root);

    private FomodInstallerViewModel CreateViewModel(string xml)
    {
        var module = ParseInline(xml, out var packageRoot);
        return new FomodInstallerViewModel(module, packageRoot);
    }

    private FomodModule ParseInline(string xml, out string packageRoot)
    {
        Directory.CreateDirectory(_root);
        packageRoot = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        var path = Path.Combine(packageRoot, "module.xml");
        File.WriteAllText(path, xml, Encoding.UTF8);
        return FomodParser.Parse(path);
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
