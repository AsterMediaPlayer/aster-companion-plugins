using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterTracking.Api;

/// <summary>
/// The page where anyone on the server links their own tracking accounts in a browser:
///   GET /AsterTracking/Link
/// Jellyfin shows plugin settings pages to administrators only, so people without the dashboard link here (or in an Aster
/// app's Settings ▸ Tracking). The page itself is static and open; everything it shows or changes goes through
/// /AsterTracking/Accounts as the signed-in person (their Jellyfin web sign-in in this browser, or one made on the page),
/// which answers only for them.
/// </summary>
[ApiController]
[Route("AsterTracking")]
public class LinkPageController : ControllerBase
{
    private static readonly Lazy<string> Page = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Jellyfin.Plugin.AsterTracking.Web.link.html")
            ?? throw new InvalidOperationException("The link page is missing from the plugin.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [HttpGet("Link")]
    [AllowAnonymous]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ContentResult LinkPage()
    {
        // Never shown inside another site's frame, and nothing it loads comes from anywhere but this server.
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; form-action 'none'; base-uri 'none'";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-cache";
        return Content(Page.Value, "text/html; charset=utf-8");
    }
}
