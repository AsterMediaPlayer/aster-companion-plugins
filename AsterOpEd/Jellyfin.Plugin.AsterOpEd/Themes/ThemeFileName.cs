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
///   music videos              "OP1-MV.mp4", "OP1 (Music Video)", "OP1 - MV - YOASOBI", "OP1 - Yuusha (MV) - YOASOBI":
///                             the artist's own video of the song, kept with the theme like a full version
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

    /// <summary>Another version of a theme, kept with it rather than being a theme of its own.</summary>
    public enum Extra
    {
        /// <summary>The theme itself.</summary>
        None,

        /// <summary>The full-length song ("OP1-Full").</summary>
        Full,

        /// <summary>The song's music video ("OP1-MV").</summary>
        MusicVideo,
    }

    /// <summary>What a file name says about its theme.</summary>
    /// <param name="Kind">Whether the file is the theme itself or another version of it.</param>
    public sealed record Parsed(string Type, int Sequence, int Version, string Variant, string? Title, List<string> Artists, string? Episodes, Extra Kind = Extra.None)
    {
        public bool IsFull => Kind == Extra.Full;

        public bool IsMusicVideo => Kind == Extra.MusicVideo;
    }

    /// <summary>The tag that marks a full-length version: "OP1-Full".</summary>
    public const string FullTag = "FULL";

    /// <summary>The tag that marks a music video: "OP1-MV".</summary>
    public const string MusicVideoTag = "MV";

    private static Extra TagKind(string tag) => tag.ToUpperInvariant() switch
    {
        FullTag => Extra.Full,
        MusicVideoTag => Extra.MusicVideo,
        _ => Extra.None,
    };

    /// <summary>Which version a marker names: "Full", "(Full Version)", "MV", "[Music Video]".</summary>
    private static Extra MarkerKind(Match m) =>
        !m.Success ? Extra.None : m.Groups["mv"].Success ? Extra.MusicVideo : Extra.Full;

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
        var kind = Extra.None;
        var vm = VariantRegex().Match(rest);
        if (vm.Success && !SourceTags.Contains(vm.Groups[1].Value))
        {
            variant = vm.Groups[1].Value.ToUpperInvariant();
            rest = rest[vm.Length..];
        }

        // "-Full" and "-MV" aren't variants: they're other versions of the same theme ("OP1-TV-Full" for a
        // variant's).
        if (TagKind(variant) is not Extra.None and var tagged)
        {
            kind = tagged;
            variant = string.Empty;
        }
        else if (VariantRegex().Match(rest) is { Success: true } fm && TagKind(fm.Groups[1].Value) is not Extra.None and var second)
        {
            kind = second;
            rest = rest[fm.Length..];
        }

        // "OP1 Full", "OP1 (Full)", "OP1 [Music Video]": the same, written as a word rather than a tag.
        if (MarkerKind(ExtraWordRegex().Match(rest)) is not Extra.None and var worded)
        {
            kind = worded;
            rest = rest[ExtraWordRegex().Match(rest).Length..];
        }

        // Song details: " - Title - Artist, Artist [episodes]".
        string? title = null;
        string? episodes = null;
        var artists = new List<string>();
        var dm = DetailsRegex().Match(rest);
        if (dm.Success)
        {
            var parts = dm.Groups["details"].Value.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            // "OP1 - Full - Eve": "Full" (or "MV") where the title goes marks that version; what follows is the
            // artists when it's all there is, else the title and artists as usual.
            if (parts.Length > 0 && MarkerKind(ExtraMarkerRegex().Match(parts[0])) is not Extra.None and var marked)
            {
                kind = marked;
                parts = parts[1..];
                if (parts.Length == 1)
                {
                    artists = SplitArtists(parts[0]);
                    parts = [];
                }
            }

            // "OP1 - Yuusha (Full) - YOASOBI": a "(Full)" or "(MV)" after the title.
            if (parts.Length > 0 && ExtraSuffixRegex().Match(parts[0]) is { Success: true } sm)
            {
                kind = MarkerKind(sm);
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

        return new Parsed(type, sequence, version, variant, title, artists, episodes, kind);
    }

    public static List<string> SplitArtists(string value) =>
        [.. value.Split([", ", " & ", "; "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    // OP/ED not glued to a preceding letter (so "TOP1" or "Speed" don't count), optional number, optional vN.
    [GeneratedRegex(@"(?<![A-Za-z])(?<type>OP|ED)[ ]?(?<seq>\d+)?(?:v(?<ver>\d+))?(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"^-([A-Za-z]+)(?=$|[-_. ])")]
    private static partial Regex VariantRegex();

    // The words for each version: "Full", "Full Version", "Full Size"; "MV", "Music Video".
    private const string ExtraWords = @"(?:(?<full>full(?:[\s_-]*(?:version|ver\.?|size|length|song))?)|(?<mv>mv|music[\s_-]*video))";

    // " Full", " (Full)", " [Music Video]", "_MV" straight after the code (but not " - Full", which is song details).
    [GeneratedRegex(@"^(?:[\s_]*[\(\[]\s*" + ExtraWords + @"\s*[\)\]]|[\s_]+" + ExtraWords + @"(?=$|[\s_.]))", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraWordRegex();

    [GeneratedRegex(@"^[\(\[]?\s*" + ExtraWords + @"\s*[\)\]]?$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraMarkerRegex();

    // "Yuusha (Full)", "Yuusha [Music Video]" at the end of a title.
    [GeneratedRegex(@"\s*[\(\[]\s*" + ExtraWords + @"\s*[\)\]]$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraSuffixRegex();

    // " - details [eps]" — the episodes bracket is optional, as is "eps"/"ep" inside it.
    [GeneratedRegex(@"^\s+-\s+(?<details>.*?)\s*(?:\[(?:eps?\.?\s*)?(?<eps>[\d][\d ,\-]*)\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DetailsRegex();
}
