using System.Net;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AsterTracking.Tracking;

/// <summary>
/// One tracking service, as the scrobbler drives it: what to send for a watched item, sending it with a person's
/// token, and refreshing that token.
/// </summary>
public interface ITracker
{
    /// <summary>"simkl", "anilist", "myanimelist", "lastfm", "listenbrainz", "hardcover": the name in routes, the person's file and the log.</summary>
    string Service { get; }

    /// <summary>Whether the server has an app for this service to sign in with (built in, or its own).</summary>
    bool Available { get; }

    LinkedAccount? Account(UserTracking tracking);

    void SetAccount(UserTracking tracking, LinkedAccount? account);

    /// <summary>The request to send for a watched film or episode, or null when this service has nothing to match it by.</summary>
    Task<string?> BodyAsync(BaseItem item, DateTimeOffset watchedAt, CancellationToken cancellation);

    Task<HistoryResult> SendAsync(string body, string token, CancellationToken cancellation);

    /// <summary>The account with fresh tokens, or null when the service has no way to refresh (AniList's year-long tokens).</summary>
    Task<LinkedAccount?> RefreshAsync(LinkedAccount account, CancellationToken cancellation);
}

/// <summary>A service that's told what's playing as it starts (Last.fm's "now playing"), as well as sent it afterwards.</summary>
public interface INowPlaying
{
    Task NowPlayingAsync(BaseItem item, string token, CancellationToken cancellation);
}

/// <summary>A service that follows how far through something the person is (Hardcover's audiobook progress).</summary>
public interface IProgressTracker
{
    /// <summary>The request for this position (or the end, when <paramref name="finished"/>), or null when the service doesn't take this item.</summary>
    Task<string?> ProgressBodyAsync(BaseItem item, long positionTicks, bool finished, CancellationToken cancellation);
}

public enum HistoryResult
{
    /// <summary>Added to the history, or the list's progress moved on.</summary>
    Added,

    /// <summary>Already there, or the list was already further along.</summary>
    Unchanged,

    NotFound,
}

/// <summary>Where a title stands on a watchlist after asking to put it on or take it off.</summary>
public enum WatchlistChange
{
    /// <summary>On the watchlist.</summary>
    On,

    /// <summary>Not on the watchlist.</summary>
    Off,

    /// <summary>On another of the person's lists (watching, completed, …), which is left alone.</summary>
    Elsewhere,

    /// <summary>The service doesn't know the title.</summary>
    NotFound,
}

/// <summary>A watchlist change, and the list the title is on when it's <see cref="WatchlistChange.Elsewhere"/>.</summary>
public readonly record struct WatchlistOutcome(WatchlistChange Change, string? Listed = null);

/// <summary>A service answered with something other than success.</summary>
public class TrackerException : Exception
{
    public TrackerException(string service, HttpStatusCode status)
        : base($"{service} answered {(int)status}")
    {
        Status = status;
    }

    public TrackerException(string service, HttpStatusCode status, string detail)
        : base($"{service}: {detail}")
    {
        Status = status;
    }

    public HttpStatusCode Status { get; }

    /// <summary>Worth retrying later: the service busy or down.</summary>
    public bool Transient => Status is HttpStatusCode.TooManyRequests || (int)Status >= 500 || Status == 0;
}

/// <summary>A person's token is no longer good (or a sign-in was refused): the account needs linking again.</summary>
public sealed class SignedOutException : Exception
{
    public SignedOutException(string service)
        : base($"{service} signed out")
    {
    }
}
