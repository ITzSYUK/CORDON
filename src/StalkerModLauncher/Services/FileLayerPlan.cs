using System.Text.RegularExpressions;
using StalkerModLauncher.Models;

namespace StalkerModLauncher.Services;

public sealed partial class FileLayerPlan
{
    private string _automaticFsgameRelativePath = "fsgame.ltx";

    private FileLayerPlan(IReadOnlyList<FileLayer> layers)
    {
        Layers = layers;
        BaseGame = layers.Single(layer => layer.Kind == FileLayerKind.BaseGame);
        UserData = layers.Single(layer => layer.Kind == FileLayerKind.UserData);
        Mods = layers
            .Where(layer => layer.Kind == FileLayerKind.Mod)
            .OrderBy(layer => layer.Order)
            .ToArray();
    }

    public IReadOnlyList<FileLayer> Layers { get; }

    public FileLayer BaseGame { get; }

    public IReadOnlyList<FileLayer> Mods { get; }

    public FileLayer UserData { get; }
    public string GameDataRoot { get; private set; } = string.Empty;
    public bool UsesSharedGameData { get; private init; }
    internal ProfileFsgameSource? ManualFsgameSource { get; private set; }
    internal bool UsesFsgameLaunchArgument { get; private set; }
    internal string FsgameLaunchRelativePath => _automaticFsgameRelativePath;

    public IEnumerable<FileLayer> SourceLayers => new[] { BaseGame }.Concat(Mods);

    public IReadOnlyList<LaunchExecutableSearchRoot> CreateExecutableRoots()
    {
        return SourceLayers
            .Select(layer => new LaunchExecutableSearchRoot(layer.RootPath, GetDisplayName(layer), layer.Order))
            .ToArray();
    }

    public FileLayerFile? FindFinalFile(string relativePath)
    {
        var providers = FindAllProviders(relativePath);
        return providers.Count == 0 ? null : providers[^1];
    }

    internal ProfileFsgameSource? FindFsgameSource()
    {
        if (ManualFsgameSource is not null)
        {
            return ManualFsgameSource;
        }

        return FindFinalFsgameSource(SourceLayers, _automaticFsgameRelativePath);
    }

    internal static ProfileFsgameSource? ResolveFsgameSource(ModProfile profile)
    {
        var relativePath = ResolveFsgameLaunchArgument(profile.LaunchArguments) ?? "fsgame.ltx";
        var manualSource = ResolveManualFsgameSource(profile);
        if (manualSource is not null)
        {
            return manualSource;
        }

        return FindFinalFsgameSource(
            CreateSourceLayers(profile.GameInstallPath, profile),
            relativePath);
    }

    public IReadOnlyList<FileLayerFile> FindAllProviders(string relativePath)
    {
        FileSystemSafety.EnsureRelativePath(relativePath, "Layer file");
        return SourceLayers
            .Where(layer => Directory.Exists(layer.RootPath))
            .Where(layer => !IsExcluded(layer, relativePath))
            .Select(layer => new FileLayerFile(
                Path.Combine(layer.RootPath, relativePath),
                relativePath,
                GetDisplayName(layer),
                layer))
            .Where(source => File.Exists(source.FullPath))
            .ToArray();
    }

    public IReadOnlyList<FileLayerExecutableCandidate> GetExecutableCandidates(CancellationToken cancellationToken = default)
    {
        var candidates = new List<FileLayerExecutableCandidate>();
        foreach (var layer in SourceLayers.Where(layer => Directory.Exists(layer.RootPath)))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(layer.RootPath, "*.exe", SafeEnumerationOptions);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(layer.RootPath, file);
                if (IsExcluded(layer, relativePath))
                {
                    continue;
                }

                candidates.Add(new FileLayerExecutableCandidate(
                    file,
                    relativePath,
                    GetDisplayName(layer),
                    layer));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Layer.Order)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<FileLayerOverwrite> GetOverwrittenFiles(FileLayer replacingLayer, CancellationToken cancellationToken = default)
    {
        if (replacingLayer.Kind is not FileLayerKind.Mod)
        {
            return [];
        }

        var previousFiles = new Dictionary<string, FileLayerFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in SourceLayers.Where(layer => layer.Order < replacingLayer.Order && Directory.Exists(layer.RootPath)))
        {
            foreach (var file in EnumerateLayerFiles(layer, cancellationToken))
            {
                previousFiles[file.RelativePath] = file;
            }
        }

        var overwrites = new List<FileLayerOverwrite>();
        foreach (var replacingFile in EnumerateLayerFiles(replacingLayer, cancellationToken))
        {
            if (previousFiles.TryGetValue(replacingFile.RelativePath, out var replacedFile))
            {
                overwrites.Add(new FileLayerOverwrite(replacingFile.RelativePath, replacedFile, replacingFile));
            }
        }

        return overwrites
            .OrderBy(overwrite => overwrite.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<FileLayerOverwrite> GetOverwrittenFiles(ModEntry mod, CancellationToken cancellationToken = default)
    {
        var layer = Mods.FirstOrDefault(layer => ReferenceEquals(layer.Mod, mod) || layer.Id == mod.Id);
        return layer is null ? [] : GetOverwrittenFiles(layer, cancellationToken);
    }

    public static string GetDisplayName(FileLayer layer)
    {
        return layer.Kind switch
        {
            FileLayerKind.BaseGame => "базовая игра",
            FileLayerKind.Mod => $"мод: {layer.Name}",
            FileLayerKind.UserData => "данные профиля",
            _ => layer.Name
        };
    }

    public static FileLayerPlan CreateLinkedWorkspace(string gamePath, ModProfile profile, string workspaceRoot)
    {
        if (profile.IsStandalone)
        {
            throw new InvalidOperationException("FileLayerPlan for standalone profiles is not part of the linked workspace pipeline.");
        }

        var paths = AppPaths.Current;
        foreach (var source in new[] { gamePath }.Concat(profile.Mods.Where(mod => mod.IsEnabled).Select(mod => mod.SourcePath)))
            paths.ValidateSourceDirectory(source);
        if (!string.IsNullOrWhiteSpace(profile.Mo2OverwritePath)) paths.ValidateSourceDirectory(profile.Mo2OverwritePath);

        var layers = CreateSourceLayers(gamePath, profile);

        layers.Add(new FileLayer(
            FileLayerKind.UserData,
            "__userdata",
            "Profile user data",
            Path.Combine(Path.GetFullPath(workspaceRoot), "userdata"),
            int.MaxValue));

        var plan = new FileLayerPlan(layers) { UsesSharedGameData = profile.UseBaseGameData };
        plan.ManualFsgameSource = ResolveManualFsgameSource(profile);
        if (ResolveFsgameLaunchArgument(profile.LaunchArguments) is { } relativePath)
        {
            plan._automaticFsgameRelativePath = relativePath;
            plan.UsesFsgameLaunchArgument = true;
        }

        var launchArgumentSource = plan.UsesFsgameLaunchArgument
            ? plan.FindFsgameSource() ?? throw new FileNotFoundException(
                $"Файл из параметра -fsltx не найден во включённых слоях: {plan._automaticFsgameRelativePath}")
            : null;
        plan.GameDataRoot = ProfileDataPathResolver.GetGameDataRoot(
            profile,
            workspaceRoot,
            gamePath,
            launchArgumentSource?.FullPath);
        return plan;
    }

    internal static string? ResolveFsgameLaunchArgument(string? launchArguments)
    {
        if (string.IsNullOrWhiteSpace(launchArguments))
        {
            return null;
        }

        var match = FsltxArgumentRegex().Match(launchArguments);
        if (!match.Success)
        {
            return null;
        }

        var relativePath = (match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value
            : match.Groups["plain"].Value).Trim();
        if (relativePath.Length == 0)
        {
            throw new InvalidOperationException("После параметра -fsltx укажите относительный путь к файлу .ltx.");
        }

        FileSystemSafety.EnsureRelativePath(relativePath, "Файл из параметра -fsltx");
        if (!Path.GetExtension(relativePath).Equals(".ltx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Параметр -fsltx должен указывать на файл с расширением .ltx: {relativePath}");
        }

        return NormalizeRelativePath(relativePath);
    }

    [GeneratedRegex(
        @"(?:^|\s)-fsltx(?=$|\s|=)(?:(?:\s*=\s*|\s+)(?:""(?<quoted>[^""]*)""|(?<plain>\S+)))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FsltxArgumentRegex();

    private static ProfileFsgameSource? ResolveManualFsgameSource(ModProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.FsgameSourcePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(profile.FsgameSourcePath.Trim());
        if (!Path.GetExtension(fullPath).Equals(".ltx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ручной файл конфигурации должен иметь расширение .ltx.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Ручной файл .ltx не найден: {fullPath}", fullPath);
        }

        var selection = ProfileExecutableSourceResolver.TryCreateSelection(profile, fullPath, includeWorkspace: false)
            ?? throw new InvalidOperationException(
                "Ручной файл .ltx должен находиться в папке базовой игры или включённого мода.");
        return new ProfileFsgameSource(fullPath, selection.RelativePath, selection.SourceName);
    }

    private static List<FileLayer> CreateSourceLayers(string gamePath, ModProfile profile)
    {
        var layers = new List<FileLayer>
        {
            new(
                FileLayerKind.BaseGame,
                "__base_game",
                "Base game",
                Path.GetFullPath(gamePath),
                0)
        };

        layers.AddRange(profile.Mods
            .Where(mod => mod.IsEnabled)
            .OrderBy(mod => mod.Order)
            .Select(mod => new FileLayer(
                FileLayerKind.Mod,
                mod.Id,
                mod.Name,
                Path.GetFullPath(mod.SourcePath),
                mod.Order,
                mod)));

        if (!string.IsNullOrWhiteSpace(profile.Mo2OverwritePath))
        {
            var overwrite = new ModEntry
            {
                Id = "__mo2_overwrite",
                Name = "Файлы overwrite из MO2",
                SourcePath = Path.GetFullPath(profile.Mo2OverwritePath),
                IsEnabled = true,
                Order = profile.Mods.Count == 0 ? 1 : profile.Mods.Max(mod => mod.Order) + 1
            };
            layers.Add(new FileLayer(
                FileLayerKind.Mod,
                overwrite.Id,
                overwrite.Name,
                overwrite.SourcePath,
                overwrite.Order,
                overwrite));
        }

        return layers;
    }

    private static ProfileFsgameSource? FindFinalFsgameSource(
        IEnumerable<FileLayer> layers,
        string relativePath)
    {
        FileSystemSafety.EnsureRelativePath(relativePath, "Profile fsgame.ltx");
        return layers
            .Where(layer => layer.Kind != FileLayerKind.UserData && Directory.Exists(layer.RootPath))
            .Where(layer => !IsExcluded(layer, relativePath))
            .OrderBy(layer => layer.Order)
            .Select(layer => new ProfileFsgameSource(
                Path.Combine(layer.RootPath, relativePath),
                relativePath,
                GetDisplayName(layer)))
            .LastOrDefault(source => File.Exists(source.FullPath));
    }

    private static IEnumerable<FileLayerFile> EnumerateLayerFiles(FileLayer layer, CancellationToken cancellationToken)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(layer.RootPath, "*", SafeEnumerationOptions);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(layer.RootPath, file);
            if (IsExcluded(layer, relativePath))
            {
                continue;
            }

            yield return new FileLayerFile(
                file,
                relativePath,
                GetDisplayName(layer),
                layer);
        }
    }

    public static bool IsExcluded(FileLayer layer, string relativePath)
    {
        return layer.Mod?.ExcludedFiles.Any(excluded =>
            NormalizeRelativePath(excluded).Equals(
                NormalizeRelativePath(relativePath),
                StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

    private static EnumerationOptions SafeEnumerationOptions { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };
}

internal sealed record ProfileFsgameSource(
    string FullPath,
    string RelativePath,
    string SourceName);
