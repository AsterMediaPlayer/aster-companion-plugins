using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AsterAurora;

/// <summary>
/// Adds Aurora's stylesheet and script tags to jellyfin-web's <c>index.html</c> as the page is served, so the plugin
/// never edits Jellyfin's own files. A page that already has the tags is left alone, so this coexists with other
/// plugins doing the same (Disc Menus uses the same approach for its own script).
/// </summary>
public static class IndexHtmlInjector
{
    /// <summary>The script's address (without base URL or version); also how an already-injected page is recognised.</summary>
    public const string ScriptPath = "/Aurora/web/aurora.js";

    public const string StylePath = "/Aurora/web/aurora.css";

    private static readonly Regex SafePathBase = new(@"^(/(?!\.{1,2}(/|$))[A-Za-z0-9._~-]+)*$", RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="html"/> with the stylesheet before <c>&lt;/head&gt;</c> and the script before the last
    /// <c>&lt;/body&gt;</c>. <paramref name="pathBase"/> is Jellyfin's base URL ("" or "/jellyfin"); anything that isn't a
    /// plain path is ignored rather than written into the page.
    /// </summary>
    public static string Inject(string html, string? pathBase, string version)
    {
        if (html.Contains(ScriptPath, StringComparison.Ordinal))
        {
            return html;
        }

        var bodyAt = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        var headAt = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (bodyAt < 0 || headAt < 0 || headAt > bodyAt)
        {
            return html;
        }

        var prefix = !string.IsNullOrEmpty(pathBase) && SafePathBase.IsMatch(pathBase) ? pathBase.TrimEnd('/') : string.Empty;
        var link = $"<link rel=\"stylesheet\" href=\"{prefix}{StylePath}?v={version}\">\n";
        var script = $"<script src=\"{prefix}{ScriptPath}?v={version}\" defer></script>\n";
        return html[..headAt] + link + html[headAt..bodyAt] + script + html[bodyAt..];
    }
}

/// <summary>Changes on every build, so the injected addresses change with it and browsers never keep a stale copy.</summary>
public static class ScriptInfo
{
    public static readonly string Version = typeof(ScriptInfo).Assembly.ManifestModule.ModuleVersionId.ToString("N");
}

/// <summary>
/// Wraps requests for the web client's start page (<c>/web/</c>, <c>/web/index.html</c>) and adds the tags on the way
/// out. Only that page is touched, only when it is a plain 200 HTML answer. The request is made unconditional and
/// uncompressed so the page can be read, and the browser told not to keep a copy without the tags.
/// </summary>
public sealed class IndexHtmlInjectionMiddleware
{
    private const long MaxPageBytes = 5 * 1024 * 1024;
    private static readonly string[] ConditionalHeaders = { "Accept-Encoding", "If-None-Match", "If-Modified-Since", "If-Match", "If-Unmodified-Since", "If-Range", "Range" };

    private readonly RequestDelegate _next;
    private readonly ILogger<IndexHtmlInjectionMiddleware> _logger;

    public IndexHtmlInjectionMiddleware(RequestDelegate next, ILogger<IndexHtmlInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public static bool IsIndexPath(PathString path) =>
        path.Equals("/web", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/web/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsIndexPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        foreach (var header in ConditionalHeaders)
        {
            context.Request.Headers.Remove(header);
        }

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        var response = context.Response;
        buffer.Position = 0;
        var isHtml = response.StatusCode == StatusCodes.Status200OK
            && (response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ?? false)
            && string.IsNullOrEmpty(response.Headers.ContentEncoding.ToString())
            && buffer.Length <= MaxPageBytes;
        if (!isHtml)
        {
            await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var html = new UTF8Encoding(false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var injected = IndexHtmlInjector.Inject(html, context.Request.PathBase.Value, ScriptInfo.Version);
        if (injected == html)
        {
            await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var bytes = new UTF8Encoding(false).GetBytes(injected);
        response.Headers.Remove("ETag");
        response.Headers.Remove("Last-Modified");
        response.Headers.CacheControl = "no-cache";
        response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        _logger.LogDebug("Added the Aurora tags to the web client's start page.");
    }
}

/// <summary>Puts <see cref="IndexHtmlInjectionMiddleware"/> at the start of the web server's pipeline.</summary>
public sealed class IndexHtmlInjectionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<IndexHtmlInjectionMiddleware>();
        next(app);
    };
}
