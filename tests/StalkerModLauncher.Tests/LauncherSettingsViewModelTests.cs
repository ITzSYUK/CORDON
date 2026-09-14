using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using System.Net.Http;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class LauncherSettingsViewModelTests
{
    [Fact]
    public async Task TrySavePassesAllLauncherPreferences()
    {
        LauncherPreferences? saved = null;
        var viewModel = new LauncherSettingsViewModel(
            LauncherPreferences.Default,
            @"C:\Logs",
            preferences =>
            {
                saved = preferences;
                return Task.CompletedTask;
            },
            new DialogService());

        viewModel.UsePdaInterface = true;
        Assert.True(viewModel.UsePdaInterface);
        viewModel.UseNewPdaInterface = true;
        viewModel.ShowTrayIcon = true;
        viewModel.StartWithWindows = true;
        viewModel.StartMinimizedToTrayOnWindowsStartup = false;
        viewModel.MinimizeToTrayOnClose = true;
        viewModel.AutoCheckForUpdates = false;
        viewModel.ShowUpdateNotifications = false;
        viewModel.LogLevel = LauncherLogLevel.Detailed;

        Assert.True(await viewModel.TrySaveAsync());
        Assert.Equal(new LauncherPreferences(
            IsPdaInterfaceEnabled: true,
            UseNewPdaInterface: true,
            ShowTrayIcon: true,
            StartWithWindows: true,
            StartMinimizedToTrayOnWindowsStartup: false,
            MinimizeToTrayOnClose: true,
            AutoCheckForUpdates: false,
            ShowUpdateNotifications: false,
            LauncherLogLevel.Detailed), saved);
        Assert.False(viewModel.UseClassicInterface);
        Assert.False(viewModel.UsePdaInterface);
        Assert.True(viewModel.UseNewPdaInterface);
    }

    [Fact]
    public async Task CheckForUpdatesReportsAvailableRelease()
    {
        var viewModel = new LauncherSettingsViewModel(
            LauncherPreferences.Default,
            @"C:\Logs",
            _ => Task.CompletedTask,
            new DialogService(),
            () => Task.FromResult(new LauncherUpdateResult(
                "1.0.0",
                "v1.1.0",
                "https://github.com/ITzSYUK/CORDON/releases/tag/v1.1.0",
                IsUpdateAvailable: true)));

        await viewModel.CheckForUpdatesAsync();

        Assert.True(viewModel.HasAvailableUpdate);
        Assert.Contains("v1.1.0", viewModel.UpdateStatus);
        Assert.True(viewModel.OpenReleaseCommand.CanExecute(null));
        Assert.True(viewModel.CanShowDownloadButton);

        viewModel.ShowDownloadOptionsCommand.Execute(null);

        Assert.True(viewModel.AreDownloadOptionsVisible);
        Assert.False(viewModel.CanShowDownloadButton);
        Assert.True(viewModel.DownloadMinimalCommand.CanExecute(null));
        Assert.True(viewModel.DownloadStandaloneCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckForUpdatesReportsConnectionFailureWithoutThrowing()
    {
        var viewModel = new LauncherSettingsViewModel(
            LauncherPreferences.Default,
            @"C:\Logs",
            _ => Task.CompletedTask,
            new DialogService(),
            () => throw new HttpRequestException("Offline"));

        await viewModel.CheckForUpdatesAsync();

        Assert.False(viewModel.HasAvailableUpdate);
        Assert.Contains("Не удалось подключиться", viewModel.UpdateStatus);
        Assert.False(viewModel.OpenReleaseCommand.CanExecute(null));
    }

    [Fact]
    public async Task DownloadedReleaseEnablesOpeningDownloadsFolder()
    {
        var exitRequested = false;
        var viewModel = new LauncherSettingsViewModel(
            LauncherPreferences.Default,
            @"C:\Logs",
            _ => Task.CompletedTask,
            new DialogService(),
            () => Task.FromResult(new LauncherUpdateResult(
                "1.0.0",
                "v1.1.0",
                "https://github.com/ITzSYUK/CORDON/releases/tag/v1.1.0",
                IsUpdateAvailable: true)),
            downloadReleasePackage: (_, _, package) =>
            {
                Assert.Equal(LauncherReleasePackage.Minimal, package);
                return Task.FromResult(@"C:\CORDON\CORDON-v1.1.0-win-x64.zip");
            },
            confirmInstall: _ => true,
            requestExit: () => exitRequested = true);

        await viewModel.CheckForUpdatesAsync();
        viewModel.DownloadMinimalCommand.Execute(null);

        Assert.True(viewModel.HasDownloadedRelease);
        Assert.True(viewModel.OpenDownloadsCommand.CanExecute(null));
        Assert.Contains("готов к установке", viewModel.UpdateStatus);
        Assert.True(exitRequested);
    }

    [Theory]
    [InlineData(false, LauncherReleasePackage.Standalone)]
    [InlineData(true, LauncherReleasePackage.Minimal)]
    public async Task PackageMismatchRequiresConfirmationBeforeDownload(
        bool isStandaloneBuild,
        LauncherReleasePackage selectedPackage)
    {
        string? warning = null;
        var downloadCalls = 0;
        var viewModel = new LauncherSettingsViewModel(
            LauncherPreferences.Default,
            @"C:\Logs",
            _ => Task.CompletedTask,
            new DialogService(),
            () => Task.FromResult(new LauncherUpdateResult(
                "1.0.0",
                "v1.1.0",
                "https://github.com/ITzSYUK/CORDON/releases/tag/v1.1.0",
                IsUpdateAvailable: true)),
            downloadReleasePackage: (_, _, _) =>
            {
                downloadCalls++;
                return Task.FromResult(@"C:\CORDON\update.zip");
            },
            isStandaloneBuild: isStandaloneBuild,
            confirmInstall: message =>
            {
                warning = message;
                return false;
            });

        await viewModel.CheckForUpdatesAsync();
        (selectedPackage == LauncherReleasePackage.Standalone
            ? viewModel.DownloadStandaloneCommand
            : viewModel.DownloadMinimalCommand).Execute(null);

        Assert.NotNull(warning);
        Assert.Contains("ВНИМАНИЕ", warning);
        Assert.Contains(isStandaloneBuild ? "выбрана обычная" : "запущена обычная версия", warning);
        Assert.Contains("Standalone", warning);
        Assert.Equal(0, downloadCalls);
    }

    [Fact]
    public async Task UpdateIsBlockedWhileGameOrLauncherOperationIsRunning()
    {
        var downloadCalls = 0;
        var viewModel = new LauncherSettingsViewModel(
            LauncherPreferences.Default,
            @"C:\Logs",
            _ => Task.CompletedTask,
            new DialogService(),
            () => Task.FromResult(new LauncherUpdateResult(
                "1.0.0",
                "v1.1.0",
                "https://github.com/ITzSYUK/CORDON/releases/tag/v1.1.0",
                IsUpdateAvailable: true)),
            downloadReleasePackage: (_, _, _) =>
            {
                downloadCalls++;
                return Task.FromResult(@"C:\CORDON\update.zip");
            },
            confirmInstall: _ => true,
            canInstallUpdate: () => false);

        await viewModel.CheckForUpdatesAsync();
        viewModel.DownloadMinimalCommand.Execute(null);

        Assert.Equal(0, downloadCalls);
        Assert.Contains("завершите запущенную игру", viewModel.UpdateStatus);
    }

    [Fact]
    public async Task DownloadReportsGitHubHttpStatusInsteadOfConnectionFailure()
    {
        var viewModel = new LauncherSettingsViewModel(
            LauncherPreferences.Default,
            @"C:\Logs",
            _ => Task.CompletedTask,
            new DialogService(),
            () => Task.FromResult(new LauncherUpdateResult(
                "1.0.0",
                "v1.1.0",
                "https://github.com/ITzSYUK/CORDON/releases/tag/v1.1.0",
                IsUpdateAvailable: true)),
            downloadReleasePackage: (_, _, _) => throw new HttpRequestException(
                "Not found",
                inner: null,
                statusCode: System.Net.HttpStatusCode.NotFound),
            confirmInstall: _ => true);

        await viewModel.CheckForUpdatesAsync();
        viewModel.DownloadMinimalCommand.Execute(null);

        Assert.Contains("HTTP 404", viewModel.UpdateStatus);
    }

    [Fact]
    public void ResetRestoresVisibleDefaultsAfterConfirmationWithoutSaving()
    {
        var saveCalls = 0;
        var viewModel = new LauncherSettingsViewModel(
            new LauncherPreferences(
                IsPdaInterfaceEnabled: true,
                UseNewPdaInterface: true,
                ShowTrayIcon: false,
                StartWithWindows: true,
                StartMinimizedToTrayOnWindowsStartup: false,
                MinimizeToTrayOnClose: true,
                AutoCheckForUpdates: false,
                ShowUpdateNotifications: false,
                LauncherLogLevel.Detailed),
            @"C:\Logs",
            _ =>
            {
                saveCalls++;
                return Task.CompletedTask;
            },
            new DialogService(),
            confirmReset: () => true);

        viewModel.ResetCommand.Execute(null);

        Assert.True(viewModel.UseClassicInterface);
        Assert.False(viewModel.UseNewPdaInterface);
        Assert.True(viewModel.ShowTrayIcon);
        Assert.False(viewModel.StartWithWindows);
        Assert.True(viewModel.StartMinimizedToTrayOnWindowsStartup);
        Assert.False(viewModel.MinimizeToTrayOnClose);
        Assert.True(viewModel.AutoCheckForUpdates);
        Assert.True(viewModel.ShowUpdateNotifications);
        Assert.Equal(LauncherLogLevel.Standard, viewModel.LogLevel);
        Assert.Equal(0, saveCalls);
    }

    [Fact]
    public void ResetLeavesValuesUntouchedWhenConfirmationIsDeclined()
    {
        var preferences = LauncherPreferences.Default with { StartWithWindows = true };
        var viewModel = new LauncherSettingsViewModel(
            preferences,
            @"C:\Logs",
            _ => Task.CompletedTask,
            new DialogService(),
            confirmReset: () => false);

        viewModel.ResetCommand.Execute(null);

        Assert.True(viewModel.StartWithWindows);
    }

    [Fact]
    public void HidingTrayIconDisablesTrayOnlyBehavior()
    {
        var viewModel = new LauncherSettingsViewModel(
            LauncherPreferences.Default with
            {
                StartWithWindows = true,
                StartMinimizedToTrayOnWindowsStartup = true,
                MinimizeToTrayOnClose = true
            },
            @"C:\Logs",
            _ => Task.CompletedTask,
            new DialogService());

        viewModel.ShowTrayIcon = false;

        Assert.False(viewModel.CanUseTray);
        Assert.False(viewModel.CanStartMinimizedToTray);
        Assert.False(viewModel.StartMinimizedToTrayOnWindowsStartup);
        Assert.False(viewModel.MinimizeToTrayOnClose);
    }
}
