using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpEd.Themes;

/// <summary>
/// Makes a copy of each theme video that every client can play: MP4 with H.264 (or HEVC) video and AAC audio. Apple's
/// player, Jellyfin's apps and browsers all play that directly, while AnimeThemes' WebM (VP9) doesn't play on Apple TV.
///
/// - Already an MP4/M4V/MOV with H.264/HEVC video and AAC (or no) audio: used as-is, nothing is written.
/// - H.264/HEVC in another container (MKV): rewrapped into MP4, audio converted to AAC. Seconds.
/// - Anything else (VP9, AV1, …): converted with libx264, one file at a time, at low CPU priority.
///
/// Copies live in the plugin's data folder, named after the theme id and the source's size and modification time,
/// so replacing a source file makes a new copy; copies no theme uses any more are deleted after each scan.
/// A failed conversion leaves a ".failed" note so it isn't retried until the source file changes.
/// </summary>
public sealed class VideoConverter
{
    private static readonly HashSet<string> Mp4Containers = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };
    private static readonly HashSet<string> AppleVideo = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc" };
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public VideoConverter(IMediaEncoder mediaEncoder, ILogger logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public static string CacheDir => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "mp4-cache");

    public bool IsBusy => _lock.CurrentCount == 0;

    /// <summary>Fills in <see cref="Theme.Mp4Path"/>/<see cref="Theme.Mp4Status"/> from what's on disk; no conversion.</summary>
    public void Classify(Theme theme)
    {
        theme.Mp4Path = null;
        theme.Mp4Status = null;
        if (theme.VideoPath is null || !File.Exists(theme.VideoPath))
        {
            return;
        }

        var target = TargetPath(theme);
        if (File.Exists(target))
        {
            theme.Mp4Path = target;
            theme.Mp4Status = "ready";
        }
        else if (File.Exists(target + ".failed"))
        {
            theme.Mp4Status = "failed";
        }
        else
        {
            // Includes MP4 sources: whether they can be used as-is is only known after probing them in ConvertPendingAsync.
            theme.Mp4Status = "pending";
        }
    }

    /// <summary>Converts every pending theme, one at a time. <paramref name="saved"/> is called after each one.</summary>
    public async Task ConvertPendingAsync(IReadOnlyList<Theme> themes, Action saved, IProgress<double>? progress, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(CacheDir);
            var pending = themes.Where(t => t.Mp4Status == "pending").ToList();
            var done = 0;
            foreach (var theme in pending)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(100.0 * done++ / pending.Count);
                if (theme.VideoPath is null || !File.Exists(theme.VideoPath))
                {
                    continue;
                }

                theme.Mp4Status = "converting";
                await ConvertOneAsync(theme, ct).ConfigureAwait(false);
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

        var keep = themes.Where(t => t.VideoPath is not null).Select(TargetPath).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(CacheDir))
        {
            var basePath = file.EndsWith(".failed", StringComparison.Ordinal) ? file[..^".failed".Length] : file;
            if (!keep.Contains(basePath))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string TargetPath(Theme theme)
    {
        var info = new FileInfo(theme.VideoPath!);
        var stamp = info.Exists ? $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}" : "missing";
        return Path.Combine(CacheDir, $"{theme.Id}-{stamp}.mp4");
    }

    private async Task ConvertOneAsync(Theme theme, CancellationToken ct)
    {
        var source = theme.VideoPath!;
        var target = TargetPath(theme);
        var (video, audio) = await ProbeAsync(source, ct).ConfigureAwait(false);
        var appleVideo = video is not null && AppleVideo.Contains(video);
        var appleAudio = audio is null || audio == "aac";

        if (appleVideo && appleAudio && Mp4Containers.Contains(Path.GetExtension(source)))
        {
            theme.Mp4Path = source;
            theme.Mp4Status = "ready";
            return;
        }

        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn" };
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

        var started = Stopwatch.StartNew();
        var (code, error) = await RunAsync(_mediaEncoder.EncoderPath, args, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        if (code == 0 && File.Exists(target + ".tmp"))
        {
            File.Move(target + ".tmp", target, overwrite: true);
            theme.Mp4Path = target;
            theme.Mp4Status = "ready";
            _logger.LogInformation("OP/ED: {Action} {File} for playback in {Seconds:0.0}s",
                appleVideo ? "rewrapped" : "converted", Path.GetFileName(source), started.Elapsed.TotalSeconds);
            return;
        }

        TryDelete(target + ".tmp");
        theme.Mp4Status = "failed";
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

        return (await Codec("v:0").ConfigureAwait(false), await Codec("a:0").ConfigureAwait(false));
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
