using Jellyfin.Plugin.DiscMenus.Model;

namespace Jellyfin.Plugin.DiscMenus.Api;

/// <summary>
/// A parent item's disc menu with every entry's extra key(s) already resolved
/// to local item IDs (or dropped, if unmatched) - everything the web menu
/// renderer needs in one response, without it having to separately fetch and
/// join the menu/binding data itself.
/// </summary>
public sealed class RenderableMenuDocument
{
    public required Guid MenuId { get; init; }

    public required string Root { get; init; }

    public BackgroundSpec? Background { get; init; }

    public ThemeSpec? Theme { get; init; }

    public MenuLayout? Layout { get; init; }

    public AudioSpec? Audio { get; init; }

    /// <summary>The item's Jellyfin theme-song item ids; only filled when a menu uses music source=themeSong.</summary>
    public List<Guid>? ThemeSongs { get; init; }

    /// <summary>
    /// This item's trailers, local files first then YouTube. Only filled when a background
    /// uses source=trailer; a background's trailerIndex indexes into this list.
    /// </summary>
    public List<RenderableTrailer>? Trailers { get; init; }

    /// <summary>
    /// The feature's chapters. Only filled when a menu has a "chapters" entry or a playFeature
    /// with startChapter; chapter numbers in menus are 1-based positions in this list.
    /// </summary>
    public List<RenderableChapter>? Chapters { get; init; }

    public required Dictionary<string, RenderableMenu> Menus { get; init; }
}

/// <summary>One chapter of the feature, as the renderer needs it.</summary>
public sealed class RenderableChapter
{
    /// <summary>0-based index (what Jellyfin's chapter image URL uses).</summary>
    public int Index { get; init; }

    public string? Name { get; init; }

    public long StartTicks { get; init; }

    /// <summary>True if Jellyfin has extracted a thumbnail for this chapter. The path itself is never sent.</summary>
    public bool HasImage { get; init; }

    /// <summary>Cache-buster for the thumbnail URL (ticks of its modification time).</summary>
    public long? ImageStamp { get; init; }
}

/// <summary>One playable trailer: a local file (ItemId) or a YouTube video (VideoId, validated).</summary>
public sealed class RenderableTrailer
{
    /// <summary>"local" or "youtube".</summary>
    public required string Kind { get; init; }

    public Guid? ItemId { get; init; }

    public string? VideoId { get; init; }

    public string? Name { get; init; }
}

public sealed class RenderableMenu
{
    public required string Title { get; init; }

    public BackgroundSpec? Background { get; init; }

    public ThemeSpec? Theme { get; init; }

    public MenuLayout? Layout { get; init; }

    public AudioSpec? Audio { get; init; }

    public required List<RenderableEntry> Entries { get; init; }
}

/// <summary>
/// One menu entry. Only the fields relevant to <see cref="Action"/> are set -
/// mirrors how the entries are described in schema/menu.schema.json itself.
/// </summary>
public sealed class RenderableEntry
{
    public required string Action { get; init; }

    public required string Label { get; init; }

    // Presentation, copied from the menu entry for every action type.
    public PositionSpec? Position { get; set; }

    public string? Style { get; set; }

    public string? Image { get; set; }

    public string? ImageFocus { get; set; }

    /// <summary>playFeature only.</summary>
    public int? StartChapter { get; init; }

    /// <summary>playExtra only. Null if the extra has no matched local item.</summary>
    public Guid? ItemId { get; init; }

    /// <summary>playSequence only. Unmatched extras are dropped, not left as gaps.</summary>
    public List<Guid>? ItemIds { get; init; }

    /// <summary>submenu only: the key to look up in the parent document's Menus.</summary>
    public string? Menu { get; init; }

    /// <summary>chapters only.</summary>
    public int? PerPage { get; init; }
}
