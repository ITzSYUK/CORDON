using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace StalkerModLauncher.Services;

public enum LauncherReleasePackage
{
    Minimal,
    Standalone
}

public static class LauncherReleaseDownloadService
{
    private const string GitHubRepository = "ITzSYUK/CORDON";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static async Task<string> DownloadAsync(
        string releaseUrl,
        string releaseTag,
        LauncherReleasePackage package,
        CancellationToken cancellationToken = default)
    {
        var downloadUri = BuildDownloadUri(releaseUrl, releaseTag, package);
        var destinationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var destinationPath = GetAvailablePath(destinationDirectory, Path.GetFileName(downloadUri.LocalPath));
        var temporaryPath = destinationPath + ".partial";
        var downloadCompleted = false;

        try
        {
            using var response = await HttpClient.GetAsync(
                downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await CopyWithLimitAsync(source, destination, 512L * 1024 * 1024, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath);
            downloadCompleted = true;
            await VerifyArchiveChecksumAsync(downloadUri, destinationPath, cancellationToken);
            LauncherSelfUpdateService.PrepareAndLaunch(destinationPath, package);
            return destinationPath;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (downloadCompleted && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    private static Uri BuildDownloadUri(
        string releaseUrl,
        string releaseTag,
        LauncherReleasePackage package)
    {
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releaseUri) ||
            releaseUri.Scheme != Uri.UriSchemeHttps ||
            !releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !releaseUri.AbsolutePath.Equals(
                $"/{GitHubRepository}/releases/tag/{releaseTag}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("GitHub returned an unexpected release URL.");
        }

        var fileName = package switch
        {
            LauncherReleasePackage.Minimal => $"CORDON-{releaseTag}-win-x64.zip",
            LauncherReleasePackage.Standalone => $"CORDON-{releaseTag}-win-x64-standalone.zip",
            _ => throw new ArgumentOutOfRangeException(nameof(package), package, null)
        };

        return new Uri($"https://github.com/{GitHubRepository}/releases/download/{releaseTag}/{fileName}");
    }

    internal static void VerifyArchiveChecksum(string checksumText, string archivePath, string assetFileName)
    {
        var expectedHash = checksumText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .Where(parts => parts.Length == 2 && parts[1].Equals(assetFileName, StringComparison.Ordinal))
            .Select(parts => parts[0])
            .SingleOrDefault();
        if (expectedHash is null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Релиз не содержит корректную контрольную сумму архива.");
        }

        using var stream = File.OpenRead(archivePath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Контрольная сумма скачанного архива не совпадает.");
        }
    }

    private static async Task VerifyArchiveChecksumAsync(
        Uri downloadUri,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var checksumUri = new Uri(downloadUri, "checksums.txt");
        using var response = await HttpClient.GetAsync(checksumUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 65536)
        {
            throw new InvalidDataException("Файл контрольных сумм релиза слишком велик.");
        }

        var checksumText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (checksumText.Length > 65536)
        {
            throw new InvalidDataException("Файл контрольных сумм релиза слишком велик.");
        }

        VerifyArchiveChecksum(checksumText, archivePath, Path.GetFileName(downloadUri.LocalPath));
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("Архив обновления слишком велик.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string GetAvailablePath(string directory, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        var number = 1;

        while (File.Exists(candidate) || File.Exists(candidate + ".partial"))
        {
            candidate = Path.Combine(directory, $"{name} ({number++}){extension}");
        }

        return candidate;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CORDON release downloader");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return client;
    }
}
