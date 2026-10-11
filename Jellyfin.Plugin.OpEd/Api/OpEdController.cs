using System.Net.Mime;
using Jellyfin.Plugin.OpEd.Themes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
///   GET  /OpEd/Themes/{themeId}/Video   the original video file (supports HTTP range requests, so clients can seek)
///   GET  /OpEd/Themes/{themeId}/VideoMp4  the same video as MP4 (H.264/HEVC + AAC) that every client can play
///   GET  /OpEd/Themes/{themeId}/Audio   the original audio file
///   GET  /OpEd/Themes/{themeId}/AudioM4a  the same audio as AAC (or MP3) that every client can play
///   POST /OpEd/Scan                     rescan the themes folder (admins)
///   GET  /OpEd/Admin/Libraries             TV libraries the clip search can be limited to (admins)
///   POST /OpEd/Admin/Clips/Extract         cut missing OPs/EDs out of episodes by chapter marker, into the review queue (admins)
///   GET  /OpEd/Admin/Clips                 the review queue (admins)
///   GET  /OpEd/Admin/Clips/{id}/Video      a queued clip (also /Audio) (admins)
///   POST /OpEd/Admin/Clips/{id}/Approve    move the clip into the themes folder (admins)
///   POST /OpEd/Admin/Clips/Apply           index approved clips and fetch their AnimeThemes metadata, in one batch (admins)
///   POST /OpEd/Admin/Clips/{id}/Reject     delete the clip and don't cut that slot again; ?tryAnother=true cuts it from the next episode instead (admins)
///   DELETE /OpEd/Admin/Clips/Rejected      forget rejections (admins)
///   GET  /OpEd/Status                   last scan report (admins)
///   GET  /OpEd/Admin/Themes             every theme with its song details, for the settings page editor (admins)
///   POST /OpEd/Admin/Themes/{id}/Details   save song details to the show's themes.json (admins)
///   DELETE /OpEd/Admin/Themes/{id}/Details remove them again, falling back to the file name / AnimeThemes (admins)
/// </summary>
[ApiController]
[Route("OpEd")]
[Authorize]
public sealed class OpEdController : ControllerBase
{
    private readonly ThemeIndex _index;
    private readonly ClipExtractor _clips;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public OpEdController(ThemeIndex index, ClipExtractor clips, ILibraryManager libraryManager, IUserManager userManager)
    {
        _index = index;
        _clips = clips;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    [HttpGet("Items/{itemId}/Themes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ThemeListDto> GetThemesForItem([FromRoute] Guid itemId, [FromQuery] string? type)
    {
        var item = itemId == Guid.Empty ? null : _libraryManager.GetItemById(itemId);
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
            Themes = [.. themes.Select(t => ToDto(t, episodeNumber is int n && t.SeasonId == seasonId ? EpisodeRanges.PlaysIn(t.Episodes, n) : null))],
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

    [HttpGet("Themes/{themeId}/VideoMp4")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetVideoMp4([FromRoute] string themeId)
    {
        var theme = VisibleTheme(themeId);
        return theme?.Mp4Status == "ready" ? Stream(theme.Mp4Path) : NotFound();
    }

    [HttpGet("Themes/{themeId}/Audio")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetAudio([FromRoute] string themeId) => Stream(VisibleTheme(themeId)?.AudioPath);

    [HttpGet("Themes/{themeId}/AudioM4a")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetAudioM4a([FromRoute] string themeId)
    {
        var theme = VisibleTheme(themeId);
        return theme?.M4aStatus == "ready" ? Stream(theme.M4aPath) : NotFound();
    }

    [HttpPost("Scan")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ScanReport>> Scan(CancellationToken ct)
    {
        var report = await _index.ScanAsync(null, ct).ConfigureAwait(false);

        // Converting can take minutes; run it after the response, not tied to this request.
        _ = Task.Run(() => _index.ConvertVideosAsync(null, CancellationToken.None), CancellationToken.None);
        return report;
    }

    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Status() => new { Scanning = _index.IsScanning, Converting = _index.IsConverting, Extracting = _clips.IsBusy, Report = _index.Report };

    [HttpGet("Admin/Themes")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<EditorThemeDto>> GetEditorThemes()
    {
        var names = new Dictionary<Guid, string?>();
        string? NameOf(Guid? id)
        {
            if (id is not Guid g)
            {
                return null;
            }

            if (!names.TryGetValue(g, out var name))
            {
                names[g] = name = _libraryManager.GetItemById(g)?.Name;
            }

            return name;
        }

        return _index.All
            .OrderBy(t => NameOf(t.SeriesId) ?? t.ShowFolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.DetailsKey, StringComparer.OrdinalIgnoreCase)
            .Select(t => new EditorThemeDto
            {
                Id = t.Id,
                Show = NameOf(t.SeriesId) ?? t.ShowFolder,
                Season = NameOf(t.SeasonId),
                Key = t.DetailsKey,
                Title = t.SongTitle,
                Artists = t.Artists,
                Episodes = t.Episodes,
                Spoiler = t.Spoiler,
                Nsfw = t.Nsfw,
                Source = t.MetadataSource,
                HasVideo = t.VideoPath is not null,
                HasAudio = t.AudioPath is not null,
            })
            .ToList();
    }

    [HttpPost("Admin/Themes/{themeId}/Details")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ThemeDto>> SaveDetails([FromRoute] string themeId, [FromBody] DetailsDto body, CancellationToken ct)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var artists = body.Artists?.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        var details = new ThemeDetailsFile.Details(
            Clean(body.Title),
            artists is { Count: > 0 } ? artists : null,
            Clean(body.Episodes),
            body.Spoiler,
            body.Nsfw);
        var theme = await _index.SaveDetailsAsync(themeId, details, ct).ConfigureAwait(false);
        return theme is null ? NotFound() : ToDto(theme, null);
    }

    [HttpDelete("Admin/Themes/{themeId}/Details")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ThemeDto>> ClearDetails([FromRoute] string themeId, CancellationToken ct)
    {
        var theme = await _index.SaveDetailsAsync(themeId, new ThemeDetailsFile.Details(null, null, null, null, null), ct).ConfigureAwait(false);
        return theme is null ? NotFound() : ToDto(theme, null);
    }

    [HttpGet("Admin/Libraries")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<LibraryDto>> GetLibraries() =>
        _libraryManager.GetVirtualFolders()
            .Where(v => v.CollectionType is null or CollectionTypeOptions.tvshows)
            .Select(v => new LibraryDto { Id = v.ItemId ?? string.Empty, Name = v.Name ?? string.Empty })
            .Where(v => v.Id.Length > 0)
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [HttpPost("Admin/Clips/Extract")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult ExtractClips()
    {
        // Re-encoding takes a while; run it after the response. A run already in progress makes this a no-op.
        _ = Task.Run(() => _clips.ExtractAsync(null, CancellationToken.None), CancellationToken.None);
        return Accepted();
    }

    [HttpGet("Admin/Clips")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ClipListDto> GetClips() => new ClipListDto
    {
        Extracting = _clips.IsBusy,
        Activity = _clips.Activity,
        SeasonsDone = _clips.SeasonsDone,
        SeasonsTotal = _clips.SeasonsTotal,
        CutThisRun = _clips.CutThisRun,
        LastResult = _clips.LastResult,
        Rejected = _clips.RejectedCount,
        ApprovedWaiting = _clips.ApprovedWaiting,
        Indexing = _index.IsScanning,
        Clips = [.. _clips.Pending.OrderBy(c => c.Show, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Sub, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Slug, StringComparer.OrdinalIgnoreCase)],
    };

    [HttpGet("Admin/Clips/{clipId}/Video")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetClipVideo([FromRoute] string clipId) => _clips.Get(clipId) is { } clip ? Stream(_clips.VideoFile(clip)) : NotFound();

    [HttpGet("Admin/Clips/{clipId}/Audio")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetClipAudio([FromRoute] string clipId) => _clips.Get(clipId) is { } clip ? Stream(_clips.AudioFile(clip)) : NotFound();

    [HttpPost("Admin/Clips/{clipId}/Approve")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ApproveClip([FromRoute] string clipId)
    {
        if (_clips.Get(clipId) is null)
        {
            return NotFound();
        }

        // Only moves the files. Indexing (and the AnimeThemes metadata match) happens once for the whole batch: Admin/Clips/Apply.
        return _clips.Approve(clipId) is { } problem ? Conflict(new { Message = problem }) : Ok();
    }

    [HttpPost("Admin/Clips/Apply")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult ApplyApprovedClips()
    {
        _ = Task.Run(() => _clips.ApplyApprovedAsync(CancellationToken.None), CancellationToken.None);
        return Accepted();
    }

    [HttpPost("Admin/Clips/{clipId}/Reject")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RejectClip([FromRoute] string clipId, [FromQuery] bool tryAnother = false)
    {
        if (_clips.Reject(clipId, tryAnother) is not { } slot)
        {
            return NotFound();
        }

        if (tryAnother)
        {
            // Cut the same slot from the next episode that has a usable chapter, after the response.
            _ = Task.Run(() => _clips.ExtractAsync(null, CancellationToken.None, slot), CancellationToken.None);
        }

        return Ok();
    }

    [HttpDelete("Admin/Clips/Rejected")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ClearRejectedClips()
    {
        _clips.ClearRejected();
        return Ok();
    }

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
        VideoMp4Url = t.Mp4Status == "ready" ? $"/OpEd/Themes/{t.Id}/VideoMp4" : null,
        VideoMp4Status = t.Mp4Status,
        AudioUrl = t.AudioPath is not null ? $"/OpEd/Themes/{t.Id}/Audio" : null,
        AudioM4aUrl = t.M4aStatus == "ready" ? $"/OpEd/Themes/{t.Id}/AudioM4a" : null,
        AudioM4aStatus = t.M4aStatus,
        VideoContainer = t.VideoPath is not null ? Path.GetExtension(t.VideoPath).TrimStart('.').ToLowerInvariant() : null,
        AudioContainer = t.AudioPath is not null ? Path.GetExtension(t.AudioPath).TrimStart('.').ToLowerInvariant() : null,
        MetadataSource = t.MetadataSource,
    };
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

    /// <summary>The video as MP4 (H.264/HEVC + AAC) that every client, including Apple TV, can play. Set once it's ready.</summary>
    public string? VideoMp4Url { get; set; }

    /// <summary>"pending", "converting", "ready" or "failed"; null when the theme has no video.</summary>
    public string? VideoMp4Status { get; set; }

    public string? AudioUrl { get; set; }

    /// <summary>The audio as AAC (or MP3, when the original already is) that every client, including Apple TV, can play. Set once it's ready.</summary>
    public string? AudioM4aUrl { get; set; }

    /// <summary>"pending", "converting", "ready" or "failed"; null when the theme has no audio.</summary>
    public string? AudioM4aStatus { get; set; }

    public string? VideoContainer { get; set; }

    public string? AudioContainer { get; set; }

    public string? MetadataSource { get; set; }
}

public sealed class EditorThemeDto
{
    public string Id { get; set; } = string.Empty;

    public string Show { get; set; } = string.Empty;

    public string? Season { get; set; }

    /// <summary>Key in the show's themes.json, e.g. "OP1" or "Season 2/OP1".</summary>
    public string Key { get; set; } = string.Empty;

    public string? Title { get; set; }

    public List<string> Artists { get; set; } = [];

    public string? Episodes { get; set; }

    public bool Spoiler { get; set; }

    public bool Nsfw { get; set; }

    /// <summary>Where the current details come from: "themes.json", "filename", "animethemes" or null.</summary>
    public string? Source { get; set; }

    public bool HasVideo { get; set; }

    public bool HasAudio { get; set; }
}

/// <summary>Song details sent by the editor. Empty values are not saved (they fall through to other sources).</summary>
public sealed class DetailsDto
{
    public string? Title { get; set; }

    public List<string>? Artists { get; set; }

    public string? Episodes { get; set; }

    public bool? Spoiler { get; set; }

    public bool? Nsfw { get; set; }
}

public sealed class ClipListDto
{
    public bool Extracting { get; set; }

    /// <summary>While extracting: what it is doing right now.</summary>
    public string Activity { get; set; } = string.Empty;

    public int SeasonsDone { get; set; }

    public int SeasonsTotal { get; set; }

    public int CutThisRun { get; set; }

    /// <summary>Outcome of the last finished run.</summary>
    public string LastResult { get; set; } = string.Empty;

    /// <summary>Slots whose clips were rejected and won't be cut again.</summary>
    public int Rejected { get; set; }

    /// <summary>Approved clips not yet indexed; "Apply" indexes them all in one scan and matches AnimeThemes metadata.</summary>
    public int ApprovedWaiting { get; set; }

    public bool Indexing { get; set; }

    public List<PendingClip> Clips { get; set; } = [];
}

public sealed class LibraryDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}
