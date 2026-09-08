using StalkerModLauncher.ViewModels;

namespace StalkerModLauncher.Services;

public sealed class AppServices : IDisposable
{
    private bool _disposed;

    public AppServices()
    {
        Paths = AppPaths.Current;
        SettingsStore = new SettingsStore(Paths);
        DialogService = new DialogService();
        LauncherUpdateService = new LauncherUpdateService();

        var workspaceBuilder = new WorkspaceBuilder(Paths);
        WorkspaceManagementService = new WorkspaceManagementService(workspaceBuilder);
        ProfileManager = new ProfileManager(Paths, workspaceBuilder);
        var launchBackends = new List<IProfileLaunchBackend>
        {
            new LinkedWorkspaceLaunchBackend(workspaceBuilder)
        };
        if (UsvfsFeatureGate.IsEnabled())
        {
            launchBackends.Add(new UsvfsLaunchBackend(
                new UsvfsRuntime(new OfficialUsvfsNativeApi()),
                x86Runtime: new X86UsvfsHostRuntime()));
        }

        LaunchCoordinator = new LaunchCoordinator(
            new ProfileLauncher(
                launchBackends,
                profileManager: ProfileManager),
            new GameSessionTracker(),
            new GameLaunchReadinessMonitor());
        LaunchPreflightService = new LaunchPreflightService(ProfileManager);
        ApplicationLogService = new ApplicationLogService(Paths);
        StartupRegistrationService = new StartupRegistrationService();
        ModConflictAnalyzer = new ModConflictAnalyzer();
        ScreenshotClipboardService = new ScreenshotClipboardService();
        ApProCatalogService = new ApProCatalogService();
        ProfileHealthService = new ProfileHealthService(ProfileManager);
        WindowNavigationService = new WindowNavigationService(
            DialogService,
            SettingsStore,
            ProfileHealthService,
            WorkspaceManagementService,
            ScreenshotClipboardService,
            ApProCatalogService,
            LauncherUpdateService);
    }

    public AppPaths Paths { get; }
    public SettingsStore SettingsStore { get; }
    public DialogService DialogService { get; }
    public LauncherUpdateService LauncherUpdateService { get; }
    public ProfileManager ProfileManager { get; }
    public LaunchCoordinator LaunchCoordinator { get; }
    public LaunchPreflightService LaunchPreflightService { get; }
    public WorkspaceManagementService WorkspaceManagementService { get; }
    public ApplicationLogService ApplicationLogService { get; }
    public IStartupRegistrationService StartupRegistrationService { get; }
    public ModConflictAnalyzer ModConflictAnalyzer { get; }
    public ProfileHealthService ProfileHealthService { get; }
    public ScreenshotClipboardService ScreenshotClipboardService { get; }
    public ApProCatalogService ApProCatalogService { get; }
    public WindowNavigationService WindowNavigationService { get; }

    public MainViewModel CreateMainViewModel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new MainViewModel(
            Paths,
            SettingsStore,
            LaunchCoordinator,
            DialogService,
            ModConflictAnalyzer,
            ProfileManager,
            LaunchPreflightService,
            ApplicationLogService,
            StartupRegistrationService);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ApProCatalogService.Dispose();
        SettingsStore.Dispose();
    }
}
