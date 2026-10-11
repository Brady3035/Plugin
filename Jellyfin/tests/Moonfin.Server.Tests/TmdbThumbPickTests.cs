using System.Text.Json;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class TmdbThumbPickTests
{
    [Fact]
    public void ABackdropWithTheTitleWins()
    {
        using var doc = JsonDocument.Parse("""
            {
              "backdrop_path": "/main.jpg",
              "images": {
                "backdrops": [
                  {"iso_639_1": null, "file_path": "/plain.jpg"},
                  {"iso_639_1": "en", "file_path": "/titled.jpg"}
                ]
              }
            }
            """);

        Assert.Equal("/titled.jpg", TmdbThumbService.PickPath(doc.RootElement, "en"));
    }

    [Fact]
    public void WithoutOneTheMainBackdropIsUsed()
    {
        using var doc = JsonDocument.Parse("""{"backdrop_path": "/main.jpg", "images": {"backdrops": [{"iso_639_1": null, "file_path": "/plain.jpg"}]}}""");

        Assert.Equal("/main.jpg", TmdbThumbService.PickPath(doc.RootElement, "en"));
    }

    [Fact]
    public void ATitleWithNoBackdropHasNoThumb()
    {
        using var doc = JsonDocument.Parse("""{"backdrop_path": null, "images": {"backdrops": []}}""");

        Assert.Null(TmdbThumbService.PickPath(doc.RootElement, "en"));
    }
}
