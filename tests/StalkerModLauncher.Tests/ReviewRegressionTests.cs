using System.Diagnostics;
using System.Net;
using System.Net.Http;
using StalkerModLauncher.Models;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;
using StalkerModLauncher.ViewModels;
using Xunit;

namespace StalkerModLauncher.Tests;

public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CordonReviewTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentJunctionCannotAuthorizeWorkspaceOrDeleteSource(bool legacy)
    {
        var sourceRoot = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        var directoryName = legacy ? "old-probe" : "profile-probe";
        var source = Directory.CreateDirectory(Path.Combine(sourceRoot, directoryName)).FullName;
        var original = Path.Combine(source, "original.txt");
        File.WriteAllText(original, "keep");
        var link = Path.Combine(_root, "Workspaces");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link}' -Target '{sourceRoot}' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var workspace = Path.Combine(link, directoryName);
            var paths = new AppPaths(Path.Combine(_root, "config"), link, false);
            var builder = new WorkspaceBuilder(paths);
            var profile = new ModProfile { Id = "probe", WorkspacePath = legacy ? string.Empty : workspace };
            profile.Mods.Add(new ModEntry { SourcePath = source });
            var profiles = new List<ModProfile> { profile };
            Assert.Throws<InvalidOperationException>(() => builder.EnsureProfileWorkspace(profile, string.Empty));
            Assert.Throws<InvalidOperationException>(() => new ProfileManager(paths, builder).Delete(profiles, profile));
            Assert.Throws<InvalidOperationException>(() => FileSystemSafety.DeleteDirectoryContents(workspace, link));
            Assert.Throws<InvalidOperationException>(() => WorkspaceMaterializer.DeleteWorkspaceContents(workspace, link));
            Assert.Throws<InvalidOperationException>(() => LegacyProfileDataAlias.Delete(workspace, profile.Id));
            Assert.Single(profiles);
            Assert.Equal("keep", File.ReadAllText(original));
            Assert.False(File.Exists(Path.Combine(source, ".stalker-launcher-workspace")));
            Assert.False(File.Exists(Path.Combine(sourceRoot, WorkspaceBuilder.RootMarkerFileName)));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyToLocalCapturesBootstrapWithoutChangingSource(bool storeChanged)
    {
        var old = Directory.CreateDirectory(Path.Combine(_root, "old")).FullName;
        var bootstrap = Directory.CreateDirectory(Path.Combine(old, ".usvfs-bootstrap")).FullName;
        var runtimeFile = Path.Combine(bootstrap, "AnomalyLauncher.cfg");
        var storedFile = ProfileWritableGameFileStore.GetStoredFilePath(old, "AnomalyLauncher.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(storedFile)!);
        File.WriteAllText(runtimeFile, "baseline");
        File.WriteAllText(storedFile, "baseline");
        ProfileWritableGameFileStore.RecordBootstrapState(bootstrap);
        File.WriteAllText(runtimeFile, "latest");
        if (storeChanged) File.WriteAllText(storedFile, "stored-edit");
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "local"), false);
        var profile = new ModProfile { WorkspacePath = old };

        await new WorkspaceManagementService(new WorkspaceBuilder(paths)).CopyToLocalAsync(
            profile, paths.WorkspaceRoot, new Progress<string>(), () => Task.CompletedTask);

        Assert.Equal("latest", File.ReadAllText(ProfileWritableGameFileStore.GetStoredFilePath(profile.WorkspacePath, "AnomalyLauncher.cfg")));
        Assert.Equal("latest", File.ReadAllText(runtimeFile));
        Assert.Equal(storeChanged ? "stored-edit" : "baseline", File.ReadAllText(storedFile));
        Assert.False(Directory.Exists(Path.Combine(old, "diagnostics")));
        if (storeChanged)
            Assert.Equal("stored-edit", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(profile.WorkspacePath, "diagnostics"), "*.bak"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogPreservesPageErrorUntilNextSuccessfulRequest(bool failSecondPage)
    {
        var fail = true;
        using var handler = new Handler(request =>
        {
            var isSecond = request.RequestUri!.AbsolutePath.Contains("page/2", StringComparison.Ordinal);
            return fail && (!failSecondPage || isSecond)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<ul data-pages='2'></ul><article class='cCmsCategoryFeaturedEntry'><h1><a href='/stuff/test/'>Test</a></h1></article>") };
        });
        using var service = new ApProCatalogService(handler, TimeSpan.Zero);
        using var model = new ModCatalogViewModel(service);
        await model.LoadInitialAsync(ApProCatalogCategory.ShadowOfChernobyl);
        if (failSecondPage) await model.LoadNextPageAsync();
        var error = failSecondPage ? Strings.Catalog_PageFailed : Strings.Catalog_Unavailable;
        Assert.Equal(error, model.StatusText);
        model.SearchQuery = "unknown";
        Assert.Equal(error, model.StatusText);
        Assert.False(model.IsLoading);
        Assert.False(model.IsLoadingMore);
        fail = false;
        model.SearchQuery = string.Empty;
        await model.LoadInitialAsync(ApProCatalogCategory.ShadowOfChernobyl, forceRefresh: true);
        Assert.Equal(string.Empty, model.StatusText);
        Assert.Single(model.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledHttpBodyTimesOutAndReleasesRequestSlot(bool thumbnail)
    {
        var stall = true;
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = stall ? new StreamContent(new StalledStream()) : new StringContent("<html></html>")
        });
        using var service = new ApProCatalogService(handler, TimeSpan.Zero, maximumConcurrentThumbnailDownloads: 1);
        async Task Request()
        {
            if (thumbnail) await service.DownloadThumbnailAsync("https://ap-pro.ru/test.jpg");
            else await service.LoadPageAsync(ApProCatalogCategory.ShadowOfChernobyl, 1);
        }

        if (thumbnail) await Request().WaitAsync(TimeSpan.FromSeconds(25));
        else await Assert.ThrowsAsync<HttpRequestException>(() => Request().WaitAsync(TimeSpan.FromSeconds(25)));
        stall = false;
        await Request().WaitAsync(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
