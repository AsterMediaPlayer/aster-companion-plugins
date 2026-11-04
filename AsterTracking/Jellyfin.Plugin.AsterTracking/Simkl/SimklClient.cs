using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.AsterTracking.Simkl;

/// <summary>
/// Simkl (simkl.com), as the Aster apps talk to it: OAuth 2 sign-in for TVs and devices ("Auth V2", a code entered at
/// simkl.com/pin; a year's token and a refresh token), and the watched history. Every request names the app in its
/// address (<c>client_id</c>, <c>app-name</c>, <c>app-version</c>) and carries a User-Agent; without them Simkl
/// answers 403.
/// </summary>
public sealed class SimklClient
{
    /// <summary>Aster's own Simkl app ("TV, devices &amp; command line": device sign-in, no secret).</summary>
    public const string BuiltInClientId = "ebd388547e0ade13dad9e3196e820dcbc4b86ebb47d84d838b142a83b31bd71d";

    private readonly IHttpClientFactory _http;

    public SimklClient(IHttpClientFactory http)
    {
        _http = http;
    }

    private static string ClientId
    {
        get
        {
            var own = Plugin.Instance?.Configuration.SimklClientId?.Trim();
            return string.IsNullOrEmpty(own) ? BuiltInClientId : own;
        }
    }

    private static string Version => typeof(SimklClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Starts a sign-in: the code to enter and where.</summary>
    public async Task<DeviceCode> StartSignInAsync(CancellationToken cancellation)
    {
        // Reading and writing the person's lists: without write a token is read-only and history quietly fails.
        var (status, reply) = await OAuthAsync("/oauth2/device", new() { ["client_id"] = ClientId, ["scope"] = "media:read media:write" }, cancellation).ConfigureAwait(false);
        if (status != HttpStatusCode.OK || reply is null)
        {
            throw new SimklException(status);
        }

        return new DeviceCode
        {
            UserCode = Text(reply, "user_code") ?? throw new SimklException(status),
            DeviceCodeValue = Text(reply, "device_code") ?? throw new SimklException(status),
            VerificationUrl = Text(reply, "verification_uri") ?? "https://simkl.com/pin",
            CompleteUrl = Text(reply, "verification_uri_complete"),
            Interval = Number(reply, "interval") ?? 5,
            Expires = DateTimeOffset.UtcNow.AddSeconds(Number(reply, "expires_in") ?? 900),
        };
    }

    /// <summary>The tokens once the code's been entered; null while it hasn't. Throws <see cref="SimklSignedOutException"/> when it was refused or ran out.</summary>
    public async Task<SimklTokens?> PollAsync(string deviceCode, CancellationToken cancellation)
    {
        var (status, reply) = await OAuthAsync("/oauth2/token", new()
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
            ["client_id"] = ClientId,
        }, cancellation).ConfigureAwait(false);
        if (status == HttpStatusCode.OK && reply is not null)
        {
            return Tokens(reply);
        }

        return (reply is null ? null : Text(reply, "error")) switch
        {
            "authorization_pending" or "slow_down" => null,
            "access_denied" or "expired_token" => throw new SimklSignedOutException(),
            _ => throw new SimklException(status),
        };
    }

    public async Task<SimklTokens> RefreshAsync(string refreshToken, CancellationToken cancellation)
    {
        var (status, reply) = await OAuthAsync("/oauth2/token", new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        }, cancellation).ConfigureAwait(false);
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new SimklSignedOutException();
        }

        if (status != HttpStatusCode.OK || reply is null)
        {
            throw new SimklException(status);
        }

        var tokens = Tokens(reply);
        return tokens with { Refresh = tokens.Refresh ?? refreshToken };
    }

    /// <summary>The account's name.</summary>
    public async Task<string> UserNameAsync(string token, CancellationToken cancellation)
    {
        var (status, reply) = await RequestAsync("/users/settings", HttpMethod.Post, null, token, cancellation).ConfigureAwait(false);
        Check(status);
        return reply?["user"]?["name"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>Adds to the history; true when Simkl added something (false: it was there already, or not found).</summary>
    public async Task<HistoryResult> AddToHistoryAsync(string body, string token, CancellationToken cancellation)
    {
        var (status, reply) = await RequestAsync("/sync/history", HttpMethod.Post, body, token, cancellation).ConfigureAwait(false);
        Check(status);
        var added = reply?["added"];
        var count = new[] { "movies", "shows", "episodes" }.Sum(key => added?[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0);
        if (count > 0)
        {
            return HistoryResult.Added;
        }

        return reply?["not_found"] is JsonObject missing && missing.Any(kind => kind.Value is JsonArray { Count: > 0 })
            ? HistoryResult.NotFound
            : HistoryResult.Unchanged;
    }

    private static void Check(HttpStatusCode status)
    {
        if (status == HttpStatusCode.Unauthorized)
        {
            throw new SimklSignedOutException();
        }

        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created))
        {
            throw new SimklException(status);
        }
    }

    private static Uri Address(string path) => new(
        $"https://api.simkl.com{path}?client_id={Uri.EscapeDataString(ClientId)}&app-name=aster&app-version={Version}");

    private HttpClient Client()
    {
        var client = _http.CreateClient("AsterTracking.Simkl");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private static void Name(HttpRequestMessage request) =>
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Aster", Version));

    private async Task<(HttpStatusCode Status, JsonNode? Reply)> RequestAsync(string path, HttpMethod method, string? body, string? token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, Address(path));
        Name(request);
        request.Headers.Add("simkl-api-key", ClientId);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json");
        return await SendAsync(request, cancellation).ConfigureAwait(false);
    }

    /// <summary>The sign-in's requests, form-encoded as Auth V2 takes them.</summary>
    private async Task<(HttpStatusCode Status, JsonNode? Reply)> OAuthAsync(string path, Dictionary<string, string> fields, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Address(path));
        Name(request);
        request.Content = new FormUrlEncodedContent(fields);
        return await SendAsync(request, cancellation).ConfigureAwait(false);
    }

    private async Task<(HttpStatusCode Status, JsonNode? Reply)> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        JsonNode? reply = null;
        try
        {
            reply = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Not JSON (an error page): the status says enough.
        }

        return (response.StatusCode, reply);
    }

    private static SimklTokens Tokens(JsonNode reply) => new(
        Text(reply, "access_token") ?? throw new SimklException(HttpStatusCode.OK),
        Text(reply, "refresh_token"),
        Number(reply, "expires_in") is double seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null);

    private static string? Text(JsonNode node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;

    private static double? Number(JsonNode node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}

/// <summary>A sign-in under way: the code to enter and where.</summary>
public sealed class DeviceCode
{
    public string UserCode { get; init; } = string.Empty;

    /// <summary>What the server polls with; never handed to the apps.</summary>
    public string DeviceCodeValue { get; init; } = string.Empty;

    public string VerificationUrl { get; init; } = string.Empty;

    /// <summary>The address with the code already in it (for a QR code).</summary>
    public string? CompleteUrl { get; init; }

    public double Interval { get; init; }

    public DateTimeOffset Expires { get; init; }
}

public sealed record SimklTokens(string Access, string? Refresh, DateTimeOffset? Expires);

public enum HistoryResult
{
    Added,
    Unchanged,
    NotFound,
}

/// <summary>Simkl answered with something other than success.</summary>
public class SimklException : Exception
{
    public SimklException(HttpStatusCode status)
        : base($"Simkl answered {(int)status}")
    {
        Status = status;
    }

    public HttpStatusCode Status { get; }

    /// <summary>Worth retrying later: Simkl busy or down.</summary>
    public bool Transient => Status is HttpStatusCode.TooManyRequests || (int)Status >= 500 || Status == 0;
}

/// <summary>The account's token is no longer good (or a sign-in was refused): it needs linking again.</summary>
public sealed class SimklSignedOutException : Exception
{
    public SimklSignedOutException()
        : base("Simkl signed out")
    {
    }
}
