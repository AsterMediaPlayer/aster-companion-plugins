using System.Security.Claims;
using Jellyfin.Plugin.AsterTracking.Seerr;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterTracking.Api;

/// <summary>
/// Seerr's API for the Aster apps, through the server, as the signed-in person:
///   {GET|POST|PUT|DELETE} /AsterTracking/Seerr/api/v1/{path}   Seerr's own route and answer
/// The apps use it exactly as Seerr's API (same paths, queries, bodies and replies), with their Jellyfin sign-in
/// instead of a Seerr one. Only browsing, titles, search and requests pass (403 otherwise); 503 when requests are off.
/// </summary>
[ApiController]
[Route("AsterTracking/Seerr/api/v1")]
[Authorize]
public class SeerrController : ControllerBase
{
    private readonly SeerrBridge _bridge;
    private readonly IUserManager _users;

    public SeerrController(SeerrBridge bridge, IUserManager users)
    {
        _bridge = bridge;
        _users = users;
    }

    [AcceptVerbs("GET", "POST", "PUT", "DELETE", Route = "{**path}")]
    public async Task<IActionResult> Pass([FromRoute] string path, CancellationToken cancellation)
    {
        if (!Guid.TryParse(User.FindFirstValue("Jellyfin-UserId"), out var userId) || userId == Guid.Empty
            || _users.GetUserById(userId) is not { } user)
        {
            return Forbid();
        }

        path = path.Trim('/');
        var method = Request.Method.ToUpperInvariant();
        if (!SeerrBridge.Allowed(method, path))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Not available through Aster." });
        }

        string? body = null;
        if (method is "POST" or "PUT")
        {
            using var reader = new StreamReader(Request.Body);
            body = await reader.ReadToEndAsync(cancellation).ConfigureAwait(false);
        }

        try
        {
            var (status, text, type) = await _bridge.SendAsync(userId, user.Username, new HttpMethod(method), path, Request.QueryString.Value ?? string.Empty, body, cancellation).ConfigureAwait(false);
            return new ContentResult { StatusCode = (int)status, Content = text, ContentType = type };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "Seerr couldn't be reached: " + ex.Message });
        }
    }
}
