using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AsterTracking.Configuration;

/// <summary>
/// The server's settings. People's linked accounts are not kept here (this file is shown to every administrator and
/// copied by backups): they'll live per person in the plugin's data folder.
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
}
