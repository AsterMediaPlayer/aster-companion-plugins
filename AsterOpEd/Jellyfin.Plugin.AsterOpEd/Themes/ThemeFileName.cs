using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AsterOpEd.Themes;

/// <summary>
/// Reads OP/ED identity, and optionally song details, out of a file name. Accepts:
///   plain names               "OP1.webm", "ED 2.ogg", "OP.mp3"
///   AnimeThemes' own names    "SousouNoFrieren-OP1.webm", "SousouNoFrieren-ED1-TV.webm", "Foo-OP2v2-NCBD1080.ogg"
///   names with song details   "OP1 - Yuusha - YOASOBI.webm", "ED1-TV - bliss - milet [1].ogg",
///                             "OP2 - Haru - Yorushika [17-28].webm"  (title - artists [episodes])
///   full-length versions      "OP1-Full.mp4", "OP1-Full - Yuusha - YOASOBI.m4a": the whole song (the single, or its
///                             music video) for the theme of the same name, not a theme of its own. Written other
///                             ways too: "OP1 Full", "OP1 (Full)", "OP1 [Full Version]", "OP1 - Full - Eve",
///                             "OP1 - Yuusha (Full) - YOASOBI"
/// </summary>
public static partial class ThemeFileName
{
    public static readonly IReadOnlySet<string> VideoExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".webm", ".mp4", ".mkv", ".m4v", ".mov" };

    public static readonly IReadOnlySet<string> AudioExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ogg", ".opus", ".mp3", ".flac", ".m4a", ".aac", ".wav" };

    /// <summary>Source/quality tags AnimeThemes appends to file names; they don't make a different theme.</summary>
    private static readonly HashSet<string> SourceTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "NC", "BD", "DVD", "WEB", "LR", "TRANS", "OVER", "SUBBED", "LYRICS", "NCBD", "NCDVD", "NCWEB", "RAW",
    };

    /// <summary>What a file name says about its theme.</summary>
    /// <param name="IsFull">A full-length version of the theme (the "-Full" tag), not the theme itself.</param>
    public sealed record Parsed(string Type, int Sequence, int Version, string Variant, string? Title, List<string> Artists, string? Episodes, bool IsFull = false);

    /// <summary>The tag that marks a full-length version: "OP1-Full".</summary>
    public const string FullTag = "FULL";

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>Parses a file name; null when it isn't recognisably an OP or ED.</summary>
    public static Parsed? Parse(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var m = SlugRegex().Match(name);
        if (!m.Success)
        {
            return null;
        }

        var type = m.Groups["type"].Value.ToUpperInvariant();
        var sequence = m.Groups["seq"].Success && int.TryParse(m.Groups["seq"].Value, out var seq) && seq > 0 ? seq : 1;
        var version = m.Groups["ver"].Success && int.TryParse(m.Groups["ver"].Value, out var ver) && ver > 0 ? ver : 1;
        var rest = name[(m.Index + m.Length)..];

        // A variant is glued on with a hyphen and no spaces ("ED1-TV"); "OP1 - Title" is song details instead.
        var variant = string.Empty;
        var isFull = false;
        var vm = VariantRegex().Match(rest);
        if (vm.Success && !SourceTags.Contains(vm.Groups[1].Value))
        {
            variant = vm.Groups[1].Value.ToUpperInvariant();
            rest = rest[vm.Length..];
        }

        // "-Full" isn't a variant: it's the same theme's full-length version ("OP1-TV-Full" for a variant's).
        if (variant == FullTag)
        {
            isFull = true;
            variant = string.Empty;
        }
        else if (VariantRegex().Match(rest) is { Success: true } fm && string.Equals(fm.Groups[1].Value, FullTag, StringComparison.OrdinalIgnoreCase))
        {
            isFull = true;
            rest = rest[fm.Length..];
        }

        // "OP1 Full", "OP1 (Full)", "OP1 [Full Version]": the same, written as a word rather than a tag.
        if (FullWordRegex().Match(rest) is { Success: true } wm)
        {
            isFull = true;
            rest = rest[wm.Length..];
        }

        // Song details: " - Title - Artist, Artist [episodes]".
        string? title = null;
        string? episodes = null;
        var artists = new List<string>();
        var dm = DetailsRegex().Match(rest);
        if (dm.Success)
        {
            var parts = dm.Groups["details"].Value.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            // "OP1 - Full - Eve": "Full" where the title goes marks the full version; what follows is the artists
            // when it's all there is, else the title and artists as usual.
            if (parts.Length > 0 && IsFullMarker(parts[0]))
            {
                isFull = true;
                parts = parts[1..];
                if (parts.Length == 1)
                {
                    artists = SplitArtists(parts[0]);
                    parts = [];
                }
            }

            // "OP1 - Yuusha (Full) - YOASOBI": a "(Full)" after the title.
            if (parts.Length > 0 && FullSuffixRegex().Match(parts[0]) is { Success: true } sm)
            {
                isFull = true;
                parts[0] = parts[0][..sm.Index].TrimEnd();
            }

            if (parts.Length == 1)
            {
                title = parts[0];
            }
            else if (parts.Length > 1)
            {
                // Titles may contain " - " themselves; the artists are always the last part.
                title = string.Join(" - ", parts[..^1]);
                artists = SplitArtists(parts[^1]);
            }

            if (dm.Groups["eps"].Success)
            {
                episodes = dm.Groups["eps"].Value.Trim();
            }
        }

        return new Parsed(type, sequence, version, variant, title, artists, episodes, isFull);
    }

    /// <summary>"Full", "Full Version", "(Full)", "[Full Size]".</summary>
    private static bool IsFullMarker(string value) => FullMarkerRegex().IsMatch(value);

    public static List<string> SplitArtists(string value) =>
        [.. value.Split([", ", " & ", "; "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    // OP/ED not glued to a preceding letter (so "TOP1" or "Speed" don't count), optional number, optional vN.
    [GeneratedRegex(@"(?<![A-Za-z])(?<type>OP|ED)[ ]?(?<seq>\d+)?(?:v(?<ver>\d+))?(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"^-([A-Za-z]+)(?=$|[-_. ])")]
    private static partial Regex VariantRegex();

    // " Full", " (Full)", " [Full Version]", "_Full" straight after the code (but not " - Full", which is song details).
    [GeneratedRegex(@"^(?:[\s_]*[\(\[]\s*full(?:[\s_-]*(?:version|ver\.?|size|length|song))?\s*[\)\]]|[\s_]+full(?:[\s_-]*(?:version|ver\.?|size|length|song))?(?=$|[\s_.]))", RegexOptions.IgnoreCase)]
    private static partial Regex FullWordRegex();

    [GeneratedRegex(@"^[\(\[]?\s*full(?:[\s_-]*(?:version|ver\.?|size|length|song))?\s*[\)\]]?$", RegexOptions.IgnoreCase)]
    private static partial Regex FullMarkerRegex();

    // "Yuusha (Full)", "Yuusha [Full Version]" at the end of a title.
    [GeneratedRegex(@"\s*[\(\[]\s*full(?:[\s_-]*(?:version|ver\.?|size|length|song))?\s*[\)\]]$", RegexOptions.IgnoreCase)]
    private static partial Regex FullSuffixRegex();

    // " - details [eps]" — the episodes bracket is optional, as is "eps"/"ep" inside it.
    [GeneratedRegex(@"^\s+-\s+(?<details>.*?)\s*(?:\[(?:eps?\.?\s*)?(?<eps>[\d][\d ,\-]*)\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DetailsRegex();
}
