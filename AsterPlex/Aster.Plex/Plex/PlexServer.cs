using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Aster.Plex.Plex;

/// <summary>
/// The Plex server and plex.tv, asked as the server's owner (<see cref="CompanionSettings.PlexToken"/>). Plex answers
/// JSON when asked for it, inside a <c>MediaContainer</c>; plex.tv's older calls answer only XML.
/// </summary>
public sealed class PlexServer
{
    /// <summary>Who the container says it is to Plex, as every Plex app must.</summary>
    public const string Product = "Aster for Plex";

    private static readonly Uri PlexTv = new("https://plex.tv/");

    private readonly IHttpClientFactory _http;

    public PlexServer(IHttpClientFactory http)
    {
        _http = http;
    }

    /// <summary>A stable id for this container, which Plex lists it by (kept in the data folder).</summary>
    public static string ClientId { get; } = LoadClientId();

    public static bool Configured =>
        !string.IsNullOrWhiteSpace(CompanionSettings.Current.PlexUrl) && !string.IsNullOrWhiteSpace(CompanionSettings.Current.PlexToken);

    /// <summary>The server's address, without a trailing slash.</summary>
    public static string BaseUrl => CompanionSettings.Current.PlexUrl.TrimEnd('/');

    /// <summary>A GET on the server as its owner; its <c>MediaContainer</c>, or null on a 404.</summary>
    public Task<JsonObject?> GetAsync(string path, CancellationToken cancellation) =>
        GetAsync(new Uri(BaseUrl + path), CompanionSettings.Current.PlexToken, cancellation);

    /// <summary>A PUT on the server as its owner (changing a preference or a playlist).</summary>
    public async Task PutAsync(string path, CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Put, new Uri(BaseUrl + path), CompanionSettings.Current.PlexToken);
        using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PlexException(response.StatusCode);
        }
    }

    /// <summary>A GET on plex.tv's JSON API (<c>api/v2/…</c>) with a token; null on a 401 or 404.</summary>
    public async Task<JsonNode?> PlexTvAsync(string path, string token, CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Get, new Uri(PlexTv, path), token);
        using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PlexException(response.StatusCode);
        }

        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
    }

    /// <summary>A plex.tv call that only answers XML (<c>api/servers/…</c>, <c>api/home/users</c>); null on a 401 or 404.</summary>
    public async Task<XElement?> PlexTvXmlAsync(string path, string token, CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Get, new Uri(PlexTv, path), token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/xml");
        using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PlexException(response.StatusCode);
        }

        return XElement.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
    }

    /// <summary>A POST to plex.tv (a sign-in code for the settings page).</summary>
    public async Task<JsonNode?> PlexTvPostAsync(string path, CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Post, new Uri(PlexTv, path), null);
        using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PlexException(response.StatusCode);
        }

        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
    }

    /// <summary>The server's own id (its <c>machineIdentifier</c>), which plex.tv lists it by.</summary>
    public async Task<string?> MachineIdAsync(CancellationToken cancellation) =>
        (await GetAsync("/identity", cancellation).ConfigureAwait(false))?["machineIdentifier"]?.ToString();

    private async Task<JsonObject?> GetAsync(Uri address, string token, CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Get, address, token);
        using var response = await Client().SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PlexException(response.StatusCode);
        }

        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        return reply?["MediaContainer"] as JsonObject;
    }

    private HttpClient Client()
    {
        var client = _http.CreateClient("Plex");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri address, string? token)
    {
        var request = new HttpRequestMessage(method, address);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("X-Plex-Product", Product);
        request.Headers.Add("X-Plex-Client-Identifier", ClientId);
        request.Headers.Add("X-Plex-Version", typeof(PlexServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("X-Plex-Token", token);
        }

        return request;
    }

    private static string LoadClientId()
    {
        var path = Path.Combine(CompanionSettings.DataPath, "client-id");
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } known)
            {
                return known;
            }

            var made = "aster-plex-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(CompanionSettings.DataPath);
            File.WriteAllText(path, made);
            return made;
        }
        catch (IOException)
        {
            return "aster-plex";
        }
    }
}

/// <summary>Plex answered with something other than success.</summary>
public sealed class PlexException : Exception
{
    public PlexException(HttpStatusCode status)
        : base($"Plex answered {(int)status}")
    {
        Status = status;
    }

    public HttpStatusCode Status { get; }
}

/// <summary>Reading Plex's JSON, whose numbers sometimes come as text.</summary>
internal static class PlexJson
{
    public static string? Text(this JsonNode? node, string name) =>
        node?[name] is JsonValue value ? value.ToString() is { Length: > 0 } text ? text : null : null;

    public static long? Long(this JsonNode? node, string name) =>
        node?[name] is JsonValue value && (value.TryGetValue<long>(out var n) || long.TryParse(value.ToString(), out n)) ? n : null;

    public static int? Int(this JsonNode? node, string name) => node.Long(name) is long n ? (int)n : null;

    public static IEnumerable<JsonNode> List(this JsonNode? node, string name) =>
        node?[name] is JsonArray list ? list.OfType<JsonNode>() : [];
}
