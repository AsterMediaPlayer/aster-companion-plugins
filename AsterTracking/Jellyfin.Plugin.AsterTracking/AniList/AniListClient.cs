using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AsterTracking.Tracking;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AsterTracking.AniList;

/// <summary>
/// AniList (anilist.co), through its GraphQL API, as the Aster apps use it. Signing in is AniList's implicit grant with
/// its own "pin" page as the redirect: AniList shows the person a year-long token, which they paste into the
/// dashboard. An episode watched sets the season's progress to it (never lowered) and the entry Watching, or
/// Completed at the last episode.
/// </summary>
public sealed class AniListClient : ITracker
{
    /// <summary>
    /// Aster's AniList app for servers, whose redirect is https://anilist.co/api/v2/oauth/pin. (The apps' own, 55643,
    /// redirects to aster://, which only an app can receive.) Blank until it's registered: a server can set its own.
    /// </summary>
    public const string BuiltInClientId = "";

    private readonly IHttpClientFactory _http;
    private readonly AnimeIds _anime;

    public AniListClient(IHttpClientFactory http, AnimeIds anime)
    {
        _http = http;
        _anime = anime;
    }

    public string Service => "anilist";

    public bool Available => ClientId.Length > 0;

    private static string ClientId
    {
        get
        {
            var own = Plugin.Instance?.Configuration.AniListClientId?.Trim();
            return string.IsNullOrEmpty(own) ? BuiltInClientId : own;
        }
    }

    /// <summary>Where the person signs in; AniList then shows the token to paste.</summary>
    public static string SignInUrl => $"https://anilist.co/api/v2/oauth/authorize?client_id={Uri.EscapeDataString(ClientId)}&response_type=token";

    public LinkedAccount? Account(UserTracking tracking) => tracking.AniList;

    public void SetAccount(UserTracking tracking, LinkedAccount? account) => tracking.AniList = account;

    /// <summary>A pasted token's account, checked with AniList. Its expiry is read from the token (a JWT) when it has one.</summary>
    public async Task<LinkedAccount> LinkAsync(string token, CancellationToken cancellation)
    {
        token = token.Trim();
        var reply = await RequestAsync("{Viewer{name}}", new JsonObject(), token, cancellation).ConfigureAwait(false);
        var name = reply?["data"]?["Viewer"]?["name"]?.GetValue<string>() ?? throw new SignedOutException(Service);
        return new LinkedAccount
        {
            AccessToken = token,
            Expires = Expiry(token) ?? DateTimeOffset.UtcNow.AddDays(365),
            Name = name,
            Linked = DateTimeOffset.UtcNow,
        };
    }

    public async Task<string?> BodyAsync(BaseItem item, DateTimeOffset watchedAt, CancellationToken cancellation)
    {
        var found = await _anime.ForAsync(item, cancellation).ConfigureAwait(false);
        return found is { Entry.AniList: int id } ? new JsonObject { ["id"] = id, ["episode"] = found.Value.Episode }.ToJsonString() : null;
    }

    public async Task<HistoryResult> SendAsync(string body, string token, CancellationToken cancellation)
    {
        var request = JsonNode.Parse(body)!;
        var mediaId = request["id"]!.GetValue<int>();
        var number = request["episode"]!.GetValue<int>();

        var lookup = await RequestAsync(
            "query($id:Int){Media(id:$id,type:ANIME){episodes mediaListEntry{progress status}}}",
            new JsonObject { ["id"] = mediaId }, token, cancellation).ConfigureAwait(false);
        var media = lookup?["data"]?["Media"];
        if (media is null)
        {
            return HistoryResult.NotFound;
        }

        var entry = media["mediaListEntry"];
        var progress = entry?["progress"] is JsonValue p && p.TryGetValue<int>(out var pr) ? pr : 0;
        var status = entry?["status"] is JsonValue s && s.TryGetValue<string>(out var st) ? st : null;
        if (number <= progress && status is not (null or "PLANNING"))
        {
            return HistoryResult.Unchanged;
        }

        var total = media["episodes"] is JsonValue e && e.TryGetValue<int>(out var t) ? t : (int?)null;
        var newProgress = Math.Max(number, progress);
        var done = total is int all && newProgress >= all;

        // A rewatch stays a rewatch until it's done.
        var newStatus = done ? "COMPLETED" : status == "REPEATING" ? "REPEATING" : "CURRENT";
        await RequestAsync(
            "mutation($id:Int,$progress:Int,$status:MediaListStatus){SaveMediaListEntry(mediaId:$id,progress:$progress,status:$status){id}}",
            new JsonObject { ["id"] = mediaId, ["progress"] = newProgress, ["status"] = newStatus }, token, cancellation).ConfigureAwait(false);
        return HistoryResult.Added;
    }

    /// <summary>AniList's tokens can't be refreshed: they last a year, then the person links again.</summary>
    public Task<LinkedAccount?> RefreshAsync(LinkedAccount account, CancellationToken cancellation) => Task.FromResult<LinkedAccount?>(null);

    private async Task<JsonNode?> RequestAsync(string query, JsonObject variables, string token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://graphql.anilist.co"));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new("Bearer", token);
        request.Content = new StringContent(new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString(), Encoding.UTF8, "application/json");
        var client = _http.CreateClient("AsterTracking.AniList");
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new SignedOutException(Service);
        }

        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest && text.Contains("Invalid token", StringComparison.OrdinalIgnoreCase))
        {
            throw new SignedOutException(Service);
        }

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            // GraphQL's "not found" for a Media id AniList doesn't have.
            return null;
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new TrackerException("AniList", response.StatusCode);
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new TrackerException("AniList", HttpStatusCode.BadGateway);
        }
    }

    /// <summary>A JWT's expiry ("exp"), when the token is one.</summary>
    private static DateTimeOffset? Expiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            var exp = JsonNode.Parse(Convert.FromBase64String(payload))?["exp"];
            return exp is JsonValue v && v.TryGetValue<double>(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
