using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Aurora.Configuration;

/// <summary>
/// Feature switches for the browser script. All of it is read anonymously by every web client (see
/// <see cref="Api.AuroraController.GetClientConfig"/>), so nothing secret belongs here.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Show the rotating hero banner at the top of Home.</summary>
    public bool EnableHero { get; set; } = true;

    /// <summary>Which titles the banner shows: "latest" (recently added), "random" or "favorites".</summary>
    public string HeroSource { get; set; } = "latest";

    /// <summary>How many titles the banner rotates through.</summary>
    public int HeroCount { get; set; } = 8;

    /// <summary>Seconds each banner title stays up.</summary>
    public int HeroIntervalSeconds { get; set; } = 9;

    /// <summary>Fade a muted trailer in behind a details page after a short delay.</summary>
    public bool EnableTrailers { get; set; } = true;

    /// <summary>Also use YouTube trailers when there is no local one. Loads an embedded YouTube player (it contacts Google).</summary>
    public bool AllowYouTubeTrailers { get; set; } = true;

    /// <summary>Seconds on a details page before the trailer starts.</summary>
    public int TrailerDelaySeconds { get; set; } = 4;

    /// <summary>Tilt and highlight cards under the mouse pointer (desktop only).</summary>
    public bool EnableTilt { get; set; } = true;

    /// <summary>Show a title's genres as chips.</summary>
    public bool EnableGenreChips { get; set; } = true;

    /// <summary>Apple TV "Up Next" look for Continue Watching and Next Up: the title's logo over each still.</summary>
    public bool EnableUpNext { get; set; } = true;

    /// <summary>Quality badges (4K, Dolby Vision, HDR10, Dolby Atmos, 5.1, CC) under a title.</summary>
    public bool EnableBadges { get; set; } = true;

    /// <summary>On show pages, an Episodes row with season tabs in place of the season cards.</summary>
    public bool EnableEpisodeShelf { get; set; } = true;
}
