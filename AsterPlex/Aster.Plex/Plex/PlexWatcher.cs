using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aster.Plex.Tracking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aster.Plex.Plex;

/// <summary>
/// Hears what's played on the Plex server, in any Plex app, without webhooks: it reads the server's sessions every
/// <see cref="SessionsEvery"/> and turns what changed into the start, progress and stop events the scrobbler takes
/// (Jellyfin's, in the plugin). A session gone since the last look has stopped, where it had got to plus the time
/// it's been playing since. Every <see cref="HistoryEvery"/> it also reads Plex's watch history for anything the
/// sessions missed (played while the container was down, or between two looks); where it got to in the history is
/// kept in the data folder, so nothing is sent twice across a restart, and history from before the first start isn't
/// sent at all.
/// </summary>
public sealed class PlexWatcher : IPlaybackSource, IHostedService, IDisposable
{
    private static readonly TimeSpan SessionsEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HistoryEvery = TimeSpan.FromMinutes(5);

    /// <summary>A play the sessions showed isn't taken again from the history within this long.</summary>
    private static readonly TimeSpan SeenFor = TimeSpan.FromHours(12);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly PlexServer _plex;
    private readonly PlexItems _items;
    private readonly PlexPeople _people;
    private readonly ILogger<PlexWatcher> _log;
    private readonly Dictionary<string, Playing> _playing = new();
    private readonly ConcurrentDictionary<(Guid User, long Key), DateTimeOffset> _seen = new();
    private readonly SemaphoreSlim _looking = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private bool _warned;

    public PlexWatcher(PlexServer plex, PlexItems items, PlexPeople people, ILogger<PlexWatcher> log)
    {
        _plex = plex;
        _items = items;
        _people = people;
        _log = log;
    }

    public event EventHandler<PlaybackEvent>? PlaybackStart;

    public event EventHandler<PlaybackEvent>? PlaybackProgress;

    public event EventHandler<PlaybackEvent>? PlaybackStopped;

    public event EventHandler<PlaybackEvent>? MarkedPlayed;

    private static string StatePath => Path.Combine(CompanionSettings.DataPath, "watcher.json");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop?.Cancel();
        if (_loop is not null)
        {
            await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _stop?.Dispose();
        _looking.Dispose();
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var nextHistory = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (!stop.IsCancellationRequested)
        {
            if (PlexServer.Configured)
            {
                try
                {
                    await SessionsAsync(stop).ConfigureAwait(false);
                    if (DateTimeOffset.UtcNow >= nextHistory)
                    {
                        nextHistory = DateTimeOffset.UtcNow + HistoryEvery;
                        await HistoryAsync(stop).ConfigureAwait(false);
                    }

                    _warned = false;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexException or JsonException)
                {
                    if (stop.IsCancellationRequested)
                    {
                        break;
                    }

                    // Said once until it's back, not every ten seconds.
                    if (!_warned)
                    {
                        _log.LogWarning("Aster for Plex: Plex couldn't be read at {Url}: {Error}", PlexServer.BaseUrl, ex.Message);
                        _warned = true;
                    }
                }
            }

            try
            {
                await Task.Delay(SessionsEvery, stop).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One look at the sessions: what started, what's moved on, what stopped.</summary>
    internal async Task SessionsAsync(CancellationToken cancellation)
    {
        await _looking.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var reply = await _plex.GetAsync("/status/sessions", cancellation).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var current = new HashSet<string>();
            foreach (var session in reply.List("Metadata"))
            {
                if (session.Text("sessionKey") is not string sessionKey || session.Long("ratingKey") is not long key
                    || session["User"].Long("id") is not long account)
                {
                    continue;
                }

                var person = await _people.ForAccountAsync(account, cancellation).ConfigureAwait(false);
                if (person is null)
                {
                    continue;
                }

                var offset = session.Long("viewOffset") ?? 0;
                var paused = session["Player"].Text("state") is "paused";
                current.Add(sessionKey);
                if (_playing.TryGetValue(sessionKey, out var was) && (was.Key != key || was.User != person.Id))
                {
                    // The same player moved on to the next song or episode.
                    Stop(was, now);
                    _playing.Remove(sessionKey);
                }

                if (!_playing.TryGetValue(sessionKey, out var playing))
                {
                    var item = await _items.ItemAsync(key, cancellation).ConfigureAwait(false);
                    if (item is null)
                    {
                        continue;
                    }

                    playing = new Playing(person.Id, key, item);
                    _playing[sessionKey] = playing;
                    Raise(PlaybackStart, new PlaybackEvent(person.Id, item, Ticks(offset)));
                }

                playing.OffsetMs = Math.Max(offset, 0);
                playing.Furthest = Math.Max(playing.Furthest, playing.OffsetMs);
                playing.Paused = paused;
                playing.Seen = now;
                _seen[(person.Id, key)] = now;
                Raise(PlaybackProgress, new PlaybackEvent(person.Id, playing.Item, Ticks(playing.OffsetMs), paused));
            }

            foreach (var gone in _playing.Where(p => !current.Contains(p.Key)).ToList())
            {
                Stop(gone.Value, now);
                _playing.Remove(gone.Key);
            }

            foreach (var old in _seen.Where(s => now - s.Value > SeenFor).Select(s => s.Key).ToList())
            {
                _seen.TryRemove(old, out _);
            }
        }
        finally
        {
            _looking.Release();
        }
    }

    private void Stop(Playing playing, DateTimeOffset now)
    {
        // Where it got to: the last position seen, plus the time since if it was playing (it ended between looks).
        var runtime = playing.Item.RunTimeTicks is long ticks ? (long)TimeSpan.FromTicks(ticks).TotalMilliseconds : 0;
        var since = playing.Paused ? 0 : (long)Math.Min((now - playing.Seen).TotalMilliseconds, SessionsEvery.TotalMilliseconds * 1.5);
        var reached = playing.OffsetMs + since;
        if (runtime > 0)
        {
            reached = Math.Min(reached, runtime);
        }

        // Within a look of the end counts as played through.
        var completed = runtime > 0 && runtime - reached <= SessionsEvery.TotalMilliseconds;
        Raise(PlaybackStopped, new PlaybackEvent(playing.User, playing.Item, Ticks(Math.Max(reached, playing.Furthest)), playing.Paused, completed));
    }

    /// <summary>Plex's history since the last look, for plays the sessions didn't show.</summary>
    internal async Task HistoryAsync(CancellationToken cancellation)
    {
        var state = ReadState();
        var now = DateTimeOffset.UtcNow;
        if (state.HistoryFrom is not long from)
        {
            // The first start: history from before it isn't sent.
            WriteState(new WatcherState { HistoryFrom = now.ToUnixTimeSeconds() });
            return;
        }

        var latest = from;
        var query = $"/status/sessions/history/all?sort=viewedAt:asc&viewedAt>={from.ToString(CultureInfo.InvariantCulture)}"
            + "&X-Plex-Container-Start=0&X-Plex-Container-Size=500";
        var reply = await _plex.GetAsync(query, cancellation).ConfigureAwait(false);
        foreach (var entry in reply.List("Metadata"))
        {
            if (entry.Long("viewedAt") is not long viewed || viewed < from || entry.Long("ratingKey") is not long key
                || entry.Long("accountID") is not long account)
            {
                continue;
            }

            latest = Math.Max(latest, viewed);
            var person = await _people.ForAccountAsync(account, cancellation).ConfigureAwait(false);
            if (person is null || _seen.ContainsKey((person.Id, key)))
            {
                continue;
            }

            var item = await _items.ItemAsync(key, cancellation).ConfigureAwait(false);
            if (item is not null)
            {
                _seen[(person.Id, key)] = now;
                Raise(MarkedPlayed, new PlaybackEvent(person.Id, item, item.RunTimeTicks, playedToCompletion: true, at: DateTimeOffset.FromUnixTimeSeconds(viewed)));
            }
        }

        // The same second again next time, as more can land in it; what's already been taken is in _seen.
        WriteState(new WatcherState { HistoryFrom = latest });
    }

    private void Raise(EventHandler<PlaybackEvent>? handler, PlaybackEvent e)
    {
        try
        {
            handler?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Aster for Plex: handling playback of {Item} failed", e.Item.Name);
        }
    }

    private static long Ticks(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).Ticks;

    private static WatcherState ReadState()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<WatcherState>(File.ReadAllText(StatePath), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new();
        }
    }

    private static void WriteState(WatcherState state)
    {
        Directory.CreateDirectory(CompanionSettings.DataPath);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
        File.Move(temporary, StatePath, true);
    }

    private sealed class Playing
    {
        public Playing(Guid user, long key, PlayedItem item)
        {
            User = user;
            Key = key;
            Item = item;
        }

        public Guid User { get; }

        public long Key { get; }

        public PlayedItem Item { get; }

        public long OffsetMs { get; set; }

        public long Furthest { get; set; }

        public bool Paused { get; set; }

        public DateTimeOffset Seen { get; set; }
    }

    private sealed class WatcherState
    {
        /// <summary>The history's viewedAt (Unix seconds) to read from next.</summary>
        public long? HistoryFrom { get; set; }
    }
}
