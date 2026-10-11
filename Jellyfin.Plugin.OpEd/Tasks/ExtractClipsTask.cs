using Jellyfin.Plugin.OpEd.Themes;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.OpEd.Tasks;

/// <summary>Cuts missing OPs/EDs out of episodes by chapter marker for review. Manual: no trigger by default.</summary>
public sealed class ExtractClipsTask : IScheduledTask
{
    private readonly ClipExtractor _extractor;

    public ExtractClipsTask(ClipExtractor extractor)
    {
        _extractor = extractor;
    }

    public string Name => "Cut OP/ED clips from chapters";

    public string Key => "OpEdExtractClips";

    public string Description => "Finds OP/ED chapters in your episodes for themes missing from the themes folder and cuts them into a review queue (Dashboard → OP/ED).";

    public string Category => "OP/ED";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _extractor.ExtractAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
