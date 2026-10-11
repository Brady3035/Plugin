using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Moonfin.Seasonal;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The seasonal row shows each title once: owned copies of one movie collapse, suggestions
/// repeat no title, and a suggestion the user owns is left out.
/// </summary>
public sealed class SeasonalRowDedupeTests
{
    private static Movie Owned(string name, int? year, string? tmdb = null, string? imdb = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name, ProductionYear = year };
        if (tmdb != null)
        {
            movie.SetProviderId(MetadataProvider.Tmdb, tmdb);
        }

        if (imdb != null)
        {
            movie.SetProviderId(MetadataProvider.Imdb, imdb);
        }

        return movie;
    }

    private static CustomRowItem Suggestion(long tmdbId, string name, int year, string? imdb = null) => new()
    {
        Id = tmdbId,
        Name = name,
        Type = "Movie",
        ProductionYear = year,
        ProviderIds = new CustomRowItemProviderIds { Tmdb = tmdbId.ToString(), Imdb = imdb }
    };

    [Fact]
    public void TwoLibraryCopiesOfOneMovieShowOnce()
    {
        var hd = Owned("Hotel Transylvania 3: Summer Vacation", 2018, tmdb: "400155");
        var uhd = Owned("Hotel Transylvania 3: Summer Vacation", 2018, tmdb: "400155", imdb: "tt5220122");
        var other = Owned("Hocus Pocus", 1993, tmdb: "10439");
        var imdbOnly = Owned("Hotel Transylvania 3", 2018, imdb: "tt5220122");

        var distinct = SeasonalRowService.DistinctOwned([hd, other, uhd, imdbOnly]);

        Assert.Equal(new BaseItem[] { hd, other }, distinct);
    }

    [Fact]
    public void ASuggestionTheUserOwnsIsLeftOut()
    {
        var owned = new[]
        {
            Owned("Hocus Pocus", 1993, tmdb: "10439"),
            Owned("Coco", 2017, imdb: "tt2380307"),
            Owned("Hotel Transylvania 3: Summer Vacation", 2018)
        };
        var keys = SeasonalRowService.OwnedKeys(owned, [9532]);
        var all = new List<CustomRowItem>
        {
            Suggestion(10439, "Hocus Pocus", 1993),
            Suggestion(354912, "Coco", 2017, imdb: "tt2380307"),
            Suggestion(400155, "Hotel Transylvania 3: Summer Vacation", 2018),
            Suggestion(9532, "Final Destination", 2000),
            Suggestion(10719, "Elf", 2003)
        };

        var picked = SeasonalRowService.PickSuggestions(all, keys, RatingLimit.None, 40);

        Assert.Equal(new long?[] { 10719 }, picked.Select(s => s.Id));
    }

    [Fact]
    public void AnOwnedCopyWithATmdbIdDoesNotHideAnotherMovieOfTheSameName()
    {
        var keys = SeasonalRowService.OwnedKeys([Owned("Halloween", 2018, tmdb: "424139")], []);

        var picked = SeasonalRowService.PickSuggestions([Suggestion(1, "Halloween", 2018)], keys, RatingLimit.None, 40);

        Assert.Single(picked);
    }

    [Fact]
    public void SuggestionsRepeatNoTitleAndStopAtTheCap()
    {
        var all = new List<CustomRowItem>
        {
            Suggestion(400155, "Hotel Transylvania 3: Summer Vacation", 2018),
            Suggestion(10439, "Hocus Pocus", 1993),
            Suggestion(400155, "Hotel Transylvania 3: Summer Vacation", 2018),
            Suggestion(10719, "Elf", 2003)
        };

        var picked = SeasonalRowService.PickSuggestions(all, new HashSet<string>(), RatingLimit.None, 40);
        var capped = SeasonalRowService.PickSuggestions(all, new HashSet<string>(), RatingLimit.None, 2);

        Assert.Equal(new long?[] { 400155, 10439, 10719 }, picked.Select(s => s.Id));
        Assert.Equal(new long?[] { 400155, 10439 }, capped.Select(s => s.Id));
    }
}
