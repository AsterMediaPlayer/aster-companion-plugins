using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpEd.Themes;

/// <summary>
/// Song metadata from AnimeThemes.moe, looked up by AniDB id.
///
/// AnimeThemes runs on a single small server and asks API users to keep the load low, so: <see cref="PrefetchAsync"/> fetches
/// every id a scan needs from their GraphQL API, up to 50 per
/// request, one request at a time with a gap between them; answers are cached on disk per AniDB id (including "nothing
/// for this id") and only re-fetched after the configured number of days; <see cref="GetThemes"/> reads that cache
/// only. Requests identify the plugin honestly in the User-Agent.
/// </summary>
public sealed partial class AnimeThemesClient
{
    private const string Api = "https://graphql.animethemes.moe/";
    // The agent string agreed with AnimeThemes: the plugin's name and a link to its author. Fixed, with no version number, so
    // that it keeps matching what they allowed, and without the word "jellyfin", which their filter refuses.
    private const string UserAgent = "OpEd-Themes (+https://github.com/peppy6582)";
    private const int BatchSize = 50;
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string _cacheDir;
    private bool _refused;

    public AnimeThemesClient(IHttpClientFactory httpClientFactory, ILogger logger, string cacheDir)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    /// <summary>One theme as AnimeThemes describes it, flattened to what we use.</summary>
    public sealed record Entry(string Type, int Sequence, string Slug, int Version, string? Song, List<string> Artists, string? Episodes, bool Spoiler, bool Nsfw, string? Overlap = null, bool? Creditless = null);

    /// <summary>An anime's id on one of the sites AnimeThemes knows (AniList, MyAnimeList, Kitsu).</summary>
    public readonly record struct ExternalId(string Site, int Id)
    {
        public string Key => $"{Site}-{Id}";
    }

    /// <summary>Fetches, in batches, every id whose cached answer is missing or older than <paramref name="refreshDays"/>.</summary>
    public async Task PrefetchAsync(IEnumerable<int> aniDbIds, int refreshDays, CancellationToken ct)
    {
        var stale = aniDbIds.Distinct().Where(id => IsStale(id, refreshDays)).Order().ToList();
        if (stale.Count == 0 || RefusedRecently())
        {
            return;
        }

        _logger.LogInformation("OP/ED: fetching AnimeThemes metadata for {Count} AniDB entries in {Batches} request(s)", stale.Count, (stale.Count + BatchSize - 1) / BatchSize);
        foreach (var batch in stale.Chunk(BatchSize))
        {
            var animeList = await QueryAsync("ANIDB", batch, ct).ConfigureAwait(false);
            if (animeList is null)
            {
                if (_refused)
                {
                    return;
                }

                continue; // network trouble: keep whatever was cached before; retried on the next scan
            }

            var found = batch.ToDictionary(id => id, _ => new JsonArray());
            foreach (var anime in animeList)
            {
                if (anime is null)
                {
                    continue;
                }

                var shaped = ToCacheShape(anime);
                foreach (var resource in anime["resources"]?["nodes"]?.AsArray() ?? [])
                {
                    if (string.Equals((string?)resource?["site"], "ANIDB", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(resource?["externalId"]?.ToString(), out var id)
                        && found.TryGetValue(id, out var list))
                    {
                        list.Add(shaped.DeepClone());
                    }
                }
            }

            foreach (var (id, list) in found)
            {
                await WriteCacheAsync(id, list, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Works out the AniDB id of anime that Jellyfin knows only by an AniList, MyAnimeList or Kitsu id. AnimeThemes lists every
    /// id an anime has, so one batched lookup per site gives the AniDB id and, in the same answer, the themes, which are
    /// cached under that AniDB id like any other. Answers (including "no AniDB id") are remembered for <paramref name="refreshDays"/>.
    /// </summary>
    public async Task ResolveAsync(IEnumerable<ExternalId> ids, int refreshDays, CancellationToken ct)
    {
        var resolved = LoadResolved();
        var stale = ids.Distinct().Where(i => !resolved.TryGetValue(i.Key, out var r) || r.At < Cutoff(r.Misses, refreshDays)).ToList();
        if (stale.Count == 0 || RefusedRecently())
        {
            return;
        }

        foreach (var bySite in stale.GroupBy(i => i.Site))
        {
            foreach (var batch in bySite.Chunk(BatchSize))
            {
                _logger.LogInformation("OP/ED: looking up {Count} {Site} ids on AnimeThemes", batch.Length, bySite.Key);
                var animeList = await QueryAsync(bySite.Key, [.. batch.Select(b => b.Id)], ct).ConfigureAwait(false);
                if (animeList is null)
                {
                    if (_refused)
                    {
                        SaveResolved(resolved);
                        return;
                    }

                    continue;
                }

                var now = DateTime.UtcNow;
                foreach (var id in batch)
                {
                    resolved[id.Key] = new Resolved(null, now, (resolved.TryGetValue(id.Key, out var before) ? before.Misses : 0) + 1);
                }

                foreach (var anime in animeList)
                {
                    if (anime is null)
                    {
                        continue;
                    }

                    int? aniDb = null;
                    var queried = new List<int>();
                    foreach (var resource in anime["resources"]?["nodes"]?.AsArray() ?? [])
                    {
                        var site = (string?)resource?["site"];
                        if (!int.TryParse(resource?["externalId"]?.ToString(), out var id))
                        {
                            continue;
                        }

                        if (string.Equals(site, "ANIDB", StringComparison.OrdinalIgnoreCase))
                        {
                            aniDb ??= id;
                        }
                        else if (string.Equals(site, bySite.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            queried.Add(id);
                        }
                    }

                    if (aniDb is not int a)
                    {
                        continue;
                    }

                    foreach (var id in queried)
                    {
                        if (resolved.ContainsKey(new ExternalId(bySite.Key, id).Key))
                        {
                            resolved[new ExternalId(bySite.Key, id).Key] = new Resolved(a, now, 0);
                        }
                    }

                    await WriteCacheAsync(a, new JsonArray(ToCacheShape(anime)), ct).ConfigureAwait(false);
                }

                SaveResolved(resolved);
            }
        }
    }

    /// <summary>The AniDB id found for an AniList/MAL/Kitsu id by an earlier <see cref="ResolveAsync"/>; null when unknown or when AnimeThemes has none.</summary>
    public int? GetResolved(ExternalId id) => LoadResolved().TryGetValue(id.Key, out var r) ? r.AniDb : null;

    private sealed record Resolved(int? AniDb, DateTime At, int Misses);

    private Dictionary<string, Resolved>? _resolved;

    private string ResolvedFile => Path.Combine(_cacheDir, "external-ids.json");

    private Dictionary<string, Resolved> LoadResolved()
    {
        if (_resolved is not null)
        {
            return _resolved;
        }

        _resolved = [];
        try
        {
            if (File.Exists(ResolvedFile) && JsonNode.Parse(File.ReadAllText(ResolvedFile)) is JsonObject o)
            {
                foreach (var (key, v) in o)
                {
                    if (DateTime.TryParse((string?)v?["at"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                    {
                        _resolved[key] = new Resolved((int?)v?["anidb"], at, (int?)v?["misses"] ?? ((int?)v?["anidb"] is null ? 1 : 0));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning("OP/ED: ignoring unreadable {File}: {Message}", ResolvedFile, e.Message);
        }

        return _resolved;
    }

    private void SaveResolved(Dictionary<string, Resolved> resolved)
    {
        var o = new JsonObject();
        foreach (var (key, r) in resolved)
        {
            o[key] = new JsonObject { ["anidb"] = r.AniDb, ["misses"] = r.Misses, ["at"] = r.At.ToString("o", System.Globalization.CultureInfo.InvariantCulture) };
        }

        File.WriteAllText(ResolvedFile, o.ToJsonString());
    }

    // After AnimeThemes refuses us, wait a day before asking again rather than knocking on every scan.
    private string RefusedFile => Path.Combine(_cacheDir, "refused-until");

    private bool RefusedRecently()
    {
        if (File.Exists(RefusedFile) && DateTime.TryParse(File.ReadAllText(RefusedFile), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var until) && until > DateTime.UtcNow)
        {
            _logger.LogDebug("OP/ED: AnimeThemes refused recently; not asking again until {Until}", until);
            return true;
        }

        return false;
    }

    /// <summary>One batched GraphQL lookup of anime by their id on <paramref name="site"/>; null when it failed (and, if refused, sets the day's pause).</summary>
    private async Task<JsonArray?> QueryAsync(string site, int[] ids, CancellationToken ct) =>
        await PostAsync(RequestBody(site, ids), "findAnimeByExternalSite", ct).ConfigureAwait(false) as JsonArray;

    /// <summary>Sends a GraphQL request and returns the node at <c>data.{field}</c> (a dotted path, empty for <c>data</c> itself); null when it failed (and, if refused, sets the day's pause).</summary>
    private async Task<JsonNode?> PostAsync(string body, string field, CancellationToken ct)
    {
        var json = await FetchAsync(body, ct).ConfigureAwait(false);
        if (json is not null)
        {
            var answer = JsonNode.Parse(json);
            JsonNode? at = answer?["data"];
            foreach (var part in field.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                at = at?[part];
            }

            if (at is not null)
            {
                return at;
            }

            var errors = answer?["errors"]?.ToJsonString() ?? "no data";
            _logger.LogWarning("OP/ED: AnimeThemes answered without data: {Errors}", errors.Length > 300 ? errors[..300] : errors);
        }

        if (_refused)
        {
            await File.WriteAllTextAsync(RefusedFile, DateTime.UtcNow.AddDays(1).ToString("o", System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            _logger.LogInformation("OP/ED: AnimeThemes is refusing this client; trying again in a day (themes keep any cached or local details)");
        }

        return null;
    }

    /// <summary>Cached themes for an AniDB anime: empty when AnimeThemes has none, null when it was never fetched.</summary>
    public List<Entry>? GetThemes(int aniDbId)
    {
        var file = CacheFile(aniDbId);
        return File.Exists(file) ? Parse(File.ReadAllText(file)) : null;
    }

    // GraphQL has a field made for this: every anime that has one of the given AniDB ids, with its themes in one call.
    // The themes of an anime, as much as we use of them; shared by the id lookup and the title search.
    private const string ThemeFields = "animethemes { type sequence slug song { title { romaji native } performances { artist { name { main native } } } } "
        + "animethemeentries { version episodes spoiler nsfw videos { nodes { nc overlap } } } }";

    private const string Query = "query($site: ResourceSite!, $ids:[Int!]){ findAnimeByExternalSite(site: $site, id: $ids) { resources { nodes { site externalId } } " + ThemeFields + " } }";

    private static string RequestBody(string site, IEnumerable<int> ids) =>
        new JsonObject
        {
            ["query"] = Query,
            ["variables"] = new JsonObject { ["site"] = site, ["ids"] = new JsonArray([.. ids.Select(i => (JsonNode)JsonValue.Create(i)!)]) },
        }.ToJsonString();

    /// <summary>
    /// One GraphQL anime reshaped into what the on-disk cache holds (the shape the REST API used to return), so cached
    /// answers from before the move to GraphQL stay valid and <see cref="Parse"/> reads both.
    /// </summary>
    private static JsonObject ToCacheShape(JsonNode anime)
    {
        var themes = new JsonArray();
        foreach (var t in anime["animethemes"]?.AsArray() ?? [])
        {
            if (t is null)
            {
                continue;
            }

            var artists = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in t["song"]?["performances"]?.AsArray() ?? [])
            {
                var name = (string?)p?["artist"]?["name"]?["main"] ?? (string?)p?["artist"]?["name"]?["native"];
                if (name is not null && seen.Add(name))
                {
                    artists.Add(new JsonObject { ["name"] = name });
                }
            }

            var entries = new JsonArray();
            foreach (var e in t["animethemeentries"]?.AsArray() ?? [])
            {
                entries.Add(new JsonObject
                {
                    ["version"] = (int?)e?["version"] ?? 1,
                    ["episodes"] = (string?)e?["episodes"],
                    ["spoiler"] = (bool?)e?["spoiler"] ?? false,
                    ["nsfw"] = (bool?)e?["nsfw"] ?? false,
                    ["overlap"] = OverlapOf(e?["videos"]?["nodes"]?.AsArray()),
                    ["creditless"] = e?["videos"]?["nodes"] is JsonArray vids && vids.Any(x => (bool?)x?["nc"] == true),
                });
            }

            themes.Add(new JsonObject
            {
                ["type"] = (string?)t["type"],
                ["sequence"] = (int?)t["sequence"],
                ["slug"] = (string?)t["slug"],
                ["song"] = t["song"] is null ? null : new JsonObject
                {
                    ["title"] = (string?)t["song"]?["title"]?["romaji"] ?? (string?)t["song"]?["title"]?["native"],
                    ["artists"] = artists,
                },
                ["animethemeentries"] = entries,
            });
        }

        return new JsonObject { ["animethemes"] = themes };
    }

    // Anime that AnimeThemes has (with themes) are re-fetched after the configured number of days. Ones it doesn't have are
    // usually just not added yet, so they are asked again sooner: after 1, 3 and 7 days, then every 30.
    private static int MissDays(int misses) => misses <= 1 ? 1 : misses == 2 ? 3 : misses == 3 ? 7 : 30;

    private static DateTime Cutoff(int misses, int refreshDays) =>
        DateTime.UtcNow.AddDays(-(misses > 0 ? MissDays(misses) : Math.Max(1, refreshDays)));

    private bool IsStale(int aniDbId, int refreshDays)
    {
        var file = CacheFile(aniDbId);
        return !File.Exists(file) || File.GetLastWriteTimeUtc(file) < Cutoff(MissesOf(file), refreshDays);
    }

    /// <summary>How many times in a row AnimeThemes had no themes for this id. Files from before this was recorded count an empty answer as one.</summary>
    private static int MissesOf(string file)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject o)
            {
                if ((int?)o["misses"] is int recorded)
                {
                    return recorded;
                }

                return o["anime"] is JsonArray a && a.Any(x => x?["animethemes"] is JsonArray t && t.Count > 0) ? 0 : 1;
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
        }

        return 0;
    }

    private async Task WriteCacheAsync(int aniDbId, JsonArray anime, CancellationToken ct)
    {
        var file = CacheFile(aniDbId);
        var hasThemes = anime.Any(x => x?["animethemes"] is JsonArray t && t.Count > 0);
        var misses = hasThemes ? 0 : (File.Exists(file) ? MissesOf(file) : 0) + 1;
        await File.WriteAllTextAsync(file, new JsonObject { ["anime"] = anime, ["misses"] = misses }.ToJsonString(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// How much of the episode shows through the video: "none", "transition" (episode footage at the start or end) or "over"
    /// (the song plays over the episode). An entry has several videos (credits or creditless, different sources); the
    /// cleanest one counts, since it can be picked. Null when AnimeThemes gave no videos.
    /// </summary>
    private static string? OverlapOf(JsonArray? videos)
    {
        var rank = int.MaxValue;
        foreach (var video in videos ?? [])
        {
            var r = ((string?)video?["overlap"])?.ToUpperInvariant() switch { "NONE" => 0, "TRANS" => 1, "OVER" => 2, _ => -1 };
            if (r >= 0)
            {
                rank = Math.Min(rank, r);
            }
        }

        return rank switch { 0 => "none", 1 => "transition", 2 => "over", _ => null };
    }

    private string CacheFile(int aniDbId) => Path.Combine(_cacheDir, $"anidb-{aniDbId}.json");

    private async Task<string?> FetchAsync(string body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wait = _lastRequest + MinGap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                _lastRequest = DateTime.UtcNow;
                using var client = _httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, Api) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
                request.Headers.UserAgent.ParseAdd(UserAgent);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                    _logger.LogInformation("OP/ED: AnimeThemes rate limit hit, waiting {Seconds}s", retry.TotalSeconds);
                    await Task.Delay(retry, ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _refused = response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)520 or HttpStatusCode.Unauthorized;
                    // 403/520 here means AnimeThemes is refusing this client; don't retry, just report it.
                    _logger.LogWarning("OP/ED: AnimeThemes refused the metadata request ({Status}); themes keep any cached metadata", (int)response.StatusCode);
                    return null;
                }

                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                _logger.LogWarning("OP/ED: AnimeThemes request failed: {Message}", e.Message);
            }
            finally
            {
                Gate.Release();
            }
        }

        return null;
    }

    private static List<Entry> Parse(string json)
    {
        var result = new List<Entry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("anime", out var animeList))
        {
            return result;
        }

        // Two AnimeThemes anime can share one AniDB id (a show and its special, say) and both have an "OP1". The one with the
        // most themes is the main one; the others only add slugs it lacks, so they never override its songs.
        var seen = new HashSet<(string Slug, int Version)>();
        foreach (var anime in animeList.EnumerateArray().OrderByDescending(a => a.TryGetProperty("animethemes", out var th) && th.ValueKind == JsonValueKind.Array ? th.GetArrayLength() : 0))
        {
            if (!anime.TryGetProperty("animethemes", out var themes))
            {
                continue;
            }

            foreach (var t in themes.EnumerateArray())
            {
                var type = Str(t, "type") ?? string.Empty;
                var sequence = t.TryGetProperty("sequence", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 1;
                var slug = Str(t, "slug") ?? type + sequence;
                string? song = null;
                var artists = new List<string>();
                if (t.TryGetProperty("song", out var songEl) && songEl.ValueKind == JsonValueKind.Object)
                {
                    song = Str(songEl, "title");
                    if (songEl.TryGetProperty("artists", out var artistsEl) && artistsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in artistsEl.EnumerateArray())
                        {
                            if (Str(a, "name") is { } name)
                            {
                                artists.Add(name);
                            }
                        }
                    }
                }

                if (!t.TryGetProperty("animethemeentries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
                {
                    if (seen.Add((slug, 1)))
                    {
                        result.Add(new Entry(type, sequence, slug, 1, song, artists, null, false, false));
                    }

                    continue;
                }

                foreach (var e in entries.EnumerateArray())
                {
                    var version = e.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1;
                    if (!seen.Add((slug, version)))
                    {
                        continue;
                    }

                    result.Add(new Entry(type, sequence, slug, version, song, artists, Str(e, "episodes"),
                        Bool(e, "spoiler"), Bool(e, "nsfw"), Str(e, "overlap"), e.TryGetProperty("creditless", out var nc) && nc.ValueKind is JsonValueKind.True or JsonValueKind.False ? nc.ValueKind == JsonValueKind.True : null));
                }
            }
        }

        return result;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
