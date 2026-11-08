using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AsterTracking.Tracking;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AsterTracking.MyAnimeList;

/// <summary>
/// MyAnimeList (myanimelist.net), through its v2 API, as the Aster apps use it. Signing in is OAuth 2 with PKCE (plain,
/// the only kind it takes; no secret for an "other" app): MyAnimeList sends the browser to a page on
/// astermediaplayer.com that shows the code to paste, and the server trades it with the verifier it kept, so the code
/// is no use to anyone else. Tokens last a month and are refreshed. An episode watched sets the season's watched
/// count to it (never lowered) and the entry Watching, or Completed at the last episode.
/// </summary>
public sealed class MyAnimeListClient : ITracker
{
    /// <summary>
    /// Aster's MyAnimeList app for servers (type "other", no secret), with <see cref="Redirect"/> as its redirect. A
    /// server can set its own.
    /// </summary>
    public static string BuiltInClientId => BuiltInKeys.MyAnimeListClientId;

    /// <summary>The page that shows the code to paste.</summary>
    public const string Redirect = "https://astermediaplayer.com/link/myanimelist/";

    private readonly IHttpClientFactory _http;
    private readonly AnimeIds _anime;

    public MyAnimeListClient(IHttpClientFactory http, AnimeIds anime)
    {
        _http = http;
        _anime = anime;
    }

    public string Service => "myanimelist";

    public bool Available => ClientId.Length > 0;

    private static string ClientId
    {
        get
        {
            var own = Plugin.Instance?.Configuration.MyAnimeListClientId?.Trim();
            return string.IsNullOrEmpty(own) ? BuiltInClientId : own;
        }
    }

    public LinkedAccount? Account(UserTracking tracking) => tracking.MyAnimeList;

    public void SetAccount(UserTracking tracking, LinkedAccount? account) => tracking.MyAnimeList = account;

    /// <summary>A sign-in's address and the verifier its code is traded with.</summary>
    public static (string Url, string Verifier) StartSignIn()
    {
        // 43 to 128 unreserved characters; with "plain" it's also the challenge.
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var verifier = new string(Enumerable.Range(0, 96).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var url = "https://myanimelist.net/v1/oauth2/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(ClientId)}&code_challenge={verifier}&code_challenge_method=plain"
            + $"&redirect_uri={Uri.EscapeDataString(Redirect)}&state={Guid.NewGuid():N}";
        return (url, verifier);
    }

    /// <summary>The account for a pasted code.</summary>
    public async Task<LinkedAccount> LinkAsync(string code, string verifier, CancellationToken cancellation)
    {
        var account = await TokensAsync(new()
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code.Trim(),
            ["code_verifier"] = verifier,
            ["redirect_uri"] = Redirect,
        }, cancellation).ConfigureAwait(false);
        var me = await ApiAsync("/users/@me", HttpMethod.Get, null, account.AccessToken, cancellation).ConfigureAwait(false);
        account.Name = me?["name"]?.GetValue<string>();
        account.Linked = DateTimeOffset.UtcNow;
        return account;
    }

    public async Task<string?> BodyAsync(BaseItem item, DateTimeOffset watchedAt, CancellationToken cancellation)
    {
        var found = await _anime.ForAsync(item, cancellation).ConfigureAwait(false);
        return found is { Entry.MyAnimeList: int id } ? new JsonObject { ["id"] = id, ["episode"] = found.Value.Episode }.ToJsonString() : null;
    }

    public async Task<HistoryResult> SendAsync(string body, string token, CancellationToken cancellation)
    {
        var request = JsonNode.Parse(body)!;
        var id = request["id"]!.GetValue<int>();
        var number = request["episode"]!.GetValue<int>();

        var reply = await ApiAsync($"/anime/{id}?fields=num_episodes,my_list_status", HttpMethod.Get, null, token, cancellation).ConfigureAwait(false);
        if (reply is null)
        {
            return HistoryResult.NotFound;
        }

        var status = reply["my_list_status"];
        var watched = status?["num_episodes_watched"] is JsonValue w && w.TryGetValue<int>(out var n) ? n : 0;
        var listed = status?["status"] is JsonValue s && s.TryGetValue<string>(out var st) ? st : null;
        if (number <= watched && listed is not (null or "plan_to_watch"))
        {
            return HistoryResult.Unchanged;
        }

        var newWatched = Math.Max(number, watched);
        var total = reply["num_episodes"] is JsonValue t && t.TryGetValue<int>(out var all) && all > 0 ? all : (int?)null;
        var done = total is int count && newWatched >= count;
        var form = new Dictionary<string, string>
        {
            ["num_watched_episodes"] = newWatched.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["status"] = done ? "completed" : "watching",
        };

        // A rewatch is a completed entry being rewatched, until it's done.
        if (status?["is_rewatching"] is JsonValue r && r.TryGetValue<bool>(out var rewatching) && rewatching && !done)
        {
            form["status"] = "completed";
            form["is_rewatching"] = "true";
        }

        await ApiAsync($"/anime/{id}/my_list_status", HttpMethod.Patch, form, token, cancellation).ConfigureAwait(false);
        return HistoryResult.Added;
    }

    /// <summary>
    /// Puts the title on the person's Plan to Watch, or takes it off. An entry on another list (Watching, Completed,
    /// …) is never changed or deleted: taking off only deletes a Plan to Watch entry.
    /// </summary>
    public async Task<WatchlistOutcome> SetPlanToWatchAsync(int id, bool add, string token, CancellationToken cancellation)
    {
        var reply = await ApiAsync($"/anime/{id}?fields=my_list_status", HttpMethod.Get, null, token, cancellation).ConfigureAwait(false);
        if (reply is null)
        {
            return new(WatchlistChange.NotFound);
        }

        var listed = reply["my_list_status"]?["status"] is JsonValue s && s.TryGetValue<string>(out var st) ? st : null;
        if (add)
        {
            if (listed == "plan_to_watch")
            {
                return new(WatchlistChange.On);
            }

            if (listed is not null)
            {
                return new(WatchlistChange.Elsewhere, ListName(listed));
            }

            await ApiAsync($"/anime/{id}/my_list_status", HttpMethod.Patch, new() { ["status"] = "plan_to_watch" }, token, cancellation).ConfigureAwait(false);
            return new(WatchlistChange.On);
        }

        if (listed != "plan_to_watch")
        {
            return new(WatchlistChange.Off);
        }

        await ApiAsync($"/anime/{id}/my_list_status", HttpMethod.Delete, null, token, cancellation).ConfigureAwait(false);
        return new(WatchlistChange.Off);
    }

    /// <summary>MyAnimeList's list statuses as the site names them.</summary>
    private static string ListName(string status) => status switch
    {
        "watching" => "Watching",
        "completed" => "Completed",
        "on_hold" => "On Hold",
        "dropped" => "Dropped",
        _ => status,
    };

    public async Task<LinkedAccount?> RefreshAsync(LinkedAccount account, CancellationToken cancellation)
    {
        if (account.RefreshToken is not string refresh)
        {
            return null;
        }

        var fresh = await TokensAsync(new()
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
        }, cancellation).ConfigureAwait(false);
        fresh.Name = account.Name;
        fresh.Linked = account.Linked;
        fresh.Scrobble = account.Scrobble;
        return fresh;
    }

    private async Task<LinkedAccount> TokensAsync(Dictionary<string, string> form, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://myanimelist.net/v1/oauth2/token"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new SignedOutException(Service);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new TrackerException("MyAnimeList", response.StatusCode);
        }

        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        return new LinkedAccount
        {
            AccessToken = reply?["access_token"]?.GetValue<string>() ?? throw new TrackerException("MyAnimeList", HttpStatusCode.BadGateway),
            RefreshToken = reply?["refresh_token"]?.GetValue<string>(),
            Expires = reply?["expires_in"] is JsonValue e && e.TryGetValue<double>(out var seconds) ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null,
        };
    }

    /// <summary>A GET for the discovery lists (<c>/users/@me/animelist</c>, <c>/anime/ranking</c>, …).</summary>
    internal Task<JsonNode?> GetAsync(string pathAndQuery, string token, CancellationToken cancellation) =>
        ApiAsync(pathAndQuery, HttpMethod.Get, null, token, cancellation);

    private async Task<JsonNode?> ApiAsync(string path, HttpMethod method, Dictionary<string, string>? form, string token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, new Uri("https://api.myanimelist.net/v2" + path));
        request.Headers.Authorization = new("Bearer", token);
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new SignedOutException(Service);
            case HttpStatusCode.NotFound:
                return null;
            case HttpStatusCode.OK:
                break;
            default:
                throw new TrackerException("MyAnimeList", response.StatusCode);
        }

        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            // A delete answers with no body.
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new TrackerException("MyAnimeList", HttpStatusCode.BadGateway);
        }
    }

    private HttpClient Client()
    {
        var client = _http.CreateClient("AsterTracking.MyAnimeList");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }
}
