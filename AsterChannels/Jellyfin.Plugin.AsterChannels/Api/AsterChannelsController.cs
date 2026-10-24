using System.Reflection;
using Jellyfin.Plugin.AsterChannels.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterChannels.Api;

/// <summary>
/// Serves the Channels page's scripts, and keeps Live TV channels' groups:
///   GET  /AsterChannels/web/{name}.js     one of the page's scripts (anonymous: only code)
///   GET  /AsterChannels/LiveTvGroups      every Live TV channel's group (any signed-in user; Aster reads it)
///   POST /AsterChannels/LiveTvGroups      set or clear one channel's group (administrators)
/// A dashboard page only reliably runs the script inside its own fragment, so the page loads its scripts through
/// script tags pointing here. Only the plugin's own embedded scripts can be served.
/// </summary>
[ApiController]
[Route("AsterChannels")]
public class AsterChannelsController : ControllerBase
{
    private static readonly HashSet<string> Scripts = new(StringComparer.Ordinal) { "channel-tags", "channels" };

    /// <summary>One of the page's scripts. Anonymous, as it's only code: every call it makes uses the signed-in user's token.</summary>
    [HttpGet("web/{name}.js")]
    [AllowAnonymous]
    public ActionResult GetScript(string name)
    {
        if (!Scripts.Contains(name))
        {
            return NotFound();
        }

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.Web.{name}.js");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        Response.Headers.CacheControl = "no-cache";
        return Content(reader.ReadToEnd(), "application/javascript");
    }

    /// <summary>Every Live TV channel's group. Without any, the app shows Live TV as it always has.</summary>
    [HttpGet("LiveTvGroups")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LiveTvGroupsDto> GetLiveTvGroups()
    {
        var groups = Plugin.Instance?.Configuration.LiveTvGroups ?? new List<LiveTvChannelGroup>();
        return new LiveTvGroupsDto { Groups = groups.Select(g => new LiveTvChannelGroup { ChannelId = g.ChannelId, Group = g.Group }).ToList() };
    }

    /// <summary>Puts a Live TV channel in a group, or takes it out of its group when Group is empty.</summary>
    [HttpPost("LiveTvGroups")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<LiveTvGroupsDto> SetLiveTvGroup([FromBody] LiveTvChannelGroup change)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var channelId = change.ChannelId?.Trim() ?? string.Empty;
        if (channelId.Length == 0)
        {
            return BadRequest(new { Message = "A channel id is needed." });
        }

        var group = change.Group?.Trim() ?? string.Empty;
        var groups = plugin.Configuration.LiveTvGroups;
        groups.RemoveAll(g => string.Equals(g.ChannelId, channelId, StringComparison.OrdinalIgnoreCase));
        if (group.Length > 0)
        {
            // An existing group's own spelling, whatever the case typed.
            var existing = groups.Select(g => g.Group).FirstOrDefault(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
            groups.Add(new LiveTvChannelGroup { ChannelId = channelId, Group = existing ?? group });
        }

        plugin.SaveConfiguration();
        return GetLiveTvGroups();
    }
}

/// <summary>The Live TV channels' groups.</summary>
public class LiveTvGroupsDto
{
    public List<LiveTvChannelGroup> Groups { get; set; } = new();
}
