using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ModOverlayTooltipTests
{
    [Fact]
    public void FormatModNamesLimitsLongLists()
    {
        var names = Enumerable.Range(1, 8).Select(index => $"Mod {index}").ToArray();

        Assert.Equal("Mod 1, Mod 2, Mod 3, Mod 4, Mod 5, Mod 6, …", MainViewModel.FormatModNames(names));
    }
}
