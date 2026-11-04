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
/// Sends what each person watches, in any app, to the services they linked: once a film or episode is stopped past 85%
/// (the Aster apps' rule), or marked played by hand. Simkl gets it in the history; AniList and MyAnimeList get an
/// anime season's progress. A send that fails while a service is down waits in the person's file and is retried every
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
    private readonly IReadOnlyList<ITracker> _trackers;
    private readonly ILogger<Scrobbler> _log;
    private readonly ConcurrentDictionary<(Guid User, Guid Item), DateTimeOffset> _sent = new();
    private readonly SemaphoreSlim _sweeping = new(1, 1);
    private Timer? _retry;

    public Scrobbler(ISessionManager sessions, IUserDataManager userData, ILibraryManager library, AccountStore store, IEnumerable<ITracker> trackers, ILogger<Scrobbler> log)
    {
        _sessions = sessions;
        _userData = userData;
        _library = library;
        _store = store;
        _trackers = trackers.ToList();
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped += OnPlaybackStopped;
        _userData.UserDataSaved += OnUserDataSaved;
        _retry = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromMinutes(1), RetryEvery);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
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

    /// <summary>"Frieren S01E05", or a film's name, for the log.</summary>
    public static string Label(BaseItem item) =>
        WatchedTitle.From(item)?.Label
        ?? (item is Episode e ? $"{e.SeriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00}" : item.Name);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is null || e.Users is null)
        {
            return;
        }

        var runtime = e.Item.RunTimeTicks ?? 0;
        var through = runtime > 0 && e.PlaybackPositionTicks is long position ? (double)position / runtime : 0;
        if (!e.PlayedToCompletion && through < WatchedAt)
        {
            return;
        }

        foreach (var user in e.Users)
        {
            Watched(user.Id, e.Item);
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason == UserDataSaveReason.TogglePlayed && e.UserData?.Played == true && e.Item is not null)
        {
            Watched(e.UserId, e.Item);
        }
    }

    private void Watched(Guid userId, BaseItem item)
    {
        var now = DateTimeOffset.UtcNow;
        if (_sent.TryGetValue((userId, item.Id), out var last) && now - last < Repeat)
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
                await SendAllAsync(userId, item, now).ConfigureAwait(false);
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
    private async Task<string?> TokenAsync(Guid userId, ITracker tracker, TimeSpan ahead)
    {
        var account = tracker.Account(await _store.GetAsync(userId).ConfigureAwait(false));
        if (account is null)
        {
            return null;
        }

        if (account.Expires is DateTimeOffset expires && expires - DateTimeOffset.UtcNow < ahead
            && await tracker.RefreshAsync(account, CancellationToken.None).ConfigureAwait(false) is LinkedAccount fresh)
        {
            await _store.UpdateAsync(userId, t =>
            {
                // Kept only if the account wasn't unlinked meanwhile; its settings as they are now.
                if (tracker.Account(t) is LinkedAccount now)
                {
                    fresh.Scrobble = now.Scrobble;
                    tracker.SetAccount(t, fresh);
                }
            }).ConfigureAwait(false);
            return fresh.AccessToken;
        }

        return account.AccessToken;
    }

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
