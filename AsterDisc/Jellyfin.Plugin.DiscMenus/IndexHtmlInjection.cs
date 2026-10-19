using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>
/// Puts the web menu renderer's script tag into jellyfin-web's <c>index.html</c> as the page is served, so the plugin needs no other plugin and
/// never edits Jellyfin's own files. The tag is added once: a page that already has it is left alone, so this and the optional File Transformation
/// hook can both be present.
/// </summary>
public static class IndexHtmlInjector
{
    /// <summary>The address (without base URL or version) of the renderer; also how an already-injected page is recognised.</summary>
    public const string ScriptPath = "/DiscMenus/web/discmenus.js";

    private static readonly Regex SafePathBase = new(@"^(/(?!\.{1,2}(/|$))[A-Za-z0-9._~-]+)*$", RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="html"/> with the script tag before the last <c>&lt;/body&gt;</c>. <paramref name="pathBase"/> is Jellyfin's configured base URL
    /// ("" or "/jellyfin"); anything that isn't a plain path is ignored rather than written into the page.
    /// </summary>
    public static string Inject(string html, string? pathBase, string version)
    {
        if (html.Contains(ScriptPath, StringComparison.Ordinal))
        {
            return html;
        }

        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return html;
        }

        var prefix = !string.IsNullOrEmpty(pathBase) && SafePathBase.IsMatch(pathBase) ? pathBase.TrimEnd('/') : string.Empty;
        return html[..at] + $"<script src=\"{prefix}{ScriptPath}?v={version}\"></script>\n" + html[at..];
    }
}

/// <summary>Changes on every build, so the injected script address changes with it and a browser never runs a stale cached renderer.</summary>
public static class ScriptInfo
{
    public static readonly string Version = typeof(ScriptInfo).Assembly.ManifestModule.ModuleVersionId.ToString("N");
}

/// <summary>
/// Wraps requests for the web client's start page (<c>/web/</c>, <c>/web/index.html</c>) and adds the renderer script to the HTML on its way out.
/// Only that page is touched, only when it is a plain 200 HTML answer. To be able to read the page the request is made unconditional and
/// uncompressed (no <c>Accept-Encoding</c>, <c>If-None-Match</c>...): the page is small, and a browser must not keep a copy that lacks the tag.
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

    /// <summary>Whether a request path is the web client's start page.</summary>
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
        if (ReferenceEquals(injected, html) || injected == html)
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
        _logger.LogDebug("Added the Disc Menus renderer script to the web client's start page.");
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
