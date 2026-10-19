using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DiscMenus.Model;

/// <summary>
/// Dispatches on the "action" field to the concrete MenuEntry subtype.
/// CanConvert is restricted to exactly typeof(MenuEntry) so the recursive
/// Deserialize&lt;TConcrete&gt; calls below use plain POCO deserialization
/// instead of re-entering this converter.
/// </summary>
public sealed class MenuEntryJsonConverter : JsonConverter<MenuEntry>
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(MenuEntry);

    public override MenuEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.TryGetProperty("action", out var actionProp))
        {
            throw new JsonException("Menu entry is missing required 'action' field.");
        }

        var action = actionProp.GetString();
        var json = root.GetRawText();
        return action switch
        {
            "playFeature" => JsonSerializer.Deserialize<PlayFeatureEntry>(json, options)!,
            "playExtra" => JsonSerializer.Deserialize<PlayExtraEntry>(json, options)!,
            "playSequence" => JsonSerializer.Deserialize<PlaySequenceEntry>(json, options)!,
            "submenu" => JsonSerializer.Deserialize<SubmenuEntry>(json, options)!,
            "chapters" => JsonSerializer.Deserialize<ChaptersEntry>(json, options)!,
            "back" => JsonSerializer.Deserialize<BackEntry>(json, options)!,
            "home" => JsonSerializer.Deserialize<HomeEntry>(json, options)!,
            _ => throw new JsonException($"Unknown menu entry action '{action}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, MenuEntry value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
