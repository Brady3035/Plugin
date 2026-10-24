using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using Moonfin.Anime;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Fetches and caches the animefillerlist.com catalog and individual show pages.
    /// </summary>
    public class AnimeFillerListClient
    {
        private const string SiteBase = "https://www.animefillerlist.com";
        private const string CatalogFileName = "anime_filler_catalog.json";
        private const string UserAgent = "Moonfin/1.0 (+https://github.com/Moonfin-Client/Plugin)";

        // The site's robots.txt asks for 10 seconds between requests.
        public static readonly TimeSpan CrawlDelay = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromDays(14);

        // The catalog is checked on every client request, so a failed download has to back off.
        // Otherwise an unreachable site costs every request the crawl delay plus the timeout.
        private static readonly TimeSpan DownloadRetryInterval = TimeSpan.FromHours(6);

        private static readonly SemaphoreSlim RequestGate = new SemaphoreSlim(1, 1);
        private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

        private static readonly Dictionary<string, AnimeFillerShow> EmptyIndex =
            new Dictionary<string, AnimeFillerShow>(StringComparer.Ordinal);

        private readonly ILogger _logger;
        private readonly SemaphoreSlim _catalogLock = new SemaphoreSlim(1, 1);
        private readonly string _catalogPath;

        private Dictionary<string, AnimeFillerShow>? _index;
        private DateTimeOffset _lastDownloadAttempt = DateTimeOffset.MinValue;

        public AnimeFillerListClient(ILogger logger)
        {
            _logger = logger;
            var dataPath = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
            if (!Directory.Exists(dataPath)) Directory.CreateDirectory(dataPath);
            _catalogPath = Path.Combine(dataPath, CatalogFileName);
        }

        public bool CatalogLoaded => _index != null;

        public int CatalogCount { get; private set; }

        public DateTimeOffset? CatalogDownloadedAt =>
            File.Exists(_catalogPath) ? File.GetLastWriteTimeUtc(_catalogPath) : (DateTimeOffset?)null;

        /// <summary>The title lookup, empty until <see cref="EnsureCatalogAsync"/> succeeds.</summary>
        public IReadOnlyDictionary<string, AnimeFillerShow> Index => _index ?? EmptyIndex;

        /// <summary>
        /// Loads the show index, downloading it only when the cached copy is missing or stale.
        /// A failed download keeps whatever is already on disk.
        /// </summary>
        public async Task EnsureCatalogAsync(CancellationToken cancellationToken)
        {
            if (_index != null && !IsCatalogStale())
            {
                return;
            }

            await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_index != null && !IsCatalogStale())
                {
                    return;
                }

                if (IsCatalogStale() &&
                    DateTimeOffset.UtcNow - _lastDownloadAttempt > DownloadRetryInterval)
                {
                    _lastDownloadAttempt = DateTimeOffset.UtcNow;

                    var downloaded = await DownloadCatalogAsync(cancellationToken).ConfigureAwait(false);
                    if (downloaded != null)
                    {
                        Adopt(downloaded);
                        SaveCatalog(downloaded);
                        _logger.Info("Anime marker catalog refreshed (" + downloaded.Count + " shows)");
                        return;
                    }
                }

                if (_index == null)
                {
                    var fromDisk = LoadCatalogFromDisk();
                    if (fromDisk != null)
                    {
                        Adopt(fromDisk);
                    }
                }
            }
            finally
            {
                _catalogLock.Release();
            }
        }

        private void Adopt(List<AnimeFillerShow> shows)
        {
            CatalogCount = shows.Count;
            _index = AnimeTitleMatcher.BuildIndex(shows);
        }

        private bool IsCatalogStale() =>
            !File.Exists(_catalogPath) ||
            DateTime.UtcNow - File.GetLastWriteTimeUtc(_catalogPath) > CatalogMaxAge;

        private List<AnimeFillerShow>? LoadCatalogFromDisk()
        {
            try
            {
                if (!File.Exists(_catalogPath))
                {
                    return null;
                }

                using var stream = File.OpenRead(_catalogPath);
                return JsonSerializer.Deserialize<List<AnimeFillerShow>>(stream);
            }
            catch (Exception ex)
            {
                _logger.Warn("Anime marker catalog on disk could not be read, it will be downloaded again: " + ex.Message);
                return null;
            }
        }

        private void SaveCatalog(List<AnimeFillerShow> shows)
        {
            try
            {
                AtomicFile.WriteAllText(_catalogPath, JsonSerializer.Serialize(shows));
            }
            catch (Exception ex)
            {
                _logger.Warn("Anime marker catalog could not be written to disk: " + ex.Message);
            }
        }

        private async Task<List<AnimeFillerShow>?> DownloadCatalogAsync(CancellationToken cancellationToken)
        {
            var html = await GetStringAsync(SiteBase + "/shows", cancellationToken).ConfigureAwait(false);
            if (html == null)
            {
                return null;
            }

            var shows = AnimeFillerListParser.ParseShowIndex(html);

            // An index that parses to almost nothing means the markup moved, not that the site
            // lost its catalog.
            if (shows.Count < 50)
            {
                _logger.Warn("Anime marker catalog looked wrong (" + shows.Count + " shows parsed), keeping any existing copy");
                return null;
            }

            return shows;
        }

        /// <summary>
        /// One show's episode table. Null when the page cant be read, and empty only when the
        /// page lists no episodes.
        /// </summary>
        public async Task<List<AnimeMarkerEpisode>?> FetchEpisodesAsync(string slug, CancellationToken cancellationToken)
        {
            var html = await GetStringAsync(SiteBase + "/shows/" + slug, cancellationToken).ConfigureAwait(false);
            return html == null ? null : AnimeFillerListParser.ParseEpisodes(html);
        }

        private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            await WaitForSlotAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(30), UserAgent);
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Debug("Anime marker page " + url + " returned " + (int)response.StatusCode);
                    return null;
                }

                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("Anime marker page " + url + " could not be fetched: " + ex.Message);
                return null;
            }
        }

        private static async Task WaitForSlotAsync(CancellationToken cancellationToken)
        {
            await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var sinceLast = DateTimeOffset.UtcNow - _lastRequestAt;
                if (sinceLast < CrawlDelay)
                {
                    await Task.Delay(CrawlDelay - sinceLast, cancellationToken).ConfigureAwait(false);
                }

                _lastRequestAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                RequestGate.Release();
            }
        }
    }
}
