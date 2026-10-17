using System.Text.Json;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The library page style travels in the synced profile, and each device type can pick its own.
/// </summary>
public sealed class LibraryStyleProfileTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public LibraryStyleProfileTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-library-style-tests-" + Guid.NewGuid().ToString("N"));
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
        var json = JsonSerializer.Serialize(new MoonfinSettingsProfile { LibraryStyle = "docked" });

        Assert.Contains("\"libraryStyle\":\"docked\"", json);
        Assert.Equal("docked", JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!.LibraryStyle);
    }

    [Fact]
    public async Task APushWithoutTheFieldKeepsWhatWasStored()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { LibraryStyle = "modern" },
            "new-client",
            notifySettingsChanged: false);

        // What an app from before library styles sends.
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { DetailScreenStyle = "classic" },
            "old-client",
            notifySettingsChanged: false);

        Assert.Equal("modern", Read(userId).GetProfile("tv")!.LibraryStyle);
    }

    [Fact]
    public async Task ADeviceStyleWinsOverTheGlobalOne()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "global",
            new MoonfinSettingsProfile { LibraryStyle = "modern" },
            "client",
            notifySettingsChanged: false);
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { LibraryStyle = "docked" },
            "client",
            notifySettingsChanged: false);

        Assert.Equal("docked", (await _service.GetResolvedProfileAsync(userId, "tv"))!.LibraryStyle);
        Assert.Equal("modern", (await _service.GetResolvedProfileAsync(userId, "mobile"))!.LibraryStyle);
    }
}
