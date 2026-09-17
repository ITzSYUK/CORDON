using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using StalkerModLauncher.Infrastructure;
using StalkerModLauncher.Resources;
using StalkerModLauncher.Services;

namespace StalkerModLauncher.ViewModels;

public sealed class ModCatalogViewModel : ObservableObject, IDisposable
{
    private readonly ApProCatalogService _catalogService;
    private readonly List<ModCatalogItemViewModel> _allItems = new();
    private int _pageLoadActive;
    private CancellationTokenSource? _loadCancellation;
    private ApProCatalogCategory _selectedCategory = ApProCatalogCategory.ShadowOfChernobyl;
    private bool _isLoading;
    private bool _isLoadingMore;
    private bool _isSearchLoadingAll;
    private bool _hasMorePages;
    private int _nextPageNumber = 1;
    private int _totalPageCount = 1;
    private int _loadGeneration;
    private string _searchQuery = string.Empty;
    private string _statusText = string.Empty;

    public ModCatalogViewModel(ApProCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    public ObservableCollection<ModCatalogItemViewModel> Items { get; } = new();

    public ApProCatalogCategory SelectedCategory
    {
        get => _selectedCategory;
        private set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(CategoryTitle));
            }
        }
    }

    public string CategoryTitle => ApProCatalogService.GetCategoryTitle(SelectedCategory);

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilter();
                _ = EnsureAllPagesLoadedForSearchAsync();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set => SetProperty(ref _isLoadingMore, value);
    }

    public bool HasMorePages
    {
        get => _hasMorePages;
        private set => SetProperty(ref _hasMorePages, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CatalogProgressText
    {
        get
        {
            if (IsLoading)
            {
                return string.Empty;
            }

            var loaded = _allItems.Count;
            if (loaded == 0)
            {
                return string.Empty;
            }

            var shown = Items.Count;
            var pages = _totalPageCount > 1
                ? LocalizedText.Format(Strings.Catalog_PagesFormat, Math.Min(_nextPageNumber - 1, _totalPageCount), _totalPageCount)
                : string.Empty;
            return string.IsNullOrWhiteSpace(SearchQuery)
                ? LocalizedText.Format(Strings.Catalog_LoadedFormat, loaded, pages)
                : LocalizedText.Format(Strings.Catalog_ShownFormat, shown, loaded, pages);
        }
    }

    public async Task LoadInitialAsync(ApProCatalogCategory category, bool forceRefresh = false)
    {
        if (_loadCancellation is not null)
        {
            await _loadCancellation.CancelAsync();
        }

        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        _loadGeneration++;
        var generation = _loadGeneration;
        var cancellationToken = _loadCancellation.Token;

        try
        {
            SelectedCategory = category;
            IsLoading = true;
            IsLoadingMore = false;
            _isSearchLoadingAll = false;
            StatusText = Strings.Catalog_LoadingService;
            OnPropertyChanged(nameof(CatalogProgressText));

            DisposeItems();
            _nextPageNumber = 1;
            _totalPageCount = 1;
            HasMorePages = false;

            var page = await _catalogService.LoadPageAsync(category, _nextPageNumber, forceRefresh, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _loadGeneration)
            {
                return;
            }

            AddItems(page.Items, cancellationToken);
            _totalPageCount = page.TotalPages;
            _nextPageNumber = page.PageNumber + 1;
            HasMorePages = page.Items.Count > 0 && _nextPageNumber <= _totalPageCount;

            UpdateEmptyStatus();
            OnPropertyChanged(nameof(CatalogProgressText));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (generation == _loadGeneration)
            {
                StatusText = Strings.Catalog_Unavailable;
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && generation == _loadGeneration)
            {
                IsLoading = false;
                UpdateEmptyStatus();
                _ = EnsureAllPagesLoadedForSearchAsync();
            }
        }
    }

    public async Task LoadNextPageAsync()
    {
        if (IsLoading || IsLoadingMore || !HasMorePages || _loadCancellation is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _pageLoadActive, 1, 0) != 0)
        {
            await Task.Delay(50);
            return;
        }

        var generation = _loadGeneration;
        var cancellationToken = _loadCancellation.Token;
        try
        {
            IsLoadingMore = true;
            UpdateEmptyStatus();
            var page = await _catalogService.LoadPageAsync(SelectedCategory, _nextPageNumber, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _loadGeneration)
            {
                return;
            }

            AddItems(page.Items, cancellationToken);
            _totalPageCount = Math.Max(_totalPageCount, page.TotalPages);
            _nextPageNumber = page.PageNumber + 1;
            HasMorePages = page.Items.Count > 0 && _nextPageNumber <= _totalPageCount;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (generation == _loadGeneration)
            {
                StatusText = Strings.Catalog_PageFailed;
                HasMorePages = false;
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoadingMore = false;
                UpdateEmptyStatus();
                OnPropertyChanged(nameof(CatalogProgressText));
            }

            Volatile.Write(ref _pageLoadActive, 0);
        }
    }

    public static void OpenListing(ModCatalogItemViewModel? item)
    {
        if (item is not null)
        {
            DialogService.OpenUrl(item.DetailUrl);
        }
    }

    public static void OpenWebsite() => DialogService.OpenUrl("https://ap-pro.ru/");

    public void Dispose()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _loadGeneration++;
        DisposeItems();
    }

    private void AddItems(IEnumerable<ApProModListing> listings, CancellationToken cancellationToken)
    {
        var existingUrls = _allItems.Select(item => item.DetailUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in listings.Where(listing => existingUrls.Add(listing.DetailUrl)))
        {
            var item = new ModCatalogItemViewModel(listing, _catalogService, cancellationToken);
            _allItems.Add(item);
            if (MatchesSearch(item))
            {
                Items.Add(item);
            }

            item.StartThumbnailLoading();
        }

        UpdateEmptyStatus();
        OnPropertyChanged(nameof(CatalogProgressText));
    }

    private void DisposeItems()
    {
        foreach (var item in _allItems)
        {
            item.Dispose();
        }

        _allItems.Clear();
        Items.Clear();
        OnPropertyChanged(nameof(CatalogProgressText));
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var item in _allItems.Where(MatchesSearch))
        {
            Items.Add(item);
        }

        UpdateEmptyStatus();
        OnPropertyChanged(nameof(CatalogProgressText));
    }

    private bool MatchesSearch(ModCatalogItemViewModel item)
    {
        var query = SearchQuery.Trim();
        return string.IsNullOrWhiteSpace(query) ||
               item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateEmptyStatus()
    {
        if (Items.Count > 0 || IsLoading)
        {
            StatusText = string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            StatusText = Strings.Catalog_Empty;
            return;
        }

        StatusText = _isSearchLoadingAll || IsLoadingMore || HasMorePages
            ? Strings.Catalog_Searching
            : Strings.Catalog_NoResults;
    }

    private async Task EnsureAllPagesLoadedForSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery) ||
            _isSearchLoadingAll ||
            IsLoading ||
            !HasMorePages ||
            _loadCancellation is null)
        {
            return;
        }

        var generation = _loadGeneration;
        var cancellationToken = _loadCancellation.Token;
        try
        {
            _isSearchLoadingAll = true;
            UpdateEmptyStatus();
            while (!cancellationToken.IsCancellationRequested &&
                   !string.IsNullOrWhiteSpace(SearchQuery) &&
                   generation == _loadGeneration &&
                   HasMorePages)
            {
                if (IsLoadingMore)
                {
                    await Task.Delay(50, cancellationToken);
                    continue;
                }

                await LoadNextPageAsync();
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _isSearchLoadingAll = false;
                UpdateEmptyStatus();
            }
        }
    }
}

public sealed class ModCatalogItemViewModel : ObservableObject, IDisposable
{
    private readonly ApProModListing _listing;
    private readonly ApProCatalogService _catalogService;
    private readonly CancellationToken _parentCancellation;
    private readonly CancellationTokenSource _thumbnailCancellation = new();
    private BitmapImage? _thumbnail;
    private bool _isDisposed;

    public ModCatalogItemViewModel(
        ApProModListing listing,
        ApProCatalogService catalogService,
        CancellationToken parentCancellation)
    {
        _listing = listing;
        _catalogService = catalogService;
        _parentCancellation = parentCancellation;
    }

    public string Title => _listing.Title;
    public string Description => _listing.Description;
    public string DetailUrl => _listing.DetailUrl;
    public string Metadata => string.Join("  ", new[]
    {
        _listing.Rating is null ? null : LocalizedText.Format(Strings.Catalog_RatingFormat, _listing.Rating),
        _listing.Views
    }.Where(value => !string.IsNullOrWhiteSpace(value))!);

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }

    public void StartThumbnailLoading()
    {
        if (string.IsNullOrWhiteSpace(_listing.ThumbnailUrl))
        {
            return;
        }

        _ = LoadThumbnailAsync();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _thumbnailCancellation.Cancel();
        _thumbnailCancellation.Dispose();
        Thumbnail = null;
    }

    private async Task LoadThumbnailAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_parentCancellation, _thumbnailCancellation.Token);
        try
        {
            var bytes = await _catalogService.DownloadThumbnailAsync(_listing.ThumbnailUrl!, cancellation.Token);
            if (bytes is null || _isDisposed)
            {
                return;
            }

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 460;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Thumbnail = image;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The listing stays usable when its preview is unavailable.
        }
    }
}
