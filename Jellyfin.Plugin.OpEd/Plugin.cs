using Jellyfin.Plugin.OpEd.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OpEd;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "OP/ED";

    /// <summary>
    /// Must match build.yaml's guid. Jellyfin rewrites meta.json from the loaded assembly on every start, so without
    /// this override the Id would default to Guid.Empty and the manifest would be zeroed.
    /// </summary>
    public override Guid Id => Guid.Parse("d459e0a2-e073-4e1e-b0ec-71883075cf4a");

    public override string Description =>
        "Anime opening and ending themes from a folder you supply, matched to your shows, with song metadata from "
        + "AnimeThemes.moe and an API for clients.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "OpEd",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            DisplayName = "OP/ED",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "queue_music",
        },
    ];
}
