using System.Security.Cryptography;
using System.Text;

namespace StalkerModLauncher.Services;

internal static class LegacyProfileDataAlias
{
    public static void Delete(string profileWorkspace, string profileId)
    {
        var workspaceParent = Directory.GetParent(Path.GetFullPath(profileWorkspace))?.FullName;
        if (workspaceParent is null)
        {
            return;
        }

        FileSystemSafety.EnsureNoDirectoryLinks(workspaceParent);
        var aliasRoot = Path.Combine(workspaceParent, ".profile-data");
        var aliasPath = Path.Combine(aliasRoot, GetProfileKey(profileId));
        var alias = new DirectoryInfo(aliasPath);
        string? target;
        try
        {
            target = alias.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (target is null)
        {
            return;
        }

        if (!Path.IsPathRooted(target))
        {
            target = Path.GetFullPath(Path.Combine(alias.Parent!.FullName, target));
        }

        var expectedTarget = Path.Combine(profileWorkspace, "userdata");
        if (!FileSystemSafety.IsSameDirectory(target, expectedTarget))
        {
            return;
        }

        Directory.Delete(aliasPath);
        if (Directory.Exists(aliasRoot) &&
            !Directory.EnumerateFileSystemEntries(aliasRoot).Any())
        {
            Directory.Delete(aliasRoot);
        }
    }

    private static string GetProfileKey(string profileId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(profileId));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
