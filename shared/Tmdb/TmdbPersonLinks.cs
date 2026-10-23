using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Moonfin.Tmdb;

/// <summary>
/// A person's IMDb id, official site and social accounts, read from TMDB's person answer with
/// external_ids appended. Anything TMDB leaves blank stays null, so the app only shows the
/// links that exist.
/// </summary>
public sealed class TmdbPersonLinks
{
    // The app puts a handle into a profile address, so one that could step out of that path
    // is dropped rather than passed along.
    private static readonly Regex Handle = new Regex(@"^@?[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex ImdbPerson = new Regex(@"^nm\d+$", RegexOptions.Compiled);

    public string? ImdbId { get; private set; }
    public string? Homepage { get; private set; }
    public string? Instagram { get; private set; }
    public string? Twitter { get; private set; }
    public string? Facebook { get; private set; }
    public string? TikTok { get; private set; }
    public string? YouTube { get; private set; }

    public static TmdbPersonLinks Parse(JsonElement person)
    {
        var ids = person.ValueKind == JsonValueKind.Object
            && person.TryGetProperty("external_ids", out var found)
            && found.ValueKind == JsonValueKind.Object
                ? found
                : default;

        return new TmdbPersonLinks
        {
            ImdbId = Matching(Text(person, "imdb_id") ?? Text(ids, "imdb_id"), ImdbPerson),
            Homepage = WebAddress(Text(person, "homepage")),
            Instagram = Matching(Text(ids, "instagram_id"), Handle),
            Twitter = Matching(Text(ids, "twitter_id"), Handle),
            Facebook = Matching(Text(ids, "facebook_id"), Handle),
            TikTok = Matching(Text(ids, "tiktok_id"), Handle),
            YouTube = Matching(Text(ids, "youtube_id"), Handle),
        };
    }

    private static string? Text(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? Matching(string? value, Regex pattern) =>
        value != null && pattern.IsMatch(value) ? value : null;

    private static string? WebAddress(string? value) =>
        value != null
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;
}
