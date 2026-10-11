using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpEd.Themes;

/// <summary>
/// Cuts missing OPs and EDs out of your own episodes, using their chapter markers, and parks the clips for review.
///
/// For every library season that has an AniDB id (or a season number), the themes AnimeThemes lists for it, or just
/// OP1/ED1 when it lists none, are compared with what the themes folder already holds. For each missing one, the first
/// episode it plays in (per the AnimeThemes episode list) that has a chapter named OP/Opening or ED/Ending of a
/// plausible length is re-encoded to MP4 (H.264 + AAC), plus an M4A of its audio, into the plugin's data folder:
///
///   pending-clips/Show Folder/anidb-18886/OP1.mp4
///
/// The same layout as the themes folder, but outside it, so nothing is served until an admin approves a clip, which
/// moves it into the themes folder (and from then on it is an ordinary theme with the usual metadata). A rejected clip
/// is deleted and remembered so that slot isn't cut again.
/// </summary>
public sealed partial class ClipExtractor
{
    private const double MinSeconds = 30;
    private const double MaxSeconds = 200;
    private const double MaxGenericSeconds = 150;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapterManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ThemeIndex _index;
    private readonly ILogger<ClipExtractor> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _saveLock = new();
    private PendingData? _data;

    public ClipExtractor(
        ILibraryManager libraryManager,
        IChapterManager chapterManager,
        IMediaEncoder mediaEncoder,
        IHttpClientFactory httpClientFactory,
        ThemeIndex index,
        ILogger<ClipExtractor> logger)
    {
        _libraryManager = libraryManager;
        _chapterManager = chapterManager;
        _mediaEncoder = mediaEncoder;
        _httpClientFactory = httpClientFactory;
        _index = index;
        _logger = logger;
    }

    public bool IsBusy => _lock.CurrentCount == 0;

    /// <summary>What a running extraction is doing, for the settings page.</summary>
    public string Activity { get; private set; } = string.Empty;

    public int SeasonsDone { get; private set; }

    public int SeasonsTotal { get; private set; }

    public int CutThisRun { get; private set; }

    /// <summary>Outcome of the last finished run, kept until the next one starts.</summary>
    public string LastResult { get; private set; } = string.Empty;

    private static string DataDir => Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "oped");

    private static string PendingDir => Path.Combine(DataDir, "pending-clips");

    private static string IndexFile => Path.Combine(DataDir, "pending-clips.json");

    private PendingData Data
    {
        get
        {
            if (_data is null)
            {
                try
                {
                    _data = File.Exists(IndexFile) ? JsonSerializer.Deserialize<PendingData>(File.ReadAllText(IndexFile)) ?? new() : new();
                }
                catch (Exception e) when (e is IOException or JsonException)
                {
                    _logger.LogWarning(e, "OP/ED: couldn't read the pending clips list");
                    _data = new();
                }
            }

            return _data;
        }
    }

    public IReadOnlyList<PendingClip> Pending => Data.Clips;

    public PendingClip? Get(string id) => Data.Clips.Find(c => c.Id == id);

    public string? VideoFile(PendingClip clip) => Path.Combine(PendingDir, clip.RelativeDir, clip.Slug + ".mp4");

    public string? AudioFile(PendingClip clip) => Path.Combine(PendingDir, clip.RelativeDir, clip.Slug + ".m4a");

    /// <summary>Moves an approved clip into the themes folder. Returns an error message, or null on success.</summary>
    public string? Approve(string id)
    {
        var clip = Get(id);
        var root = (Plugin.Instance?.Configuration.ThemesPath ?? string.Empty).Trim();
        if (clip is null)
        {
            return "No such clip.";
        }

        if (root.Length == 0 || !Directory.Exists(root))
        {
            return "The themes folder isn't set or doesn't exist.";
        }

        var targetDir = Path.Combine(root, clip.RelativeDir);
        var moves = new[] { VideoFile(clip)!, AudioFile(clip)! }.Where(File.Exists)
            .Select(f => (From: f, To: Path.Combine(targetDir, Path.GetFileName(f)))).ToList();
        if (moves.Find(m => File.Exists(m.To)).To is { } existing)
        {
            return $"{Path.GetRelativePath(root, existing)} already exists in the themes folder.";
        }

        try
        {
            Directory.CreateDirectory(targetDir);
            foreach (var (from, to) in moves)
            {
                File.Move(from, to);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Couldn't move the clip: " + e.Message;
        }

        Data.Clips.Remove(clip);
        Save();
        PruneEmptyDirs();
        return null;
    }

    /// <summary>Deletes a clip and remembers the slot so it isn't cut again.</summary>
    public bool Reject(string id)
    {
        var clip = Get(id);
        if (clip is null)
        {
            return false;
        }

        VideoConverter.TryDelete(VideoFile(clip)!);
        VideoConverter.TryDelete(AudioFile(clip)!);
        Data.Clips.Remove(clip);
        Data.Rejected.Add(clip.SlotKey);
        Save();
        PruneEmptyDirs();
        return true;
    }

    /// <summary>Forgets rejections so rejected slots are looked at again by the next run.</summary>
    public void ClearRejected()
    {
        Data.Rejected.Clear();
        Save();
    }

    public int RejectedCount => Data.Rejected.Count;

    public async Task ExtractAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (!await _lock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        CutThisRun = 0;
        SeasonsDone = 0;
        SeasonsTotal = 0;
        LastResult = string.Empty;
        Activity = "Starting…";
        try
        {
            var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
            var root = config.ThemesPath.Trim();
            if (root.Length == 0 || !Directory.Exists(root))
            {
                _logger.LogInformation("OP/ED: set the themes folder before looking for clips");
                LastResult = "Set the themes folder first.";
                return;
            }

            var seasons = LibrarySeasons();
            SeasonsTotal = seasons.Count;
            var client = new AnimeThemesClient(_httpClientFactory, _logger, Path.Combine(DataDir, "animethemes-cache"));
            if (config.FetchMetadata)
            {
                Activity = $"Asking AnimeThemes about {seasons.Count(s => s.AniDb is not null)} seasons (cached answers are skipped)…";
                await client.PrefetchAsync(seasons.Where(s => s.AniDb is not null).Select(s => s.AniDb!.Value), config.MetadataRefreshDays, ct).ConfigureAwait(false);
            }

            for (var i = 0; i < seasons.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(100.0 * i / Math.Max(1, seasons.Count));
                SeasonsDone = i;
                Activity = $"Checking {seasons[i].ShowFolder} / {seasons[i].Sub}";
                CutThisRun += await ExtractSeasonAsync(seasons[i], client, ct).ConfigureAwait(false);
            }

            progress?.Report(100);
            SeasonsDone = seasons.Count;
            LastResult = $"Checked {seasons.Count} seasons: cut {CutThisRun} new clip(s); {Data.Clips.Count} waiting for review.";
            _logger.LogInformation("OP/ED: chapter scan cut {Count} new clip(s); {Pending} waiting for review", CutThisRun, Data.Clips.Count);
        }
        finally
        {
            Activity = string.Empty;
            _lock.Release();
        }
    }

    private sealed record SeasonInfo(Season Season, string ShowFolder, string Sub, int? AniDb, List<Episode> Episodes);

    private List<SeasonInfo> LibrarySeasons()
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
        }).OfType<Episode>().Where(e => !string.IsNullOrEmpty(e.Path) && e.Season is not null && e.SeriesId != Guid.Empty);

        var result = new List<SeasonInfo>();
        foreach (var group in episodes.GroupBy(e => e.Season.Id))
        {
            var ordered = group.Where(e => e.IndexNumber is not null).OrderBy(e => e.IndexNumber).ToList();
            if (ordered.Count == 0)
            {
                continue;
            }

            var season = ordered[0].Season;
            var folder = ShowMap.ShowFolderOf(ShowMap.RealPath(ordered[0].Path)).Folder;
            var aniDb = ShowMap.AniDbOf(season);
            string? sub = aniDb is int a ? $"anidb-{a}" : season.IndexNumber is > 0 ? $"Season {season.IndexNumber}" : null;
            if (folder is null || sub is null)
            {
                continue;
            }

            result.Add(new SeasonInfo(season, folder, sub, aniDb, ordered));
        }

        return result;
    }

    private sealed record Slot(string Type, int Sequence, string Slug, string? Episodes, string? Song, List<string> Artists);

    private async Task<int> ExtractSeasonAsync(SeasonInfo info, AnimeThemesClient client, CancellationToken ct)
    {
        var slots = new List<Slot>();
        var listed = info.AniDb is int a ? client.GetThemes(a) : null;
        if (listed is { Count: > 0 })
        {
            slots.AddRange(listed.GroupBy(e => e.Slug, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(e => e.Version).First())
                .Where(e => e.Type is "OP" or "ED")
                .Select(e => new Slot(e.Type, e.Sequence, e.Slug, e.Episodes, e.Song, e.Artists)));
        }
        else
        {
            // AnimeThemes has nothing for this season (or isn't being asked): take whichever OP and ED the chapters show.
            slots.Add(new Slot("OP", 1, "OP1", null, null, []));
            slots.Add(new Slot("ED", 1, "ED1", null, null, []));
        }

        var chapters = new Dictionary<Guid, List<Marker>>();
        var cut = 0;
        foreach (var slot in slots)
        {
            ct.ThrowIfCancellationRequested();
            if (IsTaken(info, slot))
            {
                continue;
            }

            foreach (var episode in info.Episodes)
            {
                if (episode.IndexNumber is not int number || EpisodeRanges.PlaysIn(slot.Episodes, number) == false)
                {
                    continue;
                }

                if (!chapters.TryGetValue(episode.Id, out var markers))
                {
                    chapters[episode.Id] = markers = MarkersOf(episode);
                }

                var marker = markers.Find(m => m.Type == slot.Type && (m.Sequence is null || m.Sequence == slot.Sequence));
                if (marker is null)
                {
                    continue;
                }

                if (await CutAsync(info, slot, episode, marker, ct).ConfigureAwait(false))
                {
                    cut++;
                }

                break; // one clip per slot; if the first matching episode fails, a later run tries it again
            }
        }

        return cut;
    }

    /// <summary>Whether this slot already has a theme, a clip waiting for review, or a rejected clip.</summary>
    private bool IsTaken(SeasonInfo info, Slot slot)
    {
        var key = SlotKey(info.ShowFolder, info.Sub, slot.Slug);
        if (Data.Rejected.Contains(key, StringComparer.OrdinalIgnoreCase) || Data.Clips.Exists(c => string.Equals(c.SlotKey, key, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return _index.All.Any(t => t.SeriesId == info.Season.SeriesId
            && t.Type == slot.Type && t.Sequence == slot.Sequence
            && (t.SeasonId is null || t.SeasonId == info.Season.Id || (info.AniDb is not null && t.AniDbId == info.AniDb)));
    }

    private sealed record Marker(string Type, int? Sequence, string Name, double Start, double Seconds);

    /// <summary>The OP/ED chapters of an episode with a plausible length (the chapter runs until the next one starts).</summary>
    private List<Marker> MarkersOf(Episode episode)
    {
        var result = new List<Marker>();
        var chapters = _chapterManager.GetChapters(episode.Id).OrderBy(c => c.StartPositionTicks).ToList();
        for (var i = 0; i < chapters.Count; i++)
        {
            var m = ChapterName().Match(chapters[i].Name ?? string.Empty);
            if (!m.Success)
            {
                continue;
            }

            var endTicks = i + 1 < chapters.Count ? chapters[i + 1].StartPositionTicks : episode.RunTimeTicks;
            if (endTicks is not long end || end <= chapters[i].StartPositionTicks)
            {
                continue;
            }

            var seconds = (end - chapters[i].StartPositionTicks) / (double)TimeSpan.TicksPerSecond;
            if (seconds < MinSeconds || seconds > (m.Groups["gen"].Success ? MaxGenericSeconds : MaxSeconds))
            {
                continue;
            }

            var type = m.Groups["op"].Success ? "OP" : "ED";
            int? sequence = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : null;
            result.Add(new Marker(type, sequence, chapters[i].Name ?? string.Empty, chapters[i].StartPositionTicks / (double)TimeSpan.TicksPerSecond, seconds));
        }

        return result;
    }

    private async Task<bool> CutAsync(SeasonInfo info, Slot slot, Episode episode, Marker marker, CancellationToken ct)
    {
        var dir = Path.Combine(PendingDir, info.ShowFolder, info.Sub);
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, slot.Slug + ".mp4");
        var audio = Path.Combine(dir, slot.Slug + ".m4a");
        var source = episode.Path;
        var audioStream = await PickAudioStreamAsync(source, ct).ConfigureAwait(false);
        var started = System.Diagnostics.Stopwatch.StartNew();

        // -ss before -i seeks to the chapter start; with re-encoding the cut is frame-accurate.
        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-ss", marker.Start.ToString("0.###", CultureInfo.InvariantCulture), "-t", marker.Seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", source, "-map", "0:v:0", "-map", audioStream, "-sn", "-dn", "-map_chapters", "-1",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-vf", "scale='min(1920,iw)':-2", "-threads", "2",
            "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-movflags", "+faststart", "-f", "mp4", video + ".tmp",
        };
        var (code, error) = await VideoConverter.RunAsync(_mediaEncoder.EncoderPath, args, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        if (code != 0 || !File.Exists(video + ".tmp"))
        {
            VideoConverter.TryDelete(video + ".tmp");
            _logger.LogWarning("OP/ED: couldn't cut {Slug} from {File} (exit {Code}): {Error}", slot.Slug, source, code, error.Length > 300 ? error[..300] : error);
            return false;
        }

        File.Move(video + ".tmp", video, overwrite: true);
        var (audioCode, _) = await VideoConverter.RunAsync(_mediaEncoder.EncoderPath,
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", video, "-vn", "-c:a", "copy", "-movflags", "+faststart", "-f", "mp4", audio + ".tmp"],
            TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (audioCode == 0 && File.Exists(audio + ".tmp"))
        {
            File.Move(audio + ".tmp", audio, overwrite: true);
        }
        else
        {
            VideoConverter.TryDelete(audio + ".tmp");
        }

        var clip = new PendingClip
        {
            Id = ThemeIndex.StableId(SlotKey(info.ShowFolder, info.Sub, slot.Slug)),
            SlotKey = SlotKey(info.ShowFolder, info.Sub, slot.Slug),
            ShowFolder = info.ShowFolder,
            Sub = info.Sub,
            Slug = slot.Slug,
            Type = slot.Type,
            Show = info.Season.Series?.Name ?? info.ShowFolder,
            Season = info.Season.Name,
            AniDbId = info.AniDb,
            EpisodeNumber = episode.IndexNumber,
            EpisodeName = episode.Name,
            Chapter = marker.Name,
            StartSeconds = marker.Start,
            DurationSeconds = marker.Seconds,
            SongTitle = slot.Song,
            Artists = slot.Artists,
            Episodes = slot.Episodes,
            HasAudio = File.Exists(audio),
            CreatedAt = DateTime.UtcNow,
        };
        Data.Clips.Add(clip);
        Save();
        _logger.LogInformation("OP/ED: cut {Slug} for {Show} / {Sub} from episode {Episode} in {Seconds:0.0}s", slot.Slug, clip.Show, info.Sub, episode.IndexNumber, started.Elapsed.TotalSeconds);
        return true;
    }

    /// <summary>"0:a:N" for the first Japanese audio stream, else the first one ("0:a:0?" lets silent files through).</summary>
    private async Task<string> PickAudioStreamAsync(string path, CancellationToken ct)
    {
        var (code, output) = await VideoConverter.RunAsync(_mediaEncoder.ProbePath,
            ["-v", "error", "-select_streams", "a", "-show_entries", "stream_tags=language", "-of", "csv=p=0", path],
            TimeSpan.FromMinutes(1), ct, captureStdout: true).ConfigureAwait(false);
        if (code == 0)
        {
            var langs = output.Split('\n', StringSplitOptions.TrimEntries);
            var jp = Array.FindIndex(langs, l => l.Equals("jpn", StringComparison.OrdinalIgnoreCase) || l.Equals("ja", StringComparison.OrdinalIgnoreCase));
            if (jp >= 0)
            {
                return $"0:a:{jp}";
            }
        }

        return "0:a:0?";
    }

    private static string SlotKey(string showFolder, string sub, string slug) => $"{showFolder}|{sub}|{slug}";

    private void PruneEmptyDirs()
    {
        if (!Directory.Exists(PendingDir))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(PendingDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try
                {
                    Directory.Delete(dir);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(IndexFile, JsonSerializer.Serialize(Data, JsonOptions));
            }
            catch (IOException e)
            {
                _logger.LogWarning(e, "OP/ED: couldn't save the pending clips list");
            }
        }
    }

    /// <summary>
    /// "OP", "OP2", "Opening", "ED", "Ending 2"…, and for endings also the generic "Credits", "End Credits", "Outro",
    /// "Closing". Not "Intro" or "Preview". The generic names are only trusted up to <see cref="MaxGenericSeconds"/>.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:(?<op>Opening|OP)|(?<ed>Ending|ED)|(?<gen>End(?:ing)?\s*Credits|Closing(?:\s*Credits)?|Outro|Credits))(?:\s*(?:Theme|Song|Credits))?[\s_-]*(?<n>\d+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterName();
}

/// <summary>A clip cut from an episode that waits for an admin to approve it.</summary>
public sealed class PendingClip
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Show folder | subfolder | slug: one place in the themes folder.</summary>
    public string SlotKey { get; set; } = string.Empty;

    public string ShowFolder { get; set; } = string.Empty;

    /// <summary>"anidb-18886" or "Season 2".</summary>
    public string Sub { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Show { get; set; } = string.Empty;

    public string? Season { get; set; }

    public int? AniDbId { get; set; }

    public int? EpisodeNumber { get; set; }

    public string? EpisodeName { get; set; }

    public string? Chapter { get; set; }

    public double StartSeconds { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>What AnimeThemes says this slot is, so the reviewer can check the clip is the right song.</summary>
    public string? SongTitle { get; set; }

    public List<string> Artists { get; set; } = [];

    public string? Episodes { get; set; }

    public bool HasAudio { get; set; }

    public DateTime CreatedAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string RelativeDir => Path.Combine(ShowFolder, Sub);
}

public sealed class PendingData
{
    public List<PendingClip> Clips { get; set; } = [];

    public List<string> Rejected { get; set; } = [];
}
