using System.Collections.Concurrent;
using Jellyfin.Plugin.AsterTracking.Simkl;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AsterTracking.Tracking;

/// <summary>
/// Adds what each person watches, in any app, to the history of the services they linked: once a film or episode is
/// stopped past 85% (the Aster apps' rule), or marked played by hand. A send that fails while a service is down waits
/// in the person's file and is retried every quarter of an hour.
/// </summary>
public sealed class Scrobbler : IHostedService, IDisposable
{
    /// <summary>How far through counts as watched.</summary>
    public const double WatchedAt = 0.85;

    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(15);

    /// <summary>The same play reported twice (stopped, then marked played) is sent once.</summary>
    private static readonly TimeSpan Repeat = TimeSpan.FromHours(6);

    private readonly ISessionManager _sessions;
    private readonly IUserDataManager _userData;
    private readonly AccountStore _store;
    private readonly SimklClient _simkl;
    private readonly ILogger<Scrobbler> _log;
    private readonly ConcurrentDictionary<(Guid User, Guid Item), DateTimeOffset> _sent = new();
    private Timer? _retry;

    public Scrobbler(ISessionManager sessions, IUserDataManager userData, AccountStore store, SimklClient simkl, ILogger<Scrobbler> log)
    {
        _sessions = sessions;
        _userData = userData;
        _store = store;
        _simkl = simkl;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped += OnPlaybackStopped;
        _userData.UserDataSaved += OnUserDataSaved;
        _retry = new Timer(_ => _ = RetryAllAsync(), null, RetryEvery, RetryEvery);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        _userData.UserDataSaved -= OnUserDataSaved;
        _retry?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _retry?.Dispose();

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
                await SendAsync(userId, item, now).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Aster Tracking: sending {Item} for {User} failed", item.Name, userId);
            }
        });
    }

    private async Task SendAsync(Guid userId, BaseItem item, DateTimeOffset watchedAt)
    {
        var tracking = await _store.GetAsync(userId).ConfigureAwait(false);
        if (tracking.Simkl is not { Scrobble: true })
        {
            return;
        }

        var title = WatchedTitle.From(item);
        if (title is null)
        {
            _log.LogDebug("Aster Tracking: {Item} has no ids to send", item.Name);
            return;
        }

        var body = title.HistoryBody(watchedAt).ToJsonString();
        var result = await SimklSendAsync(userId, title.Label, body).ConfigureAwait(false);
        await _store.UpdateAsync(userId, t =>
        {
            if (result == Outcome.Retry)
            {
                t.Pending.Add(new PendingSend { Service = "simkl", Title = title.Label, Body = body, Added = DateTimeOffset.UtcNow });
            }

            t.Log.Add(Entry(title.Label, result));
        }).ConfigureAwait(false);
    }

    /// <summary>Sends one history body with a fresh token, signing the account out if Simkl no longer takes it.</summary>
    private async Task<Outcome> SimklSendAsync(Guid userId, string label, string body)
    {
        try
        {
            var token = await SimklTokenAsync(userId).ConfigureAwait(false);
            if (token is null)
            {
                return Outcome.SignedOut;
            }

            var result = await _simkl.AddToHistoryAsync(body, token, CancellationToken.None).ConfigureAwait(false);
            _log.LogInformation("Aster Tracking: {Title} to Simkl for {User}: {Result}", label, userId, result);
            return result switch
            {
                HistoryResult.Added => Outcome.Added,
                HistoryResult.NotFound => Outcome.NotFound,
                _ => Outcome.Unchanged,
            };
        }
        catch (SimklSignedOutException)
        {
            _log.LogWarning("Aster Tracking: Simkl no longer takes {User}'s sign-in; it needs linking again", userId);
            await _store.UpdateAsync(userId, t => t.Simkl = null).ConfigureAwait(false);
            return Outcome.SignedOut;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException || ex is SimklException { Transient: true })
        {
            _log.LogWarning("Aster Tracking: Simkl unreachable for {Title} ({Error}); will retry", label, ex.Message);
            return Outcome.Retry;
        }
        catch (SimklException ex)
        {
            _log.LogWarning("Aster Tracking: Simkl refused {Title}: {Error}", label, ex.Message);
            return Outcome.Failed;
        }
    }

    /// <summary>The account's token, refreshed when it has under two weeks left.</summary>
    private async Task<string?> SimklTokenAsync(Guid userId)
    {
        var account = (await _store.GetAsync(userId).ConfigureAwait(false)).Simkl;
        if (account is null)
        {
            return null;
        }

        if (account.Expires is DateTimeOffset expires && expires - DateTimeOffset.UtcNow < TimeSpan.FromDays(14) && account.RefreshToken is string refresh)
        {
            var fresh = await _simkl.RefreshAsync(refresh, CancellationToken.None).ConfigureAwait(false);
            await _store.UpdateAsync(userId, t =>
            {
                if (t.Simkl is not null)
                {
                    t.Simkl.AccessToken = fresh.Access;
                    t.Simkl.RefreshToken = fresh.Refresh;
                    t.Simkl.Expires = fresh.Expires;
                }
            }).ConfigureAwait(false);
            return fresh.Access;
        }

        return account.AccessToken;
    }

    private async Task RetryAllAsync()
    {
        foreach (var userId in _store.UserIds())
        {
            try
            {
                var tracking = await _store.GetAsync(userId).ConfigureAwait(false);
                foreach (var send in tracking.Pending.ToList())
                {
                    if (send.Service != "simkl" || tracking.Simkl is null)
                    {
                        continue;
                    }

                    var result = await SimklSendAsync(userId, send.Title, send.Body).ConfigureAwait(false);
                    await _store.UpdateAsync(userId, t =>
                    {
                        var waiting = t.Pending.FirstOrDefault(p => p.Body == send.Body && p.Added == send.Added);
                        if (waiting is null)
                        {
                            return;
                        }

                        waiting.Attempts++;
                        if (result != Outcome.Retry || waiting.Attempts >= 96)
                        {
                            t.Pending.Remove(waiting);
                            t.Log.Add(Entry(send.Title, result));
                        }
                    }).ConfigureAwait(false);

                    if (result == Outcome.Retry)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Aster Tracking: retrying {User}'s sends failed", userId);
            }
        }
    }

    private static SendLog Entry(string title, Outcome result) => new()
    {
        At = DateTimeOffset.UtcNow,
        Service = "simkl",
        Title = title,
        Result = result switch
        {
            Outcome.Added => "added",
            Outcome.Unchanged => "already there",
            Outcome.NotFound => "not found on Simkl",
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
