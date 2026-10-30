using StalkerModLauncher.Models;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class StandaloneLaunchBackendTests
{
    [Fact]
    public async Task DirectLaunchUsesOriginalBuildAndDoesNotCreateProfileFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "SmlDirectTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "game.exe");
            File.WriteAllText(executable, "fixture executable");
            File.WriteAllText(Path.Combine(root, "fsgame.ltx"), "$app_data_root$ = true | false | appdata");
            var profile = new ModProfile { IsStandalone = true, ExecutableRelativePath = "game.exe", LaunchArguments = " -test " };
            profile.Mods.Add(new ModEntry { SourcePath = root, IsEnabled = true });
            var plan = await new StandaloneLaunchBackend().PrepareAsync(new ProfileLaunchBackendContext(string.Empty, profile), new Progress<string>());
            Assert.Equal(LaunchBackendKind.Standalone, plan.BackendKind);
            Assert.Equal(executable, plan.ExecutablePath);
            Assert.Equal(root, plan.WorkingDirectory);
            Assert.Equal("-test", plan.Arguments);
            Assert.Null(plan.RuntimeLease);
            Assert.Empty(profile.WorkspacePath);
            Assert.Equal("$app_data_root$ = true | false | appdata", File.ReadAllText(Path.Combine(root, "fsgame.ltx")));
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
