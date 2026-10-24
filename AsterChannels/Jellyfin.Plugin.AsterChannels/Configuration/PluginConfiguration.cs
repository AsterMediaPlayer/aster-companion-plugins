using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AsterChannels.Configuration;

/// <summary>
/// The Channels-tab channels themselves are playlist tags, not plugin settings. What lives here is what has nowhere
/// else to go: Jellyfin Live TV channels' groups, which Aster's guide and channel flicking follow.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Each Live TV channel's group, by channel id. A list, as the settings file can't hold a dictionary.</summary>
    public List<LiveTvChannelGroup> LiveTvGroups { get; set; } = new();
}

/// <summary>A Live TV channel's group.</summary>
public class LiveTvChannelGroup
{
    public string ChannelId { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;
}
