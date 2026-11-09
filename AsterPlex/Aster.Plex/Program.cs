using System.Diagnostics;
using System.Net;
using Aster.Plex;
using Aster.Plex.AniList;
using Aster.Plex.Discovery;
using Aster.Plex.Hardcover;
using Aster.Plex.LastFm;
using Aster.Plex.ListenBrainz;
using Aster.Plex.MyAnimeList;
using Aster.Plex.Plex;
using Aster.Plex.Simkl;
using Aster.Plex.Tracking;
using Yarp.ReverseProxy.Forwarder;

// Aster for Plex: the Jellyfin plugin's /AsterTracking API for a Plex server, with everything else passed through to
// Plex, so the container is itself one of the server's addresses (see README.md).
CompanionSettings.Load();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(int.TryParse(Environment.GetEnvironmentVariable("ASTER_PORT"), out var port) ? port : 8098));

builder.Services.AddHttpClient();
// Jellyfin's PascalCase ("Services", "Seerr"), as the plugin answers, so the apps read both the same way.
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null);
builder.Services.AddHttpForwarder();
// No default sign-in: only requests to the container's own API are checked, not what's passed through to Plex.
builder.Services.AddAuthentication()
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, PlexAuth>(PlexAuth.SchemeName, null);
builder.Services.AddAuthorization(options =>
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(PlexAuth.SchemeName).RequireAuthenticatedUser().Build());

builder.Services.AddSingleton<Aster.Plex.Web.SignIns>();
builder.Services.AddSingleton<PlexServer>();
builder.Services.AddSingleton<PlexPeople>();
builder.Services.AddSingleton<PlexItems>();
builder.Services.AddSingleton<IItemLookup>(s => s.GetRequiredService<PlexItems>());
builder.Services.AddSingleton<PlexWatcher>();
builder.Services.AddSingleton<IPlaybackSource>(s => s.GetRequiredService<PlexWatcher>());
builder.Services.AddHostedService(s => s.GetRequiredService<PlexWatcher>());

// As in the plugin: one store shared by the API and the scrobbler, so their writes to a person's file don't cross.
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<AnimeIds>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<DiscoveryService>();
builder.Services.AddSingleton<SimklClient>();
builder.Services.AddSingleton<AniListClient>();
builder.Services.AddSingleton<MyAnimeListClient>();
builder.Services.AddSingleton<LastFmClient>();
builder.Services.AddSingleton<ListenBrainzClient>();
builder.Services.AddSingleton<HardcoverClient>();
builder.Services.AddSingleton<ITracker>(s => s.GetRequiredService<SimklClient>());
builder.Services.AddSingleton<ITracker>(s => s.GetRequiredService<AniListClient>());
builder.Services.AddSingleton<ITracker>(s => s.GetRequiredService<MyAnimeListClient>());
builder.Services.AddSingleton<ITracker>(s => s.GetRequiredService<LastFmClient>());
builder.Services.AddSingleton<ITracker>(s => s.GetRequiredService<ListenBrainzClient>());
builder.Services.AddSingleton<ITracker>(s => s.GetRequiredService<HardcoverClient>());
builder.Services.AddHostedService<Scrobbler>();

var app = builder.Build();

// The settings page, at /aster (Plex has nothing there). Its files are served before routing, which would otherwise
// hand every path to the pass-through to Plex below.
app.Use(async (context, next) =>
{
    if (context.Request.Path.Value == "/aster")
    {
        context.Response.Redirect("/aster/");
        return;
    }

    await next();
});
var page = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(app.Environment.WebRootPath, "aster"));
app.UseDefaultFiles(new DefaultFilesOptions { RequestPath = "/aster", FileProvider = page });
app.UseStaticFiles(new StaticFileOptions { RequestPath = "/aster", FileProvider = page });
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Everything else goes to Plex as it came: the container is one of the server's addresses, so any Plex app may use it.
var toPlex = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,
    UseCookies = false,
    EnableMultipleHttp2Connections = true,
    ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
    ConnectTimeout = TimeSpan.FromSeconds(15),
});
app.Map("/{**rest}", async (HttpContext context, IHttpForwarder forwarder) =>
{
    if (!PlexServer.Configured)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsync("Aster for Plex isn't set up yet: set PLEX_URL and PLEX_TOKEN.");
        return;
    }

    await forwarder.SendAsync(context, PlexServer.BaseUrl + "/", toPlex, ForwarderRequestConfig.Empty, HttpTransformer.Default);
}).WithOrder(int.MaxValue);

app.Run();
