using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ApProCatalogParserTests
{
    [Fact]
    public void ParseExtractsFeaturedEntryDetails()
    {
        const string html = """
            <article class='cCmsCategoryFeaturedEntry ipsClear'>
              <header><h1><a href="/stuff/ten_chernobylja/test-mod-r1/">Тестовый мод</a></h1></header>
              <div class="cCmsRecord_image"><img src="/uploads/test.thumb.jpg"></div>
              <section data-ipsTruncate>Описание <strong>модификации</strong>&nbsp;для теста.</section>
              <ul class='ipsRating_collective'>
                <li class='ipsRating_on'></li><li class='ipsRating_on'></li><li class='ipsRating_half'></li>
              </ul>
              <li class='ipsType_light'>1&nbsp;234 просмотров</li>
            </article>
            """;

        var result = ApProCatalogParser.Parse(html);

        var item = Assert.Single(result);
        Assert.Equal("Тестовый мод", item.Title);
        Assert.Equal("Описание модификации для теста.", item.Description);
        Assert.Equal("https://ap-pro.ru/stuff/ten_chernobylja/test-mod-r1/", item.DetailUrl);
        Assert.Equal("https://ap-pro.ru/uploads/test.thumb.jpg", item.ThumbnailUrl);
        Assert.Equal(2.5, item.Rating);
        Assert.Equal("1 234 просмотров", item.Views);
    }

    [Fact]
    public void ParseHandlesNestedAndImperfectHtml()
    {
        const string html = """
            <article class="cCmsCategoryFeaturedEntry">
              <h1><span><a href="/stuff/test/"><b>Nested</b> title</a></span></h1>
              <div class="cCmsRecord_image"><picture><img data-src="/uploads/lazy.jpg"></picture></div>
              <section data-ipsTruncate>Description <strong>without a closing tag</section>
            </article>
            """;

        var item = Assert.Single(ApProCatalogParser.Parse(html));

        Assert.Equal("Nested title", item.Title);
        Assert.Equal("Description without a closing tag", item.Description);
        Assert.Equal("https://ap-pro.ru/uploads/lazy.jpg", item.ThumbnailUrl);
    }

    [Theory]
    [InlineData(ApProCatalogCategory.ShadowOfChernobyl, "https://ap-pro.ru/stuff/ten_chernobylja/")]
    [InlineData(ApProCatalogCategory.ClearSky, "https://ap-pro.ru/stuff/chistoe_nebo/")]
    [InlineData(ApProCatalogCategory.CallOfPripyat, "https://ap-pro.ru/stuff/zov_pripjati/")]
    public void GetCategoryUrlReturnsOfficialCategoryUrl(ApProCatalogCategory category, string expectedUrl)
    {
        Assert.Equal(expectedUrl, ApProCatalogService.GetCategoryUrl(category));
    }

    [Fact]
    public void GetTotalPageCountReadsPaginationMetadata()
    {
        const string html = "<ul class='ipsPagination' data-pages='10'><li>...</li></ul>";

        Assert.Equal(10, ApProCatalogParser.GetTotalPageCount(html));
    }

    [Fact]
    public void GetPageUrlReturnsExpectedUrlForFollowingPage()
    {
        var url = ApProCatalogService.GetPageUrl(ApProCatalogCategory.ShadowOfChernobyl, 2);

        Assert.Equal("https://ap-pro.ru/stuff/ten_chernobylja/page/2/?d=3", url);
    }

    [Fact]
    public async Task LoadPageAsyncUsesHonestLauncherUserAgent()
    {
        string? userAgent = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(CreateResponse(HttpStatusCode.OK, "<html></html>"));
        });
        var service = new ApProCatalogService(handler, TimeSpan.Zero);

        await service.LoadPageAsync(ApProCatalogCategory.ShadowOfChernobyl, 1);

        var assemblyVersion = typeof(ApProCatalogService).Assembly.GetName().Version?.ToString(3);
        Assert.StartsWith($"CORDON/{assemblyVersion}", userAgent);
        Assert.Contains("github.com/ITzSYUK/CORDON", userAgent);
        Assert.DoesNotContain("Mozilla", userAgent);
    }

    [Fact]
    public async Task LoadPageAsyncRetriesOnceAfterTooManyRequests()
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                var throttled = CreateResponse(HttpStatusCode.TooManyRequests, string.Empty);
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(throttled);
            }

            return Task.FromResult(CreateResponse(HttpStatusCode.OK, "<html></html>"));
        });
        var service = new ApProCatalogService(handler, TimeSpan.Zero);

        await service.LoadPageAsync(ApProCatalogCategory.ShadowOfChernobyl, 1);

        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task LoadPageAsyncLeavesMinimumIntervalBetweenPages()
    {
        var requestTimes = new ConcurrentQueue<TimeSpan>();
        var stopwatch = Stopwatch.StartNew();
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestTimes.Enqueue(stopwatch.Elapsed);
            return Task.FromResult(CreateResponse(HttpStatusCode.OK, "<html></html>"));
        });
        var service = new ApProCatalogService(handler, TimeSpan.FromMilliseconds(60));

        await service.LoadPageAsync(ApProCatalogCategory.ShadowOfChernobyl, 1);
        await service.LoadPageAsync(ApProCatalogCategory.ShadowOfChernobyl, 2);

        var times = requestTimes.ToArray();
        Assert.Equal(2, times.Length);
        Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(45));
    }

    [Fact]
    public async Task DownloadThumbnailAsyncLimitsConcurrentRequests()
    {
        var activeRequests = 0;
        var maximumActiveRequests = 0;
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            var active = Interlocked.Increment(ref activeRequests);
            UpdateMaximum(ref maximumActiveRequests, active);
            try
            {
                await Task.Delay(40, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                };
            }
            finally
            {
                Interlocked.Decrement(ref activeRequests);
            }
        });
        var service = new ApProCatalogService(
            handler,
            TimeSpan.Zero,
            maximumConcurrentThumbnailDownloads: 2);

        var downloads = Enumerable.Range(1, 8)
            .Select(index => service.DownloadThumbnailAsync($"https://ap-pro.ru/uploads/{index}.jpg"));
        var results = await Task.WhenAll(downloads);

        Assert.All(results, result => Assert.NotNull(result));
        Assert.InRange(maximumActiveRequests, 1, 2);
    }

    [Fact]
    public async Task DownloadThumbnailAsyncRejectsOversizedResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableMemoryStream([1, 2, 3, 4]))
        }));
        var service = new ApProCatalogService(
            handler,
            TimeSpan.Zero,
            maximumThumbnailBytes: 3);

        var result = await service.DownloadThumbnailAsync("https://ap-pro.ru/uploads/large.jpg");

        Assert.Null(result);
    }

    [Fact]
    public async Task LoadPageAsyncRejectsOversizedResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateResponse(
            HttpStatusCode.OK,
            "<html></html>")));
        var service = new ApProCatalogService(
            handler,
            TimeSpan.Zero,
            maximumCatalogPageBytes: 4);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.LoadPageAsync(ApProCatalogCategory.ShadowOfChernobyl, 1));

        Assert.Equal(
            LocalizedText.Format(
                Strings.Catalog_ContentTooLargeFormat,
                Strings.Catalog_PageDescription,
                4),
            error.Message);
    }

    [Fact]
    public async Task LoadPageAsyncCacheSupportsConcurrentRefreshes()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return CreateResponse(HttpStatusCode.OK, "<html></html>");
        });
        var service = new ApProCatalogService(handler, TimeSpan.Zero);

        var loads = Enumerable.Range(0, 40)
            .Select(index => service.LoadPageAsync(
                ApProCatalogCategory.ShadowOfChernobyl,
                index % 3 + 1,
                forceRefresh: index % 7 == 0));

        var pages = await Task.WhenAll(loads);

        Assert.Equal(40, pages.Length);
        Assert.All(pages, page => Assert.InRange(page.PageNumber, 1, 3));
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content)
    };

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (candidate <= current || Interlocked.CompareExchange(ref maximum, candidate, current) == current)
            {
                return;
            }
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class NonSeekableMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override bool CanSeek => false;
    }
}
