using System.Text.Json;
using Moonfin.Tmdb;
using Xunit;

namespace Moonfin.Tmdb.Tests;

/// <summary>
/// The apps draw a review with their own markdown readers, none of which read HTML, so the
/// parser hands back plain CommonMark along with who wrote it and their score.
/// </summary>
public sealed class TmdbReviewsTests
{
    private static List<TmdbReview> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TmdbReview.ParseList(doc.RootElement);
    }

    [Fact]
    public void ReadsTheAuthorScoreAndAvatar()
    {
        var reviews = Parse("""
            {
              "results": [
                {
                  "author": "Goddard",
                  "author_details": {"name": "", "username": "Goddard", "avatar_path": "/https://secure.gravatar.com/avatar/abc.jpg", "rating": null},
                  "content": "Pretty awesome movie.",
                  "created_at": "2018-06-09T17:51:53.359Z",
                  "id": "5b1c13b9c3a36848f2026384",
                  "url": "https://www.themoviedb.org/review/5b1c13b9c3a36848f2026384"
                },
                {
                  "author": "Brett Pascoe",
                  "author_details": {"name": "Brett", "username": "brettp", "avatar_path": "/pic.jpg", "rating": 9.0},
                  "content": "In my top 5.",
                  "id": "5d1",
                  "url": "javascript:alert(1)"
                }
              ]
            }
            """);

        Assert.Equal(2, reviews.Count);
        Assert.Equal("Goddard", reviews[0].Author);
        Assert.Null(reviews[0].Rating);
        Assert.Equal("https://secure.gravatar.com/avatar/abc.jpg", reviews[0].AvatarUrl);
        Assert.Equal("2018-06-09T17:51:53.359Z", reviews[0].CreatedAt);
        Assert.Equal("Brett", reviews[1].Author);
        Assert.Equal(9.0, reviews[1].Rating);
        Assert.Equal("https://image.tmdb.org/t/p/w185/pic.jpg", reviews[1].AvatarUrl);
        Assert.Null(reviews[1].Url);
    }

    [Fact]
    public void LeavesOutReviewsWithNoText()
    {
        var reviews = Parse("""{"results": [{"author": "a", "content": "  "}, {"author": "b"}]}""");

        Assert.Empty(reviews);
    }

    [Fact]
    public void TurnsHtmlIntoMarkdown()
    {
        var text = TmdbReview.CleanContent(
            "<p>A <em>great</em> film,<br/>truly <b>bold</b>.</p>\r\n\r\n\r\n<a href=\"https://example.com/x\">More</a> &amp; <span>so on</span>");

        Assert.Equal("A *great* film,\ntruly **bold**.\n\n[More](https://example.com/x) & so on", text);
    }

    [Fact]
    public void KeepsTextThatOnlyLooksLikeATag()
    {
        Assert.Equal("3 < 5 and 7 > 2", TmdbReview.CleanContent("3 < 5 and 7 > 2"));
    }

    [Fact]
    public void DropsLinksThatArentWebAddresses()
    {
        Assert.Equal("click", TmdbReview.CleanContent("<a href=\"javascript:alert(1)\">click</a>"));
    }

    [Fact]
    public void AsksInEnglishForAnythingThatIsntALanguageCode()
    {
        Assert.Equal("de-DE", TmdbReview.RequestLanguage(" de-DE "));
        Assert.Equal("fr", TmdbReview.RequestLanguage("fr"));
        Assert.Equal("en-US", TmdbReview.RequestLanguage("system"));
        Assert.Equal("en-US", TmdbReview.RequestLanguage(null));
    }

    [Fact]
    public void AsksAgainInEnglishOnlyWhenAnotherLanguageHasNothing()
    {
        Assert.True(TmdbReview.ShouldAskInEnglish(0, "de-DE"));
        Assert.False(TmdbReview.ShouldAskInEnglish(1, "de-DE"));
        Assert.False(TmdbReview.ShouldAskInEnglish(0, "en-US"));
    }

    [Fact]
    public void WritesTheFieldsTheAppsRead()
    {
        using var doc = JsonDocument.Parse("""{"results": [{"id": "a", "author": "Goddard", "content": "Fine."}]}""");
        var json = JsonSerializer.Serialize(TmdbReview.ParseList(doc.RootElement));

        Assert.Contains("\"author\":\"Goddard\"", json);
        Assert.Contains("\"createdAt\":null", json);
        Assert.Contains("\"avatarUrl\":null", json);
    }

    [Fact]
    public void CountsEveryReviewTmdbHas()
    {
        using var doc = JsonDocument.Parse("""{"total_results": 34, "results": []}""");

        Assert.Equal(34, TmdbReview.TotalResults(doc.RootElement));
    }
}
