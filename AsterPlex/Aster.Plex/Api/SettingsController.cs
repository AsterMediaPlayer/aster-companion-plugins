using System.Text.Json.Nodes;
using Aster.Plex.Plex;
using Aster.Plex.Tracking;
using Aster.Plex.Web;
using Microsoft.AspNetCore.Mvc;

namespace Aster.Plex.Api;

/// <summary>
/// The settings page's own calls (the page is wwwroot/aster). Signing in is plex.tv's: a code made here, approved at
/// app.plex.tv, then exchanged for the person's token, which the page's cookie stands for (<see cref="SignIns"/>).
///   GET  /aster/api/me                who's signed in, and whether the container is set up
///   POST /aster/api/signin            starts a sign-in: { Id, Url } to open
///   POST /aster/api/signin/{id}       200 signed in (sets the cookie), 202 not yet, 410 ran out
///   POST /aster/api/signout
///   GET  /aster/api/servers           the signed-in person's own Plex servers and their addresses, to set up with
///   POST /aster/api/setup             { MachineId, PlexUrl }: watch this server, as the signed-in person (its owner)
///   GET  /aster/api/names             the server's people's names (for the Channels page)
///   GET  /aster/api/settings          the owner's settings, the server's music libraries and people
///   PUT  /aster/api/settings          changes them; the public address is added to (or taken off) Plex's Custom server access URLs
/// Everything but signing in needs the owner once the container is set up; before that, setup takes anyone signed in
/// who owns the server they pick.
/// </summary>
[ApiController]
[Route("aster/api")]
public sealed class SettingsController : ControllerBase
{
    private readonly SignIns _signIns;
    private readonly PlexServer _plex;
    private readonly PlexPeople _people;
    private readonly IReadOnlyList<ITracker> _trackers;
    private readonly ILogger<SettingsController> _log;

    public SettingsController(SignIns signIns, PlexServer plex, PlexPeople people, IEnumerable<ITracker> trackers, ILogger<SettingsController> log)
    {
        _signIns = signIns;
        _plex = plex;
        _people = people;
        _trackers = trackers.ToList();
        _log = log;
    }

    [HttpGet("me")]
    public async Task<ActionResult<MeDto>> Me(CancellationToken cancellation)
    {
        var me = new MeDto { Configured = PlexServer.Configured, Version = typeof(SettingsController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
        if (_signIns.TokenOf(Request) is not string token)
        {
            return me;
        }

        me.SignedIn = true;
        if (PlexServer.Configured && await _people.ForTokenAsync(token, cancellation).ConfigureAwait(false) is PlexPerson person)
        {
            me.Name = person.Name;
            me.Owner = person.Owner;
            me.OnServer = true;
            me.UserId = person.Id;
        }
        else
        {
            me.Name = (await _plex.PlexTvAsync("api/v2/user", token, cancellation).ConfigureAwait(false)).Text("title");
        }

        if (PlexServer.Configured)
        {
            try
            {
                var identity = await _plex.GetAsync("/", cancellation).ConfigureAwait(false);
                me.ServerName = identity.Text("friendlyName");
                me.Reachable = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexException)
            {
                me.Reachable = false;
            }
        }

        return me;
    }

    [HttpPost("signin")]
    public async Task<ActionResult<SignInDto>> StartSignIn([FromBody] SignInStartDto? body, CancellationToken cancellation)
    {
        JsonNode? pin;
        try
        {
            pin = await _plex.PlexTvPostAsync("api/v2/pins?strong=true", cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexException)
        {
            return Problem("plex.tv couldn't be reached.", statusCode: StatusCodes.Status502BadGateway);
        }

        if (pin.Long("id") is not long id || pin.Text("code") is not string code)
        {
            return Problem("plex.tv didn't give a sign-in code.", statusCode: StatusCodes.Status502BadGateway);
        }

        var back = body?.ReturnUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" ? url : null;
        var address = "https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(PlexServer.ClientId)
            + "&code=" + Uri.EscapeDataString(code)
            + "&context%5Bdevice%5D%5Bproduct%5D=" + Uri.EscapeDataString(PlexServer.Product)
            + (back is null ? string.Empty : "&forwardUrl=" + Uri.EscapeDataString(back));
        return new SignInDto { Id = id, Code = code, Url = address };
    }

    [HttpPost("signin/{id:long}")]
    public async Task<ActionResult> CompleteSignIn([FromRoute] long id, [FromQuery] string code, CancellationToken cancellation)
    {
        JsonNode? pin;
        try
        {
            pin = await _plex.PlexTvAsync($"api/v2/pins/{id}?code={Uri.EscapeDataString(code ?? string.Empty)}", string.Empty, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexException)
        {
            return Problem("plex.tv couldn't be reached.", statusCode: StatusCodes.Status502BadGateway);
        }

        if (pin is null)
        {
            return StatusCode(StatusCodes.Status410Gone);
        }

        if (pin.Text("authToken") is not string token || token == "null")
        {
            return Accepted();
        }

        _signIns.SignIn(HttpContext, token);
        return Ok();
    }

    [HttpPost("signout")]
    public ActionResult SignOutOfPage()
    {
        _signIns.SignOut(HttpContext);
        return Ok();
    }

    [HttpGet("servers")]
    public async Task<ActionResult<List<ServerDto>>> Servers(CancellationToken cancellation)
    {
        if (await SetterAsync(cancellation).ConfigureAwait(false) is not string token)
        {
            return Forbid();
        }

        return await OwnedServersAsync(token, cancellation).ConfigureAwait(false);
    }

    [HttpPost("setup")]
    public async Task<ActionResult> Setup([FromBody] SetupDto setup, CancellationToken cancellation)
    {
        if (await SetterAsync(cancellation).ConfigureAwait(false) is not string token)
        {
            return Forbid();
        }

        var servers = await OwnedServersAsync(token, cancellation).ConfigureAwait(false);
        if (servers.FirstOrDefault(s => s.MachineId == setup.MachineId) is not ServerDto server)
        {
            return Problem("That isn't one of your own Plex servers.", statusCode: StatusCodes.Status403Forbidden);
        }

        var url = setup.PlexUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (await _plex.MachineIdAtAsync(url, cancellation).ConfigureAwait(false) != server.MachineId)
        {
            return Problem($"{server.Name} doesn't answer at {url} from here. Pick another of its addresses, or type the one this container reaches it at.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        CompanionSettings.Update(s =>
        {
            s.PlexUrl = url;
            s.PlexToken = token;
        });
        _people.Reset();
        _log.LogInformation("Aster for Plex: set up for {Server} at {Url}", server.Name, url);
        return Ok();
    }

    /// <summary>The names of the server's people, for a channel kept to some of them (aster:user=…): anyone on the server may ask.</summary>
    [HttpGet("names")]
    public async Task<ActionResult<List<string>>> Names(CancellationToken cancellation)
    {
        if (!PlexServer.Configured || _signIns.TokenOf(Request) is not string token || await _people.ForTokenAsync(token, cancellation).ConfigureAwait(false) is null)
        {
            return Forbid();
        }

        return (await _people.EveryoneAsync(cancellation).ConfigureAwait(false)).Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [HttpGet("settings")]
    public async Task<ActionResult<SettingsDto>> GetSettings(CancellationToken cancellation)
    {
        if (!await IsOwnerAsync(cancellation).ConfigureAwait(false))
        {
            return Forbid();
        }

        var settings = CompanionSettings.Current;
        var dto = new SettingsDto
        {
            PlexUrl = settings.PlexUrl,
            PublicUrl = settings.PublicUrl,
            PublishToPlex = settings.PublishToPlex,
            AudiobookLibraries = settings.AudiobookLibraries,
            SimklClientId = settings.SimklClientId,
            AniListClientId = settings.AniListClientId,
            MyAnimeListClientId = settings.MyAnimeListClientId,
            HardcoverClientId = settings.HardcoverClientId,
            LastFmApiKey = settings.LastFmApiKey,
            LastFmSharedSecretSet = !string.IsNullOrEmpty(settings.LastFmSharedSecret),
            Services = _trackers.Select(t => new ServiceDto { Service = t.Service, Available = t.Available }).ToList(),
        };

        try
        {
            var custom = await _plex.PreferenceAsync("customConnections", cancellation).ConfigureAwait(false) ?? string.Empty;
            dto.CustomConnections = Split(custom);
            var sections = await _plex.GetAsync("/library/sections", cancellation).ConfigureAwait(false);
            dto.MusicLibraries = sections.List("Directory").Where(d => d.Text("type") == "artist")
                .Select(d => new LibraryDto { Id = d.Text("key") ?? string.Empty, Title = d.Text("title") ?? string.Empty, Audiobooks = PlexItems.IsAudiobookLibrary(d.Text("title"), d.Text("key")) })
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexException)
        {
            dto.Problem = $"Plex couldn't be read at {settings.PlexUrl}: {ex.Message}";
        }

        dto.People = (await _people.EveryoneAsync(cancellation).ConfigureAwait(false))
            .Select(p => new PersonDto { Id = p.Id, Name = p.Name, Owner = p.Owner }).ToList();
        return dto;
    }

    [HttpPut("settings")]
    public async Task<ActionResult> SetSettings([FromBody] SettingsDto change, CancellationToken cancellation)
    {
        if (!await IsOwnerAsync(cancellation).ConfigureAwait(false))
        {
            return Forbid();
        }

        var publicUrl = change.PublicUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (publicUrl.Length > 0 && (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")))
        {
            return Problem("The public address has to be a full http:// or https:// address.", statusCode: StatusCodes.Status400BadRequest);
        }

        var plexUrl = change.PlexUrl?.Trim().TrimEnd('/') ?? CompanionSettings.Current.PlexUrl;
        if (plexUrl != CompanionSettings.Current.PlexUrl
            && await _plex.MachineIdAtAsync(plexUrl, cancellation).ConfigureAwait(false) != await _plex.MachineIdAsync(cancellation).ConfigureAwait(false))
        {
            return Problem($"The server doesn't answer at {plexUrl} from here.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        static string Clean(string? value) => value?.Trim() ?? string.Empty;
        CompanionSettings.Update(s =>
        {
            s.PlexUrl = plexUrl;
            s.PublicUrl = publicUrl;
            s.PublishToPlex = change.PublishToPlex;
            s.AudiobookLibraries = change.AudiobookLibraries?.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList() ?? new();
            s.SimklClientId = Clean(change.SimklClientId);
            s.AniListClientId = Clean(change.AniListClientId);
            s.MyAnimeListClientId = Clean(change.MyAnimeListClientId);
            s.HardcoverClientId = Clean(change.HardcoverClientId);
            s.LastFmApiKey = Clean(change.LastFmApiKey);

            // The secret is never sent back to the page: blank keeps it, unless the key's cleared too.
            if (!string.IsNullOrWhiteSpace(change.LastFmSharedSecret))
            {
                s.LastFmSharedSecret = change.LastFmSharedSecret.Trim();
            }
            else if (s.LastFmApiKey.Length == 0)
            {
                s.LastFmSharedSecret = string.Empty;
            }
        });

        try
        {
            await PublishAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexException)
        {
            return Problem($"Saved, but Plex's Custom server access URLs couldn't be changed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
        }

        return Ok();
    }

    /// <summary>
    /// Puts the public address in Plex's Custom server access URLs, which plex.tv hands every app as one of the
    /// server's addresses, and takes off the one put there before. The admin's own entries are left as they are.
    /// </summary>
    private async Task PublishAsync(CancellationToken cancellation)
    {
        var settings = CompanionSettings.Current;
        var current = Split(await _plex.PreferenceAsync("customConnections", cancellation).ConfigureAwait(false) ?? string.Empty);
        var wanted = current.Where(c => !Same(c, settings.PublishedUrl) || (settings.PublishToPlex && Same(c, settings.PublicUrl))).ToList();
        if (settings.PublishToPlex && settings.PublicUrl.Length > 0 && !wanted.Any(c => Same(c, settings.PublicUrl)))
        {
            wanted.Add(settings.PublicUrl);
        }

        if (!wanted.SequenceEqual(current))
        {
            await _plex.SetPreferenceAsync("customConnections", string.Join(",", wanted), cancellation).ConfigureAwait(false);
            _log.LogInformation("Aster for Plex: Custom server access URLs now {Urls}", string.Join(", ", wanted));
        }

        var published = settings.PublishToPlex ? settings.PublicUrl : string.Empty;
        if (published != settings.PublishedUrl)
        {
            CompanionSettings.Update(s => s.PublishedUrl = published);
        }
    }

    private static List<string> Split(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool Same(string a, string b) =>
        b.Length > 0 && string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whoever's signed in to the page is the server's owner.</summary>
    private async Task<bool> IsOwnerAsync(CancellationToken cancellation) =>
        PlexServer.Configured && _signIns.TokenOf(Request) is string token
        && await _people.ForTokenAsync(token, cancellation).ConfigureAwait(false) is { Owner: true };

    /// <summary>The token of someone who may set the container up: anyone signed in before it's set up, then only the owner.</summary>
    private async Task<string?> SetterAsync(CancellationToken cancellation)
    {
        if (_signIns.TokenOf(Request) is not string token)
        {
            return null;
        }

        return !PlexServer.Configured || await IsOwnerAsync(cancellation).ConfigureAwait(false) ? token : null;
    }

    /// <summary>The person's own servers on plex.tv, each with the addresses it can be reached at, the home network's first.</summary>
    private async Task<List<ServerDto>> OwnedServersAsync(string token, CancellationToken cancellation)
    {
        var resources = await _plex.PlexTvAsync("api/v2/resources?includeHttps=1&includeRelay=0", token, cancellation).ConfigureAwait(false) as JsonArray;
        var servers = new List<ServerDto>();
        foreach (var device in resources?.OfType<JsonObject>() ?? [])
        {
            if (device.Text("provides")?.Contains("server", StringComparison.Ordinal) != true || device["owned"]?.GetValue<bool>() != true)
            {
                continue;
            }

            var addresses = new List<AddressDto>();
            foreach (var connection in device.List("connections").OrderBy(c => c["local"]?.GetValue<bool>() == true ? 0 : 1))
            {
                var local = connection["local"]?.GetValue<bool>() == true;
                if (local && connection.Text("address") is string ip && connection.Long("port") is long port)
                {
                    addresses.Add(new AddressDto { Url = $"http://{ip}:{port}", Local = true });
                }

                if (connection.Text("uri") is string uri)
                {
                    addresses.Add(new AddressDto { Url = uri, Local = local });
                }
            }

            servers.Add(new ServerDto
            {
                Name = device.Text("name") ?? "Plex",
                MachineId = device.Text("clientIdentifier") ?? string.Empty,
                Addresses = addresses.DistinctBy(a => a.Url).ToList(),
            });
        }

        return servers;
    }
}

public class MeDto
{
    public bool SignedIn { get; set; }

    public string? Name { get; set; }

    /// <summary>The signed-in person is one of the server's people.</summary>
    public bool OnServer { get; set; }

    public bool Owner { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>Whether the container has a Plex server to watch.</summary>
    public bool Configured { get; set; }

    public string? ServerName { get; set; }

    public bool? Reachable { get; set; }

    public string Version { get; set; } = string.Empty;
}

public class SignInStartDto
{
    /// <summary>Where app.plex.tv sends the browser back to once signed in.</summary>
    public string? ReturnUrl { get; set; }
}

public class SignInDto
{
    public long Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

public class ServerDto
{
    public string Name { get; set; } = string.Empty;

    public string MachineId { get; set; } = string.Empty;

    public List<AddressDto> Addresses { get; set; } = new();
}

public class AddressDto
{
    public string Url { get; set; } = string.Empty;

    public bool Local { get; set; }
}

public class SetupDto
{
    public string MachineId { get; set; } = string.Empty;

    public string? PlexUrl { get; set; }
}

public class SettingsDto
{
    public string? PlexUrl { get; set; }

    public string? PublicUrl { get; set; }

    public bool PublishToPlex { get; set; } = true;

    public List<string>? AudiobookLibraries { get; set; }

    public string? SimklClientId { get; set; }

    public string? AniListClientId { get; set; }

    public string? MyAnimeListClientId { get; set; }

    public string? HardcoverClientId { get; set; }

    public string? LastFmApiKey { get; set; }

    /// <summary>A new shared secret; never sent back (see <see cref="LastFmSharedSecretSet"/>).</summary>
    public string? LastFmSharedSecret { get; set; }

    public bool LastFmSharedSecretSet { get; set; }

    /// <summary>Plex's Custom server access URLs as they are now.</summary>
    public List<string> CustomConnections { get; set; } = new();

    public List<LibraryDto> MusicLibraries { get; set; } = new();

    public List<PersonDto> People { get; set; } = new();

    public List<ServiceDto> Services { get; set; } = new();

    public string? Problem { get; set; }
}

public class LibraryDto
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Counted as audiobooks now.</summary>
    public bool Audiobooks { get; set; }
}

public class PersonDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Owner { get; set; }
}

public class ServiceDto
{
    public string Service { get; set; } = string.Empty;

    public bool Available { get; set; }
}
