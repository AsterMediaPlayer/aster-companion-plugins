using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AsterOpEd.Themes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AsterOpEd.Library;

/// <summary>
/// Publishes the themes as two ordinary Jellyfin libraries, so playlists, favourites, watched state, search and parental
/// controls all come from Jellyfin itself:
///
///   {LibraryPath}/Videos/{Show}/01 OP1 - Song.mp4          a Music Videos library: a link to the playable MP4 (no second
///                                                           copy), a .nfo Jellyfin reads, and a frame as its picture
///   {LibraryPath}/Songs/{Show}/01 OP1 - Song.m4a           a Music library: the song with its tags written inside (Jellyfin's
///                                                           music libraries read tags, not .nfo files), an album per show
///
/// Publishing follows the theme index: after every scan and conversion it brings the folder up to date (only what changed
/// is rewritten; themes that are gone are removed) and asks Jellyfin to rescan just those two libraries. What was
/// published is recorded in library-manifest.json in the plugin's data folder.
/// </summary>
public sealed class LibraryPublisher
{
    private const string VideosFolder = "Videos";
    private const string SongsFolder = "Songs";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LibraryPublisher> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly object _queueLock = new();
    private IReadOnlyList<Theme>? _queued;
    private bool _queueRunning;
    private Dictionary<string, Published>? _manifest;

    public LibraryPublisher(ILibraryManager libraryManager, IMediaEncoder mediaEncoder, IFileSystem fileSystem, ILogger<LibraryPublisher> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public bool IsSyncing => _syncLock.CurrentCount == 0 || _queueRunning;

    public DateTime? LastSync { get; private set; }

    public string? LastError { get; private set; }

    private static Configuration.PluginConfiguration? Config => Plugin.Instance?.Configuration;

    private static string? Root => Config?.LibraryPath is { } p && p.Trim().Length > 0 ? Path.GetFullPath(p.Trim()) : null;

    public static string? VideosDir => Root is { } r ? Path.Combine(r, VideosFolder) : null;

    public static string? SongsDir => Root is { } r ? Path.Combine(r, SongsFolder) : null;

    private static string ManifestFile => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "library-manifest.json");

    private Dictionary<string, Published> Manifest
    {
        get
        {
            if (_manifest is null)
            {
                try
                {
                    _manifest = File.Exists(ManifestFile)
                        ? JsonSerializer.Deserialize<Dictionary<string, Published>>(File.ReadAllText(ManifestFile)) ?? []
                        : [];
                }
                catch (Exception e) when (e is IOException or JsonException)
                {
                    _logger.LogWarning(e, "OP/ED: couldn't read the library manifest; the libraries will be rebuilt");
                    _manifest = [];
                }
            }

            return _manifest;
        }
    }

    // MARK: Problems and status

    /// <summary>Why the libraries can't be published to the chosen folder, or null when they can.</summary>
    public string? Problem()
    {
        if (Root is not { } root)
        {
            return "Choose a folder for the libraries first.";
        }

        var themes = Config?.ThemesPath?.Trim();
        if (!string.IsNullOrEmpty(themes) && Overlaps(root, Path.GetFullPath(themes)))
        {
            return "The libraries' folder can't be inside the themes folder, or hold it.";
        }

        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            foreach (var location in folder.Locations ?? [])
            {
                if (IsOurs(location))
                {
                    continue;
                }

                if (Overlaps(root, Path.GetFullPath(location)))
                {
                    return $"The libraries' folder overlaps the \"{folder.Name}\" library ({location}). Choose a folder outside every other library.";
                }
            }
        }

        return null;
    }

    /// <summary>The Music Videos and Music libraries on the plugin's folders, if they exist.</summary>
    public (string? VideosId, string? SongsId) LibraryIds()
    {
        string? videos = null, songs = null;
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            foreach (var location in folder.Locations ?? [])
            {
                if (SamePath(location, VideosDir))
                {
                    videos ??= folder.ItemId;
                }
                else if (SamePath(location, SongsDir))
                {
                    songs ??= folder.ItemId;
                }
            }
        }

        return (videos, songs);
    }

    public LibraryStatus Status()
    {
        var (videos, songs) = LibraryIds();
        return new LibraryStatus
        {
            VideosLibraryId = videos,
            SongsLibraryId = songs,
            Videos = Manifest.Values.Count(p => p.Video is not null),
            Songs = Manifest.Values.Count(p => p.Song is not null),
            Syncing = IsSyncing,
            LastSync = LastSync,
            LastError = LastError,
            Problem = Problem(),
        };
    }

    // MARK: Lookups for the API

    /// <summary>The Jellyfin items a theme was published as: its music video and song, and its full version's.</summary>
    public (Guid? Video, Guid? Song, Guid? FullVideo, Guid? FullSong) ItemIds(string themeId)
    {
        if (!Manifest.TryGetValue(themeId, out var published))
        {
            return (null, null, null, null);
        }

        Guid? Find(string? path) => path is not null && _libraryManager.FindByPath(path, false) is { } item ? item.Id : null;
        return (Find(published.Video), Find(published.Song), Find(published.FullVideo), Find(published.FullSong));
    }

    /// <summary>The theme a published file belongs to, by the file's path, and whether it's the full version.</summary>
    public (string ThemeId, bool Full)? ThemeForPath(string? path)
    {
        if (path is null)
        {
            return null;
        }

        foreach (var (id, published) in Manifest)
        {
            if (SamePath(path, published.Video) || SamePath(path, published.Song))
            {
                return (id, false);
            }

            if (SamePath(path, published.FullVideo) || SamePath(path, published.FullSong))
            {
                return (id, true);
            }
        }

        return null;
    }

    // MARK: Creating the libraries

    /// <summary>
    /// Adds the Music Videos and Music libraries on the plugin's folders (each only if it isn't there yet), reading the
    /// plugin's .nfo files and tags but fetching nothing from the internet, so nothing overwrites what the plugin wrote.
    /// Returns a problem, or null.
    /// </summary>
    public async Task<string?> CreateLibrariesAsync(IReadOnlyList<Theme> themes)
    {
        if (Problem() is { } problem)
        {
            return problem;
        }

        Directory.CreateDirectory(VideosDir!);
        Directory.CreateDirectory(SongsDir!);
        await SyncAsync(themes, refresh: false, CancellationToken.None).ConfigureAwait(false);

        var (videos, songs) = LibraryIds();
        var config = Config!;
        if (videos is null)
        {
            await _libraryManager.AddVirtualFolder(
                Name(config.VideosLibraryName, "Anime Openings & Endings"),
                CollectionTypeOptions.musicvideos,
                Options(VideosDir!, "MusicVideo"),
                true).ConfigureAwait(false);
        }

        if (songs is null)
        {
            await _libraryManager.AddVirtualFolder(
                Name(config.SongsLibraryName, "Anime Theme Songs"),
                CollectionTypeOptions.music,
                Options(SongsDir!, "MusicArtist", "MusicAlbum", "Audio"),
                true).ConfigureAwait(false);
        }

        return null;

        static string Name(string? chosen, string fallback) => string.IsNullOrWhiteSpace(chosen) ? fallback : chosen.Trim();

        static LibraryOptions Options(string path, params string[] types) => new()
        {
            PathInfos = [new MediaPathInfo(path)],
            EnableRealtimeMonitor = true,
            SaveLocalMetadata = false,
            TypeOptions = [.. types.Select(t => new TypeOptions
            {
                Type = t,
                MetadataFetchers = [],
                MetadataFetcherOrder = [],
                ImageFetchers = [],
                ImageFetcherOrder = [],
            })],
        };
    }

    // MARK: Syncing

    /// <summary>Brings the libraries up to date with <paramref name="themes"/> in the background; calls made while one runs are folded into one more run.</summary>
    public void QueueSync(IReadOnlyList<Theme> themes)
    {
        if (Config?.PublishLibraries != true || Root is null)
        {
            return;
        }

        lock (_queueLock)
        {
            _queued = themes;
            if (_queueRunning)
            {
                return;
            }

            _queueRunning = true;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                IReadOnlyList<Theme>? next;
                lock (_queueLock)
                {
                    next = _queued;
                    _queued = null;
                    if (next is null)
                    {
                        _queueRunning = false;
                        return;
                    }
                }

                try
                {
                    await SyncAsync(next, refresh: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "OP/ED: publishing the libraries failed");
                }
            }
        });
    }

    /// <summary>
    /// Writes what changed: each theme's video link, .nfo and picture, and its song; removes themes that are gone; then,
    /// with <paramref name="refresh"/>, asks Jellyfin to rescan the two libraries.
    /// </summary>
    public async Task SyncAsync(IReadOnlyList<Theme> themes, bool refresh, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Problem() is { } problem)
            {
                LastError = problem;
                return;
            }

            Directory.CreateDirectory(VideosDir!);
            Directory.CreateDirectory(SongsDir!);
            var manifest = Manifest;
            var plans = Plan(themes);
            var changed = 0;
            var errors = new List<string>();

            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                if (manifest.TryGetValue(plan.Theme.Id, out var old) && old.Hash == plan.Hash && old.FilesExist())
                {
                    continue;
                }

                if (old is not null)
                {
                    old.Delete();
                }

                try
                {
                    manifest[plan.Theme.Id] = await PublishAsync(plan, ct).ConfigureAwait(false);
                    changed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
                {
                    manifest.Remove(plan.Theme.Id);
                    errors.Add($"{plan.Show} {plan.Code}: {e.Message}");
                }
            }

            var current = plans.Select(p => p.Theme.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in manifest.Keys.Where(id => !current.Contains(id)).ToList())
            {
                manifest[gone].Delete();
                manifest.Remove(gone);
                changed++;
            }

            AddAlbumArt(plans);
            RemoveEmptyFolders(VideosDir!);
            RemoveEmptyFolders(SongsDir!);
            SaveManifest();
            LastSync = DateTime.UtcNow;
            LastError = errors.Count > 0 ? string.Join("\n", errors.Take(20)) : null;
            _logger.LogInformation("OP/ED: libraries published ({Changed} changed, {Videos} videos, {Songs} songs)",
                changed, manifest.Values.Count(p => p.Video is not null), manifest.Values.Count(p => p.Song is not null));

            if (refresh && changed > 0)
            {
                await RefreshLibrariesAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Everything about how a theme is published, worked out once: names, track number, metadata, and a hash of it all.</summary>
    private List<ThemePlan> Plan(IReadOnlyList<Theme> themes)
    {
        var plans = new List<ThemePlan>();
        foreach (var show in themes.Where(t => t.SeriesId is not null).GroupBy(t => t.SeriesId!.Value))
        {
            var series = _libraryManager.GetItemById(show.Key) as Series;
            var showName = series?.Name ?? show.First().ShowFolder;
            var seasons = show.Select(t => t.SeasonId).Distinct().Count() > 1;
            var ordered = show
                .Select(t => (Theme: t, Season: t.SeasonId is Guid id ? _libraryManager.GetItemById(id) as Season : null))
                .OrderBy(x => x.Season?.IndexNumber ?? x.Theme.FolderSeason ?? 0)
                .ThenBy(x => x.Theme.Type == "OP" ? 0 : 1)
                .ThenBy(x => x.Theme.Sequence)
                .ThenBy(x => x.Theme.Version)
                .ThenBy(x => x.Theme.Variant, StringComparer.Ordinal)
                .ToList();

            var usedVideoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedSongNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var track = 0;
            foreach (var (theme, season) in ordered)
            {
                track++;
                var seasonNumber = season?.IndexNumber ?? theme.FolderSeason;
                var code = (seasons && seasonNumber is int n ? $"S{n} " : string.Empty)
                    + theme.Type + theme.Sequence
                    + (theme.Version > 1 ? "v" + theme.Version.ToString(CultureInfo.InvariantCulture) : string.Empty)
                    + (theme.Variant.Length > 0 ? "-" + theme.Variant : string.Empty);
                var song = string.IsNullOrWhiteSpace(theme.SongTitle) ? null : theme.SongTitle.Trim();
                var premiere = season?.PremiereDate ?? series?.PremiereDate;
                var plan = new ThemePlan
                {
                    Theme = theme,
                    Show = showName,
                    ShowFolder = SafeName(showName),
                    SeriesImage = series?.GetImagePath(ImageType.Primary, 0),
                    SeasonName = seasons ? season?.Name : null,
                    Code = code,
                    Song = song,
                    Track = track,
                    Year = premiere?.Year ?? season?.ProductionYear ?? series?.ProductionYear,
                    Premiered = premiere,
                    NsfwRating = theme.Nsfw ? (string.IsNullOrWhiteSpace(Config?.NsfwRating) ? "NC-17" : Config!.NsfwRating.Trim()) : null,
                };
                // Not "{Show} - …": Jellyfin takes files named after their folder like that for versions of one video.
                plan.VideoName = Unique(usedVideoNames, SafeName($"{track:00} {code}" + (song is null ? string.Empty : $" - {song}")));
                plan.SongName = Unique(usedSongNames, SafeName($"{track:00} {code}" + (song is null ? string.Empty : $" - {song}")));
                if (theme.HasFullVersion)
                {
                    plan.FullVideoName = Unique(usedVideoNames, SafeName($"{track:00} {code} (Full)" + (song is null ? string.Empty : $" - {song}")));
                    plan.FullSongName = Unique(usedSongNames, SafeName($"{track:00} {code} (Full)" + (song is null ? string.Empty : $" - {song}")));
                }
                plan.Hash = Hash(plan);
                plans.Add(plan);
            }
        }

        return plans;

        static string Unique(HashSet<string> used, string name)
        {
            var candidate = name;
            for (var i = 2; !used.Add(candidate); i++)
            {
                candidate = $"{name} ({i})";
            }

            return candidate;
        }
    }

    private async Task<Published> PublishAsync(ThemePlan plan, CancellationToken ct)
    {
        var theme = plan.Theme;
        var published = new Published { Hash = plan.Hash };

        if (theme.Mp4Status == "ready" && theme.Mp4Path is { } mp4 && File.Exists(mp4))
        {
            var folder = Path.Combine(VideosDir!, plan.ShowFolder);
            Directory.CreateDirectory(folder);
            var video = Path.Combine(folder, plan.VideoName + ".mp4");
            LinkOrCopy(mp4, video);
            published.Video = video;

            var nfo = Path.Combine(folder, plan.VideoName + ".nfo");
            WriteNfo(plan, nfo, full: false);
            published.Nfo = nfo;

            // The same frame as the item's picture ("-poster") and its landscape thumbnail ("-thumb").
            var poster = Path.Combine(folder, plan.VideoName + "-poster.jpg");
            if (await GrabFrameAsync(mp4, poster, ct).ConfigureAwait(false))
            {
                var thumb = Path.Combine(folder, plan.VideoName + "-thumb.jpg");
                File.Copy(poster, thumb, overwrite: true);
                published.Pictures = [poster, thumb];
            }
        }

        if (SongSource(theme) is { } source)
        {
            var folder = Path.Combine(SongsDir!, plan.ShowFolder);
            Directory.CreateDirectory(folder);
            var songFile = Path.Combine(folder, plan.SongName + ".m4a");
            if (await WriteSongAsync(plan, source, songFile, full: false, ct).ConfigureAwait(false))
            {
                published.Song = songFile;
            }
        }

        // The full-length version, as items of its own: "Yuusha (Full)".
        if (plan.FullVideoName is { } fullVideoName && theme.FullMp4Status == "ready" && theme.FullMp4Path is { } fullMp4 && File.Exists(fullMp4))
        {
            var folder = Path.Combine(VideosDir!, plan.ShowFolder);
            Directory.CreateDirectory(folder);
            var video = Path.Combine(folder, fullVideoName + ".mp4");
            LinkOrCopy(fullMp4, video);
            published.FullVideo = video;
            var nfo = Path.Combine(folder, fullVideoName + ".nfo");
            WriteNfo(plan, nfo, full: true);
            published.FullNfo = nfo;
            var poster = Path.Combine(folder, fullVideoName + "-poster.jpg");
            if (await GrabFrameAsync(fullMp4, poster, ct).ConfigureAwait(false))
            {
                var thumb = Path.Combine(folder, fullVideoName + "-thumb.jpg");
                File.Copy(poster, thumb, overwrite: true);
                published.Pictures.AddRange([poster, thumb]);
            }
        }

        if (plan.FullSongName is { } fullSongName && FullSongSource(theme) is { } fullSource)
        {
            var folder = Path.Combine(SongsDir!, plan.ShowFolder);
            Directory.CreateDirectory(folder);
            var songFile = Path.Combine(folder, fullSongName + ".m4a");
            if (await WriteSongAsync(plan, fullSource, songFile, full: true, ct).ConfigureAwait(false))
            {
                published.FullSong = songFile;
            }
        }

        return published;
    }

    /// <summary>Where the full version's song comes from: its audio when ready, else its video's sound.</summary>
    private static string? FullSongSource(Theme theme)
    {
        if (theme.FullM4aStatus == "ready" && theme.FullM4aPath is { } m4a && File.Exists(m4a))
        {
            return m4a;
        }

        return theme.FullMp4Status == "ready" && theme.FullMp4Path is { } mp4 && File.Exists(mp4) ? mp4 : null;
    }

    /// <summary>The playable copy to take a theme's song from: its audio when that's ready, else its video's sound.</summary>
    private static string? SongSource(Theme theme)
    {
        if (theme.M4aStatus == "ready" && theme.M4aPath is { } m4a && File.Exists(m4a))
        {
            return m4a;
        }

        return theme.Mp4Status == "ready" && theme.Mp4Path is { } mp4 && File.Exists(mp4) ? mp4 : null;
    }

    /// <summary>A link to the playable copy, so the library adds no second copy of the video; a copy where links aren't allowed.</summary>
    private static void LinkOrCopy(string target, string path)
    {
        VideoConverter.TryDelete(path);
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            File.Copy(target, path, overwrite: true);
        }
    }

    private static void WriteNfo(ThemePlan plan, string path, bool full)
    {
        var theme = plan.Theme;
        var kind = theme.Type == "OP" ? "Opening" : "Ending";
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var writer = XmlWriter.Create(path, settings);
        writer.WriteStartDocument(true);
        writer.WriteStartElement("musicvideo");
        writer.WriteElementString("title", Title(plan, full));
        foreach (var artist in theme.Artists.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            writer.WriteElementString("artist", artist.Trim());
        }

        writer.WriteElementString("album", plan.Show);
        writer.WriteElementString("track", plan.Track.ToString(CultureInfo.InvariantCulture));
        if (plan.Year is int year)
        {
            writer.WriteElementString("year", year.ToString(CultureInfo.InvariantCulture));
        }

        if (plan.Premiered is DateTime premiered)
        {
            writer.WriteElementString("premiered", premiered.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        writer.WriteElementString("genre", "Anime");
        writer.WriteElementString("genre", kind);
        writer.WriteElementString("tag", kind);
        if (full)
        {
            writer.WriteElementString("tag", "Full Version");
        }

        if (plan.SeasonName is { } seasonName)
        {
            writer.WriteElementString("tag", seasonName);
        }

        if (theme.Spoiler)
        {
            writer.WriteElementString("tag", "Spoiler");
        }

        if (theme.Nsfw)
        {
            writer.WriteElementString("tag", "NSFW");
        }

        if (plan.NsfwRating is { } rating)
        {
            writer.WriteElementString("mpaa", rating);
        }

        writer.WriteElementString("plot", full ? Plot(plan) + " · Full version" : Plot(plan));
        writer.WriteElementString("sorttitle", SortTitle(plan) + (full ? " full" : string.Empty));
        writer.WriteStartElement("uniqueid");
        writer.WriteAttributeString("type", "oped");
        writer.WriteAttributeString("default", "true");
        writer.WriteString(theme.Id);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>The song's title ("Yuusha", or "Yuusha (Full)" for the full version).</summary>
    private static string Title(ThemePlan plan, bool full)
    {
        var title = plan.Song ?? $"{plan.Show} {(plan.Theme.Type == "OP" ? "Opening" : "Ending")} {plan.Theme.Sequence}";
        return full ? title + " (Full)" : title;
    }

    /// <summary>"Opening 2 of Hell's Paradise · Season 2 · Episodes 5–13 · Contains spoilers".</summary>
    private static string Plot(ThemePlan plan)
    {
        var theme = plan.Theme;
        var parts = new List<string> { $"{(theme.Type == "OP" ? "Opening" : "Ending")} {theme.Sequence} of {plan.Show}" };
        if (plan.SeasonName is { } season)
        {
            parts.Add(season);
        }

        if (!string.IsNullOrWhiteSpace(theme.Episodes))
        {
            parts.Add("Episodes " + theme.Episodes.Replace("-", "–", StringComparison.Ordinal));
        }

        if (theme.Spoiler)
        {
            parts.Add("Contains spoilers");
        }

        if (theme.Nsfw)
        {
            parts.Add("Not safe for work");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Keeps a show's themes together, season by season, openings before endings.</summary>
    private static string SortTitle(ThemePlan plan) =>
        $"{plan.Show} {plan.Track:000}";

    private async Task<bool> WriteSongAsync(ThemePlan plan, string source, string path, bool full, CancellationToken ct)
    {
        var theme = plan.Theme;
        var codec = await ProbeAudioAsync(source, ct).ConfigureAwait(false);
        if (codec is null)
        {
            return false; // a video with no sound
        }

        var copy = codec is "aac" or "alac";
        var comment = full ? Plot(plan) + " · Full version" : Plot(plan);
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", source,
            "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1" };
        args.AddRange(copy ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "256k", "-ac", "2"]);
        void Tag(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                args.AddRange(["-metadata", $"{key}={value}"]);
            }
        }

        Tag("title", Title(plan, full));
        Tag("artist", theme.Artists.Count > 0 ? string.Join(", ", theme.Artists) : null);
        Tag("album", $"{plan.Show} — Openings & Endings");
        Tag("album_artist", plan.Show);
        Tag("genre", "Anime");
        Tag("track", plan.Track.ToString(CultureInfo.InvariantCulture));
        Tag("date", plan.Year?.ToString(CultureInfo.InvariantCulture));
        Tag("comment", comment);
        args.AddRange(["-movflags", "+faststart", "-f", "mp4", path + ".tmp"]);

        var (code, error) = await VideoConverter.RunAsync(_mediaEncoder.EncoderPath, args, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (code == 0 && File.Exists(path + ".tmp"))
        {
            File.Move(path + ".tmp", path, overwrite: true);
            return true;
        }

        VideoConverter.TryDelete(path + ".tmp");
        _logger.LogWarning("OP/ED: couldn't write the song for {Show} {Code} (exit {Code2}): {Error}", plan.Show, plan.Code, code, error.Length > 300 ? error[..300] : error);
        return false;
    }

    private async Task<string?> ProbeAudioAsync(string path, CancellationToken ct)
    {
        var (code, output) = await VideoConverter.RunAsync(_mediaEncoder.ProbePath,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", path],
            TimeSpan.FromMinutes(1), ct, captureStdout: true).ConfigureAwait(false);
        var value = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return code == 0 && !string.IsNullOrEmpty(value) ? value.ToLowerInvariant() : null;
    }

    /// <summary>A frame about a third of the way in (past any fade from black), at most 1280 wide.</summary>
    private async Task<bool> GrabFrameAsync(string video, string path, CancellationToken ct)
    {
        var (probe, output) = await VideoConverter.RunAsync(_mediaEncoder.ProbePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", video],
            TimeSpan.FromMinutes(1), ct, captureStdout: true).ConfigureAwait(false);
        var seconds = probe == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? Math.Min(d * 0.35, 45)
            : 0;
        var (code, _) = await VideoConverter.RunAsync(_mediaEncoder.EncoderPath,
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", video,
                "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", "-q:v", "3", path],
            TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        return code == 0 && File.Exists(path);
    }

    /// <summary>Each show's folder gets the show's picture, as the album art (songs) and the folder's picture (videos).</summary>
    private static void AddAlbumArt(List<ThemePlan> plans)
    {
        foreach (var show in plans.GroupBy(p => p.ShowFolder, StringComparer.OrdinalIgnoreCase))
        {
            var image = show.Select(p => p.SeriesImage).FirstOrDefault(i => i is not null && File.Exists(i));
            if (image is null)
            {
                continue;
            }

            foreach (var root in new[] { VideosDir!, SongsDir! })
            {
                var folder = Path.Combine(root, show.Key);
                var art = Path.Combine(folder, "folder" + Path.GetExtension(image).ToLowerInvariant());
                if (Directory.Exists(folder) && !File.Exists(art))
                {
                    try
                    {
                        File.Copy(image, art);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }

    /// <summary>Show folders left with nothing but their picture are removed.</summary>
    private static void RemoveEmptyFolders(string root)
    {
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var media = Directory.EnumerateFiles(folder).Any(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase));
            if (!media)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>Rescans just the plugin's two libraries, not the whole server.</summary>
    private async Task RefreshLibrariesAsync(CancellationToken ct)
    {
        var (videos, songs) = LibraryIds();
        foreach (var id in new[] { videos, songs })
        {
            if (Guid.TryParse(id, out var guid) && _libraryManager.GetItemById(guid) is Folder folder)
            {
                try
                {
                    await folder.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(new DirectoryService(_fileSystem)), true, false, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogWarning(e, "OP/ED: couldn't rescan the {Library} library", folder.Name);
                }
            }
        }
    }

    private void SaveManifest()
    {
        var file = ManifestFile;
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(Manifest, JsonOptions));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    private static string Hash(ThemePlan plan)
    {
        var t = plan.Theme;
        var parts = new object?[]
        {
            3, // bump to republish everything when the layout changes (3: full versions)
            plan.Show, plan.ShowFolder, plan.VideoName, plan.SongName, plan.Code, plan.Song, plan.Track, plan.Year, plan.Premiered,
            plan.SeasonName, plan.NsfwRating, string.Join("|", t.Artists), t.Episodes, t.Spoiler, t.Nsfw,
            t.Mp4Status == "ready" ? t.Mp4Path : null, t.M4aStatus == "ready" ? t.M4aPath : null,
            plan.FullVideoName, plan.FullSongName,
            t.FullMp4Status == "ready" ? t.FullMp4Path : null, t.FullM4aStatus == "ready" ? t.FullM4aPath : null,
        };
        var text = string.Join("\u001f", parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>A file or folder name without characters that aren't allowed in one.</summary>
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.');
        if (cleaned.Length > 150)
        {
            cleaned = cleaned[..150].Trim();
        }

        return cleaned.Length == 0 ? "_" : cleaned;
    }

    private static bool IsOurs(string location) => SamePath(location, VideosDir) || SamePath(location, SongsDir);

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.Ordinal);

    /// <summary>Whether one folder is the other or inside it.</summary>
    private static bool Overlaps(string a, string b)
    {
        var x = Path.TrimEndingDirectorySeparator(a) + Path.DirectorySeparatorChar;
        var y = Path.TrimEndingDirectorySeparator(b) + Path.DirectorySeparatorChar;
        return x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal);
    }

    private sealed class ThemePlan
    {
        public required Theme Theme { get; init; }

        public required string Show { get; init; }

        public required string ShowFolder { get; init; }

        public string? SeriesImage { get; init; }

        public string? SeasonName { get; init; }

        public required string Code { get; init; }

        public string? Song { get; init; }

        public int Track { get; init; }

        public int? Year { get; init; }

        public DateTime? Premiered { get; init; }

        public string? NsfwRating { get; init; }

        public string VideoName { get; set; } = string.Empty;

        public string SongName { get; set; } = string.Empty;

        public string? FullVideoName { get; set; }

        public string? FullSongName { get; set; }

        public string Hash { get; set; } = string.Empty;
    }
}

/// <summary>What was published for one theme, so it can be updated or removed later.</summary>
public sealed class Published
{
    public string Hash { get; set; } = string.Empty;

    public string? Video { get; set; }

    public string? Nfo { get; set; }

    public List<string> Pictures { get; set; } = [];

    public string? Song { get; set; }

    public string? FullVideo { get; set; }

    public string? FullNfo { get; set; }

    public string? FullSong { get; set; }

    private IEnumerable<string> Files => new[] { Video, Nfo, Song, FullVideo, FullNfo, FullSong }.Concat(Pictures).OfType<string>();

    /// <summary>Whether every file is still there (a link counts only while its target is).</summary>
    public bool FilesExist() => Files.All(File.Exists);

    public void Delete()
    {
        foreach (var file in Files)
        {
            VideoConverter.TryDelete(file);
        }
    }
}

public sealed class LibraryStatus
{
    public string? VideosLibraryId { get; set; }

    public string? SongsLibraryId { get; set; }

    public int Videos { get; set; }

    public int Songs { get; set; }

    public bool Syncing { get; set; }

    public DateTime? LastSync { get; set; }

    public string? LastError { get; set; }

    /// <summary>Why publishing can't happen with the current settings; null when it can.</summary>
    public string? Problem { get; set; }
}
