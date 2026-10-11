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

    /// <summary>A Jellyfin season and the AniDB anime it represents (Shokofin puts the AniDB id on each season).</summary>
    public sealed record SeasonRef(Guid SeasonId, int? AniDbId, int Order);

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

            var seasonRef = new SeasonRef(season.Id, AniDbOf(season), season.IndexNumber ?? 0);
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
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        return map;
    }

    public int ShowCount => _seriesByFolder.Count;

    public Guid? SeriesFor(string folder) => _seriesByFolder.TryGetValue(folder, out var id) ? id : null;

    /// <summary>Seasons a theme could belong to, most likely first: the folder's season if given, else the whole show.</summary>
    public IReadOnlyList<SeasonRef> CandidateSeasons(string folder, Guid seriesId, int? folderSeason)
    {
        if (folderSeason is int fs && _seasonsByFolderSeason.TryGetValue((folder, fs), out var inSeason))
        {
            return inSeason;
        }

        return _seasonsBySeries.TryGetValue(seriesId, out var all) ? all : [];
    }

    public SeasonRef? SeasonForAniDb(Guid seriesId, int aniDbId) =>
        _seasonsBySeries.TryGetValue(seriesId, out var all) ? all.Find(s => s.AniDbId == aniDbId) : null;

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
