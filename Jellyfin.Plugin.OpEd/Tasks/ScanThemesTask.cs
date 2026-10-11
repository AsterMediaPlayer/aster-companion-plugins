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

    public string Description => "Indexes the OP/ED themes folder, matches themes to shows and fetches song metadata from AnimeThemes.";

    public string Category => "OP/ED";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _index.ScanAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(12).Ticks },
    ];
}
