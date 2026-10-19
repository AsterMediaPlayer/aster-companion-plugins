using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterChannels.Api;

/// <summary>
/// Serves the Channels page's scripts. A dashboard page only reliably runs the script inside its own fragment, so the
/// page loads these through script tags pointing here. Only the plugin's own embedded scripts can be served.
/// </summary>
[ApiController]
[Route("AsterChannels")]
public class AsterChannelsController : ControllerBase
{
    private static readonly HashSet<string> Scripts = new(StringComparer.Ordinal) { "channel-tags", "channels" };

    /// <summary>One of the page's scripts. Anonymous, as it's only code: every call it makes uses the signed-in user's token.</summary>
    [HttpGet("web/{name}.js")]
    [AllowAnonymous]
    public ActionResult GetScript(string name)
    {
        if (!Scripts.Contains(name))
        {
            return NotFound();
        }

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.Web.{name}.js");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        Response.Headers.CacheControl = "no-cache";
        return Content(reader.ReadToEnd(), "application/javascript");
    }
}
