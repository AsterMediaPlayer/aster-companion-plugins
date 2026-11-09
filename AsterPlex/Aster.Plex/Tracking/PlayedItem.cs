namespace Aster.Plex.Tracking;

/// <summary>What kind of thing was played, as the services tell them apart.</summary>
public enum PlayedKind
{
    Movie,
    Episode,
    Song,
    Audiobook,
    Other,
}

/// <summary>
/// Something played on the media server, described the way the tracking services need it. It stands in for the
/// Jellyfin plugin's <c>BaseItem</c>: the service clients copied from the plugin read the same fields from it (the
/// provider ids under Jellyfin's names, "Tmdb", "Imdb", "Tvdb", "MusicBrainzRecording" and so on), and
/// <see cref="Plex.PlexItems"/> fills it from Plex's metadata.
/// </summary>
public sealed class PlayedItem
{
    /// <summary>The server's id for it, as a Guid (Plex's rating key, see <see cref="Plex.PlexItems.IdFor"/>).</summary>
    public Guid Id { get; init; }

    public PlayedKind Kind { get; init; }

    /// <summary>The film's, episode's, song's or book's own name.</summary>
    public string Name { get; init; } = string.Empty;

    public int? ProductionYear { get; init; }

    public long? RunTimeTicks { get; init; }

    /// <summary>Its file, for an audiobook's author when the tags have none.</summary>
    public string? Path { get; init; }

    /// <summary>The film's ids, or the episode's or song's own, under Jellyfin's names.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>An episode's show.</summary>
    public string? SeriesName { get; init; }

    /// <summary>The show's ids ("Tmdb", "Imdb", "Tvdb"), for an episode.</summary>
    public IReadOnlyDictionary<string, string> SeriesProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// An anime season's ids on the anime sites, when the server knows them: "AniList", "MyAnimeList", and "AniDB"
    /// when it's one AniDB entry (as the HAMA agent and Shoko give them). Only ids that are the season's own belong
    /// here: an AniDB id guessed for a whole cartoon would put its episodes on the wrong entries.
    /// </summary>
    public IReadOnlyDictionary<string, string> SeasonProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>An episode's season (0 for specials); null for anything else.</summary>
    public int? ParentIndexNumber { get; init; }

    /// <summary>An episode's number, or a song's track number.</summary>
    public int? IndexNumber { get; init; }

    /// <summary>The last episode in a file holding several.</summary>
    public int? IndexNumberEnd { get; init; }

    /// <summary>A song's or book's artists (a book's authors or narrators).</summary>
    public IReadOnlyList<string> Artists { get; init; } = [];

    public IReadOnlyList<string> AlbumArtists { get; init; } = [];

    /// <summary>A song's album, or the book an audiobook's part belongs to.</summary>
    public string? Album { get; init; }

    public string? GetProviderId(string name) => ProviderIds.TryGetValue(name, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    public string? GetSeriesProviderId(string name) => SeriesProviderIds.TryGetValue(name, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    public string? GetSeasonProviderId(string name) => SeasonProviderIds.TryGetValue(name, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;
}
