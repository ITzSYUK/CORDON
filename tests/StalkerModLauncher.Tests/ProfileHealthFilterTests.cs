using System.Xml.Linq;
using StalkerModLauncher.Models;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ProfileHealthFilterTests
{
    [Theory]
    [InlineData(ProfileHealthStatus.Healthy, HealthCheckFilter.All, true)]
    [InlineData(ProfileHealthStatus.Warning, HealthCheckFilter.All, true)]
    [InlineData(ProfileHealthStatus.Error, HealthCheckFilter.All, true)]
    [InlineData(ProfileHealthStatus.Healthy, HealthCheckFilter.Warnings, false)]
    [InlineData(ProfileHealthStatus.Warning, HealthCheckFilter.Warnings, true)]
    [InlineData(ProfileHealthStatus.Error, HealthCheckFilter.Warnings, false)]
    [InlineData(ProfileHealthStatus.Healthy, HealthCheckFilter.Errors, false)]
    [InlineData(ProfileHealthStatus.Warning, HealthCheckFilter.Errors, false)]
    [InlineData(ProfileHealthStatus.Error, HealthCheckFilter.Errors, true)]
    public void MatchesFilterKeepsOnlyRequestedSeverities(
        ProfileHealthStatus status,
        HealthCheckFilter filter,
        bool expected)
    {
        var check = new ProfileHealthCheck(status, "Title", "Details");

        Assert.Equal(expected, ProfileHealthViewModel.MatchesFilter(check, filter));
    }

    [Theory]
    [InlineData(HealthCheckFilter.All, HealthCheckFilter.Warnings, HealthCheckFilter.Warnings)]
    [InlineData(HealthCheckFilter.All, HealthCheckFilter.Errors, HealthCheckFilter.Errors)]
    [InlineData(HealthCheckFilter.Warnings, HealthCheckFilter.Warnings, HealthCheckFilter.All)]
    [InlineData(HealthCheckFilter.Errors, HealthCheckFilter.Errors, HealthCheckFilter.All)]
    [InlineData(HealthCheckFilter.Warnings, HealthCheckFilter.Errors, HealthCheckFilter.Errors)]
    public void ToggleFilterReselectsActiveFilterBackToAll(
        HealthCheckFilter current,
        HealthCheckFilter requested,
        HealthCheckFilter expected)
    {
        Assert.Equal(expected, ProfileHealthViewModel.ToggleFilter(current, requested));
    }

    [Theory]
    [InlineData("Views/ProfileHealthWindow.xaml")]
    [InlineData("Views/Controls/PdaHealthView.xaml")]
    public void HealthViewsExposeFilteredChecksAndFilterButtons(string relativePath)
    {
        var projectRoot = FindProjectRoot();
        var document = XDocument.Load(Path.Combine(projectRoot, relativePath));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            document.Descendants(presentation + "ListBox"),
            element => ((string?)element.Attribute("ItemsSource"))?.Contains("VisibleChecks", StringComparison.Ordinal) == true);

        var filterButtons = document
            .Descendants(presentation + "Button")
            .Where(element => ((string?)element.Attribute("Command"))?.Contains("SetHealthFilterCommand", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(2, filterButtons.Length);
    }

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "StalkerModLauncher");
            if (File.Exists(Path.Combine(candidate, "StalkerModLauncher.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("StalkerModLauncher project root was not found.");
    }
}
