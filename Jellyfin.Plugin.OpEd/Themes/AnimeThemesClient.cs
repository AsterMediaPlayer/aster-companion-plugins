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
public sealed class AnimeThemesClient
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
    public sealed record Entry(string Type, int Sequence, string Slug, int Version, string? Song, List<string> Artists, string? Episodes, bool Spoiler, bool Nsfw);

    /// <summary>Fetches, in batches, every id whose cached answer is missing or older than <paramref name="refreshDays"/>.</summary>
    public async Task PrefetchAsync(IEnumerable<int> aniDbIds, int refreshDays, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, refreshDays));
        var stale = aniDbIds.Distinct().Where(id => !File.Exists(CacheFile(id)) || File.GetLastWriteTimeUtc(CacheFile(id)) < cutoff).Order().ToList();
        if (stale.Count == 0)
        {
            return;
        }

        // After AnimeThemes refuses us, wait a day before asking again rather than knocking on every scan.
        var refusedFile = Path.Combine(_cacheDir, "refused-until");
        if (File.Exists(refusedFile) && DateTime.TryParse(File.ReadAllText(refusedFile), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var until) && until > DateTime.UtcNow)
        {
            _logger.LogDebug("OP/ED: AnimeThemes refused recently; not asking again until {Until}", until);
            return;
        }

        _logger.LogInformation("OP/ED: fetching AnimeThemes metadata for {Count} AniDB entries in {Batches} request(s)", stale.Count, (stale.Count + BatchSize - 1) / BatchSize);
        foreach (var batch in stale.Chunk(BatchSize))
        {
            var found = batch.ToDictionary(id => id, _ => new JsonArray());
            var ok = false;
            var json = await FetchAsync(RequestBody(batch), ct).ConfigureAwait(false);
            if (json is not null)
            {
                var answer = JsonNode.Parse(json);
                if (answer?["data"]?["findAnimeByExternalSite"] is JsonArray animeList)
                {
                    ok = true;
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
                }
                else
                {
                    var errors = answer?["errors"]?.ToJsonString() ?? "no data";
                    _logger.LogWarning("OP/ED: AnimeThemes answered without data: {Errors}", errors.Length > 300 ? errors[..300] : errors);
                }
            }

            if (!ok)
            {
                if (_refused)
                {
                    await File.WriteAllTextAsync(refusedFile, DateTime.UtcNow.AddDays(1).ToString("o", System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                    _logger.LogInformation("OP/ED: AnimeThemes is refusing this client; trying again in a day (themes keep any cached or local details)");
                    return;
                }

                continue; // network trouble: keep whatever was cached before; retried on the next scan
            }

            foreach (var (id, list) in found)
            {
                await File.WriteAllTextAsync(CacheFile(id), new JsonObject { ["anime"] = list }.ToJsonString(), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Cached themes for an AniDB anime: empty when AnimeThemes has none, null when it was never fetched.</summary>
    public List<Entry>? GetThemes(int aniDbId)
    {
        var file = CacheFile(aniDbId);
        return File.Exists(file) ? Parse(File.ReadAllText(file)) : null;
    }

    // GraphQL has a field made for this: every anime that has one of the given AniDB ids, with its themes in one call.
    private const string Query = "query($ids:[Int!]){ findAnimeByExternalSite(site: ANIDB, id: $ids) { resources { nodes { site externalId } } "
        + "animethemes { type sequence slug song { title { romaji native } performances { artist { name { main native } } } } "
        + "animethemeentries { version episodes spoiler nsfw } } } }";

    private static string RequestBody(IEnumerable<int> ids) =>
        new JsonObject { ["query"] = Query, ["variables"] = new JsonObject { ["ids"] = new JsonArray([.. ids.Select(i => (JsonNode)JsonValue.Create(i)!)]) } }.ToJsonString();

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

        foreach (var anime in animeList.EnumerateArray())
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
                    result.Add(new Entry(type, sequence, slug, 1, song, artists, null, false, false));
                    continue;
                }

                foreach (var e in entries.EnumerateArray())
                {
                    var version = e.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1;
                    result.Add(new Entry(type, sequence, slug, version, song, artists, Str(e, "episodes"),
                        Bool(e, "spoiler"), Bool(e, "nsfw")));
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
