using Jellyfin.Plugin.OpEd.Themes;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.OpEd.Tasks;

/// <summary>Rescans the themes folder: at startup and every 12 hours by default (Dashboard → Scheduled Tasks).</summary>
public sealed class ScanThemesTask : IScheduledTask
{
    private readonly ThemeIndex _index;

    public ScanThemesTask(ThemeIndex index)
    {
        _index = index;
    }

    public string Name => "Scan OP/ED themes";

    public string Key => "OpEdScanThemes";

    public string Description => "Scans the OP/ED themes folder, matches themes to your shows, looks up song details on AnimeThemes and makes MP4/M4A copies for clients that can't play the originals.";

    public string Category => "OP/ED";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // First half of the progress bar: the scan; second half: making playable MP4 copies of new videos.
        await _index.ScanAsync(new Progress<double>(p => progress.Report(p / 2)), cancellationToken).ConfigureAwait(false);
        await _index.ConvertVideosAsync(new Progress<double>(p => progress.Report(50 + (p / 2))), cancellationToken).ConfigureAwait(false);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(12).Ticks },
    ];
}
