using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AsterTracking.Tracking;

namespace Jellyfin.Plugin.AsterTracking.Discovery;

/// <summary>
/// TMDB (themoviedb.org) for Discover: what's like a film or show ("because you watched"), and what's trending. Each
/// server uses its admin's own key (free from TMDB for personal use; Aster has no key of its own, since an app-wide key
/// is a paid commercial licence). Without one, those rows are left out: anime still comes from AniList, and trending
/// from Simkl. Answers are kept a day (trending an hour): they change slowly and TMDB asks apps to go easy.
/// </summary>
public sealed class TmdbClient
{
    private readonly IHttpClientFactory _http;
    private readonly ConcurrentDictionary<string, (JsonNode? Reply, DateTimeOffset At)> _cache = new();

    public TmdbClient(IHttpClientFactory http)
    {
        _http = http;
    }

    private static string Key
    {
        get
        {
            // A v4 read access token ("eyJ…") or a v3 API key.
            return Plugin.Instance?.Configuration.TmdbApiKey?.Trim() ?? string.Empty;
        }
    }

    public bool Available => Key.Length > 0;

    /// <summary>TMDB's recommendations for a film ("movie") or show ("tv"), as Discover items.</summary>
    public async Task<List<DiscoverItemDto>> RecommendationsAsync(string mediaType, int id, CancellationToken cancellation)
    {
        var reply = await GetAsync($"{(mediaType == "movie" ? "movie" : "tv")}/{id}/recommendations?language=en-US&page=1", TimeSpan.FromDays(1), cancellation).ConfigureAwait(false);
        return Items(reply, mediaType == "movie" ? "movie" : "tv");
    }

    /// <summary>This week's trending films and shows.</summary>
    public async Task<List<DiscoverItemDto>> TrendingAsync(CancellationToken cancellation)
    {
        var items = new List<DiscoverItemDto>();
        for (var page = 1; page <= 2; page++)
        {
            items.AddRange(Items(await GetAsync($"trending/all/week?language=en-US&page={page}", TimeSpan.FromHours(1), cancellation).ConfigureAwait(false), null));
        }

        return items;
    }

    private static List<DiscoverItemDto> Items(JsonNode? reply, string? mediaType)
    {
        var items = new List<DiscoverItemDto>();
        foreach (var result in reply?["results"]?.AsArray() ?? [])
        {
            var type = mediaType ?? result?["media_type"]?.ToString();
            if (result is null || type is not ("movie" or "tv") || result["adult"]?.GetValue<bool>() == true)
            {
                continue;
            }

            var date = (type == "movie" ? result["release_date"] : result["first_air_date"])?.ToString();
            var item = new DiscoverItemDto
            {
                Title = (type == "movie" ? result["title"] : result["name"])?.ToString() ?? string.Empty,
                Year = date is { Length: >= 4 } && int.TryParse(date[..4], out var year) ? year : null,
                MediaType = type,
                Poster = result["poster_path"]?.ToString() is { Length: > 0 } poster ? $"https://image.tmdb.org/t/p/w342{poster}" : null,
                Overview = result["overview"]?.ToString(),
                // Animation from Japan, as Seerr's Anime filter has it.
                Anime = result["original_language"]?.ToString() == "ja" && (result["genre_ids"]?.AsArray().Any(g => g?.GetValue<int>() == 16) ?? false),
            };
            item.Ids["tmdb"] = result["id"]!.ToString();
            items.Add(item);
        }

        return items;
    }

    private async Task<JsonNode?> GetAsync(string path, TimeSpan keep, CancellationToken cancellation)
    {
        if (_cache.TryGetValue(path, out var cached) && DateTimeOffset.UtcNow - cached.At < keep)
        {
            return cached.Reply;
        }

        var key = Key;
        var v4 = key.StartsWith("eyJ", StringComparison.Ordinal);
        var address = new Uri($"https://api.themoviedb.org/3/{path}{(v4 ? string.Empty : $"&api_key={Uri.EscapeDataString(key)}")}");
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (v4)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        request.Headers.Accept.ParseAdd("application/json");
        var client = _http.CreateClient("AsterTracking.Tmdb");
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _cache[path] = (null, DateTimeOffset.UtcNow);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException("TMDB", response.StatusCode);
        }

        try
        {
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            _cache[path] = (reply, DateTimeOffset.UtcNow);
            return reply;
        }
        catch (JsonException)
        {
            throw new TrackerException("TMDB", HttpStatusCode.BadGateway);
        }
    }
}
