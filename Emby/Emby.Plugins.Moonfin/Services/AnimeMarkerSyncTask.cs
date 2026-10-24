using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Moonfin.Anime;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Fetches the AnimeFillerList catalog and the show pages behind it, then caches the episode
    /// classifications for the library to draw from.
    /// </summary>
    public class AnimeMarkerSyncTask : IScheduledTask
    {
        public string Name => "Moonfin Anime Markers Sync";
        public string Key => "Moonfin.Anime.MarkerSync";
        public string Description =>
            "Fetches filler, canon and recap classifications for the anime in your library from AnimeFillerList. No account or API key is needed.";
        public string Category => "Moonfin";

        // A run stops after this and leaves the rest for tomorrow.
        private static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(45);

        private const int FlushEveryNShows = 10;

        // An unreachable site looks the same from every show, so after this many failures in a
        // row working through the rest gains nothing.
        private const int MaxConsecutiveFailures = 5;

        private readonly ILogger _logger;

        public AnimeMarkerSyncTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger("MoonfinAnimeMarkers");
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            var configuration = plugin?.Configuration;
            if (configuration?.AnimeMarkersEnabled != true)
            {
                _logger.Info("Anime markers sync skipped: disabled in configuration");
                return;
            }

            var client = plugin?.AnimeFillerList;
            var resolver = plugin?.AnimeMarkerResolver;
            var cache = plugin?.AnimeMarkerCache;
            var recapService = plugin?.AnimeRecapFetch;
            var diagnostics = plugin?.AnimeMarkerDiagnostics;
            if (client == null || resolver == null || cache == null || recapService == null || diagnostics == null)
            {
                _logger.Warn("Anime markers sync skipped: plugin services not initialized yet");
                return;
            }

            var clock = Stopwatch.StartNew();
            progress.Report(0);

            await client.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (!client.CatalogLoaded)
            {
                _logger.Warn("Anime markers sync aborted: the show catalog could not be loaded. Check that the server can reach animefillerlist.com");
                return;
            }

            progress.Report(5);

            // A localized library name never matches the site's English titles, so the titles
            // behind those series' provider ids are fetched before matching.
            var candidates = resolver.GetCandidateSeries();
            try
            {
                await resolver.RefreshAliasesAsync(candidates, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn("Anime markers sync: the provider id title lookup failed, matching on library titles only: " + ex.Message);
            }

            var matches = resolver.BuildMatches(candidates);
            var showsBySlug = matches
                .GroupBy(match => match.Show.Slug, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Show, StringComparer.OrdinalIgnoreCase);

            _logger.Info("Anime markers sync: " + matches.Count + " series matched " + showsBySlug.Count + " shows on AnimeFillerList");

            var maxAge = TimeSpan.FromDays(Math.Max(1, configuration.AnimeMarkerMaxAgeDays));
            var fresh = cache.GetFreshSlugs(maxAge);
            var pending = showsBySlug.Keys.Where(slug => !fresh.Contains(slug)).ToList();

            if (pending.Count == 0)
            {
                _logger.Info("Anime markers sync: every matched show is already cached");
            }
            else
            {
                _logger.Info(
                    "Anime markers sync: fetching " + pending.Count + " shows, about " +
                    Math.Max(1, (int)(pending.Count * AnimeFillerListClient.CrawlDelay.TotalMinutes)) +
                    " minutes at the site's requested crawl delay");
            }

            await FetchShowsAsync(client, cache, diagnostics, pending, showsBySlug, clock, progress, cancellationToken).ConfigureAwait(false);

            if (configuration.AnimeMarkerRecapLookup)
            {
                await FetchRecapsAsync(cache, recapService, diagnostics, matches, clock, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                diagnostics.Write("recap pass skipped: the recap lookup is switched off in settings");
            }

            await cache.FlushAsync().ConfigureAwait(false);

            _logger.Info(
                "Anime markers sync finished in " + clock.Elapsed + ": " + cache.EntryCount() + " shows cached, " +
                cache.TotalEpisodeCount() + " episodes classified, " + cache.FlaggedEpisodeCount() + " of them filler or mixed");

            progress.Report(100);
        }

        private async Task FetchShowsAsync(
            AnimeFillerListClient client,
            AnimeMarkerCacheService cache,
            AnimeMarkerDiagnosticLog diagnostics,
            List<string> pending,
            Dictionary<string, AnimeFillerShow> showsBySlug,
            Stopwatch clock,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var processed = 0;
            var fetched = 0;
            var failed = 0;
            var consecutiveFailures = 0;
            var sinceFlush = 0;

            foreach (var slug in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (clock.Elapsed > RunBudget)
                {
                    _logger.Info(
                        "Anime markers sync: run budget reached with " + (pending.Count - processed) +
                        " shows left, they will be fetched on the next run");
                    break;
                }

                var episodes = await client.FetchEpisodesAsync(slug, cancellationToken).ConfigureAwait(false);
                processed++;

                diagnostics.Write(
                    episodes == null
                        ? $"sync  fetch {slug} -> the page could not be read"
                        : $"sync  fetch {slug} -> {episodes.Count} episodes");

                progress.Report(5 + ((double)processed / pending.Count * 75));

                if (episodes == null)
                {
                    failed++;
                    consecutiveFailures++;

                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        _logger.Warn(
                            "Anime markers sync stopped early: " + consecutiveFailures +
                            " shows failed in a row, so the site is unreachable rather than the shows being missing. " +
                            fetched + " were cached first");
                        break;
                    }

                    continue;
                }

                consecutiveFailures = 0;

                if (episodes.Count == 0)
                {
                    _logger.Debug("Anime markers: " + slug + " listed no episodes, not caching it");
                    continue;
                }

                cache.Set(slug, new AnimeMarkerCacheEntry
                {
                    Slug = slug,
                    Title = showsBySlug[slug].Title,
                    Episodes = episodes
                });

                fetched++;
                sinceFlush++;

                if (sinceFlush >= FlushEveryNShows)
                {
                    await cache.FlushAsync().ConfigureAwait(false);
                    sinceFlush = 0;

                    _logger.Info(
                        "Anime markers sync progress: " + processed + "/" + pending.Count + " shows, " +
                        fetched + " cached, " + failed + " unreadable");
                }
            }

            progress.Report(80);
        }

        /// <summary>
        /// Fetches recap flags for every matched show that has a cached table but no recap flags yet.
        /// </summary>
        private async Task FetchRecapsAsync(
            AnimeMarkerCacheService cache,
            AnimeRecapFetchService recapService,
            AnimeMarkerDiagnosticLog diagnostics,
            List<SeriesMatch> matches,
            Stopwatch clock,
            CancellationToken cancellationToken)
        {
            var updated = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (clock.Elapsed > RunBudget)
                {
                    break;
                }

                if (!seen.Add(match.Show.Slug))
                {
                    continue;
                }

                var entry = cache.TryGetAny(match.Show.Slug);
                if (entry == null)
                {
                    diagnostics.Write($"recap {match.Show.Slug}: skipped, the show has no cached table yet");
                    continue;
                }

                if (entry.RecapMalId != null)
                {
                    continue;
                }

                // A match made through a provider id's titles already knows which entry named the
                // show. Resolving again could land on another, such as a movie sharing the ids.
                var malId = match.MalId
                    ?? await recapService.TryResolveMalIdAsync(match.Series, cancellationToken).ConfigureAwait(false);
                if (malId == null)
                {
                    diagnostics.Write(
                        $"recap {match.Show.Slug}: no MyAnimeList id for \"{match.Series.Name}\" from its "
                        + $"provider ids ({string.Join(", ", AnimeProviderIds.Of(match.Series).Select(pair => pair.Key + "=" + pair.Value))}), "
                        + "the offline mapping table, or the AniList search");
                    continue;
                }

                var lookup = await recapService.FetchRecapsAsync(malId.Value, cancellationToken).ConfigureAwait(false);
                if (lookup == null)
                {
                    diagnostics.Write(
                        $"recap {match.Show.Slug}: the episode API would not answer for MAL {malId} after retries, leaving it for the next run");
                    continue;
                }

                diagnostics.Write(
                    $"recap {match.Show.Slug}: MAL {malId} covers {lookup.Value.EpisodeCount} episodes, " +
                    $"{lookup.Value.RecapNumbers.Count} of them recaps");

                foreach (var episode in entry.Episodes)
                {
                    if (episode.Number <= lookup.Value.EpisodeCount)
                    {
                        episode.Recap = lookup.Value.RecapNumbers.Contains(episode.Number);
                    }
                }

                entry.RecapMalId = malId;
                updated++;
            }

            if (updated > 0)
            {
                _logger.Info("Anime markers sync: recap flags added for " + updated + " shows");
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Never at startup, since a first run walks the whole library at the site's crawl delay.
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
        }
    }
}
