using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AsterTracking.AniList;
using Jellyfin.Plugin.AsterTracking.MyAnimeList;
using Jellyfin.Plugin.AsterTracking.Simkl;
using Jellyfin.Plugin.AsterTracking.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AsterTracking.Discovery;

/// <summary>
/// Lists from each person's linked services for the Aster apps' Discover: what they plan to watch and are watching,
/// what's trending, this anime season, and what the services recommend for them. Every list comes back in one shape,
/// with TMDB ids where they can be found (anime through ARM), so the apps open Seerr's page for a title; and with the
/// title on this server, when it's here, so they can play it instead. Lists are kept 10 minutes per person.
/// </summary>
public sealed class DiscoveryService
{
    private const int MaxPageSize = 60;

    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    private readonly AccountStore _store;
    private readonly TokenService _tokens;
    private readonly SimklClient _simkl;
    private readonly AniListClient _aniList;
    private readonly MyAnimeListClient _mal;
    private readonly AnimeIds _anime;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly LibraryIndex _index;
    private readonly TmdbClient _tmdb;
    private readonly ConcurrentDictionary<(Guid User, string Service, string List), (List<DiscoverItemDto> Items, DateTimeOffset At)> _lists = new();

    public DiscoveryService(AccountStore store, TokenService tokens, SimklClient simkl, AniListClient aniList, MyAnimeListClient mal, AnimeIds anime, ILibraryManager library, IUserManager users, LibraryIndex index, TmdbClient tmdb)
    {
        _store = store;
        _tokens = tokens;
        _simkl = simkl;
        _aniList = aniList;
        _mal = mal;
        _anime = anime;
        _library = library;
        _users = users;
        _index = index;
        _tmdb = tmdb;
    }

    /// <summary>Every list, by service, in the order the apps show them.</summary>
    private static readonly (string Service, string List, string Kind, string Title, bool Personal)[] Catalog =
    [
        ("simkl", "watchlist", "Watchlist", "Simkl Watchlist", true),
        ("anilist", "watchlist", "Watchlist", "AniList Planning", true),
        ("myanimelist", "watchlist", "Watchlist", "MyAnimeList Plan to Watch", true),
        ("simkl", "watching", "Watching", "Watching on Simkl", true),
        ("anilist", "watching", "Watching", "Watching on AniList", true),
        ("myanimelist", "watching", "Watching", "Watching on MyAnimeList", true),
        ("anilist", "recommended", "Recommended", "Recommended by AniList", true),
        ("myanimelist", "recommended", "Recommended", "Suggested by MyAnimeList", true),
        ("simkl", "trending", "Trending", "Trending on Simkl", false),
        ("anilist", "trending", "Trending", "Trending Anime on AniList", false),
        ("myanimelist", "trending", "Trending", "Top Airing on MyAnimeList", false),
        ("anilist", "season", "This Season", "This Season's Anime", false),
    ];

    /// <summary>The lists this person can browse: those of the services they've linked.</summary>
    public async Task<List<DiscoverListDto>> ListsAsync(Guid userId)
    {
        var tracking = await _store.GetAsync(userId).ConfigureAwait(false);
        var linked = new HashSet<string>();
        if (tracking.Simkl is not null)
        {
            linked.Add("simkl");
        }

        if (tracking.AniList is not null)
        {
            linked.Add("anilist");
        }

        if (tracking.MyAnimeList is not null)
        {
            linked.Add("myanimelist");
        }

        // One "This Season" is enough: AniList's, with MyAnimeList's only for someone without AniList.
        var lists = Catalog.Where(c => linked.Contains(c.Service))
            .Select(c => new DiscoverListDto { Service = c.Service, List = c.List, Kind = c.Kind, Title = c.Title, Personal = c.Personal })
            .ToList();
        if (!linked.Contains("anilist") && linked.Contains("myanimelist"))
        {
            lists.Add(new DiscoverListDto { Service = "myanimelist", List = "season", Kind = "This Season", Title = "This Season's Anime" });
        }

        return lists;
    }

    /// <summary>A page of one list, or null when there's no such list for this person.</summary>
    public async Task<DiscoverPageDto?> PageAsync(Guid userId, string service, string list, int page, int pageSize, CancellationToken cancellation)
    {
        service = service.ToLowerInvariant();
        list = list.ToLowerInvariant();
        if (!(await ListsAsync(userId).ConfigureAwait(false)).Any(l => l.Service == service && l.List == list))
        {
            return null;
        }

        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(page, 1);
        var key = (userId, service, list);
        if (!_lists.TryGetValue(key, out var cached) || DateTimeOffset.UtcNow - cached.At > Keep)
        {
            var items = await FetchAsync(userId, service, list, cancellation).ConfigureAwait(false);
            // Ids once for the whole list (ARM in batches), not on every page, and genres where the service gave none.
            await FillIdsAsync(items, cancellation).ConfigureAwait(false);
            await FillAnimeGenresAsync(items, cancellation).ConfigureAwait(false);
            cached = (items, DateTimeOffset.UtcNow);
            _lists[key] = cached;
        }

        var slice = cached.Items.Skip((page - 1) * pageSize).Take(pageSize).Select(Copy).ToList();
        MatchLibrary(userId, slice);

        // What the server's admin keeps out of Discover.
        var rules = ContentFilter.For(userId);
        if (rules.Any)
        {
            await _tmdb.FillKeywordsAsync(slice, cancellation).ConfigureAwait(false);
        }

        slice.RemoveAll(rules.Blocks);
        return new DiscoverPageDto
        {
            Items = slice,
            Page = page,
            TotalItems = cached.Items.Count,
            HasMore = page * pageSize < cached.Items.Count,
        };
    }

    private async Task<List<DiscoverItemDto>> FetchAsync(Guid userId, string service, string list, CancellationToken cancellation)
    {
        return service switch
        {
            "simkl" => await SimklAsync(userId, list, cancellation).ConfigureAwait(false),
            "anilist" => await AniListAsync(userId, list, cancellation).ConfigureAwait(false),
            _ => await MyAnimeListAsync(userId, list, cancellation).ConfigureAwait(false),
        };
    }

    // ---- Simkl ----

    private async Task<List<DiscoverItemDto>> SimklAsync(Guid userId, string list, CancellationToken cancellation)
    {
        var token = await _tokens.TokenAsync(userId, _simkl, TimeSpan.FromDays(1)).ConfigureAwait(false) ?? throw new SignedOutException("simkl");
        var items = new List<(DateTimeOffset Added, DiscoverItemDto Item)>();
        if (list == "trending")
        {
            foreach (var (path, type) in new[] { ("movies/trending/today", "movie"), ("tv/trending/today", "tv"), ("anime/trending/today", "anime") })
            {
                var reply = await _simkl.GetAsync($"/{path}?extended=overview,tmdb", token, cancellation).ConfigureAwait(false);
                var rank = 0;
                foreach (var entry in reply?.AsArray() ?? [])
                {
                    if (entry is null)
                    {
                        continue;
                    }

                    var item = SimklItem(entry, type);
                    // Interleaved by rank: a film, a show, an anime, then the next of each.
                    items.Add((DateTimeOffset.MaxValue.AddMinutes(-(++rank * 3) - (type == "movie" ? 0 : type == "tv" ? 1 : 2)), item));
                }
            }

            return items.OrderByDescending(i => i.Added).Select(i => i.Item).Take(90).ToList();
        }

        var status = list == "watching" ? "watching" : "plantowatch";
        foreach (var type in new[] { "shows", "movies", "anime" })
        {
            var reply = await _simkl.GetAsync($"/sync/all-items/{type}/{status}?extended=full", token, cancellation).ConfigureAwait(false);
            foreach (var entry in reply?[type]?.AsArray() ?? [])
            {
                var media = entry?[type == "movies" ? "movie" : "show"];
                if (media is null)
                {
                    continue;
                }

                var item = SimklItem(media, type == "movies" ? "movie" : type == "anime" ? (entry?["anime_type"]?.ToString() == "movie" ? "movie" : "anime") : "tv");
                if (list == "watching" && entry?["watched_episodes_count"] is JsonValue w && w.TryGetValue<int>(out var watched))
                {
                    var total = entry["total_episodes_count"] is JsonValue t && t.TryGetValue<int>(out var all) ? all : (int?)null;
                    item.Progress = total is int count ? $"Episode {watched} of {count}" : $"{watched} episodes watched";
                }

                var added = DateTimeOffset.TryParse(entry?["last_watched_at"]?.ToString() ?? entry?["added_to_watchlist_at"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue;
                items.Add((added, item));
            }
        }

        return items.OrderByDescending(i => i.Added).Select(i => i.Item).ToList();
    }

    private static DiscoverItemDto SimklItem(JsonNode media, string type)
    {
        var item = new DiscoverItemDto
        {
            Title = media["title"]?.ToString() ?? string.Empty,
            Year = media["year"] is JsonValue y && y.TryGetValue<int>(out var year) ? year : YearOf(media["release_date"]?.ToString()),
            MediaType = type == "movie" ? "movie" : "tv",
            Anime = type == "anime",
            Poster = SimklClient.Poster(media["poster"]?.ToString()),
            Overview = media["overview"]?.ToString(),
        };
        item.Genres.AddRange((media["genres"]?.AsArray() ?? []).Select(g => g?.ToString()).OfType<string>());
        foreach (var (from, to) in new[] { ("tmdb", "tmdb"), ("tvdb", "tvdb"), ("imdb", "imdb"), ("anilist", "anilist"), ("mal", "mal"), ("anidb", "anidb"), ("simkl", "simkl"), ("simkl_id", "simkl") })
        {
            if (media["ids"]?[from] is JsonValue v && v.ToString() is { Length: > 0 } id)
            {
                item.Ids[to] = id;
            }
        }

        return item;
    }

    // ---- AniList ----

    internal const string AniListMedia = "id idMal title { english romaji } seasonYear format episodes coverImage { large } description(asHtml:false) genres tags { name isMediaSpoiler } isAdult";

    private async Task<List<DiscoverItemDto>> AniListAsync(Guid userId, string list, CancellationToken cancellation)
    {
        var token = await _tokens.TokenAsync(userId, _aniList, TimeSpan.FromDays(1)).ConfigureAwait(false) ?? throw new SignedOutException("anilist");
        var items = new List<DiscoverItemDto>();
        switch (list)
        {
            case "watchlist" or "watching":
            {
                var viewer = await _aniList.GraphAsync("{ Viewer { id } }", new JsonObject(), token, cancellation).ConfigureAwait(false);
                var id = viewer?["data"]?["Viewer"]?["id"]?.GetValue<int>() ?? throw new SignedOutException("anilist");
                var reply = await _aniList.GraphAsync(
                    "query($u:Int,$s:[MediaListStatus]){ MediaListCollection(userId:$u, type:ANIME, status_in:$s, sort:UPDATED_TIME_DESC) { lists { entries { progress media { " + AniListMedia + " } } } } }",
                    new JsonObject { ["u"] = id, ["s"] = list == "watching" ? new JsonArray("CURRENT", "REPEATING") : new JsonArray("PLANNING") },
                    token, cancellation).ConfigureAwait(false);
                foreach (var group in reply?["data"]?["MediaListCollection"]?["lists"]?.AsArray() ?? [])
                {
                    foreach (var entry in group?["entries"]?.AsArray() ?? [])
                    {
                        if (entry?["media"] is not JsonNode media)
                        {
                            continue;
                        }

                        var item = AniListItem(media);
                        if (list == "watching" && entry["progress"] is JsonValue p && p.TryGetValue<int>(out var progress))
                        {
                            var total = media["episodes"] is JsonValue e && e.TryGetValue<int>(out var all) ? all : (int?)null;
                            item.Progress = total is int count ? $"Episode {progress} of {count}" : $"{progress} episodes watched";
                        }

                        items.Add(item);
                    }
                }

                break;
            }

            case "recommended":
            {
                // Recommendations for what's on the person's list, best rated first, each title once and none already on it.
                var reply = await _aniList.GraphAsync(
                    "{ Page(perPage:50) { recommendations(onList:true, sort:RATING_DESC) { mediaRecommendation { " + AniListMedia + " mediaListEntry { status } } } } }",
                    new JsonObject(), token, cancellation).ConfigureAwait(false);
                var seen = new HashSet<string>();
                foreach (var r in reply?["data"]?["Page"]?["recommendations"]?.AsArray() ?? [])
                {
                    if (r?["mediaRecommendation"] is JsonNode media && media["mediaListEntry"] is null && seen.Add(media["id"]!.ToString()))
                    {
                        items.Add(AniListItem(media));
                    }
                }

                break;
            }

            default:
            {
                var (season, year) = Season(DateTime.UtcNow);
                var reply = await _aniList.GraphAsync(
                    list == "season"
                        ? "query($s:MediaSeason,$y:Int){ Page(perPage:50) { media(type:ANIME, season:$s, seasonYear:$y, sort:POPULARITY_DESC, isAdult:false) { " + AniListMedia + " } } }"
                        : "{ Page(perPage:50) { media(type:ANIME, sort:TRENDING_DESC, isAdult:false) { " + AniListMedia + " } } }",
                    list == "season" ? new JsonObject { ["s"] = season.ToUpperInvariant(), ["y"] = year } : new JsonObject(),
                    token, cancellation).ConfigureAwait(false);
                items.AddRange((reply?["data"]?["Page"]?["media"]?.AsArray() ?? []).OfType<JsonNode>().Select(AniListItem));
                break;
            }
        }

        return items;
    }

    internal static DiscoverItemDto AniListItem(JsonNode media)
    {
        var item = new DiscoverItemDto
        {
            Title = media["title"]?["english"]?.ToString() is { Length: > 0 } english ? english : media["title"]?["romaji"]?.ToString() ?? string.Empty,
            Year = media["seasonYear"] is JsonValue y && y.TryGetValue<int>(out var year) ? year : null,
            MediaType = media["format"]?.ToString() == "MOVIE" ? "movie" : "tv",
            Anime = true,
            Poster = media["coverImage"]?["large"]?.ToString(),
            Overview = media["description"]?.ToString(),
        };
        item.Ids["anilist"] = media["id"]!.ToString();
        item.Genres.AddRange((media["genres"]?.AsArray() ?? []).Select(g => g?.ToString()).OfType<string>());
        item.Tags.AddRange((media["tags"]?.AsArray() ?? []).Select(t => t?["name"]?.ToString()).OfType<string>());
        item.Adult = media["isAdult"] is JsonValue adult && adult.TryGetValue<bool>(out var isAdult) && isAdult;
        if (media["idMal"] is JsonValue mal && mal.TryGetValue<int>(out var malId))
        {
            item.Ids["mal"] = malId.ToString(CultureInfo.InvariantCulture);
        }

        return item;
    }

    // ---- MyAnimeList ----

    private const string MalFields = "fields=alternative_titles,start_season,media_type,num_episodes,synopsis,list_status,my_list_status,genres,nsfw";

    private async Task<List<DiscoverItemDto>> MyAnimeListAsync(Guid userId, string list, CancellationToken cancellation)
    {
        var token = await _tokens.TokenAsync(userId, _mal, TimeSpan.FromDays(1)).ConfigureAwait(false) ?? throw new SignedOutException("myanimelist");
        var (season, year) = Season(DateTime.UtcNow);
        var path = list switch
        {
            "watchlist" => $"/users/@me/animelist?status=plan_to_watch&sort=list_updated_at&limit=200&nsfw=true&{MalFields}",
            "watching" => $"/users/@me/animelist?status=watching&sort=list_updated_at&limit=200&nsfw=true&{MalFields}",
            "recommended" => $"/anime/suggestions?limit=60&{MalFields}",
            "season" => $"/anime/season/{year}/{season}?sort=anime_num_list_users&limit=60&{MalFields}",
            _ => $"/anime/ranking?ranking_type=airing&limit=60&{MalFields}",
        };
        var reply = await _mal.GetAsync(path, token, cancellation).ConfigureAwait(false);
        var items = new List<DiscoverItemDto>();
        foreach (var entry in reply?["data"]?.AsArray() ?? [])
        {
            if (entry?["node"] is not JsonNode node)
            {
                continue;
            }

            // Suggestions leave out what's already on the person's list.
            if (list == "recommended" && node["my_list_status"] is not null)
            {
                continue;
            }

            var item = new DiscoverItemDto
            {
                Title = node["alternative_titles"]?["en"]?.ToString() is { Length: > 0 } english ? english : node["title"]?.ToString() ?? string.Empty,
                Year = node["start_season"]?["year"] is JsonValue y && y.TryGetValue<int>(out var started) ? started : null,
                MediaType = node["media_type"]?.ToString() == "movie" ? "movie" : "tv",
                Anime = true,
                Poster = node["main_picture"]?["large"]?.ToString() ?? node["main_picture"]?["medium"]?.ToString(),
                Overview = node["synopsis"]?.ToString(),
            };
            item.Ids["mal"] = node["id"]!.ToString();
            item.Genres.AddRange((node["genres"]?.AsArray() ?? []).Select(g => g?["name"]?.ToString()).OfType<string>());
            item.Adult = node["nsfw"]?.ToString() == "black";
            if (list == "watching" && entry["list_status"]?["num_episodes_watched"] is JsonValue w && w.TryGetValue<int>(out var watched))
            {
                var total = node["num_episodes"] is JsonValue t && t.TryGetValue<int>(out var all) && all > 0 ? all : (int?)null;
                item.Progress = total is int count ? $"Episode {watched} of {count}" : $"{watched} episodes watched";
            }

            items.Add(item);
        }

        return items;
    }

    // ---- Ids and the library ----

    /// <summary>TMDB, TVDB, IMDb and AniDB ids for anime that came with only AniList or MyAnimeList's, from ARM.</summary>
    internal async Task FillIdsAsync(List<DiscoverItemDto> items, CancellationToken cancellation)
    {
        static (string Source, int Id)? Key(DiscoverItemDto item) =>
            item.Ids.TryGetValue("anilist", out var a) && int.TryParse(a, out var anilist) ? ("anilist", anilist)
            : item.Ids.TryGetValue("mal", out var m) && int.TryParse(m, out var mal) ? ("myanimelist", mal)
            : null;

        var wanted = items.Where(i => i.Anime && !i.Ids.ContainsKey("tmdb")).Select(Key).OfType<(string, int)>().ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var found = await _anime.LookupAsync(wanted, cancellation).ConfigureAwait(false);
        foreach (var item in items)
        {
            if (Key(item) is not { } key || !found.TryGetValue(key, out var ids) || ids is null)
            {
                continue;
            }

            void Put(string name, object? value)
            {
                if (value is not null && !item.Ids.ContainsKey(name))
                {
                    item.Ids[name] = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                }
            }

            Put("anilist", ids.AniList);
            Put("mal", ids.MyAnimeList);
            Put("anidb", ids.AniDb);
            Put("tmdb", ids.Tmdb);
            Put("tmdbSeason", ids.TmdbSeason);
            Put("tvdb", ids.Tvdb);
            Put("imdb", ids.Imdb);
            if (ids.Media is "MOVIE" or "Movie" or "movie")
            {
                item.MediaType = "movie";
            }
        }
    }

    /// <summary>
    /// AniList's genres, tags and adult flag for anime that came without them (Simkl's lists give none), by AniList id,
    /// 50 to a request. So a block on Ecchi or Nudity holds whichever service listed the title.
    /// </summary>
    internal async Task FillAnimeGenresAsync(IEnumerable<DiscoverItemDto> items, CancellationToken cancellation)
    {
        var bare = items.Where(i => i.Anime && i.Genres.Count == 0 && i.Tags.Count == 0 && i.Ids.ContainsKey("anilist"))
            .GroupBy(i => i.Ids["anilist"]).ToList();
        foreach (var chunk in bare.Chunk(50))
        {
            try
            {
                var ids = new JsonArray(chunk.Select(g => (JsonNode)JsonValue.Create(int.Parse(g.Key, CultureInfo.InvariantCulture))).ToArray());
                var reply = await _aniList.GraphAsync(
                    "query($ids:[Int]){ Page(perPage:50) { media(id_in:$ids) { id genres tags { name } isAdult } } }",
                    new JsonObject { ["ids"] = ids }, null, cancellation).ConfigureAwait(false);
                foreach (var media in reply?["data"]?["Page"]?["media"]?.AsArray() ?? [])
                {
                    var group = chunk.FirstOrDefault(g => g.Key == media?["id"]?.ToString());
                    foreach (var item in group ?? Enumerable.Empty<DiscoverItemDto>())
                    {
                        item.Genres.AddRange((media!["genres"]?.AsArray() ?? []).Select(g => g?.ToString()).OfType<string>());
                        item.Tags.AddRange((media["tags"]?.AsArray() ?? []).Select(t => t?["name"]?.ToString()).OfType<string>());
                        item.Adult |= media["isAdult"] is JsonValue a && a.TryGetValue<bool>(out var adult) && adult;
                    }
                }
            }
            catch (Exception ex) when (ex is TrackerException or SignedOutException or HttpRequestException or TaskCanceledException or FormatException)
            {
                // Left as they were: the other checks still apply.
            }
        }
    }

    /// <summary>Each title's film or show on this server, among what this person may see (from the library index).</summary>
    private void MatchLibrary(Guid userId, List<DiscoverItemDto> items)
    {
        if (_users.GetUserById(userId) is not { } user)
        {
            return;
        }

        var index = _index.For(user);
        foreach (var item in items)
        {
            index.Match(item);
        }
    }

    private static DiscoverItemDto Copy(DiscoverItemDto item) => new()
    {
        Title = item.Title, Year = item.Year, MediaType = item.MediaType, Ids = new Dictionary<string, string>(item.Ids), Poster = item.Poster,
        Overview = item.Overview, Progress = item.Progress, Anime = item.Anime,
        Genres = new List<string>(item.Genres), Tags = new List<string>(item.Tags), Adult = item.Adult,
    };

    private static int? YearOf(string? date) =>
        DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Year
        : DateTime.TryParseExact(date, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.Year : null;

    /// <summary>The anime season a date falls in, as AniList and MyAnimeList name them: winter, spring, summer, fall.</summary>
    private static (string Season, int Year) Season(DateTime date) => (date.Month switch
    {
        <= 3 => "winter",
        <= 6 => "spring",
        <= 9 => "summer",
        _ => "fall",
    }, date.Year);
}
