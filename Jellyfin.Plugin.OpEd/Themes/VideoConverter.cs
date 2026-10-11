using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpEd.Themes;

/// <summary>
/// Makes copies of theme media that every client can play, Apple TV included:
///
/// Video → MP4 with H.264 (or HEVC) video and AAC audio. AnimeThemes' WebM (VP9) doesn't play in AVPlayer.
/// - Already an MP4/M4V/MOV with H.264/HEVC video and AAC (or no) audio: used as-is, nothing is written.
/// - H.264/HEVC in another container (MKV): rewrapped into MP4, audio converted to AAC. Seconds.
/// - Anything else (VP9, AV1, …): converted with libx264.
///
/// Audio → M4A (AAC). AnimeThemes' audio is Opus in Ogg, which AVPlayer doesn't play.
/// - Already MP3 (.mp3) or AAC (.m4a/.aac/.mp4): used as-is.
/// - Anything else (Opus/Vorbis in Ogg, FLAC, WAV): converted to AAC-LC 256 kbps stereo.
///
/// Work runs one file at a time at low CPU priority. Copies live in the plugin's data folder, named after the theme id
/// and the source's size and modification time, so replacing a source file makes a new copy; copies no theme uses any
/// more are deleted after each scan. A failed conversion leaves a ".failed" note (the ffmpeg error) so it isn't retried
/// until the source file changes.
/// </summary>
public sealed class VideoConverter
{
    private static readonly HashSet<string> Mp4Containers = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };
    private static readonly HashSet<string> AppleVideo = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc" };

    /// <summary>Audio files AVPlayer plays as they are: container extension → codecs accepted in it.</summary>
    private static readonly Dictionary<string, string[]> AppleAudioFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = ["mp3"],
        [".m4a"] = ["aac", "alac"],
        [".aac"] = ["aac"],
        [".mp4"] = ["aac", "alac"],
    };

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public VideoConverter(IMediaEncoder mediaEncoder, ILogger logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    private enum Kind
    {
        Video,
        Audio,
    }

    public static string CacheDir => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "mp4-cache");

    public bool IsBusy => _lock.CurrentCount == 0;

    /// <summary>Fills in the playable-copy path/status fields from what's on disk; no conversion.</summary>
    public void Classify(Theme theme)
    {
        (theme.Mp4Path, theme.Mp4Status) = ClassifyOne(theme, Kind.Video);
        (theme.M4aPath, theme.M4aStatus) = ClassifyOne(theme, Kind.Audio);
    }

    /// <summary>Converts every pending video and audio, one at a time. <paramref name="saved"/> is called after each one.</summary>
    public async Task ConvertPendingAsync(IReadOnlyList<Theme> themes, Action saved, IProgress<double>? progress, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(CacheDir);
            var pending = themes.Where(t => t.Mp4Status == "pending").Select(t => (Theme: t, Kind: Kind.Video))
                .Concat(themes.Where(t => t.M4aStatus == "pending").Select(t => (Theme: t, Kind: Kind.Audio)))
                .ToList();
            var done = 0;
            foreach (var (theme, kind) in pending)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(100.0 * done++ / pending.Count);
                if (Source(theme, kind) is not { } source || !File.Exists(source))
                {
                    continue;
                }

                SetResult(theme, kind, null, "converting");
                await ConvertOneAsync(theme, kind, ct).ConfigureAwait(false);
                saved();
            }

            progress?.Report(100);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Deletes cached copies (and failure notes) that no current theme points at.</summary>
    public void RemoveUnused(IEnumerable<Theme> themes)
    {
        if (!Directory.Exists(CacheDir) || IsBusy)
        {
            return;
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in themes)
        {
            if (t.VideoPath is not null)
            {
                keep.Add(TargetPath(t, Kind.Video));
            }

            if (t.AudioPath is not null)
            {
                keep.Add(TargetPath(t, Kind.Audio));
            }
        }

        foreach (var file in Directory.EnumerateFiles(CacheDir))
        {
            var basePath = file.EndsWith(".failed", StringComparison.Ordinal) ? file[..^".failed".Length] : file;
            if (!keep.Contains(basePath))
            {
                TryDelete(file);
            }
        }
    }

    private static string? Source(Theme theme, Kind kind) => kind == Kind.Video ? theme.VideoPath : theme.AudioPath;

    private static void SetResult(Theme theme, Kind kind, string? path, string? status)
    {
        if (kind == Kind.Video)
        {
            (theme.Mp4Path, theme.Mp4Status) = (path, status);
        }
        else
        {
            (theme.M4aPath, theme.M4aStatus) = (path, status);
        }
    }

    private static (string? Path, string? Status) ClassifyOne(Theme theme, Kind kind)
    {
        var source = Source(theme, kind);
        if (source is null || !File.Exists(source))
        {
            return (null, null);
        }

        var target = TargetPath(theme, kind);
        if (File.Exists(target))
        {
            return (target, "ready");
        }

        // Includes sources that may turn out usable as-is: that's only known after probing them in ConvertPendingAsync.
        return File.Exists(target + ".failed") ? (null, "failed") : (null, "pending");
    }

    private static string TargetPath(Theme theme, Kind kind)
    {
        var info = new FileInfo(Source(theme, kind)!);
        var stamp = info.Exists ? $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}" : "missing";
        return kind == Kind.Video
            ? Path.Combine(CacheDir, $"{theme.Id}-{stamp}.mp4")
            : Path.Combine(CacheDir, $"{theme.Id}-a-{stamp}.m4a");
    }

    private async Task ConvertOneAsync(Theme theme, Kind kind, CancellationToken ct)
    {
        var source = Source(theme, kind)!;
        var target = TargetPath(theme, kind);
        var (video, audio) = await ProbeAsync(source, ct).ConfigureAwait(false);
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", source };
        string action;

        if (kind == Kind.Video)
        {
            var appleVideo = video is not null && AppleVideo.Contains(video);
            if (appleVideo && (audio is null || audio == "aac") && Mp4Containers.Contains(Path.GetExtension(source)))
            {
                SetResult(theme, kind, source, "ready");
                return;
            }

            args.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn"]);
            if (appleVideo)
            {
                args.AddRange(["-c:v", "copy"]);
                if (video == "hevc")
                {
                    args.AddRange(["-tag:v", "hvc1"]); // Apple players only accept HEVC in MP4 with the hvc1 tag
                }
            }
            else
            {
                args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-profile:v", "high", "-pix_fmt", "yuv420p",
                    "-vf", "scale='min(1920,iw)':-2", "-threads", "2"]);
            }

            args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ac", "2", "-movflags", "+faststart", "-f", "mp4", target + ".tmp"]);
            action = appleVideo ? "rewrapped" : "converted";
        }
        else
        {
            if (audio is not null && AppleAudioFiles.TryGetValue(Path.GetExtension(source), out var ok) && ok.Contains(audio))
            {
                SetResult(theme, kind, source, "ready");
                return;
            }

            // Audio only (-vn drops embedded cover art), AAC-LC 256 kbps: transparent for a theme song.
            args.AddRange(["-map", "0:a:0", "-vn", "-sn", "-dn", "-c:a", "aac", "-b:a", "256k", "-ac", "2",
                "-movflags", "+faststart", "-f", "mp4", target + ".tmp"]);
            action = "converted";
        }

        var started = Stopwatch.StartNew();
        var (code, error) = await RunAsync(_mediaEncoder.EncoderPath, args, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        if (code == 0 && File.Exists(target + ".tmp"))
        {
            File.Move(target + ".tmp", target, overwrite: true);
            SetResult(theme, kind, target, "ready");
            _logger.LogInformation("OP/ED: {Action} {File} for playback in {Seconds:0.0}s", action, Path.GetFileName(source), started.Elapsed.TotalSeconds);
            return;
        }

        TryDelete(target + ".tmp");
        SetResult(theme, kind, null, "failed");
        await File.WriteAllTextAsync(target + ".failed", error, CancellationToken.None).ConfigureAwait(false);
        _logger.LogWarning("OP/ED: couldn't convert {File} (exit {Code}): {Error}", source, code, error.Length > 300 ? error[..300] : error);
    }

    private async Task<(string? Video, string? Audio)> ProbeAsync(string path, CancellationToken ct)
    {
        async Task<string?> Codec(string stream)
        {
            var (code, output) = await RunAsync(_mediaEncoder.ProbePath,
                ["-v", "error", "-select_streams", stream, "-show_entries", "stream=codec_name", "-of", "csv=p=0", path],
                TimeSpan.FromMinutes(1), ct, captureStdout: true).ConfigureAwait(false);
            var value = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return code == 0 && !string.IsNullOrEmpty(value) ? value.ToLowerInvariant() : null;
        }

        // "V" (capital) skips attached pictures such as cover art in audio files.
        return (await Codec("V:0").ConfigureAwait(false), await Codec("a:0").ConfigureAwait(false));
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, bool captureStdout = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return (-1, "timed out after " + timeout.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " minutes");
        }

        var output = captureStdout ? await stdout.ConfigureAwait(false) : await stderr.ConfigureAwait(false);
        return (process.ExitCode, output.Trim());
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
