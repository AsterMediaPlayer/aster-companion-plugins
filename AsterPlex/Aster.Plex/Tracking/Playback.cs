namespace Aster.Plex.Tracking;

/// <summary>
/// One moment of someone's playback, in the shape the Jellyfin plugin's scrobbler reads Jellyfin's session events
/// (<c>Item</c>, <c>Users</c>, <c>PlaybackPositionTicks</c>, <c>IsPaused</c>, <c>PlayedToCompletion</c>), so its logic is
/// copied unchanged. On Plex, <see cref="Plex.PlexWatcher"/> makes them from the server's sessions.
/// </summary>
public sealed class PlaybackEvent
{
    public PlaybackEvent(Guid userId, PlayedItem item, long? positionTicks, bool isPaused = false, bool playedToCompletion = false, DateTimeOffset? at = null)
    {
        At = at;
        Item = item;
        Users = [new Listener(userId)];
        PlaybackPositionTicks = positionTicks;
        IsPaused = isPaused;
        PlayedToCompletion = playedToCompletion;
    }

    public PlayedItem Item { get; }

    /// <summary>Who's playing it: one person on Plex (Jellyfin can have several in a SyncPlay group).</summary>
    public IReadOnlyList<Listener> Users { get; }

    public long? PlaybackPositionTicks { get; }

    public bool IsPaused { get; }

    public bool PlayedToCompletion { get; }

    /// <summary>When it was played, for one heard of afterwards (from the history).</summary>
    public DateTimeOffset? At { get; }
}

/// <summary>A person playing something.</summary>
public sealed record Listener(Guid Id);

/// <summary>Where playback is heard from: the media server's sessions and history.</summary>
public interface IPlaybackSource
{
    event EventHandler<PlaybackEvent>? PlaybackStart;

    event EventHandler<PlaybackEvent>? PlaybackProgress;

    event EventHandler<PlaybackEvent>? PlaybackStopped;

    /// <summary>A film or episode watched, or a song played, that the sessions didn't show (played while the container was down, or marked played).</summary>
    event EventHandler<PlaybackEvent>? MarkedPlayed;
}

/// <summary>Looks up an item again by its id, for a send retried later whose body couldn't be made at the time.</summary>
public interface IItemLookup
{
    Task<PlayedItem?> GetItemAsync(Guid id, CancellationToken cancellation);
}
