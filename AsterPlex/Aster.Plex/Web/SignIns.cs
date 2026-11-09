using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Aster.Plex.Web;

/// <summary>
/// People signed in to the settings page: a random id in a cookie, standing for the Plex token they signed in with.
/// Kept in memory only, so a restart signs everyone out of the page (never out of their apps). The cookie is
/// HttpOnly and SameSite=Strict, so other sites can't make requests with it.
/// </summary>
public sealed class SignIns
{
    public const string Cookie = "aster_session";

    private static readonly TimeSpan Lasts = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _sessions = new();

    /// <summary>Signs someone in, and sets the cookie on the response.</summary>
    public void SignIn(HttpContext context, string token)
    {
        var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var until = DateTimeOffset.UtcNow + Lasts;
        _sessions[id] = (token, until);
        context.Response.Cookies.Append(Cookie, id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
            Expires = until,
        });
        foreach (var old in _sessions.Where(s => s.Value.Until < DateTimeOffset.UtcNow).Select(s => s.Key).ToList())
        {
            _sessions.TryRemove(old, out _);
        }
    }

    public void SignOut(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(Cookie, out var id))
        {
            _sessions.TryRemove(id, out _);
        }

        context.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
    }

    /// <summary>The Plex token of whoever's signed in to the page with this request, or null.</summary>
    public string? TokenOf(HttpRequest request) =>
        request.Cookies.TryGetValue(Cookie, out var id) && _sessions.TryGetValue(id, out var session) && session.Until > DateTimeOffset.UtcNow
            ? session.Token
            : null;
}
