using System;
using System.Text.Json;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public sealed class SeasonalSuggestionDetailsTests
{
    private const string Details = """
        {
          "overview": "Three witches return to Salem.",
          "vote_average": 7.08,
          "runtime": 96,
          "genres": [{"id": 35, "name": "Comedy"}, {"id": 14, "name": "Fantasy"}],
          "imdb_id": "tt0107120",
          "images": {
            "backdrops": [
              {"iso_639_1": null, "file_path": "/plain.jpg"},
              {"iso_639_1": "en", "file_path": "/titled.jpg"}
            ]
          }
        }
        """;

    [Fact]
    public void DetailsFillWhatTheFocusedCardShows()
    {
        var item = new CustomRowItem { Id = 10439, BackdropUrl = "/discover.jpg" };
        using var doc = JsonDocument.Parse(Details);

        SeasonalRowService.ApplyDetails(item, doc.RootElement);

        Assert.Equal("Three witches return to Salem.", item.Overview);
        Assert.Equal(7.1, item.Rating);
        Assert.Equal(96 * TimeSpan.TicksPerMinute, item.RunTimeTicks);
        Assert.Equal(new[] { "Comedy", "Fantasy" }, item.Genres);
        Assert.Equal("tt0107120", item.ProviderIds.Imdb);
    }

    [Fact]
    public void TheBackdropStaysTheOneDiscoverGaveEvenWithATitledOne()
    {
        var item = new CustomRowItem { Id = 10439, BackdropUrl = "/discover.jpg" };
        using var doc = JsonDocument.Parse(Details);

        SeasonalRowService.ApplyDetails(item, doc.RootElement);

        Assert.Equal("/discover.jpg", item.BackdropUrl);
    }

    [Fact]
    public void MissingDetailsLeaveTheItemAsDiscoverGaveIt()
    {
        var item = new CustomRowItem { Id = 10439, BackdropUrl = "/discover.jpg" };
        using var doc = JsonDocument.Parse("""{"runtime": 0, "imdb_id": null}""");

        SeasonalRowService.ApplyDetails(item, doc.RootElement);

        Assert.Equal("/discover.jpg", item.BackdropUrl);
        Assert.Null(item.RunTimeTicks);
        Assert.Null(item.Genres);
        Assert.Null(item.ProviderIds.Imdb);
    }
}
