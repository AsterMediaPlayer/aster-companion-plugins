using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.OpEd.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpEd.Themes;

/// <summary>
/// The themes found in the themes folder, matched to shows and seasons, with metadata. Rebuilt by <see cref="ScanAsync"/>
/// (scheduled task, or POST /OpEd/Scan) and kept in memory plus a JSON file in the plugin's data folder so a restart
/// doesn't need a rescan.
///
/// Folder layout (under the configured themes folder):
///   Show Folder/OP1.webm, OP1.ogg, ED1.webm …        themes for the show; season worked out from metadata
///   Show Folder/Season 2/OP1.webm                     only the AniDB entries in that library season folder
///   Show Folder/anidb-18886/OP1.webm                  exactly that AniDB entry
/// A video and an audio file with the same OP/ED identity in the same folder are one theme.
/// </summary>
public sealed partial class ThemeIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ThemeIndex> _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _saveLock = new();
    private readonly VideoConverter _converter;
    private readonly LibraryPublisher _publisher;
    private ThemeIndexData _data = new();
    private bool _loaded;

    public ThemeIndex(ILibraryManager libraryManager, IHttpClientFactory httpClientFactory, IMediaEncoder mediaEncoder, LibraryPublisher publisher, ILogger<ThemeIndex> logger)
    {
        _libraryManager = libraryManager;
        _publisher = publisher;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _converter = new VideoConverter(mediaEncoder, logger);
    }

    private static string DataDir => Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "oped");

    private static string IndexFile => Path.Combine(DataDir, "index.json");

    public ScanReport Report => Data.Report;

    public bool IsScanning => _scanLock.CurrentCount == 0;

    public bool IsConverting => _converter.IsBusy;

    /// <summary>
    /// Makes playable MP4 copies of the videos that need one (see <see cref="VideoConverter"/>), then brings the published
    /// libraries up to date with them.
    /// </summary>
    public async Task ConvertVideosAsync(IProgress<double>? progress, CancellationToken ct)
    {
        await _converter.ConvertPendingAsync([.. Data.Themes], SaveCurrent, progress, ct).ConfigureAwait(false);
        _publisher.QueueSync([.. Data.Themes]);
    }

    /// <summary>Brings the published libraries up to date now (the settings page's "Sync now").</summary>
    public Task PublishAsync(CancellationToken ct) => _publisher.SyncAsync([.. Data.Themes], refresh: true, ct);

    /// <summary>Creates the published libraries in Jellyfin; returns a problem, or null.</summary>
    public Task<string?> CreateLibrariesAsync() => _publisher.CreateLibrariesAsync([.. Data.Themes]);

    private ThemeIndexData Data
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                try
                {
                    if (File.Exists(IndexFile))
                    {
                        _data = JsonSerializer.Deserialize<ThemeIndexData>(File.ReadAllText(IndexFile)) ?? new();
                    }
                }
                catch (Exception e) when (e is IOException or JsonException)
                {
                    _logger.LogWarning(e, "OP/ED: couldn't read the saved index; a rescan will rebuild it");
                }
            }

            return _data;
        }
    }

    public Theme? Get(string id) => Data.Themes.Find(t => t.Id == id);

    public IReadOnlyList<Theme> All => Data.Themes;

    /// <summary>
    /// Writes song details for one theme into its show's themes.json (an empty <paramref name="details"/> removes the
    /// entry), then rescans without contacting AnimeThemes so the change shows straight away.
    /// </summary>
    public async Task<Theme?> SaveDetailsAsync(string themeId, ThemeDetailsFile.Details details, CancellationToken ct)
    {
        var theme = Get(themeId);
        var root = (Plugin.Instance?.Configuration.ThemesPath ?? string.Empty).Trim();
        if (theme is null || root.Length == 0)
        {
            return null;
        }

        ThemeDetailsFile.Save(Path.Combine(root, theme.ShowFolder), theme.DetailsKey, details);
        await ScanAsync(null, ct, allowRemote: false).ConfigureAwait(false);
        return Get(themeId);
    }

    private volatile bool _rescanning;

    /// <summary>True while a rescan of replaced files (see <see cref="StartRescan"/>) is running.</summary>
    public bool IsRescanning => _rescanning;

    /// <summary>
    /// For files you added or replaced by hand: rescans the themes folder (without contacting AnimeThemes), makes whatever
    /// the given themes need playable, and puts the playable file in the themes folder in place of the original, which is
    /// moved to an "_originals" folder next to it. Nothing is deleted. Runs in the background; poll <see cref="IsRescanning"/>.
    /// </summary>
    public void StartRescan(IReadOnlyCollection<string> themeIds)
    {
        if (_rescanning)
        {
            return;
        }

        _rescanning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await RescanFilesAsync(themeIds, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "OP/ED: the rescan of replaced files failed");
            }
            finally
            {
                _rescanning = false;
            }
        });
    }

    private async Task RescanFilesAsync(IReadOnlyCollection<string> themeIds, CancellationToken ct)
    {
        await ScanAsync(null, ct, allowRemote: false).ConfigureAwait(false);
        await ConvertVideosAsync(null, ct).ConfigureAwait(false); // makes the playable copies; files that already play are marked ready as they are
        var placed = 0;
        foreach (var id in themeIds.Distinct())
        {
            if (Get(id) is { } theme)
            {
                placed += PlaceInFolder(theme.VideoPath, theme.Mp4Path, theme.Mp4Status) + PlaceInFolder(theme.AudioPath, theme.M4aPath, theme.M4aStatus);
            }
        }

        if (placed > 0)
        {
            await ScanAsync(null, ct, allowRemote: false).ConfigureAwait(false);
            await ConvertVideosAsync(null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Moves a playable copy made in the plugin's cache next to its source, under the source's name with the copy's extension,
    /// and the source into "_originals". Returns 1 when something was placed. Leaves everything as it was on a problem.
    /// </summary>
    private int PlaceInFolder(string? source, string? copy, string? status)
    {
        if (source is null || copy is null || status != "ready" || string.Equals(copy, source, StringComparison.Ordinal)
            || !copy.StartsWith(VideoConverter.CacheDir, StringComparison.Ordinal) || !File.Exists(source) || !File.Exists(copy))
        {
            return 0;
        }

        var dir = Path.GetDirectoryName(source)!;
        var target = Path.Combine(dir, Path.GetFileNameWithoutExtension(source) + Path.GetExtension(copy));
        if (!string.Equals(target, source, StringComparison.Ordinal) && File.Exists(target))
        {
            _logger.LogWarning("OP/ED: {Target} already exists next to {Source}; left both as they are", target, source);
            return 0;
        }

        var originals = Path.Combine(dir, OriginalsFolder);
        var kept = Path.Combine(originals, Path.GetFileName(source));
        if (File.Exists(kept))
        {
            kept = Path.Combine(originals, Path.GetFileNameWithoutExtension(source) + "-" + DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + Path.GetExtension(source));
        }

        var tmp = target + ".oped-tmp";
        try
        {
            Directory.CreateDirectory(originals);
            File.Copy(copy, tmp, overwrite: true);
            File.Move(source, kept);
            File.Move(tmp, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            VideoConverter.TryDelete(tmp);
            if (!File.Exists(source) && File.Exists(kept))
            {
                File.Move(kept, source);
            }

            _logger.LogWarning(e, "OP/ED: couldn't put the playable copy of {Source} in the themes folder; left as it was", source);
            return 0;
        }

        VideoConverter.TryDelete(copy);
        _logger.LogInformation("OP/ED: {Target} now plays as it is; the original went to {Kept}", target, kept);
        return 1;
    }

    /// <summary>Folders with this name (or any starting with an underscore) inside a show's folder are ignored by scans.</summary>
    public const string OriginalsFolder = "_originals";

    public IEnumerable<Theme> ForSeries(Guid seriesId) =>
        Data.Themes.Where(t => t.SeriesId == seriesId)
            .OrderBy(t => t.SeasonId is null ? 1 : 0).ThenBy(t => t.AniDbId).ThenBy(t => t.Type == "OP" ? 0 : 1)
            .ThenBy(t => t.Sequence).ThenBy(t => t.Version).ThenBy(t => t.Variant, StringComparer.Ordinal);

    /// <param name="allowRemote">False skips AnimeThemes requests (cached answers are still used), e.g. after an edit.</param>
    public async Task<ScanReport> ScanAsync(IProgress<double>? progress, CancellationToken ct, bool allowRemote = true)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var report = new ScanReport();
            var themes = new List<Theme>();
            var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
            var root = config.ThemesPath.Trim();
            if (root.Length == 0 || !Directory.Exists(root))
            {
                report.Error = root.Length == 0 ? "No themes folder is set." : $"Themes folder not found: {root}";
                return Save(themes, report);
            }

            // 1. Files -> themes, grouped by (show folder, subfolder, OP/ED identity).
            var groups = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);
            var fromFileNames = new Dictionary<Theme, ThemeFileName.Parsed>();
            var fromDetailsFiles = new Dictionary<string, Dictionary<string, ThemeDetailsFile.Details>>(StringComparer.OrdinalIgnoreCase);
            foreach (var showDir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var showFolder = Path.GetFileName(showDir);
                try
                {
                    fromDetailsFiles[showFolder] = ThemeDetailsFile.Load(showDir);
                }
                catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
                {
                    report.UnparsedFiles.Add($"{Path.Combine(showFolder, ThemeDetailsFile.FileName)}  (couldn't read it: {e.Message})");
                }

                foreach (var file in Directory.EnumerateFiles(showDir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 }))
                {
                    if (!ThemeFileName.IsVideo(file) && !ThemeFileName.IsAudio(file))
                    {
                        continue;
                    }

                    if (Path.GetRelativePath(showDir, file).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('_')))
                    {
                        continue; // "_originals" and the like: kept files, not themes
                    }

                    var rel = Path.GetRelativePath(root, file);
                    var parsed = ThemeFileName.Parse(Path.GetFileName(file));
                    if (parsed is null)
                    {
                        report.UnparsedFiles.Add(rel);
                        continue;
                    }

                    var (type, seq, ver, variant) = (parsed.Type, parsed.Sequence, parsed.Version, parsed.Variant);

                    var sub = Path.GetDirectoryName(Path.GetRelativePath(showDir, file)) ?? string.Empty;
                    int? folderSeason = null;
                    int? folderAniDb = null;
                    if (sub.Length > 0)
                    {
                        var sm = ShowMap.SeasonFolder().Match(sub);
                        var am = AniDbFolder().Match(sub);
                        if (am.Success)
                        {
                            folderAniDb = int.Parse(am.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else if (sm.Success)
                        {
                            folderSeason = sm.Groups[1].Success ? int.Parse(sm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                        }
                        else
                        {
                            report.UnparsedFiles.Add(rel + "  (subfolder must be \"Season N\" or \"anidb-N\")");
                            continue;
                        }
                    }

                    var key = $"{showFolder}|{sub}|{type}{seq}v{ver}|{variant}";
                    if (!groups.TryGetValue(key, out var theme))
                    {
                        groups[key] = theme = new Theme
                        {
                            Id = StableId(key),
                            Type = type,
                            Sequence = seq,
                            Version = ver,
                            Variant = variant,
                            ShowFolder = showFolder,
                            FolderSeason = folderSeason,
                            FolderAniDb = folderAniDb,
                            AniDbId = folderAniDb,
                            MatchedBy = folderAniDb is null ? null : "folder",
                        };
                        theme.DetailsKey = ThemeDetailsFile.KeyFor(theme);
                    }

                    if ((parsed.Title is not null || parsed.Artists.Count > 0 || parsed.Episodes is not null) && !fromFileNames.ContainsKey(theme))
                    {
                        fromFileNames[theme] = parsed;
                    }

                    if (ThemeFileName.IsVideo(file))
                    {
                        theme.VideoPath ??= file;
                    }
                    else
                    {
                        theme.AudioPath ??= file;
                    }
                }
            }

            // A season picked by hand (themes.json "anidb") wins over the folder and the metadata lookup.
            foreach (var theme in groups.Values)
            {
                if (fromDetailsFiles.GetValueOrDefault(theme.ShowFolder)?.GetValueOrDefault(ThemeDetailsFile.NormalizeKey(theme.DetailsKey))?.AniDb is int picked)
                {
                    theme.AniDbOverride = picked;
                    theme.AniDbId = picked;
                    theme.MatchedBy = "picked";
                }
            }

            // 2. Themes -> Jellyfin shows/seasons, and metadata.
            var map = ShowMap.Build(_libraryManager, _logger);
            var client = new AnimeThemesClient(_httpClientFactory, _logger, Path.Combine(DataDir, "animethemes-cache"));
            var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var done = 0;

            // AnimeThemes asked API users to batch lookups rather than send one request per show, so fetch every AniDB
            // entry this scan could need up front, many ids per request; the per-theme lookups below then read the cache.
            if (config.FetchMetadata)
            {
                // Seasons and shows that Jellyfin knows only by an AniList, MyAnimeList or Kitsu id get their AniDB id from
                // AnimeThemes (one batched lookup per site); answers already cached are used even when not allowed to ask.
                if (allowRemote)
                {
                    await client.ResolveAsync(map.UnresolvedExternals(), config.MetadataRefreshDays, ct).ConfigureAwait(false);
                }

                map.Resolve(e => client.GetResolved(e));

                // Shows with themes but still no AniDB id: search AnimeThemes by the show title (ten shows to a request), and
                // accept the best match only when it is alike enough. Never overrides an id.
                if (config.EnableTitleFallback)
                {
                    var folderOf = groups.Values
                        .Select(t => (Series: map.SeriesFor(t.ShowFolder), t.ShowFolder))
                        .Where(x => x.Series is not null)
                        .GroupBy(x => x.Series!.Value)
                        .ToDictionary(g => g.Key, g => g.First().ShowFolder);
                    var threshold = Math.Clamp(config.TitleMatchThreshold, 50, 100);
                    var queries = map.TitleQueries(folderOf);
                    if (allowRemote)
                    {
                        await client.MatchTitlesAsync(queries, threshold, config.MetadataRefreshDays, ct).ConfigureAwait(false);
                    }

                    foreach (var q in queries)
                    {
                        if (client.GetTitleMatch(q, threshold) is int found)
                        {
                            map.SetTitleMatch(q.SeriesId, found);
                        }
                    }
                }

                // Seasons without an AniDB id (a library not managed by Shokofin): find each one in the show's AnimeThemes
                // franchise by when it started. Only for shows that have themes, and only when the dates line up.
                var themedSeries = groups.Values.Select(t => map.SeriesFor(t.ShowFolder)).Where(g => g is not null).Select(g => g!.Value).ToHashSet();
                if (allowRemote)
                {
                    await client.PrefetchFranchiseAsync(map.SeasonMatchSeeds(themedSeries), config.MetadataRefreshDays, ct).ConfigureAwait(false);
                }

                map.MatchSeasonsByDate(themedSeries, id => client.GetFranchise(id), _logger);
            }

            if (config.FetchMetadata && allowRemote)
            {
                var wanted = new HashSet<int>();
                foreach (var theme in groups.Values)
                {
                    if (theme.AniDbId is int fixedId)
                    {
                        wanted.Add(fixedId);
                    }
                    else if (map.SeriesFor(theme.ShowFolder) is Guid sid)
                    {
                        foreach (var s in map.CandidateSeasons(theme.ShowFolder, sid, theme.FolderSeason))
                        {
                            if (s.AniDbId is int a)
                            {
                                wanted.Add(a);
                            }
                        }
                    }
                }

                await client.PrefetchAsync(wanted, config.MetadataRefreshDays, ct).ConfigureAwait(false);
            }
            foreach (var theme in groups.Values)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(100.0 * done++ / Math.Max(1, groups.Count));
                var seriesId = map.SeriesFor(theme.ShowFolder);
                if (seriesId is null)
                {
                    unmatched.Add(theme.ShowFolder);
                    continue;
                }

                theme.SeriesId = seriesId;
                if (theme.AniDbId is int fixedAniDb)
                {
                    theme.SeasonId = map.SeasonForAniDb(seriesId.Value, fixedAniDb)?.SeasonId;
                }

                if (config.FetchMetadata)
                {
                    AttachMetadata(theme, map, client);
                }

                ApplyLocalDetails(theme, fromFileNames.GetValueOrDefault(theme), fromDetailsFiles.GetValueOrDefault(theme.ShowFolder));

                // Without metadata: a theme whose folder (season folder, or the whole show) maps to a single AniDB entry
                // still gets that entry.
                if (theme.SeasonId is null && theme.AniDbId is null)
                {
                    var candidates = map.CandidateSeasons(theme.ShowFolder, seriesId.Value, theme.FolderSeason)
                        .Where(s => s.AniDbId is not null).ToList();
                    if (candidates.Count == 1)
                    {
                        theme.SeasonId = candidates[0].SeasonId == Guid.Empty ? null : candidates[0].SeasonId;
                        theme.AniDbId = candidates[0].AniDbId;
                        theme.MatchedBy ??= candidates[0].ByDate ? "date" : candidates[0].SeasonId == Guid.Empty && map.MatchedByTitle(seriesId.Value) ? "title" : "only-season";
                    }
                }

                _converter.Classify(theme);
                themes.Add(theme);
                if (theme.MetadataSource is null)
                {
                    report.NoMetadata.Add(Path.Combine(theme.ShowFolder, theme.FolderSeason is int s ? $"Season {s}" : string.Empty, theme.Slug));
                }
            }

            report.UnmatchedFolders = [.. unmatched.Order(StringComparer.OrdinalIgnoreCase)];
            report.Themes = themes.Count;
            report.WithMetadata = themes.Count(t => t.MetadataSource is not null);
            progress?.Report(100);
            _logger.LogInformation(
                "OP/ED: scan found {Themes} themes ({Meta} with metadata) for {Shows} shows; {Unmatched} folders unmatched, {Unparsed} files not recognised",
                report.Themes, report.WithMetadata, themes.Select(t => t.SeriesId).Distinct().Count(), report.UnmatchedFolders.Count, report.UnparsedFiles.Count);
            var saved = Save(themes, report);
            _converter.RemoveUnused(themes);
            _publisher.QueueSync([.. themes]);
            return saved;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "OP/ED: scan failed");
            var report = Data.Report;
            report.Error = e.Message;
            return report;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>
    /// Song details you supplied override AnimeThemes field by field: the file name first, then themes.json on top.
    /// <see cref="Theme.MetadataSource"/> names the most specific source that contributed anything.
    /// </summary>
    private static void ApplyLocalDetails(Theme theme, ThemeFileName.Parsed? fromName, Dictionary<string, ThemeDetailsFile.Details>? detailsFile)
    {
        if (fromName is not null)
        {
            theme.SongTitle = fromName.Title ?? theme.SongTitle;
            theme.Artists = fromName.Artists.Count > 0 ? fromName.Artists : theme.Artists;
            theme.Episodes = fromName.Episodes ?? theme.Episodes;
            theme.MetadataSource = "filename";
        }

        if (detailsFile is not null && detailsFile.TryGetValue(ThemeDetailsFile.NormalizeKey(theme.DetailsKey), out var d) && !d.IsEmpty)
        {
            theme.SongTitle = d.Title ?? theme.SongTitle;
            theme.Artists = d.Artists ?? theme.Artists;
            theme.Episodes = d.Episodes ?? theme.Episodes;
            theme.Spoiler = d.Spoiler ?? theme.Spoiler;
            theme.Nsfw = d.Nsfw ?? theme.Nsfw;
            theme.MetadataSource = ThemeDetailsFile.FileName;
        }
    }

    /// <summary>
    /// Finds the theme on AnimeThemes. Candidate AniDB entries are the folder's fixed one, else the seasons of the
    /// library season folder, else every season of the show, earliest first; the first entry that has this OP/ED
    /// (by slug, then ignoring the variant) wins.
    /// </summary>
    private static void AttachMetadata(Theme theme, ShowMap map, AnimeThemesClient client)
    {
        var candidates = theme.AniDbId is int fixedId
            ? [new ShowMap.SeasonRef(theme.SeasonId ?? Guid.Empty, fixedId, 0)]
            : map.CandidateSeasons(theme.ShowFolder, theme.SeriesId!.Value, theme.FolderSeason);

        foreach (var withVariant in new[] { true, false })
        {
            if (!withVariant && theme.Variant.Length == 0)
            {
                break;
            }

            foreach (var season in candidates)
            {
                if (season.AniDbId is not int aniDb)
                {
                    continue;
                }

                var entries = client.GetThemes(aniDb);
                var slug = withVariant ? theme.Slug : theme.Type + theme.Sequence;
                var matches = entries?.Where(e => string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase)
                    || (withVariant && theme.Variant.Length == 0 && string.Equals(e.Type, theme.Type, StringComparison.OrdinalIgnoreCase) && e.Sequence == theme.Sequence)).ToList();
                if (matches is null || matches.Count == 0)
                {
                    continue;
                }

                // "ED1" also loosely matches "ED1-TV" (same type and number); an exact slug beats that.
                var exact = matches.FindAll(e => string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase));
                var pool = exact.Count > 0 ? exact : matches;
                var entry = pool.Find(e => e.Version == theme.Version) ?? pool[0];
                theme.AniDbId = aniDb;
                theme.MatchedBy ??= season.ByDate ? "date" : season.SeasonId == Guid.Empty && map.MatchedByTitle(theme.SeriesId!.Value) ? "title" : "lookup";
                theme.SeasonId = season.SeasonId == Guid.Empty ? map.SeasonForAniDb(theme.SeriesId!.Value, aniDb)?.SeasonId : season.SeasonId;
                theme.SongTitle = entry.Song;
                theme.Artists = entry.Artists;
                theme.Episodes = entry.Episodes;
                theme.Spoiler = entry.Spoiler;
                theme.Nsfw = entry.Nsfw;
                theme.Overlap = entry.Overlap;
                theme.CreditlessAvailable = entry.Creditless;
                theme.MetadataSource = "animethemes";
                return;
            }
        }
    }

    private ScanReport Save(List<Theme> themes, ScanReport report)
    {
        report.FinishedAt = DateTime.UtcNow;
        _data = new ThemeIndexData { Themes = themes, Report = report };
        _loaded = true;
        SaveCurrent();
        return report;
    }

    private void SaveCurrent()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(IndexFile, JsonSerializer.Serialize(_data, JsonOptions));
            }
            catch (IOException e)
            {
                _logger.LogWarning(e, "OP/ED: couldn't save the index");
            }
        }
    }

    internal static string StableId(string key) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..16].ToLowerInvariant();

    [GeneratedRegex(@"^anidb[-_ ]?(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AniDbFolder();
}
