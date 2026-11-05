using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AsterTracking.Seerr;

/// <summary>
/// Seerr for the Aster apps, through the server: the apps never sign in to Seerr. The plugin holds Seerr's address
/// and admin API key, and calls Seerr as the signed-in person (Seerr's <c>X-Api-User</c>), so Seerr applies that
/// person's own permissions, quotas and auto-approval. Each Jellyfin user is matched to the Seerr user linked to them,
/// and someone without one is imported from Jellyfin on first use, with Seerr's defaults for new users. Only Seerr's
/// browsing and request routes pass; its settings and user management don't.
/// </summary>
public sealed partial class SeerrBridge
{
    private static readonly TimeSpan KeepUsers = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<SeerrBridge> _log;
    private readonly ConcurrentDictionary<Guid, (int SeerrId, DateTimeOffset At)> _users = new();
    private readonly SemaphoreSlim _importing = new(1, 1);

    public SeerrBridge(IHttpClientFactory http, ILogger<SeerrBridge> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Seerr's address as the server reaches it, and its API key; null when either is missing.</summary>
    public static (Uri Url, string Key)? Connection
    {
        get
        {
            var config = Plugin.Instance?.Configuration;
            var url = Api.AsterTrackingController.SeerrAddress(config?.SeerrUrl);
            var key = config?.SeerrApiKey?.Trim();
            return config?.SeerrEnabled == true && url is not null && !string.IsNullOrEmpty(key) ? (new Uri(url), key) : null;
        }
    }

    /// <summary>Whether a call may pass: Seerr's browsing, title, search and request routes only.</summary>
    public static bool Allowed(string method, string path) => method switch
    {
        "GET" => ReadRoutes().IsMatch(path),
        "POST" => PostRoutes().IsMatch(path),
        "PUT" => PutRoutes().IsMatch(path),
        "DELETE" => DeleteRoutes().IsMatch(path),
        _ => false,
    };

    [GeneratedRegex(@"^(auth/me|(movie|tv)/\d+(/[a-z0-9/_-]*)?|person/\d+(/[a-z_]*)?|collection/\d+|discover/[a-z0-9/_-]+|search(/[a-z]+)?|request(/count|/\d+)?|media|genres/(movie|tv)|studio/\d+|network/\d+|keyword/\d+|settings/discover|service/(radarr|sonarr)(/\d+)?|user/\d+/(quota|requests|watchlist)|watchproviders/(movies|tv|regions)|languages|regions)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReadRoutes();

    [GeneratedRegex(@"^request(/\d+/(approve|decline|retry))?$", RegexOptions.IgnoreCase)]
    private static partial Regex PostRoutes();

    [GeneratedRegex(@"^request/\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex PutRoutes();

    [GeneratedRegex(@"^request/\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteRoutes();

    /// <summary>Passes one call to Seerr as the person; Seerr's own status and body come back.</summary>
    public async Task<(HttpStatusCode Status, string Body, string ContentType)> SendAsync(Guid userId, string userName, HttpMethod method, string path, string query, string? body, CancellationToken cancellation)
    {
        if (Connection is not { } seerr)
        {
            return (HttpStatusCode.ServiceUnavailable, "{\"message\":\"Requests are off on this server.\"}", "application/json");
        }

        var seerrUser = await SeerrUserAsync(seerr, userId, userName, cancellation).ConfigureAwait(false);
        if (seerrUser is null)
        {
            return (HttpStatusCode.Forbidden, "{\"message\":\"No Seerr account for this person, and Seerr didn't make one.\"}", "application/json");
        }

        // Seerr's own path rules: user/{id}/… only for the person themselves.
        var own = UserRoute().Match(path);
        if (own.Success && own.Groups[1].Value != seerrUser.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            return (HttpStatusCode.Forbidden, "{\"message\":\"Only your own.\"}", "application/json");
        }

        using var request = Request(seerr, method, path + query, seerrUser);
        if (body is not null && method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

        // What the server's admin keeps out of Discover: out of Seerr's lists too, and its title pages closed.
        if (method == HttpMethod.Get && response.IsSuccessStatusCode)
        {
            var filtered = Discovery.ContentFilter.For(userId).Filter(path, text);
            if (filtered is null)
            {
                return (HttpStatusCode.NotFound, "{\"message\":\"Not available on this server.\"}", "application/json");
            }

            text = filtered;
        }

        return (response.StatusCode, text, response.Content.Headers.ContentType?.MediaType ?? "application/json");
    }

    [GeneratedRegex(@"^user/(\d+)/")]
    private static partial Regex UserRoute();

    /// <summary>
    /// The Seerr user for a Jellyfin user: the one Seerr linked to them, else one imported from Jellyfin now. Known
    /// ones are remembered 10 minutes.
    /// </summary>
    private async Task<int?> SeerrUserAsync((Uri Url, string Key) seerr, Guid userId, string userName, CancellationToken cancellation)
    {
        if (_users.TryGetValue(userId, out var known) && DateTimeOffset.UtcNow - known.At < KeepUsers)
        {
            return known.SeerrId;
        }

        var jellyfinId = userId.ToString("N");
        if (await FindAsync(seerr, jellyfinId, cancellation).ConfigureAwait(false) is int found)
        {
            _users[userId] = (found, DateTimeOffset.UtcNow);
            return found;
        }

        // One import at a time, so two first requests don't make two accounts.
        await _importing.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (await FindAsync(seerr, jellyfinId, cancellation).ConfigureAwait(false) is int meanwhile)
            {
                _users[userId] = (meanwhile, DateTimeOffset.UtcNow);
                return meanwhile;
            }

            using var request = Request(seerr, HttpMethod.Post, "user/import-from-jellyfin", null);
            request.Content = new StringContent(new JsonObject { ["jellyfinUserIds"] = new JsonArray(jellyfinId) }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Aster Tracking: Seerr wouldn't import {User} from Jellyfin: {Status} {Body}", userName, (int)response.StatusCode, text.Length > 200 ? text[..200] : text);
                return null;
            }

            _log.LogInformation("Aster Tracking: imported {User} into Seerr from Jellyfin", userName);
            if (await FindAsync(seerr, jellyfinId, cancellation).ConfigureAwait(false) is int made)
            {
                _users[userId] = (made, DateTimeOffset.UtcNow);
                return made;
            }

            return null;
        }
        finally
        {
            _importing.Release();
        }
    }

    private async Task<int?> FindAsync((Uri Url, string Key) seerr, string jellyfinId, CancellationToken cancellation)
    {
        for (var skip = 0; skip < 5000; skip += 100)
        {
            using var request = Request(seerr, HttpMethod.Get, $"user?take=100&skip={skip}", null);
            using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Seerr answered {(int)response.StatusCode} listing users");
            }

            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            var results = reply?["results"]?.AsArray() ?? [];
            foreach (var user in results)
            {
                if (string.Equals(user?["jellyfinUserId"]?.ToString()?.Replace("-", string.Empty, StringComparison.Ordinal), jellyfinId, StringComparison.OrdinalIgnoreCase))
                {
                    return user!["id"]!.GetValue<int>();
                }
            }

            if (results.Count < 100)
            {
                break;
            }
        }

        return null;
    }

    private static HttpRequestMessage Request((Uri Url, string Key) seerr, HttpMethod method, string pathAndQuery, int? asUser)
    {
        var request = new HttpRequestMessage(method, new Uri(seerr.Url.AbsoluteUri.TrimEnd('/') + "/api/v1/" + pathAndQuery));
        request.Headers.Add("X-Api-Key", seerr.Key);
        if (asUser is int id)
        {
            request.Headers.Add("X-Api-User", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private HttpClient Client()
    {
        var client = _http.CreateClient("AsterTracking.Seerr");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}
