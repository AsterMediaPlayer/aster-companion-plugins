using Aster.Plex.Plex;
using Yarp.ReverseProxy.Forwarder;

namespace Aster.Plex.Web;

/// <summary>
/// Plex's own API for the settings page, as whoever is signed in to it: <c>/aster/plex/{path}</c> goes to Plex's
/// <c>/{path}</c> with their token, which the page never sees (its cookie stands for it). The Channels page reads and
/// writes the person's playlists and reads the libraries through it, as the app does. Only the paths the page needs
/// are passed.
/// </summary>
public static class PlexAsPerson
{
    private static readonly string[] Allowed = ["/identity", "/library/", "/playlists"];

    public static void Map(WebApplication app, HttpMessageInvoker toPlex)
    {
        app.Map("/aster/plex/{**rest}", async (HttpContext context, IHttpForwarder forwarder, SignIns signIns, PlexPeople people) =>
        {
            var token = signIns.TokenOf(context.Request);
            if (token is null || !PlexServer.Configured || await people.ForTokenAsync(token, context.RequestAborted) is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var path = "/" + (context.Request.RouteValues["rest"]?.ToString() ?? string.Empty);
            if (!Allowed.Any(a => path == a.TrimEnd('/') || path.StartsWith(a, StringComparison.Ordinal)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await forwarder.SendAsync(context, PlexServer.BaseUrl, toPlex, ForwarderRequestConfig.Empty, new AsPerson(token, path));
        });
    }

    private sealed class AsPerson(string token, string path) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);
            proxyRequest.RequestUri = new Uri(destinationPrefix.TrimEnd('/') + path + httpContext.Request.QueryString.Value);
            proxyRequest.Headers.Remove("Cookie");
            proxyRequest.Headers.Remove("X-Plex-Token");
            proxyRequest.Headers.Add("X-Plex-Token", token);
            proxyRequest.Headers.Remove("Accept");
            proxyRequest.Headers.Add("Accept", "application/json");
            proxyRequest.Headers.Add("X-Plex-Product", PlexServer.Product);
            proxyRequest.Headers.Add("X-Plex-Client-Identifier", PlexServer.ClientId);
        }
    }
}
