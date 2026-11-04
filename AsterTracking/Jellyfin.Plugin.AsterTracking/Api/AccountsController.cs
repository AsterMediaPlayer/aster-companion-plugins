using System.Collections.Concurrent;
using System.Security.Claims;
using Jellyfin.Plugin.AsterTracking.Simkl;
using Jellyfin.Plugin.AsterTracking.Tracking;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AsterTracking.Api;

/// <summary>
/// Each person's linked accounts, for the apps' settings and the dashboard page (a signed-in person for themselves;
/// an administrator may name anyone with <c>userId</c>):
///   GET    /AsterTracking/Accounts                        what's linked, and the last sends
///   POST   /AsterTracking/Accounts/Simkl/Link             starts linking: a code to enter at simkl.com/pin
///   POST   /AsterTracking/Accounts/Simkl/Link/Complete    asks whether it's been entered: 200 linked, 202 not yet
///   PUT    /AsterTracking/Accounts/Simkl/Settings         { "Scrobble": false } to stop sending without unlinking
///   DELETE /AsterTracking/Accounts/Simkl                  unlinks
/// </summary>
[ApiController]
[Route("AsterTracking/Accounts")]
[Authorize]
public class AccountsController : ControllerBase
{
    private static readonly ConcurrentDictionary<Guid, DeviceCode> Linking = new();

    private readonly AccountStore _store;
    private readonly SimklClient _simkl;
    private readonly IUserManager _users;

    public AccountsController(AccountStore store, SimklClient simkl, IUserManager users)
    {
        _store = store;
        _simkl = simkl;
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
            Simkl = tracking.Simkl is { } simkl
                ? new AccountDto { Linked = true, Name = simkl.Name, LinkedAt = simkl.Linked, Scrobble = simkl.Scrobble }
                : new AccountDto(),
            Waiting = tracking.Pending.Count,
            Log = Enumerable.Reverse(tracking.Log).ToList(),
        };
    }

    [HttpPost("Simkl/Link")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<LinkDto>> StartSimkl([FromQuery] Guid? userId, CancellationToken cancellation)
    {
        if (Person(userId) is not Guid id)
        {
            return Forbid();
        }

        DeviceCode code;
        try
        {
            code = await _simkl.StartSignInAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SimklException or HttpRequestException or TaskCanceledException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        Linking[id] = code;
        return new LinkDto
        {
            Code = code.UserCode,
            Url = code.VerificationUrl,
            CompleteUrl = code.CompleteUrl,
            Interval = code.Interval,
            ExpiresIn = (int)(code.Expires - DateTimeOffset.UtcNow).TotalSeconds,
        };
    }

    [HttpPost("Simkl/Link/Complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<AccountDto>> CompleteSimkl([FromQuery] Guid? userId, CancellationToken cancellation)
    {
        if (Person(userId) is not Guid id)
        {
            return Forbid();
        }

        if (!Linking.TryGetValue(id, out var code) || code.Expires < DateTimeOffset.UtcNow)
        {
            Linking.TryRemove(id, out _);
            return StatusCode(StatusCodes.Status410Gone);
        }

        SimklTokens? tokens;
        string name;
        try
        {
            tokens = await _simkl.PollAsync(code.DeviceCodeValue, cancellation).ConfigureAwait(false);
            if (tokens is null)
            {
                return Accepted();
            }

            name = await _simkl.UserNameAsync(tokens.Access, cancellation).ConfigureAwait(false);
        }
        catch (SimklSignedOutException)
        {
            Linking.TryRemove(id, out _);
            return StatusCode(StatusCodes.Status410Gone);
        }
        catch (Exception ex) when (ex is SimklException or HttpRequestException or TaskCanceledException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        Linking.TryRemove(id, out _);
        var linked = DateTimeOffset.UtcNow;
        await _store.UpdateAsync(id, t => t.Simkl = new SimklAccount
        {
            AccessToken = tokens.Access,
            RefreshToken = tokens.Refresh,
            Expires = tokens.Expires,
            Name = name,
            Linked = linked,
        }).ConfigureAwait(false);
        return new AccountDto { Linked = true, Name = name, LinkedAt = linked, Scrobble = true };
    }

    [HttpPut("Simkl/Settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> SetSimkl([FromQuery] Guid? userId, [FromBody] AccountSettingsDto settings)
    {
        if (Person(userId) is not Guid id)
        {
            return Forbid();
        }

        var tracking = await _store.UpdateAsync(id, t =>
        {
            if (t.Simkl is not null && settings.Scrobble is bool scrobble)
            {
                t.Simkl.Scrobble = scrobble;
            }
        }).ConfigureAwait(false);
        return tracking.Simkl is null ? NotFound() : NoContent();
    }

    [HttpDelete("Simkl")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> UnlinkSimkl([FromQuery] Guid? userId)
    {
        if (Person(userId) is not Guid id)
        {
            return Forbid();
        }

        // Simkl has no revoke: the token is forgotten here, and the person can remove Aster at simkl.com/settings.
        await _store.UpdateAsync(id, t =>
        {
            t.Simkl = null;
            t.Pending.RemoveAll(p => p.Service == "simkl");
        }).ConfigureAwait(false);
        return NoContent();
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
}

public class AccountsDto
{
    public AccountDto Simkl { get; set; } = new();

    /// <summary>Sends waiting to be retried.</summary>
    public int Waiting { get; set; }

    /// <summary>The last sends, newest first.</summary>
    public List<SendLog> Log { get; set; } = new();
}

public class AccountDto
{
    public bool Linked { get; set; }

    public string? Name { get; set; }

    public DateTimeOffset? LinkedAt { get; set; }

    public bool Scrobble { get; set; }
}

public class AccountSettingsDto
{
    public bool? Scrobble { get; set; }
}

public class LinkDto
{
    /// <summary>The code to enter.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Where to enter it.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The address with the code in it, for a QR code, where the service gives one.</summary>
    public string? CompleteUrl { get; set; }

    /// <summary>Seconds to wait between Complete calls.</summary>
    public double Interval { get; set; }

    public int ExpiresIn { get; set; }
}
