using System.Security.Claims;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AsterTracking.Discovery;
using Jellyfin.Plugin.AsterTracking.Simkl;
using Jellyfin.Plugin.AsterTracking.Tracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterTracking.Api;

/// <summary>
/// Putting a title on the signed-in person's watchlist on a linked service, from the Aster apps' Discover and title pages.
/// Simkl for now (its Plan to Watch); AniList's Planning and MyAnimeList's Plan to Watch can follow under the same routes.
///   GET    /AsterTracking/Watchlist?mediaType=movie&amp;tmdb=…&amp;imdb=…   where it stands: { Services: [{ Service, Linked, On }] }
///   POST   /AsterTracking/Watchlist/{service}   body { MediaType, Anime, Ids, Title, Year }   on it: { Service, On: true }
///   DELETE /AsterTracking/Watchlist/{service}   the same body                                    off it: { Service, On: false }
/// A title with none of the ids the service knows is 400; a service it can't do yet 404; one the person hasn't linked
/// 409; one that's signed out 410; one that doesn't know the title 422; one that can't be reached 502.
/// </summary>
[ApiController]
[Route("AsterTracking/Watchlist")]
[Authorize]
public class WatchlistController : ControllerBase
{
    /// <summary>The services whose watchlist the apps can add to, in the order they're shown.</summary>
    public static readonly string[] Services = ["simkl"];

    private static readonly string[] IdKeys = ["tmdb", "imdb", "tvdb", "simkl", "mal", "anidb", "anilist"];

    private readonly AccountStore _store;
    private readonly TokenService _tokens;
    private readonly SimklClient _simkl;
    private readonly DiscoveryService _discovery;
    private readonly HomeService _home;

    public WatchlistController(AccountStore store, TokenService tokens, SimklClient simkl, DiscoveryService discovery, HomeService home)
    {
        _store = store;
        _tokens = tokens;
        _simkl = simkl;
        _discovery = discovery;
        _home = home;
    }

    /// <summary>Whether the title is on each watchlist the person can add to (On is null where it couldn't be told).</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<WatchlistStateDto>> GetState([FromQuery] string mediaType, CancellationToken cancellation)
    {
        if (Caller() is not Guid id)
        {
            return Forbid();
        }

        var kind = Kind(mediaType);
        var ids = IdKeys.Select(k => (k, Request.Query[k].ToString())).Where(p => !string.IsNullOrWhiteSpace(p.Item2))
            .ToDictionary(p => p.k, p => p.Item2.Trim());
        var tracking = await _store.GetAsync(id).ConfigureAwait(false);
        var state = new WatchlistStateDto();
        foreach (var service in Services)
        {
            var linked = service == "simkl" && tracking.Simkl is not null;
            bool? on = null;
            if (linked && kind is not null && ids.Count > 0)
            {
                try
                {
                    on = (await _discovery.WatchlistAsync(id, service, cancellation).ConfigureAwait(false))?.Any(i => DiscoveryService.SameTitle(i, kind, ids));
                }
                catch (Exception ex) when (ex is TrackerException or SignedOutException or HttpRequestException or TaskCanceledException)
                {
                    on = null;
                }
            }

            state.Services.Add(new WatchlistServiceDto { Service = service, Linked = linked, On = on });
        }

        return state;
    }

    [HttpPost("{service}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ActionResult<WatchlistResultDto>> Add([FromRoute] string service, [FromBody] WatchlistTitleDto title, CancellationToken cancellation) =>
        ChangeAsync(service, title, true, cancellation);

    [HttpDelete("{service}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ActionResult<WatchlistResultDto>> Remove([FromRoute] string service, [FromBody] WatchlistTitleDto title, CancellationToken cancellation) =>
        ChangeAsync(service, title, false, cancellation);

    private async Task<ActionResult<WatchlistResultDto>> ChangeAsync(string service, WatchlistTitleDto title, bool add, CancellationToken cancellation)
    {
        if (Caller() is not Guid id)
        {
            return Forbid();
        }

        service = service.ToLowerInvariant();
        if (!Services.Contains(service))
        {
            return Problem($"Aster can't add to a {service} watchlist yet.", statusCode: StatusCodes.Status404NotFound);
        }

        var kind = Kind(title.MediaType);
        var simklIds = SimklClient.Ids(title.Ids);
        if (kind is null || simklIds.Count == 0)
        {
            return Problem("Give the title's kind (movie or tv) and at least one id Simkl knows: tmdb, imdb, tvdb, mal, anidb, anilist or simkl.", statusCode: StatusCodes.Status400BadRequest);
        }

        string token;
        try
        {
            if (await _tokens.TokenAsync(id, _simkl, TimeSpan.FromDays(1)).ConfigureAwait(false) is not string found)
            {
                return Problem("Link Simkl first (Settings ▸ Tracking, or the plugin's page on the dashboard).", statusCode: StatusCodes.Status409Conflict);
            }

            token = found;
        }
        catch (SignedOutException)
        {
            return Problem("Simkl needs linking again.", statusCode: StatusCodes.Status410Gone);
        }

        var body = new JsonObject { ["ids"] = simklIds };
        if (!string.IsNullOrWhiteSpace(title.Title))
        {
            body["title"] = title.Title;
        }

        if (title.Year is int year)
        {
            body["year"] = year;
        }

        var movie = kind == "movie";
        try
        {
            if (!add)
            {
                // Off Plan to Watch only: Simkl's removal takes a title off every list, history and all, so a title
                // that isn't on the watchlist is left alone.
                var list = await _discovery.WatchlistAsync(id, service, cancellation).ConfigureAwait(false);
                if (list?.Any(i => DiscoveryService.SameTitle(i, kind, title.Ids)) != true)
                {
                    return new WatchlistResultDto { Service = service, On = false };
                }
            }

            var done = add
                ? await _simkl.AddToWatchlistAsync(body, movie, token, cancellation).ConfigureAwait(false)
                : await _simkl.RemoveFromListsAsync(body, movie, token, cancellation).ConfigureAwait(false);
            if (!done)
            {
                return Problem("Simkl doesn't know this title.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        }
        catch (SignedOutException)
        {
            return Problem("Simkl needs linking again.", statusCode: StatusCodes.Status410Gone);
        }
        catch (Exception ex) when (ex is TrackerException or HttpRequestException or TaskCanceledException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        // The person's lists and Discover page see the change: the next look asks Simkl again.
        _discovery.Forget(id, service);
        _home.Refresh(id);
        return new WatchlistResultDto { Service = service, On = add };
    }

    /// <summary>"movie" or "tv"; null for anything else.</summary>
    private static string? Kind(string? mediaType) => mediaType?.Trim().ToLowerInvariant() switch
    {
        "movie" => "movie",
        "tv" or "show" or "series" => "tv",
        _ => null,
    };

    /// <summary>The signed-in person; an API key, which has none, can't.</summary>
    private Guid? Caller() =>
        Guid.TryParse(User.FindFirstValue("Jellyfin-UserId"), out var id) && id != Guid.Empty ? id : null;
}

/// <summary>A title to put on a watchlist or take off it, as Discover's items describe it.</summary>
public class WatchlistTitleDto
{
    /// <summary>"movie" or "tv".</summary>
    public string MediaType { get; set; } = "tv";

    public bool Anime { get; set; }

    /// <summary>"tmdb", "imdb", "tvdb", "mal", "anidb", "simkl": any the app knows.</summary>
    public Dictionary<string, string> Ids { get; set; } = new();

    public string? Title { get; set; }

    public int? Year { get; set; }
}

public class WatchlistResultDto
{
    public string Service { get; set; } = string.Empty;

    /// <summary>Whether the title is on the watchlist now.</summary>
    public bool On { get; set; }
}

public class WatchlistStateDto
{
    public List<WatchlistServiceDto> Services { get; set; } = new();
}

public class WatchlistServiceDto
{
    public string Service { get; set; } = string.Empty;

    public bool Linked { get; set; }

    /// <summary>Whether the title's on it; null when not linked or it couldn't be told.</summary>
    public bool? On { get; set; }
}
