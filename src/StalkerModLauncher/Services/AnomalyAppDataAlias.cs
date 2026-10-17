using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class AnomalyAppDataAlias
{
    internal static bool IsAppDataPath(string relativePath) =>
        relativePath.Equals("appdata", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith("appdata\\", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith("appdata/", StringComparison.OrdinalIgnoreCase);

    public static void Prepare(string runtimeRoot, FileLayerPlan plan)
    {
        var target = Path.GetFullPath(plan.GameDataRoot);
        if (FileSystemSafety.IsDirectoryInside(target, runtimeRoot) ||
            FileSystemSafety.IsDirectoryInside(runtimeRoot, target))
        {
            throw new InvalidOperationException(Strings.ProfileData_OverlapsWorkspace);
        }

        Directory.CreateDirectory(target);
        var alias = new DirectoryInfo(Path.Combine(runtimeRoot, "appdata"));
        if (alias.LinkTarget is { } linkTarget && FileSystemSafety.IsSameDirectory(
                Path.GetFullPath(linkTarget, runtimeRoot), target))
        {
            return;
        }
        if (alias.Exists && alias.LinkTarget is null)
        {
            throw new IOException(LocalizedText.Format(Strings.Error_AppDataAliasFormat, alias.FullName));
        }

        Directory.CreateDirectory(alias.FullName);
        try
        {
            // A directory junction survives atomic replacement of user.ltx and needs no symlink privilege.
            var substituteName = Encoding.Unicode.GetBytes(@"\??\" + target);
            var printName = Encoding.Unicode.GetBytes(target);
            var buffer = new byte[16 + substituteName.Length + printName.Length + 4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substituteName.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substituteName.Length + 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)printName.Length));
            substituteName.CopyTo(buffer, 16);
            printName.CopyTo(buffer, 18 + substituteName.Length);
            using var handle = CreateFile(alias.FullName, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex) when (ex is Win32Exception or OverflowException)
        {
            throw new IOException(LocalizedText.Format(Strings.Error_AppDataAliasFormat, alias.FullName), ex);
        }
    }

    public static void ArchiveLegacyUserFiles(string profileWorkspace, string gameDataRoot, bool includeCurrent = false)
    {
        var canonical = Path.Combine(gameDataRoot, "user.ltx");
        var paths = new List<string>
        {
            Path.Combine(profileWorkspace, "userdata", "writable-game-files", "appdata", "user.ltx"),
            Path.Combine(profileWorkspace, "userdata", "overwrite", "appdata", "user.ltx")
        };
        var currentAppData = new DirectoryInfo(Path.Combine(profileWorkspace, "current", "appdata"));
        if (includeCurrent && currentAppData.LinkTarget is null)
            paths.Add(Path.Combine(currentAppData.FullName, "user.ltx"));
        foreach (var path in paths.Where(File.Exists))
        {
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!);
                 parent is not null && !FileSystemSafety.IsSameDirectory(parent.FullName, profileWorkspace);
                 parent = parent.Parent)
            {
                if (parent.LinkTarget is not null)
                    throw new IOException(LocalizedText.Format(Strings.Error_AppDataAliasFormat, parent.FullName));
            }
            if (!ProfileDataConfigurator.FilesAreEqual(path, canonical))
            {
                var diagnostics = Path.Combine(profileWorkspace, "diagnostics");
                Directory.CreateDirectory(diagnostics);
                File.Copy(path, Path.Combine(diagnostics, $"user.ltx.migrated-{Guid.NewGuid():N}.bak"), overwrite: false);
            }
            if (path == Path.Combine(currentAppData.FullName, "user.ltx")) continue;
            File.Delete(path);
            var parentPath = Path.GetDirectoryName(path)!;
            if (!Directory.EnumerateFileSystemEntries(parentPath).Any()) Directory.Delete(parentPath);
        }
    }

    public static void Detach(string runtimeRoot)
    {
        var alias = new DirectoryInfo(Path.Combine(runtimeRoot, "appdata"));
        if (alias.LinkTarget is not null) Directory.Delete(alias.FullName, recursive: false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
