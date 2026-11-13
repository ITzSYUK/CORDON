using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class MiseryClassSupport
{
    private static readonly string[] Classes = ["assaulter", "recon", "sniper"];

    private static readonly string[] AliasNames =
    [
        "$game_class_assaulter$",
        "$game_class_recon$",
        "$game_class_sniper$"
    ];

    // Keep the flags and paths used by MISERY's axr_misery.script.
    private static readonly string[] AliasDefinitions =
    [
        @"$game_class_assaulter$  = true|  false| $game_data$|          class_diversity\assaulter\",
        @"$game_class_recon$      = true|  false| $game_data$|          class_diversity\recon\",
        @"$game_class_sniper$     = true|  false| $game_data$|          class_diversity\sniper\"
    ];

    private static readonly EnumerationOptions SafeEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    internal static IReadOnlyList<string> FindWritablePaths(
        FileLayerPlan layerPlan,
        CancellationToken cancellationToken = default)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nestedClassRoot = Path.Combine("gamedata", "class_diversity");
        var nestedClassPrefix = nestedClassRoot + Path.DirectorySeparatorChar;
        foreach (var className in Classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classRoot = Path.Combine("gamedata", "class_diversity", className);
            var classDataRoot = Path.Combine(classRoot, "gamedata");
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in layerPlan.SourceLayers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRoot = Path.Combine(layer.RootPath, classDataRoot);
                try
                {
                    // EnumerationOptions does not check the enumeration root or its ancestors.
                    if (!Directory.Exists(sourceRoot) || WorkspaceBuilder.HasDirectoryLinks(sourceRoot))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SafeEnumerationOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        candidates.Add(Path.GetRelativePath(layer.RootPath, file));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A missing or inaccessible class directory does not establish class support.
                }
            }

            var hasEffectiveFile = false;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = layerPlan.FindFinalFile(candidate);
                if (source is null)
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(source.FullPath) & FileAttributes.ReparsePoint) != 0 ||
                        WorkspaceBuilder.HasDirectoryLinks(Path.GetDirectoryName(source.FullPath)!))
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var target = Path.GetRelativePath(classRoot, candidate);
                FileSystemSafety.EnsureRelativePath(target, Strings.Safety_LayerFile);
                if (target.Equals(nestedClassRoot, StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith(nestedClassPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                hasEffectiveFile = true;
                targets.Add(target);
            }

            if (!hasEffectiveFile)
            {
                return [];
            }
        }

        return targets.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string[] AddMissingAliases(string[] lines)
    {
        var missingAliases = 0;
        var missingCount = 0;
        for (var aliasIndex = 0; aliasIndex < AliasNames.Length; aliasIndex++)
        {
            var found = false;
            foreach (var line in lines)
            {
                var equalsIndex = line.IndexOf('=');
                if (equalsIndex >= 0 && line.AsSpan(0, equalsIndex).Trim().Equals(
                        AliasNames[aliasIndex], StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                missingAliases |= 1 << aliasIndex;
                missingCount++;
            }
        }

        if (missingCount == 0)
        {
            return lines;
        }

        var result = new string[lines.Length + missingCount];
        Array.Copy(lines, result, lines.Length);
        var nextIndex = lines.Length;
        for (var aliasIndex = 0; aliasIndex < AliasNames.Length; aliasIndex++)
        {
            if ((missingAliases & (1 << aliasIndex)) != 0)
            {
                result[nextIndex++] = AliasDefinitions[aliasIndex];
            }
        }

        return result;
    }
}
