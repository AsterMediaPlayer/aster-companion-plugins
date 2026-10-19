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

    /// <summary>
    /// For shows with themes but no usable AniDB/AniList/MyAnimeList/Kitsu id, search AnimeThemes by the show title. Sends the
    /// show name to AnimeThemes, one request per show, cached.
    /// </summary>
    public bool EnableTitleFallback { get; set; } = true;

    /// <summary>How alike (percent, 50 to 100) the show title and an AnimeThemes title must be to count as the same anime.</summary>
    public int TitleMatchThreshold { get; set; } = 80;

    /// <summary>
    /// Item ids of the Jellyfin libraries that "Cut OP/ED clips from chapters" searches. Empty means none: the feature
    /// does nothing until at least one library is chosen.
    /// </summary>
    public string[] ClipLibraryIds { get; set; } = [];

    /// <summary>
    /// Publish the themes as two Jellyfin libraries (a Music Videos library of the videos, a Music library of the songs),
    /// kept up to date after every scan.
    /// </summary>
    public bool PublishLibraries { get; set; }

    /// <summary>
    /// Folder the plugin owns for those libraries, as Jellyfin sees it; it holds "Videos" and "Songs". It must be outside
    /// the themes folder and every other library.
    /// </summary>
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>Name for the Music Videos library, when the plugin creates it.</summary>
    public string VideosLibraryName { get; set; } = "Anime Openings & Endings";

    /// <summary>Name for the Music library, when the plugin creates it.</summary>
    public string SongsLibraryName { get; set; } = "Anime Theme Songs";

    /// <summary>Rating given to NSFW themes' videos, so Jellyfin's parental controls hide them.</summary>
    public string NsfwRating { get; set; } = "NC-17";
}
