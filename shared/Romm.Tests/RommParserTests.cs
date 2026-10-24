using System.Text.Json;
using Moonfin.Romm;
using Xunit;

namespace Moonfin.Romm.Tests;

/// <summary>
/// The fixtures follow what RomM 5.4.1 sends, trimmed to the fields the reader uses. Game
/// folders downloaded from ROM sites often carry a readme next to the ROM, which RomM reports as
/// a second file, so most of these games are "multi-file" in RomM's terms.
/// </summary>
public class RommParserTests
{
    private const string Platforms = """
        [
          { "id": 10, "slug": "snes", "fs_slug": "snes", "display_name": "Super Nintendo Entertainment System",
            "firmware": [] },
          { "id": 8, "slug": "psx", "fs_slug": "psx", "display_name": "PlayStation",
            "firmware": [ { "id": 3, "file_name": "scph5501.bin", "file_size_bytes": 524288, "missing_from_fs": false },
                          { "id": 4, "file_name": "gone.bin", "file_size_bytes": 1, "missing_from_fs": true } ] },
          { "id": 11, "slug": "saturn", "fs_slug": "saturn", "display_name": "Sega Saturn", "firmware": [] },
          { "id": 12, "slug": "arcade", "fs_slug": "arcade", "display_name": "Arcade", "firmware": [] },
          { "id": 13, "slug": "nes", "fs_slug": "nes", "display_name": "NES", "missing_from_fs": true, "firmware": [] }
        ]
        """;

    [Fact]
    public void KeepsOnlyPlatformsWithACoreAndTheirPresentFirmware()
    {
        var platforms = ParsePlatforms();

        Assert.Equal(new[] { 10, 8, 12 }, platforms.Select(platform => platform.Id));
        var psx = platforms.Single(platform => platform.Id == 8);
        Assert.Equal("psx", psx.Core);
        Assert.Equal("PlayStation", psx.Name);
        var firmware = Assert.Single(psx.Firmware);
        Assert.Equal("scph5501.bin", firmware.FileName);
        Assert.Equal(524288, firmware.SizeBytes);
    }

    [Fact]
    public void GivesEachSystemAShortIdWithNoSlash()
    {
        using var document = JsonDocument.Parse("""
            [ { "id": 1, "slug": "genesis", "fs_slug": "genesis", "display_name": "Sega Mega Drive/Genesis" },
              { "id": 2, "slug": "snes", "fs_slug": "snes", "display_name": "Super Nintendo Entertainment System" },
              { "id": 3, "slug": "snes", "fs_slug": "snes-hacks", "display_name": "Super Nintendo Entertainment System" } ]
            """);

        var platforms = RommParser.ParsePlatforms(document.RootElement);

        Assert.Equal(new[] { "Genesis", "SNES", "SNES (snes-hacks)" }, platforms.Select(platform => platform.SystemId));
        Assert.Equal("Sega Mega Drive/Genesis", platforms[0].Name);
    }

    [Fact]
    public void PicksTheRomOverTheReadmeBesideIt()
    {
        var game = Assert.Single(ParseRoms("""
            [ { "id": 12, "platform_id": 10, "name": "Super Mario World", "fs_name_no_ext": "Super Mario World (USA)",
                "updated_at": "2026-10-23T17:47:20", "path_cover_large": "/assets/romm/resources/roms/10/12/cover/big.png?ts=2026-10-23 17:47:20",
                "merged_screenshots": [ "/assets/romm/resources/roms/10/12/screenshots/0.jpg" ],
                "files": [
                  { "id": 14, "file_name": "Vimm's Lair.txt", "file_size_bytes": 270, "category": "game" },
                  { "id": 15, "file_name": "Super Mario World (USA).sfc", "file_size_bytes": 524288, "category": "game" } ] } ]
            """));

        Assert.Equal("Super Mario World", game.Title);
        Assert.Equal(15, game.Rom.Id);
        Assert.Equal("Super Mario World (USA).sfc", game.ServedName);
        Assert.Equal(524288, game.ServedSizeBytes);
        Assert.False(game.UnpackArchive);
        Assert.Equal("/assets/romm/resources/roms/10/12/screenshots/0.jpg", game.ScreenshotPath);
    }

    [Fact]
    public void DropsGamesMoonfinCannotServe()
    {
        var games = ParseRoms("""
            [ { "id": 1, "platform_id": 10, "name": "No files", "files": [] },
              { "id": 2, "platform_id": 11, "name": "Saturn game", "files": [ { "id": 5, "file_name": "game.iso", "file_size_bytes": 9 } ] },
              { "id": 3, "platform_id": 10, "name": "Missing", "missing_from_fs": true, "files": [ { "id": 6, "file_name": "a.sfc", "file_size_bytes": 9 } ] },
              { "id": 4, "platform_id": 10, "name": "Manual only", "files": [ { "id": 7, "file_name": "a.sfc", "file_size_bytes": 9, "category": "manual" } ] },
              { "id": 5, "platform_id": 10, "name": "Unlisted zip", "files": [ { "id": 8, "file_name": "a.zip", "file_size_bytes": 9 } ] } ]
            """);

        Assert.Empty(games);
    }

    [Fact]
    public void NamesTheRomInsideAZipBecauseThePluginUnpacksIt()
    {
        var game = Assert.Single(ParseRoms("""
            [ { "id": 7, "platform_id": 10, "name": "Zipped",
                "files": [ { "id": 9, "file_name": "Zipped (USA).zip", "file_size_bytes": 300000,
                  "archive_members": [ { "name": "docs/readme.txt", "size": 900000 },
                                       { "name": "roms/Zipped (USA).sfc", "size": 1048576 } ] } ] } ]
            """));

        Assert.True(game.UnpackArchive);
        Assert.Equal("Zipped (USA).zip", game.Rom.FileName);
        Assert.Equal("Zipped (USA).sfc", game.ServedName);
        Assert.Equal(1048576, game.ServedSizeBytes);
    }

    [Fact]
    public void SendsAnArcadeSetIntactUnderItsOwnName()
    {
        var game = Assert.Single(ParseRoms("""
            [ { "id": 8, "platform_id": 12, "name": "Metal Slug",
                "files": [ { "id": 10, "file_name": "mslug.zip", "file_size_bytes": 4000000,
                  "archive_members": [ { "name": "201-p1.p1", "size": 2097152 } ] } ] } ]
            """));

        Assert.False(game.UnpackArchive);
        Assert.Equal("mslug.zip", game.ServedName);
        Assert.Equal(4000000, game.ServedSizeBytes);
    }

    [Fact]
    public void ConvertsRommMetadataToMoonfinScales()
    {
        var game = Assert.Single(ParseRoms("""
            [ { "id": 13, "platform_id": 10, "name": "Donkey Kong Country 2: Diddy's Kong Quest", "summary": "Diddy and Dixie set off.",
                "regions": [ "USA", "Europe" ],
                "metadatum": { "genres": [ "Platform", "Adventure" ], "developers": [], "publishers": [ "Nintendo" ],
                               "franchises": [ "Donkey Kong" ], "player_count": "1-2",
                               "first_release_date": 816912000000, "average_rating": 79.51 },
                "files": [ { "id": 17, "file_name": "DKC2 (USA).sfc", "file_size_bytes": 4194304 } ] } ]
            """));

        Assert.Equal("Diddy and Dixie set off.", game.Summary);
        Assert.Equal("Platform, Adventure", game.Genre);
        Assert.Null(game.Developer);
        Assert.Equal("Nintendo", game.Publisher);
        Assert.Equal("Donkey Kong", game.Franchise);
        Assert.Equal("USA, Europe", game.Region);
        Assert.Equal(2, game.Players);
        Assert.Equal(1995, game.Year);
        Assert.Equal(4.0, game.Rating);
    }

    [Fact]
    public void SnapshotSignatureChangesOnlyWhenWhatClientsSeeChanges()
    {
        var platforms = ParsePlatforms();
        RommGame Game(string updatedAt) => new RommGame { Id = 1, PlatformId = 10, UpdatedAt = updatedAt, Rom = new RommFile { Id = 2 } };

        var first = new RommSnapshot(platforms, new[] { Game("a") }, DateTime.UtcNow).ContentSignature();
        var same = new RommSnapshot(platforms, new[] { Game("a") }, DateTime.UtcNow.AddMinutes(5)).ContentSignature();
        var edited = new RommSnapshot(platforms, new[] { Game("b") }, DateTime.UtcNow).ContentSignature();

        Assert.Equal(first, same);
        Assert.NotEqual(first, edited);
    }

    private static List<RommPlatform> ParsePlatforms()
    {
        using var document = JsonDocument.Parse(Platforms);
        return RommParser.ParsePlatforms(document.RootElement);
    }

    private static List<RommGame> ParseRoms(string json)
    {
        var platforms = ParsePlatforms().ToDictionary(platform => platform.Id);
        using var document = JsonDocument.Parse(json);
        return RommParser.ParseRoms(document.RootElement, platforms, RommTestRules.Rules);
    }
}

/// <summary>A small stand-in for each plugin's ROM file rules.</summary>
internal static class RommTestRules
{
    private static readonly HashSet<string> RomExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nes", ".sfc", ".gba", ".md", ".z64", ".nds", ".iso",
    };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z" };

    public static readonly RommFileRules Rules = new(
        (core, name) => IsArcade(core)
            ? Path.GetExtension(name).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            : RomExtensions.Contains(Path.GetExtension(name)) || Archives.Contains(Path.GetExtension(name)),
        name => Archives.Contains(Path.GetExtension(name)),
        name => RomExtensions.Contains(Path.GetExtension(name)),
        (core, name) => IsArcade(core) && Path.GetExtension(name).Equals(".zip", StringComparison.OrdinalIgnoreCase));

    private static bool IsArcade(string core) => core is "arcade" or "mame";
}
