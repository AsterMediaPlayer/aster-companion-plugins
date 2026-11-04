using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AsterTracking.Configuration;

/// <summary>
/// The server's settings. People's linked accounts are not kept here (this file is shown to every administrator and
/// copied by backups): they're kept per person in the plugin's data folder (data/AsterTracking/users).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Whether Aster's Seerr features (requesting, request status) are on for this server's users.</summary>
    public bool SeerrEnabled { get; set; }

    /// <summary>
    /// The Seerr server's address as the apps reach it, e.g. https://requests.example.com. The apps sign each person in
    /// to Seerr with their own Jellyfin login, so no key is kept here.
    /// </summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>
    /// This server's own Simkl app (a "TV, devices &amp; command line" app's client id), in place of Aster's built-in
    /// one. Blank uses Aster's.
    /// </summary>
    public string SimklClientId { get; set; } = string.Empty;

    /// <summary>
    /// This server's own AniList app (its redirect set to https://anilist.co/api/v2/oauth/pin), in place of Aster's.
    /// Blank uses Aster's.
    /// </summary>
    public string AniListClientId { get; set; } = string.Empty;

    /// <summary>
    /// This server's own MyAnimeList app (type "other", its redirect https://astermediaplayer.com/link/myanimelist/),
    /// in place of Aster's. Blank uses Aster's.
    /// </summary>
    public string MyAnimeListClientId { get; set; } = string.Empty;

    /// <summary>This server's own Last.fm API account, in place of Aster's. Blank uses Aster's; set both or neither.</summary>
    public string LastFmApiKey { get; set; } = string.Empty;

    /// <summary>The shared secret Last.fm gives with <see cref="LastFmApiKey"/>, which signs every request.</summary>
    public string LastFmSharedSecret { get; set; } = string.Empty;

    /// <summary>This server's own Hardcover app (device grant on), in place of Aster's. Blank uses Aster's.</summary>
    public string HardcoverClientId { get; set; } = string.Empty;
}
