using System;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Certifications looked up on TMDB, keyed "movie:{tmdbId}" and "tv:{tmdbId}", plus the
    /// TMDB title and backdrop an IMDb id resolved to under "imdb:{imdbId}". A title TMDB has no
    /// rating for is kept too, so it isn't asked about again for a month.
    /// </summary>
    public class TmdbRatingCacheService : FileBackedCache<TmdbRatingEntry>
    {
        public TmdbRatingCacheService(ILogger logger) : base(logger, "tmdb_ratings_cache.json", "TMDB ratings") { }

        public TmdbRatingEntry? TryGet(string key, TimeSpan maxAge)
        {
            var cache = EnsureLoaded();
            if (cache.TryGetValue(key, out var entry) && DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
                return entry;
            return null;
        }

        public void Set(string key, TmdbRatingEntry entry)
        {
            entry.CachedAt = DateTimeOffset.UtcNow;
            EnsureLoaded()[key] = entry;
        }
    }

    public class TmdbRatingEntry
    {
        [JsonPropertyName("certification")] public string? Certification { get; set; }
        [JsonPropertyName("mediaType")] public string? MediaType { get; set; }
        [JsonPropertyName("tmdbId")] public long? TmdbId { get; set; }

        /// <summary>The backdrop TMDB's find gave for an IMDb id, a relative path like "/abc.jpg".</summary>
        [JsonPropertyName("backdropPath")] public string? BackdropPath { get; set; }

        /// <summary>
        /// Whether <see cref="BackdropPath"/> was looked for. An IMDb entry cached before backdrops
        /// were kept reads false, so it's looked up again instead of passing for one with none.
        /// </summary>
        [JsonPropertyName("backdropChecked")] public bool BackdropChecked { get; set; }

        [JsonPropertyName("cachedAt")] public DateTimeOffset CachedAt { get; set; }
    }
}
