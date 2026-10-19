using Jellyfin.Plugin.DiscMenus.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DiscMenus;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Disc Menus";

    /// <summary>
    /// Must match build.yaml's guid. PluginManager.CreatePluginInstance
    /// overwrites meta.json's guid/description with this instance's Id/
    /// Description on every startup (treats the loaded assembly as the
    /// source of truth over the manifest file) - without this override,
    /// Id defaults to Guid.Empty and the manifest gets permanently zeroed.
    /// </summary>
    public override Guid Id => Guid.Parse("913b9d44-cafe-4fe7-ae0b-f1362dc12cc7");

    public override string Description =>
        "Loads user-authored disc menu definitions, resolves them against a local binding file, "
        + "and exposes the matched entries as the parent item's Special Features.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "DiscMenus",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            DisplayName = "Disc Menus",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "extension",
        },
        new PluginPageInfo
        {
            Name = "DiscMenusEditor",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.editorPage.html",
            DisplayName = "Menu Editor",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "edit",
        },
        new PluginPageInfo
        {
            Name = "DiscMenusCatalogue",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.cataloguePage.html",
            DisplayName = "Menu Catalogue",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "cloud_download",
        }
    ];
}
