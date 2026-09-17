using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using StalkerModLauncher.Resources;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed partial class LocalizationResourcesTests
{
    [Fact]
    public void EnglishAndRussianResourcesHaveTheSameValidContract()
    {
        var english = Read(CultureInfo.InvariantCulture);
        var russian = Read(CultureInfo.GetCultureInfo("ru"));
        var intentionallyShared = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Strings.Settings_ClassicUi),
            nameof(Strings.Settings_LanguageEnglish),
            nameof(Strings.Settings_PdaUi),
            nameof(Strings.Settings_PdaUi2)
        };

        Assert.Equal(english.Keys.Order(), russian.Keys.Order());
        Assert.All(english, item => Assert.False(string.IsNullOrWhiteSpace(item.Value)));
        Assert.All(russian, item => Assert.False(string.IsNullOrWhiteSpace(item.Value)));
        Assert.All(english, item => Assert.DoesNotMatch(CyrillicRegex(), item.Value));
        foreach (var key in english.Keys)
        {
            Assert.Equal(Placeholders(english[key]), Placeholders(russian[key]));
        }

        Assert.Equal(
            intentionallyShared.Order(),
            english.Keys.Where(key => english[key] == russian[key]).Order());
    }

    [Fact]
    public void EveryResourceKeyHasAStronglyTypedProperty()
    {
        var properties = typeof(Strings).GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Read(CultureInfo.InvariantCulture).Keys.Order(), properties.Order());
    }

    [Fact]
    public void EveryResourceKeyIsUsed()
    {
        var projectRoot = FindProjectRoot();
        var source = string.Join('\n', Directory
            .EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Resources{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));

        Assert.All(
            Read(CultureInfo.InvariantCulture).Keys,
            key => Assert.Contains($"Strings.{key}", source, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en-US", "Save")]
    [InlineData("ru-RU", "Сохранить")]
    [InlineData("de-DE", "Save")]
    public void ResourceManagerUsesCultureFallback(string cultureName, string expected)
    {
        Assert.Equal(expected, Strings.ResourceManager.GetString(
            nameof(Strings.Common_Save),
            CultureInfo.GetCultureInfo(cultureName)));
    }

    [Fact]
    public void UserFacingSourceDoesNotContainRawLocalizedText()
    {
        var projectRoot = FindProjectRoot();
        var allowedXamlText = new HashSet<string>(StringComparer.Ordinal)
        {
            "CORDON",
            "CORDON — PDA",
            "S.T.A.L.K.E.R. Mod Launcher",
            "AP-PRO.RU ©",
            "Workspace",
            "USVFS",
            "DX8",
            "DX9",
            "DX10",
            "DX11",
            "AVX",
            "ZIP",
            "MO2"
        };
        var localizedAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Text", "Content", "Header", "Title", "ToolTip", "Watermark",
            "AutomationProperties.Name", "AutomationProperties.HelpText"
        };

        foreach (var path in Directory.EnumerateFiles(projectRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(path);
            var rawValues = document.Root!.DescendantsAndSelf()
                .Attributes()
                .Where(attribute => localizedAttributes.Contains(attribute.Name.LocalName))
                .Select(attribute => attribute.Value)
                .Where(value => !value.StartsWith('{') && LetterRegex().IsMatch(value));
            Assert.All(rawValues, value => Assert.Contains(value, allowedXamlText));

            var rawTextNodes = document.Root.DescendantNodes()
                .OfType<XText>()
                .Select(node => node.Value.Trim())
                .Where(value => !value.StartsWith('#') && LetterRegex().IsMatch(value));
            Assert.Empty(rawTextNodes);
        }

        foreach (var path in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.EndsWith("Strings.Designer.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotMatch(RawUserFacingCallRegex(), source);
            Assert.DoesNotMatch(RawExceptionRegex(), source);

            var cyrillicLines = File.ReadLines(path)
                .Where(line => CyrillicRegex().IsMatch(line))
                .ToArray();
            if (path.EndsWith("ApProCatalogService.cs", StringComparison.OrdinalIgnoreCase))
            {
                Assert.All(cyrillicLines, line => Assert.Contains("просмотров", line));
            }
            else
            {
                Assert.Empty(cyrillicLines);
            }
        }
    }

    private static Dictionary<string, string> Read(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: true)!;
        return set.Cast<DictionaryEntry>().ToDictionary(
            item => (string)item.Key,
            item => (string)item.Value!,
            StringComparer.Ordinal);
    }

    private static int[] Placeholders(string value) => PlaceholderRegex().Matches(value)
        .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
        .Order()
        .ToArray();

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var projectRoot = Path.Combine(directory.FullName, "src", "StalkerModLauncher");
            if (File.Exists(Path.Combine(projectRoot, "StalkerModLauncher.csproj")))
            {
                return projectRoot;
            }
        }

        throw new DirectoryNotFoundException("StalkerModLauncher project root was not found.");
    }

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[^}]*)\}(?!\})")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex("[А-Яа-яЁё]")]
    private static partial Regex CyrillicRegex();

    [GeneratedRegex("[A-Za-zА-Яа-яЁё]")]
    private static partial Regex LetterRegex();

    [GeneratedRegex("\\b(?:Log|Report|Write|AppendLog|ShowError|ShowWarning|ShowInfo|PickFolder|PickExecutable|PickFile)\\s*\\(\\s*\\$?@?\"(?=[^\"\\r\\n]*[A-Za-zА-Яа-яЁё])", RegexOptions.Singleline)]
    private static partial Regex RawUserFacingCallRegex();

    [GeneratedRegex("throw\\s+new\\s+\\w+(?:Exception)?\\s*\\(\\s*\\$?@?\"(?=[^\"\\r\\n]*[A-Za-zА-Яа-яЁё])", RegexOptions.Singleline)]
    private static partial Regex RawExceptionRegex();
}
