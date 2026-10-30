using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugin.AsterChannels
{
    /// <summary>
    /// A dashboard page for Aster's channels on Emby. A channel is a video playlist with aster:… tags, so the page
    /// reads and writes those tags with the signed-in administrator's own API calls; the plugin itself stores nothing.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
        }

        public override string Name => "Aster Channels";

        public override Guid Id => new Guid("c1da4f0c-3e3a-400c-a0c7-cf97da573315");

        public override string Description =>
            "Make and edit Aster Media Player's TV channels from the dashboard. Channels are playlist tags, so the app, "
            + "this page and Emby's own metadata editor all change the same channel.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            var ns = GetType().Namespace;
            return new[]
            {
                // The page, in the server menu. Its controller and scripts are served as further resources.
                new PluginPageInfo
                {
                    Name = "asterchannels",
                    EmbeddedResourcePath = ns + ".Configuration.channelsPage.html",
                    DisplayName = "Channels",
                    EnableInMainMenu = true,
                    MenuSection = "server",
                    MenuIcon = "live_tv",
                    IsMainConfigPage = true,
                },
                new PluginPageInfo { Name = "asterchannelsjs", EmbeddedResourcePath = ns + ".Configuration.channelsPage.js" },
                new PluginPageInfo { Name = "asterchanneltagsjs", EmbeddedResourcePath = ns + ".Web.channel-tags.js" },
                new PluginPageInfo { Name = "asterchannelsviewjs", EmbeddedResourcePath = ns + ".Web.channels.js" },
            };
        }
    }

    /// <summary>Nothing to set: the channels themselves are playlist tags.</summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
    }
}
