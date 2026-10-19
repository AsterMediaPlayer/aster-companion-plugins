using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DiscMenus.Model;

/// <summary>
/// Jellyfin item IDs appear as either 32-hex "N" format or hyphenated "D"
/// format (see binding.schema.json's jellyfinId pattern). The built-in
/// System.Text.Json Guid converter only accepts "D". Guid.Parse accepts both.
/// </summary>
public sealed class FlexibleGuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Guid.Parse(reader.GetString() ?? throw new JsonException("Expected a GUID string."));

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("N"));
}
