using Moonfin.Romm;
using Xunit;

namespace Moonfin.Romm.Tests;

public class RommSourceTests
{
    private const string LibraryId = "8f3e04c1bc361191c16f63a5c8f9c190";

    [Fact]
    public void OwnsItsLibraryWhicheverWayTheIdIsWritten()
    {
        var source = Source();

        Assert.True(source.Owns(LibraryId));
        Assert.True(source.Owns("8F3E04C1-BC36-1191-C16F-63A5C8F9C190"));
        Assert.False(source.Owns("9bdd941d4d8f6619d69450f8e6a90bbf"));
        Assert.False(source.Owns(null));
    }

    [Theory]
    [InlineData("http://romm.test:8080", "rmm_abc", "", false)]
    [InlineData("http://romm.test:8080", "", LibraryId, false)]
    [InlineData("http://romm.test:8080", "rmm abc", LibraryId, false)]
    [InlineData("not a url", "rmm_abc", LibraryId, false)]
    [InlineData("http://romm.test:8080", " rmm_abc ", LibraryId, true)]
    public void ASettingThatIsMissingOrUnusableLeavesRommOff(string url, string token, string libraryId, bool on) =>
        Assert.Equal(on, RommSettings.From(url, token, libraryId) != null);

    [Fact]
    public void FindsGamesAndFirmwareByTheirOwnPrefixes()
    {
        var source = Source();

        Assert.Equal(12, source.FindGame("romm-12")?.Id);
        Assert.Equal(3, source.FindFirmware("romm-fw-3")?.Id);
        Assert.Null(source.FindGame("romm-fw-3"));
        Assert.Null(source.FindFirmware("romm-12"));
        Assert.Null(source.FindGame("romm-+12"));
        Assert.Null(source.FindGame("romm-99"));
    }

    [Fact]
    public void ANewTokenStartsAFreshInventory()
    {
        var token = "rmm_one";
        var loadedWith = new List<string>();
        var source = new RommSource(
            () => RommSettings.From("http://romm.test:8080", token, LibraryId),
            () => throw new InvalidOperationException("The test loads through the override."),
            RommTestRules.Rules,
            (settings, _) =>
            {
                loadedWith.Add(settings.Token);
                return Task.FromResult(Snapshot());
            });

        source.Snapshot();
        token = "rmm_two";
        source.Snapshot();

        Assert.Equal(new[] { "rmm_one", "rmm_two" }, loadedWith);
    }

    private static RommSource Source() => new(
        () => RommSettings.From("http://romm.test:8080", "rmm_abc", LibraryId),
        () => throw new InvalidOperationException("The test loads through the override."),
        RommTestRules.Rules,
        (_, _) => Task.FromResult(Snapshot()));

    private static RommSnapshot Snapshot()
    {
        var platform = new RommPlatform { Id = 8, SystemId = "PlayStation", Name = "PlayStation", Core = "psx" };
        platform.Firmware.Add(new RommFile { Id = 3, FileName = "scph5501.bin", SizeBytes = 524288 });
        var game = new RommGame { Id = 12, PlatformId = 8, Title = "Game", Rom = new RommFile { Id = 15, FileName = "game.chd" } };
        return new RommSnapshot(new[] { platform }, new[] { game }, DateTime.UtcNow);
    }
}
