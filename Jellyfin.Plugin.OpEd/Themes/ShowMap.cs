using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpEd.Themes;

/// <summary>
/// Which Jellyfin show (and which of its seasons) a folder name in the themes folder refers to.
///
/// Theme folders are named after the show's folder in the media library ("Frieren - Beyond Journey's End (2023)").
/// For ordinary libraries that is just the episode's folder. Shokofin libraries are virtual: their episodes are
/// links into the real library ("/data/tvshows/general/anime/Bleach (2004)/Season 17/…"), so the real folder name
/// is read from the link target instead.
/// </summary>
public sealed partial class ShowMap
{
    private readonly Dictionary<string, Guid> _seriesByFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Folder, int Season), List<SeasonRef>> _seasonsByFolderSeason = [];
    private readonly Dictionary<Guid, List<SeasonRef>> _seasonsBySeries = [];

    /// <summary>
    /// A Jellyfin season and the AniDB anime it represents (Shokofin puts the AniDB id on each season). A season that has only
    /// an AniList/MyAnimeList/Kitsu id carries it in <paramref name="External"/> until <see cref="Resolve"/> finds its AniDB id.
    /// </summary>
    public sealed record SeasonRef(Guid SeasonId, int? AniDbId, int Order, AnimeThemesClient.ExternalId? External = null, DateTime? Start = null, bool ByDate = false);

    private readonly Dictionary<Guid, (int? AniDb, AnimeThemesClient.ExternalId? External)> _seriesIds = [];
    private readonly Dictionary<Guid, int> _seriesAniDb = [];
    private readonly Dictionary<Guid, (string Name, string? Original, int? Year)> _seriesInfo = [];
    private readonly HashSet<Guid> _titleMatched = [];

    public static ShowMap Build(ILibraryManager libraryManager, ILogger logger)
    {
        var map = new ShowMap();
        var episodes = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
        });

        var folderVotes = new Dictionary<string, Dictionary<Guid, int>>(StringComparer.OrdinalIgnoreCase);
        var seasonsSeen = new HashSet<Guid>();
        var seasonStarts = new Dictionary<Guid, DateTime>();
        foreach (var item in episodes)
        {
            if (item is not Episode episode || string.IsNullOrEmpty(episode.Path))
            {
                continue;
            }

            var seriesId = episode.SeriesId;
            var season = episode.Season;
            if (seriesId == Guid.Empty)
            {
                continue;
            }

            var real = RealPath(episode.Path);
            var (folder, folderSeason) = ShowFolderOf(real);
            if (folder is not null)
            {
                if (!folderVotes.TryGetValue(folder, out var votes))
                {
                    folderVotes[folder] = votes = [];
                }

                votes[seriesId] = votes.GetValueOrDefault(seriesId) + 1;
            }

            if (season is null)
            {
                continue;
            }

            if (episode.PremiereDate is DateTime aired && (!seasonStarts.TryGetValue(season.Id, out var earliest) || aired < earliest))
            {
                seasonStarts[season.Id] = aired;
            }

            var seasonRef = new SeasonRef(season.Id, AniDbOf(season), season.IndexNumber ?? 0, AniDbOf(season) is null ? ExternalOf(season) : null);
            if (episode.Series is { } series && !map._seriesIds.ContainsKey(seriesId))
            {
                map._seriesIds[seriesId] = (AniDbOf(series), AniDbOf(series) is null ? ExternalOf(series) : null);
                map._seriesInfo[seriesId] = (series.Name, series.OriginalTitle, series.ProductionYear);
            }

            if (seasonsSeen.Add(season.Id))
            {
                if (!map._seasonsBySeries.TryGetValue(seriesId, out var list))
                {
                    map._seasonsBySeries[seriesId] = list = [];
                }

                list.Add(seasonRef);
            }

            if (folder is not null && folderSeason is int fs)
            {
                if (!map._seasonsByFolderSeason.TryGetValue((folder, fs), out var list))
                {
                    map._seasonsByFolderSeason[(folder, fs)] = list = [];
                }

                if (!list.Exists(s => s.SeasonId == seasonRef.SeasonId))
                {
                    list.Add(seasonRef);
                }
            }
        }

        foreach (var (folder, votes) in folderVotes)
        {
            map._seriesByFolder[folder] = votes.MaxBy(v => v.Value).Key;
            if (votes.Count > 1)
            {
                logger.LogWarning("OP/ED: folder name {Folder} belongs to {Count} shows; using the one with most episodes", folder, votes.Count);
            }
        }

        foreach (var list in map._seasonsBySeries.Values.Concat(map._seasonsByFolderSeason.Values))
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (seasonStarts.TryGetValue(list[i].SeasonId, out var start))
                {
                    list[i] = list[i] with { Start = start };
                }
            }
        }

        foreach (var list in map._seasonsBySeries.Values.Concat(map._seasonsByFolderSeason.Values))
        {
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        return map;
    }

    public int ShowCount => _seriesByFolder.Count;

    public Guid? SeriesFor(string folder) => _seriesByFolder.TryGetValue(folder, out var id) ? id : null;

    /// <summary>
    /// Seasons a theme could belong to, most likely first: the folder's season if given, else the whole show. When no season
    /// has an AniDB id but the show as a whole does (its own, or found through its AniList/MyAnimeList/Kitsu id), that one
    /// entry stands in for the show; it belongs to no particular season, so themes using it stay show-wide.
    /// </summary>
    public IReadOnlyList<SeasonRef> CandidateSeasons(string folder, Guid seriesId, int? folderSeason)
    {
        if (folderSeason is int fs && _seasonsByFolderSeason.TryGetValue((folder, fs), out var inSeason))
        {
            return inSeason;
        }

        var all = _seasonsBySeries.TryGetValue(seriesId, out var seasons) ? seasons : [];
        if (folderSeason is null && !all.Exists(s => s.AniDbId is not null) && _seriesAniDb.TryGetValue(seriesId, out var showId))
        {
            return [new SeasonRef(Guid.Empty, showId, 0)];
        }

        return all;
    }

    public SeasonRef? SeasonForAniDb(Guid seriesId, int aniDbId) =>
        _seasonsBySeries.TryGetValue(seriesId, out var all) ? all.Find(s => s.AniDbId == aniDbId) : null;

    /// <summary>
    /// Shows that have themes (<paramref name="folderOf"/>: series id to its themes folder name) but no AniDB id at all, neither
    /// their own nor on a season nor found through another site's id: the ones to search for by title.
    /// </summary>
    public List<AnimeThemesClient.TitleQuery> TitleQueries(IReadOnlyDictionary<Guid, string> folderOf)
    {
        var result = new List<AnimeThemesClient.TitleQuery>();
        foreach (var (seriesId, folder) in folderOf)
        {
            if (_seriesAniDb.ContainsKey(seriesId) || !_seriesInfo.TryGetValue(seriesId, out var info)
                || (_seasonsBySeries.TryGetValue(seriesId, out var seasons) && seasons.Exists(s => s.AniDbId is not null)))
            {
                continue;
            }

            var alternates = new List<string> { folder };
            if (!string.IsNullOrWhiteSpace(info.Original))
            {
                alternates.Add(info.Original);
            }

            result.Add(new AnimeThemesClient.TitleQuery(seriesId, info.Name, alternates, info.Year ?? YearInName(folder)));
        }

        return result;
    }

    /// <summary>Records the AniDB entry found for a show by its title: it stands in for the whole show.</summary>
    public void SetTitleMatch(Guid seriesId, int aniDbId)
    {
        _seriesAniDb[seriesId] = aniDbId;
        _titleMatched.Add(seriesId);
    }

    public bool MatchedByTitle(Guid seriesId) => _titleMatched.Contains(seriesId);

    /// <summary>"Frieren (2023)" gives 2023.</summary>
    internal static int? YearInName(string name)
    {
        var m = YearSuffix().Match(name);
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"\((\d{4})\)\s*$")]
    private static partial Regex YearSuffix();

    private static bool Open(SeasonRef s) => s.AniDbId is null && s.Order > 0 && s.Start is not null;

    /// <summary>AniDB ids of shows that have themes and seasons still without an AniDB id: the shows whose franchise is worth fetching.</summary>
    public IEnumerable<int> SeasonMatchSeeds(IReadOnlySet<Guid> themedSeries) =>
        _seasonsBySeries.Where(kv => themedSeries.Contains(kv.Key) && _seriesAniDb.ContainsKey(kv.Key) && kv.Value.Exists(Open))
            .Select(kv => _seriesAniDb[kv.Key]).Distinct();

    /// <summary>
    /// Gives seasons that have no AniDB id the entry of the show's franchise (TV only) whose season starts closest to the
    /// season's first episode, if within three months. Each entry goes to one season at most, and nothing is guessed when the
    /// dates don't line up: that season then stays without an entry.
    /// </summary>
    public int MatchSeasonsByDate(IReadOnlySet<Guid> themedSeries, Func<int, IReadOnlyList<AnimeThemesClient.FranchiseEntry>?> franchise, ILogger logger)
    {
        var matched = 0;
        foreach (var (seriesId, seasons) in _seasonsBySeries)
        {
            if (!themedSeries.Contains(seriesId) || !_seriesAniDb.TryGetValue(seriesId, out var showId))
            {
                continue;
            }

            var open = seasons.Where(Open).ToList();
            if (open.Count == 0 || franchise(showId) is not { Count: > 0 } entries)
            {
                continue;
            }

            var taken = seasons.Where(s => s.AniDbId is not null).Select(s => s.AniDbId!.Value).ToHashSet();
            var pairs = (from s in open
                         from e in entries
                         where !taken.Contains(e.AniDb) && e.Month is not null && (e.Format is null or "TV" or "TV_SHORT")
                         let diff = Math.Abs((s.Start!.Value.Year * 12) + s.Start.Value.Month - ((e.Year * 12) + e.Month!.Value))
                         where diff <= 3
                         orderby diff, s.Order
                         select (Season: s, Entry: e)).ToList();
            var usedSeasons = new HashSet<Guid>();
            foreach (var (season, entry) in pairs)
            {
                if (!usedSeasons.Add(season.SeasonId) || !taken.Add(entry.AniDb))
                {
                    continue;
                }

                foreach (var list in _seasonsBySeries.Values.Concat(_seasonsByFolderSeason.Values))
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (list[i].SeasonId == season.SeasonId)
                        {
                            list[i] = list[i] with { AniDbId = entry.AniDb, ByDate = true };
                        }
                    }
                }

                matched++;
                logger.LogInformation("OP/ED: season {Order} of show {Series} matched to AniDB {AniDb} by premiere date", season.Order, _seriesInfo.TryGetValue(seriesId, out var info) ? info.Name : seriesId, entry.AniDb);
            }
        }

        return matched;
    }

    /// <summary>Seasons and shows known only by an AniList/MyAnimeList/Kitsu id: the ids to look up.</summary>
    public IEnumerable<AnimeThemesClient.ExternalId> UnresolvedExternals() =>
        _seasonsBySeries.Values.SelectMany(l => l).Select(s => s.External)
            .Concat(_seriesIds.Values.Select(v => v.External))
            .Where(e => e is not null).Select(e => e!.Value).Distinct();

    /// <summary>
    /// Fills in the AniDB id of seasons that only had another site's id, and records the AniDB id of each show as a whole
    /// (its own AniDB id, or the one found for its AniList/MyAnimeList/Kitsu id).
    /// </summary>
    public void Resolve(Func<AnimeThemesClient.ExternalId, int?> lookup)
    {
        foreach (var lists in _seasonsBySeries.Values.Concat(_seasonsByFolderSeason.Values))
        {
            for (var i = 0; i < lists.Count; i++)
            {
                if (lists[i].AniDbId is null && lists[i].External is { } ext && lookup(ext) is int found)
                {
                    lists[i] = lists[i] with { AniDbId = found };
                }
            }
        }

        foreach (var (seriesId, (aniDb, ext)) in _seriesIds)
        {
            if ((aniDb ?? (ext is { } e ? lookup(e) : null)) is int id)
            {
                _seriesAniDb[seriesId] = id;
            }
        }
    }

    // Jellyfin's AniList, MyAnimeList and Kitsu providers store their ids under these names. AniList first: it is the
    // one anime libraries most often have, and AnimeThemes lists all three.
    private static readonly (string Provider, string Site)[] ExternalSites = [("AniList", "ANILIST"), ("MyAnimeList", "MAL"), ("Kitsu", "KITSU")];

    internal static AnimeThemesClient.ExternalId? ExternalOf(BaseItem item)
    {
        foreach (var (provider, site) in ExternalSites)
        {
            var key = item.ProviderIds.Keys.FirstOrDefault(k => string.Equals(k, provider, StringComparison.OrdinalIgnoreCase));
            if (key is not null && int.TryParse(item.ProviderIds[key], out var id) && id > 0)
            {
                return new AnimeThemesClient.ExternalId(site, id);
            }
        }

        return null;
    }

    internal static int? AniDbOf(BaseItem item) =>
        item.ProviderIds.TryGetValue("AniDB", out var v) && int.TryParse(v, out var id) ? id : null;

    internal static string RealPath(string path)
    {
        try
        {
            var target = new FileInfo(path).LinkTarget;
            if (!string.IsNullOrEmpty(target))
            {
                return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target));
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return path;
    }

    /// <summary>"…/Show/Season 2/ep.mkv" gives ("Show", 2); "…/Show/ep.mkv" gives ("Show", null).</summary>
    internal static (string? Folder, int? Season) ShowFolderOf(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            return (null, null);
        }

        var name = Path.GetFileName(dir);
        var m = SeasonFolder().Match(name);
        if (m.Success)
        {
            var season = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            return (Path.GetFileName(Path.GetDirectoryName(dir)), season);
        }

        return (name, null);
    }

    /// <summary>"Season 2", "Season 02", "S2", "Specials" (season 0).</summary>
    [GeneratedRegex(@"^(?:(?:Season|Series|S)\s*0*(\d+)|Specials)$", RegexOptions.IgnoreCase)]
    internal static partial Regex SeasonFolder();
}
