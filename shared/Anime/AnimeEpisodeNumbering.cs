using System.Collections.Generic;
using System.Linq;

namespace Moonfin.Anime;

/// <summary>
/// Flattens a series' season and episode numbers into the absolute numbering AnimeFillerList uses.
/// </summary>
public static class AnimeEpisodeNumbering
{
    /// <summary>
    /// True when the library already numbers this series absolutely (S04E207), which is the
    /// case exactly when no index number repeats across the whole series.
    /// </summary>
    public static bool IsAlreadyAbsolute(IReadOnlyList<(int Season, int Index)> episodes) =>
        episodes.Count > 0 &&
        episodes.Select(episode => episode.Index).Distinct().Count() == episodes.Count;

    /// <summary>
    /// The absolute episode number for each of a series' episodes, given their season and
    /// index numbers already sorted by season then index.
    /// </summary>
    public static int[] AssignAbsoluteNumbers(IReadOnlyList<(int Season, int Index)> episodes)
    {
        var numbers = new int[episodes.Count];
        if (episodes.Count == 0)
        {
            return numbers;
        }

        if (IsAlreadyAbsolute(episodes))
        {
            for (var i = 0; i < episodes.Count; i++)
            {
                numbers[i] = episodes[i].Index;
            }

            return numbers;
        }

        // Each season restarts at 1, so the earlier seasons are added back on. A season's
        // length is its highest index rather than its episode count, so a library missing an
        // episode mid-season doesnt shift every later episode onto the wrong marker.
        var seasonLengths = new Dictionary<int, int>();
        foreach (var (season, index) in episodes)
        {
            seasonLengths[season] = seasonLengths.TryGetValue(season, out var length) && length > index
                ? length
                : index;
        }

        var offsets = new Dictionary<int, int>();
        var running = 0;
        foreach (var season in seasonLengths.Keys.OrderBy(season => season))
        {
            offsets[season] = running;
            running += seasonLengths[season];
        }

        for (var i = 0; i < episodes.Count; i++)
        {
            numbers[i] = offsets[episodes[i].Season] + episodes[i].Index;
        }

        return numbers;
    }
}
