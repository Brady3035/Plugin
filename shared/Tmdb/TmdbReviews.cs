using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Moonfin.Tmdb;

/// <summary>
/// One user review from a TMDB reviews page. Its text is turned into plain CommonMark here, so
/// each app draws it with the markdown reader it already has.
/// </summary>
public sealed class TmdbReview
{
    private const string AvatarBase = "https://image.tmdb.org/t/p/w185";

    private static readonly Regex LanguageCode = new Regex(@"^[a-z]{2}(-[A-Z]{2})?$", RegexOptions.Compiled);
    private static readonly Regex LineBreak = new Regex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Paragraph = new Regex(@"</?p(\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Bold = new Regex(@"<(strong|b)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Italic = new Regex(@"<(em|i)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Link = new Regex(@"<a\s[^>]*?href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnyTag = new Regex(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled);
    private static readonly Regex TrailingSpace = new Regex(@"[ \t]+\n", RegexOptions.Compiled);
    private static readonly Regex ExtraBlankLines = new Regex(@"\n{3,}", RegexOptions.Compiled);

    [JsonPropertyName("id")]
    public string Id { get; private set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; private set; } = string.Empty;

    /// <summary>Gets the author's score out of 10, when they gave one.</summary>
    [JsonPropertyName("rating")]
    public double? Rating { get; private set; }

    [JsonPropertyName("content")]
    public string Content { get; private set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; private set; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; private set; }

    [JsonPropertyName("url")]
    public string? Url { get; private set; }

    /// <summary>
    /// The language to ask TMDB for. Anything that isn't a plain language code, like an app's
    /// "system", is asked in English.
    /// </summary>
    public static string RequestLanguage(string? asked) =>
        asked?.Trim() is { } code && LanguageCode.IsMatch(code) ? code : "en-US";

    /// <summary>
    /// Whether to ask again in English, since TMDB has far fewer reviews in other languages.
    /// </summary>
    public static bool ShouldAskInEnglish(int totalResults, string language) =>
        totalResults == 0 && !language.StartsWith("en", StringComparison.Ordinal);

    public static int TotalResults(JsonElement page) =>
        page.ValueKind == JsonValueKind.Object
        && page.TryGetProperty("total_results", out var total)
        && total.ValueKind == JsonValueKind.Number
            ? total.GetInt32()
            : 0;

    /// <summary>
    /// Reads the reviews on a page, leaving out any without text.
    /// </summary>
    public static List<TmdbReview> ParseList(JsonElement page)
    {
        var reviews = new List<TmdbReview>();
        if (page.ValueKind != JsonValueKind.Object
            || !page.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return reviews;
        }

        foreach (var result in results.EnumerateArray())
        {
            var content = CleanContent(Text(result, "content"));
            if (content.Length == 0)
            {
                continue;
            }

            var details = result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("author_details", out var found)
                && found.ValueKind == JsonValueKind.Object
                    ? found
                    : default;

            reviews.Add(new TmdbReview
            {
                Id = Text(result, "id") ?? string.Empty,
                Author = Text(details, "name") ?? Text(result, "author") ?? Text(details, "username") ?? string.Empty,
                Rating = details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("rating", out var rating)
                    && rating.ValueKind == JsonValueKind.Number
                        ? rating.GetDouble()
                        : null,
                Content = content,
                CreatedAt = Text(result, "created_at"),
                AvatarUrl = Avatar(Text(details, "avatar_path")),
                Url = WebAddress(Text(result, "url")),
            });
        }

        return reviews;
    }

    public static string CleanContent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw!.Replace("\r\n", "\n").Replace('\r', '\n');
        text = LineBreak.Replace(text, "\n");
        text = Paragraph.Replace(text, "\n\n");
        text = Bold.Replace(text, "**$2**");
        text = Italic.Replace(text, "*$2*");
        text = Link.Replace(text, m => WebAddress(m.Groups[1].Value) is { } href
            ? $"[{m.Groups[2].Value}]({href})"
            : m.Groups[2].Value);
        text = AnyTag.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = TrailingSpace.Replace(text, "\n");
        text = ExtraBlankLines.Replace(text, "\n\n");
        return text.Trim();
    }

    // TMDB gives either its own image path or a whole Gravatar address behind a stray slash.
    private static string? Avatar(string? path)
    {
        if (path == null || !path.StartsWith("/", StringComparison.Ordinal))
        {
            return null;
        }

        var gravatar = path.Substring(1);
        return gravatar.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? WebAddress(gravatar)
            : AvatarBase + path;
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

    private static string? WebAddress(string? value) =>
        value != null
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;
}
