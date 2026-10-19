using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DiscMenus;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<DiscMenuService>();
        serviceCollection.AddSingleton<Fanart.FanartService>();
        // The web client's start page gets the renderer script from our own middleware: no other plugin is needed.
        serviceCollection.AddTransient<IStartupFilter, IndexHtmlInjectionStartupFilter>();

        // If the File Transformation plugin is installed too, also offer it our transformation. Harmless: a page that already has the tag is left alone.
        serviceCollection.AddHostedService<FileTransformationIntegration>();
    }
}
