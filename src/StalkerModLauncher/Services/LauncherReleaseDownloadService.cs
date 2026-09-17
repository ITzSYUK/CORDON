using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using StalkerModLauncher.Resources;

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
        var assetFileName = Path.GetFileName(downloadUri.LocalPath);
        var expectedHash = await GetReleaseAssetSha256Async(releaseTag, assetFileName, cancellationToken);
        var destinationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var destinationPath = GetAvailablePath(destinationDirectory, assetFileName);
        var temporaryPath = destinationPath + ".partial";
        var downloadCompleted = false;

        try
        {
            using var response = await HttpClient.GetAsync(
                downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength
                ?? throw new InvalidDataException(Strings.Update_ArchiveSizeMissing);
            LauncherSelfUpdateService.EnsureSpaceForDownload(destinationDirectory, contentLength);

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
            VerifyArchiveChecksum(expectedHash, destinationPath);
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
            throw new InvalidDataException(Strings.Update_UnexpectedReleaseUrl);
        }

        var fileName = package switch
        {
            LauncherReleasePackage.Minimal => $"CORDON-{releaseTag}-win-x64.zip",
            LauncherReleasePackage.Standalone => $"CORDON-{releaseTag}-win-x64-standalone.zip",
            _ => throw new ArgumentOutOfRangeException(nameof(package), package, null)
        };

        return new Uri($"https://github.com/{GitHubRepository}/releases/download/{releaseTag}/{fileName}");
    }

    internal static void VerifyArchiveChecksum(string expectedHash, string archivePath)
    {
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(Strings.Update_ChecksumInvalid);
        }

        using var stream = File.OpenRead(archivePath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Strings.Update_ChecksumMismatch);
        }
    }

    private static async Task<string> GetReleaseAssetSha256Async(
        string releaseTag,
        string assetFileName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{GitHubRepository}/releases/tags/{Uri.EscapeDataString(releaseTag)}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return GetReleaseAssetSha256(document.RootElement, releaseTag, assetFileName);
    }

    internal static string GetReleaseAssetSha256(
        JsonElement release,
        string releaseTag,
        string assetFileName)
    {
        if (!release.TryGetProperty("tag_name", out var tagName) ||
            !string.Equals(tagName.GetString(), releaseTag, StringComparison.Ordinal) ||
            !release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(Strings.Update_ReleaseDataIncomplete);
        }

        var asset = assets.EnumerateArray().SingleOrDefault(candidate =>
            candidate.TryGetProperty("name", out var name) &&
            string.Equals(name.GetString(), assetFileName, StringComparison.Ordinal));
        if (asset.ValueKind != JsonValueKind.Object ||
            !asset.TryGetProperty("digest", out var digest) ||
            digest.ValueKind != JsonValueKind.String ||
            !digest.GetString()!.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(LocalizedText.Format(Strings.Update_ShaMissingFormat, assetFileName));
        }

        var hash = digest.GetString()!["sha256:".Length..];
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(LocalizedText.Format(Strings.Update_ShaInvalidFormat, assetFileName));
        }

        return hash;
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
                throw new InvalidDataException(Strings.Update_ArchiveTooLarge);
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
