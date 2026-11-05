using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AsterTracking.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AsterTracking.Discovery;

/// <summary>
/// The films and shows on the server that one person may see, by their ids (TMDB, TVDB, IMDb, and Shokofin's AniDB
/// seasons), held in memory for half an hour. Matching a list's titles to the library is then a lookup, not a database
/// query per title: a list of 30 went from about 3 seconds to milliseconds.
/// </summary>
public sealed class LibraryIndex
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);

    private readonly ILibraryManager _library;
    private readonly ConcurrentDictionary<Guid, (Index Index, DateTimeOffset At)> _indexes = new();

    public LibraryIndex(ILibraryManager library)
    {
        _library = library;
    }

    public sealed class Index
    {
        /// <summary>"tmdb:movie:603", "tvdb:tv:…", "imdb:…", "anidb:…" to the film or show.</summary>
        public Dictionary<string, BaseItem> ById { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<BaseItem> Catalog { get; } = new();

        /// <summary>The item's film or show here, or null.</summary>
        public BaseItem? Find(DiscoverItemDto item)
        {
            var type = item.MediaType == "movie" ? "movie" : "tv";
            if (item.Anime && item.Ids.TryGetValue("anidb", out var aniDb) && ById.TryGetValue($"anidb:{aniDb}", out var anime))
            {
                return anime;
            }

            foreach (var (key, name) in new[] { ("tmdb", "tmdb:" + type), ("tvdb", "tvdb:" + type), ("imdb", "imdb") })
            {
                if (item.Ids.TryGetValue(key, out var id) && ById.TryGetValue($"{name}:{id}", out var found))
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>Sets the item's <c>InLibrary</c> when it's here.</summary>
        public void Match(DiscoverItemDto item)
        {
            if (item.InLibrary is null && Find(item) is { } found)
            {
                item.InLibrary = new LibraryMatchDto { ItemId = found.Id, Name = found.Name, Type = found is Movie ? "Movie" : "Series" };
            }
        }
    }

    public Index For(User user)
    {
        if (_indexes.TryGetValue(user.Id, out var known) && DateTimeOffset.UtcNow - known.At < Keep)
        {
            return known.Index;
        }

        var index = Build(user);
        _indexes[user.Id] = (index, DateTimeOffset.UtcNow);
        return index;
    }

    private Index Build(User user)
    {
        var index = new Index();
        foreach (var item in _library.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series], Recursive = true }))
        {
            index.Catalog.Add(item);
            var type = item is Movie ? "movie" : "tv";
            foreach (var (provider, name) in new[] { ("Tmdb", "tmdb:" + type), ("Tvdb", "tvdb:" + type), ("Imdb", "imdb") })
            {
                if (item.GetProviderId(provider) is { Length: > 0 } id)
                {
                    index.ById.TryAdd($"{name}:{id}", item);
                }
            }

            // Shokofin's shows: their AniDB entry; other plugins' AniDB guesses don't count.
            if (!string.IsNullOrEmpty(item.GetProviderId("Shoko Series")) && item.GetProviderId("AniDB") is { Length: > 0 } aniDb)
            {
                index.ById.TryAdd($"anidb:{aniDb}", item);
            }
        }

        // Each Shokofin season is its own AniDB entry: the exact anime a list means.
        foreach (var season in _library.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Season], Recursive = true }).OfType<Season>())
        {
            if (AnimeIds.ShokoAniDb(season) is { } aniDb && season.Series is { } series)
            {
                index.ById.TryAdd($"anidb:{aniDb}", series);
            }
        }

        return index;
    }
}
