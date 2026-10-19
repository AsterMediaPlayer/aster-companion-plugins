using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DiscMenus.Model;

/// <summary>
/// Mirrors schema/binding.schema.json: resolves a shared menu's extra keys
/// to this server's local Jellyfin item IDs. Never shared.
/// </summary>
public sealed class BindingDocument
{
    public int SchemaVersion { get; init; }

    public Guid MenuId { get; init; }

    public int MenuRevision { get; init; }

    [JsonConverter(typeof(FlexibleGuidJsonConverter))]
    public Guid ParentItemId { get; init; }

    /// <summary>Mutable: set by DiscMenuService.RunAutoMatch after each pass.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    public Dictionary<string, ExtraBinding> Bindings { get; init; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BindingStatus
{
    [JsonStringEnumMemberName("matched")]
    Matched,

    [JsonStringEnumMemberName("unmatched")]
    Unmatched,

    [JsonStringEnumMemberName("ignored")]
    Ignored,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BindingMethod
{
    [JsonStringEnumMemberName("duration")]
    Duration,

    [JsonStringEnumMemberName("duration+ordinal")]
    DurationOrdinal,

    [JsonStringEnumMemberName("manual")]
    Manual,
}

public sealed class ExtraBinding
{
    public BindingStatus Status { get; init; }

    [JsonConverter(typeof(FlexibleGuidJsonConverter))]
    public Guid? ItemId { get; init; }

    public BindingMethod? Method { get; init; }

    public double? Confidence { get; init; }

    public double? ObservedDurationSec { get; init; }
}
