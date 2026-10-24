using Jellyfin.Plugin.AsterAurora.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AsterAurora;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        RenameMigration.Run(this);
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    // Jellyfin names the plugin's install folder after this: no slash.
    public override string Name => "Aster Aurora";

    /// <summary>
    /// Must match build.yaml's guid. Jellyfin rewrites meta.json from the loaded assembly on every start, so without
    /// this override the Id would default to Guid.Empty and the manifest would be zeroed.
    /// </summary>
    public override Guid Id => Guid.Parse("22f5ff1c-c1cd-47b2-839f-25e6cd9c8afc");

    public override string Description =>
        "Apple TV style behaviour for the Jellyfin web client: a home hero banner, trailer backgrounds on details pages, "
        + "card tilt and genre chips. Pairs with the Aurora custom CSS but works without it.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "Aurora",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            DisplayName = "Aster Aurora",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "auto_awesome",
        },
    ];
}
