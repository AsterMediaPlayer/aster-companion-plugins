using Jellyfin.Plugin.DiscMenus.Fanart;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DiscMenus.Api;

/// <summary>
/// Artwork from fanart.tv for menus whose background is "fanart". Clients ask this server for a picture and never contact
/// fanart.tv: the server looks it up with the administrator's API key and serves it from a cache. The picture route is open
/// (an image element can't send credentials, like the asset route); the others are for administrators.
/// </summary>
[ApiController]
[Route("DiscMenus/Fanart")]
public sealed class DiscMenusFanartController : ControllerBase
{
    private readonly DiscMenuService _discMenuService;
    private readonly FanartService _fanart;

    public DiscMenusFanartController(DiscMenuService discMenuService, FanartService fanart)
    {
        _discMenuService = discMenuService;
        _fanart = fanart;
    }

    /// <summary>
    /// A picture from fanart.tv's listing for this title. Anything wrong (no key, no such picture, fanart.tv unreachable) is the
    /// same plain 404, so this route never explains the server's configuration to whoever asks.
    /// </summary>
    [HttpGet("{itemId:guid}/{imageId}")]
    [AllowAnonymous]
    public async Task<ActionResult> GetImage(Guid itemId, string imageId, CancellationToken ct)
    {
        var target = _discMenuService.GetFanartTarget(itemId);
        if (target is null)
        {
            return NotFound();
        }

        var result = await _fanart.GetImageAsync(target, imageId, ct).ConfigureAwait(false);
        if (result.Status != FanartStatus.Ok || result.Path is null || result.ContentType is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return PhysicalFile(result.Path, result.ContentType);
    }

    /// <summary>Whether a key is configured. The key itself is never returned.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetStatus() => Ok(new { Configured = _fanart.HasKey });

    /// <summary>Asks fanart.tv, with the configured key, whether it works.</summary>
    [HttpPost("Test")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Test(CancellationToken ct) =>
        Ok(new { Status = (await _fanart.TestKeyAsync(ct).ConfigureAwait(false)).ToString() });

    /// <summary>The pictures fanart.tv has for a library title: the ids to put in a menu's <c>fanartId</c>.</summary>
    [HttpGet("List/{itemId:guid}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> List(Guid itemId, CancellationToken ct)
    {
        var target = _discMenuService.GetFanartTarget(itemId);
        if (target is null)
        {
            return NotFound(new { Error = "That item has no TMDB id (movies) or TVDB id (series), so fanart.tv can't be asked about it." });
        }

        var lookup = await _fanart.GetListingAsync(target, ct).ConfigureAwait(false);
        return Ok(new
        {
            Status = lookup.Status.ToString(),
            Images = lookup.Images.Select(i => new { i.Id, i.Category, i.Lang, i.Likes }),
        });
    }
}
