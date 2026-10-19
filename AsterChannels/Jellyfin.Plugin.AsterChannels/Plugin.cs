using Jellyfin.Plugin.AsterChannels.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AsterChannels;

/// <summary>
/// Nothing but a dashboard page: channels live in playlist tags, which the page
/// reads and writes with the signed-in administrator's own API calls.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Aster Channels";

    /// <summary>
    /// Must match build.yaml's guid: Jellyfin rewrites the plugin's meta.json with this on every start.
    /// </summary>
    public override Guid Id => Guid.Parse("5b489227-2fc4-4dce-b7f8-6d0e3b2082ee");

    public override string Description =>
        "Make and edit Aster Media Player's TV channels from the dashboard. Channels are playlist tags, so the app, "
        + "this page and tagging by hand all edit the same thing.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "AsterChannels",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.channelsPage.html",
            DisplayName = "Channels",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "live_tv",
        }
    ];
}
