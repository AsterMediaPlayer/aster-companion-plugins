using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AsterTracking.Tracking;

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
    /// The AniDB id of a season Shokofin manages. Other metadata plugins guess AniDB ids for films and cartoons
    /// (The 'Burbs, Invader ZIM), so only seasons carrying Shoko's own id count.
    /// </summary>
    public static string? ShokoAniDb(Season? season) =>
        season is not null && !string.IsNullOrEmpty(season.GetProviderId("Shoko Series"))
            ? season.GetProviderId("AniDB") is { Length: > 0 } id ? id : null
            : null;

    /// <summary>The episode's anime entry and its episode number there, or null when it isn't anime the sites can be matched to.</summary>
    public async Task<(AnimeEntry Entry, int Episode)?> ForAsync(BaseItem item, CancellationToken cancellation)
    {
        // Shokofin numbers a season's episodes as AniDB does, which the sites follow; specials share the series' ids.
        if (item is not Episode { IndexNumber: int first, ParentIndexNumber: > 0 } episode)
        {
            return null;
        }

        var last = episode.IndexNumberEnd is int end && end > first ? end : first;
        var season = episode.Season;
        var aniList = Number(season?.GetProviderId("AniList"));
        var mal = Number(season?.GetProviderId("MyAnimeList"));
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
        else if (Number(ShokoAniDb(season)) is int aniDb)
        {
            entry = await MapAsync("anidb", aniDb, cancellation).ConfigureAwait(false);
        }

        return entry is null ? null : (entry, last);
    }

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
public sealed record ArmIds(int? AniList, int? MyAnimeList, int? AniDb, int? Tmdb, int? TmdbSeason, int? Tvdb, string? Imdb, string? Media)
{
    public static ArmIds From(JsonObject o)
    {
        static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : null;
        return new ArmIds(Int(o["anilist"]), Int(o["myanimelist"]), Int(o["anidb"]), Int(o["themoviedb"]), Int(o["themoviedb-season"]),
            Int(o["thetvdb"]), Str(o["imdb"]), Str(o["media"]));
    }
}

/// <summary>An entry on the anime sites.</summary>
public sealed record AnimeEntry(int? AniList, int? MyAnimeList);
