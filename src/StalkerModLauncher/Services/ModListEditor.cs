using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

public static class ModListEditor
{
    public static ModEntry Add(ModProfile profile, string sourcePath, string? name = null)
    {
        var mod = new ModEntry
        {
            Name = name ?? Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            SourcePath = sourcePath,
            IsEnabled = true,
            Order = profile.Mods.Count + 1
        };

        profile.Mods.Add(mod);
        return mod;
    }

    public static int Remove(ModProfile profile, IEnumerable<ModEntry> mods)
    {
        var removed = 0;
        foreach (var mod in mods.Distinct().ToList())
        {
            if (profile.Mods.Remove(mod))
            {
                removed++;
            }
        }

        Renumber(profile);
        return removed;
    }

    public static bool Move(ModProfile profile, ModEntry source, ModEntry target)
    {
        var oldIndex = profile.Mods.IndexOf(source);
        var newIndex = profile.Mods.IndexOf(target);
        return MoveToIndex(profile, oldIndex, newIndex);
    }

    public static bool MoveByOffset(ModProfile profile, ModEntry source, int offset)
    {
        var oldIndex = profile.Mods.IndexOf(source);
        return MoveToIndex(profile, oldIndex, oldIndex + offset);
    }

    public static bool MoveToEnd(ModProfile profile, ModEntry source)
    {
        return MoveToIndex(profile, profile.Mods.IndexOf(source), profile.Mods.Count - 1);
    }

    public static bool MoveToInsertionIndex(ModProfile profile, ModEntry source, int insertionIndex)
    {
        return MoveManyToInsertionIndex(profile, [source], insertionIndex);
    }

    public static bool MoveManyToInsertionIndex(
        ModProfile profile,
        IEnumerable<ModEntry> sources,
        int insertionIndex)
    {
        var selected = sources
            .Where(profile.Mods.Contains)
            .Distinct()
            .ToHashSet();
        if (selected.Count == 0)
        {
            return false;
        }

        var original = profile.Mods.ToList();
        var orderedSelection = original.Where(selected.Contains).ToList();
        insertionIndex = Math.Clamp(insertionIndex, 0, original.Count);

        // The insertion index is expressed against the original list. Account for
        // selected rows preceding it before inserting the block into the remainder.
        var removedBeforeInsertion = original
            .Take(insertionIndex)
            .Count(selected.Contains);
        var remainder = original.Where(mod => !selected.Contains(mod)).ToList();
        var adjustedIndex = Math.Clamp(insertionIndex - removedBeforeInsertion, 0, remainder.Count);
        remainder.InsertRange(adjustedIndex, orderedSelection);

        if (original.SequenceEqual(remainder))
        {
            return false;
        }

        ApplyOrder(
            profile,
            remainder,
            orderedSelection,
            adjustedIndex < original.IndexOf(orderedSelection[0]));
        Renumber(profile);
        return true;
    }

    public static bool MoveManyToStart(ModProfile profile, IEnumerable<ModEntry> sources)
    {
        return MoveManyToInsertionIndex(profile, sources, 0);
    }

    public static bool MoveManyToEnd(ModProfile profile, IEnumerable<ModEntry> sources)
    {
        return MoveManyToInsertionIndex(profile, sources, profile.Mods.Count);
    }

    public static IReadOnlyList<string> GetGroupNames(ModProfile profile) => profile.Mods
        .OrderBy(mod => mod.Order)
        .Select(mod => mod.GroupName.Trim())
        .Where(name => name.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static void UpdateViewGroupKeys(ModProfile profile)
    {
        ModGroupKey? currentGroup = null;
        string? currentName = null;
        foreach (var mod in profile.Mods)
        {
            var groupName = mod.GroupName.Trim();
            if (groupName.Length == 0)
            {
                currentGroup = null;
                currentName = null;
                mod.ViewGroupKey = ModGroupKey.Ungrouped(mod.Id);
                continue;
            }

            if (currentGroup is null || !groupName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
            {
                currentName = groupName;
                currentGroup = ModGroupKey.Group(mod.Id, groupName);
            }

            mod.ViewGroupKey = currentGroup;
        }
    }

    public static bool GroupExists(ModProfile profile, string groupName, string? except = null)
    {
        var normalized = groupName.Trim();
        return normalized.Length > 0 && profile.Mods.Any(mod =>
            mod.GroupName.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
            (except is null || !mod.GroupName.Equals(except, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool CreateGroup(ModProfile profile, IEnumerable<ModEntry> mods, string groupName)
    {
        var normalized = groupName.Trim();
        var selected = mods.Where(profile.Mods.Contains).Distinct().ToArray();
        if (normalized.Length == 0 || selected.Length == 0 || GroupExists(profile, normalized))
        {
            return false;
        }

        var insertionIndex = selected.Min(profile.Mods.IndexOf);
        MoveManyToInsertionIndex(profile, selected, insertionIndex);
        return SetGroup(selected, normalized);
    }

    public static bool RenameGroup(ModProfile profile, string oldName, string newName)
    {
        var normalized = newName.Trim();
        var mods = profile.Mods
            .Where(mod => mod.GroupName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (normalized.Length == 0 || mods.Length == 0 || GroupExists(profile, normalized, oldName))
        {
            return false;
        }

        return SetGroup(mods, normalized);
    }

    public static bool DeleteGroup(ModProfile profile, string groupName) => SetGroup(
        profile.Mods.Where(mod => mod.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase)),
        string.Empty);

    public static bool MoveToGroup(ModProfile profile, IEnumerable<ModEntry> mods, string groupName)
    {
        var normalized = groupName.Trim();
        var selected = mods.Where(profile.Mods.Contains).Distinct().ToArray();
        if (selected.Length == 0 || (normalized.Length > 0 && !GroupExists(profile, normalized)))
        {
            return false;
        }

        var moved = false;
        if (normalized.Length > 0)
        {
            var selectedSet = selected.ToHashSet();
            var target = profile.Mods
                .Select((mod, index) => (mod, index))
                .LastOrDefault(item =>
                    !selectedSet.Contains(item.mod) &&
                    item.mod.GroupName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (target.mod is not null)
            {
                moved = MoveManyToInsertionIndex(profile, selected, target.index + 1);
            }
        }

        return SetGroup(selected, normalized) || moved;
    }

    public static bool SetGroup(IEnumerable<ModEntry> mods, string groupName)
    {
        var normalized = groupName.Trim();
        var changed = false;
        foreach (var mod in mods)
        {
            if (mod.GroupName.Equals(normalized, StringComparison.Ordinal))
            {
                continue;
            }

            mod.GroupName = normalized;
            changed = true;
        }

        return changed;
    }

    public static bool CanMoveByOffset(ModProfile profile, ModEntry source, int offset)
    {
        var oldIndex = profile.Mods.IndexOf(source);
        var newIndex = oldIndex + offset;
        return oldIndex >= 0 && newIndex >= 0 && newIndex < profile.Mods.Count;
    }

    public static void Renumber(ModProfile profile)
    {
        for (var index = 0; index < profile.Mods.Count; index++)
        {
            profile.Mods[index].Order = index + 1;
        }
    }

    private static bool MoveToIndex(ModProfile profile, int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || newIndex < 0 || newIndex >= profile.Mods.Count || oldIndex == newIndex)
        {
            return false;
        }

        profile.Mods.Move(oldIndex, newIndex);
        Renumber(profile);
        return true;
    }

    private static void ApplyOrder(
        ModProfile profile,
        List<ModEntry> desiredOrder,
        List<ModEntry> selection,
        bool movingEarlier)
    {
        var indexes = movingEarlier
            ? Enumerable.Range(0, selection.Count)
            : Enumerable.Range(0, selection.Count).Reverse();
        foreach (var index in indexes)
        {
            var mod = selection[index];
            var currentIndex = profile.Mods.IndexOf(mod);
            var targetIndex = desiredOrder.IndexOf(mod);
            if (currentIndex != targetIndex)
            {
                profile.Mods.Move(currentIndex, targetIndex);
            }
        }
    }
}
