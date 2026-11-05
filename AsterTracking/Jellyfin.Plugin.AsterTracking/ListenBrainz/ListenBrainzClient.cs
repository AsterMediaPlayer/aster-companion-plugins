using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AsterTracking.LastFm;
using Jellyfin.Plugin.AsterTracking.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AsterTracking.ListenBrainz;

/// <summary>
/// ListenBrainz (listenbrainz.org), the open alternative to Last.fm. No app to register: each person pastes their own
/// user token (listenbrainz.org/settings), which doesn't run out. Songs follow Last.fm's rule (ListenBrainz asks for the
/// same): "playing now" as they start, and a listen once they've played half way or four minutes. Listens carry the
/// MusicBrainz ids the server has, so ListenBrainz needn't guess the recording from its name.
/// </summary>
public sealed class ListenBrainzClient : ITracker, INowPlaying
{
    private static readonly Uri Api = new("https://api.listenbrainz.org/1/");

    private readonly IHttpClientFactory _http;

    public ListenBrainzClient(IHttpClientFactory http)
    {
        _http = http;
    }

    public string Service => "listenbrainz";

    public bool Available => true;

    /// <summary>Where the person finds their token.</summary>
    public static string TokenPage => "https://listenbrainz.org/settings/";

    private static string Version => typeof(ListenBrainzClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public LinkedAccount? Account(UserTracking tracking) => tracking.ListenBrainz;

    public void SetAccount(UserTracking tracking, LinkedAccount? account) => tracking.ListenBrainz = account;

    /// <summary>A pasted token's account, checked with ListenBrainz.</summary>
    public async Task<LinkedAccount> LinkAsync(string token, CancellationToken cancellation)
    {
        token = token.Trim();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Api, "validate-token"));
        JsonNode? reply;
        try
        {
            reply = await SendAsync(request, token, cancellation).ConfigureAwait(false);
        }
        catch (TrackerException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            // Not a token ListenBrainz knows.
            throw new SignedOutException(Service);
        }
        if (reply?["valid"] is not JsonValue valid || !valid.GetValue<bool>())
        {
            throw new SignedOutException(Service);
        }

        return new LinkedAccount { AccessToken = token, Name = reply["user_name"]?.GetValue<string>(), Linked = DateTimeOffset.UtcNow };
    }

    public Task<string?> BodyAsync(BaseItem item, DateTimeOffset watchedAt, CancellationToken cancellation)
    {
        if (Listen(item) is not JsonObject listen)
        {
            return Task.FromResult<string?>(null);
        }

        listen["listened_at"] = watchedAt.ToUnixTimeSeconds();
        return Task.FromResult<string?>(Submission("single", listen).ToJsonString());
    }

    public async Task<HistoryResult> SendAsync(string body, string token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Api, "submit-listens"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        await SendAsync(request, token, cancellation).ConfigureAwait(false);
        return HistoryResult.Added;
    }

    public async Task NowPlayingAsync(BaseItem item, string token, CancellationToken cancellation)
    {
        if (Listen(item) is JsonObject listen)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Api, "submit-listens"))
            {
                Content = new StringContent(Submission("playing_now", listen).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            await SendAsync(request, token, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>ListenBrainz's tokens don't run out.</summary>
    public Task<LinkedAccount?> RefreshAsync(LinkedAccount account, CancellationToken cancellation) => Task.FromResult<LinkedAccount?>(null);

    /// <summary>A song's listen (without its time), or null for anything that isn't one: Last.fm's idea of a song.</summary>
    internal static JsonObject? Listen(BaseItem item)
    {
        if (LastFmClient.Song(item) is not { } song || item is not Audio audio)
        {
            return null;
        }

        var info = new JsonObject
        {
            ["media_player"] = "Jellyfin",
            ["submission_client"] = "Aster Discovery and Tracking",
            ["submission_client_version"] = Version,
        };
        if (audio.RunTimeTicks is long ticks && ticks > 0)
        {
            info["duration_ms"] = (long)TimeSpan.FromTicks(ticks).TotalMilliseconds;
        }

        if (audio.IndexNumber is int number)
        {
            info["tracknumber"] = number;
        }

        // Jellyfin's MusicBrainz ids, as ListenBrainz names them. "MusicBrainzTrack" is the release's track, not the
        // recording, so it goes as track_mbid.
        foreach (var (provider, name) in new[]
        {
            ("MusicBrainzRecording", "recording_mbid"), ("MusicBrainzTrack", "track_mbid"),
            ("MusicBrainzAlbum", "release_mbid"), ("MusicBrainzReleaseGroup", "release_group_mbid"),
        })
        {
            if (Mbid(audio.GetProviderId(provider)) is string id)
            {
                info[name] = id;
            }
        }

        var artists = (audio.GetProviderId("MusicBrainzArtist") ?? string.Empty)
            .Split(['/', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Mbid).OfType<string>().ToList();
        if (artists.Count > 0)
        {
            info["artist_mbids"] = new JsonArray(artists.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
        }

        var metadata = new JsonObject { ["artist_name"] = song["artist"], ["track_name"] = song["track"] };
        if (song.TryGetValue("album", out var album))
        {
            metadata["release_name"] = album;
        }

        metadata["additional_info"] = info;
        return new JsonObject { ["track_metadata"] = metadata };
    }

    private static JsonObject Submission(string type, JsonObject listen) =>
        new() { ["listen_type"] = type, ["payload"] = new JsonArray(listen) };

    private static string? Mbid(string? value) => Guid.TryParse(value?.Trim(), out var id) ? id.ToString() : null;

    private async Task<JsonNode?> SendAsync(HttpRequestMessage request, string token, CancellationToken cancellation)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        request.Headers.UserAgent.ParseAdd($"AsterTracking/{Version}");
        var client = _http.CreateClient("AsterTracking.ListenBrainz");
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SignedOutException(Service);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException("ListenBrainz", response.StatusCode);
        }

        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
