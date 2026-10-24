using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Downloads and caches a table mapping anime provider ids to MyAnimeList ids, so the recap
    /// pass can resolve a show without going to the network. The table comes from
    /// Fribb/anime-lists, a community-maintained mirror of AniList's own mapping.
    /// </summary>
    public class AnimeIdMappingService
    {
        private const string MappingUrl =
            "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json";

        private const string MappingFileName = "anime_id_mapping.json";

        private static readonly TimeSpan MappingMaxAge = TimeSpan.FromDays(14);

        // A stale copy isnt downloaded more often than this, so a failing download doesnt hammer GitHub.
        private static readonly TimeSpan DownloadRetryInterval = TimeSpan.FromHours(6);

        private readonly ILogger _logger;
        private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);
        private readonly string _mappingPath;

        private MappingIndex? _index;
        private DateTime? _indexWrittenAt;
        private DateTimeOffset _lastDownloadAttempt = DateTimeOffset.MinValue;

        public AnimeIdMappingService(ILogger logger)
        {
            _logger = logger;
            var dataPath = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
            if (!Directory.Exists(dataPath)) Directory.CreateDirectory(dataPath);
            _mappingPath = Path.Combine(dataPath, MappingFileName);
        }

        /// <summary>
        /// Loads the table, downloading it only when the copy on disk is missing or stale. A
        /// failed download keeps whatever is already there.
        /// </summary>
        public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (_index != null && !IsStale())
            {
                return;
            }

            await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_index != null && !IsStale())
                {
                    return;
                }

                if (IsStale() && DateTimeOffset.UtcNow - _lastDownloadAttempt > DownloadRetryInterval)
                {
                    _lastDownloadAttempt = DateTimeOffset.UtcNow;
                    await DownloadAsync(cancellationToken).ConfigureAwait(false);
                }

                // Only reparse when the file has changed, or a stale copy that cant be refreshed
                // gets deserialized again on every lookup.
                if (File.Exists(_mappingPath))
                {
                    var writtenAt = File.GetLastWriteTimeUtc(_mappingPath);
                    if (_index == null || _indexWrittenAt != writtenAt)
                    {
                        _index = Parse(_mappingPath);
                        _indexWrittenAt = writtenAt;
                        _logger.Info("Anime id mapping loaded (" + _index.Count + " entries with a MyAnimeList id)");
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn("Anime id mapping could not be loaded: " + ex.Message);
            }
            finally
            {
                _loadLock.Release();
            }
        }

        private bool IsStale() =>
            !File.Exists(_mappingPath) ||
            DateTime.UtcNow - File.GetLastWriteTimeUtc(_mappingPath) > MappingMaxAge;

        private async Task DownloadAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var client = MoonfinHttp.CreateClient(TimeSpan.FromMinutes(2), "Moonfin/1.0");
                using var response = await client.GetAsync(MappingUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn("Anime id mapping download returned " + (int)response.StatusCode + ", keeping any existing copy");
                    return;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                AtomicFile.WriteAllText(_mappingPath, json);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                _logger.Warn("Anime id mapping download failed, keeping any existing copy: " + ex.Message);
            }
        }

        // For tests, which check the parsing without a download.
        internal void LoadFrom(string path) => _index = Parse(path);

        private static MappingIndex Parse(string path)
        {
            var index = new MappingIndex();

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var malId = ReadInt(entry, "mal_id");
                if (malId == null)
                {
                    continue;
                }

                index.Count++;

                Add(index.ByAniList, ReadInt(entry, "anilist_id"), malId.Value);
                Add(index.ByAniDb, ReadInt(entry, "anidb_id"), malId.Value);
                Add(index.ByAniSearch, ReadInt(entry, "anisearch_id"), malId.Value);
                Add(index.ByKitsu, ReadInt(entry, "kitsu_id"), malId.Value);
                foreach (var imdbId in ReadStrings(entry, "imdb_id"))
                {
                    Add(index.ByImdb, imdbId, malId.Value);
                }

                // One TVDB or TMDB id covers a whole show, while MyAnimeList splits it per
                // season, so the first entry wins. That is the earliest season, and the recap
                // pass only applies numbers inside the entry's own episode count anyway.
                Add(index.ByTvdb, ReadInt(entry, "tvdb_id"), malId.Value);

                if (entry.TryGetProperty("themoviedb_id", out var tmdb) && tmdb.ValueKind == JsonValueKind.Object)
                {
                    Add(index.ByTmdb, ReadInt(tmdb, "tv"), malId.Value);
                }
            }

            return index;
        }

        /// <summary>
        /// Resolves one provider id, named the way <see cref="AnimeTitleAliasService.ProviderKeys"/>
        /// names them, to a MyAnimeList id.
        /// </summary>
        public int? ResolveProvider(string provider, string value)
        {
            var index = _index;
            if (index == null || string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (provider == "imdb")
            {
                return index.ByImdb.TryGetValue(trimmed, out var fromImdb) ? fromImdb : (int?)null;
            }

            if (!int.TryParse(trimmed, out var id))
            {
                return null;
            }

            var map = provider switch
            {
                "anilist" => index.ByAniList,
                "anidb" => index.ByAniDb,
                "anisearch" => index.ByAniSearch,
                "kitsu" => index.ByKitsu,
                "tvdb" => index.ByTvdb,
                "tmdb" => index.ByTmdb,
                _ => null
            };

            return map != null && map.TryGetValue(id, out var malId) ? malId : (int?)null;
        }

        /// <summary>
        /// A MyAnimeList id for a series from the provider ids it already has, trying the anime
        /// databases before TVDB, TMDB and IMDb.
        /// </summary>
        public int? Resolve(BaseItem series)
        {
            foreach (var (_, provider, value) in AnimeTitleAliasService.ProviderKeys(series))
            {
                var malId = ResolveProvider(provider, value);
                if (malId != null)
                {
                    return malId;
                }
            }

            return null;
        }

        private static void Add(Dictionary<int, int> map, int? key, int malId)
        {
            if (key != null)
            {
                map.TryAdd(key.Value, malId);
            }
        }

        private static void Add(Dictionary<string, int> map, string? key, int malId)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                map.TryAdd(key.Trim(), malId);
            }
        }

        internal static int? ReadInt(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var parsed)
                ? parsed
                : (int?)null;

        /// <summary>
        /// Reads a property that is either one string or an array of them.
        /// </summary>
        private static IEnumerable<string?> ReadStrings(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value))
            {
                yield break;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                yield return value.GetString();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        yield return item.GetString();
                    }
                }
            }
        }

        private class MappingIndex
        {
            public int Count { get; set; }
            public Dictionary<int, int> ByAniList { get; } = new Dictionary<int, int>();
            public Dictionary<int, int> ByAniDb { get; } = new Dictionary<int, int>();
            public Dictionary<int, int> ByAniSearch { get; } = new Dictionary<int, int>();
            public Dictionary<int, int> ByKitsu { get; } = new Dictionary<int, int>();
            public Dictionary<int, int> ByTvdb { get; } = new Dictionary<int, int>();
            public Dictionary<int, int> ByTmdb { get; } = new Dictionary<int, int>();
            public Dictionary<string, int> ByImdb { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Emby can hand back a null provider id dictionary.
    internal static class AnimeProviderIds
    {
        private static readonly Dictionary<string, string> Empty = new Dictionary<string, string>();

        public static Dictionary<string, string> Of(BaseItem item) =>
            (Dictionary<string, string>?)item.ProviderIds ?? Empty;

        public static bool TryGetInt(BaseItem item, out int value, params string[] keys)
        {
            foreach (var key in keys)
            {
                foreach (var pair in Of(item))
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(pair.Value?.Trim(), out value))
                    {
                        return true;
                    }
                }
            }

            value = 0;
            return false;
        }
    }
}
