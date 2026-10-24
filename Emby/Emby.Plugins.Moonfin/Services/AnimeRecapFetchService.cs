using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using Moonfin.Anime;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Finds which episodes are recaps, which AnimeFillerList has no category for. The episode
    /// data comes from api.tenrai.org, a mirror of the MyAnimeList episode lists that needs no
    /// account or key. A failed lookup records nothing, so the filler data is never held up.
    /// </summary>
    public class AnimeRecapFetchService
    {
        private const string EpisodeApiBase = "https://api.tenrai.org/v1";

        private const string AniListEndpoint = "https://graphql.anilist.co";

        // The episode API allows a few requests a second.
        private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromSeconds(2);

        // Guards against a malformed pagination response spinning forever, at 100 episodes a page.
        private const int MaxPages = 30;

        private const int MaxAttemptsPerPage = 3;

        private static readonly SemaphoreSlim RequestGate = new SemaphoreSlim(1, 1);
        private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly AnimeIdMappingService _mapping;
        private readonly AnimeMarkerDiagnosticLog _diagnostics;
        private readonly ILogger _logger;

        public AnimeRecapFetchService(
            AnimeIdMappingService mapping,
            AnimeMarkerDiagnosticLog diagnostics,
            ILogger logger)
        {
            _mapping = mapping;
            _diagnostics = diagnostics;
            _logger = logger;
        }

        /// <summary>
        /// Resolves a series to its MyAnimeList id from its own provider ids, then the offline
        /// mapping table, then AniList.
        /// </summary>
        public async Task<int?> TryResolveMalIdAsync(BaseItem series, CancellationToken cancellationToken)
        {
            if (AnimeProviderIds.TryGetInt(series, out var malId, "MyAnimeList", "Mal"))
            {
                return malId;
            }

            // The offline table is tried before AniList so most shows need no network request.
            await _mapping.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            var mapped = _mapping.Resolve(series);
            if (mapped != null)
            {
                return mapped;
            }

            if (AnimeProviderIds.TryGetInt(series, out var anilistId, "AniList"))
            {
                var viaAniList = await ResolveViaAniListAsync(anilistId, cancellationToken).ConfigureAwait(false);
                if (viaAniList != null)
                {
                    return viaAniList;
                }
            }

            // Last resort and the least reliable, since the search is fuzzy.
            return await ResolveByTitleAsync(series.Name, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Looks a series up by title on AniList. The search is fuzzy, so the answer only counts
        /// when one of its titles normalizes to the one we asked for.
        /// </summary>
        private async Task<int?> ResolveByTitleAsync(string? title, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            try
            {
                var media = await QueryAniListMediaAsync(
                    "query($search:String){Media(search:$search,type:ANIME){idMal title{romaji english native}}}",
                    new { search = title },
                    cancellationToken).ConfigureAwait(false);

                if (media == null || !(AnimeIdMappingService.ReadInt(media.Value, "idMal") is int malId))
                {
                    return null;
                }

                var wanted = AnimeTitleMatcher.Normalize(title);
                if (wanted.Length == 0 || !media.Value.TryGetProperty("title", out var titles))
                {
                    return null;
                }

                foreach (var field in new[] { "romaji", "english", "native" })
                {
                    if (titles.TryGetProperty(field, out var value) &&
                        value.ValueKind == JsonValueKind.String &&
                        AnimeTitleMatcher.Normalize(value.GetString()) == wanted)
                    {
                        return malId;
                    }
                }

                _logger.Debug("Recap lookup: the AniList search for " + title + " came back as a different show, ignoring it");
                return null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("AniList title search failed for " + title + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The episode numbers this entry marks as recaps. Null when the lookup failed, so the
        /// caller leaves the cached show alone rather than recording an empty answer.
        /// </summary>
        public async Task<RecapLookup?> FetchRecapsAsync(int malId, CancellationToken cancellationToken)
        {
            var recaps = new HashSet<int>();
            var episodeCount = 0;

            for (var page = 1; page <= MaxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var response = await GetEpisodePageAsync(malId, page, cancellationToken).ConfigureAwait(false);
                if (response == null)
                {
                    return null;
                }

                foreach (var episode in response.Data)
                {
                    episodeCount++;

                    if (episode.Recap)
                    {
                        recaps.Add(episode.MalId);
                    }
                }

                if (response.Pagination?.HasNextPage != true)
                {
                    return new RecapLookup(recaps, episodeCount);
                }
            }

            // Out of pages with more still to come. A partial answer would let the caller write
            // recap=false over every episode we never got to see.
            _logger.Debug("Recap lookup for MAL " + malId + " hit the page cap with more still to fetch");
            return null;
        }

        private async Task<MalEpisodesResponse?> GetEpisodePageAsync(int malId, int page, CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= MaxAttemptsPerPage; attempt++)
            {
                var (result, retryable) = await TryGetEpisodePageAsync(malId, page, cancellationToken).ConfigureAwait(false);
                if (result != null || !retryable)
                {
                    return result;
                }

                if (attempt < MaxAttemptsPerPage)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }

            return null;
        }

        private async Task<(MalEpisodesResponse? Page, bool Retryable)> TryGetEpisodePageAsync(
            int malId, int page, CancellationToken cancellationToken)
        {
            await WaitForSlotAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(20), "Moonfin/1.0");

                var url = EpisodeApiBase + "/anime/" + malId.ToString(CultureInfo.InvariantCulture)
                    + "/episodes?page=" + page.ToString(CultureInfo.InvariantCulture);

                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    var body = string.Empty;
                    try
                    {
                        body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (body.Length > 200)
                        {
                            body = body.Substring(0, 200);
                        }
                    }
                    catch (Exception)
                    {
                        // The status alone still says enough.
                    }

                    _diagnostics.Write($"    episodes MAL {malId} page {page} -> HTTP {status} {body}");

                    // 5xx and 429 mean the API is briefly unavailable. A 404 means the entry
                    // doesnt exist, so it isnt retried.
                    return (null, status >= 500 || status == 429);
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return (JsonSerializer.Deserialize<MalEpisodesResponse>(json, JsonOptions), false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                _diagnostics.Write(
                    $"    episodes MAL {malId} page {page} -> {ex.GetType().Name}: {ex.Message}" +
                    (ex.InnerException != null ? $" ({ex.InnerException.GetType().Name}: {ex.InnerException.Message})" : string.Empty));

                // No response at all means a timeout or a dropped connection, both worth another go.
                return (null, true);
            }
        }

        private async Task<int?> ResolveViaAniListAsync(int anilistId, CancellationToken cancellationToken)
        {
            try
            {
                var media = await QueryAniListMediaAsync(
                    "query($id:Int){Media(id:$id,type:ANIME){idMal}}",
                    new { id = anilistId },
                    cancellationToken).ConfigureAwait(false);

                return media == null ? null : AnimeIdMappingService.ReadInt(media.Value, "idMal");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("AniList idMal lookup failed for " + anilistId + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Posts one AniList query and hands back its data.Media object, or null when there
        /// isnt one.
        /// </summary>
        private static async Task<JsonElement?> QueryAniListMediaAsync(string query, object variables, CancellationToken cancellationToken)
        {
            using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(15), "Moonfin/1.0");

            var body = JsonSerializer.Serialize(new { query, variables });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(AniListEndpoint, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("Media", out var media) &&
                media.ValueKind == JsonValueKind.Object)
            {
                // Cloned because the document is disposed on the way out.
                return media.Clone();
            }

            return null;
        }

        private static async Task WaitForSlotAsync(CancellationToken cancellationToken)
        {
            await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var sinceLast = DateTimeOffset.UtcNow - _lastRequestAt;
                if (sinceLast < MinRequestSpacing)
                {
                    await Task.Delay(MinRequestSpacing - sinceLast, cancellationToken).ConfigureAwait(false);
                }

                _lastRequestAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                RequestGate.Release();
            }
        }

        /// <summary>
        /// The episode numbers marked as recaps, and how many episodes the entry covers.
        /// </summary>
        public readonly struct RecapLookup
        {
            public RecapLookup(HashSet<int> recapNumbers, int episodeCount)
            {
                RecapNumbers = recapNumbers;
                EpisodeCount = episodeCount;
            }

            public HashSet<int> RecapNumbers { get; }

            public int EpisodeCount { get; }
        }

        private class MalEpisodesResponse
        {
            [JsonPropertyName("data")]
            public List<MalEpisode> Data { get; set; } = new List<MalEpisode>();

            [JsonPropertyName("pagination")]
            public MalPagination? Pagination { get; set; }
        }

        private class MalPagination
        {
            [JsonPropertyName("has_next_page")]
            public bool HasNextPage { get; set; }
        }

        private class MalEpisode
        {
            // On the episodes endpoint this is the episode number within the entry.
            [JsonPropertyName("mal_id")]
            public int MalId { get; set; }

            [JsonPropertyName("recap")]
            public bool Recap { get; set; }
        }
    }
}
