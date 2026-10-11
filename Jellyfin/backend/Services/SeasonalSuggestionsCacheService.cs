using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// The popular TMDB movies for each holiday, keyed "{holiday}:{year}" and shared by every
/// user. The per-user work (owned titles out, parental limit) happens on a copy.
/// </summary>
public class SeasonalSuggestionsCacheService : FileBackedCacheService<SeasonalSuggestionsEntry>
{
    // Raised when a cached suggestion changes shape, so an entry built the old way is rebuilt
    // once. 2 keeps the plain backdrop and adds the IMDb id.
    private const int CurrentSchema = 2;

    public SeasonalSuggestionsCacheService(ILogger<SeasonalSuggestionsCacheService> logger)
        : base(logger, "seasonal_suggestions_cache.json", "Seasonal suggestions")
    {
    }

    /// <summary>The entry, unless it is older than <paramref name="maxAge"/>, was built from fewer discover pages than asked for, or predates the current schema.</summary>
    public List<CustomRowItem>? TryGet(string key, TimeSpan maxAge, int pages)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(key, out var entry) && entry.Schema >= CurrentSchema && entry.Pages >= pages && DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry.Items;
        }

        return null;
    }

    public void Set(string key, List<CustomRowItem> items, int pages)
    {
        EnsureLoaded()[key] = new SeasonalSuggestionsEntry { Items = items, CachedAt = DateTimeOffset.UtcNow, Pages = pages, Schema = CurrentSchema };
    }
}

public class SeasonalSuggestionsEntry
{
    [JsonPropertyName("items")]
    public List<CustomRowItem> Items { get; set; } = new();

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }

    [JsonPropertyName("pages")]
    public int Pages { get; set; }

    [JsonPropertyName("schema")]
    public int Schema { get; set; }
}
