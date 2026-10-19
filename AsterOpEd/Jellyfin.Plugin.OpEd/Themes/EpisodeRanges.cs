using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OpEd.Themes;

/// <summary>AnimeThemes episode lists such as "1-14, 16".</summary>
public static partial class EpisodeRanges
{
    /// <summary>Whether episode n is in the list; null when the list is unknown.</summary>
    public static bool? PlaysIn(string? episodes, int n)
    {
        if (string.IsNullOrWhiteSpace(episodes))
        {
            return null;
        }

        foreach (Match m in Range().Matches(episodes))
        {
            var from = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var to = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : from;
            if (n >= from && n <= to)
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"(\d+)\s*(?:-\s*(\d+))?")]
    private static partial Regex Range();
}
