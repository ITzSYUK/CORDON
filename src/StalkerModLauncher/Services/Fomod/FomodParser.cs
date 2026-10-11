using System.Xml.Linq;

namespace StalkerModLauncher.Services.Fomod;

/// <summary>
/// Locates a FOMOD script inside already extracted staging content.
/// Case-insensitive, following the FOMOD convention that the fomod folder
/// name casing is not significant.
/// </summary>
public static class FomodDetector
{
    public static FomodLocation? TryLocate(string stagingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);

        var current = stagingPath;
        for (var depth = 0; depth <= 6; depth++)
        {
            if (TryReadFolder(current) is { } root) return root;
            var directories = SafeEnumerateDirectories(current);
            var candidates = directories.Select(TryReadFolder).OfType<FomodLocation>().ToArray();
            if (candidates.Length > 1) throw new InvalidDataException(Resources.Strings.Archive_MultipleRoots);
            if (candidates.Length == 1) return candidates[0];
            if (directories.Length != 1) return null;
            current = directories[0];
        }
        return null;
    }

    private static FomodLocation? TryReadFolder(string packageRoot)
    {
        var fomodDirectory = FindEntryIgnoreCase(packageRoot, "fomod", directoryEntry: true);
        if (fomodDirectory is null)
        {
            return null;
        }

        var configPath = FindEntryIgnoreCase(fomodDirectory, "ModuleConfig.xml", directoryEntry: false);
        var scriptPath = FindEntryIgnoreCase(fomodDirectory, "script.cs", directoryEntry: false);

        if (configPath is null && scriptPath is null)
        {
            return null;
        }

        return new FomodLocation(packageRoot, configPath, scriptPath is not null);
    }

    private static string? FindEntryIgnoreCase(string directory, string name, bool directoryEntry)
    {
        try
        {
            var entries = directoryEntry
                ? Directory.EnumerateDirectories(directory)
                : Directory.EnumerateFiles(directory);
            return entries.FirstOrDefault(entry =>
                Path.GetFileName(entry).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[] SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// Parses declarative FOMOD ModuleConfig.xml into <see cref="FomodModule"/>.
/// </summary>
public static class FomodParser
{
    public static FomodModule Parse(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        XDocument document;
        try
        {
            using var reader = System.Xml.XmlReader.Create(configPath, new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 8 * 1024 * 1024
            });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, ex.Message),
                ex);
        }

        var root = document.Root;
        if (root is null || !root.Name.LocalName.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                Resources.LocalizedText.Format(
                    Resources.Strings.Archive_FomodInvalidFormat,
                    Resources.Strings.Archive_FomodInvalidRoot));
        }

        try
        {
            return ParseModule(root);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, ex.Message),
                ex);
        }
    }

    private static FomodModule ParseModule(XElement root)
    {
        var name = ChildValue(root, "moduleName");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException(
                Resources.LocalizedText.Format(
                    Resources.Strings.Archive_FomodInvalidFormat,
                    Resources.Strings.Archive_FomodNameMissing));
        }

        // Capture file order before sorting steps, groups and plugins for display.
        var documentOrder = 0;
        foreach (var element in root.Descendants().Where(element =>
                     element.Name.LocalName.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                     element.Name.LocalName.Equals("folder", StringComparison.OrdinalIgnoreCase)))
        {
            element.AddAnnotation(documentOrder++);
        }

        var required = root.Elements()
            .Where(e => e.Name.LocalName.Equals("requiredInstallFiles", StringComparison.OrdinalIgnoreCase))
            .SelectMany(ParseFileList)
            .ToArray();

        var steps = root.Elements()
            .Where(e => e.Name.LocalName.Equals("installSteps", StringComparison.OrdinalIgnoreCase))
            .SelectMany(element => ParseOrdered(element, "installStep", ParseStep))
            .ToArray();

        var conditional = root.Elements()
            .Where(e => e.Name.LocalName.Equals("conditionalFileInstalls", StringComparison.OrdinalIgnoreCase))
            .SelectMany(element => element.Elements()
                .Where(p => p.Name.LocalName.Equals("patterns", StringComparison.OrdinalIgnoreCase))
                .SelectMany(patterns => patterns.Elements()
                    .Where(p => p.Name.LocalName.Equals("pattern", StringComparison.OrdinalIgnoreCase))
                    .Select(ParseConditionalPattern)))
            .ToArray();

        return new FomodModule(name.Trim(), required, steps, conditional,
            root.ElementLocal("moduleDependencies") is { } dependencies ? ParseDependencies(dependencies) : null,
            root.ElementLocal("moduleImage")?.Attribute("path")?.Value ?? string.Empty);
    }

    private static FomodStep ParseStep(XElement element)
    {
        var name = element.Attribute("name")?.Value ?? string.Empty;
        var visible = element.Elements()
            .Where(e => e.Name.LocalName.Equals("visible", StringComparison.OrdinalIgnoreCase))
            .Select(element => ParseDependencies(element))
            .FirstOrDefault();
        var groups = element.Elements()
            .Where(e => e.Name.LocalName.Equals("optionalFileGroups", StringComparison.OrdinalIgnoreCase))
            .SelectMany(groupsElement => ParseOrdered(groupsElement, "group", ParseGroup))
            .ToArray();
        return new FomodStep(name, visible, groups);
    }

    private static FomodGroup ParseGroup(XElement element)
    {
        var name = element.Attribute("name")?.Value ?? string.Empty;
        var type = ParseGroupType(element.Attribute("type")?.Value);
        var plugins = element.Elements()
            .Where(e => e.Name.LocalName.Equals("plugins", StringComparison.OrdinalIgnoreCase))
            .SelectMany(pluginsElement => ParseOrdered(pluginsElement, "plugin", ParsePlugin))
            .ToArray();
        return new FomodGroup(name, type, plugins);
    }

    private static FomodPlugin ParsePlugin(XElement element)
    {
        var name = element.Attribute("name")?.Value ?? string.Empty;
        var description = ChildValue(element, "description") ?? string.Empty;
        var imagePath = element.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("path")?.Value ?? string.Empty;
        var (baseType, patterns) = ParseTypeDescriptor(element);
        var files = element.Elements()
            .Where(e => e.Name.LocalName.Equals("files", StringComparison.OrdinalIgnoreCase))
            .SelectMany(ParseFileListWithFlags)
            .ToArray();
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var flagsElement in element.Elements()
                     .Where(e => e.Name.LocalName.Equals("conditionFlags", StringComparison.OrdinalIgnoreCase))
                     .SelectMany(f => f.Elements()
                         .Where(c => c.Name.LocalName.Equals("flag", StringComparison.OrdinalIgnoreCase))))
        {
            var flagName = flagsElement.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(flagName))
            {
                flags[flagName] = flagsElement.Value ?? string.Empty;
            }
        }

        return new FomodPlugin(name, description.Trim(), baseType, patterns, files, flags, imagePath.Trim());
    }

    private static (FomodPluginType BaseType, IReadOnlyList<FomodTypePattern> Patterns) ParseTypeDescriptor(XElement plugin)
    {
        var descriptor = plugin.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("typeDescriptor", StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return (FomodPluginType.Optional, []);
        }

        var direct = descriptor.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return (ParsePluginType(direct.Attribute("name")?.Value), []);
        }

        var dependencyType = descriptor.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("dependencyType", StringComparison.OrdinalIgnoreCase));
        if (dependencyType is null)
        {
            return (FomodPluginType.Optional, []);
        }

        var defaultType = ParsePluginType(dependencyType.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("defaultType", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("name")?.Value);
        var patterns = dependencyType.Elements()
            .Where(e => e.Name.LocalName.Equals("patterns", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Elements()
                .Where(x => x.Name.LocalName.Equals("pattern", StringComparison.OrdinalIgnoreCase)))
            .Select(pattern => new FomodTypePattern(
                ParseDependencies(pattern.ElementLocal("dependencies")),
                ParsePluginType(pattern.ElementLocal("type")?.Attribute("name")?.Value)))
            .ToArray();
        return (defaultType, patterns);
    }

    private static FomodConditionalPattern ParseConditionalPattern(XElement element) =>
        new(
            ParseDependencies(element.ElementLocal("dependencies")),
            element.Elements()
                .Where(e => e.Name.LocalName.Equals("files", StringComparison.OrdinalIgnoreCase))
                .SelectMany(ParseFileListWithFlags)
                .ToArray());

    private static IEnumerable<FomodFileEntry> ParseFileList(XElement filesRoot) =>
        filesRoot.Elements().SelectMany(ParseFileOrFolder);

    private static IEnumerable<FomodFileEntry> ParseFileListWithFlags(XElement filesRoot) =>
        filesRoot.Elements().SelectMany(ParseFileOrFolder);

    private static IEnumerable<FomodFileEntry> ParseFileOrFolder(XElement element)
    {
        var local = element.Name.LocalName;
        if (!local.Equals("file", StringComparison.OrdinalIgnoreCase) &&
            !local.Equals("folder", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, local));
        }

        var source = element.Attribute("source")?.Value;
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, local));
        }

        var destination = element.Attribute("destination")?.Value ?? source;
        var priority = element.Attribute("priority") is { } priorityAttribute
            ? int.Parse(priorityAttribute.Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        yield return new FomodFileEntry(
            source.Trim(),
            destination.Trim(),
            priority,
            IsTrue(element.Attribute("alwaysInstall")?.Value),
            IsTrue(element.Attribute("installIfUsable")?.Value))
        {
            DocumentOrder = element.Annotation(typeof(int)) is int order ? order : 0
        };
    }

    private static FomodCondition ParseDependencies(XElement? dependenciesRoot, int depth = 0)
    {
        if (depth > 64)
        {
            throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, "dependencies"));
        }
        if (dependenciesRoot is null)
        {
            return new FomodAlwaysCondition();
        }

        var op = dependenciesRoot.Attribute("operator")?.Value;
        if (op is not null && !op.Equals("And", StringComparison.OrdinalIgnoreCase) && !op.Equals("Or", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, op));
        }
        var children = dependenciesRoot.Elements().Select(child => ParseDependencyChild(child, depth + 1)).ToArray();
        if (children.Length == 0)
        {
            return new FomodAlwaysCondition();
        }

        return new FomodCompositeCondition(
            string.Equals(op, "Or", StringComparison.OrdinalIgnoreCase)
                ? FomodConditionOperator.Or
                : FomodConditionOperator.And,
            children);
    }

    private static FomodCondition ParseDependencyChild(XElement element, int depth)
    {
        var local = element.Name.LocalName;
        if (local.Equals("dependencies", StringComparison.OrdinalIgnoreCase))
        {
            return ParseDependencies(element, depth);
        }

        if (local.Equals("flagDependency", StringComparison.OrdinalIgnoreCase))
        {
            return new FomodFlagCondition(
                element.Attribute("flag")?.Value ?? string.Empty,
                element.Attribute("value")?.Value ?? string.Empty);
        }

        if (local.Equals("fileDependency", StringComparison.OrdinalIgnoreCase))
        {
            return new FomodFileCondition(
                element.Attribute("file")?.Value ?? string.Empty,
                element.Attribute("state")?.Value ?? string.Empty);
        }

        if (local.Equals("gameDependency", StringComparison.OrdinalIgnoreCase) ||
            local.Equals("fommDependency", StringComparison.OrdinalIgnoreCase) ||
            local.Equals("foseDependency", StringComparison.OrdinalIgnoreCase) ||
            local.Equals("versionDependency", StringComparison.OrdinalIgnoreCase))
        {
            return new FomodVersionCondition(local, element.Attribute("version")?.Value ?? string.Empty);
        }

        throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, local));
    }

    private static FomodGroupType ParseGroupType(string? value) =>
        value switch
        {
            _ when string.Equals(value, "SelectExactlyOne", StringComparison.OrdinalIgnoreCase) =>
                FomodGroupType.SelectExactlyOne,
            _ when string.Equals(value, "SelectAtMostOne", StringComparison.OrdinalIgnoreCase) =>
                FomodGroupType.SelectAtMostOne,
            _ when string.Equals(value, "SelectAtLeastOne", StringComparison.OrdinalIgnoreCase) =>
                FomodGroupType.SelectAtLeastOne,
            _ when string.Equals(value, "SelectAll", StringComparison.OrdinalIgnoreCase) =>
                FomodGroupType.SelectAll,
            _ when value is null || string.Equals(value, "SelectAny", StringComparison.OrdinalIgnoreCase) => FomodGroupType.SelectAny,
            _ => throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, value)),
        };

    private static FomodPluginType ParsePluginType(string? value) =>
        value switch
        {
            _ when string.Equals(value, "Required", StringComparison.OrdinalIgnoreCase) =>
                FomodPluginType.Required,
            _ when string.Equals(value, "Recommended", StringComparison.OrdinalIgnoreCase) =>
                FomodPluginType.Recommended,
            _ when string.Equals(value, "NotUsable", StringComparison.OrdinalIgnoreCase) =>
                FomodPluginType.NotUsable,
            _ when string.Equals(value, "CouldBeUsable", StringComparison.OrdinalIgnoreCase) =>
                FomodPluginType.CouldBeUsable,
            _ when value is null || string.Equals(value, "Optional", StringComparison.OrdinalIgnoreCase) => FomodPluginType.Optional,
            _ => throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, value)),
        };

    private static bool IsTrue(string? value) => value switch
    {
        null or "0" => false,
        "1" => true,
        _ when value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        _ when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        _ => throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, value))
    };

    private static string? ChildValue(XElement parent, string localName) =>
        parent.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static XElement? ElementLocal(this XElement parent, string localName) =>
        parent.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static T[] ParseOrdered<T>(
        XElement container,
        string itemLocalName,
        Func<XElement, T> parse)
        where T : notnull
    {
        var items = container.Elements()
            .Where(e => e.Name.LocalName.Equals(itemLocalName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var order = container.Attribute("order")?.Value;
        if (order is not null && !order.Equals("Explicit", StringComparison.OrdinalIgnoreCase) &&
            !order.Equals("Ascending", StringComparison.OrdinalIgnoreCase) && !order.Equals("Descending", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Resources.LocalizedText.Format(Resources.Strings.Archive_FomodInvalidFormat, order));
        }
        if (!string.Equals(order, "Explicit", StringComparison.OrdinalIgnoreCase) &&
            typeof(T) != typeof(FomodFileEntry))
        {
            // Non-explicit order lists alphabetically by name.
            Array.Sort(items, (left, right) => string.Compare(
                left.Attribute("name")?.Value ?? string.Empty,
                right.Attribute("name")?.Value ?? string.Empty,
                StringComparison.Ordinal));
            if (string.Equals(order, "Descending", StringComparison.OrdinalIgnoreCase))
            {
                Array.Reverse(items);
            }
        }

        return items.Select(parse).ToArray();
    }
}
