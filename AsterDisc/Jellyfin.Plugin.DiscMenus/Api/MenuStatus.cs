namespace Jellyfin.Plugin.DiscMenus.Api;

/// <summary>
/// Where one menu file stands in automatic discovery: which library item it is bound to, or why
/// it isn't. One of these per <c>*.menu.json</c> in the menus folder.
/// </summary>
public sealed class MenuStatus
{
    /// <summary>The menu file, relative to the menus folder (never an absolute server path).</summary>
    public required string MenuFile { get; init; }

    /// <summary>The menu's own id; <see cref="Guid.Empty"/> if the file could not be read.</summary>
    public Guid MenuId { get; init; }

    /// <summary>
    /// "bound" (shown on the item), "pending" (no matching title in the library yet),
    /// "ambiguous" (several equally good items: needs a person), "conflict" (another menu already
    /// claims the item) or "invalid" (the file or its binding could not be loaded).
    /// </summary>
    public required string State { get; init; }

    /// <summary>"manual" for a binding file written by hand next to the menu, "auto" for one the plugin created.</summary>
    public string? Source { get; init; }

    public string? Title { get; init; }

    public string? Edition { get; init; }

    public Guid? ItemId { get; init; }

    public string? ItemName { get; init; }

    /// <summary>For "ambiguous": the library items that all fit.</summary>
    public List<MenuStatusCandidate>? Candidates { get; init; }

    /// <summary>Plain-language explanation for anything other than a clean bind.</summary>
    public string? Message { get; init; }

    public int? ExtrasTotal { get; init; }

    public int? ExtrasMatched { get; init; }
}

public sealed class MenuStatusCandidate
{
    public Guid Id { get; init; }

    public string? Name { get; init; }

    public int? Year { get; init; }
}
