using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OpEd.Themes;

/// <summary>
/// The optional themes.json in a show's theme folder: song details you write yourself (or with the editor on the
/// settings page). Keys are the theme's place in the folder: "OP1", "ED1-TV", "OP1v2", "Season 2/OP1",
/// "anidb-18886/ED2". Every field is optional; whatever is given overrides the file name and AnimeThemes.
///
/// {
///   "OP1":          { "title": "Yuusha", "artists": ["YOASOBI"], "episodes": "1-16" },
///   "ED1-TV":       { "title": "bliss", "artists": "milet", "episodes": "1", "spoiler": false },
///   "Season 2/OP1": { "title": "lulu.", "artists": ["Mrs. GREEN APPLE"] }
/// }
/// </summary>
public static partial class ThemeDetailsFile
{
    public const string FileName = "themes.json";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>One entry. Null fields aren't set (fall through to the file name / AnimeThemes).</summary>
    public sealed record Details(string? Title, List<string>? Artists, string? Episodes, bool? Spoiler, bool? Nsfw)
    {
        public bool IsEmpty => Title is null && Artists is null && Episodes is null && Spoiler is null && Nsfw is null;
    }

    /// <summary>The key a theme has in its show's themes.json.</summary>
    public static string KeyFor(Theme theme)
    {
        var prefix = theme.FolderAniDb is int a ? $"anidb-{a}/" : theme.FolderSeason is int s ? $"Season {s}/" : string.Empty;
        return prefix + theme.Type + theme.Sequence + (theme.Version > 1 ? "v" + theme.Version : string.Empty)
            + (theme.Variant.Length > 0 ? "-" + theme.Variant : string.Empty);
    }

    /// <summary>Reads a show folder's themes.json; empty when there is none. Throws <see cref="JsonException"/> on bad JSON.</summary>
    public static Dictionary<string, Details> Load(string showDir)
    {
        var result = new Dictionary<string, Details>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(showDir, FileName);
        if (!File.Exists(path))
        {
            return result;
        }

        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (root is not JsonObject obj)
        {
            throw new JsonException("themes.json must be a JSON object of \"OP1\": { … } entries");
        }

        foreach (var (key, value) in obj)
        {
            if (value is not JsonObject entry)
            {
                continue;
            }

            List<string>? artists = entry["artists"] switch
            {
                JsonArray arr => [.. arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim())],
                JsonValue v when v.ToString() is { Length: > 0 } one => ThemeFileName.SplitArtists(one),
                _ => null,
            };
            result[NormalizeKey(key)] = new Details(
                Text(entry["title"]),
                artists,
                Text(entry["episodes"]),
                Flag(entry["spoiler"]),
                Flag(entry["nsfw"]));
        }

        return result;
    }

    /// <summary>Writes (or, when <paramref name="details"/> is empty, removes) one entry, keeping the rest of the file.</summary>
    public static void Save(string showDir, string key, Details details)
    {
        var path = Path.Combine(showDir, FileName);
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? []
            : [];

        // Drop any existing spelling of the same key ("season 02/op1" and "Season 2/OP1" are the same theme).
        foreach (var existing in root.Select(kv => kv.Key).Where(k => NormalizeKey(k).Equals(NormalizeKey(key), StringComparison.OrdinalIgnoreCase)).ToList())
        {
            root.Remove(existing);
        }

        if (!details.IsEmpty)
        {
            var entry = new JsonObject();
            if (details.Title is not null)
            {
                entry["title"] = details.Title;
            }

            if (details.Artists is not null)
            {
                entry["artists"] = new JsonArray([.. details.Artists.Select(a => (JsonNode)a)]);
            }

            if (details.Episodes is not null)
            {
                entry["episodes"] = details.Episodes;
            }

            if (details.Spoiler is not null)
            {
                entry["spoiler"] = details.Spoiler;
            }

            if (details.Nsfw is not null)
            {
                entry["nsfw"] = details.Nsfw;
            }

            root[key] = entry;
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Pretty) + Environment.NewLine);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>"season 02 / op1" → "Season 2/OP1"; "anidb 18886/ed2" → "anidb-18886/ED2".</summary>
    public static string NormalizeKey(string key)
    {
        var parts = key.Replace('\\', '/').Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return key;
        }

        var slug = parts[^1].ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (parts.Length == 1)
        {
            return slug;
        }

        var folder = parts[0];
        var sm = ShowMap.SeasonFolder().Match(folder);
        var am = AniDbFolder().Match(folder);
        folder = am.Success ? "anidb-" + am.Groups[1].Value
            : sm.Success ? "Season " + (sm.Groups[1].Success ? int.Parse(sm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0)
            : folder;
        return folder + "/" + slug;
    }

    private static string? Text(JsonNode? node) => node?.ToString() is { Length: > 0 } s ? s.Trim() : null;

    private static bool? Flag(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    [GeneratedRegex(@"^anidb[-_ ]?(\d+)$", RegexOptions.IgnoreCase)]
    internal static partial Regex AniDbFolder();
}
