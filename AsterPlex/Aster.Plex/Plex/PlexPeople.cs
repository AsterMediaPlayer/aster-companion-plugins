using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Aster.Plex.Plex;

/// <summary>Someone with access to the Plex server.</summary>
/// <param name="Id">Their id here: a Guid made from their plex.tv account id, so it never changes.</param>
/// <param name="PlexId">Their plex.tv account id.</param>
/// <param name="Owner">The server's owner, who can change the container's settings.</param>
public sealed record PlexPerson(Guid Id, long PlexId, string Name, bool Owner);

/// <summary>
/// Who's who on the Plex server: the owner, the friends it's shared with, and the owner's Home. An app's token is
/// matched to its person through the server's shares (plex.tv gives the owner each friend's token for this server),
/// or else by asking plex.tv whose account the token is and checking they're one of the server's people. Plex's
/// sessions and history name people by account id, the owner as 1.
/// </summary>
public sealed class PlexPeople
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Unknown = TimeSpan.FromMinutes(1);

    private readonly PlexServer _plex;
    private readonly ILogger<PlexPeople> _log;
    private readonly SemaphoreSlim _loading = new(1, 1);
    private readonly ConcurrentDictionary<string, (PlexPerson? Person, DateTimeOffset At)> _tokens = new();
    private Roster _roster = new([], new Dictionary<string, long>(), DateTimeOffset.MinValue);

    public PlexPeople(PlexServer plex, ILogger<PlexPeople> log)
    {
        _plex = plex;
        _log = log;
    }

    /// <summary>The Guid for a plex.tv account id.</summary>
    public static Guid IdFor(long plexId) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("aster-plex-user:" + plexId)));

    /// <summary>The person a token belongs to, or null when it isn't someone with access to this server.</summary>
    public async Task<PlexPerson?> ForTokenAsync(string token, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(token) || !PlexServer.Configured)
        {
            return null;
        }

        token = token.Trim();
        if (_tokens.TryGetValue(token, out var known) && DateTimeOffset.UtcNow - known.At < (known.Person is null ? Unknown : Keep))
        {
            return known.Person;
        }

        var roster = await RosterAsync(fresh: false, cancellation).ConfigureAwait(false);
        PlexPerson? person = null;
        if (token == CompanionSettings.Current.PlexToken)
        {
            person = roster.People.FirstOrDefault(p => p.Owner);
        }
        else if (roster.ServerTokens.TryGetValue(token, out var shared))
        {
            person = roster.People.FirstOrDefault(p => p.PlexId == shared);
        }

        if (person is null)
        {
            // A plex.tv account token (or a Home profile's): whose is it, and are they one of the server's people?
            var account = await _plex.PlexTvAsync("api/v2/user", token, cancellation).ConfigureAwait(false);
            if (account.Long("id") is long id)
            {
                person = roster.People.FirstOrDefault(p => p.PlexId == id);
                if (person is null && DateTimeOffset.UtcNow - roster.At > TimeSpan.FromSeconds(30))
                {
                    // Someone added since the list was read.
                    roster = await RosterAsync(fresh: true, cancellation).ConfigureAwait(false);
                    person = roster.People.FirstOrDefault(p => p.PlexId == id);
                }
            }
        }

        _tokens[token] = (person, DateTimeOffset.UtcNow);
        return person;
    }

    /// <summary>The person Plex's sessions and history call <paramref name="accountId"/> (1 is the owner).</summary>
    public async Task<PlexPerson?> ForAccountAsync(long accountId, CancellationToken cancellation)
    {
        var roster = await RosterAsync(fresh: false, cancellation).ConfigureAwait(false);
        return accountId == 1
            ? roster.People.FirstOrDefault(p => p.Owner)
            : roster.People.FirstOrDefault(p => p.PlexId == accountId);
    }

    /// <summary>Whether <paramref name="id"/> is one of the server's people.</summary>
    public async Task<PlexPerson?> ForIdAsync(Guid id, CancellationToken cancellation) =>
        (await RosterAsync(fresh: false, cancellation).ConfigureAwait(false)).People.FirstOrDefault(p => p.Id == id);

    public async Task<IReadOnlyList<PlexPerson>> EveryoneAsync(CancellationToken cancellation) =>
        (await RosterAsync(fresh: false, cancellation).ConfigureAwait(false)).People;

    /// <summary>Forgets what's known, after the settings change.</summary>
    public void Reset()
    {
        _tokens.Clear();
        _roster = new([], new Dictionary<string, long>(), DateTimeOffset.MinValue);
    }

    private async Task<Roster> RosterAsync(bool fresh, CancellationToken cancellation)
    {
        if (!fresh && DateTimeOffset.UtcNow - _roster.At < Keep)
        {
            return _roster;
        }

        await _loading.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!fresh && DateTimeOffset.UtcNow - _roster.At < Keep)
            {
                return _roster;
            }

            var owner = CompanionSettings.Current.PlexToken;
            var people = new Dictionary<long, PlexPerson>();
            var tokens = new Dictionary<string, long>(StringComparer.Ordinal);

            var me = await _plex.PlexTvAsync("api/v2/user", owner, cancellation).ConfigureAwait(false);
            if (me.Long("id") is not long ownerId)
            {
                _log.LogWarning("Aster for Plex: plex.tv didn't take the owner's token; check it in the settings");
                return _roster;
            }

            people[ownerId] = new PlexPerson(IdFor(ownerId), ownerId, me.Text("title") ?? me.Text("username") ?? "Owner", true);

            // The owner's Home: profiles, managed or with their own accounts.
            var home = await _plex.PlexTvXmlAsync("api/home/users", owner, cancellation).ConfigureAwait(false);
            foreach (var user in home?.Elements("User") ?? [])
            {
                if (long.TryParse((string?)user.Attribute("id"), out var id) && !people.ContainsKey(id))
                {
                    people[id] = new PlexPerson(IdFor(id), id, (string?)user.Attribute("title") ?? (string?)user.Attribute("username") ?? $"Profile {id}", false);
                }
            }

            // Friends the server is shared with, with each one's token for this server.
            if (await _plex.MachineIdAsync(cancellation).ConfigureAwait(false) is string machine)
            {
                var shares = await _plex.PlexTvXmlAsync($"api/servers/{Uri.EscapeDataString(machine)}/shared_servers", owner, cancellation).ConfigureAwait(false);
                foreach (var share in shares?.Elements("SharedServer") ?? [])
                {
                    if (!long.TryParse((string?)share.Attribute("userID"), out var id))
                    {
                        continue;
                    }

                    if (!people.ContainsKey(id))
                    {
                        people[id] = new PlexPerson(IdFor(id), id, (string?)share.Attribute("username") ?? (string?)share.Attribute("email") ?? $"Friend {id}", false);
                    }

                    if ((string?)share.Attribute("accessToken") is { Length: > 0 } token)
                    {
                        tokens[token] = id;
                    }
                }
            }

            _roster = new Roster(people.Values.ToList(), tokens, DateTimeOffset.UtcNow);
            return _roster;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexException or System.Xml.XmlException or System.Text.Json.JsonException)
        {
            _log.LogWarning("Aster for Plex: couldn't read the server's people: {Error}", ex.Message);
            return _roster;
        }
        finally
        {
            _loading.Release();
        }
    }

    private sealed record Roster(IReadOnlyList<PlexPerson> People, IReadOnlyDictionary<string, long> ServerTokens, DateTimeOffset At);
}
