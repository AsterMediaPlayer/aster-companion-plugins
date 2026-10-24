using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Aurora;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Adds the Aurora script and stylesheet tags to the web client's start page; no other plugin needed.
        serviceCollection.AddTransient<IStartupFilter, IndexHtmlInjectionStartupFilter>();
    }
}
