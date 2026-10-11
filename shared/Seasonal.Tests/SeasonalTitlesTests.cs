using System.Linq;
using Xunit;

namespace Moonfin.Seasonal.Tests;

public sealed class SeasonalTitlesTests
{
    private sealed class Entry
    {
        public Entry(string label, string? tmdb = null, string? imdb = null)
        {
            Label = label;
            Tmdb = tmdb;
            Imdb = imdb;
        }

        public string Label { get; }

        public string? Tmdb { get; }

        public string? Imdb { get; }
    }

    private static string[] Labels(params Entry[] entries) =>
        SeasonalTitles.Distinct(entries, e => SeasonalTitles.IdKeys(e.Tmdb, e.Imdb)).Select(e => e.Label).ToArray();

    [Fact]
    public void TwoCopiesWithTheSameTmdbIdShowOnce()
    {
        Assert.Equal(
            new[] { "1080p", "Elf" },
            Labels(new Entry("1080p", "400155"), new Entry("Elf", "10719"), new Entry("4K", "400155")));
    }

    [Fact]
    public void CopiesMatchByImdbIdWhenOneHasNoTmdbId()
    {
        Assert.Equal(
            new[] { "first" },
            Labels(new Entry("first", "400155", "tt5220122"), new Entry("second", null, "TT5220122")));
    }

    [Fact]
    public void ADroppedCopyCarriesItsOtherIdsToTheTitleItRepeats()
    {
        Assert.Equal(
            new[] { "a" },
            Labels(new Entry("a", "400155"), new Entry("b", "400155", "tt5220122"), new Entry("c", null, "tt5220122")));
    }

    [Fact]
    public void EntriesWithoutIdsAreAllKept()
    {
        Assert.Equal(new[] { "a", "b" }, Labels(new Entry("a"), new Entry("b")));
    }

    [Fact]
    public void NameKeysIgnoreCaseAndPunctuationButNeedTheYear()
    {
        Assert.Equal(
            SeasonalTitles.NameKey("Hotel Transylvania 3: Summer Vacation", 2018),
            SeasonalTitles.NameKey("hotel transylvania 3 - summer vacation", 2018));
        Assert.NotEqual(SeasonalTitles.NameKey("Halloween", 1978), SeasonalTitles.NameKey("Halloween", 2018));
        Assert.Null(SeasonalTitles.NameKey("Halloween", null));
        Assert.Null(SeasonalTitles.NameKey("  ", 2018));
    }
}
