using Jellyfin.Plugin.DiscMenus.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DiscMenus.Api;

/// <summary>
/// Player-facing API: unlike <see cref="DiscMenusController"/> (admin-only,
/// [Authorize(Policy = "RequiresElevation")]), this just requires any
/// authenticated Jellyfin user - any viewer watching a title should be able
/// to fetch its disc menu, not just admins.
/// </summary>
[ApiController]
[Authorize]
[Route("DiscMenus")]
public sealed class DiscMenusPlayerController : ControllerBase
{
    private readonly DiscMenuService _discMenuService;

    public DiscMenusPlayerController(DiscMenuService discMenuService)
    {
        _discMenuService = discMenuService;
    }

    /// <summary>
    /// A parent item's disc menu, with every playExtra/playSequence entry
    /// already resolved to local item IDs, ready for the web renderer to
    /// consume directly - no separate menu/binding join needed client-side.
    /// </summary>
    [HttpGet("{parentItemId}/Menu")]
    public ActionResult<RenderableMenuDocument> GetRenderableMenu([FromRoute] Guid parentItemId)
    {
        if (!_discMenuService.ScanBindings().TryGetValue(parentItemId, out var pair))
        {
            return NotFound($"No disc menu bound to item '{parentItemId}'.");
        }

        var (menu, binding, _) = pair;
        return Ok(RenderableBuilder.Build(menu, binding, parentItemId, _discMenuService));
    }
}
