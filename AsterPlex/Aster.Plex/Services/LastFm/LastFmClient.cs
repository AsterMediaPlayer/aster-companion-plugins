using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aster.Plex.Tracking;

namespace Aster.Plex.LastFm;

/// <summary>
/// Last.fm, through its 2.0 API. Signing in is its desktop flow, which needs no redirect: the server gets a token,
/// the person approves Aster at last.fm with it, and the server then trades it for a session key that never runs out.
/// Every request is signed with the app's shared secret. A song gets "now playing" as it starts, and a scrobble once
/// it's played half way or four minutes (Last.fm's rule), when it's longer than 30 seconds.
/// </summary>
public sealed class LastFmClient : ITracker, INowPlaying
{
    /// <summary>Aster's Last.fm API account ("Aster Media Plugin"). A server can set its own.</summary>
    public static string BuiltInApiKey => BuiltInKeys.LastFmApiKey;

    public static string BuiltInSharedSecret => BuiltInKeys.LastFmSharedSecret;

    private static readonly Uri Api = new("https://ws.audioscrobbler.com/2.0/");

    private readonly IHttpClientFactory _http;

    public LastFmClient(IHttpClientFactory http)
    {
        _http = http;
    }

    public string Service => "lastfm";

    public bool Available => ApiKey.Length > 0 && Secret.Length > 0;

    private static string ApiKey => Own(CompanionSettings.Current.LastFmApiKey) ?? BuiltInApiKey;

    private static string Secret => Own(CompanionSettings.Current.LastFmSharedSecret) ?? BuiltInSharedSecret;

    public LinkedAccount? Account(UserTracking tracking) => tracking.LastFm;

    public void SetAccount(UserTracking tracking, LinkedAccount? account) => tracking.LastFm = account;

    /// <summary>Starts a sign-in: the token, and the page where the person approves it (good for an hour).</summary>
    public async Task<(string Token, string Url)> StartSignInAsync(CancellationToken cancellation)
    {
        var reply = await CallAsync(new() { ["method"] = "auth.getToken" }, null, cancellation).ConfigureAwait(false);
        var token = reply?["token"]?.GetValue<string>() ?? throw new TrackerException("Last.fm", HttpStatusCode.BadGateway);
        return (token, $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(ApiKey)}&token={Uri.EscapeDataString(token)}");
    }

    /// <summary>The account once the token's been approved; null while it hasn't. Throws <see cref="SignedOutException"/> when it ran out.</summary>
    public async Task<LinkedAccount?> PollAsync(string token, CancellationToken cancellation)
    {
        JsonNode? reply;
        try
        {
            reply = await CallAsync(new() { ["method"] = "auth.getSession", ["token"] = token }, null, cancellation).ConfigureAwait(false);
        }
        catch (LastFmError error) when (error.Code == 14)
        {
            // Not approved yet.
            return null;
        }

        var session = reply?["session"];
        return new LinkedAccount
        {
            AccessToken = session?["key"]?.GetValue<string>() ?? throw new TrackerException("Last.fm", HttpStatusCode.BadGateway),
            Name = session?["name"]?.GetValue<string>(),
            Linked = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>A song's scrobble (its fields, with when it started); null for anything that isn't a song Last.fm takes.</summary>
    public Task<string?> BodyAsync(PlayedItem item, DateTimeOffset watchedAt, CancellationToken cancellation)
    {
        var fields = Song(item);
        if (fields is null)
        {
            return Task.FromResult<string?>(null);
        }

        fields["timestamp"] = watchedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return Task.FromResult<string?>(JsonSerializer.Serialize(fields));
    }

    public async Task<HistoryResult> SendAsync(string body, string token, CancellationToken cancellation)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        fields["method"] = "track.scrobble";
        var reply = await CallAsync(fields, token, cancellation).ConfigureAwait(false);
        var counts = reply?["scrobbles"]?["@attr"];
        var accepted = counts?["accepted"] is JsonValue a && (a.TryGetValue<int>(out var n) || int.TryParse(a.ToString(), out n)) ? n : 0;
        return accepted > 0 ? HistoryResult.Added : HistoryResult.NotFound;
    }

    public async Task NowPlayingAsync(PlayedItem item, string token, CancellationToken cancellation)
    {
        if (Song(item) is { } fields)
        {
            fields["method"] = "track.updateNowPlaying";
            await CallAsync(fields, token, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Last.fm's session keys never run out.</summary>
    public Task<LinkedAccount?> RefreshAsync(LinkedAccount account, CancellationToken cancellation) => Task.FromResult<LinkedAccount?>(null);

    /// <summary>A song's fields as Last.fm names them, or null without an artist and title. Audiobooks aren't songs.</summary>
    internal static Dictionary<string, string>? Song(PlayedItem item)
    {
        if (item.Kind != PlayedKind.Song || string.IsNullOrWhiteSpace(item.Name))
        {
            return null;
        }

        var artist = item.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? item.AlbumArtists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (artist is null)
        {
            return null;
        }

        var fields = new Dictionary<string, string> { ["artist"] = artist, ["track"] = item.Name };
        if (!string.IsNullOrWhiteSpace(item.Album))
        {
            fields["album"] = item.Album;
        }

        if (item.AlbumArtists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) is string albumArtist && albumArtist != artist)
        {
            fields["albumArtist"] = albumArtist;
        }

        if (item.RunTimeTicks is long ticks && ticks > 0)
        {
            fields["duration"] = ((long)TimeSpan.FromTicks(ticks).TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        if (item.IndexNumber is int number)
        {
            fields["trackNumber"] = number.ToString(CultureInfo.InvariantCulture);
        }

        return fields;
    }

    /// <summary>
    /// The request's signature: every field but <c>format</c>, sorted by name, each name then value, followed by the
    /// shared secret, as an MD5 in hex.
    /// </summary>
    internal static string Signature(IDictionary<string, string> fields, string secret)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in fields.Where(f => f.Key is not ("format" or "callback")).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            text.Append(name).Append(value);
        }

        text.Append(secret);
#pragma warning disable CA5351 // Last.fm's signatures are MD5.
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
#pragma warning restore CA5351
    }

    private async Task<JsonNode?> CallAsync(Dictionary<string, string> fields, string? session, CancellationToken cancellation)
    {
        fields["api_key"] = ApiKey;
        if (session is not null)
        {
            fields["sk"] = session;
        }

        fields["api_sig"] = Signature(fields, Secret);
        fields["format"] = "json";

        using var request = new HttpRequestMessage(HttpMethod.Post, Api) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.UserAgent.ParseAdd($"AsterTracking/{typeof(LastFmClient).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        var client = _http.CreateClient("AsterTracking.LastFm");
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
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

        // Last.fm's errors come as { "error": n, "message": "…" }, with a 4xx status or a 200.
        if (reply?["error"] is JsonValue code && code.TryGetValue<int>(out var error))
        {
            throw error switch
            {
                // Invalid session key; the token ran out or was never approved.
                9 or 4 or 15 => new SignedOutException(Service),
                // Service offline, temporarily unavailable, rate limit.
                11 or 16 or 29 => new TrackerException("Last.fm", HttpStatusCode.ServiceUnavailable),
                _ => new LastFmError(error, reply?["message"]?.ToString()),
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException("Last.fm", response.StatusCode);
        }

        return reply;
    }

    private static string? Own(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>One of Last.fm's numbered errors.</summary>
    private sealed class LastFmError : TrackerException
    {
        public LastFmError(int code, string? message)
            : base("Last.fm", HttpStatusCode.BadRequest)
        {
            Code = code;
            Detail = message;
        }

        public int Code { get; }

        public string? Detail { get; }

        public override string Message => $"Last.fm error {Code}: {Detail}";
    }
}
