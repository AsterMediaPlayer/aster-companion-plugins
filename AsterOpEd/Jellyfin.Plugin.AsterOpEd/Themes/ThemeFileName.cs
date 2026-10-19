using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AsterOpEd.Themes;

/// <summary>
/// Reads OP/ED identity, and optionally song details, out of a file name. Accepts:
///   plain names               "OP1.webm", "ED 2.ogg", "OP.mp3"
///   AnimeThemes' own names    "SousouNoFrieren-OP1.webm", "SousouNoFrieren-ED1-TV.webm", "Foo-OP2v2-NCBD1080.ogg"
///   names with song details   "OP1 - Yuusha - YOASOBI.webm", "ED1-TV - bliss - milet [1].ogg",
///                             "OP2 - Haru - Yorushika [17-28].webm"  (title - artists [episodes])
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
    public sealed record Parsed(string Type, int Sequence, int Version, string Variant, string? Title, List<string> Artists, string? Episodes);

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
        var vm = VariantRegex().Match(rest);
        if (vm.Success && !SourceTags.Contains(vm.Groups[1].Value))
        {
            variant = vm.Groups[1].Value.ToUpperInvariant();
            rest = rest[vm.Length..];
        }

        // Song details: " - Title - Artist, Artist [episodes]".
        string? title = null;
        string? episodes = null;
        var artists = new List<string>();
        var dm = DetailsRegex().Match(rest);
        if (dm.Success)
        {
            var parts = dm.Groups["details"].Value.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
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

        return new Parsed(type, sequence, version, variant, title, artists, episodes);
    }

    public static List<string> SplitArtists(string value) =>
        [.. value.Split([", ", " & ", "; "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    // OP/ED not glued to a preceding letter (so "TOP1" or "Speed" don't count), optional number, optional vN.
    [GeneratedRegex(@"(?<![A-Za-z])(?<type>OP|ED)[ ]?(?<seq>\d+)?(?:v(?<ver>\d+))?(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"^-([A-Za-z]+)(?=$|[-_. ])")]
    private static partial Regex VariantRegex();

    // " - details [eps]" — the episodes bracket is optional, as is "eps"/"ep" inside it.
    [GeneratedRegex(@"^\s+-\s+(?<details>.*?)\s*(?:\[(?:eps?\.?\s*)?(?<eps>[\d][\d ,\-]*)\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DetailsRegex();
}
