using Jellyfin.Plugin.AsterOpEd.Library;
using Jellyfin.Plugin.AsterOpEd.Themes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AsterOpEd;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // One index shared by the API, the scheduled task and the settings page.
        serviceCollection.AddSingleton<LibraryPublisher>();
        serviceCollection.AddSingleton<ThemeIndex>();
        serviceCollection.AddSingleton<ClipExtractor>();
    }
}
