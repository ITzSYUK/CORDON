using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using StalkerModLauncher.Resources;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace StalkerModLauncher.Services;

public enum ApProCatalogCategory
{
    ShadowOfChernobyl,
    ClearSky,
    CallOfPripyat
}

public sealed record ApProModListing(
    string Title,
    string Description,
    string DetailUrl,
    string? ThumbnailUrl,
    double? Rating,
    string? Views);

public sealed record ApProCatalogPage(
    int PageNumber,
    int TotalPages,
    IReadOnlyList<ApProModListing> Items);

internal sealed record ApProCatalogPageContent(
    IReadOnlyList<ApProModListing> Items,
    int? TotalPageCount);

public sealed class ApProCatalogService : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumCatalogRequestInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan DefaultRetryAfterDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryAfterDelay = TimeSpan.FromSeconds(30);
    private const int MaximumConcurrentThumbnailDownloads = 4;
    private const int MaximumCatalogPageBytes = 4 * 1024 * 1024;
    private const int MaximumThumbnailBytes = 8 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _minimumCatalogRequestInterval;
    private readonly SemaphoreSlim _catalogRequestLock = new(1, 1);
    private readonly SemaphoreSlim _thumbnailDownloadLimit;
    private readonly int _maximumCatalogPageBytes;
    private readonly int _maximumThumbnailBytes;
    private readonly object _cacheSync = new();
    private readonly Dictionary<CatalogPageKey, CachedCatalog> _cache = new();
    private DateTimeOffset _lastCatalogRequestAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public ApProCatalogService(
        HttpMessageHandler? httpMessageHandler = null,
        TimeSpan? minimumCatalogRequestInterval = null,
        int maximumConcurrentThumbnailDownloads = MaximumConcurrentThumbnailDownloads,
        int maximumCatalogPageBytes = MaximumCatalogPageBytes,
        int maximumThumbnailBytes = MaximumThumbnailBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentThumbnailDownloads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCatalogPageBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumThumbnailBytes, 1);

        _httpClient = CreateHttpClient(httpMessageHandler);
        _minimumCatalogRequestInterval = minimumCatalogRequestInterval ?? MinimumCatalogRequestInterval;
        _maximumCatalogPageBytes = maximumCatalogPageBytes;
        _maximumThumbnailBytes = maximumThumbnailBytes;
        _thumbnailDownloadLimit = new SemaphoreSlim(
            maximumConcurrentThumbnailDownloads,
            maximumConcurrentThumbnailDownloads);
    }

    public static string GetCategoryTitle(ApProCatalogCategory category) => category switch
    {
        ApProCatalogCategory.ShadowOfChernobyl => Strings.Catalog_ShadowOfChernobyl,
        ApProCatalogCategory.ClearSky => Strings.Catalog_ClearSky,
        ApProCatalogCategory.CallOfPripyat => Strings.Catalog_CallOfPripyat,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    public static string GetCategoryUrl(ApProCatalogCategory category) => category switch
    {
        ApProCatalogCategory.ShadowOfChernobyl => "https://ap-pro.ru/stuff/ten_chernobylja/",
        ApProCatalogCategory.ClearSky => "https://ap-pro.ru/stuff/chistoe_nebo/",
        ApProCatalogCategory.CallOfPripyat => "https://ap-pro.ru/stuff/zov_pripjati/",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    public static string GetPageUrl(ApProCatalogCategory category, int pageNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);

        var categoryUrl = GetCategoryUrl(category);
        return pageNumber == 1
            ? $"{categoryUrl}?d=3"
            : $"{categoryUrl}page/{pageNumber}/?d=3";
    }

    public async Task<IReadOnlyList<ApProModListing>> LoadAsync(
        ApProCatalogCategory category,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        return (await LoadPageAsync(category, 1, forceRefresh, cancellationToken)).Items;
    }

    public async Task<ApProCatalogPage> LoadPageAsync(
        ApProCatalogCategory category,
        int pageNumber,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = new CatalogPageKey(category, pageNumber);
        if (forceRefresh)
        {
            InvalidateCategory(category);
        }

        CachedCatalog? cached;
        lock (_cacheSync)
        {
            _cache.TryGetValue(key, out cached);
        }

        if (cached is not null && DateTimeOffset.UtcNow - cached.LoadedAt < CacheLifetime)
        {
            return cached.Page;
        }

        var html = await DownloadCatalogPageAsync(GetPageUrl(category, pageNumber), cancellationToken);
        if (html.Contains("cf-chl", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException(Strings.Catalog_BrowserCheck);
        }

        var content = ApProCatalogParser.ParsePage(html);
        var totalPages = Math.Max(pageNumber, content.TotalPageCount ?? pageNumber);
        var page = new ApProCatalogPage(pageNumber, totalPages, content.Items);
        lock (_cacheSync)
        {
            _cache[key] = new CachedCatalog(DateTimeOffset.UtcNow, page);
        }

        return page;
    }

    public async Task<byte[]?> DownloadThumbnailAsync(string thumbnailUrl, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _thumbnailDownloadLimit.WaitAsync(cancellationToken);
        try
        {
            using var response = await SendWithRetryAfterAsync(thumbnailUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await ReadContentWithLimitAsync(
                response.Content,
                _maximumThumbnailBytes,
                Strings.Catalog_ThumbnailDescription,
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        finally
        {
            _thumbnailDownloadLimit.Release();
        }
    }

    private async Task<string> DownloadCatalogPageAsync(string url, CancellationToken cancellationToken)
    {
        await _catalogRequestLock.WaitAsync(cancellationToken);
        try
        {
            await WaitForCatalogRequestIntervalAsync(cancellationToken);
            using var response = await SendWithRetryAfterAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var bytes = await ReadContentWithLimitAsync(
                response.Content,
                _maximumCatalogPageBytes,
                Strings.Catalog_PageDescription,
                cancellationToken);
            return DecodeText(response.Content, bytes);
        }
        finally
        {
            _catalogRequestLock.Release();
        }
    }

    private async Task WaitForCatalogRequestIntervalAsync(CancellationToken cancellationToken)
    {
        var remaining = _minimumCatalogRequestInterval - (DateTimeOffset.UtcNow - _lastCatalogRequestAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }

        _lastCatalogRequestAt = DateTimeOffset.UtcNow;
    }

    private async Task<HttpResponseMessage> SendWithRetryAfterAsync(string url, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return response;
        }

        var retryDelay = GetRetryAfterDelay(response.Headers.RetryAfter);
        response.Dispose();
        if (retryDelay > TimeSpan.Zero)
        {
            await Task.Delay(retryDelay, cancellationToken);
        }

        return await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<byte[]> ReadContentWithLimitAsync(
        HttpContent content,
        int maximumBytes,
        string description,
        CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maximumBytes)
        {
            throw new HttpRequestException(
                LocalizedText.Format(Strings.Catalog_ContentTooLargeFormat, description, maximumBytes));
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(
            contentLength is > 0 and <= int.MaxValue
                ? (int)Math.Min(contentLength.Value, maximumBytes)
                : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new HttpRequestException(
                        LocalizedText.Format(Strings.Catalog_ContentTooLargeFormat, description, maximumBytes));
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string DecodeText(HttpContent content, byte[] bytes)
    {
        var encoding = Encoding.UTF8;
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // Ignore an invalid server charset and fall back to UTF-8.
            }
        }

        using var reader = new StreamReader(
            new MemoryStream(bytes, writable: false),
            encoding,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static TimeSpan GetRetryAfterDelay(RetryConditionHeaderValue? retryAfter)
    {
        var delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is { } retryDate)
        {
            delay = retryDate - DateTimeOffset.UtcNow;
        }

        if (delay is null)
        {
            return DefaultRetryAfterDelay;
        }

        return delay <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(Math.Min(delay.Value.Ticks, MaximumRetryAfterDelay.Ticks));
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler? httpMessageHandler)
    {
        var client = httpMessageHandler is null
            ? new HttpClient()
            : new HttpClient(httpMessageHandler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"CORDON/{GetApplicationVersion()} (+https://github.com/ITzSYUK/CORDON)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.6");
        return client;
    }

    private static string GetApplicationVersion()
    {
        var informationalVersion = typeof(ApProCatalogService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return informationalVersion?.Split('+')[0]
               ?? typeof(ApProCatalogService).Assembly.GetName().Version?.ToString(3)
               ?? "unknown";
    }

    private void InvalidateCategory(ApProCatalogCategory category)
    {
        lock (_cacheSync)
        {
            foreach (var key in _cache.Keys.Where(key => key.Category == category).ToArray())
            {
                _cache.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        _catalogRequestLock.Dispose();
        _thumbnailDownloadLimit.Dispose();
        lock (_cacheSync)
        {
            _cache.Clear();
        }
    }

    private sealed record CatalogPageKey(ApProCatalogCategory Category, int PageNumber);
    private sealed record CachedCatalog(DateTimeOffset LoadedAt, ApProCatalogPage Page);
}

public static class ApProCatalogParser
{
    private static readonly Regex WhitespaceExpression = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex ViewsExpression = new(@"(?<views>[\d\s\u00A0]+просмотров)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int? GetTotalPageCount(string html) => ParsePage(html).TotalPageCount;

    public static IReadOnlyList<ApProModListing> Parse(string html) => ParsePage(html).Items;

    internal static ApProCatalogPageContent ParsePage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new ApProCatalogPageContent(Array.Empty<ApProModListing>(), null);
        }

        var document = new HtmlParser().ParseDocument(html);
        var pageCountValue = document.QuerySelector("[data-pages]")?.GetAttribute("data-pages");
        int? totalPageCount = int.TryParse(pageCountValue, out var pageCount) && pageCount > 0
            ? pageCount
            : null;
        var result = new List<ApProModListing>();
        foreach (var article in document.QuerySelectorAll("article.cCmsCategoryFeaturedEntry"))
        {
            var titleLink = article.QuerySelector("h1 a[href]");
            var detailUrl = ToAbsoluteUrl(titleLink?.GetAttribute("href"));
            if (titleLink is null || detailUrl is null)
            {
                continue;
            }

            var image = article.QuerySelector(".cCmsRecord_image img");
            var imageUrl = image?.GetAttribute("src") ?? image?.GetAttribute("data-src");
            var description = article.QuerySelector("section[data-ipstruncate]");
            var onStars = article.QuerySelectorAll(".ipsRating_on").Length;
            var halfStars = article.QuerySelectorAll(".ipsRating_half").Length;
            var viewsMatch = ViewsExpression.Match(NormalizeWhitespace(article.TextContent));

            result.Add(new ApProModListing(
                NormalizeWhitespace(titleLink.TextContent),
                description is null ? string.Empty : NormalizeWhitespace(description.TextContent),
                detailUrl,
                ToAbsoluteUrl(imageUrl),
                onStars + halfStars * 0.5d > 0 ? onStars + halfStars * 0.5d : null,
                viewsMatch.Success ? NormalizeWhitespace(viewsMatch.Groups["views"].Value) : null));
        }

        return new ApProCatalogPageContent(result, totalPageCount);
    }

    private static string? ToAbsoluteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.AbsoluteUri;
        }

        return Uri.TryCreate(new Uri("https://ap-pro.ru/"), value, out var relative) ? relative.AbsoluteUri : null;
    }

    private static string NormalizeWhitespace(string value) => WhitespaceExpression.Replace(value.Replace('\u00A0', ' '), " ").Trim();
}
