using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class ProfileShaderCacheSeeder
{
    private static EnumerationOptions SafeEnumerationOptions { get; } = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static void Seed(
        FileLayerPlan layerPlan,
        string profileDataPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sources = FindFinalCacheFiles(layerPlan, cancellationToken);
        if (sources.Count == 0)
        {
            return;
        }

        var destinationRoot = Path.Combine(profileDataPath, "shaders_cache");
        var copied = 0;
        var failed = 0;

        foreach (var source in sources.Values.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = FileSystemSafety.ResolvePathInside(
                destinationRoot,
                source.RelativePath,
                Strings.Safety_ProfileShaderCacheFile);

            try
            {
                if (FilesAreEqual(source.FullPath, destination))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + $".launcher-{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(source.FullPath, temporary, overwrite: false);
                    File.SetAttributes(temporary, File.GetAttributes(temporary) & ~FileAttributes.ReadOnly);
                    if (File.Exists(destination))
                    {
                        File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
                    }

                    File.Move(temporary, destination, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }

                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        if (copied > 0)
        {
            progress?.Report(LocalizedText.Format(Strings.Progress_ShaderCachePreparedFormat, copied));
        }

        if (failed > 0)
        {
            progress?.Report(LocalizedText.Format(Strings.Progress_ShaderCacheFailedFormat, failed));
        }
    }

    private static Dictionary<string, ShaderCacheSourceFile> FindFinalCacheFiles(
        FileLayerPlan layerPlan,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, ShaderCacheSourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layerPlan.SourceLayers.OrderBy(layer => layer.Order))
        {
            foreach (var cacheRoot in FindShaderCacheRoots(layer))
            {
                IEnumerable<string> cacheFiles;
                try
                {
                    cacheFiles = Directory.EnumerateFiles(cacheRoot, "*", SafeEnumerationOptions);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var cacheFile in cacheFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = Path.GetRelativePath(cacheRoot, cacheFile);
                    files[relativePath] = new ShaderCacheSourceFile(cacheFile, relativePath);
                }
            }
        }

        return files;
    }

    private static IEnumerable<string> FindShaderCacheRoots(FileLayer layer)
    {
        foreach (var appDataRoot in ProfileAppDataSourceLocator.EnumerateRoots(layer))
        {
            var cacheRoot = Path.Combine(appDataRoot, "shaders_cache");
            if (Directory.Exists(cacheRoot))
            {
                yield return cacheRoot;
                yield break;
            }
        }
    }

    private static bool FilesAreEqual(string source, string destination)
    {
        if (!File.Exists(destination))
        {
            return false;
        }

        var sourceInfo = new FileInfo(source);
        var destinationInfo = new FileInfo(destination);
        if (sourceInfo.Length != destinationInfo.Length)
        {
            return false;
        }

        const int bufferSize = 64 * 1024;
        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        using var destinationStream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        var sourceBuffer = new byte[bufferSize];
        var destinationBuffer = new byte[bufferSize];

        while (true)
        {
            var sourceRead = sourceStream.Read(sourceBuffer, 0, sourceBuffer.Length);
            var destinationRead = destinationStream.Read(destinationBuffer, 0, destinationBuffer.Length);
            if (sourceRead != destinationRead)
            {
                return false;
            }

            if (sourceRead == 0)
            {
                return true;
            }

            if (!sourceBuffer.AsSpan(0, sourceRead).SequenceEqual(destinationBuffer.AsSpan(0, destinationRead)))
            {
                return false;
            }
        }
    }

    private sealed record ShaderCacheSourceFile(string FullPath, string RelativePath);
}
