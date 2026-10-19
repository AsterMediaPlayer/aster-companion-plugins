using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DiscMenus.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Directory scanned for "*.menu.json" / "*.binding.json" pairs.
    /// Empty means "menus" under the plugin's data folder.
    /// </summary>
    public string MenusPath { get; set; } = string.Empty;

    /// <summary>
    /// The administrator's own fanart.tv API key, used only by this server to look up artwork for menus whose background is
    /// "fanart". Empty means such backgrounds show as plain dark. Never sent to a client or put in a URL.
    /// </summary>
    public string FanartApiKey { get; set; } = string.Empty;
}
