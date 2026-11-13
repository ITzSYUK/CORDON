using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using System.Text;
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
    public async Task BuildAsyncOverlaysModsInOrderWithoutChangingSources()
    {
        var firstMod = CreateMod("first", "first");
        var secondMod = CreateMod("second", "second");
        var profile = CreateProfile(firstMod, secondMod);

        var result = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal("second", File.ReadAllText(Path.Combine(result.WorkspaceRoot, "gamedata", "config", "shared.ltx")));
        Assert.Equal("base", File.ReadAllText(Path.Combine(_gamePath, "gamedata", "config", "shared.ltx")));
        Assert.Equal("first", File.ReadAllText(Path.Combine(firstMod, "gamedata", "config", "shared.ltx")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(secondMod, "gamedata", "config", "shared.ltx")));
    }

    [Fact]
    public async Task BuildAsyncPreservesEmptySourceDirectories()
    {
        var emptyGameDirectory = Path.Combine(_gamePath, "gamedata", "empty-game-directory");
        var modPath = CreateMod("empty-directory-mod", "mod");
        var emptyModDirectory = Path.Combine(modPath, "gamedata", "empty-mod-directory");
        Directory.CreateDirectory(emptyGameDirectory);
        Directory.CreateDirectory(emptyModDirectory);
        var profile = CreateProfile(modPath);

        var result = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.True(Directory.Exists(Path.Combine(result.WorkspaceRoot, "gamedata", "empty-game-directory")));
        Assert.True(Directory.Exists(Path.Combine(result.WorkspaceRoot, "gamedata", "empty-mod-directory")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsyncRebuildsWhenEmptySourceDirectoryIsAddedOrRemoved(bool useMod)
    {
        var modPath = CreateMod("empty-directory-changes", "mod");
        var profile = CreateProfile(modPath);
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var relative = Path.Combine("gamedata", "empty-directory-changes");
        var sourceDirectory = Path.Combine(useMod ? modPath : _gamePath, relative);
        var workspaceDirectory = Path.Combine(first.WorkspaceRoot, relative);
        Directory.CreateDirectory(sourceDirectory);
        var addedProgress = new ProgressLog();

        await _builder.BuildAsync(_gamePath, profile, addedProgress);

        Assert.True(Directory.Exists(workspaceDirectory));
        Assert.DoesNotContain(Strings.WorkspaceManifest_Current, addedProgress.Messages);
        Directory.Delete(sourceDirectory);
        var removedProgress = new ProgressLog();

        await _builder.BuildAsync(_gamePath, profile, removedProgress);

        Assert.False(Directory.Exists(workspaceDirectory));
        Assert.DoesNotContain(Strings.WorkspaceManifest_Current, removedProgress.Messages);
    }

    [Fact]
    public async Task BuildAsyncRestoresMissingCachedEmptyDirectory()
    {
        var relative = Path.Combine("gamedata", "cached-empty-directory");
        Directory.CreateDirectory(Path.Combine(_gamePath, relative));
        var profile = CreateProfile();
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var workspaceDirectory = Path.Combine(first.WorkspaceRoot, relative);
        Directory.Delete(workspaceDirectory);
        var progress = new ProgressLog();

        await _builder.BuildAsync(_gamePath, profile, progress);

        Assert.True(Directory.Exists(workspaceDirectory));
        Assert.DoesNotContain(Strings.WorkspaceManifest_Current, progress.Messages);
    }

    [Theory]
    [InlineData("bin/xrCore.dll")]
    [InlineData("gamedata/config/shared.ltx")]
    public async Task BuildAsyncRestoresMissingCachedFileWithoutChangingSource(string relative)
    {
        CreateFile(_gamePath, relative, "source content");
        var profile = CreateProfile();
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var workspaceFile = Path.Combine(first.WorkspaceRoot, relative);
        File.Delete(workspaceFile);
        var progress = new ProgressLog();

        await _builder.BuildAsync(_gamePath, profile, progress);

        Assert.Equal("source content", File.ReadAllText(workspaceFile));
        Assert.Equal("source content", File.ReadAllText(Path.Combine(_gamePath, relative)));
        Assert.DoesNotContain(Strings.WorkspaceManifest_Current, progress.Messages);
    }

    [Fact]
    public async Task BuildAsyncReusesCacheWithExcludedModFile()
    {
        var modPath = CreateMod("excluded-cache", "mod");
        const string relative = "gamedata/config/excluded.ltx";
        CreateFile(modPath, relative, "excluded content");
        var profile = CreateProfile(modPath);
        profile.Mods[0].ExcludedFiles.Add(relative);
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var progress = new ProgressLog();

        await _builder.BuildAsync(_gamePath, profile, progress);

        Assert.Contains(Strings.WorkspaceManifest_Current, progress.Messages);
        Assert.False(File.Exists(Path.Combine(first.WorkspaceRoot, relative)));
    }

    [Fact]
    public async Task BuildAsyncPreservesWritableSettingsWhileRepairingCache()
    {
        const string optionsRelative = "gamedata/configs/axr_options.ltx";
        CreateFile(_gamePath, optionsRelative, "source options");
        var profile = CreateProfile();
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var options = Path.Combine(first.WorkspaceRoot, optionsRelative);
        File.WriteAllText(options, "latest profile options");
        var save = Path.Combine(first.ProfileWorkspacePath, "userdata", "savedgames", "cache-repair.sav");
        CreateFileAtPath(save, "profile save");
        File.Delete(Path.Combine(first.WorkspaceRoot, "gamedata", "config", "shared.ltx"));

        await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.True(File.Exists(Path.Combine(first.WorkspaceRoot, "gamedata", "config", "shared.ltx")));
        Assert.Equal("latest profile options", File.ReadAllText(options));
        Assert.Equal("profile save", File.ReadAllText(save));
        Assert.Equal("source options", File.ReadAllText(Path.Combine(_gamePath, optionsRelative)));
    }

    [Fact]
    public async Task BuildAsyncSkipsProfileExcludedConflictFile()
    {
        var firstMod = CreateMod("excluded-first", "first");
        var secondMod = CreateMod("excluded-second", "second");
        var profile = CreateProfile(firstMod, secondMod);
        profile.Mods[1].ExcludedFiles.Add(Path.Combine("gamedata", "config", "shared.ltx"));

        var result = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal("first", File.ReadAllText(Path.Combine(result.WorkspaceRoot, "gamedata", "config", "shared.ltx")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(secondMod, "gamedata", "config", "shared.ltx")));
    }

    [Fact]
    public async Task BuildAsyncLinksModConfigurationWithoutCopyingIt()
    {
        var modPath = CreateMod("mod", "mod source");
        var profile = CreateProfile(modPath);

        var result = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal("mod source", File.ReadAllText(Path.Combine(modPath, "gamedata", "config", "shared.ltx")));
        Assert.Equal("base", File.ReadAllText(Path.Combine(_gamePath, "gamedata", "config", "shared.ltx")));
        Assert.True(File.Exists(Path.Combine(result.WorkspaceRoot, "gamedata", "config", "shared.ltx")));
    }

    [Fact]
    public async Task BuildAsyncRebuildsReadOnlyModFilesWithoutChangingTheirAttributes()
    {
        var modPath = CreateMod("read-only", "mod source");
        var sourceFile = Path.Combine(modPath, "gamedata", "config", "shared.ltx");
        File.SetAttributes(sourceFile, File.GetAttributes(sourceFile) | FileAttributes.ReadOnly);
        var profile = CreateProfile(modPath);

        try
        {
            var progress = new ProgressLog();
            await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
            CreateFile(modPath, "gamedata/config/rebuild-marker.ltx", "changed");

            var rebuilt = await _builder.BuildAsync(_gamePath, profile, progress);

            Assert.Equal("mod source", File.ReadAllText(sourceFile));
            Assert.NotEqual(0, (int)(File.GetAttributes(sourceFile) & FileAttributes.ReadOnly));
            Assert.Equal("mod source", File.ReadAllText(Path.Combine(rebuilt.WorkspaceRoot, "gamedata", "config", "shared.ltx")));
            Assert.True(File.Exists(Path.Combine(rebuilt.WorkspaceRoot, "gamedata", "config", "rebuild-marker.ltx")));
            Assert.Contains(progress.Messages, message => message.Contains("Файлы «только чтение»", StringComparison.Ordinal));
            Assert.Contains(progress.Messages, message => message.Contains("Время подготовки", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(sourceFile))
            {
                File.SetAttributes(sourceFile, File.GetAttributes(sourceFile) & ~FileAttributes.ReadOnly);
            }
        }
    }

    [Fact]
    public async Task BuildAsyncRepairsLegacyReadOnlyHardLinkBeforeRebuild()
    {
        var modPath = CreateMod("legacy-read-only", "mod source");
        var sourceFile = Path.Combine(modPath, "gamedata", "config", "shared.ltx");
        var profile = CreateProfile(modPath);
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var workspaceFile = Path.Combine(first.WorkspaceRoot, "gamedata", "config", "shared.ltx");
        File.SetAttributes(sourceFile, File.GetAttributes(sourceFile) | FileAttributes.ReadOnly);

        try
        {
            Assert.NotEqual(0, (int)(File.GetAttributes(workspaceFile) & FileAttributes.ReadOnly));
            CreateFile(modPath, "gamedata/config/rebuild-marker.ltx", "changed");

            var progress = new ProgressLog();
            var rebuilt = await _builder.BuildAsync(_gamePath, profile, progress);

            Assert.Equal("mod source", File.ReadAllText(sourceFile));
            Assert.NotEqual(0, (int)(File.GetAttributes(sourceFile) & FileAttributes.ReadOnly));
            Assert.Equal("mod source", File.ReadAllText(Path.Combine(rebuilt.WorkspaceRoot, "gamedata", "config", "shared.ltx")));
            Assert.Contains(progress.Messages, message => message.Contains("Освобождено защищённых ссылок", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(sourceFile))
            {
                File.SetAttributes(sourceFile, File.GetAttributes(sourceFile) & ~FileAttributes.ReadOnly);
            }
        }
    }

    [Fact]
    public async Task BuildAsyncRewritesOnlyWorkspaceFsgameAndKeepsProfileUserData()
    {
        var modPath = CreateMod("mod", "mod");
        var profile = CreateProfile(modPath);

        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var savePath = Path.Combine(first.ProfileWorkspacePath, "userdata", "savedgames", "test.sav");
        CreateFileAtPath(savePath, "save");
        CreateFileAtPath(Path.Combine(first.ProfileWorkspacePath, "userdata", "user.ltx"), "profile settings");
        CreateFile(modPath, "gamedata/config/new-file.ltx", "changed");

        await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal("save", File.ReadAllText(savePath));
        Assert.Equal("profile settings", File.ReadAllText(Path.Combine(first.ProfileWorkspacePath, "userdata", "user.ltx")));
        Assert.Equal("$app_data_root$ = true | false | appdata", File.ReadAllText(Path.Combine(_gamePath, "fsgame.ltx")));
        Assert.Contains(Path.Combine(first.ProfileWorkspacePath, "userdata"), File.ReadAllText(Path.Combine(first.WorkspaceRoot, "fsgame.ltx")));
    }

    [Fact]
    public async Task BuildAsyncSeedsUserSettingsFromHighestPriorityModAppData()
    {
        var firstMod = CreateMod("first-user-settings", "first");
        var fix = CreateMod("fix-user-settings", "fix");
        CreateFile(firstMod, "appdata/user.ltx", "first mod settings");
        CreateFile(fix, "appdata/user.ltx", "fix settings");
        var profile = CreateProfile(firstMod, fix);

        var result = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal(
            "fix settings",
            File.ReadAllText(Path.Combine(result.ProfileWorkspacePath, "userdata", "user.ltx")));
        Assert.Equal("first mod settings", File.ReadAllText(Path.Combine(firstMod, "appdata", "user.ltx")));
        Assert.Equal("fix settings", File.ReadAllText(Path.Combine(fix, "appdata", "user.ltx")));
    }

    [Fact]
    public async Task BuildAsyncUpgradesUnchangedBaseUserSettingsWhenFixIsAdded()
    {
        var profile = CreateProfile();
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var profileUserLtx = Path.Combine(first.ProfileWorkspacePath, "userdata", "user.ltx");
        Assert.Equal("base user settings", File.ReadAllText(profileUserLtx));

        var fix = CreateMod("later-user-settings-fix", "fix");
        CreateFile(fix, "appdata/user.ltx", "fix settings");
        profile.Mods.Add(new ModEntry
        {
            Id = "later-user-settings-fix",
            Name = "Later user settings fix",
            SourcePath = fix,
            IsEnabled = true,
            Order = 1
        });

        await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal("fix settings", File.ReadAllText(profileUserLtx));
    }

    [Fact]
    public async Task BuildAsyncSeedsMissingProfileShaderCacheAndRestoresItAfterDeletion()
    {
        CreateFile(
            _gamePath,
            "appdata/shaders_cache/r4/pp_bloom.ps/variant",
            "precompiled shader");
        var profile = CreateProfile(CreateMod("mod", "mod"));

        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var profileCacheFile = Path.Combine(
            first.ProfileWorkspacePath,
            "userdata",
            "shaders_cache",
            "r4",
            "pp_bloom.ps",
            "variant");

        Assert.Equal("precompiled shader", File.ReadAllText(profileCacheFile));

        Directory.Delete(Path.Combine(first.ProfileWorkspacePath, "userdata", "shaders_cache"), recursive: true);
        var progress = new ProgressLog();
        await _builder.BuildAsync(_gamePath, profile, progress);

        Assert.Equal("precompiled shader", File.ReadAllText(profileCacheFile));
        Assert.Contains(
            progress.Messages,
            message => message.Contains("Кэш шейдеров профиля подготовлен", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsyncWritesFsgameWithWindows1251ForCyrillicWorkspacePath()
    {
        var profile = CreateProfile(CreateMod("mod", "mod"));
        profile.Name = "Аномали";

        var result = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var fsgameBytes = File.ReadAllBytes(Path.Combine(result.WorkspaceRoot, "fsgame.ltx"));
        var fsgameText = Encoding.GetEncoding(1251).GetString(fsgameBytes);
        Assert.Contains(Path.Combine(result.ProfileWorkspacePath, "userdata"), fsgameText);
        Assert.Equal($"profile-{profile.Id}", Path.GetFileName(result.ProfileWorkspacePath));
        Assert.All(fsgameText, character => Assert.True(character <= 0x7F));
    }

    [Fact]
    public async Task BuildAsyncRewritesCachedUtf8FsgameWithAsciiProfileDataPath()
    {
        var profile = CreateProfile(CreateMod("mod", "mod"));
        profile.Name = "Мой мод";
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var fsgamePath = Path.Combine(first.WorkspaceRoot, "fsgame.ltx");
        var appDataPath = Path.Combine(first.ProfileWorkspacePath, "userdata");

        File.WriteAllText(fsgamePath, $"$app_data_root$ = true | false| {appDataPath}", Encoding.UTF8);

        var second = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal(first.WorkspaceRoot, second.WorkspaceRoot);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var fsgameText = Encoding.GetEncoding(1251).GetString(File.ReadAllBytes(fsgamePath));
        Assert.Contains(appDataPath, fsgameText);
        Assert.DoesNotContain("РњРѕР№", fsgameText);
    }

    [Fact]
    public async Task BuildAsyncDoesNotRestoreLegacyStoredFsgame()
    {
        var modPath = CreateMod("mod", "mod");
        var profile = CreateProfile(modPath);
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var storedFsgamePath = Path.Combine(
            first.ProfileWorkspacePath,
            "userdata",
            "writable-game-files",
            "fsgame.ltx");
        Directory.CreateDirectory(Path.GetDirectoryName(storedFsgamePath)!);
        File.WriteAllText(
            storedFsgamePath,
            """
            $app_data_root$ = true | false| old
            $game_weathers$ = true| false| $game_config$| environment\weathers
            $mod_dir$ = false| false| $fs_root$| mods\
            """);
        CreateFile(modPath, "gamedata/config/rebuild-marker.ltx", "changed");

        var rebuilt = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        var fsgameText = File.ReadAllText(Path.Combine(rebuilt.WorkspaceRoot, "fsgame.ltx"));
        Assert.Contains(Path.Combine(rebuilt.ProfileWorkspacePath, "userdata"), fsgameText);
        Assert.DoesNotContain("$game_weathers$", fsgameText);
        Assert.DoesNotContain("$mod_dir$", fsgameText);
        Assert.False(File.Exists(storedFsgamePath));
    }

    [Fact]
    public async Task BuildAsyncPreservesGeneratedAnomalyLocalizationFileAcrossRebuild()
    {
        var modPath = CreateMod("mod", "mod");
        var profile = CreateProfile(modPath);
        profile.Name = "Аномали";
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var localizationPath = Path.Combine(first.WorkspaceRoot, "gamedata", "configs", "localization.ltx");

        Assert.True(Directory.Exists(Path.GetDirectoryName(localizationPath)));
        File.WriteAllText(localizationPath, "language = rus");
        CreateFile(modPath, "gamedata/config/rebuild-marker.ltx", "changed");

        var rebuilt = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal("language = rus", File.ReadAllText(Path.Combine(
            rebuilt.WorkspaceRoot,
            "gamedata",
            "configs",
            "localization.ltx")));
        var storedLocalizationPath = Path.Combine(
            rebuilt.ProfileWorkspacePath,
            "userdata",
            "writable-game-files",
            "gamedata",
            "configs",
            "localization.ltx");
        Assert.Equal("language = rus", File.ReadAllText(storedLocalizationPath));
        File.WriteAllText(Path.Combine(
            rebuilt.WorkspaceRoot,
            "gamedata",
            "configs",
            "localization.ltx"), "language = eng");
        Assert.Equal("language = rus", File.ReadAllText(storedLocalizationPath));
        Assert.False(File.Exists(Path.Combine(_gamePath, "gamedata", "configs", "localization.ltx")));
    }

    [Fact]
    public async Task BuildAsyncKeepsAnomalyOptionsIndependentFromBaseGameAcrossRebuild()
    {
        var sourceOptions = Path.Combine(_gamePath, "gamedata", "configs", "axr_options.ltx");
        CreateFileAtPath(sourceOptions, "base options");
        var modPath = CreateMod("anomaly-options", "mod");
        var profile = CreateProfile(modPath);

        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var workspaceOptions = Path.Combine(
            first.WorkspaceRoot,
            "gamedata",
            "configs",
            "axr_options.ltx");
        File.WriteAllText(workspaceOptions, "profile options");

        Assert.Equal("base options", File.ReadAllText(sourceOptions));

        CreateFile(modPath, "gamedata/config/rebuild-marker.ltx", "changed");
        var rebuilt = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal("base options", File.ReadAllText(sourceOptions));
        Assert.Equal("profile options", File.ReadAllText(Path.Combine(
            rebuilt.WorkspaceRoot,
            "gamedata",
            "configs",
            "axr_options.ltx")));
        Assert.Equal("profile options", File.ReadAllText(Path.Combine(
            rebuilt.ProfileWorkspacePath,
            "userdata",
            "writable-game-files",
            "gamedata",
            "configs",
            "axr_options.ltx")));
    }

    [Fact]
    public async Task BuildAsyncKeepsAnomalyLauncherConfigurationIndependentFromBaseGame()
    {
        var sourceConfiguration = Path.Combine(_gamePath, "AnomalyLauncher.cfg");
        CreateFileAtPath(sourceConfiguration, "DX11\nAVX\n1");
        var profile = CreateProfile();

        var result = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var workspaceConfiguration = Path.Combine(result.WorkspaceRoot, "AnomalyLauncher.cfg");
        File.WriteAllText(workspaceConfiguration, "DX9\nNOAVX\n1");

        Assert.Equal("DX11\nAVX\n1", File.ReadAllText(sourceConfiguration));
        Assert.Equal("DX9\nNOAVX\n1", File.ReadAllText(workspaceConfiguration));
    }

    [Fact]
    public async Task BuildAsyncCapturesAnomalyOptionsBeforeReusingCachedWorkspace()
    {
        var sourceOptions = Path.Combine(_gamePath, "gamedata", "configs", "axr_options.ltx");
        CreateFileAtPath(sourceOptions, "base options");
        var profile = CreateProfile();
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var workspaceOptions = Path.Combine(
            first.WorkspaceRoot,
            "gamedata",
            "configs",
            "axr_options.ltx");
        File.WriteAllText(workspaceOptions, "changed by game");
        var progress = new ProgressLog();

        var cached = await _builder.BuildAsync(_gamePath, profile, progress);

        Assert.Contains(
            progress.Messages,
            message => message.Contains("Пересборка не требуется", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("base options", File.ReadAllText(sourceOptions));
        Assert.Equal("changed by game", File.ReadAllText(Path.Combine(
            cached.ProfileWorkspacePath,
            "userdata",
            "writable-game-files",
            "gamedata",
            "configs",
            "axr_options.ltx")));
        Assert.Equal("changed by game", File.ReadAllText(workspaceOptions));
    }

    [Fact]
    public async Task BuildAsyncPreservesNewerProfileOptionsWhenReturningFromUsvfs()
    {
        var sourceOptions = Path.Combine(_gamePath, "gamedata", "configs", "axr_options.ltx");
        CreateFileAtPath(sourceOptions, "base options");
        var profile = CreateProfile();
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var workspaceOptions = Path.Combine(
            first.WorkspaceRoot,
            "gamedata",
            "configs",
            "axr_options.ltx");
        var workspaceWriteTime = DateTime.UtcNow.AddMinutes(-2);
        File.WriteAllText(workspaceOptions, "workspace options");
        File.SetLastWriteTimeUtc(workspaceOptions, workspaceWriteTime);
        var layers = FileLayerPlan.CreateForProfile(_gamePath, profile, first.ProfileWorkspacePath);
        var manifest = OverlayManifestBuilder.BuildVirtualFileSystem(profile, layers, first.ProfileWorkspacePath);
        ProfileWritableGameFileStore.Prepare(layers, manifest, first.ProfileWorkspacePath);
        var storedOptions = Path.Combine(
            first.ProfileWorkspacePath,
            "userdata",
            "writable-game-files",
            "gamedata",
            "configs",
            "axr_options.ltx");
        Assert.Equal("workspace options", File.ReadAllText(storedOptions));
        var usvfsWriteTime = DateTime.UtcNow;
        File.WriteAllText(storedOptions, "usvfs options");
        File.SetLastWriteTimeUtc(storedOptions, usvfsWriteTime);
        await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal("base options", File.ReadAllText(sourceOptions));
        Assert.Equal("usvfs options", File.ReadAllText(storedOptions));
        Assert.Equal("usvfs options", File.ReadAllText(workspaceOptions));
        Assert.Equal(usvfsWriteTime, File.GetLastWriteTimeUtc(workspaceOptions));
    }

    [Fact]
    public async Task BuildAsyncUsesCacheUntilOverlayInputChanges()
    {
        var modPath = CreateMod("mod", "first");
        var profile = CreateProfile(modPath);
        await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var cachedProgress = new ProgressLog();

        await _builder.BuildAsync(_gamePath, profile, cachedProgress);

        Assert.Contains(cachedProgress.Messages, message => message.Contains("Workspace уже актуален", StringComparison.Ordinal));

        CreateFile(modPath, "gamedata/config/shared.ltx", "updated content");
        var rebuildProgress = new ProgressLog();
        var rebuilt = await _builder.BuildAsync(_gamePath, profile, rebuildProgress);

        Assert.DoesNotContain(rebuildProgress.Messages, message => message.Contains("Workspace уже актуален", StringComparison.Ordinal));
        Assert.Contains(rebuildProgress.Messages, message => message.Contains("Workspace будет пересобран", StringComparison.Ordinal));
        Assert.Contains(
            rebuildProgress.Messages,
            message => message.Contains("gamedata", StringComparison.OrdinalIgnoreCase) &&
                       message.Contains("shared.ltx", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("updated content", File.ReadAllText(Path.Combine(rebuilt.WorkspaceRoot, "gamedata", "config", "shared.ltx")));
    }

    [Fact]
    public async Task BuildAsyncRebuildsWhenFsltxTargetChanges()
    {
        CreateFile(
            _gamePath,
            "fsgame_coc.ltx",
            "$app_data_root$ = true | false | appdata\n$source$ = coc");
        CreateFile(
            _gamePath,
            "fsgame_alt.ltx",
            "$app_data_root$ = true | false | appdata\n$source$ = alt");
        var profile = CreateProfile();
        profile.LaunchArguments = "-fsltx fsgame_coc.ltx";
        await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        profile.LaunchArguments = "-fsltx fsgame_alt.ltx";
        var progress = new ProgressLog();

        var rebuilt = await _builder.BuildAsync(_gamePath, profile, progress);

        Assert.DoesNotContain(progress.Messages, message => message.Contains("Workspace уже актуален", StringComparison.Ordinal));
        Assert.Contains("$source$ = alt", File.ReadAllText(Path.Combine(rebuilt.WorkspaceRoot, "fsgame_alt.ltx")));
    }

    [Fact]
    public async Task BuildAsyncReportsExecutableChangeAsRebuildReason()
    {
        CreateFile(_gamePath, "bin/alternate.exe", "alternate executable");
        var profile = CreateProfile();
        await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        profile.ExecutableRelativePath = @"bin\alternate.exe";
        var progress = new ProgressLog();

        await _builder.BuildAsync(_gamePath, profile, progress);

        Assert.Contains(
            progress.Messages,
            message => message.Contains("сменился файл запуска", StringComparison.OrdinalIgnoreCase) &&
                       message.Contains("alternate.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsyncRebuildsWorkspaceWhenCachedExecutableIsMissing()
    {
        var profile = CreateProfile(CreateMod("mod", "mod"));
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        File.Delete(first.ExecutablePath);
        var progress = new ProgressLog();

        var rebuilt = await _builder.BuildAsync(_gamePath, profile, progress);

        Assert.True(File.Exists(rebuilt.ExecutablePath));
        Assert.Contains(progress.Messages, message => message.Contains("выбранный EXE отсутствует", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("Подготовка чистой рабочей среды", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClearProfileWorkspaceCacheRestoresMissingMarkerForGeneratedWorkspace()
    {
        var profile = CreateProfile(CreateMod("mod", "mod"));
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        profile.WorkspacePath = first.ProfileWorkspacePath;
        var markerPath = Path.Combine(first.ProfileWorkspacePath, ".stalker-launcher-workspace");
        File.Delete(markerPath);

        var progress = new ProgressLog();
        _builder.ClearProfileWorkspaceCache(profile, _gamePath, progress);

        Assert.True(File.Exists(markerPath));
        Assert.False(Directory.Exists(first.WorkspaceRoot));
        Assert.Contains(progress.Messages, message => message.Contains("Восстановлен защитный маркер", StringComparison.Ordinal));
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
    public async Task BuildAsyncDoesNotFallbackToDedicatedServerExecutable()
    {
        File.Delete(Path.Combine(_gamePath, "bin", "xr_3da.exe"));
        CreateFile(_gamePath, "bin/dedicated/XR_3DA.exe", "dedicated server");
        var profile = CreateProfile();

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => _builder.BuildAsync(_gamePath, profile, new ProgressLog()));

        Assert.Contains("не найден файл запуска профиля", exception.Message);
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

    [Fact]
    public async Task BuildAsyncRejectsManagedWorkspaceRootAsProfileWorkspace()
    {
        var profile = CreateProfile(CreateMod("mod", "mod"));
        profile.WorkspacePath = _workspaceRoot;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _builder.BuildAsync(_gamePath, profile, new ProgressLog()));

        Assert.Contains(_workspaceRoot, exception.Message);
    }

    [Fact]
    public async Task BuildAsyncReportsMissingEnabledModFolderClearly()
    {
        var missingMod = Path.Combine(_root, "mods", "missing");
        var profile = CreateProfile(missingMod);

        var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _builder.BuildAsync(_gamePath, profile, new ProgressLog()));

        Assert.Contains(missingMod, exception.Message);
        Assert.Equal("base", File.ReadAllText(Path.Combine(_gamePath, "gamedata", "config", "shared.ltx")));
    }

    [Fact]
    public async Task BuildAsyncRejectsEmptyOverlayGamePathBeforeCreatingWorkspace()
    {
        var profile = CreateProfile(CreateMod("mod", "mod"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _builder.BuildAsync(string.Empty, profile, new ProgressLog()));

        Assert.Contains("выберите папку игры", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(profile.WorkspacePath);
        Assert.False(Directory.Exists(_workspaceRoot));
    }

    [Fact]
    public async Task BuildAsyncCancellationDoesNotChangeSourcesOrWriteManifest()
    {
        var modPath = CreateMod("mod", "mod source");
        var profile = CreateProfile(modPath);
        using var cancellation = new CancellationTokenSource();
        var progress = new ProgressLog(message =>
        {
            if (message.Contains("Подготовка чистой рабочей среды", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _builder.BuildAsync(_gamePath, profile, progress, cancellationToken: cancellation.Token));

        Assert.Equal("base", File.ReadAllText(Path.Combine(_gamePath, "gamedata", "config", "shared.ltx")));
        Assert.Equal("mod source", File.ReadAllText(Path.Combine(modPath, "gamedata", "config", "shared.ltx")));
        var generatedWorkspace = Path.Combine(_workspaceRoot, $"profile-{profile.Id}");
        Assert.False(File.Exists(Path.Combine(generatedWorkspace, "build-manifest.json")));
    }

    [Fact]
    public async Task BuildAsyncDoesNotMutateBoundProfilePropertiesOnWorkerThread()
    {
        var profile = CreateProfile(CreateMod("mod", "mod"));
        var originalExecutable = profile.ExecutableRelativePath;

        var result = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Empty(profile.WorkspacePath);
        Assert.Equal(originalExecutable, profile.ExecutableRelativePath);
        Assert.Empty(profile.WorkingDirectoryRelative);
        Assert.EndsWith($"profile-{profile.Id}", result.ProfileWorkspacePath);
    }

    [Fact]
    public async Task BuildAsyncUsesManuallyPinnedExecutableSourceOverHigherPriorityMod()
    {
        var mainMod = CreateMod("main", "main");
        var patch = CreateMod("patch", "patch");
        CreateFile(mainMod, "bin_x64/xrEngine.exe", "main executable");
        CreateFile(patch, "bin_x64/xrEngine.exe", "patch executable");
        var profile = CreateProfile(mainMod, patch);
        profile.ExecutableRelativePath = @"bin_x64\xrEngine.exe";
        profile.ExecutableSourcePath = mainMod;

        var result = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal("main executable", File.ReadAllText(result.ExecutablePath));
        Assert.Equal(Path.Combine(result.WorkspaceRoot, "bin_x64", "xrEngine.exe"), result.ExecutablePath);
    }

    [Fact]
    public async Task BuildAsyncDoesNotReusePartiallyRebuiltCurrent()
    {
        var main = CreateMod("main", "main");
        var patch = CreateMod("patch", "patch");
        CreateFile(main, "bin/xr_3da.exe", "pinned");
        CreateFile(patch, "bin/xr_3da.exe", "higher-priority");
        var profile = CreateProfile(main, patch);
        profile.ExecutableSourcePath = main;
        var first = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        File.Delete(Path.Combine(first.WorkspaceRoot, "gamedata", "config", "shared.ltx"));

        using var cancellation = new CancellationTokenSource();
        var patchName = FileLayerPlan.GetDisplayName(
            FileLayerPlan.CreateForProfile(_gamePath, profile, first.ProfileWorkspacePath).Mods[^1]);
        var afterPatch = Strings.Materializer_ModConnectedFormat.Split("{1:N0}", StringSplitOptions.None)[0]
            .Replace("{0}", patchName, StringComparison.Ordinal);
        var progress = new ProgressLog(message =>
        {
            if (message.StartsWith(afterPatch, StringComparison.Ordinal))
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _builder.BuildAsync(_gamePath, profile, progress, cancellationToken: cancellation.Token));

        var retry = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.Equal("pinned", File.ReadAllText(retry.ExecutablePath));
    }

    [Fact]
    public async Task BuildAsyncBlocksLaunchWhenFsgameCannotIsolateProfileData()
    {
        var sourceFsgame = Path.Combine(_gamePath, "fsgame.ltx");
        const string original = "$game_data$ = true | true | gamedata\\";
        File.WriteAllText(sourceFsgame, original);
        var profile = CreateProfile(CreateMod("missing-app-data-root", "mod"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => _builder.BuildAsync(_gamePath, profile, new ProgressLog()));

        Assert.Contains("$app_data_root$", error.Message);
        Assert.Contains("запуск заблокирован", error.Message);
        Assert.Equal(original, File.ReadAllText(sourceFsgame));
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
