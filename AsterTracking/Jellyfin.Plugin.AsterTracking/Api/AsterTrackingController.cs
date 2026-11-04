using Jellyfin.Plugin.AsterTracking.Tracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterTracking.Api;

/// <summary>
/// What this server offers Aster, for the apps to read as they sign in:
///   GET /AsterTracking/Features   Seerr on or off, and its address; the tracking services (any signed-in user)
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
        var config = Plugin.Instance?.Configuration;
        var url = SeerrAddress(config?.SeerrUrl);
        var on = config?.SeerrEnabled == true && url is not null;
        return new FeaturesDto
        {
            Seerr = new SeerrDto { Enabled = on, Url = on ? url : null },
            Services = _trackers.Where(t => t.Available).Select(t => t.Service).ToList(),
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

    /// <summary>The tracking services people can link on this server: "simkl", "anilist", "myanimelist".</summary>
    public List<string> Services { get; set; } = new();
}

/// <summary>Seerr: whether Aster's request features are on, and the server's address when they are.</summary>
public class SeerrDto
{
    public bool Enabled { get; set; }

    public string? Url { get; set; }
}
