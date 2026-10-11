namespace StalkerModLauncher.Services.Fomod;

/// <summary>
/// Selection rule of a FOMOD option group, mirroring the ModuleConfig schema.
/// </summary>
public enum FomodGroupType
{
    SelectExactlyOne,
    SelectAtMostOne,
    SelectAtLeastOne,
    SelectAny,
    SelectAll
}

/// <summary>
/// Effective option type of a FOMOD plugin.
/// </summary>
public enum FomodPluginType
{
    Optional,
    Recommended,
    Required,
    NotUsable,
    CouldBeUsable
}

public enum FomodConditionOperator
{
    And,
    Or
}

/// <summary>
/// Single file or folder entry of a FOMOD file list.
/// Source is relative to the FOMOD package root, destination is relative to the mod root.
/// AlwaysInstall entries are installed even when their plugin is not selected.
/// </summary>
public sealed record FomodFileEntry(
    string Source,
    string Destination,
    int Priority,
    bool AlwaysInstall = false,
    bool InstallIfUsable = false)
{
    public int DocumentOrder { get; init; }
}

/// <summary>
/// Type override applied when its dependencies evaluate to true.
/// </summary>
public sealed record FomodTypePattern(FomodCondition Dependencies, FomodPluginType Type);

public sealed record FomodPlugin(
    string Name,
    string Description,
    FomodPluginType BaseType,
    IReadOnlyList<FomodTypePattern> TypePatterns,
    IReadOnlyList<FomodFileEntry> Files,
    IReadOnlyDictionary<string, string> Flags,
    string ImagePath = "");

public sealed record FomodGroup(
    string Name,
    FomodGroupType Type,
    IReadOnlyList<FomodPlugin> Plugins);

public sealed record FomodStep(
    string Name,
    FomodCondition? Visible,
    IReadOnlyList<FomodGroup> Groups);

public sealed record FomodConditionalPattern(
    FomodCondition Dependencies,
    IReadOnlyList<FomodFileEntry> Files);

public sealed record FomodModule(
    string Name,
    IReadOnlyList<FomodFileEntry> RequiredFiles,
    IReadOnlyList<FomodStep> Steps,
    IReadOnlyList<FomodConditionalPattern> ConditionalPatterns,
    FomodCondition? Dependencies = null,
    string ImagePath = "");

public abstract record FomodCondition;

public sealed record FomodAlwaysCondition : FomodCondition;

public sealed record FomodFlagCondition(string Flag, string Value) : FomodCondition;

/// <summary>
/// File state supplied by the target profile; standalone callers default to Missing.
/// </summary>
public sealed record FomodFileCondition(string File, string State) : FomodCondition;

/// <summary>
/// Version dependencies (game, FOMM, script extender) are not applicable to
/// X-Ray and cannot be satisfied by this launcher.
/// </summary>
public sealed record FomodVersionCondition(string Name, string Version) : FomodCondition;

public sealed record FomodCompositeCondition(
    FomodConditionOperator Operator,
    IReadOnlyList<FomodCondition> Children) : FomodCondition;

/// <summary>
/// Location of a detected FOMOD script inside extracted staging content.
/// PackageRoot is the directory that contains the fomod folder (archive root
/// or a single wrapper directory).
/// </summary>
public sealed record FomodLocation(
    string PackageRoot,
    string? ConfigPath,
    bool HasCSharpScript);
