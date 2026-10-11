using Jellyfin.Plugin.OpEd.Themes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OpEd;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // One index shared by the API, the scheduled task and the settings page.
        serviceCollection.AddSingleton<ThemeIndex>();
    }
}
