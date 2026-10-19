using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>
/// Builds an empty but valid menu for a library title: its ids, one Main Menu with a Play button, a calm default look. It is the "start from
/// nothing" option beside the draft builder (which also pulls in the title's extras and chapters, and only for movies). Pure and free of
/// Jellyfin types so it can be tested exhaustively.
/// </summary>
public static class MenuBlankBuilder
{
    private static readonly Regex Digits = new("^[0-9]{1,12}$", RegexOptions.Compiled);
    private static readonly Regex Imdb = new("^tt[0-9]{7,9}$", RegexOptions.Compiled);

    /// <param name="itemType">"Movie", "Series" or "Season".</param>
    /// <exception cref="ArgumentException">The type is unknown, or the title has no usable TMDB, IMDB or TVDB id.</exception>
    public static JsonObject Build(string itemType, IReadOnlyDictionary<string, string> providerIds, int? year, int? seasonNumber = null, Guid? menuId = null, DateTimeOffset? created = null)
    {
        if (itemType is not ("Movie" or "Series" or "Season"))
        {
            throw new ArgumentException("A menu can be made for a movie, a series or a season.");
        }

        var ids = new JsonObject();
        foreach (var (provider, value) in providerIds)
        {
            var key = provider.ToLowerInvariant();
            if (key == "tmdb" && Digits.IsMatch(value))
            {
                ids["Tmdb"] = value;
            }
            else if (key == "imdb" && Imdb.IsMatch(value))
            {
                ids["Imdb"] = value;
            }
            else if (key == "tvdb" && Digits.IsMatch(value))
            {
                ids["Tvdb"] = value;
            }
        }

        if (ids.Count == 0)
        {
            throw new ArgumentException("This title has no TMDB, IMDB or TVDB id, so a menu can't be matched to it.");
        }

        var match = new JsonObject { ["itemType"] = itemType, ["providerIds"] = ids };
        if (itemType == "Season")
        {
            match["seasonNumber"] = seasonNumber is >= 0 ? seasonNumber : 1;
        }

        if (year is >= 1900 and <= 2100)
        {
            match["release"] = new JsonObject { ["year"] = year };
        }

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["menuId"] = (menuId ?? Guid.NewGuid()).ToString("D"),
            ["revision"] = 1,
            ["meta"] = new JsonObject { ["created"] = (created ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") },
            ["match"] = match,
            ["theme"] = new JsonObject { ["id"] = "list-dark", ["accent"] = "#4cc9f0", ["align"] = "left" },
            ["extras"] = new JsonObject(),
            ["root"] = "main",
            ["menus"] = new JsonObject
            {
                ["main"] = new JsonObject
                {
                    ["title"] = "Main Menu",
                    ["entries"] = new JsonArray(new JsonObject { ["action"] = "playFeature", ["label"] = "Play" }),
                },
            },
        };
    }
}
