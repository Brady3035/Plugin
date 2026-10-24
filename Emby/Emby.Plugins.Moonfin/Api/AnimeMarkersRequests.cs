using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/AnimeMarkers/Series", "GET")]
    [Authenticated]
    public class GetAnimeSeriesMarkersRequest : IReturn<object>
    {
        public string? SeriesId { get; set; }
    }

    [Route("/Moonfin/AnimeMarkers/Items", "GET")]
    [Authenticated]
    public class GetAnimeItemMarkersRequest : IReturn<object>
    {
        /// <summary>Comma-separated item ids.</summary>
        public string? Ids { get; set; }
    }

    [Route("/Moonfin/AnimeMarkers/Diagnostics", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetAnimeMarkerDiagnosticsRequest : IReturn<object>
    {
    }

    [Route("/Moonfin/AnimeMarkers/Preview", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetAnimeMarkerPreviewRequest : IReturn<object>
    {
        public string? SeriesId { get; set; }
    }

    [Route("/Moonfin/AnimeMarkers/Refresh", "POST")]
    [Authenticated(Roles = "Admin")]
    public class RefreshAnimeMarkersRequest : IReturn<object>
    {
        public string? SeriesId { get; set; }
    }

    [Route("/Moonfin/AnimeMarkers/Log", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetAnimeMarkerLogRequest : IReturn<object>
    {
        public int? Lines { get; set; }
    }

    [Route("/Moonfin/AnimeMarkers/ClearLog", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ClearAnimeMarkerLogRequest : IReturn<object>
    {
    }

    [Route("/Moonfin/AnimeMarkers/ClearCache", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ClearAnimeMarkerCacheRequest : IReturn<object>
    {
    }
}
