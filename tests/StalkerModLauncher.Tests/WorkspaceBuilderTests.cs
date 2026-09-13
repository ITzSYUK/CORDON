using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using System.Text;
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
            message => message.Contains("Profile shader cache prepared", StringComparison.Ordinal));
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
        await _builder.BuildAsync(_gamePath, profile, new ProgressLog());
        var storedOptions = Path.Combine(
            first.ProfileWorkspacePath,
            "userdata",
            "writable-game-files",
            "gamedata",
            "configs",
            "axr_options.ltx");
        var usvfsWriteTime = DateTime.UtcNow;
        File.WriteAllText(storedOptions, "usvfs options");
        File.SetLastWriteTimeUtc(storedOptions, usvfsWriteTime);
        var progress = new ProgressLog();

        await _builder.BuildAsync(_gamePath, profile, progress);

        Assert.Equal("base options", File.ReadAllText(sourceOptions));
        Assert.Equal("usvfs options", File.ReadAllText(storedOptions));
        Assert.Equal("usvfs options", File.ReadAllText(workspaceOptions));
        Assert.Equal(usvfsWriteTime, File.GetLastWriteTimeUtc(workspaceOptions));
        Assert.Contains(
            progress.Messages,
            message => message.Contains("устаревшие файлы current пропущены", StringComparison.OrdinalIgnoreCase));
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

        Assert.Contains("Profile executable was not found", exception.Message);
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

    [Fact]
    public async Task BuildAsyncRejectsManagedWorkspaceRootAsProfileWorkspace()
    {
        var profile = CreateProfile(CreateMod("mod", "mod"));
        profile.WorkspacePath = _workspaceRoot;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _builder.BuildAsync(_gamePath, profile, new ProgressLog()));

        Assert.Contains("profile-specific folder", exception.Message);
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
    public async Task BuildAsyncRefreshesUnusedGeneratedWorkspaceAfterProfileRename()
    {
        var profile = CreateProfile(CreateMod("mod", "mod"));
        profile.WorkspacePath = Path.Combine(_workspaceRoot, $"Profile 6-{profile.Id[..8]}");
        profile.Name = "Тень Чернобыля";

        var result = await _builder.BuildAsync(_gamePath, profile, new ProgressLog());

        Assert.EndsWith($"profile-{profile.Id}", result.ProfileWorkspacePath);
        Assert.False(Directory.Exists(profile.WorkspacePath));
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
    public async Task BuildAsyncBlocksLaunchWhenFsgameCannotIsolateProfileData()
    {
        var sourceFsgame = Path.Combine(_gamePath, "fsgame.ltx");
        const string original = "$game_data$ = true | true | gamedata\\";
        File.WriteAllText(sourceFsgame, original);
        var profile = CreateProfile(CreateMod("missing-app-data-root", "mod"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => _builder.BuildAsync(_gamePath, profile, new ProgressLog()));

        Assert.Contains("$app_data_root$", error.Message);
        Assert.Contains("launch was blocked", error.Message);
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
