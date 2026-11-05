namespace Jellyfin.Plugin.AsterTracking.Discovery;

/// <summary>A list a person can browse, given what they've linked.</summary>
public class DiscoverListDto
{
    /// <summary>"simkl", "anilist", "myanimelist".</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>The list's name in routes: "watchlist", "watching", "trending", "season", "recommended".</summary>
    public string List { get; set; } = string.Empty;

    /// <summary>"Watchlist", "Watching", "Trending", "This Season", "Recommended": what kind of list, for grouping.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The row's title, "Simkl Watchlist".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Personal lists change as the person uses the service; the others are everyone's.</summary>
    public bool Personal { get; set; }
}

/// <summary>A page of a list.</summary>
public class DiscoverPageDto
{
    public List<DiscoverItemDto> Items { get; set; } = new();

    public int Page { get; set; }

    public int TotalItems { get; set; }

    public bool HasMore { get; set; }
}

/// <summary>A film or show on a list, the same shape from every service.</summary>
public class DiscoverItemDto
{
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    /// <summary>"movie" or "tv", as Seerr and TMDB divide them.</summary>
    public string MediaType { get; set; } = "tv";

    /// <summary>
    /// What's known of it: "tmdb", "tvdb", "imdb", and for anime "anilist", "mal", "anidb", "tmdbSeason" (the TMDB
    /// season an anime entry is). With "tmdb" the apps open Seerr's page for it.
    /// </summary>
    public Dictionary<string, string> Ids { get; set; } = new();

    public string? Poster { get; set; }

    public string? Overview { get; set; }

    /// <summary>For Watching lists: "Episode 3 of 12".</summary>
    public string? Progress { get; set; }

    /// <summary>Anime, as the lists' services keep it apart.</summary>
    public bool Anime { get; set; }

    /// <summary>Its genres, from whichever source knew them (and the library's, when it's here).</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>Its tags and keywords: AniList's tags, TMDB's keywords, the library's tags.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Marked adult by its source.</summary>
    public bool Adult { get; set; }

    /// <summary>The title on this server for this person, to play instead of request; null when it isn't here.</summary>
    public LibraryMatchDto? InLibrary { get; set; }
}

public class LibraryMatchDto
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>"Movie" or "Series".</summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>The whole Discover page for one person, built on the server.</summary>
public class DiscoverHomeDto
{
    /// <summary>The rows in the order to show them; empty rows are left out.</summary>
    public List<DiscoverRowDto> Rows { get; set; } = new();

    public DateTimeOffset Built { get; set; }
}

/// <summary>One row of the Discover page.</summary>
public class DiscoverRowDto
{
    /// <summary>Stable for a row's kind and source: "recommended", "because-&lt;itemId&gt;", "library", …</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>"continue", "recommended", "because", "library", "watchlist", "trending", "season".</summary>
    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Why it's there, when that helps: "From what you watch and your linked services".</summary>
    public string? Subtitle { get; set; }

    public List<DiscoverItemDto> Items { get; set; } = new();
}
