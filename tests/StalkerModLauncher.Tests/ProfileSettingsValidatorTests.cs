using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileSettingsValidatorTests
{
    [Fact]
    public void ValidateAcceptsValidSettings()
    {
        var result = ProfileSettingsValidator.Validate("Zona", @"bin_x64\xrEngine.exe", _ => false);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateRejectsEmptyName()
    {
        var result = ProfileSettingsValidator.Validate("   ", @"bin\xr_3da.exe", _ => false);

        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, message => message.Contains("название", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateRejectsDuplicateName()
    {
        var result = ProfileSettingsValidator.Validate("Zona", @"bin\xr_3da.exe", name => name == "Zona");

        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, message => message.Contains("уже существует", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateRejectsExecutableOutsideProfile()
    {
        var result = ProfileSettingsValidator.Validate("Zona", @"..\outside.exe", _ => false);

        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, message => message.Contains("не должен выходить", StringComparison.OrdinalIgnoreCase));
    }
}
