using Jellyfin.Plugin.AsterOpEd.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AsterOpEd;

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
    public override string Name => "Aster OP-ED";

    /// <summary>
    /// Must match build.yaml's guid. Jellyfin rewrites meta.json from the loaded assembly on every start, so without
    /// this override the Id would default to Guid.Empty and the manifest would be zeroed.
    /// </summary>
    public override Guid Id => Guid.Parse("d459e0a2-e073-4e1e-b0ec-71883075cf4a");

    public override string Description =>
        "Anime openings and endings for your shows. Matches theme files in a folder you choose to your library, "
        + "adds song details from AnimeThemes.moe, and serves them to clients.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "OpEd",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            DisplayName = "Aster OP/ED",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "queue_music",
        },
    ];
}
