using System.Text.Json;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The book library style travels in the synced profile, and each device type can pick its own.
/// </summary>
public sealed class BookLibraryStyleProfileTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public BookLibraryStyleProfileTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-book-library-style-tests-" + Guid.NewGuid().ToString("N"));
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
        var json = JsonSerializer.Serialize(new MoonfinSettingsProfile { BookLibraryStyle = "bookstore" });

        Assert.Contains("\"bookLibraryStyle\":\"bookstore\"", json);
        Assert.Equal("bookstore", JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!.BookLibraryStyle);
    }

    [Fact]
    public async Task APushWithoutTheFieldKeepsWhatWasStored()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { BookLibraryStyle = "readingRoom" },
            "new-client",
            notifySettingsChanged: false);

        // What an app from before book library styles sends.
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { DetailScreenStyle = "classic" },
            "old-client",
            notifySettingsChanged: false);

        Assert.Equal("readingRoom", Read(userId).GetProfile("tv")!.BookLibraryStyle);
    }

    [Fact]
    public async Task ADeviceStyleWinsOverTheGlobalOne()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "global",
            new MoonfinSettingsProfile { BookLibraryStyle = "readingRoom" },
            "client",
            notifySettingsChanged: false);
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { BookLibraryStyle = "bookstore" },
            "client",
            notifySettingsChanged: false);

        Assert.Equal("bookstore", (await _service.GetResolvedProfileAsync(userId, "tv"))!.BookLibraryStyle);
        Assert.Equal("readingRoom", (await _service.GetResolvedProfileAsync(userId, "mobile"))!.BookLibraryStyle);
    }
}
