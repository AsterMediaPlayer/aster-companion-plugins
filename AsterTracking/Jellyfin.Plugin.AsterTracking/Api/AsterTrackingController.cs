using Jellyfin.Plugin.AsterTracking.Tracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterTracking.Api;

/// <summary>
/// What this server offers Aster, for the apps to read as they sign in:
///   GET /AsterTracking/Features   Seerr on or off, and its address; the tracking services; the watchlists the apps can add to (any signed-in user)
/// Without the plugin the call is a 404, which the apps take as "off": Seerr stays hidden unless it's turned on here.
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
        // On only with an address and a key: the apps reach Seerr through the plugin, never directly.
        var on = Seerr.SeerrBridge.Connection is not null;
        return new FeaturesDto
        {
            Seerr = new SeerrDto { Enabled = on, Proxy = on ? "/AsterTracking/Seerr/api/v1" : null },
            Services = _trackers.Where(t => t.Available).Select(t => t.Service).ToList(),
            Watchlist = WatchlistController.Services.Where(s => _trackers.Any(t => t.Service == s && t.Available)).ToList(),
            LinkPage = "/AsterTracking/Link",
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

    /// <summary>The page where anyone links their own accounts in a browser, on this server's address (0.10).</summary>
    public string LinkPage { get; set; } = string.Empty;
}

/// <summary>Seerr: whether Aster's request features are on, and where the apps reach Seerr's API (through this server).</summary>
public class SeerrDto
{
    public bool Enabled { get; set; }

    /// <summary>Seerr's API through this server, as the signed-in person: the apps call {server}{Proxy}/{Seerr's own path}.</summary>
    public string? Proxy { get; set; }
}
