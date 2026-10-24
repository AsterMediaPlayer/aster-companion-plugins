using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AsterAurora.Configuration;

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

    /// <summary>A back button in the top-left corner on every page except Home, the player, login and the dashboard.</summary>
    public bool EnableBackButton { get; set; } = true;

    /// <summary>
    /// The Aster app's details: an episode page titled with the episode's own name and an "S2, E2" line, "N min left"
    /// beside the buttons of a title that's been started, and Resume on the banner for one.
    /// </summary>
    public bool EnableAsterDetails { get; set; } = true;

    /// <summary>
    /// On an anime show's page, loop its opening (from the Aster OP/ED plugin) behind the page instead of a trailer,
    /// as the Aster app does. Uses <see cref="EnableTrailers"/>' delay and sound button; needs the OP/ED plugin.
    /// </summary>
    public bool EnableOpeningBackgrounds { get; set; } = true;
}
