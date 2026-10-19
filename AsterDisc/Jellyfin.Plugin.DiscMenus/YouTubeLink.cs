using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>
/// Extracts a YouTube video id from the kinds of URL Jellyfin stores as a
/// "remote trailer". Only the id is ever sent to the browser (never the raw
/// URL), and only if it has YouTube's exact 11-character shape, so nothing
/// else from item metadata can reach an embed URL.
/// </summary>
public static class YouTubeLink
{
    private static readonly Regex VideoId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com",
        "youtube-nocookie.com", "www.youtube-nocookie.com",
    };

    private static readonly string[] PathPrefixes = { "/embed/", "/shorts/", "/v/", "/live/" };

    public static string? TryGetVideoId(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        string? id = null;
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            id = uri.AbsolutePath.Trim('/').Split('/')[0];
        }
        else if (Hosts.Contains(uri.Host))
        {
            if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
            {
                id = QueryValue(uri.Query, "v");
            }
            else
            {
                foreach (var prefix in PathPrefixes)
                {
                    if (uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        id = uri.AbsolutePath[prefix.Length..].Split('/')[0];
                        break;
                    }
                }
            }
        }

        return id is not null && VideoId.IsMatch(id) ? id : null;
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key)
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }
}
