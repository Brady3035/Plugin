using System.Text.Json;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The music library style travels in the synced profile, and each device type can pick its own.
/// </summary>
public sealed class MusicLibraryStyleProfileTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public MusicLibraryStyleProfileTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-music-library-style-tests-" + Guid.NewGuid().ToString("N"));
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private MoonfinUserSettings Read(Guid userId) =>
        JsonSerializer.Deserialize<MoonfinUserSettings>(
            File.ReadAllText(Path.Combine(_dataPath, userId + ".json")),
            CamelCase)!;

    [Fact]
    public void TheFieldUsesTheClientsWireName()
    {
        var json = JsonSerializer.Serialize(new MoonfinSettingsProfile { MusicLibraryStyle = "immersive" });

        Assert.Contains("\"musicLibraryStyle\":\"immersive\"", json);
        Assert.Equal("immersive", JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!.MusicLibraryStyle);
    }

    [Fact]
    public async Task APushWithoutTheFieldKeepsWhatWasStored()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { MusicLibraryStyle = "quickAccess" },
            "new-client",
            notifySettingsChanged: false);

        // What an app from before music library styles sends.
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { DetailScreenStyle = "classic" },
            "old-client",
            notifySettingsChanged: false);

        Assert.Equal("quickAccess", Read(userId).GetProfile("tv")!.MusicLibraryStyle);
    }

    [Fact]
    public async Task ADeviceStyleWinsOverTheGlobalOne()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "global",
            new MoonfinSettingsProfile { MusicLibraryStyle = "quickAccess" },
            "client",
            notifySettingsChanged: false);
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { MusicLibraryStyle = "immersive" },
            "client",
            notifySettingsChanged: false);

        Assert.Equal("immersive", (await _service.GetResolvedProfileAsync(userId, "tv"))!.MusicLibraryStyle);
        Assert.Equal("quickAccess", (await _service.GetResolvedProfileAsync(userId, "mobile"))!.MusicLibraryStyle);
    }
}
