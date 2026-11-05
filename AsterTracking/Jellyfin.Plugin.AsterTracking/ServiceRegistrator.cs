using Jellyfin.Plugin.AsterTracking.AniList;
using Jellyfin.Plugin.AsterTracking.Discovery;
using Jellyfin.Plugin.AsterTracking.Hardcover;
using Jellyfin.Plugin.AsterTracking.LastFm;
using Jellyfin.Plugin.AsterTracking.ListenBrainz;
using Jellyfin.Plugin.AsterTracking.MyAnimeList;
using Jellyfin.Plugin.AsterTracking.Seerr;
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
        serviceCollection.AddSingleton<AnimeIds>();
        serviceCollection.AddSingleton<TokenService>();
        serviceCollection.AddSingleton<DiscoveryService>();
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<LibraryIndex>();
        serviceCollection.AddSingleton<HomeService>();
        serviceCollection.AddHostedService<DiscoverWarmer>();
        serviceCollection.AddSingleton<SeerrBridge>();
        serviceCollection.AddSingleton<SimklClient>();
        serviceCollection.AddSingleton<AniListClient>();
        serviceCollection.AddSingleton<MyAnimeListClient>();
        serviceCollection.AddSingleton<LastFmClient>();
        serviceCollection.AddSingleton<ListenBrainzClient>();
        serviceCollection.AddSingleton<HardcoverClient>();
        serviceCollection.AddSingleton<ITracker>(services => services.GetRequiredService<SimklClient>());
        serviceCollection.AddSingleton<ITracker>(services => services.GetRequiredService<AniListClient>());
        serviceCollection.AddSingleton<ITracker>(services => services.GetRequiredService<MyAnimeListClient>());
        serviceCollection.AddSingleton<ITracker>(services => services.GetRequiredService<LastFmClient>());
        serviceCollection.AddSingleton<ITracker>(services => services.GetRequiredService<ListenBrainzClient>());
        serviceCollection.AddSingleton<ITracker>(services => services.GetRequiredService<HardcoverClient>());
        serviceCollection.AddHostedService<Scrobbler>();
    }
}
