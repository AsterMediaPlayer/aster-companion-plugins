using Aster.Plex.Tracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aster.Plex.Api;

/// <summary>
/// What this server offers Aster, for the apps to read as they sign in:
///   GET /AsterTracking/Features   Seerr on or off, and its address; the tracking services; the watchlists the apps can add to (any signed-in user)
/// The same answer as the Jellyfin plugin's, so the apps treat a Plex server with this container like a Jellyfin server
/// with the plugin. Seerr isn't offered here yet.
/// </summary>
[ApiController]
[Route("AsterTracking")]
public class AsterTrackingController : ControllerBase
{
    private readonly IReadOnlyList<ITracker> _trackers;

    public AsterTrackingController(IEnumerable<ITracker> trackers)
    {
        _trackers = trackers.ToList();
    }

    /// <summary>The server's Aster features. Seerr is only on with an address the apps can use.</summary>
    [HttpGet("Features")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FeaturesDto> GetFeatures()
    {
        return new FeaturesDto
        {
            Seerr = new SeerrDto { Enabled = false },
            Discover = false,
            Services = _trackers.Where(t => t.Available).Select(t => t.Service).ToList(),
            Watchlist = WatchlistController.Services.Where(s => _trackers.Any(t => t.Service == s && t.Available)).ToList(),
        };
    }

    /// <summary>An absolute http(s) address without a trailing slash, or null for anything else.</summary>
    internal static string? SeerrAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}

/// <summary>The server's Aster features.</summary>
public class FeaturesDto
{
    public SeerrDto Seerr { get; set; } = new();

    /// <summary>The tracking services people can link on this server: "simkl", "anilist", "myanimelist", "lastfm", "listenbrainz", "hardcover".</summary>
    public List<string> Services { get; set; } = new();

    /// <summary>The services the apps can put titles on the watchlist of (<c>/AsterTracking/Watchlist</c>): "simkl" for now.</summary>
    public List<string> Watchlist { get; set; } = new();

    /// <summary>
    /// Whether there's a Discover page (<c>/AsterTracking/Discover/Home</c>). The Jellyfin plugin's Features has no such
    /// field, which the apps read as yes; this container has none yet, and says so.
    /// </summary>
    public bool Discover { get; set; }
}

/// <summary>Seerr: whether Aster's request features are on, and where the apps reach Seerr's API (through this server).</summary>
public class SeerrDto
{
    public bool Enabled { get; set; }

    /// <summary>Seerr's API through this server, as the signed-in person: the apps call {server}{Proxy}/{Seerr's own path}.</summary>
    public string? Proxy { get; set; }
}
