namespace StalkerModLauncher.Services.Fomod;

/// <summary>
/// Default (non-interactive) FOMOD answers: Required options always on,
/// Recommended options pre-selected, Optional off, NotUsable never.
/// The wizard and installer share dependency evaluation and selection validation.
/// </summary>
public static class FomodSelector
{
    /// <summary>
    /// Selected plugin indices per (step, group), evaluated in script order so
    /// condition flags set by earlier choices apply to later type descriptors.
    /// </summary>
    public sealed class Selection
    {
        public Func<string, string>? FileState { get; init; }
        public Dictionary<(int Step, int Group), HashSet<int>> Plugins { get; } = new();

        public Dictionary<string, string> Flags { get; } = new(StringComparer.Ordinal);

        public bool IsSelected(int step, int group, int plugin) =>
            Plugins.TryGetValue((step, group), out var set) && set.Contains(plugin);
    }

    public static Selection CreateDefaults(FomodModule module, Func<string, string>? fileState = null)
    {
        ArgumentNullException.ThrowIfNull(module);

        var selection = new Selection { FileState = fileState };
        EnsureDependencies(module, selection);
        for (var stepIndex = 0; stepIndex < module.Steps.Count; stepIndex++)
        {
            var step = module.Steps[stepIndex];
            if (step.Visible is not null && !Evaluate(step.Visible, selection.Flags, fileState))
            {
                continue;
            }

            for (var groupIndex = 0; groupIndex < step.Groups.Count; groupIndex++)
            {
                var group = step.Groups[groupIndex];
                var chosen = ChooseDefaults(group, selection.Flags, fileState);
                if (chosen.Count > 0)
                {
                    selection.Plugins[(stepIndex, groupIndex)] = chosen;
                    foreach (var pluginIndex in chosen.Order())
                    {
                        foreach (var flag in group.Plugins[pluginIndex].Flags)
                        {
                            selection.Flags[flag.Key] = flag.Value;
                        }
                    }
                }
            }
        }

        return selection;
    }

    public static IReadOnlyList<FomodFileEntry> ResolveFiles(FomodModule module, Selection selection)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(selection);
        EnsureDependencies(module, selection);

        var collected = new List<FomodFileEntry>(module.RequiredFiles);
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var stepIndex = 0; stepIndex < module.Steps.Count; stepIndex++)
        {
            var step = module.Steps[stepIndex];
            var visible = step.Visible is null || Evaluate(step.Visible, flags, selection.FileState);
            for (var groupIndex = 0; groupIndex < step.Groups.Count; groupIndex++)
            {
                var group = step.Groups[groupIndex];
                var types = group.Plugins.Select(plugin => EffectiveType(plugin, flags, selection.FileState)).ToArray();
                var chosen = Enumerable.Range(0, group.Plugins.Count)
                    .Where(index => visible && selection.IsSelected(stepIndex, groupIndex, index)).ToHashSet();
                if (visible && ValidationError(group, types, chosen) is { } error)
                {
                    throw new InvalidDataException(error);
                }
                for (var pluginIndex = 0; pluginIndex < group.Plugins.Count; pluginIndex++)
                {
                    var plugin = group.Plugins[pluginIndex];
                    var effective = types[pluginIndex];
                    var selected = chosen.Contains(pluginIndex);
                    foreach (var file in plugin.Files)
                    {
                        if (selected || file.AlwaysInstall || (file.InstallIfUsable && effective != FomodPluginType.NotUsable))
                        {
                            collected.Add(file);
                        }
                    }
                }
                foreach (var index in chosen.Order())
                {
                    foreach (var flag in group.Plugins[index].Flags)
                    {
                        flags[flag.Key] = flag.Value;
                    }
                }
            }
        }

        foreach (var pattern in module.ConditionalPatterns)
        {
            if (Evaluate(pattern.Dependencies, flags, selection.FileState))
            {
                collected.AddRange(pattern.Files);
            }
        }

        // Higher priority installs later; equal priorities retain XML order, not display order.
        return collected
            .OrderBy(file => file.Priority)
            .ThenBy(file => file.DocumentOrder)
            .ToArray();
    }

    public static bool Evaluate(FomodCondition condition, IReadOnlyDictionary<string, string> flags, Func<string, string>? fileState = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(flags);

        return condition switch
        {
            FomodAlwaysCondition => true,
            FomodVersionCondition => false,
            FomodFlagCondition flag => (flags.GetValueOrDefault(flag.Flag) ?? string.Empty).Equals(flag.Value, StringComparison.Ordinal),
            FomodFileCondition file => string.Equals(file.State, fileState?.Invoke(file.File) ?? "Missing", StringComparison.OrdinalIgnoreCase),
            FomodCompositeCondition composite => composite.Operator == FomodConditionOperator.And
                ? composite.Children.All(child => Evaluate(child, flags, fileState))
                : composite.Children.Any(child => Evaluate(child, flags, fileState)),
            _ => false
        };
    }

    internal static HashSet<int> ChooseDefaults(FomodGroup group, IReadOnlyDictionary<string, string> flags, Func<string, string>? fileState = null)
    {
        var effective = group.Plugins.Select(plugin => EffectiveType(plugin, flags, fileState)).ToArray();
        var usable = Enumerable.Range(0, group.Plugins.Count)
            .Where(i => effective[i] != FomodPluginType.NotUsable)
            .ToArray();
        var required = usable.Where(i => effective[i] == FomodPluginType.Required).ToArray();
        var recommended = usable.Where(i => effective[i] == FomodPluginType.Recommended).ToArray();

        return group.Type switch
        {
            FomodGroupType.SelectAll => usable.ToHashSet(),
            FomodGroupType.SelectExactlyOne when required.Length > 0 => required.ToHashSet(),
            FomodGroupType.SelectExactlyOne when recommended.Length > 0 => recommended.Take(1).ToHashSet(),
            FomodGroupType.SelectExactlyOne => usable.Take(1).ToHashSet(),
            FomodGroupType.SelectAtMostOne when required.Length > 0 => required.ToHashSet(),
            FomodGroupType.SelectAtMostOne when recommended.Length > 0 => recommended.Take(1).ToHashSet(),
            FomodGroupType.SelectAtMostOne => new HashSet<int>(),
            FomodGroupType.SelectAtLeastOne when required.Length > 0 || recommended.Length > 0 =>
                required.Concat(recommended).ToHashSet(),
            FomodGroupType.SelectAtLeastOne => usable.Take(1).ToHashSet(),
            _ => required.Concat(recommended).ToHashSet(),
        };
    }

    public static FomodPluginType EffectiveType(FomodPlugin plugin, IReadOnlyDictionary<string, string> flags, Func<string, string>? fileState = null)
    {
        foreach (var pattern in plugin.TypePatterns)
        {
            if (Evaluate(pattern.Dependencies, flags, fileState))
            {
                return pattern.Type;
            }
        }

        return plugin.BaseType;
    }

    internal static void EnsureDependencies(FomodModule module, Selection selection)
    {
        if (module.Dependencies is not null && !Evaluate(module.Dependencies, new Dictionary<string, string>(), selection.FileState))
        {
            throw new InvalidDataException(Resources.Strings.Fomod_DependenciesNotMet);
        }
    }

    internal static string? ValidationError(FomodGroup group, IReadOnlyList<FomodPluginType> types, HashSet<int> chosen)
    {
        if (chosen.Any(index => types[index] == FomodPluginType.NotUsable) ||
            Enumerable.Range(0, types.Count).Any(index =>
                (types[index] == FomodPluginType.Required || group.Type == FomodGroupType.SelectAll && types[index] != FomodPluginType.NotUsable) && !chosen.Contains(index)))
        {
            return Resources.LocalizedText.Format(Resources.Strings.Fomod_InvalidSelectionFormat, group.Name);
        }
        if (group.Type is FomodGroupType.SelectExactlyOne or FomodGroupType.SelectAtLeastOne && chosen.Count == 0)
        {
            return Resources.LocalizedText.Format(Resources.Strings.Fomod_SelectAtLeastOneFormat, group.Name);
        }
        if (group.Type is FomodGroupType.SelectExactlyOne or FomodGroupType.SelectAtMostOne && chosen.Count > 1)
        {
            return Resources.LocalizedText.Format(Resources.Strings.Fomod_SelectAtMostOneFormat, group.Name);
        }
        return null;
    }
}
