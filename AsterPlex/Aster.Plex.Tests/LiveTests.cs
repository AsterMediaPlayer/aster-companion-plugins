using Aster.Plex.Plex;
using Aster.Plex.Tracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Aster.Plex.Tests;

/// <summary>
/// Against a real Plex server, only when PLEX_LIVE_URL and PLEX_LIVE_TOKEN (the owner's) are set, and
/// PLEX_LIVE_KEYS names rating keys to read: what the container makes of them, written to the test output.
/// </summary>
public class LiveTests
{
    private readonly ITestOutputHelper _output;

    public LiveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ReadsARealServer()
    {
        var url = Environment.GetEnvironmentVariable("PLEX_LIVE_URL");
        var token = Environment.GetEnvironmentVariable("PLEX_LIVE_TOKEN");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
        {
            return;
        }

        CompanionSettings.Replace(new CompanionSettings { PlexUrl = url, PlexToken = token });
        var http = new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var server = new PlexServer(http);
        var people = new PlexPeople(server, NullLogger<PlexPeople>.Instance);
        foreach (var person in await people.EveryoneAsync(CancellationToken.None))
        {
            _output.WriteLine($"person {person.PlexId} {person.Name} owner={person.Owner}");
        }

        var items = new PlexItems(server, NullLogger<PlexItems>.Instance);
        var anime = new AnimeIds(http, new OutputLogger<AnimeIds>(_output));
        foreach (var key in (Environment.GetEnvironmentVariable("PLEX_LIVE_KEYS") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var item = await items.ItemAsync(long.Parse(key, System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
            if (item is null)
            {
                _output.WriteLine($"{key}: nothing");
                continue;
            }

            var title = WatchedTitle.From(item);
            // ARM takes TLS 1.3 only, which .NET can't do on macOS (it can in the Linux image).
            (AnimeEntry Entry, int Episode)? entry = null;
            try
            {
                entry = await anime.ForAsync(item, CancellationToken.None);
            }
            catch (TrackerException) when (OperatingSystem.IsMacOS())
            {
                _output.WriteLine($"{key}: ARM unreachable from macOS");
            }

            _output.WriteLine($"{key}: {item.Kind} '{item.Name}' series='{item.SeriesName}' ids=[{string.Join(", ", item.ProviderIds.Select(p => p.Key + "=" + p.Value))}] "
                + $"show=[{string.Join(", ", item.SeriesProviderIds.Select(p => p.Key + "=" + p.Value))}] → {title?.Label} {(title is null ? "" : title.HistoryBody(DateTimeOffset.UtcNow).ToJsonString())} "
                + $"anime={entry?.Entry} ep={entry?.Episode} song={(Aster.Plex.LastFm.LastFmClient.Song(item) is { } s ? string.Join(",", s.Select(f => f.Key + "=" + f.Value)) : "-")}");
        }
    }
}

internal sealed class OutputLogger<T>(ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        output.WriteLine($"{logLevel}: {formatter(state, exception)}");
}
