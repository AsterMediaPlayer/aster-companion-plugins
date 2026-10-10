using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Aurora.Api;

/// <summary>
/// Serves Aurora's browser files and its feature switches. All three are anonymous: the start page loads them before
/// anyone signs in, and none of it is sensitive (the switches are plain on/off settings).
/// </summary>
[ApiController]
[Route("Aurora")]
public sealed class AuroraController : ControllerBase
{
    [HttpGet("web/aurora.js")]
    [AllowAnonymous]
    public ContentResult GetScript() => Resource("Web.aurora.js", "application/javascript");

    [HttpGet("web/aurora.css")]
    [AllowAnonymous]
    public ContentResult GetStyle() => Resource("Web.aurora.css", "text/css");

    [HttpGet("config")]
    [AllowAnonymous]
    public ActionResult<object> GetClientConfig()
    {
        var c = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        Response.Headers.CacheControl = "no-cache";
        return new
        {
            c.EnableHero,
            HeroSource = c.HeroSource is "latest" or "random" or "favorites" ? c.HeroSource : "latest",
            HeroCount = Math.Clamp(c.HeroCount, 1, 20),
            HeroIntervalSeconds = Math.Clamp(c.HeroIntervalSeconds, 4, 60),
            c.EnableTrailers,
            c.AllowYouTubeTrailers,
            TrailerDelaySeconds = Math.Clamp(c.TrailerDelaySeconds, 1, 30),
            c.EnableTilt,
            c.EnableGenreChips,
            c.EnableUpNext,
            c.EnableBadges,
            c.EnableEpisodeShelf,
        };
    }

    private ContentResult Resource(string name, string contentType)
    {
        var assembly = GetType().Assembly;
        var resourceName = $"{assembly.GetName().Name}.{name}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        Response.Headers.CacheControl = "no-cache";
        return Content(reader.ReadToEnd(), contentType);
    }
}
