using System.Text.Json.Nodes;
using Jellyfin.Plugin.AsterTracking.Configuration;

namespace Jellyfin.Plugin.AsterTracking.Discovery;

/// <summary>
/// What the server's admin keeps out of Discover: adult titles, and titles with a blocked genre or tag (AniList's and
/// MyAnimeList's genres and tags, TMDB's genres and keywords, the library's genres and tags), server-wide and for
/// particular people. Names match whole, ignoring case. Applied to the plugin's rows and to Seerr's answers passing
/// through it.
/// </summary>
public sealed class ContentFilter
{
    /// <summary>TMDB's genres by id (films and shows), for Seerr's and TMDB's lists, which give only ids.</summary>
    public static readonly IReadOnlyDictionary<int, string> TmdbGenres = new Dictionary<int, string>
    {
        [28] = "Action", [12] = "Adventure", [16] = "Animation", [35] = "Comedy", [80] = "Crime", [99] = "Documentary",
        [18] = "Drama", [10751] = "Family", [14] = "Fantasy", [36] = "History", [27] = "Horror", [10402] = "Music",
        [9648] = "Mystery", [10749] = "Romance", [878] = "Science Fiction", [10770] = "TV Movie", [53] = "Thriller",
        [10752] = "War", [37] = "Western", [10759] = "Action & Adventure", [10762] = "Kids", [10763] = "News",
        [10764] = "Reality", [10765] = "Sci-Fi & Fantasy", [10766] = "Soap", [10767] = "Talk", [10768] = "War & Politics",
    };

    /// <summary>The rules for one person: the server's, with theirs added.</summary>
    public static Rules For(Guid userId)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var genres = new HashSet<string>(Names(config.BlockedGenres), StringComparer.OrdinalIgnoreCase);
        var tags = new HashSet<string>(Names(config.BlockedTags), StringComparer.OrdinalIgnoreCase);
        var adult = config.BlockAdult;
        foreach (var own in config.PersonBlocks.Where(p => Names(p.UserIds).Append(p.UserId).Any(id => Guid.TryParse(id, out var who) && who == userId)))
        {
            genres.UnionWith(Names(own.Genres));
            tags.UnionWith(Names(own.Tags));
            adult |= own.BlockAdult;
        }

        return new Rules(adult, genres, tags);
    }

    /// <summary>A comma- or line-separated list as names.</summary>
    public static IEnumerable<string> Names(string? text) =>
        (text ?? string.Empty).Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public sealed record Rules(bool Adult, HashSet<string> Genres, HashSet<string> Tags)
    {
        public bool Any => Adult || Genres.Count > 0 || Tags.Count > 0;

        /// <summary>Whether the title is kept out. A blocked genre counts among tags too, and the other way round: sources disagree on which is which (Ecchi is AniList's genre, MyAnimeList's theme).</summary>
        public bool Blocks(DiscoverItemDto item) =>
            (Adult && item.Adult)
            || item.Genres.Concat(item.Tags).Any(n => Genres.Contains(n) || Tags.Contains(n));

        public bool Blocks(bool adult, IEnumerable<string> names) =>
            (Adult && adult) || names.Any(n => Genres.Contains(n) || Tags.Contains(n));

        /// <summary>
        /// Seerr's answer without what's blocked: a list's <c>results</c> filtered (by genre ids and the adult flag,
        /// all a list gives), or null for a title page that's blocked (its genres and keywords), or the answer as it was.
        /// </summary>
        public string? Filter(string path, string json, IReadOnlyDictionary<string, IReadOnlyList<string>>? known = null)
        {
            if (!Any)
            {
                return json;
            }

            JsonNode? reply;
            try
            {
                reply = JsonNode.Parse(json);
            }
            catch (System.Text.Json.JsonException)
            {
                return json;
            }

            if (reply is JsonObject page && page["results"] is JsonArray results)
            {
                var kept = new JsonArray();
                foreach (var result in results.ToList())
                {
                    results.Remove(result);
                    if (result is not JsonObject title || !BlocksSeerrTitle(title, known))
                    {
                        kept.Add(result);
                    }
                }

                page["results"] = kept;
                return page.ToJsonString();
            }

            // A film's or show's own page: its genres and keywords are all there.
            if (reply is JsonObject one && System.Text.RegularExpressions.Regex.IsMatch(path, @"^(movie|tv)/\d+$") && BlocksSeerrTitle(one, null))
            {
                return null;
            }

            return json;
        }

        /// <summary>
        /// A title in Seerr's answer: its genres (ids in lists, names on its page), keywords, adult flag, and in
        /// <paramref name="known"/> ("movie:603") its genres and keywords looked up for lists, which carry none.
        /// </summary>
        private bool BlocksSeerrTitle(JsonObject title, IReadOnlyDictionary<string, IReadOnlyList<string>>? known)
        {
            if (title["mediaType"]?.ToString() == "person")
            {
                return false;
            }

            var names = new List<string>();
            foreach (var id in title["genreIds"]?.AsArray() ?? [])
            {
                if (id is JsonValue v && v.TryGetValue<int>(out var genre) && TmdbGenres.TryGetValue(genre, out var name))
                {
                    names.Add(name);
                }
            }

            names.AddRange((title["genres"]?.AsArray() ?? []).Select(g => g?["name"]?.ToString()).OfType<string>());
            names.AddRange((title["keywords"]?.AsArray() ?? []).Select(k => k?["name"]?.ToString()).OfType<string>());
            if (known is not null && SeerrKey(title) is { } key && known.TryGetValue(key, out var more))
            {
                names.AddRange(more);
            }

            var adult = title["adult"] is JsonValue a && a.TryGetValue<bool>(out var isAdult) && isAdult;
            return Blocks(adult, names);
        }

        /// <summary>"movie:603" or "tv:1399" for a title in Seerr's answers.</summary>
        public static string? SeerrKey(JsonObject title)
        {
            var type = title["mediaType"]?.ToString();
            return type is "movie" or "tv" && title["id"] is JsonValue v && v.TryGetValue<int>(out var id) ? $"{type}:{id}" : null;
        }
    }
}
