using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>The plugin adds its own script tag to the web client's start page, so no other plugin is needed.</summary>
public class IndexHtmlInjectionTests
{
    private const string Page = "<!doctype html><html><head><title>Jellyfin</title></head><body><div id=\"reactRoot\"></div><script src=\"main.js\"></script></body></html>";

    // ---- the text change
    [Fact]
    public void TheTagGoesBeforeTheLastBodyClose()
    {
        var result = IndexHtmlInjector.Inject(Page, null, "v1");
        Assert.Contains("<script src=\"/DiscMenus/web/discmenus.js?v=v1\"></script>\n</body></html>", result);
        Assert.Equal(1, result.Split("discmenus.js").Length - 1);
    }

    [Fact]
    public void ThePageAroundTheTagIsUnchanged()
    {
        var result = IndexHtmlInjector.Inject(Page, null, "v1");
        Assert.Equal(Page, result.Replace("<script src=\"/DiscMenus/web/discmenus.js?v=v1\"></script>\n", string.Empty));
    }

    [Fact]
    public void ThePageIsNeverInjectedTwice()
    {
        var once = IndexHtmlInjector.Inject(Page, null, "v1");
        Assert.Equal(once, IndexHtmlInjector.Inject(once, null, "v2"));
        Assert.Equal(once, IndexHtmlInjector.Inject(once, "/jellyfin", "v3"));
    }

    [Theory]
    [InlineData("<html><head></head><body>x</BODY></html>")]
    [InlineData("<html><body>a</body><body>b</body></html>")]
    public void BodyCloseIsFoundWhateverItsCaseAndTheLastOneIsUsed(string html)
    {
        var result = IndexHtmlInjector.Inject(html, null, "v");
        var scriptAt = result.IndexOf("<script src=\"/DiscMenus", StringComparison.Ordinal);
        Assert.True(scriptAt > 0);
        Assert.Equal(html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase), scriptAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not html at all")]
    [InlineData("<html><body>unterminated")]
    public void APageWithoutABodyCloseIsLeftAlone(string html) => Assert.Equal(html, IndexHtmlInjector.Inject(html, null, "v"));

    [Theory]
    [InlineData("/jellyfin", "/jellyfin/DiscMenus/web/discmenus.js?v=v")]
    [InlineData("/a/b-c_d.e", "/a/b-c_d.e/DiscMenus/web/discmenus.js?v=v")]
    [InlineData("", "/DiscMenus/web/discmenus.js?v=v")]
    [InlineData(null, "/DiscMenus/web/discmenus.js?v=v")]
    [InlineData("/", "/DiscMenus/web/discmenus.js?v=v")]
    public void AConfiguredBaseUrlIsPartOfTheAddress(string? pathBase, string expected) =>
        Assert.Contains($"src=\"{expected}\"", IndexHtmlInjector.Inject(Page, pathBase, "v"));

    [Theory]
    [InlineData("/x\"><script>alert(1)</script>")]
    [InlineData("/a b")]
    [InlineData("//evil.example")]
    [InlineData("/../x")]
    [InlineData("/<b>")]
    [InlineData("javascript:alert(1)")]
    public void AnOddBaseUrlIsNeverWrittenIntoThePage(string pathBase)
    {
        var result = IndexHtmlInjector.Inject(Page, pathBase, "v");
        Assert.Contains("src=\"/DiscMenus/web/discmenus.js?v=v\"", result);
        Assert.DoesNotContain("alert", result);
        Assert.DoesNotContain("evil", result);
    }

    // ---- which requests
    [Theory]
    [InlineData("/web", true)]
    [InlineData("/web/", true)]
    [InlineData("/web/index.html", true)]
    [InlineData("/WEB/Index.HTML", true)]
    [InlineData("/web/main.jellyfin.bundle.js", false)]
    [InlineData("/web/index.html.map", false)]
    [InlineData("/web/other/index.html", false)]
    [InlineData("/", false)]
    [InlineData("/Items", false)]
    [InlineData("/DiscMenus/web/discmenus.js", false)]
    [InlineData("/webextra", false)]
    public void OnlyTheStartPageIsTouched(string path, bool expected) =>
        Assert.Equal(expected, IndexHtmlInjectionMiddleware.IsIndexPath(new PathString(path)));

    // ---- the middleware
    private static async Task<(DefaultHttpContext Context, string Body, Stream? DownstreamBody)> Run(
        string method, string path, Func<HttpContext, Task> downstream, Action<DefaultHttpContext>? setup = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        var output = new MemoryStream();
        context.Response.Body = output;
        setup?.Invoke(context);
        Stream? seen = null;
        var middleware = new IndexHtmlInjectionMiddleware(
            async c =>
            {
                seen = c.Response.Body;
                await downstream(c);
            },
            NullLogger<IndexHtmlInjectionMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        return (context, Encoding.UTF8.GetString(output.ToArray()), seen);
    }

    private static Func<HttpContext, Task> Html(string html = Page, int status = 200, string type = "text/html") => async c =>
    {
        c.Response.StatusCode = status;
        c.Response.ContentType = type;
        c.Response.Headers.ETag = "\"abc\"";
        c.Response.Headers.LastModified = "Wed, 01 Jan 2025 00:00:00 GMT";
        c.Response.ContentLength = Encoding.UTF8.GetByteCount(html);
        await c.Response.WriteAsync(html);
    };

    [Fact]
    public async Task TheStartPageGainsTheTagOnce()
    {
        var (context, body, _) = await Run("GET", "/web/index.html", Html());
        Assert.Contains("discmenus.js?v=" + ScriptInfo.Version, body);
        Assert.Equal(1, body.Split("discmenus.js").Length - 1);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), context.Response.ContentLength);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.StartsWith("text/html", context.Response.ContentType);
    }

    [Fact]
    public async Task TheStaleValidatorsAreDropped()
    {
        var (context, _, _) = await Run("GET", "/web/", Html());
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.False(context.Response.Headers.ContainsKey("Last-Modified"));
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task TheRequestReachesTheServerUnconditionalAndUncompressed()
    {
        IHeaderDictionary? seen = null;
        await Run(
            "GET",
            "/web/index.html",
            async c =>
            {
                seen = new HeaderDictionary(c.Request.Headers.ToDictionary(h => h.Key, h => h.Value));
                await Html()(c);
            },
            c =>
            {
                c.Request.Headers.AcceptEncoding = "gzip, br";
                c.Request.Headers.IfNoneMatch = "\"abc\"";
                c.Request.Headers.IfModifiedSince = "Wed, 01 Jan 2025 00:00:00 GMT";
                c.Request.Headers.Range = "bytes=0-10";
                c.Request.Headers["X-Other"] = "kept";
            });
        Assert.NotNull(seen);
        foreach (var gone in new[] { "Accept-Encoding", "If-None-Match", "If-Modified-Since", "Range" })
        {
            Assert.False(seen!.ContainsKey(gone), gone);
        }

        Assert.Equal("kept", seen!["X-Other"].ToString());
    }

    [Theory]
    [InlineData("GET", "/web/main.js")]
    [InlineData("GET", "/Items")]
    [InlineData("GET", "/")]
    [InlineData("POST", "/web/index.html")]
    [InlineData("HEAD", "/web/index.html")]
    [InlineData("OPTIONS", "/web/")]
    public async Task OtherRequestsPassStraightThrough(string method, string path)
    {
        var original = new MemoryStream();
        var (context, body, seen) = await Run(method, path, Html(), c =>
        {
            c.Response.Body = original;
            c.Request.Headers.AcceptEncoding = "gzip";
        });
        Assert.Same(original, seen);
        Assert.DoesNotContain("discmenus.js", body);
        Assert.False(string.IsNullOrEmpty(context.Request.Headers.AcceptEncoding.ToString()));
        Assert.True(context.Response.Headers.ContainsKey("ETag"));
    }

    [Theory]
    [InlineData(404, "text/html")]
    [InlineData(200, "application/json")]
    [InlineData(200, "text/javascript")]
    [InlineData(500, "text/html")]
    [InlineData(304, "text/html")]
    public async Task OnlyAPlainHtmlAnswerIsChanged(int status, string type)
    {
        var (context, body, _) = await Run("GET", "/web/index.html", Html(Page, status, type));
        Assert.Equal(Page, body);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.True(context.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task AlreadyCompressedContentIsNotTouched()
    {
        var (_, body, _) = await Run("GET", "/web/index.html", async c =>
        {
            await Html()(c);
            c.Response.Headers.ContentEncoding = "gzip";
        });
        Assert.Equal(Page, body);
    }

    [Fact]
    public async Task APageThatAlreadyHasTheTagIsServedAsItIs()
    {
        var already = IndexHtmlInjector.Inject(Page, null, "from-file-transformation");
        var (context, body, _) = await Run("GET", "/web/index.html", Html(already));
        Assert.Equal(already, body);
        Assert.Equal(1, body.Split("discmenus.js").Length - 1);
        Assert.True(context.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task AnEnormousPageIsNotBuffered()
    {
        var big = "<html>" + new string('x', 6 * 1024 * 1024) + "</body></html>";
        var (_, body, _) = await Run("GET", "/web/index.html", Html(big));
        Assert.DoesNotContain("discmenus.js", body);
        Assert.Equal(big.Length, body.Length);
    }

    [Fact]
    public async Task AFailingServerLeavesTheResponseBodyAsItWas()
    {
        var original = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/web/index.html";
        context.Response.Body = original;
        var middleware = new IndexHtmlInjectionMiddleware(_ => throw new InvalidOperationException("boom"), NullLogger<IndexHtmlInjectionMiddleware>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        Assert.Same(original, context.Response.Body);
    }

    [Fact]
    public async Task TheBaseUrlOfTheRequestIsUsed()
    {
        var (_, body, _) = await Run("GET", "/web/index.html", Html(), c => c.Request.PathBase = "/jellyfin");
        Assert.Contains("src=\"/jellyfin/DiscMenus/web/discmenus.js?v=", body);
    }

    [Fact]
    public async Task ThePageIsWrittenAsUtf8WithoutAByteOrderMark()
    {
        var html = "<html><body>café ☃ \U0001F600</body></html>";
        var (_, body, _) = await Run("GET", "/web/index.html", Html(html));
        Assert.Contains("café ☃ \U0001F600", body);
        Assert.False(body.StartsWith('﻿'));
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void TheStartupFilterPutsTheMiddlewareFirst()
    {
        var calls = new List<string>();
        var builder = new Microsoft.AspNetCore.Builder.ApplicationBuilder(new EmptyServices());
        var configure = new IndexHtmlInjectionStartupFilter().Configure(app => calls.Add("rest of the pipeline"));
        configure(builder);
        Assert.Equal(new[] { "rest of the pipeline" }, calls);
    }
}
