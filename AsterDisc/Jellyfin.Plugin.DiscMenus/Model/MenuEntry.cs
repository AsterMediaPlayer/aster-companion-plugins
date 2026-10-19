using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DiscMenus.Model;

/// <summary>
/// Mirrors schema/menu.schema.json's $defs/entry oneOf. Discriminated by
/// <see cref="Action"/>; see <see cref="MenuEntryJsonConverter"/>.
/// </summary>
[JsonConverter(typeof(MenuEntryJsonConverter))]
public abstract class MenuEntry
{
    public required string Action { get; init; }

    public required string Label { get; init; }

    /// <summary>Optional authored placement; null means "flow in the default column".</summary>
    public PositionSpec? Position { get; init; }

    /// <summary>One of text/frame/glow/arrow; null falls back to the menu layout's default.</summary>
    public string? Style { get; init; }

    /// <summary>https or data: image URL for button artwork (schema-validated).</summary>
    public string? Image { get; init; }

    /// <summary>Artwork shown while the button is highlighted.</summary>
    public string? ImageFocus { get; init; }
}

/// <summary>Play the main feature, optionally from a chapter.</summary>
public sealed class PlayFeatureEntry : MenuEntry
{
    public int? StartChapter { get; init; }
}

/// <summary>Play one extra, resolved via the binding file.</summary>
public sealed class PlayExtraEntry : MenuEntry
{
    public required string Extra { get; init; }
}

/// <summary>Play several extras in order (a "Play All").</summary>
public sealed class PlaySequenceEntry : MenuEntry
{
    public required List<string> Extras { get; init; }
}

/// <summary>Open another menu.</summary>
public sealed class SubmenuEntry : MenuEntry
{
    public required string Menu { get; init; }
}

/// <summary>Scene selection generated from the feature's chapters.</summary>
public sealed class ChaptersEntry : MenuEntry
{
    public int? PerPage { get; init; }

    /// <summary>Optional menu key whose title/background/layout style the generated scene screen.</summary>
    public string? Menu { get; init; }
}

/// <summary>Jump straight to the root menu (a "Home" / "Main Menu" button).</summary>
public sealed class HomeEntry : MenuEntry
{
}

/// <summary>Return to the previous menu.</summary>
public sealed class BackEntry : MenuEntry
{
}
