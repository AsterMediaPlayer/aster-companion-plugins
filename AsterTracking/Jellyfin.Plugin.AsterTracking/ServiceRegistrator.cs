using Jellyfin.Plugin.AsterTracking.Simkl;
using Jellyfin.Plugin.AsterTracking.Tracking;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AsterTracking;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // One store shared by the API and the scrobbler, so their writes to a person's file don't cross.
        serviceCollection.AddSingleton<AccountStore>();
        serviceCollection.AddSingleton<SimklClient>();
        serviceCollection.AddHostedService<Scrobbler>();
    }
}
