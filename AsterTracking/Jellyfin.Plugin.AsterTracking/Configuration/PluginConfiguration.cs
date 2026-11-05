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
    /// Where this server reaches Seerr, e.g. http://192.168.1.10:5055. The apps never use it: they reach Seerr through
    /// the plugin (/AsterTracking/Seerr/api/v1/…), which calls Seerr as each person.
    /// </summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>Seerr's API key (Seerr: Settings › General), with which the plugin calls Seerr as each person.</summary>
    public string SeerrApiKey { get; set; } = string.Empty;

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

    /// <summary>This server's own TMDB key (a v4 read access token or a v3 key) for Discover's recommendations, in place of Aster's.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Whether titles their source marks adult are kept out of Discover, for everyone. On unless turned off.</summary>
    public bool BlockAdult { get; set; } = true;

    /// <summary>Genres kept out of Discover for everyone: names, separated by commas or new lines.</summary>
    public string BlockedGenres { get; set; } = string.Empty;

    /// <summary>Tags and keywords kept out of Discover for everyone.</summary>
    public string BlockedTags { get; set; } = string.Empty;

    /// <summary>Further blocks for some people (kids' accounts, say), on top of everyone's.</summary>
    public List<PersonBlock> PersonBlocks { get; set; } = new();
}

/// <summary>Further blocks for some people, besides everyone's.</summary>
public class PersonBlock
{
    /// <summary>The Jellyfin users these apply to: ids, separated by commas.</summary>
    public string UserIds { get; set; } = string.Empty;

    /// <summary>One user's id, as the first version kept it (still honoured).</summary>
    public string UserId { get; set; } = string.Empty;

    public string Genres { get; set; } = string.Empty;

    public string Tags { get; set; } = string.Empty;

    public bool BlockAdult { get; set; }
}
