using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Aster.Plex.Tracking;

/// <summary>
/// Which AniList and MyAnimeList entry an anime episode belongs to. Those sites list each season (each cour, often)
/// apart, so it's the season's ids that count: an AniList or MyAnimeList id the server has for it, else the AniDB id
/// Shokofin gives each season (one AniDB entry), mapped through ARM (arm.haglund.dev, the anime-lists id map), as the
/// Aster apps do. Remembered for a day.
/// </summary>
public sealed class AnimeIds
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<AnimeIds> _log;
    private readonly ConcurrentDictionary<string, (AnimeEntry? Entry, DateTimeOffset At)> _found = new();
    private readonly ConcurrentDictionary<(string Source, int Id), (ArmIds? Ids, DateTimeOffset At)> _full = new();

    public AnimeIds(IHttpClientFactory http, ILogger<AnimeIds> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// The AniDB id of an episode's season, when it's that season's own (see <see cref="PlayedItem.SeasonProviderIds"/>).
    /// Plugins and agents guess AniDB ids for films and cartoons too (The 'Burbs, Invader ZIM), so only those count.
    /// </summary>
    public static string? SeasonAniDb(PlayedItem item) => item.GetSeasonProviderId("AniDB");

    /// <summary>The episode's anime entry and its episode number there, or null when it isn't anime the sites can be matched to.</summary>
    public async Task<(AnimeEntry Entry, int Episode)?> ForAsync(PlayedItem item, CancellationToken cancellation)
    {
        // A season that's one AniDB entry is numbered as AniDB does, which the sites follow; specials share the series' ids.
        if (item is not { Kind: PlayedKind.Episode, IndexNumber: int first, ParentIndexNumber: > 0 } episode)
        {
            return null;
        }

        var last = episode.IndexNumberEnd is int end && end > first ? end : first;
        var aniList = Number(episode.GetSeasonProviderId("AniList"));
        var mal = Number(episode.GetSeasonProviderId("MyAnimeList"));
        AnimeEntry? entry = null;
        if (aniList is not null && mal is not null)
        {
            entry = new AnimeEntry(aniList, mal);
        }
        else if (aniList is not null || mal is not null)
        {
            var mapped = await MapAsync(aniList is not null ? "anilist" : "myanimelist", (aniList ?? mal)!.Value, cancellation).ConfigureAwait(false);
            entry = new AnimeEntry(aniList ?? mapped?.AniList, mal ?? mapped?.MyAnimeList);
        }
        else if (Number(SeasonAniDb(episode)) is int aniDb)
        {
            entry = await MapAsync("anidb", aniDb, cancellation).ConfigureAwait(false);
        }
        else if (Number(episode.GetSeriesProviderId("Tvdb")) is int tvdb && episode.ParentIndexNumber is int seasonNumber)
        {
            // Plex's own agent and Shoko give shows their TVDB id only: ARM knows which anime entry each TVDB season is.
            entry = await MapTvdbSeasonAsync(tvdb, seasonNumber, cancellation).ConfigureAwait(false);
        }

        return entry is null ? null : (entry, last);
    }

    /// <summary>
    /// The AniList and MyAnimeList entry for a title the apps describe by its ids ("anilist", "mal", "anidb"), mapped
    /// through ARM for whichever is missing; null when it has none of them (not anime the sites can be matched to).
    /// </summary>
    public async Task<AnimeEntry?> EntryForAsync(IReadOnlyDictionary<string, string> ids, CancellationToken cancellation)
    {
        var aniList = ids.TryGetValue("anilist", out var a) ? Number(a) : null;
        var mal = ids.TryGetValue("mal", out var m) ? Number(m) : null;
        if (aniList is not null && mal is not null)
        {
            return new AnimeEntry(aniList, mal);
        }

        if (aniList is not null || mal is not null)
        {
            var mapped = await MapAsync(aniList is not null ? "anilist" : "myanimelist", (aniList ?? mal)!.Value, cancellation).ConfigureAwait(false);
            return new AnimeEntry(aniList ?? mapped?.AniList, mal ?? mapped?.MyAnimeList);
        }

        return ids.TryGetValue("anidb", out var d) && Number(d) is int aniDb
            ? await MapAsync("anidb", aniDb, cancellation).ConfigureAwait(false)
            : null;
    }

    /// <summary>Whether the ids name an anime entry the sites can be matched to.</summary>
    public static bool HasAnimeIds(IReadOnlyDictionary<string, string> ids) =>
        ids.Keys.Any(k => k is "anilist" or "mal" or "anidb");

    /// <summary>ARM's AniList and MyAnimeList ids for one site's id.</summary>
    private async Task<AnimeEntry?> MapAsync(string source, int id, CancellationToken cancellation)
    {
        var key = $"{source}:{id}";
        if (_found.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < Keep)
        {
            return known.Entry;
        }

        try
        {
            var client = _http.CreateClient("AsterTracking.Arm");
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.GetAsync(new Uri($"https://arm.haglund.dev/api/v2/ids?source={source}&id={id}"), cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A miss is a 404 (or "null"); anything else isn't remembered, so it's asked again next time.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _found[key] = (null, DateTimeOffset.UtcNow);
                }

                return null;
            }

            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            var aniList = reply?["anilist"] is JsonValue a && a.TryGetValue<int>(out var al) ? al : (int?)null;
            var mal = reply?["myanimelist"] is JsonValue m && m.TryGetValue<int>(out var ml) ? ml : (int?)null;
            var entry = aniList is null && mal is null ? null : new AnimeEntry(aniList, mal);
            _found[key] = (entry, DateTimeOffset.UtcNow);
            return entry;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _log.LogWarning("Aster Discovery and Tracking: ARM lookup {Key} failed: {Error}", key, ex.Message);
            throw new TrackerException("ARM", 0);
        }
    }

    private static int? Number(string? text) => int.TryParse(text, out var n) ? n : null;

    /// <summary>
    /// The anime entry a TVDB show's season is, through ARM; null when it isn't anime ARM knows, or when the season is
    /// split across several entries (two cours under one TVDB season), as ARM doesn't say where each begins.
    /// </summary>
    private async Task<AnimeEntry?> MapTvdbSeasonAsync(int tvdb, int season, CancellationToken cancellation)
    {
        var key = $"thetvdb:{tvdb}:{season}";
        if (_found.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < Keep)
        {
            return known.Entry;
        }

        var entries = await ArmEntriesAsync("thetvdb", tvdb, cancellation).ConfigureAwait(false);
        if (entries is null)
        {
            return null;
        }

        // A show with one entry and no season given is that entry's season 1.
        var matching = entries.Where(e => (e.TvdbSeason ?? (entries.Count == 1 ? 1 : null)) == season).ToList();
        var entry = matching.Count == 1 ? Entry(matching[0]) : null;
        _found[key] = (entry, DateTimeOffset.UtcNow);
        return entry;
    }

    /// <summary>
    /// The anime entry a whole show (by its TVDB id) or film (by its TMDB id) starts with, for a watchlist: its only
    /// entry, or its first season's; null when ARM doesn't know it as anime. ARM's sources: "thetvdb", "themoviedb".
    /// </summary>
    public async Task<AnimeEntry?> TitleEntryAsync(string source, int id, CancellationToken cancellation)
    {
        var key = $"{source}:{id}:title";
        if (_found.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < Keep)
        {
            return known.Entry;
        }

        var entries = await ArmEntriesAsync(source, id, cancellation).ConfigureAwait(false);
        if (entries is null)
        {
            return null;
        }

        var first = entries.Count == 1
            ? entries[0]
            : entries.Where(e => (source == "thetvdb" ? e.TvdbSeason : e.TmdbSeason) == 1).ToList() is { Count: 1 } season1 ? season1[0] : null;
        var entry = first is null ? null : Entry(first);
        _found[key] = (entry, DateTimeOffset.UtcNow);
        return entry;
    }

    private static AnimeEntry? Entry(ArmIds ids) =>
        ids.AniList is not null || ids.MyAnimeList is not null ? new AnimeEntry(ids.AniList, ids.MyAnimeList) : null;

    /// <summary>Every anime entry ARM has for one TVDB or TMDB id (empty when it isn't anime); null when ARM didn't answer.</summary>
    private async Task<List<ArmIds>?> ArmEntriesAsync(string source, int id, CancellationToken cancellation)
    {
        try
        {
            var client = _http.CreateClient("AsterTracking.Arm");
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.GetAsync(new Uri($"https://arm.haglund.dev/api/v2/{source}?id={id}"), cancellation).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return (JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false)) as JsonArray)?
                .OfType<JsonObject>().Select(ArmIds.From).ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _log.LogWarning("Aster for Plex: ARM lookup {Source} {Id} failed: {Error}", source, id, ex.Message);
            throw new TrackerException("ARM", 0);
        }
    }

    /// <summary>
    /// Every id ARM knows for many entries at once ("anilist", "myanimelist", "anidb"), for a page of a discovery
    /// list: one request for those not already known, at most 100 at a time. Entries ARM doesn't know come back null.
    /// </summary>
    public async Task<Dictionary<(string Source, int Id), ArmIds?>> LookupAsync(IEnumerable<(string Source, int Id)> wanted, CancellationToken cancellation)
    {
        var result = new Dictionary<(string Source, int Id), ArmIds?>();
        var missing = new List<(string Source, int Id)>();
        foreach (var key in wanted.Distinct())
        {
            if (_full.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < Keep)
            {
                result[key] = known.Ids;
            }
            else if (key.Id > 0 && key.Id <= 50_000_000)
            {
                missing.Add(key);
            }
        }

        foreach (var chunk in missing.Chunk(100))
        {
            try
            {
                var body = new JsonArray(chunk.Select(k => (JsonNode)new JsonObject { [k.Source] = k.Id }).ToArray());
                var client = _http.CreateClient("AsterTracking.Arm");
                client.Timeout = TimeSpan.FromSeconds(20);
                using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(new Uri("https://arm.haglund.dev/api/v2/ids"), content, cancellation).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var replies = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false)) as JsonArray;
                for (var i = 0; i < chunk.Length; i++)
                {
                    var ids = replies is not null && i < replies.Count && replies[i] is JsonObject found ? ArmIds.From(found) : null;
                    result[chunk[i]] = ids;
                    _full[chunk[i]] = (ids, DateTimeOffset.UtcNow);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _log.LogWarning("Aster Discovery and Tracking: ARM batch lookup failed: {Error}", ex.Message);
            }
        }

        return result;
    }
}

/// <summary>An anime entry's ids across the sites, as ARM maps them.</summary>
public sealed record ArmIds(int? AniList, int? MyAnimeList, int? AniDb, int? Tmdb, int? TmdbSeason, int? Tvdb, string? Imdb, string? Media, int? TvdbSeason = null)
{
    public static ArmIds From(JsonObject o)
    {
        static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : null;
        return new ArmIds(Int(o["anilist"]), Int(o["myanimelist"]), Int(o["anidb"]), Int(o["themoviedb"]), Int(o["themoviedb-season"]),
            Int(o["thetvdb"]), Str(o["imdb"]), Str(o["media"]), Int(o["thetvdb-season"]));
    }
}

/// <summary>An entry on the anime sites.</summary>
public sealed record AnimeEntry(int? AniList, int? MyAnimeList);
