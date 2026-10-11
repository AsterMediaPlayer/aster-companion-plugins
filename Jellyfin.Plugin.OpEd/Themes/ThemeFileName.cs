using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OpEd.Themes;

/// <summary>
/// Reads OP/ED identity out of a file name. Accepts plain names ("OP1.webm", "ED 2.ogg", "OP.mp3") and AnimeThemes'
/// own names ("SousouNoFrieren-OP1.webm", "SousouNoFrieren-ED1-TV.webm", "Foo-OP2v2-NCBD1080.ogg").
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

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>Parses a file name; false when it isn't recognisably an OP or ED.</summary>
    public static bool TryParse(string fileName, out string type, out int sequence, out int version, out string variant)
    {
        type = string.Empty;
        sequence = 1;
        version = 1;
        variant = string.Empty;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var m = SlugRegex().Match(name);
        if (!m.Success)
        {
            return false;
        }

        type = m.Groups["type"].Value.ToUpperInvariant();
        if (m.Groups["seq"].Success && int.TryParse(m.Groups["seq"].Value, out var seq) && seq > 0)
        {
            sequence = seq;
        }

        if (m.Groups["ver"].Success && int.TryParse(m.Groups["ver"].Value, out var ver) && ver > 0)
        {
            version = ver;
        }

        // The first purely alphabetic token after the slug that isn't a source tag is a variant (ED1-TV).
        foreach (var token in name[(m.Index + m.Length)..].Split(['-', ' ', '_', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.All(char.IsLetter) && !SourceTags.Contains(token))
            {
                variant = token.ToUpperInvariant();
            }

            break;
        }

        return true;
    }

    // OP/ED not glued to a preceding letter (so "TOP1" or "Speed" don't count), optional number, optional vN.
    [GeneratedRegex(@"(?<![A-Za-z])(?<type>OP|ED)[ ]?(?<seq>\d+)?(?:v(?<ver>\d+))?(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();
}
