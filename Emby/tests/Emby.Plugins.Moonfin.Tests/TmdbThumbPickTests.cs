using System.Text.Json;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

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

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("fr", "fr")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void TheMetadataLanguageIsATwoLetterCode(string? configured, string expected)
    {
        Assert.Equal(expected, TmdbThumbService.MetadataLanguage(configured));
    }
}
