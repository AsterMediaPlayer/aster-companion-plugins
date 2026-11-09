using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aster.Plex.Tracking;
using Microsoft.Extensions.Logging;

namespace Aster.Plex.Plex;

/// <summary>
/// Plex's films, episodes and tracks as <see cref="PlayedItem"/>s, for the services: their TMDB, IMDb and TVDB ids
/// from Plex's <c>Guid</c> list (or, for the older agents, from the item's own guid), an episode's show's ids, and for
/// anime the AniDB id the HAMA agent gives a show that's one AniDB entry. Music from the libraries set as audiobooks
/// counts as audiobooks. Each item is remembered for ten minutes.
/// </summary>
public sealed partial class PlexItems : IItemLookup
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    private readonly PlexServer _plex;
    private readonly ILogger<PlexItems> _log;
    private readonly ConcurrentDictionary<long, (PlayedItem? Item, DateTimeOffset At)> _items = new();
    private readonly ConcurrentDictionary<long, (JsonNode? Show, DateTimeOffset At)> _shows = new();

    public PlexItems(PlexServer plex, ILogger<PlexItems> log)
    {
        _plex = plex;
        _log = log;
    }

    /// <summary>The Guid for a Plex rating key: the key itself in the first eight bytes, so it can be read back.</summary>
    public static Guid IdFor(long ratingKey)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(ratingKey).CopyTo(bytes, 0);
        "plex-key"u8.ToArray().CopyTo(bytes, 8);
        return new Guid(bytes);
    }

    /// <summary>The rating key in a Guid from <see cref="IdFor"/>, or null for any other Guid.</summary>
    public static long? KeyOf(Guid id)
    {
        var bytes = id.ToByteArray();
        return bytes.AsSpan(8).SequenceEqual("plex-key"u8) ? BitConverter.ToInt64(bytes, 0) : null;
    }

    public Task<PlayedItem?> GetItemAsync(Guid id, CancellationToken cancellation) =>
        KeyOf(id) is long key ? ItemAsync(key, cancellation) : Task.FromResult<PlayedItem?>(null);

    /// <summary>The item with this rating key, or null when Plex doesn't have it (or it isn't a film, episode or track).</summary>
    public async Task<PlayedItem?> ItemAsync(long ratingKey, CancellationToken cancellation)
    {
        if (_items.TryGetValue(ratingKey, out var known) && DateTimeOffset.UtcNow - known.At < Keep)
        {
            return known.Item;
        }

        var reply = await _plex.GetAsync($"/library/metadata/{ratingKey}?includeGuids=1", cancellation).ConfigureAwait(false);
        var metadata = reply.List("Metadata").FirstOrDefault();
        var item = metadata is null ? null : await FromAsync(metadata, cancellation).ConfigureAwait(false);
        _items[ratingKey] = (item, DateTimeOffset.UtcNow);
        return item;
    }

    private async Task<PlayedItem?> FromAsync(JsonNode m, CancellationToken cancellation)
    {
        if (m.Long("ratingKey") is not long key)
        {
            return null;
        }

        var runtime = m.Long("duration") is long ms && ms > 0 ? TimeSpan.FromMilliseconds(ms).Ticks : (long?)null;
        var path = m.List("Media").SelectMany(media => media.List("Part")).Select(part => part.Text("file")).FirstOrDefault(f => f is not null);
        switch (m.Text("type"))
        {
            case "movie":
                return new PlayedItem
                {
                    Id = IdFor(key),
                    Kind = PlayedKind.Movie,
                    Name = m.Text("title") ?? string.Empty,
                    ProductionYear = m.Int("year"),
                    RunTimeTicks = runtime,
                    Path = path,
                    ProviderIds = Ids(m),
                };

            case "episode":
            {
                var show = m.Long("grandparentRatingKey") is long showKey ? await ShowAsync(showKey, cancellation).ConfigureAwait(false) : null;
                var season = m.Int("parentIndex");
                var seasonIds = new Dictionary<string, string>();

                // HAMA lists a show that's one AniDB entry as anidb-<id>, its episodes in season 1 numbered as AniDB
                // does (specials in season 0, which share the id and are left out). A show HAMA matched through TVDB
                // spans several AniDB entries, so it has none.
                if (season == 1 && HamaAniDb(show?.Text("guid") ?? m.Text("grandparentGuid")) is string aniDb)
                {
                    seasonIds["AniDB"] = aniDb;
                }

                return new PlayedItem
                {
                    Id = IdFor(key),
                    Kind = PlayedKind.Episode,
                    Name = m.Text("title") ?? string.Empty,
                    ProductionYear = m.Int("year"),
                    RunTimeTicks = runtime,
                    Path = path,
                    ProviderIds = Ids(m),
                    SeriesName = m.Text("grandparentTitle"),
                    SeriesProviderIds = show is null ? new Dictionary<string, string>() : Ids(show),
                    SeasonProviderIds = seasonIds,
                    ParentIndexNumber = season,
                    IndexNumber = m.Int("index"),
                };
            }

            case "track":
            {
                // A track's own artist is its originalTitle when it differs from the album's. Plex fills gaps with
                // "[Unknown Album]" and "[Unknown Artist]", which aren't names to send.
                var albumArtist = Known(m.Text("grandparentTitle"));
                var artist = Known(m.Text("originalTitle")) ?? albumArtist;
                var book = IsAudiobookLibrary(m.Text("librarySectionTitle"), m.Text("librarySectionID"));
                return new PlayedItem
                {
                    Id = IdFor(key),
                    Kind = book ? PlayedKind.Audiobook : PlayedKind.Song,
                    Name = m.Text("title") ?? string.Empty,
                    ProductionYear = m.Int("parentYear") ?? m.Int("year"),
                    RunTimeTicks = runtime,
                    Path = path,
                    Artists = artist is null ? [] : [artist],
                    AlbumArtists = albumArtist is null ? [] : [albumArtist],
                    Album = Known(m.Text("parentTitle")),
                    IndexNumber = m.Int("index"),
                };
            }

            default:
                return null;
        }
    }

    /// <summary>A name, or null for Plex's placeholders ("[Unknown Album]", "[Unknown Artist]").</summary>
    private static string? Known(string? name) =>
        name is null || (name.StartsWith("[Unknown ", StringComparison.OrdinalIgnoreCase) && name.EndsWith(']')) ? null : name;

    /// <summary>Whether a library holds audiobooks: one named in the settings, or with none named, any with "book" in its name.</summary>
    public static bool IsAudiobookLibrary(string? title, string? sectionId)
    {
        var chosen = CompanionSettings.Current.AudiobookLibraries.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
        if (chosen.Count > 0)
        {
            return chosen.Any(l => string.Equals(l, title, StringComparison.OrdinalIgnoreCase) || l == sectionId);
        }

        return title?.Contains("book", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<JsonNode?> ShowAsync(long key, CancellationToken cancellation)
    {
        if (_shows.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < Keep)
        {
            return known.Show;
        }

        try
        {
            var reply = await _plex.GetAsync($"/library/metadata/{key}?includeGuids=1", cancellation).ConfigureAwait(false);
            var show = reply.List("Metadata").FirstOrDefault();
            _shows[key] = (show, DateTimeOffset.UtcNow);
            return show;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexException)
        {
            _log.LogWarning("Aster for Plex: show {Key} couldn't be read: {Error}", key, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The item's ids under Jellyfin's provider names, from Plex's <c>Guid</c> list (<c>tmdb://603</c>,
    /// <c>imdb://tt0133093</c>, <c>tvdb://81189</c>), or from an older agent's own guid.
    /// </summary>
    internal static Dictionary<string, string> Ids(JsonNode item)
    {
        var ids = new Dictionary<string, string>();
        foreach (var guid in item.List("Guid").Select(g => g.Text("id")).OfType<string>().Append(item.Text("guid") ?? string.Empty))
        {
            var (provider, id) = Parse(guid);
            if (provider is not null && id is not null && !ids.ContainsKey(provider))
            {
                ids[provider] = id;
            }
        }

        return ids;
    }

    /// <summary>One guid as a provider and id: "tmdb://603" → ("Tmdb", "603"); legacy agents' guids too.</summary>
    internal static (string? Provider, string? Id) Parse(string guid)
    {
        var match = GuidPattern().Match(guid);
        if (!match.Success)
        {
            return (null, null);
        }

        var scheme = match.Groups["scheme"].Value.ToLowerInvariant();
        var id = match.Groups["id"].Value;
        return scheme switch
        {
            "tmdb" or "com.plexapp.agents.themoviedb" when id.All(char.IsAsciiDigit) => ("Tmdb", id),
            "imdb" or "com.plexapp.agents.imdb" when id.StartsWith("tt", StringComparison.Ordinal) => ("Imdb", id),
            "tvdb" or "com.plexapp.agents.thetvdb" when id.All(char.IsAsciiDigit) => ("Tvdb", id),
            "com.plexapp.agents.hama" when id.StartsWith("tvdb-", StringComparison.Ordinal) && id[5..].All(char.IsAsciiDigit) => ("Tvdb", id[5..]),
            _ => (null, null),
        };
    }

    /// <summary>The AniDB id of a show HAMA matched as one AniDB entry ("com.plexapp.agents.hama://anidb-5391?lang=en").</summary>
    internal static string? HamaAniDb(string? guid)
    {
        var match = GuidPattern().Match(guid ?? string.Empty);
        return match.Success && match.Groups["scheme"].Value == "com.plexapp.agents.hama"
            && match.Groups["id"].Value is { } id && id.StartsWith("anidb-", StringComparison.Ordinal)
            && int.TryParse(id[6..], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? id[6..]
            : null;
    }

    [GeneratedRegex(@"^(?<scheme>[a-z.]+)://(?<id>[^/?]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GuidPattern();
}
