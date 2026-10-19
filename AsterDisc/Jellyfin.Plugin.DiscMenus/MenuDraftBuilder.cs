using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>One local extra of a title, as the draft builder needs it.</summary>
/// <param name="Name">The item's name in the library (often straight from a disc rip, e.g. "Featurette - title 083").</param>
/// <param name="Type">Jellyfin's ExtraType name: Featurette, Short, Unknown, ...</param>
public sealed record DraftExtra(string Name, string Type, double DurationSec);

/// <summary>Everything the library knows about a title that a starter menu can be built from.</summary>
public sealed record DraftSource(
    string Title,
    int? Year,
    int? Width,
    IReadOnlyDictionary<string, string> ProviderIds,
    IReadOnlyList<DraftExtra> Extras,
    bool HasChapters,
    Guid? MenuId = null,
    DateTimeOffset? Created = null);

/// <summary>
/// Builds a starter menu (as schema-shaped JSON) from a library title: its provider ids, its real
/// extras with their types and durations, and a scene-selection entry if it has chapters. Pure and
/// free of Jellyfin types so it can be tested exhaustively. A draft is a starting point to edit,
/// not a finished design: labels come from the rip's own names and the structure is generic.
/// </summary>
public static class MenuDraftBuilder
{
    // How many extras a flat Special Features list may hold before they are grouped by type.
    private const int FlatListLimit = 8;

    // Schema caps a menu at 50 entries; leave room for Play All and Back.
    private const int MaxExtrasPerGroup = 46;

    private static readonly string[] TypeOrder =
    {
        "BehindTheScenes", "DeletedScene", "Featurette", "Interview", "Short", "Scene", "Clip", "Trailer", "Sample", "Unknown",
    };

    private static readonly Dictionary<string, (string Plural, string Singular)> TypeLabels = new()
    {
        ["BehindTheScenes"] = ("Behind the Scenes", "Behind the Scenes"),
        ["DeletedScene"] = ("Deleted Scenes", "Deleted Scene"),
        ["Featurette"] = ("Featurettes", "Featurette"),
        ["Interview"] = ("Interviews", "Interview"),
        ["Short"] = ("Shorts", "Short"),
        ["Scene"] = ("Scenes", "Scene"),
        ["Clip"] = ("Clips", "Clip"),
        ["Trailer"] = ("Trailers", "Trailer"),
        ["Sample"] = ("Samples", "Sample"),
        ["Unknown"] = ("Other", "Extra"),
    };

    // Theme music/video are for the title page itself, not disc extras.
    private static readonly HashSet<string> NotMenuExtras = new(StringComparer.Ordinal) { "ThemeSong", "ThemeVideo" };

    private static readonly Regex Tmdb = new("^[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex Imdb = new("^tt[0-9]{7,9}$", RegexOptions.Compiled);

    // "Featurette - " / "BnL Short - " style prefixes that ripping tools add.
    private static readonly Regex TypePrefix = new(
        @"^\s*(featurette|short|bnl short|deleted scene|behind the scenes|trailer|clip|interview|scene|sample|bonus)s?\s*[-:–]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Names that carry no meaning ("title 083", "track 4", "00012"): replaced by "Featurette 1".
    private static readonly Regex Meaningless = new(
        @"^\s*((title|track|chapter|clip|video|extra|bonus)\s*[_-]?\s*)?\d+\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Builds the draft. Throws <see cref="ArgumentException"/> if the title has no usable provider id.</summary>
    public static JsonObject Build(DraftSource source)
    {
        var providerIds = new JsonObject();
        foreach (var (provider, value) in source.ProviderIds)
        {
            var key = provider.ToLowerInvariant();
            if (key == "tmdb" && Tmdb.IsMatch(value))
            {
                providerIds["Tmdb"] = value;
            }
            else if (key == "imdb" && Imdb.IsMatch(value))
            {
                providerIds["Imdb"] = value;
            }
            else if (key == "tvdb" && Tmdb.IsMatch(value))
            {
                providerIds["Tvdb"] = value;
            }
        }

        if (providerIds.Count == 0)
        {
            throw new ArgumentException("This title has no TMDB, IMDB or TVDB id, so a menu can't be matched to it.");
        }

        var match = new JsonObject { ["itemType"] = "Movie", ["providerIds"] = providerIds };
        var release = new JsonObject();
        if (source.Width is >= 3000)
        {
            release["format"] = "UHD";
        }
        else if (source.Width is >= 1700)
        {
            release["format"] = "BluRay";
        }

        if (source.Year is >= 1900 and <= 2100)
        {
            release["year"] = source.Year;
        }

        if (release.Count > 0)
        {
            match["release"] = release;
        }

        var title = Plain(source.Title, 80);
        if (title.Length == 0)
        {
            title = "Main Menu";
        }

        // ---- extras: real types and durations, stable keys, sensible labels
        var usable = source.Extras
            .Where(e => !NotMenuExtras.Contains(e.Type) && e.DurationSec > 0)
            .OrderBy(e => Array.IndexOf(TypeOrder, e.Type) is var i && i < 0 ? TypeOrder.Length : i)
            .ThenBy(e => e.Name, NaturalComparer.Instance)
            .ThenBy(e => e.DurationSec)
            .ToList();

        var extrasJson = new JsonObject();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var perTypeCount = new Dictionary<string, int>();
        var items = new List<(string Key, string Type, string Label)>();
        foreach (var extra in usable)
        {
            perTypeCount[extra.Type] = perTypeCount.GetValueOrDefault(extra.Type) + 1;
            var key = UniqueKey(Slug(TypePrefix.Replace(extra.Name, string.Empty)), extra.Type, used);
            extrasJson[key] = new JsonObject
            {
                ["type"] = extra.Type,
                ["durationSec"] = Math.Round(extra.DurationSec, 1),
            };
            items.Add((key, extra.Type, LabelFor(extra, perTypeCount[extra.Type])));
        }

        // ---- menus
        var menus = new JsonObject();
        var mainEntries = new JsonArray
        {
            new JsonObject { ["action"] = "playFeature", ["label"] = "Play Movie" },
        };
        if (source.HasChapters)
        {
            mainEntries.Add(new JsonObject { ["action"] = "chapters", ["label"] = "Scene Selection", ["perPage"] = 6 });
        }

        if (items.Count > 0)
        {
            mainEntries.Add(new JsonObject { ["action"] = "submenu", ["label"] = "Special Features", ["menu"] = "features" });
            menus["features"] = BuildFeatures(items, menus);
        }

        menus["main"] = new JsonObject { ["title"] = title, ["entries"] = mainEntries };

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["menuId"] = (source.MenuId ?? Guid.NewGuid()).ToString("D"),
            ["revision"] = 1,
            ["meta"] = new JsonObject
            {
                ["author"] = "auto-draft",
                ["created"] = (source.Created ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["language"] = "en-US",
                ["notes"] = "Draft generated from this server's library. The extras, their types and durations are real; "
                    + "labels come from the files' own names and the layout is generic, so edit it to taste. "
                    + "Not tuned for sharing.",
            },
            ["match"] = match,
            ["background"] = new JsonObject { ["source"] = "jellyfin", ["imageType"] = "Backdrop", ["index"] = 0, ["dim"] = 0.5 },
            ["theme"] = new JsonObject { ["id"] = "default", ["accent"] = "#3ddc84", ["align"] = "left" },
            ["extras"] = extrasJson,
            ["root"] = "main",
            ["menus"] = menus,
        };
    }

    // Few extras: one flat list. Many: one submenu per type (with Play All), and a type with a
    // single extra is just a direct entry.
    private static JsonObject BuildFeatures(List<(string Key, string Type, string Label)> items, JsonObject menus)
    {
        var entries = new JsonArray();
        if (items.Count <= FlatListLimit)
        {
            foreach (var item in items)
            {
                entries.Add(Play(item));
            }
        }
        else
        {
            foreach (var group in items.GroupBy(i => i.Type))
            {
                var members = group.Take(MaxExtrasPerGroup).ToList();
                if (members.Count == 1)
                {
                    entries.Add(Play(members[0]));
                    continue;
                }

                var groupLabel = TypeLabels.TryGetValue(group.Key, out var l) ? l.Plural : group.Key;
                var menuKey = "group-" + Slug(groupLabel);
                var groupEntries = new JsonArray
                {
                    new JsonObject
                    {
                        ["action"] = "playSequence",
                        ["label"] = "Play All",
                        ["extras"] = new JsonArray(members.Select(m => (JsonNode)JsonValue.Create(m.Key)!).ToArray()),
                    },
                };
                foreach (var m in members)
                {
                    groupEntries.Add(Play(m));
                }

                groupEntries.Add(new JsonObject { ["action"] = "back", ["label"] = "Back" });
                menus[menuKey] = new JsonObject { ["title"] = groupLabel, ["entries"] = groupEntries };
                entries.Add(new JsonObject { ["action"] = "submenu", ["label"] = groupLabel, ["menu"] = menuKey });
            }
        }

        entries.Add(new JsonObject { ["action"] = "back", ["label"] = "Back" });
        return new JsonObject { ["title"] = "Special Features", ["entries"] = entries };
    }

    private static JsonObject Play((string Key, string Type, string Label) item) =>
        new() { ["action"] = "playExtra", ["label"] = item.Label, ["extra"] = item.Key };

    // The rip's own name when it means something; otherwise "Featurette 1" plus the length so the
    // person editing the draft can tell the files apart.
    private static string LabelFor(DraftExtra extra, int ordinalInType)
    {
        var cleaned = Plain(TypePrefix.Replace(extra.Name, string.Empty), 70);
        if (cleaned.Length > 0 && !Meaningless.IsMatch(cleaned))
        {
            return cleaned;
        }

        var singular = TypeLabels.TryGetValue(extra.Type, out var l) ? l.Singular : "Extra";
        var span = TimeSpan.FromSeconds(Math.Round(extra.DurationSec));
        var length = span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
        return $"{singular} {ordinalInType} ({length})";
    }

    // Plain text only (the schema forbids < and > in labels), trimmed to a length.
    private static string Plain(string? text, int max)
    {
        var cleaned = (text ?? string.Empty).Replace("<", string.Empty).Replace(">", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..max].TrimEnd();
    }

    /// <summary>Lowercase a-z0-9 words joined by hyphens, diacritics folded: a valid menu key stem.</summary>
    public static string Slug(string text)
    {
        var folded = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(c);
            }
        }

        var slug = Regex.Replace(folded.ToString().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 48 ? slug[..48].Trim('-') : slug;
    }

    private static string UniqueKey(string stem, string type, HashSet<string> used)
    {
        var baseKey = stem.Length > 0 && !Meaningless.IsMatch(stem.Replace('-', ' '))
            ? stem
            : Slug(TypeLabels.TryGetValue(type, out var l) ? l.Singular : "extra");
        if (baseKey.Length == 0)
        {
            baseKey = "extra";
        }

        var key = baseKey;
        for (var n = 2; !used.Add(key); n++)
        {
            key = $"{baseKey}-{n}";
        }

        return key;
    }

    /// <summary>Orders "title 2" before "title 10" instead of text order.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Regex.Split(x ?? string.Empty, "([0-9]+)");
            var b = Regex.Split(y ?? string.Empty, "([0-9]+)");
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var c = long.TryParse(a[i], out var na) && long.TryParse(b[i], out var nb)
                    ? na.CompareTo(nb)
                    : string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0)
                {
                    return c;
                }
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
