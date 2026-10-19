using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DiscMenus.Fanart;

/// <summary>Which fanart.tv catalogue a title is in: movies are looked up by TMDB id, shows by TVDB id.</summary>
public enum FanartKind
{
    Movie,
    Show,
}

/// <summary>One image from fanart.tv's listing for a title.</summary>
/// <param name="Id">fanart.tv's numeric image id: what a menu's <c>fanartId</c> names.</param>
/// <param name="Category">The listing's group, such as "moviebackground", "hdmovielogo" or "showbackground".</param>
/// <param name="Url">An https URL on fanart.tv's image host (validated).</param>
public sealed record FanartImage(string Id, string Category, string Url, string? Lang, int Likes);

public enum FanartStatus
{
    Ok,

    /// <summary>No API key is configured.</summary>
    NoKey,

    /// <summary>fanart.tv rejected the key.</summary>
    BadKey,

    /// <summary>Nothing for this title or image.</summary>
    NotFound,

    /// <summary>fanart.tv asked us to slow down.</summary>
    RateLimited,

    /// <summary>Network trouble, a server error, or a response that wasn't what it should be.</summary>
    Unavailable,
}

public sealed record FanartLookup(FanartStatus Status, IReadOnlyList<FanartImage> Images);

/// <param name="Path">A file in the cache, to stream.</param>
public sealed record FanartImageResult(FanartStatus Status, string? Path = null, string? ContentType = null);

/// <summary>The fanart.tv title a library item corresponds to.</summary>
public sealed record FanartTarget(FanartKind Kind, string ProviderId)
{
    private static readonly Regex Digits = new("^[0-9]{1,12}$", RegexOptions.Compiled);

    /// <summary>
    /// Movies use their TMDB id, series their TVDB id, and a season uses its series'. Anything without a usable
    /// (all digits) id has no fanart.tv page we can ask for.
    /// </summary>
    public static FanartTarget? For(string itemType, IReadOnlyDictionary<string, string>? ids, IReadOnlyDictionary<string, string>? seriesIds)
    {
        static string? Find(IReadOnlyDictionary<string, string>? d, string name) =>
            d?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

        var (kind, id) = itemType switch
        {
            "Movie" => (FanartKind.Movie, Find(ids, "Tmdb")),
            "Series" => (FanartKind.Show, Find(ids, "Tvdb")),
            "Season" => (FanartKind.Show, Find(seriesIds, "Tvdb")),
            _ => (FanartKind.Movie, null),
        };
        return id is not null && Digits.IsMatch(id) ? new FanartTarget(kind, id) : null;
    }
}

/// <summary>
/// Reads fanart.tv's v3 listing and keeps only what is safe to act on. The listing comes from a third party, so every
/// URL in it is checked: https, fanart.tv's own image host, an image file name, no credentials, no query.
/// </summary>
public static class FanartParser
{
    public const int MaxImages = 2000;

    /// <summary>The only host images are fetched from.</summary>
    public const string ImageHost = "assets.fanart.tv";

    private static readonly Regex Category = new("^[a-z0-9_]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex Id = new("^[0-9]{1,12}$", RegexOptions.Compiled);
    private static readonly Regex Lang = new("^[A-Za-z]{2,3}$", RegexOptions.Compiled);

    public static IReadOnlyList<FanartImage> ParseListing(string json)
    {
        var result = new List<FanartImage>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var group in doc.RootElement.EnumerateObject())
        {
            // "name", "tmdb_id" and the like are plain values; the images are the arrays of objects.
            if (!Category.IsMatch(group.Name) || group.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var e in group.Value.EnumerateArray())
            {
                if (result.Count >= MaxImages)
                {
                    return result;
                }

                if (e.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = Text(e, "id");
                if (id is null || !Id.IsMatch(id) || !TryNormalizeUrl(Text(e, "url"), out var url))
                {
                    continue;
                }

                var lang = Text(e, "lang");
                var likes = int.TryParse(Text(e, "likes"), out var l) && l >= 0 ? l : 0;
                result.Add(new FanartImage(id, group.Name, url, lang is not null && Lang.IsMatch(lang) ? lang : null, likes));
            }
        }

        return result;
    }

    /// <summary>Accepts only an image on fanart.tv's image host and returns it as https.</summary>
    public static bool TryNormalizeUrl(string? url, out string safe)
    {
        safe = string.Empty;
        // Checked on the raw text: Uri would quietly collapse "/a/../b" and "%2e%2e" before a path check could see them.
        if (string.IsNullOrEmpty(url) || url.Length > 400 || url.Contains("..", StringComparison.Ordinal) || url.Contains('%')
            || url.Contains('\\') || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if ((uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.Equals(uri.Host, ImageHost, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !uri.IsDefaultPort)
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (ContentTypeFor(path) is null)
        {
            return false;
        }

        safe = "https://" + ImageHost + path;
        return true;
    }

    /// <summary>The content type for a file name this fetches, or null if it isn't one.</summary>
    public static string? ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => null,
    };

    /// <summary>Does this look like the kind of picture it claims to be (not an error page saved under an image name)?</summary>
    public static bool LooksLike(string contentType, ReadOnlySpan<byte> head) => contentType switch
    {
        "image/jpeg" => head.Length > 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
        "image/png" => head.Length > 7 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47,
        "image/webp" => head.Length > 11 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
            && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P',
        _ => false,
    };

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;
}
