using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using Moonfin.Anime;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Looks up the English and romaji titles behind a series' anime provider ids, so a series
    /// with a localized library name ("El detectiu Conan") can still match the title
    /// AnimeFillerList uses. Titles come from AniList and are cached per provider id, keyed like
    /// "anidb:266", so the request path never goes to the network.
    /// </summary>
    public class AnimeTitleAliasService : FileBackedCache<AnimeTitleAliasEntry>
    {
        private const string AniListEndpoint = "https://graphql.anilist.co";

        private const string MediaQuery =
            "query($ids:[Int]){Page(perPage:50){media({0}:$ids,type:ANIME){id idMal title{romaji english} synonyms}}}";

        private const int BatchSize = 50;

        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

        // AniList allows 30 requests a minute while degraded, which is most of the time.
        private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromSeconds(2.5);

        // Tried in this order. The anime databases come first because they name one entry, where
        // a TVDB or TMDB id covers a whole show that MyAnimeList splits into several.
        private static readonly (string Name, string[] Keys)[] Providers =
        {
            ("mal", new[] { "MyAnimeList", "Mal" }),
            ("anilist", new[] { "AniList" }),
            ("anidb", new[] { "AniDB" }),
            ("kitsu", new[] { "Kitsu", "KitsuIo" }),
            ("anisearch", new[] { "AniSearch" }),
            ("tvdb", new[] { "Tvdb", "TheTVDB" }),
            ("tmdb", new[] { "Tmdb", "TheMovieDb" }),
            ("imdb", new[] { "Imdb" })
        };

        private readonly AnimeIdMappingService _mapping;
        private readonly AnimeMarkerDiagnosticLog _diagnostics;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

        public AnimeTitleAliasService(
            AnimeIdMappingService mapping,
            AnimeMarkerDiagnosticLog diagnostics,
            ILogger logger)
            : base(logger, "anime_title_aliases.json", "Anime title aliases")
        {
            _mapping = mapping;
            _diagnostics = diagnostics;
            _logger = logger;
        }

        public static IEnumerable<(string Key, string Provider, string Value)> ProviderKeys(BaseItem series)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (name, keys) in Providers)
            {
                foreach (var pair in AnimeProviderIds.Of(series))
                {
                    if (string.IsNullOrWhiteSpace(pair.Value) ||
                        !keys.Any(key => string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var value = pair.Value.Trim();
                    var key = name + ":" + value;
                    if (seen.Add(key))
                    {
                        yield return (key, name, value);
                    }
                }
            }
        }

        /// <summary>
        /// The cached titles for each of the series' provider ids, best first. Never goes to the
        /// network, so a series nobody has refreshed yet has none.
        /// </summary>
        public IEnumerable<(string Key, AnimeTitleAliasEntry Entry)> GetAliases(BaseItem series)
        {
            var cache = EnsureLoaded();

            foreach (var (key, _, _) in ProviderKeys(series))
            {
                if (cache.TryGetValue(key, out var entry) && entry.Titles.Count > 0)
                {
                    yield return (key, entry);
                }
            }
        }

        /// <summary>
        /// Fetches titles for every provider id that isnt cached or is older than a month. Ids
        /// that cant be tied to an anime entry offline cost nothing, so a whole library can be
        /// handed over. Returns how many ids were stored.
        /// </summary>
        public async Task<int> RefreshAsync(IEnumerable<BaseItem> seriesList, CancellationToken cancellationToken)
        {
            var pending = seriesList.ToList();
            if (pending.Count == 0)
            {
                return 0;
            }

            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _mapping.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

                var cache = EnsureLoaded();
                var now = DateTimeOffset.UtcNow;

                // An AniList id is asked about directly. Anything else needs a MyAnimeList id,
                // either stated outright or found through the offline mapping table.
                var byAniListId = new Dictionary<int, List<string>>();
                var byMalId = new Dictionary<int, List<string>>();

                foreach (var series in pending)
                {
                    foreach (var (key, provider, value) in ProviderKeys(series))
                    {
                        if (cache.TryGetValue(key, out var cached) && now - cached.FetchedAt < MaxAge)
                        {
                            continue;
                        }

                        if (provider == "anilist" && int.TryParse(value, out var aniListId))
                        {
                            AddKey(byAniListId, aniListId, key);
                            continue;
                        }

                        int? malId = provider == "mal"
                            ? int.TryParse(value, out var stated) ? stated : (int?)null
                            : _mapping.ResolveProvider(provider, value);

                        if (malId != null)
                        {
                            AddKey(byMalId, malId.Value, key);
                        }
                    }
                }

                var stored = await FetchAndStoreAsync("id_in", byAniListId, media => media.Id, cancellationToken).ConfigureAwait(false);
                stored += await FetchAndStoreAsync("idMal_in", byMalId, media => media.MalId, cancellationToken).ConfigureAwait(false);

                if (stored > 0)
                {
                    await FlushAsync().ConfigureAwait(false);
                    _logger.Info("Anime markers: titles looked up for " + stored + " provider ids");
                }

                return stored;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task<int> FetchAndStoreAsync(
            string field,
            Dictionary<int, List<string>> keysById,
            Func<AniListMedia, int?> idOf,
            CancellationToken cancellationToken)
        {
            var stored = 0;
            var cache = EnsureLoaded();
            var ids = keysById.Keys.ToList();

            for (var start = 0; start < ids.Count; start += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = ids.GetRange(start, Math.Min(BatchSize, ids.Count - start)).ToArray();
                var media = await QueryAsync(field, batch, cancellationToken).ConfigureAwait(false);
                if (media == null)
                {
                    // A failed request stores nothing, so the next run asks again.
                    continue;
                }

                var found = new Dictionary<int, AniListMedia>();
                foreach (var item in media)
                {
                    if (idOf(item) is int id)
                    {
                        found.TryAdd(id, item);
                    }
                }

                foreach (var id in batch)
                {
                    // An id AniList has no entry for is stored empty, so it isnt asked again until it goes stale.
                    found.TryGetValue(id, out var item);
                    var entry = new AnimeTitleAliasEntry
                    {
                        MalId = item?.MalId,
                        Titles = item?.Titles ?? new List<string>(),
                        FetchedAt = DateTimeOffset.UtcNow
                    };

                    foreach (var key in keysById[id])
                    {
                        cache[key] = entry;
                        stored++;
                        _diagnostics.Write(entry.Titles.Count == 0
                            ? $"alias {key}: AniList has no entry for it"
                            : $"alias {key}: MAL {entry.MalId?.ToString(CultureInfo.InvariantCulture) ?? "?"} " +
                              $"known as {string.Join(" | ", entry.Titles.Take(5))}");
                    }
                }
            }

            return stored;
        }

        private async Task<List<AniListMedia>?> QueryAsync(string field, int[] ids, CancellationToken cancellationToken)
        {
            var sinceLast = DateTimeOffset.UtcNow - _lastRequestAt;
            if (sinceLast < MinRequestSpacing)
            {
                await Task.Delay(MinRequestSpacing - sinceLast, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;

            try
            {
                using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(20), "Moonfin/1.0");

                var body = JsonSerializer.Serialize(new
                {
                    query = MediaQuery.Replace("{0}", field),
                    variables = new { ids }
                });

                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(AniListEndpoint, content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _diagnostics.Write($"alias AniList {field} lookup of {ids.Length} ids -> HTTP {(int)response.StatusCode}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseMedia(json);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                _diagnostics.Write($"alias AniList {field} lookup of {ids.Length} ids -> {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads a Page.media response. Null when the response isnt one, so a malformed answer
        /// isnt mistaken for AniList having no entries.
        /// </summary>
        public static List<AniListMedia>? ParseMedia(string json)
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("Page", out var page) ||
                page.ValueKind != JsonValueKind.Object ||
                !page.TryGetProperty("media", out var mediaList) ||
                mediaList.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<AniListMedia>();

            foreach (var media in mediaList.EnumerateArray())
            {
                if (media.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var titles = new List<string>();
                if (media.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.Object)
                {
                    AddTitle(titles, title, "english");
                    AddTitle(titles, title, "romaji");
                }

                if (media.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var synonym in synonyms.EnumerateArray())
                    {
                        if (synonym.ValueKind == JsonValueKind.String)
                        {
                            AddTitle(titles, synonym.GetString());
                        }
                    }
                }

                result.Add(new AniListMedia(
                    AnimeIdMappingService.ReadInt(media, "id"),
                    AnimeIdMappingService.ReadInt(media, "idMal"),
                    titles));
            }

            return result;
        }

        private static void AddTitle(List<string> titles, JsonElement parent, string property)
        {
            if (parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            {
                AddTitle(titles, value.GetString());
            }
        }

        // Titles in a non-Latin script fold to nothing and could never match, so they arent kept.
        private static void AddTitle(List<string> titles, string? title)
        {
            if (string.IsNullOrWhiteSpace(title) || AnimeTitleMatcher.Normalize(title).Length == 0)
            {
                return;
            }

            var trimmed = title.Trim();
            if (!titles.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                titles.Add(trimmed);
            }
        }

        private static void AddKey(Dictionary<int, List<string>> map, int id, string key)
        {
            if (!map.TryGetValue(id, out var keys))
            {
                keys = new List<string>();
                map[id] = keys;
            }

            if (!keys.Contains(key))
            {
                keys.Add(key);
            }
        }

        /// <summary>One AniList entry, with its usable titles English first.</summary>
        public sealed class AniListMedia
        {
            public AniListMedia(int? id, int? malId, List<string> titles)
            {
                Id = id;
                MalId = malId;
                Titles = titles;
            }

            public int? Id { get; }

            public int? MalId { get; }

            public List<string> Titles { get; }
        }
    }
}
