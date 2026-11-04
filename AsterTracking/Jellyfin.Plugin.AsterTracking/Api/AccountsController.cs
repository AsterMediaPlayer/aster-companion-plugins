using System.Collections.Concurrent;
using System.Security.Claims;
using Jellyfin.Plugin.AsterTracking.AniList;
using Jellyfin.Plugin.AsterTracking.LastFm;
using Jellyfin.Plugin.AsterTracking.MyAnimeList;
using Jellyfin.Plugin.AsterTracking.Simkl;
using Jellyfin.Plugin.AsterTracking.Tracking;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterTracking.Api;

/// <summary>
/// Each person's linked accounts, for the apps' settings and the dashboard page (a signed-in person for themselves;
/// an administrator may name anyone with <c>userId</c>). <c>{service}</c> is simkl, anilist, myanimelist or lastfm.
///   GET    /AsterTracking/Accounts                           what's linked, and the last sends
///   POST   /AsterTracking/Accounts/{service}/Link            starts linking: { Url, and for Simkl Code to enter there; Paste when a code comes back to paste }
///   POST   /AsterTracking/Accounts/{service}/Link/Complete   Simkl, Last.fm: 200 linked, 202 not entered or approved yet. AniList, MyAnimeList: body { "Code": "…" } as pasted
///   PUT    /AsterTracking/Accounts/{service}/Settings        { "Scrobble": false } to stop sending without unlinking
///   DELETE /AsterTracking/Accounts/{service}                 unlinks
/// A link that ran out or was refused is 410; a service the server has no app for yet is 409.
/// </summary>
[ApiController]
[Route("AsterTracking/Accounts")]
[Authorize]
public class AccountsController : ControllerBase
{
    private static readonly TimeSpan PasteTime = TimeSpan.FromMinutes(15);

    /// <summary>Links under way, per person and service: Simkl's device code, or MyAnimeList's verifier.</summary>
    private static readonly ConcurrentDictionary<(Guid User, string Service), Linking> Pending = new();

    private readonly AccountStore _store;
    private readonly SimklClient _simkl;
    private readonly AniListClient _aniList;
    private readonly MyAnimeListClient _mal;
    private readonly LastFmClient _lastFm;
    private readonly IReadOnlyList<ITracker> _trackers;
    private readonly IUserManager _users;

    public AccountsController(AccountStore store, SimklClient simkl, AniListClient aniList, MyAnimeListClient mal, LastFmClient lastFm, IEnumerable<ITracker> trackers, IUserManager users)
    {
        _store = store;
        _simkl = simkl;
        _aniList = aniList;
        _mal = mal;
        _lastFm = lastFm;
        _trackers = trackers.ToList();
        _users = users;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountsDto>> GetAccounts([FromQuery] Guid? userId)
    {
        if (Person(userId) is not Guid id)
        {
            return Forbid();
        }

        var tracking = await _store.GetAsync(id).ConfigureAwait(false);
        return new AccountsDto
        {
            Simkl = Describe(_simkl, tracking),
            AniList = Describe(_aniList, tracking),
            MyAnimeList = Describe(_mal, tracking),
            LastFm = Describe(_lastFm, tracking),
            Waiting = tracking.Pending.Count,
            Log = Enumerable.Reverse(tracking.Log).ToList(),
        };
    }

    [HttpPost("{service}/Link")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LinkDto>> StartLink([FromRoute] string service, [FromQuery] Guid? userId, CancellationToken cancellation)
    {
        if (Person(userId) is not Guid id)
        {
            return Forbid();
        }

        var tracker = Tracker(service);
        if (tracker is null)
        {
            return NotFound();
        }

        if (!tracker.Available)
        {
            return Problem($"This server has no {service} app to sign in with yet: add one under Developer apps.", statusCode: StatusCodes.Status409Conflict);
        }

        switch (tracker)
        {
            case SimklClient:
                DeviceCode code;
                try
                {
                    code = await _simkl.StartSignInAsync(cancellation).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TrackerException or HttpRequestException or TaskCanceledException)
                {
                    return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
                }

                Pending[(id, tracker.Service)] = new Linking(code.Expires, code.DeviceCodeValue);
                return new LinkDto
                {
                    Code = code.UserCode,
                    Url = code.VerificationUrl,
                    CompleteUrl = code.CompleteUrl,
                    Interval = code.Interval,
                    ExpiresIn = (int)(code.Expires - DateTimeOffset.UtcNow).TotalSeconds,
                };

            case LastFmClient:
                string token, approve;
                try
                {
                    (token, approve) = await _lastFm.StartSignInAsync(cancellation).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TrackerException or SignedOutException or HttpRequestException or TaskCanceledException)
                {
                    return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
                }

                // Last.fm's tokens are good for an hour.
                Pending[(id, tracker.Service)] = new Linking(DateTimeOffset.UtcNow.AddMinutes(55), token);
                return new LinkDto { Url = approve, Interval = 5, ExpiresIn = 55 * 60 };

            case MyAnimeListClient:
                var (url, verifier) = MyAnimeListClient.StartSignIn();
                Pending[(id, tracker.Service)] = new Linking(DateTimeOffset.UtcNow + PasteTime, verifier);
                return new LinkDto { Url = url, Paste = true, ExpiresIn = (int)PasteTime.TotalSeconds };

            default:
                return new LinkDto { Url = AniListClient.SignInUrl, Paste = true };
        }
    }

    [HttpPost("{service}/Link/Complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<AccountDto>> CompleteLink([FromRoute] string service, [FromQuery] Guid? userId, [FromBody] PastedDto? pasted, CancellationToken cancellation)
    {
        if (Person(userId) is not Guid id)
        {
            return Forbid();
        }

        var tracker = Tracker(service);
        if (tracker is null)
        {
            return NotFound();
        }

        var key = (id, tracker.Service);
        Pending.TryGetValue(key, out var linking);
        if (linking is not null && linking.Expires < DateTimeOffset.UtcNow)
        {
            Pending.TryRemove(key, out _);
            linking = null;
        }

        var code = Pasted(pasted?.Code);
        if (tracker is not (SimklClient or LastFmClient) && code is null)
        {
            return Problem("Paste the code the sign-in page showed.", statusCode: StatusCodes.Status400BadRequest);
        }

        LinkedAccount account;
        try
        {
            switch (tracker)
            {
                case SimklClient:
                    if (linking is null)
                    {
                        return StatusCode(StatusCodes.Status410Gone);
                    }

                    var tokens = await _simkl.PollAsync(linking.Secret, cancellation).ConfigureAwait(false);
                    if (tokens is null)
                    {
                        return Accepted();
                    }

                    account = new LinkedAccount
                    {
                        AccessToken = tokens.Access,
                        RefreshToken = tokens.Refresh,
                        Expires = tokens.Expires,
                        Name = await _simkl.UserNameAsync(tokens.Access, cancellation).ConfigureAwait(false),
                        Linked = DateTimeOffset.UtcNow,
                    };
                    break;

                case LastFmClient:
                    if (linking is null)
                    {
                        return StatusCode(StatusCodes.Status410Gone);
                    }

                    if (await _lastFm.PollAsync(linking.Secret, cancellation).ConfigureAwait(false) is not LinkedAccount approved)
                    {
                        return Accepted();
                    }

                    account = approved;
                    break;

                case MyAnimeListClient:
                    if (linking is null)
                    {
                        return StatusCode(StatusCodes.Status410Gone);
                    }

                    account = await _mal.LinkAsync(code!, linking.Secret, cancellation).ConfigureAwait(false);
                    break;

                default:
                    account = await _aniList.LinkAsync(code!, cancellation).ConfigureAwait(false);
                    break;
            }
        }
        catch (SignedOutException)
        {
            Pending.TryRemove(key, out _);
            return StatusCode(StatusCodes.Status410Gone);
        }
        catch (Exception ex) when (ex is TrackerException or HttpRequestException or TaskCanceledException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        Pending.TryRemove(key, out _);
        await _store.UpdateAsync(id, t => tracker.SetAccount(t, account)).ConfigureAwait(false);
        return Describe(tracker, account);
    }

    [HttpPut("{service}/Settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> SetSettings([FromRoute] string service, [FromQuery] Guid? userId, [FromBody] AccountSettingsDto settings)
    {
        if (Person(userId) is not Guid id)
        {
            return Forbid();
        }

        if (Tracker(service) is not ITracker tracker)
        {
            return NotFound();
        }

        var tracking = await _store.UpdateAsync(id, t =>
        {
            if (tracker.Account(t) is LinkedAccount account && settings.Scrobble is bool scrobble)
            {
                account.Scrobble = scrobble;
            }
        }).ConfigureAwait(false);
        return tracker.Account(tracking) is null ? NotFound() : NoContent();
    }

    [HttpDelete("{service}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Unlink([FromRoute] string service, [FromQuery] Guid? userId)
    {
        if (Person(userId) is not Guid id)
        {
            return Forbid();
        }

        if (Tracker(service) is not ITracker tracker)
        {
            return NotFound();
        }

        // None of the three has a revoke: the token is forgotten here, and the person can remove Aster in the
        // service's own settings.
        await _store.UpdateAsync(id, t =>
        {
            tracker.SetAccount(t, null);
            t.Pending.RemoveAll(p => p.Service == tracker.Service);
        }).ConfigureAwait(false);
        return NoContent();
    }

    private ITracker? Tracker(string service) =>
        _trackers.FirstOrDefault(t => string.Equals(t.Service, service, StringComparison.OrdinalIgnoreCase));

    private static AccountDto Describe(ITracker tracker, UserTracking tracking) =>
        tracker.Account(tracking) is LinkedAccount account ? Describe(tracker, account) : new AccountDto { Available = tracker.Available };

    private static AccountDto Describe(ITracker tracker, LinkedAccount account) => new()
    {
        Available = tracker.Available,
        Linked = true,
        Name = account.Name,
        LinkedAt = account.Linked,
        Expires = tracker is AniListClient ? account.Expires : null,
        Scrobble = account.Scrobble,
    };

    /// <summary>The code as pasted, or the code out of a whole pasted address ("…?code=…&amp;state=…").</summary>
    internal static string? Pasted(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Query.Length > 1)
        {
            foreach (var pair in uri.Query[1..].Split('&'))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == "code")
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
        }

        return text;
    }

    /// <summary>The person asked about: the caller, or anyone for an administrator; null when that's not allowed.</summary>
    private Guid? Person(Guid? userId)
    {
        // An API key has no person of its own (an empty id), so it has to name one.
        Guid.TryParse(User.FindFirstValue("Jellyfin-UserId"), out var caller);
        if (userId is not Guid asked || asked == Guid.Empty || asked == caller)
        {
            return caller == Guid.Empty ? null : caller;
        }

        return User.IsInRole("Administrator") && _users.GetUserById(asked) is not null ? asked : null;
    }

    private sealed record Linking(DateTimeOffset Expires, string Secret);
}

public class AccountsDto
{
    public AccountDto Simkl { get; set; } = new();

    public AccountDto AniList { get; set; } = new();

    public AccountDto MyAnimeList { get; set; } = new();

    public AccountDto LastFm { get; set; } = new();

    /// <summary>Sends waiting to be retried.</summary>
    public int Waiting { get; set; }

    /// <summary>The last sends, newest first.</summary>
    public List<SendLog> Log { get; set; } = new();
}

public class AccountDto
{
    /// <summary>Whether the server has an app for this service to link with.</summary>
    public bool Available { get; set; }

    public bool Linked { get; set; }

    public string? Name { get; set; }

    public DateTimeOffset? LinkedAt { get; set; }

    /// <summary>When the link runs out and needs doing again (AniList's year; the others refresh themselves).</summary>
    public DateTimeOffset? Expires { get; set; }

    public bool Scrobble { get; set; }
}

public class AccountSettingsDto
{
    public bool? Scrobble { get; set; }
}

public class PastedDto
{
    public string? Code { get; set; }
}

public class LinkDto
{
    /// <summary>Where to go: the page to enter <see cref="Code"/> on, or the service's sign-in.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Simkl's code to enter (none for Last.fm, where the person only approves Aster at <see cref="Url"/>).</summary>
    public string? Code { get; set; }

    /// <summary>The address with the code in it, for a QR code, where the service gives one.</summary>
    public string? CompleteUrl { get; set; }

    /// <summary>Whether the sign-in ends with a code to paste back to Complete (AniList, MyAnimeList).</summary>
    public bool Paste { get; set; }

    /// <summary>Seconds to wait between Complete calls (Simkl, Last.fm).</summary>
    public double Interval { get; set; }

    public int ExpiresIn { get; set; }
}
