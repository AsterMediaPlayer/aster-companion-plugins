using System.Text.Json.Serialization;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DiscMenus.Model;

/// <summary>
/// Mirrors schema/menu.schema.json: the shareable menu definition.
/// </summary>
public sealed class MenuDocument
{
    public int SchemaVersion { get; init; }

    public Guid MenuId { get; init; }

    public int Revision { get; init; }

    public MenuMeta? Meta { get; init; }

    public required MatchInfo Match { get; init; }

    public BackgroundSpec? Background { get; init; }

    public ThemeSpec? Theme { get; init; }

    /// <summary>Default music and button sounds; a menu's own audio overrides it per part.</summary>
    public AudioSpec? Audio { get; init; }

    /// <summary>Default layout every menu inherits, overridden field by field by a menu's own layout.</summary>
    public MenuLayout? Layout { get; init; }

    public Dictionary<string, ExtraSpec> Extras { get; init; } = new();

    public required string Root { get; init; }

    public required Dictionary<string, MenuDef> Menus { get; init; }
}

public sealed class MenuMeta
{
    public string? Author { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? Updated { get; init; }

    public string? Language { get; init; }

    public string? Notes { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MatchItemType
{
    Movie,
    Series,
    Season,
}

public sealed class MatchInfo
{
    public MatchItemType ItemType { get; init; }

    public Dictionary<string, string> ProviderIds { get; init; } = new();

    public int? SeasonNumber { get; init; }

    public ReleaseInfo? Release { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReleaseFormat
{
    DVD,
    BluRay,
    UHD,
    Digital,
    Other,
}

public sealed class ReleaseInfo
{
    public string? Edition { get; init; }

    public ReleaseFormat? Format { get; init; }

    public string? Region { get; init; }

    public int? Year { get; init; }

    public string? Publisher { get; init; }

    public string? Upc { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackgroundSource
{
    [JsonStringEnumMemberName("jellyfin")]
    Jellyfin,

    [JsonStringEnumMemberName("tmdb")]
    Tmdb,

    [JsonStringEnumMemberName("fanart")]
    Fanart,

    [JsonStringEnumMemberName("color")]
    Color,

    [JsonStringEnumMemberName("image")]
    Image,

    [JsonStringEnumMemberName("trailer")]
    Trailer,
}

/// <summary>
/// Image references only; the reference image is fetched/rendered locally,
/// never stored in the shared menu file.
/// </summary>
public sealed class BackgroundSpec
{
    public BackgroundSource Source { get; init; }

    /// <summary>
    /// Jellyfin's own ImageType enum, reused directly (README: "Verified
    /// against server source"). It isn't decorated with a string-enum
    /// converter in MediaBrowser.Model, so it must be applied here.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ImageType? ImageType { get; init; }

    public int? Index { get; init; }

    public string? TmdbFilePath { get; init; }

    /// <summary>For source=tmdb: w780, w1280 (default) or original.</summary>
    public string? TmdbSize { get; init; }

    public string? FanartId { get; init; }

    public string? Color { get; init; }

    /// <summary>For source=image: https URL, data: URI or asset: reference.</summary>
    public string? Image { get; init; }

    /// <summary>For source=trailer: 0-based index into the item's trailers (local files first, then YouTube).</summary>
    public int? TrailerIndex { get; init; }

    /// <summary>For source=trailer: default true.</summary>
    public bool? Muted { get; init; }

    /// <summary>For source=trailer: picture shown until the video plays, and as the fallback.</summary>
    public string? Poster { get; init; }

    public double? Dim { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThemeAlign
{
    [JsonStringEnumMemberName("left")]
    Left,

    [JsonStringEnumMemberName("center")]
    Center,

    [JsonStringEnumMemberName("right")]
    Right,
}

public sealed class ThemeSpec
{
    public required string Id { get; init; }

    public string? Accent { get; init; }

    public ThemeAlign? Align { get; init; }

    public string? TextColor { get; init; }

    /// <summary>One of sans/serif/condensed/wide/mono (a built-in stack; never an arbitrary name).</summary>
    public string? Font { get; init; }

    /// <summary>Percent of the screen height.</summary>
    public double? FontSize { get; init; }

    public bool? Uppercase { get; init; }

    public bool? Bold { get; init; }

    public double? LetterSpacing { get; init; }
}

/// <summary>
/// An extra this release is expected to contain. Deliberately excludes file
/// names/paths; duration is the primary local-matching signal.
/// </summary>
public sealed class ExtraSpec
{
    /// <summary>
    /// Jellyfin's own ExtraType enum, reused directly (README: "Verified
    /// against server source"). See the JsonConverter note on
    /// <see cref="BackgroundSpec.ImageType"/>.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExtraType Type { get; init; }

    public double DurationSec { get; init; }

    public double? ToleranceSec { get; init; }

    public int? Ordinal { get; init; }

    public bool? Optional { get; init; }
}

public sealed class MenuDef
{
    public required string Title { get; init; }

    public BackgroundSpec? Background { get; init; }

    public ThemeSpec? Theme { get; init; }

    public MenuLayout? Layout { get; init; }

    public AudioSpec? Audio { get; init; }

    public required List<MenuEntry> Entries { get; init; }
}

/// <summary>Where an element sits, in percent of the menu screen.</summary>
public sealed class PositionSpec
{
    public double X { get; init; }

    public double Y { get; init; }

    public double? W { get; init; }

    public double? H { get; init; }

    /// <summary>Which point of the element x/y refers to (e.g. "center", "bottom-left"); default top-left.</summary>
    public string? Anchor { get; init; }
}

public sealed class MenuLayout
{
    public PositionSpec? TitlePosition { get; init; }

    public bool? HideTitle { get; init; }

    public string? ButtonStyle { get; init; }

    public FlowSpec? Flow { get; init; }

    public TransitionSpec? Transition { get; init; }

    public List<LayerSpec>? Layers { get; init; }
}

/// <summary>Animation between menus/pages: none, fade, slide, rise, zoom or wipe.</summary>
public sealed class TransitionSpec
{
    public required string Style { get; init; }

    public int? DurationMs { get; init; }
}

public sealed class AudioSpec
{
    public MusicSpec? Music { get; init; }

    public SoundsSpec? Sounds { get; init; }
}

/// <summary>Background music: a file (asset:/https), the item's Jellyfin theme song, or none.</summary>
public sealed class MusicSpec
{
    public required string Source { get; init; }

    public string? File { get; init; }

    public double? Volume { get; init; }
}

/// <summary>Button sounds: a built-in preset and/or per-sound audio files.</summary>
public sealed class SoundsSpec
{
    public string? Preset { get; init; }

    public double? Volume { get; init; }

    public string? Move { get; init; }

    public string? Select { get; init; }

    public string? Back { get; init; }
}

/// <summary>Automatic grid placement with paging (More / Previous).</summary>
public sealed class FlowSpec
{
    public required PositionSpec Region { get; init; }

    public int Columns { get; init; }

    public int Rows { get; init; }

    public string? MoreLabel { get; init; }

    public string? PreviousLabel { get; init; }
}

/// <summary>A decorative layer behind the buttons: type "panel" (flat box) or "image".</summary>
public sealed class LayerSpec
{
    public required string Type { get; init; }

    public required PositionSpec Position { get; init; }

    public string? Fill { get; init; }

    public double? Opacity { get; init; }

    public string? BorderColor { get; init; }

    public double? BorderWidth { get; init; }

    public double? Radius { get; init; }

    public string? Image { get; init; }

    /// <summary>contain (default) / fill / cover, for image layers.</summary>
    public string? Fit { get; init; }
}
