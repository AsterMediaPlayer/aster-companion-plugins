using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AsterTracking.AniList;
using Jellyfin.Plugin.AsterTracking.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AsterTracking.Discovery;

/// <summary>
/// The whole Discover page for one person, built on the server and sent to the Aster apps ready to show: what they're
/// watching on their services, what's recommended for them (from what they've watched here, their favourites, and the
/// recommendations of their linked services), "because you watched" rows, what's on the server that they haven't
/// watched and would like, their watchlists, and what's trending. Built only from that person's own plays and accounts.
/// Titles on the server come first, since they can be played now. Kept 30 minutes per person.
/// </summary>
public sealed class HomeService
{
    private const int RowLength = 30;

    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);

    private readonly DiscoveryService _discovery;
    private readonly TmdbClient _tmdb;
    private readonly AniListClient _aniList;
    private readonly AnimeIds _anime;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly LibraryIndex _index;
    private readonly ILogger<HomeService> _log;
    private readonly ConcurrentDictionary<Guid, (DiscoverHomeDto Home, DateTimeOffset At)> _homes = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _building = new();

    public HomeService(DiscoveryService discovery, TmdbClient tmdb, AniListClient aniList, AnimeIds anime, ILibraryManager library, IUserManager users, IUserDataManager userData, LibraryIndex index, ILogger<HomeService> log)
    {
        _discovery = discovery;
        _tmdb = tmdb;
        _aniList = aniList;
        _anime = anime;
        _library = library;
        _users = users;
        _userData = userData;
        _index = index;

        // The admin's blocks changed: pages are built again with them.
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += (_, _) => _homes.Clear();
        }
        _log = log;
    }

    /// <summary>When each person last asked, so their page is kept warm (see <see cref="WarmAsync"/>).</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _asked = new();

    /// <summary>
    /// The person's page. A page older than half an hour comes back at once and is built again in the background, so
    /// nobody waits for a build once they've had a page; only the very first (or <paramref name="fresh"/>) waits.
    /// </summary>
    public async Task<DiscoverHomeDto?> HomeAsync(Guid userId, bool fresh, CancellationToken cancellation)
    {
        if (_users.GetUserById(userId) is not { } user)
        {
            return null;
        }

        _asked[userId] = DateTimeOffset.UtcNow;
        if (!fresh && _homes.TryGetValue(userId, out var cached))
        {
            if (DateTimeOffset.UtcNow - cached.At >= Keep)
            {
                _ = RebuildAsync(user);
            }

            return cached.Home;
        }

        return await BuildLockedAsync(user, fresh, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds again the pages of everyone who's asked in the last day and whose page is getting old, so the next look is
    /// instant. Run every quarter of an hour by <see cref="DiscoverWarmer"/>.
    /// </summary>
    public async Task WarmAsync()
    {
        foreach (var (userId, asked) in _asked.ToList())
        {
            if (DateTimeOffset.UtcNow - asked > TimeSpan.FromDays(1))
            {
                _asked.TryRemove(userId, out _);
                continue;
            }

            if ((!_homes.TryGetValue(userId, out var home) || DateTimeOffset.UtcNow - home.At > Keep - TimeSpan.FromMinutes(10))
                && _users.GetUserById(userId) is { } user)
            {
                await RebuildAsync(user).ConfigureAwait(false);
            }
        }
    }

    private async Task RebuildAsync(User user)
    {
        try
        {
            await BuildLockedAsync(user, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Aster Tracking: building {User}'s Discover page failed", user.Username);
        }
    }

    private async Task<DiscoverHomeDto> BuildLockedAsync(User user, bool fresh, CancellationToken cancellation)
    {
        var gate = _building.GetOrAdd(user.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Built meanwhile by another request: that one will do.
            if (_homes.TryGetValue(user.Id, out var cached) && DateTimeOffset.UtcNow - cached.At < (fresh ? TimeSpan.FromSeconds(30) : Keep))
            {
                return cached.Home;
            }

            var home = await BuildAsync(user, cancellation).ConfigureAwait(false);
            _homes[user.Id] = (home, DateTimeOffset.UtcNow);
            return home;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DiscoverHomeDto> BuildAsync(User user, CancellationToken cancellation)
    {
        var profile = Profile(user);
        var lists = await _discovery.ListsAsync(user.Id).ConfigureAwait(false);

        async Task<List<DiscoverItemDto>> Lists(string kind)
        {
            var pages = await Task.WhenAll(lists.Where(l => l.Kind == kind).Select(async l =>
            {
                try
                {
                    return (await _discovery.PageAsync(user.Id, l.Service, l.List, 1, 60, cancellation).ConfigureAwait(false))?.Items ?? [];
                }
                catch (Exception ex) when (ex is TrackerException or SignedOutException or HttpRequestException or TaskCanceledException)
                {
                    return new List<DiscoverItemDto>();
                }
            })).ConfigureAwait(false);
            return Merge(pages);
        }

        var watching = Lists("Watching");
        var watchlists = Lists("Watchlist");
        var serviceRecommendations = Lists("Recommended");
        var because = Task.WhenAll(profile.Seeds.Take(8).Select((seed, rank) => SeedRecommendationsAsync(seed, rank, cancellation)));
        var trending = TrendingAsync(lists, user.Id, cancellation);
        var season = SeasonAsync(cancellation);

        // Every source's titles together, scored: a title several sources suggest ranks higher.
        var seeds = (await because.ConfigureAwait(false)).Where(s => s.Items.Count > 0).ToList();
        var candidates = new Dictionary<string, (DiscoverItemDto Item, double Score)>();
        void Add(IEnumerable<DiscoverItemDto> items, double weight)
        {
            var position = 0;
            foreach (var item in items)
            {
                // Earlier in a source's list is a better match.
                var score = weight * (1.0 - Math.Min(position++, 20) * 0.02);
                var key = Key(item);
                candidates[key] = candidates.TryGetValue(key, out var known) ? (known.Item, known.Score + score) : (item, score);
            }
        }

        foreach (var seed in seeds)
        {
            Add(seed.Items, seed.Weight);
        }

        Add(await serviceRecommendations.ConfigureAwait(false), 0.8);

        // Every suggested title's ids (anime through ARM) and its film or show on the server, before any filtering.
        var all = seeds.SelectMany(s => s.Items).Concat(candidates.Values.Select(c => c.Item)).Distinct().ToList();
        await _discovery.FillIdsAsync(all, cancellation).ConfigureAwait(false);
        foreach (var item in all)
        {
            Match(profile, item);
        }

        // What the server's admin keeps out of Discover, for this person.
        var rules = ContentFilter.For(user.Id);
        if (rules.Tags.Count > 0)
        {
            await _tmdb.FillKeywordsAsync(all, cancellation).ConfigureAwait(false);
        }

        var recommended = candidates.Values
            .Where(c => !profile.Watched(c.Item) && !rules.Blocks(c.Item))
            .Select(c => (c.Item, Score: c.Score * (c.Item.InLibrary is not null ? 1.6 : 1.0) + profile.Affinity(c.Item) * 0.3))
            .OrderByDescending(c => c.Score)
            .Select(c => c.Item)
            .Take(RowLength)
            .ToList();

        var rows = new List<DiscoverRowDto>();
        void Row(string id, string kind, string title, string? subtitle, IEnumerable<DiscoverItemDto> items)
        {
            var list = items.Take(RowLength).ToList();
            if (list.Count > 0)
            {
                rows.Add(new DiscoverRowDto { Id = id, Kind = kind, Title = title, Subtitle = subtitle, Items = list });
            }
        }

        Row("continue", "continue", "Continue Watching on Your Services", null, await watching.ConfigureAwait(false));
        Row("recommended", "recommended", "Recommended for You", "From what you watch and your linked services", recommended);

        // "Because you watched": the three most recent with recommendations, each ranked the same way.
        foreach (var seed in seeds.Take(3))
        {
            var items = seed.Items.Where(i => !profile.Watched(i)).OrderByDescending(i => i.InLibrary is not null).ToList();
            Row($"because-{seed.Source.Id}", "because", $"Because You Watched {seed.Source.Name}", null, items);
        }

        Row("library", "library", "On Your Server, Not Yet Watched", "Picked for you from what's here", Unwatched(profile, candidates).Where(i => !rules.Blocks(i)));
        var watchlist = await watchlists.ConfigureAwait(false);
        Row("watchlist", "watchlist", "Your Watchlists", null, watchlist);
        Row("trending", "trending", "Trending", null, await trending.ConfigureAwait(false));
        Row("season", "season", "This Season's Anime", null, await season.ConfigureAwait(false));

        // Library matches for rows built from services' and TMDB's titles that weren't scored above.
        foreach (var item in rows.SelectMany(r => r.Items).Where(i => i.InLibrary is null))
        {
            Match(profile, item);
        }

        // The blocks over every row, then rows left empty dropped.
        if (rules.Any)
        {
            if (rules.Tags.Count > 0)
            {
                await _tmdb.FillKeywordsAsync(rows.SelectMany(r => r.Items), cancellation).ConfigureAwait(false);
            }

            foreach (var row in rows)
            {
                row.Items.RemoveAll(rules.Blocks);
            }

            rows.RemoveAll(r => r.Items.Count == 0);
        }

        return new DiscoverHomeDto { Rows = rows, Built = DateTimeOffset.UtcNow };
    }

    // ---- The person's history ----

    private sealed class ViewerProfile
    {
        public List<BaseItem> Seeds { get; } = new();

        public HashSet<Guid> WatchedIds { get; } = new();

        public Dictionary<string, double> Genres { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The library's films and shows by their ids, shared with the lists (<see cref="LibraryIndex"/>).</summary>
        public LibraryIndex.Index Library { get; set; } = new();

        public List<BaseItem> Catalog => Library.Catalog;

        public bool Watched(DiscoverItemDto item) =>
            (item.InLibrary is { } here && WatchedIds.Contains(here.ItemId))
            || (item.Ids.TryGetValue("tmdb", out var tmdb) && WatchedTmdb.Contains($"{item.MediaType}:{tmdb}"));

        /// <summary>Whether any copy of this film or show on the server has been watched (or started).</summary>
        public bool WatchedAnyCopy(BaseItem item) =>
            WatchedIds.Contains(item.Id) || (item.GetProviderId("Tmdb") is { Length: > 0 } tmdb && WatchedTmdb.Contains($"{(item is Movie ? "movie" : "tv")}:{tmdb}"));

        public HashSet<string> WatchedTmdb { get; } = new(StringComparer.OrdinalIgnoreCase);

        public double Affinity(DiscoverItemDto item) =>
            item.InLibrary is { } here && Catalog.FirstOrDefault(c => c.Id == here.ItemId) is { } found ? Affinity(found) : 0;

        public double Affinity(BaseItem item) => item.Genres.Sum(g => Genres.GetValueOrDefault(g));
    }

    private ViewerProfile Profile(User user)
    {
        var profile = new ViewerProfile();

        profile.Library = _index.For(user);

        // What they've watched, newest first: films, and shows through their episodes.
        var played = _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsPlayed = true,
            Recursive = true,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = 4000,
        });
        var seen = new HashSet<Guid>();
        foreach (var item in played)
        {
            var title = item is Episode episode ? (BaseItem?)episode.Series : item;
            if (title is null || !seen.Add(title.Id))
            {
                continue;
            }

            profile.WatchedIds.Add(title.Id);
            if (profile.Seeds.Count < 12)
            {
                profile.Seeds.Add(title);
            }

            // Genres weighed by how recent: the last few hundred titles, the newest counting most.
            var weight = 1.0 / (1 + (seen.Count / 25.0));
            foreach (var genre in title.Genres)
            {
                profile.Genres[genre] = profile.Genres.GetValueOrDefault(genre) + weight;
            }
        }

        // Favourites count as seeds too, after the most recent.
        foreach (var favourite in _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsFavorite = true,
            Recursive = true,
            Limit = 20,
        }))
        {
            if (profile.Seeds.All(s => s.Id != favourite.Id))
            {
                profile.Seeds.Add(favourite);
            }
        }

        // Films and shows they've started count as watched, so they aren't recommended back to them.
        foreach (var started in _library.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie], IsResumable = true, Recursive = true, Limit = 500 }))
        {
            profile.WatchedIds.Add(started is Episode { Series: { } series } ? series.Id : started.Id);
        }

        foreach (var id in profile.WatchedIds)
        {
            if (profile.Catalog.FirstOrDefault(c => c.Id == id) is { } watched && watched.GetProviderId("Tmdb") is { Length: > 0 } tmdb)
            {
                profile.WatchedTmdb.Add($"{(watched is Movie ? "movie" : "tv")}:{tmdb}");
            }
        }

        var top = profile.Genres.Values.DefaultIfEmpty(1).Max();
        foreach (var genre in profile.Genres.Keys.ToList())
        {
            profile.Genres[genre] /= top;
        }

        return profile;
    }

    // ---- Sources ----

    private sealed record Seed(BaseItem Source, double Weight, List<DiscoverItemDto> Items);

    /// <summary>What's like one title they watched: AniList's recommendations for anime, TMDB's for the rest.</summary>
    private async Task<Seed> SeedRecommendationsAsync(BaseItem source, int rank, CancellationToken cancellation)
    {
        var weight = 1.0 / (1 + (rank * 0.25));
        try
        {
            // Anime from Shokofin: AniList knows what's like it best.
            var aniDb = source.GetProviderId("AniDB");
            if (!string.IsNullOrEmpty(source.GetProviderId("Shoko Series")) && int.TryParse(aniDb, out var aniDbId))
            {
                var ids = await _anime.LookupAsync([("anidb", aniDbId)], cancellation).ConfigureAwait(false);
                if (ids.GetValueOrDefault(("anidb", aniDbId))?.AniList is int aniListId)
                {
                    return new Seed(source, weight, await AniListRecommendationsAsync(aniListId, cancellation).ConfigureAwait(false));
                }
            }

            if (_tmdb.Available && int.TryParse(source.GetProviderId("Tmdb"), out var tmdb))
            {
                return new Seed(source, weight, await _tmdb.RecommendationsAsync(source is Movie ? "movie" : "tv", tmdb, cancellation).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is TrackerException or HttpRequestException or TaskCanceledException)
        {
            _log.LogDebug("Aster Tracking: recommendations for {Title} failed: {Error}", source.Name, ex.Message);
        }

        return new Seed(source, weight, []);
    }

    private const string AniListMedia = DiscoveryService.AniListMedia;

    private async Task<List<DiscoverItemDto>> AniListRecommendationsAsync(int id, CancellationToken cancellation)
    {
        var reply = await _aniList.GraphAsync(
            "query($id:Int){ Media(id:$id) { recommendations(sort:RATING_DESC, perPage:20) { nodes { rating mediaRecommendation { " + AniListMedia + " } } } } }",
            new JsonObject { ["id"] = id }, null, cancellation).ConfigureAwait(false);
        return (reply?["data"]?["Media"]?["recommendations"]?["nodes"]?.AsArray() ?? [])
            .Select(n => n?["mediaRecommendation"])
            .OfType<JsonNode>()
            .Where(m => m["isAdult"]?.GetValue<bool>() != true)
            .Select(DiscoveryService.AniListItem)
            .ToList();
    }

    /// <summary>TMDB's trending films and shows, with AniList's trending anime woven in; Simkl's without a TMDB key.</summary>
    private async Task<List<DiscoverItemDto>> TrendingAsync(List<DiscoverListDto> lists, Guid userId, CancellationToken cancellation)
    {
        var general = new List<DiscoverItemDto>();
        try
        {
            if (_tmdb.Available)
            {
                general = await _tmdb.TrendingAsync(cancellation).ConfigureAwait(false);
            }
            else if (lists.Any(l => l.Service == "simkl" && l.List == "trending"))
            {
                general = (await _discovery.PageAsync(userId, "simkl", "trending", 1, 60, cancellation).ConfigureAwait(false))?.Items ?? [];
            }
        }
        catch (Exception ex) when (ex is TrackerException or SignedOutException or HttpRequestException or TaskCanceledException)
        {
            _log.LogDebug("Aster Tracking: trending failed: {Error}", ex.Message);
        }

        var anime = new List<DiscoverItemDto>();
        try
        {
            var reply = await _aniList.GraphAsync("{ Page(perPage:20) { media(type:ANIME, sort:TRENDING_DESC, isAdult:false) { " + AniListMedia + " } } }", new JsonObject(), null, cancellation).ConfigureAwait(false);
            anime = (reply?["data"]?["Page"]?["media"]?.AsArray() ?? []).OfType<JsonNode>().Select(DiscoveryService.AniListItem).ToList();
            await _discovery.FillIdsAsync(anime, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TrackerException or SignedOutException or HttpRequestException or TaskCanceledException)
        {
            _log.LogDebug("Aster Tracking: trending anime failed: {Error}", ex.Message);
        }

        // Two of everyone's, then one anime.
        var woven = new List<DiscoverItemDto>();
        for (int g = 0, a = 0; g < general.Count || a < anime.Count;)
        {
            for (var i = 0; i < 2 && g < general.Count; i++)
            {
                woven.Add(general[g++]);
            }

            if (a < anime.Count)
            {
                woven.Add(anime[a++]);
            }
        }

        return Merge([woven]);
    }

    private async Task<List<DiscoverItemDto>> SeasonAsync(CancellationToken cancellation)
    {
        try
        {
            var now = DateTime.UtcNow;
            var season = now.Month switch { <= 3 => "WINTER", <= 6 => "SPRING", <= 9 => "SUMMER", _ => "FALL" };
            var reply = await _aniList.GraphAsync(
                "query($s:MediaSeason,$y:Int){ Page(perPage:40) { media(type:ANIME, season:$s, seasonYear:$y, sort:POPULARITY_DESC, isAdult:false) { " + AniListMedia + " } } }",
                new JsonObject { ["s"] = season, ["y"] = now.Year }, null, cancellation).ConfigureAwait(false);
            var items = (reply?["data"]?["Page"]?["media"]?.AsArray() ?? []).OfType<JsonNode>().Select(DiscoveryService.AniListItem).ToList();
            await _discovery.FillIdsAsync(items, cancellation).ConfigureAwait(false);
            return items;
        }
        catch (Exception ex) when (ex is TrackerException or SignedOutException or HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }

    // ---- The library ----

    /// <summary>
    /// Films and shows on the server that this person hasn't started, best matches first: those the recommendations
    /// also suggested, then by the genres they watch most and how well each is rated.
    /// </summary>
    private List<DiscoverItemDto> Unwatched(ViewerProfile profile, Dictionary<string, (DiscoverItemDto Item, double Score)> candidates)
    {
        var suggested = candidates.Values.Where(c => c.Item.InLibrary is not null)
            .GroupBy(c => c.Item.InLibrary!.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Score));
        return profile.Catalog
            .Where(i => !profile.WatchedAnyCopy(i))
            .Select(i => (Item: i, Score: profile.Affinity(i) + ((i.CommunityRating ?? 5) / 20.0) + (suggested.GetValueOrDefault(i.Id) * 2)))
            .OrderByDescending(x => x.Score)
            .Select(x => LibraryItem(x.Item))
            // One of each title, however many copies the server has (a 4K one beside the usual).
            .DistinctBy(Key)
            .Take(RowLength)
            .ToList();
    }

    private static DiscoverItemDto LibraryItem(BaseItem item)
    {
        var movie = item is Movie;
        var dto = new DiscoverItemDto
        {
            Title = item.Name,
            Year = item.ProductionYear,
            MediaType = movie ? "movie" : "tv",
            // Relative to the server: the apps fetch it there, as any library picture.
            Poster = $"/Items/{item.Id:N}/Images/Primary?maxHeight=450",
            Overview = item.Overview,
            Anime = !string.IsNullOrEmpty(item.GetProviderId("Shoko Series")),
            Genres = item.Genres.ToList(),
            Tags = item.Tags.ToList(),
            InLibrary = new LibraryMatchDto { ItemId = item.Id, Name = item.Name, Type = movie ? "Movie" : "Series" },
        };
        foreach (var (provider, name) in new[] { ("Tmdb", "tmdb"), ("Tvdb", "tvdb"), ("Imdb", "imdb"), ("AniDB", "anidb") })
        {
            if (item.GetProviderId(provider) is { Length: > 0 } id)
            {
                dto.Ids[name] = id;
            }
        }

        return dto;
    }

    /// <summary>The item's film or show on the server, through the library index.</summary>
    private static void Match(ViewerProfile profile, DiscoverItemDto item) => profile.Library.Match(item);

    /// <summary>A title's identity across sources: its TMDB id, else its AniList or MyAnimeList id, else its name and year.</summary>
    private static string Key(DiscoverItemDto item) =>
        item.Ids.TryGetValue("tmdb", out var tmdb) ? $"tmdb:{item.MediaType}:{tmdb}"
        : item.Ids.TryGetValue("anilist", out var anilist) ? $"anilist:{anilist}"
        : item.Ids.TryGetValue("mal", out var mal) ? $"mal:{mal}"
        : $"name:{item.Title.ToLowerInvariant()}:{item.Year}";

    /// <summary>Lists one after another, each title once (the first it appears in).</summary>
    private static List<DiscoverItemDto> Merge(IEnumerable<List<DiscoverItemDto>> lists)
    {
        var seen = new HashSet<string>();
        var merged = new List<DiscoverItemDto>();
        foreach (var item in lists.SelectMany(l => l))
        {
            if (seen.Add(Key(item)) && (!item.Ids.TryGetValue("anilist", out var a) || seen.Add($"anilist:{a}")))
            {
                merged.Add(item);
            }
        }

        return merged;
    }
}
