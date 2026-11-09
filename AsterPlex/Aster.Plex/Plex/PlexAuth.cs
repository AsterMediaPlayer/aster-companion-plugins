using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aster.Plex.Plex;

/// <summary>
/// Signs in each request with the Plex token it carries, the same one the app uses with the Plex server: in
/// <c>X-Plex-Token</c> (header or query, as Plex apps send it), or in an <c>Authorization</c> header the way the Aster
/// apps send it to Jellyfin (<c>MediaBrowser …, Token="…"</c>), so their tracking code is the same for both. The person
/// is put in the <see cref="UserIdClaim"/> claim, and the server's owner gets the Administrator role. The settings page
/// signs in with a cookie instead (<see cref="Web.SignIns"/>), standing for the token its person signed in with.
/// </summary>
public sealed partial class PlexAuth : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Plex";

    /// <summary>The person's id, read by the controllers copied from the Jellyfin plugin (which read "Jellyfin-UserId").</summary>
    public const string UserIdClaim = "Aster-UserId";

    public const string AdministratorRole = "Administrator";

    private readonly PlexPeople _people;
    private readonly Web.SignIns _signIns;

    public PlexAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PlexPeople people, Web.SignIns signIns)
        : base(options, logger, encoder)
    {
        _people = people;
        _signIns = signIns;
    }

    /// <summary>The token a request carries, or null.</summary>
    public static string? TokenOf(HttpRequest request)
    {
        if (request.Headers["X-Plex-Token"].ToString() is { Length: > 0 } header)
        {
            return header;
        }

        if (request.Query["X-Plex-Token"].ToString() is { Length: > 0 } query)
        {
            return query;
        }

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization[7..].Trim();
        }

        return MediaBrowserToken().Match(authorization) is { Success: true } match ? match.Groups[1].Value : null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // An app's token, or the settings page's sign-in.
        if ((TokenOf(Request) ?? _signIns.TokenOf(Request)) is not string token)
        {
            return AuthenticateResult.NoResult();
        }

        PlexPerson? person;
        try
        {
            person = await _people.ForTokenAsync(token, Context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexException)
        {
            return AuthenticateResult.Fail("plex.tv couldn't be reached to check the token");
        }

        if (person is null)
        {
            return AuthenticateResult.Fail("not someone with access to this Plex server");
        }

        var claims = new List<Claim>
        {
            new(UserIdClaim, person.Id.ToString()),
            new(ClaimTypes.Name, person.Name),
        };
        if (person.Owner)
        {
            claims.Add(new Claim(ClaimTypes.Role, AdministratorRole));
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }

    [GeneratedRegex("Token=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex MediaBrowserToken();
}
