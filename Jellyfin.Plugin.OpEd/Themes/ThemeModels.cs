namespace Jellyfin.Plugin.OpEd.Themes;

/// <summary>One opening or ending: the files for it (video and/or audio) plus whatever metadata was found.</summary>
public sealed class Theme
{
    /// <summary>Stable id derived from where the theme sits in the themes folder; survives rescans.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>"OP" or "ED".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>OP/ED number, 1 when the file name has none.</summary>
    public int Sequence { get; set; } = 1;

    /// <summary>Version (the "2" in OP1v2), 1 when absent.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Variant tag after the slug, e.g. "TV" in ED1-TV; empty when absent.</summary>
    public string Variant { get; set; } = string.Empty;

    /// <summary>AnimeThemes-style slug: OP1, ED2, ED1-TV.</summary>
    public string Slug => Type + Sequence + (Variant.Length > 0 ? "-" + Variant : string.Empty);

    /// <summary>Show folder name in the themes folder (matches the show's folder in the media library).</summary>
    public string ShowFolder { get; set; } = string.Empty;

    /// <summary>Season number from a "Season N" subfolder, if the theme sits in one.</summary>
    public int? FolderSeason { get; set; }

    /// <summary>AniDB id from an "anidb-N" subfolder, if the theme sits in one.</summary>
    public int? FolderAniDb { get; set; }

    /// <summary>This theme's key in its show's themes.json ("OP1", "Season 2/OP1", "anidb-18886/ED2").</summary>
    public string DetailsKey { get; set; } = string.Empty;

    /// <summary>AniDB anime the theme belongs to: from an "anidb-N" subfolder, or found by the metadata lookup.</summary>
    public int? AniDbId { get; set; }

    /// <summary>AniDB entry (season) picked by hand in themes.json ("anidb"); null when the season is worked out automatically.</summary>
    public int? AniDbOverride { get; set; }

    public Guid? SeriesId { get; set; }

    /// <summary>The Jellyfin season (for Shokofin libraries, one per AniDB entry) the theme belongs to, when known.</summary>
    public Guid? SeasonId { get; set; }

    public string? VideoPath { get; set; }

    public string? AudioPath { get; set; }

    /// <summary>MP4 (H.264/HEVC + AAC) every client can play: the original when it already qualifies, else a converted copy.</summary>
    public string? Mp4Path { get; set; }

    /// <summary>"pending", "converting", "ready" or "failed"; null when the theme has no video.</summary>
    public string? Mp4Status { get; set; }

    /// <summary>M4A (AAC, or MP3) every client can play: the original audio when it already qualifies, else a converted copy.</summary>
    public string? M4aPath { get; set; }

    /// <summary>"pending", "converting", "ready" or "failed"; null when the theme has no audio.</summary>
    public string? M4aStatus { get; set; }

    public string? SongTitle { get; set; }

    public List<string> Artists { get; set; } = [];

    /// <summary>Episodes the theme plays in, as AnimeThemes writes it ("1-16", "1-14, 16").</summary>
    public string? Episodes { get; set; }

    public bool Spoiler { get; set; }

    public bool Nsfw { get; set; }

    /// <summary>Most specific source of the song details: "themes.json", "filename" or "animethemes"; null when none.</summary>
    public string? MetadataSource { get; set; }
}

/// <summary>What the last scan found, for the settings page.</summary>
public sealed class ScanReport
{
    public DateTime? FinishedAt { get; set; }

    public int Themes { get; set; }

    public int WithMetadata { get; set; }

    public List<string> UnmatchedFolders { get; set; } = [];

    public List<string> UnparsedFiles { get; set; } = [];

    public List<string> NoMetadata { get; set; } = [];

    public string? Error { get; set; }
}

public sealed class ThemeIndexData
{
    public List<Theme> Themes { get; set; } = [];

    public ScanReport Report { get; set; } = new();
}
