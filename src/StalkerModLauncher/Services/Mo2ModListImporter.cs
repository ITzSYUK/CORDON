using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

public sealed record Mo2ModListImportResult(
    int MatchedCount,
    int EnabledStateChanges,
    IReadOnlyList<string> MissingProfileMods,
    IReadOnlyList<string> UnlistedLauncherMods);

public static class Mo2ModListImporter
{
    public static Mo2ModListImportResult Import(ModProfile profile, string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(Strings.Mo2Service_ModListFileMissing, filePath);
        }

        var entries = Parse(File.ReadLines(filePath));
        if (entries.Count == 0)
        {
            throw new InvalidDataException(Strings.Mo2Service_ModListNoEntries);
        }

        var available = profile.Mods.ToList();
        var matchedInMo2Order = new List<(ModEntry Mod, bool IsEnabled)>();
        var missing = new List<string>();

        foreach (var entry in entries)
        {
            var match = available.FirstOrDefault(mod => NamesMatch(mod, entry.Name));
            if (match is null)
            {
                missing.Add(entry.Name);
                continue;
            }

            available.Remove(match);
            matchedInMo2Order.Add((match, entry.IsEnabled));
        }

        var stateChanges = 0;
        foreach (var (mod, isEnabled) in matchedInMo2Order)
        {
            if (mod.IsEnabled != isEnabled)
            {
                mod.IsEnabled = isEnabled;
                stateChanges++;
            }
        }

        // MO2 writes highest-priority mods first. The launcher displays higher
        // priority lower in the list, so matched entries must be reversed.
        var desiredOrder = available
            .Concat(matchedInMo2Order.AsEnumerable().Reverse().Select(item => item.Mod))
            .ToList();
        ApplyOrder(profile, desiredOrder);

        return new Mo2ModListImportResult(
            matchedInMo2Order.Count,
            stateChanges,
            missing,
            available.Select(mod => mod.Name).ToArray());
    }

    private static List<Mo2ModListEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<Mo2ModListEntry>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length < 2 || line[0] == '#')
            {
                continue;
            }

            var marker = line[0];
            if (marker is not ('+' or '-' or '*'))
            {
                continue;
            }

            var name = line[1..].Trim();
            if (name.Length > 0)
            {
                entries.Add(new Mo2ModListEntry(name, marker is '+' or '*'));
            }
        }

        return entries;
    }

    private static bool NamesMatch(ModEntry mod, string mo2Name)
    {
        if (string.Equals(mod.Name.Trim(), mo2Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var folderName = Path.GetFileName(
            mod.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(folderName, mo2Name, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyOrder(ModProfile profile, List<ModEntry> desiredOrder)
    {
        for (var targetIndex = 0; targetIndex < desiredOrder.Count; targetIndex++)
        {
            var currentIndex = profile.Mods.IndexOf(desiredOrder[targetIndex]);
            if (currentIndex != targetIndex)
            {
                profile.Mods.Move(currentIndex, targetIndex);
            }
        }

        for (var index = 0; index < profile.Mods.Count; index++)
        {
            profile.Mods[index].Order = index + 1;
        }
    }

    private sealed record Mo2ModListEntry(string Name, bool IsEnabled);
}
