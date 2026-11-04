using Jellyfin.Plugin.AsterTracking.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AsterTracking;

/// <summary>
/// Aster's tracking plugin: scrobbling and discovery through each person's own Trakt, Simkl, AniList, MyAnimeList,
/// Last.fm, ListenBrainz and Hardcover accounts (from the next release), and the switch that turns Aster's Seerr
/// requests on, with the Seerr server's address. See ../SPEC.md.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Aster Tracking";

    /// <summary>
    /// Must match build.yaml's guid: Jellyfin rewrites the plugin's meta.json with this on every start.
    /// </summary>
    public override Guid Id => Guid.Parse("023a7921-895e-40fb-b815-ab4462f68107");

    public override string Description =>
        "Tracking, scrobbling and discovery for Aster Media Player through each person's own accounts, and the "
        + "switch that turns Aster's Seerr requests on.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "AsterTracking",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            DisplayName = "Aster Tracking",
        }
    ];
}
