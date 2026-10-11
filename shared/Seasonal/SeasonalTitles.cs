using System;
using System.Collections.Generic;
using System.Linq;

namespace Moonfin.Seasonal;

/// <summary>
/// Tells when two entries of the seasonal row are the same movie, so the row shows each title
/// once. A title is known by keys like "tmdb:400155" and "imdb:tt5220122", and by its name and
/// year for an owned copy that has no TMDB id to go by.
/// </summary>
public static class SeasonalTitles
{
    public static string? TmdbKey(string? tmdbId)
    {
        return string.IsNullOrWhiteSpace(tmdbId) ? null : "tmdb:" + tmdbId!.Trim();
    }

    public static string? ImdbKey(string? imdbId)
    {
        return string.IsNullOrWhiteSpace(imdbId) ? null : "imdb:" + imdbId!.Trim().ToLowerInvariant();
    }

    /// <summary>The TMDB and IMDb keys of a title, leaving out the ids it doesn't have.</summary>
    public static List<string> IdKeys(string? tmdbId, string? imdbId)
    {
        var keys = new List<string>(2);
        var tmdb = TmdbKey(tmdbId);
        if (tmdb != null)
        {
            keys.Add(tmdb);
        }

        var imdb = ImdbKey(imdbId);
        if (imdb != null)
        {
            keys.Add(imdb);
        }

        return keys;
    }

    /// <summary>
    /// The name and year as one key, ignoring case, spaces and punctuation. Null without both,
    /// since a name alone would match remakes.
    /// </summary>
    public static string? NameKey(string? name, int? year)
    {
        if (string.IsNullOrWhiteSpace(name) || !year.HasValue)
        {
            return null;
        }

        var letters = new string(name!.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return letters.Length == 0 ? null : "name:" + letters + ":" + year.Value;
    }

    /// <summary>
    /// Keeps the first entry of each title, in order. Entries are the same title when they share
    /// any key, and an entry without keys is always kept.
    /// </summary>
    public static List<T> Distinct<T>(IEnumerable<T> items, Func<T, IEnumerable<string>> keys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<T>();
        foreach (var item in items)
        {
            var itemKeys = keys(item).ToList();
            if (!itemKeys.Any(seen.Contains))
            {
                kept.Add(item);
            }

            // A dropped copy's other ids still belong to the title it repeats.
            seen.UnionWith(itemKeys);
        }

        return kept;
    }
}
