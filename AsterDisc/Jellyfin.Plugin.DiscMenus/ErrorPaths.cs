using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>
/// Turns what the loader says about a menu into a JSON Pointer ("/menus/main/entries/0/label") so the editor can select the offending text.
/// The loader's messages start with where they are (<c>main[0] label ...</c>, <c>menu 'main' title ...</c>, <c>document background ...</c>);
/// this reads that prefix and the property it names. The pointer is a best guess at the most specific spot: the editor walks up from it
/// until it finds text that exists, so a pointer to something absent (for example a missing property) still lands on its parent.
/// </summary>
public static class ErrorPaths
{
    private static readonly Regex MenuPrefix = new(@"^menu '([^']*)' (.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex EntryPrefix = new(@"^([^\s\[\]]+)\[(\d+)\] (.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AreaPrefix = new(@"^(document|[^\s']+) (.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Layer = new(@"^layer (\d+) (.*)$", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The JSON Pointer a loader message is about, or null when it can't be placed.</summary>
    public static string? FromMessage(string message)
    {
        if (message.StartsWith("root '", StringComparison.Ordinal))
        {
            return "/root";
        }

        if (MenuPrefix.Match(message) is { Success: true } mm)
        {
            var menu = "/menus/" + Escape(mm.Groups[1].Value);
            var rest = mm.Groups[2].Value;
            if (rest.StartsWith("title ", StringComparison.Ordinal))
            {
                return menu + "/title";
            }

            if (rest.StartsWith("needs a flow grid", StringComparison.Ordinal))
            {
                return menu + "/layout/flow";
            }

            if (rest.StartsWith("uses flow layout", StringComparison.Ordinal) || rest.StartsWith("mixes positioned", StringComparison.Ordinal))
            {
                return menu + "/entries";
            }

            return menu;
        }

        if (EntryPrefix.Match(message) is { Success: true } em)
        {
            var entry = "/menus/" + Escape(em.Groups[1].Value) + "/entries/" + em.Groups[2].Value;
            var rest = em.Groups[3].Value;
            if (rest.StartsWith("label ", StringComparison.Ordinal))
            {
                return entry + "/label";
            }

            if (rest.StartsWith("position ", StringComparison.Ordinal))
            {
                return entry + "/position";
            }

            if (rest.StartsWith("unknown anchor", StringComparison.Ordinal))
            {
                return entry + "/position/anchor";
            }

            if (rest.StartsWith("unknown button style", StringComparison.Ordinal))
            {
                return entry + "/style";
            }

            if (rest.StartsWith("image ", StringComparison.Ordinal))
            {
                return entry + "/image";
            }

            if (rest.StartsWith("unknown extra ", StringComparison.Ordinal))
            {
                return entry + "/extra";
            }

            if (rest.StartsWith("unknown menu ", StringComparison.Ordinal))
            {
                return entry + "/menu";
            }

            return entry;
        }

        if (AreaPrefix.Match(message) is { Success: true } am)
        {
            var area = am.Groups[1].Value == "document" ? string.Empty : "/menus/" + Escape(am.Groups[1].Value);
            return area + InArea(am.Groups[2].Value);
        }

        return null;
    }

    /// <summary>The pointer for a System.Text.Json path such as <c>$.menus.main.entries[0].label</c>.</summary>
    public static string? FromJsonPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '$')
        {
            return null;
        }

        var parts = new List<string>();
        foreach (Match m in Regex.Matches(path[1..], @"\.([^.\[\]]+)|\[(\d+)\]|\['([^']*)'\]"))
        {
            parts.Add(Escape(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value));
        }

        return parts.Count == 0 ? string.Empty : "/" + string.Join('/', parts);
    }

    private static string InArea(string rest)
    {
        if (Layer.Match(rest) is { Success: true } lm)
        {
            var layer = "/layout/layers/" + lm.Groups[1].Value;
            var what = lm.Groups[2].Value;
            return what switch
            {
                _ when what.StartsWith("unknown type", StringComparison.Ordinal) => layer + "/type",
                _ when what.StartsWith("unknown fit", StringComparison.Ordinal) => layer + "/fit",
                _ when what.StartsWith("image ", StringComparison.Ordinal) => layer + "/image",
                _ when what.StartsWith("position ", StringComparison.Ordinal) => layer + "/position",
                _ when what.StartsWith("unknown anchor", StringComparison.Ordinal) => layer + "/position/anchor",
                _ when what.StartsWith("opacity", StringComparison.Ordinal) => layer,
                _ => layer,
            };
        }

        var table = new (string Prefix, string Pointer)[]
        {
            ("title position", "/layout/titlePosition"),
            ("title unknown anchor", "/layout/titlePosition/anchor"),
            ("unknown button style", "/layout/buttonStyle"),
            ("transition style", "/layout/transition/style"),
            ("transition durationMs", "/layout/transition/durationMs"),
            ("flow region", "/layout/flow/region"),
            ("flow columns", "/layout/flow"),
            ("flow needs", "/layout/flow"),
            ("flow labels", "/layout/flow"),
            ("theme colour", "/theme"),
            ("unknown font", "/theme/font"),
            ("fontSize", "/theme/fontSize"),
            ("letterSpacing", "/theme/letterSpacing"),
            ("audio music source", "/audio/music/source"),
            ("audio music file", "/audio/music/file"),
            ("audio music volume", "/audio/music/volume"),
            ("audio sounds preset", "/audio/sounds/preset"),
            ("audio sounds volume", "/audio/sounds/volume"),
            ("audio sound ", "/audio/sounds"),
            ("background image", "/background/image"),
            ("background tmdbFilePath", "/background/tmdbFilePath"),
            ("background tmdbSize", "/background/tmdbSize"),
            ("background fanartId", "/background/fanartId"),
            ("background poster", "/background/poster"),
            ("background trailerIndex", "/background/trailerIndex"),
        };
        foreach (var (prefix, pointer) in table)
        {
            if (rest.StartsWith(prefix, StringComparison.Ordinal))
            {
                return pointer;
            }
        }

        return string.Empty;
    }

    private static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
