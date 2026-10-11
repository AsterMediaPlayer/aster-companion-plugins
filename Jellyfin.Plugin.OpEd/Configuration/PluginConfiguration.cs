using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OpEd.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Folder holding the theme files, as Jellyfin sees it (a path inside the Jellyfin container). One folder per
    /// show, named like the show's folder in the media library; optional "Season N" or "anidb-12345" subfolders.
    /// Keep it outside every library's folders so no library scans the themes as ordinary videos.
    /// </summary>
    public string ThemesPath { get; set; } = string.Empty;

    /// <summary>Look up song title, artists and episode ranges on AnimeThemes.moe (by AniDB ID).</summary>
    public bool FetchMetadata { get; set; } = true;

    /// <summary>Days before a cached AnimeThemes answer is fetched again.</summary>
    public int MetadataRefreshDays { get; set; } = 14;
}
