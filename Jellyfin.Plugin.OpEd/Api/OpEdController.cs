using System.Net.Mime;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.OpEd.Themes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OpEd.Api;

/// <summary>
/// Client API. Every endpoint needs a signed-in user (or an API key), and a user only gets themes for shows they can
/// see in Jellyfin.
///
///   GET  /OpEd/Items/{itemId}/Themes   themes for a show, season or episode (?type=OP|ED to filter)
///   GET  /OpEd/Themes/{themeId}         one theme
///   GET  /OpEd/Themes/{themeId}/Video   the video file (supports HTTP range requests, so clients can seek)
///   GET  /OpEd/Themes/{themeId}/Audio   the audio file
///   POST /OpEd/Scan                     rescan the themes folder (admins)
///   GET  /OpEd/Status                   last scan report (admins)
/// </summary>
[ApiController]
[Route("OpEd")]
[Authorize]
public sealed partial class OpEdController : ControllerBase
{
    private readonly ThemeIndex _index;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public OpEdController(ThemeIndex index, ILibraryManager libraryManager, IUserManager userManager)
    {
        _index = index;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    [HttpGet("Items/{itemId}/Themes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ThemeListDto> GetThemesForItem([FromRoute] Guid itemId, [FromQuery] string? type)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !CanSee(item))
        {
            return NotFound();
        }

        Guid? seriesId = item switch
        {
            Series s => s.Id,
            Season s => s.SeriesId,
            Episode e => e.SeriesId,
            _ => null,
        };
        if (seriesId is null)
        {
            return new ThemeListDto { ItemId = itemId };
        }

        Guid? seasonId = item switch
        {
            Season s => s.Id,
            Episode e => e.SeasonId,
            _ => null,
        };
        int? episodeNumber = item is Episode ep ? ep.IndexNumber : null;

        var themes = _index.ForSeries(seriesId.Value);
        if (type is not null)
        {
            themes = themes.Where(t => string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase));
        }

        // For a season or an episode: that season's themes, plus show-wide themes whose season isn't known.
        if (seasonId is not null)
        {
            themes = themes.Where(t => t.SeasonId is null || t.SeasonId == seasonId);
        }

        return new ThemeListDto
        {
            ItemId = itemId,
            SeriesId = seriesId,
            Themes = [.. themes.Select(t => ToDto(t, episodeNumber is int n && t.SeasonId == seasonId ? PlaysIn(t.Episodes, n) : null))],
        };
    }

    [HttpGet("Themes/{themeId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ThemeDto> GetTheme([FromRoute] string themeId)
    {
        var theme = VisibleTheme(themeId);
        return theme is null ? NotFound() : ToDto(theme, null);
    }

    [HttpGet("Themes/{themeId}/Video")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetVideo([FromRoute] string themeId) => Stream(VisibleTheme(themeId)?.VideoPath);

    [HttpGet("Themes/{themeId}/Audio")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetAudio([FromRoute] string themeId) => Stream(VisibleTheme(themeId)?.AudioPath);

    [HttpPost("Scan")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ScanReport>> Scan(CancellationToken ct) =>
        await _index.ScanAsync(null, ct).ConfigureAwait(false);

    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Status() => new { Scanning = _index.IsScanning, Report = _index.Report };

    private Theme? VisibleTheme(string themeId)
    {
        var theme = _index.Get(themeId);
        if (theme?.SeriesId is not Guid seriesId)
        {
            return null;
        }

        var series = _libraryManager.GetItemById(seriesId);
        return series is not null && CanSee(series) ? theme : null;
    }

    /// <summary>API keys (no user) see everything; users see what their library access allows.</summary>
    private bool CanSee(BaseItem item)
    {
        var claim = User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value;
        if (!Guid.TryParse(claim, out var userId) || userId == Guid.Empty)
        {
            return User.Claims.Any(c => c.Type == "Jellyfin-IsApiKey" && c.Value == "True");
        }

        var user = _userManager.GetUserById(userId);
        return user is not null && item.IsVisibleStandalone(user);
    }

    private ActionResult Stream(string? path)
    {
        if (path is null || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, ContentTypeFor(path), enableRangeProcessing: true);
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".ogg" or ".opus" => "audio/ogg",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" or ".aac" => "audio/mp4",
        ".wav" => "audio/wav",
        _ => MediaTypeNames.Application.Octet,
    };

    /// <summary>Whether episode n is in an AnimeThemes episode list like "1-14, 16"; null when the list is unknown.</summary>
    private static bool? PlaysIn(string? episodes, int n)
    {
        if (string.IsNullOrWhiteSpace(episodes))
        {
            return null;
        }

        foreach (Match m in EpisodeRange().Matches(episodes))
        {
            var from = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var to = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : from;
            if (n >= from && n <= to)
            {
                return true;
            }
        }

        return false;
    }

    private static ThemeDto ToDto(Theme t, bool? playsInEpisode) => new()
    {
        Id = t.Id,
        Type = t.Type,
        Sequence = t.Sequence,
        Version = t.Version,
        Variant = t.Variant.Length > 0 ? t.Variant : null,
        Slug = t.Slug,
        SeriesId = t.SeriesId,
        SeasonId = t.SeasonId,
        AniDbId = t.AniDbId,
        SongTitle = t.SongTitle,
        Artists = t.Artists,
        Episodes = t.Episodes,
        PlaysInEpisode = playsInEpisode,
        Spoiler = t.Spoiler,
        Nsfw = t.Nsfw,
        HasVideo = t.VideoPath is not null,
        HasAudio = t.AudioPath is not null,
        VideoUrl = t.VideoPath is not null ? $"/OpEd/Themes/{t.Id}/Video" : null,
        AudioUrl = t.AudioPath is not null ? $"/OpEd/Themes/{t.Id}/Audio" : null,
        VideoContainer = t.VideoPath is not null ? Path.GetExtension(t.VideoPath).TrimStart('.').ToLowerInvariant() : null,
        AudioContainer = t.AudioPath is not null ? Path.GetExtension(t.AudioPath).TrimStart('.').ToLowerInvariant() : null,
        MetadataSource = t.MetadataSource,
    };

    [GeneratedRegex(@"(\d+)\s*(?:-\s*(\d+))?")]
    private static partial Regex EpisodeRange();
}

public sealed class ThemeListDto
{
    public Guid ItemId { get; set; }

    public Guid? SeriesId { get; set; }

    public List<ThemeDto> Themes { get; set; } = [];
}

public sealed class ThemeDto
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public int Sequence { get; set; }

    public int Version { get; set; }

    public string? Variant { get; set; }

    public string Slug { get; set; } = string.Empty;

    public Guid? SeriesId { get; set; }

    public Guid? SeasonId { get; set; }

    public int? AniDbId { get; set; }

    public string? SongTitle { get; set; }

    public List<string> Artists { get; set; } = [];

    public string? Episodes { get; set; }

    /// <summary>Only for episode requests: whether AnimeThemes lists this theme for that episode.</summary>
    public bool? PlaysInEpisode { get; set; }

    public bool Spoiler { get; set; }

    public bool Nsfw { get; set; }

    public bool HasVideo { get; set; }

    public bool HasAudio { get; set; }

    public string? VideoUrl { get; set; }

    public string? AudioUrl { get; set; }

    public string? VideoContainer { get; set; }

    public string? AudioContainer { get; set; }

    public string? MetadataSource { get; set; }
}
