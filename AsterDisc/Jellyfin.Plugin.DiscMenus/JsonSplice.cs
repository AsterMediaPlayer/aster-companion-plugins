using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>
/// Changes one top-level value in JSON text without re-serialising the rest, so a copy or a restored backup keeps the author's own formatting.
/// Only top-level string and number values are supported (that is all a menu's <c>menuId</c> and <c>revision</c> are).
/// </summary>
public static class JsonSplice
{
    /// <summary>
    /// Replaces the value of top-level property <paramref name="name"/> with <paramref name="newRawValue"/> (already valid JSON text, such as
    /// <c>"abc"</c> or <c>7</c>). Throws <see cref="JsonException"/> if the text is not an object or the property is missing or not a string/number.
    /// </summary>
    public static string ReplaceTopLevel(string json, string name, string newRawValue)
    {
        var bytes = new UTF8Encoding(false).GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        long? start = null, end = null;
        var depth0Object = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 0)
            {
                depth0Object = true;
            }

            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals(name))
            {
                if (!reader.Read())
                {
                    break;
                }

                if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number))
                {
                    throw new JsonException($"'{name}' is not a string or a number.");
                }

                start = reader.TokenStartIndex;
                end = reader.TokenType == JsonTokenType.String
                    ? reader.TokenStartIndex + reader.ValueSpan.Length + 2 // the quotes are not part of ValueSpan
                    : reader.TokenStartIndex + reader.ValueSpan.Length;
                break;
            }
        }

        if (!depth0Object)
        {
            throw new JsonException("The text is not a JSON object.");
        }

        if (start is null || end is null)
        {
            throw new JsonException($"There is no top-level '{name}'.");
        }

        var head = Encoding.UTF8.GetString(bytes, 0, (int)start.Value);
        var tail = Encoding.UTF8.GetString(bytes, (int)end.Value, bytes.Length - (int)end.Value);
        return head + newRawValue + tail;
    }
}
