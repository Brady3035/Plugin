using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Plugins.Moonfin.Models
{
    public class GameLibrary
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonIgnore] public List<string> Locations { get; set; } = new List<string>();
    }

    public class GameSystem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("core")] public string Core { get; set; } = string.Empty;
        [JsonPropertyName("gameCount")] public int GameCount { get; set; }
    }

    public class GameSummary
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("system")] public string System { get; set; } = string.Empty;
        [JsonPropertyName("core")] public string Core { get; set; } = string.Empty;
        [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;
    }

    public class GameBios
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;
        [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    }

    public class GameDetail : GameSummary
    {
        [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
        [JsonPropertyName("bios")] public List<GameBios> Bios { get; set; } = new List<GameBios>();

        // Optional libretro-database metadata. Any field may be null (coverage is uneven).
        [JsonPropertyName("genre")] public string? Genre { get; set; }
        [JsonPropertyName("developer")] public string? Developer { get; set; }
        [JsonPropertyName("publisher")] public string? Publisher { get; set; }
        [JsonPropertyName("franchise")] public string? Franchise { get; set; }
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("year")] public int? Year { get; set; }
        [JsonPropertyName("players")] public int? Players { get; set; }
        [JsonPropertyName("overview")] public string? Overview { get; set; }
        [JsonPropertyName("rating")] public double? Rating { get; set; }
    }

    public class RommTestResult
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("platformCount")] public int PlatformCount { get; set; }
        [JsonPropertyName("supportedPlatformCount")] public int SupportedPlatformCount { get; set; }
        [JsonPropertyName("supportedRomCount")] public int SupportedRomCount { get; set; }

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Error { get; set; }
    }

    public class CoresStatus
    {
        [JsonPropertyName("installed")] public bool Installed { get; set; }
        [JsonPropertyName("downloading")] public bool Downloading { get; set; }
        [JsonPropertyName("state")] public string State { get; set; } = "idle";
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("filesInstalled")] public int FilesInstalled { get; set; }
    }
}
