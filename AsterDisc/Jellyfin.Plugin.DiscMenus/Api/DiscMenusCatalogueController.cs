using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DiscMenus.Api;

/// <summary>
/// Backend of the catalogue page (a dashboard page). Admin-only. The browser downloads the catalogue's public index
/// and each menu file itself and hands the file to <c>Install</c> with the fingerprint the index promised: this server
/// never fetches anything, and nothing about the library is sent anywhere (<c>Match</c> works on what the browser sends
/// in and answers from the local library).
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("DiscMenus/Catalogue")]
public sealed class DiscMenusCatalogueController : ControllerBase
{
    private const int MaxMatchBytes = 2 * 1024 * 1024;
    private const int MaxMatchQueries = 5000;

    private readonly DiscMenuService _discMenuService;

    public DiscMenusCatalogueController(DiscMenuService discMenuService)
    {
        _discMenuService = discMenuService;
    }

    /// <summary>What was installed from the catalogue, and this plugin's version (a menu may need a newer one).</summary>
    [HttpGet("Installed")]
    public ActionResult GetInstalled() => Ok(new
    {
        PluginVersion = Plugin.Instance!.Version.ToString(3),
        Installed = _discMenuService.CreateCatalogueStore().List()
            .Select(i => new { i.MenuId, i.Revision, i.Sha256, i.EditedLocally, i.File }),
    });

    /// <summary>
    /// Installs (or upgrades) the menu in the request body. <paramref name="sha256"/> is the catalogue index's fingerprint
    /// for the file; if the body doesn't hash to it, nothing is written.
    /// </summary>
    [HttpPost("Install")]
    public async Task<ActionResult> Install([FromQuery] string? sha256)
    {
        var body = await ReadBodyAsync(CatalogueStore.MaxBytes);
        if (body is null)
        {
            return StatusCode(413, new { Error = "This menu is larger than the catalogue allows." });
        }

        var result = _discMenuService.CreateCatalogueStore().Install(body, sha256);
        if (result.Kind == CatalogueResultKind.Ok && result.Action != "unchanged")
        {
            _discMenuService.NotifyFilesChanged();
        }

        return Respond(result);
    }

    /// <summary>Removes a menu that was installed from the catalogue.</summary>
    [HttpDelete("{menuId:guid}")]
    public ActionResult Uninstall(Guid menuId)
    {
        var result = _discMenuService.CreateCatalogueStore().Uninstall(menuId);
        if (result.Kind == CatalogueResultKind.Ok)
        {
            _discMenuService.NotifyFilesChanged();
        }

        return Respond(result);
    }

    /// <summary>
    /// Which of the given catalogue entries are for titles in this library. The body is a JSON array of
    /// <c>{ "menuId", "match": { "itemType", "providerIds": { ... } } }</c>, as the index lists them.
    /// </summary>
    [HttpPost("Match")]
    public async Task<ActionResult> Match()
    {
        var body = await ReadBodyAsync(MaxMatchBytes);
        if (body is null)
        {
            return StatusCode(413, new { Error = "That request is too large." });
        }

        var queries = ParseQueries(body);
        if (queries is null)
        {
            return BadRequest(new { Error = "Expected a JSON array of catalogue entries." });
        }

        var results = CatalogueMatching.Match(queries, _discMenuService.GetLibraryTitles());

        // The matched titles' extras, so the page can say when a menu's special features would have nothing to play.
        var extras = _discMenuService.CountLocalExtras(results.SelectMany(r => r.Titles).Select(t => t.Id));
        return Ok(results.Select(r => new
        {
            r.MenuId,
            r.State,
            Titles = r.Titles.Select(t => new { t.Id, t.Name, t.Year, Extras = extras.GetValueOrDefault(t.Id) }),
        }));
    }

    public static IReadOnlyList<CatalogueMatchQuery>? ParseQueries(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > MaxMatchQueries)
            {
                return null;
            }

            var list = new List<CatalogueMatchQuery>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !e.TryGetProperty("menuId", out var id) || id.ValueKind != JsonValueKind.String
                    || !e.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Object
                    || !match.TryGetProperty("itemType", out var type) || type.ValueKind != JsonValueKind.String
                    || !match.TryGetProperty("providerIds", out var ids) || ids.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var providerIds = new Dictionary<string, string>();
                foreach (var p in ids.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v)
                    {
                        providerIds[p.Name] = v;
                    }
                }

                list.Add(new CatalogueMatchQuery(id.GetString()!, type.GetString()!, providerIds));
            }

            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<byte[]?> ReadBodyAsync(int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    private ActionResult Respond(CatalogueResult r) => r.Kind switch
    {
        CatalogueResultKind.Ok => Ok(new { r.Action, r.Revision }),
        CatalogueResultKind.TooLarge => StatusCode(413, new { Error = r.Message }),
        CatalogueResultKind.Invalid => UnprocessableEntity(new { Errors = r.Errors }),
        CatalogueResultKind.HashMismatch => UnprocessableEntity(new { Error = r.Message }),
        CatalogueResultKind.Refused => Conflict(new { Error = r.Message }),
        CatalogueResultKind.NotFound => NotFound(new { Error = r.Message }),
        _ => BadRequest(new { Error = r.Message }),
    };
}
