using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Branding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterAurora.Api;

/// <summary>
/// Serves Aurora's browser files and its feature switches. All three are anonymous: the start page loads them before
/// anyone signs in, and none of it is sensitive (the switches are plain on/off settings, and the custom CSS is
/// already served anonymously by Jellyfin itself at /Branding/Css).
/// </summary>
[ApiController]
[Route("Aurora")]
public sealed partial class AuroraController : ControllerBase
{
    private readonly IConfigurationManager _configurationManager;

    public AuroraController(IConfigurationManager configurationManager)
    {
        _configurationManager = configurationManager;
    }

    [HttpGet("web/aurora.js")]
    [AllowAnonymous]
    public ContentResult GetScript() => Resource("Web.aurora.js", "application/javascript");

    /// <summary>
    /// Aurora's own styles, followed by the server's custom CSS (Dashboard → Branding).
    /// jellyfin-web only applies the custom CSS after it has started and fetched it, so the first frames of every app
    /// load are drawn unstyled and the skin then snaps in (a visible flash, and a 16 px jump from the taller header).
    /// This stylesheet is linked in the page's &lt;head&gt;, so carrying the custom CSS here applies it from the first
    /// frame. jellyfin-web still adds its own copy later; identical rules change nothing.
    /// </summary>
    [HttpGet("web/aurora.css")]
    [AllowAnonymous]
    public ContentResult GetStyle()
    {
        var own = ReadResource("Web.aurora.css");
        var custom = string.Empty;
        try
        {
            custom = _configurationManager.GetConfiguration<BrandingOptions>("branding")?.CustomCss ?? string.Empty;
        }
        catch (Exception)
        {
            // No branding config available: serve Aurora's own styles only.
        }

        // @import is only valid at the very top of a stylesheet, so lift the custom CSS's imports (the skin's web font)
        // above everything else.
        var imports = new StringBuilder();
        foreach (Match m in ImportRule().Matches(custom))
        {
            imports.AppendLine(m.Value);
        }

        var body = ImportRule().Replace(custom, string.Empty);
        var css = new StringBuilder()
            .Append(imports)
            .AppendLine(own)
            .AppendLine("/* ===== Server custom CSS (Dashboard → Branding), applied early by Aurora ===== */")
            .Append(body)
            .ToString();

        Response.Headers.CacheControl = "no-cache";
        return Content(css, "text/css");
    }

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
            c.EnableBackButton,
        };
    }

    // A plain "[^;]+;" breaks on URLs that contain semicolons (Google Fonts: "wght@400;500;600"), leaving a
    // half-statement that corrupts the rest of the sheet; so match the url(...) or quoted string as a whole first.
    [GeneratedRegex(@"@import\s+(?:url\([^)]*\)|""[^""]*""|'[^']*')[^;]*;", RegexOptions.IgnoreCase)]
    private static partial Regex ImportRule();

    private ContentResult Resource(string name, string contentType)
    {
        Response.Headers.CacheControl = "no-cache";
        return Content(ReadResource(name), contentType);
    }

    private string ReadResource(string name)
    {
        var assembly = GetType().Assembly;
        var resourceName = $"{assembly.GetName().Name}.{name}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
