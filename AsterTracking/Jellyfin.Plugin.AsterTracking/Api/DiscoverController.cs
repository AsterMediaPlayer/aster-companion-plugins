using System.Security.Claims;
using Jellyfin.Plugin.AsterTracking.Discovery;
using Jellyfin.Plugin.AsterTracking.Tracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterTracking.Api;

/// <summary>
/// Lists from the signed-in person's linked services, for the Aster apps' Discover:
///   GET /AsterTracking/Discover/Home                          the whole Discover page, built for this person
///   GET /AsterTracking/Discover                               the lists there are, given what they've linked
///   GET /AsterTracking/Discover/{service}/{list}?page=&amp;pageSize=  a page of one, each title with its ids and, when
///                                                             it's on this server, the item to play
/// A service that's signed out answers 410 (link it again); one that can't be reached, 502.
/// </summary>
[ApiController]
[Route("AsterTracking/Discover")]
[Authorize]
public class DiscoverController : ControllerBase
{
    private readonly DiscoveryService _discovery;
    private readonly HomeService _home;

    public DiscoverController(DiscoveryService discovery, HomeService home)
    {
        _discovery = discovery;
        _home = home;
    }

    /// <summary>
    /// The whole Discover page for the signed-in person, built here: rows in order, each of titles in the same shape
    /// as the lists. Kept 30 minutes; <c>fresh=true</c> builds it again (pull to refresh).
    /// </summary>
    [HttpGet("Home")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiscoverHomeDto>> GetHome([FromQuery] bool fresh = false, CancellationToken cancellation = default)
    {
        if (Caller() is not Guid id)
        {
            return Forbid();
        }

        return await _home.HomeAsync(id, fresh, cancellation).ConfigureAwait(false) is { } home ? home : NotFound();
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<DiscoverListDto>>> GetLists()
    {
        if (Caller() is not Guid id)
        {
            return Forbid();
        }

        return await _discovery.ListsAsync(id).ConfigureAwait(false);
    }

    [HttpGet("{service}/{list}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<DiscoverPageDto>> GetList([FromRoute] string service, [FromRoute] string list, [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken cancellation = default)
    {
        if (Caller() is not Guid id)
        {
            return Forbid();
        }

        try
        {
            return await _discovery.PageAsync(id, service, list, page, pageSize, cancellation).ConfigureAwait(false) is { } result ? result : NotFound();
        }
        catch (SignedOutException)
        {
            return Problem($"{service} needs linking again.", statusCode: StatusCodes.Status410Gone);
        }
        catch (Exception ex) when (ex is TrackerException or HttpRequestException or TaskCanceledException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>The signed-in person; an API key, which has none, can't browse.</summary>
    private Guid? Caller() =>
        Guid.TryParse(User.FindFirstValue("Jellyfin-UserId"), out var id) && id != Guid.Empty ? id : null;
}
