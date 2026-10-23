using System.Text.Json;
using Moonfin.Tmdb;
using Xunit;

namespace Moonfin.Tmdb.Tests;

/// <summary>
/// The person page shows a link for each account TMDB lists, so the parser keeps the ones that
/// are there and drops anything blank or anything that isn't a plain handle or web address.
/// </summary>
public sealed class TmdbPersonLinksTests
{
    private static TmdbPersonLinks Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TmdbPersonLinks.Parse(doc.RootElement);
    }

    [Fact]
    public void ReadsEveryAccountTmdbLists()
    {
        var links = Parse("""
            {
              "id": 70851,
              "imdb_id": "nm0085312",
              "homepage": null,
              "external_ids": {
                "imdb_id": "nm0085312",
                "facebook_id": "JackBlack",
                "instagram_id": "jackblack",
                "tiktok_id": "jackblack",
                "twitter_id": "jackblack",
                "youtube_id": "@JablinskiGames",
                "wikidata_id": "Q311615"
              }
            }
            """);

        Assert.Equal("nm0085312", links.ImdbId);
        Assert.Null(links.Homepage);
        Assert.Equal("jackblack", links.Instagram);
        Assert.Equal("jackblack", links.Twitter);
        Assert.Equal("JackBlack", links.Facebook);
        Assert.Equal("jackblack", links.TikTok);
        Assert.Equal("@JablinskiGames", links.YouTube);
    }

    [Fact]
    public void BlankAndMissingAccountsStayNull()
    {
        var links = Parse("""
            {
              "imdb_id": "",
              "homepage": "",
              "external_ids": { "instagram_id": "", "twitter_id": null, "facebook_id": "   " }
            }
            """);

        Assert.Null(links.ImdbId);
        Assert.Null(links.Homepage);
        Assert.Null(links.Instagram);
        Assert.Null(links.Twitter);
        Assert.Null(links.Facebook);
        Assert.Null(links.TikTok);
        Assert.Null(links.YouTube);
    }

    [Fact]
    public void TakesTheImdbIdFromExternalIdsWhenThePersonHasNone()
    {
        var links = Parse("""{ "external_ids": { "imdb_id": "nm0000120" } }""");

        Assert.Equal("nm0000120", links.ImdbId);
    }

    [Fact]
    public void KeepsOnlyWebHomepages()
    {
        Assert.Equal("https://example.com/", Parse("""{ "homepage": " https://example.com " }""").Homepage);
        Assert.Equal("http://example.com/about", Parse("""{ "homepage": "http://example.com/about" }""").Homepage);
        Assert.Null(Parse("""{ "homepage": "javascript:alert(1)" }""").Homepage);
        Assert.Null(Parse("""{ "homepage": "example.com" }""").Homepage);
    }

    [Fact]
    public void DropsAnythingThatIsNotAPlainHandle()
    {
        var links = Parse("""
            {
              "imdb_id": "tt0085312",
              "external_ids": {
                "instagram_id": "jack/../black",
                "twitter_id": "jack black",
                "facebook_id": "https://facebook.com/JackBlack",
                "tiktok_id": "jack?black"
              }
            }
            """);

        Assert.Null(links.ImdbId);
        Assert.Null(links.Instagram);
        Assert.Null(links.Twitter);
        Assert.Null(links.Facebook);
        Assert.Null(links.TikTok);
    }

    [Fact]
    public void AnAnswerThatIsNoObjectGivesNoLinks()
    {
        var links = Parse("[]");

        Assert.Null(links.ImdbId);
        Assert.Null(links.Instagram);
    }
}
