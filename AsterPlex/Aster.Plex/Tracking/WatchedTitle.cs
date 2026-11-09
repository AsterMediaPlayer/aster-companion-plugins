using System.Globalization;
using System.Text.Json.Nodes;

namespace Aster.Plex.Tracking;

/// <summary>
/// What a watched film or episode is, for the services' histories: the film's or show's ids (TMDB, IMDb, TVDB), with
/// an episode's season and number. Anime from Shokofin goes by its season's AniDB id instead, as Simkl lists anime
/// (each season its own entry, numbered by episode alone), the same way the Aster apps send the MyAnimeList id.
/// </summary>
public sealed class WatchedTitle
{
    private WatchedTitle(string label, Dictionary<string, string> ids, int? season, IReadOnlyList<int> episodes, bool anime)
    {
        Label = label;
        Ids = ids;
        Season = season;
        Episodes = episodes;
        Anime = anime;
    }

    /// <summary>"Frieren S01E05", as the plugin's log shows it.</summary>
    public string Label { get; }

    /// <summary>The services' names for the ids: "tmdb", "imdb", "tvdb", or "anidb" for anime.</summary>
    public Dictionary<string, string> Ids { get; }

    /// <summary>The season, for a show that isn't anime; null for a film.</summary>
    public int? Season { get; }

    /// <summary>The episode numbers (more than one for a file holding several); empty for a film.</summary>
    public IReadOnlyList<int> Episodes { get; }

    public bool IsMovie => Episodes.Count == 0;

    /// <summary>An anime season sent by its AniDB id.</summary>
    public bool Anime { get; }

    /// <summary>The film or episode as the server has it, or null when there's nothing to match it by.</summary>
    public static WatchedTitle? From(PlayedItem item)
    {
        switch (item)
        {
            case { Kind: PlayedKind.Movie } movie:
            {
                var ids = VideoIds(movie.ProviderIds);
                var year = movie.ProductionYear is int y ? $" ({y})" : string.Empty;
                return ids.Count == 0 ? null : new WatchedTitle(movie.Name + year, ids, null, [], false);
            }

            case { Kind: PlayedKind.Episode } episode when episode.IndexNumber is int first:
            {
                var last = episode.IndexNumberEnd is int end && end > first ? end : first;
                var numbers = Enumerable.Range(first, last - first + 1).ToList();
                var label = $"{episode.SeriesName} S{episode.ParentIndexNumber ?? 0:00}E{first:00}";

                // A season that's one AniDB entry goes by its AniDB id. Its specials share the series' id and would
                // land on the wrong episodes, so they're left out. AniDB guesses for whole shows don't count.
                var aniDb = AnimeIds.SeasonAniDb(episode);
                if (!string.IsNullOrEmpty(aniDb))
                {
                    return episode.ParentIndexNumber == 0
                        ? null
                        : new WatchedTitle(label, new() { ["anidb"] = aniDb }, null, numbers, true);
                }

                var ids = VideoIds(episode.SeriesProviderIds);
                return ids.Count == 0 || episode.ParentIndexNumber is not int season
                    ? null
                    : new WatchedTitle(label, ids, season, numbers, false);
            }

            default:
                return null;
        }
    }

    private static Dictionary<string, string> VideoIds(IReadOnlyDictionary<string, string> providerIds)
    {
        var ids = new Dictionary<string, string>();
        foreach (var (service, provider) in new[] { ("tmdb", "Tmdb"), ("imdb", "Imdb"), ("tvdb", "Tvdb") })
        {
            if (providerIds.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id))
            {
                ids[service] = id.Trim();
            }
        }

        return ids;
    }

    /// <summary>
    /// The history body: <c>movies</c>, or <c>shows</c> with the season and episodes, or for anime <c>shows</c> with
    /// the episodes alone. Numeric ids go as numbers, as the services list them.
    /// </summary>
    public JsonObject HistoryBody(DateTimeOffset watchedAt)
    {
        var at = watchedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var ids = new JsonObject();
        foreach (var (name, value) in Ids)
        {
            ids[name] = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? JsonValue.Create(number)
                : JsonValue.Create(value);
        }

        if (IsMovie)
        {
            return new JsonObject { ["movies"] = new JsonArray(new JsonObject { ["ids"] = ids, ["watched_at"] = at }) };
        }

        var episodes = new JsonArray(Episodes.Select(n => (JsonNode)new JsonObject { ["number"] = n, ["watched_at"] = at }).ToArray());
        var show = new JsonObject { ["ids"] = ids };
        if (Anime)
        {
            show["episodes"] = episodes;
        }
        else
        {
            show["seasons"] = new JsonArray(new JsonObject { ["number"] = Season, ["episodes"] = episodes });
        }

        return new JsonObject { ["shows"] = new JsonArray(show) };
    }
}
