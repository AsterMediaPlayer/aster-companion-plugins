using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AsterTracking.Tracking;

/// <summary>
/// Sends what each person watches and listens to, in any app, to the services they linked: a film or episode once it's
/// stopped past 85% (the Aster apps' rule), or marked played by hand; a song once it's played half way or four minutes,
/// when it's longer than 30 seconds (Last.fm's rule), with "now playing" as it starts. Simkl gets films and episodes in
/// the history; AniList and MyAnimeList get an anime season's progress; Last.fm and ListenBrainz get songs. Audiobooks
/// go by their progress instead (Hardcover): every quarter of an hour while one plays, when it stops, and Read at the
/// end or when it's marked played. A send that fails while a service is down waits in the person's file and is retried every
/// quarter of an hour, when tokens close to running out are also refreshed.
/// </summary>
public sealed class Scrobbler : IHostedService, IDisposable
{
    /// <summary>How far through counts as watched.</summary>
    public const double WatchedAt = 0.85;

    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(15);

    /// <summary>The same play reported twice (stopped, then marked played) is sent once.</summary>
    private static readonly TimeSpan Repeat = TimeSpan.FromHours(6);

    /// <summary>Tokens with less than this left are refreshed by the quarter-hourly sweep.</summary>
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromDays(2);

    private readonly ISessionManager _sessions;
    private readonly IUserDataManager _userData;
    private readonly ILibraryManager _library;
    private readonly AccountStore _store;
    private readonly TokenService _tokens;
    private readonly IReadOnlyList<ITracker> _trackers;
    private readonly ILogger<Scrobbler> _log;
    private readonly ConcurrentDictionary<(Guid User, Guid Item), DateTimeOffset> _sent = new();

    /// <summary>
    /// When each song began, for its scrobble's time, and the furthest it's been reported played: some apps report a
    /// song stopped only once the next has loaded, at position 0.
    /// </summary>
    private readonly ConcurrentDictionary<(Guid User, Guid Item), (DateTimeOffset At, long Furthest)> _started = new();
    private readonly SemaphoreSlim _sweeping = new(1, 1);
    private Timer? _retry;

    public Scrobbler(ISessionManager sessions, IUserDataManager userData, ILibraryManager library, AccountStore store, TokenService tokens, IEnumerable<ITracker> trackers, ILogger<Scrobbler> log)
    {
        _sessions = sessions;
        _userData = userData;
        _library = library;
        _store = store;
        _tokens = tokens;
        _trackers = trackers.ToList();
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnPlaybackStart;
        _sessions.PlaybackProgress += OnPlaybackProgress;
        _sessions.PlaybackStopped += OnPlaybackStopped;
        _userData.UserDataSaved += OnUserDataSaved;
        _retry = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromMinutes(1), RetryEvery);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlaybackStart;
        _sessions.PlaybackProgress -= OnPlaybackProgress;
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        _userData.UserDataSaved -= OnUserDataSaved;
        _retry?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _retry?.Dispose();
        _sweeping.Dispose();
    }

    /// <summary>"Frieren S01E05", "Artist – Song", or a film's name, for the log.</summary>
    public static string Label(BaseItem item) =>
        WatchedTitle.From(item)?.Label
        ?? (item is Episode e ? $"{e.SeriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00}"
            : LastFm.LastFmClient.Song(item) is { } song ? $"{song["artist"]} – {song["track"]}" : item.Name);

    /// <summary>How often an audiobook's progress is sent while it plays.</summary>
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<(Guid User, Guid Item), DateTimeOffset> _progressSent = new();

    private static bool IsSong(BaseItem item) => LastFm.LastFmClient.Song(item) is not null;

    private static bool IsAudiobook(BaseItem item) => item is AudioBook;

    /// <summary>An audiobook at <paramref name="position"/>, or finished: sent to the services that follow progress.</summary>
    private void Progress(Guid userId, BaseItem item, long position, bool finished, bool retry)
    {
        _progressSent[(userId, item.Id)] = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                var tracking = await _store.GetAsync(userId).ConfigureAwait(false);
                foreach (var tracker in _trackers)
                {
                    if (tracker is not IProgressTracker progress || tracker.Account(tracking) is not { Scrobble: true })
                    {
                        continue;
                    }

                    var label = item.Name + (finished ? " (finished)" : $" ({(item.RunTimeTicks is long run && run > 0 ? 100 * position / run : 0)}%)");
                    var body = await progress.ProgressBodyAsync(item, position, finished, CancellationToken.None).ConfigureAwait(false);
                    if (body is null)
                    {
                        continue;
                    }

                    var result = await SendAsync(userId, tracker, label, body).ConfigureAwait(false);
                    if (result == Outcome.Retry && retry)
                    {
                        await Wait(userId, tracker, label, body, item.Id, DateTimeOffset.UtcNow).ConfigureAwait(false);
                    }
                    else if (result != Outcome.Unchanged && (retry || result != Outcome.Retry))
                    {
                        await _store.UpdateAsync(userId, t => t.Log.Add(Entry(tracker.Service, label, result))).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Aster Tracking: progress of {Item} for {User} failed", item.Name, userId);
            }
        });
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is null || e.Users is null || !IsSong(e.Item))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var user in e.Users)
        {
            _started[(user.Id, e.Item.Id)] = (now, 0);
            var userId = user.Id;
            var item = e.Item;
            _ = Task.Run(() => NowPlayingAsync(userId, item));
        }

        foreach (var old in _started.Where(pair => now - pair.Value.At > TimeSpan.FromHours(12)).Select(pair => pair.Key).ToList())
        {
            _started.TryRemove(old, out _);
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is null || e.Users is null || e.PlaybackPositionTicks is not long position)
        {
            return;
        }

        if (IsAudiobook(e.Item))
        {
            foreach (var user in e.Users)
            {
                var key = (user.Id, e.Item.Id);
                if (!_progressSent.TryGetValue(key, out var last))
                {
                    // The first report starts the clock: no send for merely starting.
                    _progressSent[key] = DateTimeOffset.UtcNow;
                }
                else if (DateTimeOffset.UtcNow - last >= ProgressEvery && !e.IsPaused)
                {
                    Progress(user.Id, e.Item, position, false, retry: false);
                }
            }

            return;
        }

        foreach (var user in e.Users)
        {
            var key = (user.Id, e.Item.Id);
            if (_started.TryGetValue(key, out var song) && position > song.Furthest)
            {
                _started[key] = (song.At, position);
            }
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is null || e.Users is null)
        {
            return;
        }

        var runtime = e.Item.RunTimeTicks ?? 0;
        var position = e.PlaybackPositionTicks ?? 0;
        if (IsAudiobook(e.Item))
        {
            // The end counts as finished: the last 2% is usually credits and the publisher's sign-off.
            var finished = e.PlayedToCompletion || (runtime > 0 && position >= runtime * 0.98);
            foreach (var user in e.Users)
            {
                _progressSent.TryRemove((user.Id, e.Item.Id), out _);
                if (position > 0 || finished)
                {
                    Progress(user.Id, e.Item, position, finished, retry: true);
                }
            }

            return;
        }

        if (IsSong(e.Item))
        {
            foreach (var user in e.Users)
            {
                var known = _started.TryRemove((user.Id, e.Item.Id), out var song);
                var played = Math.Max(position, known ? song.Furthest : 0);
                var began = known ? song.At : DateTimeOffset.UtcNow - TimeSpan.FromTicks(played);

                // Last.fm's rule: longer than 30 seconds, and played half way or four minutes.
                var counts = runtime > TimeSpan.FromSeconds(30).Ticks
                    && (e.PlayedToCompletion || played >= runtime / 2 || played >= TimeSpan.FromMinutes(4).Ticks);
                if (counts)
                {
                    // The same song again counts again, once the first play could have finished.
                    Watched(user.Id, e.Item, began, TimeSpan.FromTicks(Math.Max(runtime / 2, TimeSpan.FromSeconds(30).Ticks)));
                }
            }

            return;
        }

        var through = runtime > 0 ? (double)position / runtime : 0;
        if (!e.PlayedToCompletion && through < WatchedAt)
        {
            return;
        }

        foreach (var user in e.Users)
        {
            Watched(user.Id, e.Item, DateTimeOffset.UtcNow, Repeat);
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // A song marked played wasn't listened to: only films and episodes count that way. An audiobook marked played
        // is finished.
        if (e.SaveReason == UserDataSaveReason.TogglePlayed && e.UserData?.Played == true && e.Item is not null && IsAudiobook(e.Item))
        {
            Progress(e.UserId, e.Item, e.Item.RunTimeTicks ?? 0, true, retry: true);
            return;
        }

        if (e.SaveReason == UserDataSaveReason.TogglePlayed && e.UserData?.Played == true && e.Item is not null && !IsSong(e.Item))
        {
            Watched(e.UserId, e.Item, DateTimeOffset.UtcNow, Repeat);
        }
    }

    private async Task NowPlayingAsync(Guid userId, BaseItem item)
    {
        try
        {
            var tracking = await _store.GetAsync(userId).ConfigureAwait(false);
            foreach (var tracker in _trackers)
            {
                if (tracker is INowPlaying playing && tracker.Account(tracking) is { Scrobble: true })
                {
                    try
                    {
                        var token = await TokenAsync(userId, tracker, TimeSpan.FromDays(1)).ConfigureAwait(false);
                        if (token is not null)
                        {
                            await playing.NowPlayingAsync(item, token, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TrackerException or SignedOutException)
                    {
                        // "Now playing" is only a nicety: not retried, and a sign-out shows up on the scrobble.
                        _log.LogDebug("Aster Tracking: now playing on {Service} failed: {Error}", tracker.Service, ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Aster Tracking: now playing {Item} for {User} failed", item.Name, userId);
        }
    }

    private void Watched(Guid userId, BaseItem item, DateTimeOffset watchedAt, TimeSpan repeat)
    {
        var now = DateTimeOffset.UtcNow;
        if (_sent.TryGetValue((userId, item.Id), out var last) && now - last < repeat)
        {
            return;
        }

        _sent[(userId, item.Id)] = now;
        foreach (var old in _sent.Where(pair => now - pair.Value > Repeat).Select(pair => pair.Key).ToList())
        {
            _sent.TryRemove(old, out _);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SendAllAsync(userId, item, watchedAt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Aster Tracking: sending {Item} for {User} failed", item.Name, userId);
            }
        });
    }

    private async Task SendAllAsync(Guid userId, BaseItem item, DateTimeOffset watchedAt)
    {
        var tracking = await _store.GetAsync(userId).ConfigureAwait(false);
        var label = Label(item);
        foreach (var tracker in _trackers)
        {
            if (tracker.Account(tracking) is not { Scrobble: true })
            {
                continue;
            }

            string? body;
            try
            {
                body = await tracker.BodyAsync(item, watchedAt, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TrackerException ex) when (ex.Transient)
            {
                // The anime id map is down: the body is made again when it's retried.
                await Wait(userId, tracker, label, null, item.Id, watchedAt).ConfigureAwait(false);
                continue;
            }

            if (body is null)
            {
                _log.LogDebug("Aster Tracking: {Item} has nothing to match it by on {Service}", item.Name, tracker.Service);
                continue;
            }

            var result = await SendAsync(userId, tracker, label, body).ConfigureAwait(false);
            if (result == Outcome.Retry)
            {
                await Wait(userId, tracker, label, body, item.Id, watchedAt).ConfigureAwait(false);
            }
            else
            {
                await _store.UpdateAsync(userId, t => t.Log.Add(Entry(tracker.Service, label, result))).ConfigureAwait(false);
            }
        }
    }

    private Task Wait(Guid userId, ITracker tracker, string label, string? body, Guid itemId, DateTimeOffset watchedAt) =>
        _store.UpdateAsync(userId, t =>
        {
            t.Pending.Add(new PendingSend
            {
                Service = tracker.Service, Title = label, Body = body, ItemId = itemId, WatchedAt = watchedAt, Added = DateTimeOffset.UtcNow,
            });
            t.Log.Add(Entry(tracker.Service, label, Outcome.Retry));
        });

    /// <summary>Sends one body with a fresh token, signing the account out if the service no longer takes it.</summary>
    private async Task<Outcome> SendAsync(Guid userId, ITracker tracker, string label, string body)
    {
        try
        {
            var token = await TokenAsync(userId, tracker, TimeSpan.FromDays(1)).ConfigureAwait(false);
            if (token is null)
            {
                return Outcome.SignedOut;
            }

            var result = await tracker.SendAsync(body, token, CancellationToken.None).ConfigureAwait(false);
            _log.LogInformation("Aster Tracking: {Title} to {Service} for {User}: {Result}", label, tracker.Service, userId, result);
            return result switch
            {
                HistoryResult.Added => Outcome.Added,
                HistoryResult.NotFound => Outcome.NotFound,
                _ => Outcome.Unchanged,
            };
        }
        catch (SignedOutException)
        {
            _log.LogWarning("Aster Tracking: {Service} no longer takes {User}'s sign-in; it needs linking again", tracker.Service, userId);
            await _store.UpdateAsync(userId, t => tracker.SetAccount(t, null)).ConfigureAwait(false);
            return Outcome.SignedOut;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException || ex is TrackerException { Transient: true })
        {
            _log.LogWarning("Aster Tracking: {Service} unreachable for {Title} ({Error}); will retry", tracker.Service, label, ex.Message);
            return Outcome.Retry;
        }
        catch (TrackerException ex)
        {
            _log.LogWarning("Aster Tracking: {Service} refused {Title}: {Error}", tracker.Service, label, ex.Message);
            return Outcome.Failed;
        }
    }

    /// <summary>The account's token, refreshed first when it has less than <paramref name="ahead"/> left.</summary>
    private Task<string?> TokenAsync(Guid userId, ITracker tracker, TimeSpan ahead) => _tokens.TokenAsync(userId, tracker, ahead);

    /// <summary>Retries waiting sends and refreshes tokens about to run out, for everyone.</summary>
    private async Task SweepAsync()
    {
        if (!await _sweeping.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            foreach (var userId in _store.UserIds())
            {
                try
                {
                    await KeepSignedInAsync(userId).ConfigureAwait(false);
                    await RetryAsync(userId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Aster Tracking: the sweep for {User} failed", userId);
                }
            }
        }
        finally
        {
            _sweeping.Release();
        }
    }

    private async Task KeepSignedInAsync(Guid userId)
    {
        foreach (var tracker in _trackers)
        {
            try
            {
                await TokenAsync(userId, tracker, RefreshAhead).ConfigureAwait(false);
            }
            catch (SignedOutException)
            {
                _log.LogWarning("Aster Tracking: {Service} refused to refresh {User}'s sign-in; it needs linking again", tracker.Service, userId);
                await _store.UpdateAsync(userId, t => tracker.SetAccount(t, null)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TrackerException)
            {
                // Tried again next sweep.
            }
        }
    }

    private async Task RetryAsync(Guid userId)
    {
        var tracking = await _store.GetAsync(userId).ConfigureAwait(false);
        var down = new HashSet<string>();
        foreach (var send in tracking.Pending.ToList())
        {
            var tracker = _trackers.FirstOrDefault(t => t.Service == send.Service);
            if (tracker is null || tracker.Account(tracking) is null || down.Contains(send.Service))
            {
                continue;
            }

            var body = send.Body;
            var result = Outcome.Retry;
            if (body is null)
            {
                var item = _library.GetItemById(send.ItemId);
                try
                {
                    body = item is null ? null : await tracker.BodyAsync(item, send.WatchedAt, CancellationToken.None).ConfigureAwait(false);
                    result = body is null ? Outcome.NotFound : Outcome.Retry;
                }
                catch (TrackerException ex) when (ex.Transient)
                {
                    // Still down.
                }
            }

            if (body is not null)
            {
                result = await SendAsync(userId, tracker, send.Title, body).ConfigureAwait(false);
            }

            await _store.UpdateAsync(userId, t =>
            {
                var waiting = t.Pending.FirstOrDefault(p => p.Service == send.Service && p.ItemId == send.ItemId && p.Added == send.Added);
                if (waiting is null)
                {
                    return;
                }

                waiting.Attempts++;
                waiting.Body = body;
                if (result != Outcome.Retry || waiting.Attempts >= 96)
                {
                    t.Pending.Remove(waiting);
                    t.Log.Add(Entry(send.Service, send.Title, result == Outcome.Retry ? Outcome.Failed : result));
                }
            }).ConfigureAwait(false);

            if (result == Outcome.Retry)
            {
                down.Add(send.Service);
            }
        }
    }

    private static SendLog Entry(string service, string title, Outcome result) => new()
    {
        At = DateTimeOffset.UtcNow,
        Service = service,
        Title = title,
        Result = result switch
        {
            Outcome.Added => "added",
            Outcome.Unchanged => "already there",
            Outcome.NotFound => "not found there",
            Outcome.SignedOut => "signed out: link again",
            Outcome.Retry => "waiting to retry",
            _ => "refused",
        },
    };

    private enum Outcome
    {
        Added,
        Unchanged,
        NotFound,
        SignedOut,
        Retry,
        Failed,
    }
}
