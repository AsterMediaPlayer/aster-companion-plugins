using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpEd.Themes;

// Finding an anime by its title, for shows that have no id AnimeThemes knows.
public sealed partial class AnimeThemesClient
{
    // Several searches in one request: each is a field of its own (s0, s1, ...), so the load is one request per SearchBatch shows.
    private const int SearchBatch = 10;
    private const string SearchFields = "anime { year title { romaji english native } synonyms { text } resources { nodes { site externalId } } }";

    /// <summary>A show to look for by title: its name, other names it goes by (folder name, original title) and first-aired year.</summary>
    public sealed record TitleQuery(Guid SeriesId, string Name, IReadOnlyList<string> Alternates, int? Year)
    {
        public string Key => $"{Normalize(Name)}|{Year}";
    }

    private sealed record TitleMatch(int? AniDb, int Score, string? Title, DateTime At, int Misses);

    private Dictionary<string, TitleMatch>? _titleMatches;

    private string TitleMatchFile => Path.Combine(_cacheDir, "title-matches.json");

    /// <summary>
    /// Searches AnimeThemes for the shows, ten to a request (never in parallel, with the usual gap), and remembers the best
    /// match and how alike it was, so changing the threshold doesn't need another request. Found matches are re-checked after
    /// <paramref name="refreshDays"/>; shows with no match are asked again after 1, 3 and 7 days, then every 30.
    /// </summary>
    public async Task MatchTitlesAsync(IEnumerable<TitleQuery> queries, int threshold, int refreshDays, CancellationToken ct)
    {
        var known = LoadTitleMatches();
        var stale = queries.Where(q => !known.TryGetValue(q.Key, out var m) || m.At < Cutoff(m.Misses, refreshDays)).ToList();
        if (stale.Count == 0 || RefusedRecently())
        {
            return;
        }

        _logger.LogInformation("OP/ED: searching AnimeThemes by title for {Count} show(s) that have no usable id, {Batch} to a request", stale.Count, SearchBatch);
        foreach (var batch in stale.Chunk(SearchBatch))
        {
            var data = await PostAsync(SearchBody(batch), string.Empty, ct).ConfigureAwait(false) as JsonObject;
            if (data is null)
            {
                if (_refused)
                {
                    return;
                }

                continue; // network trouble: retried on the next scan
            }

            for (var i = 0; i < batch.Length; i++)
            {
                var query = batch[i];
                var animeList = data[$"s{i}"]?["anime"] as JsonArray ?? [];
                var best = Best(query, animeList);
                var previous = known.TryGetValue(query.Key, out var before) ? before.Misses : 0;
                var matched = best is not null && best.Value.Score >= threshold;
                known[query.Key] = new TitleMatch(best?.AniDb, best?.Score ?? 0, best?.Title, DateTime.UtcNow, matched ? 0 : previous + 1);
                if (best is { } b)
                {
                    _logger.LogInformation(
                        "OP/ED: title search for {Name}: best match {Title} (AniDB {AniDb}) at {Score}%, {Verdict}",
                        query.Name, b.Title, b.AniDb, b.Score, matched ? "accepted" : $"below the {threshold}% threshold");
                }
                else
                {
                    _logger.LogInformation("OP/ED: title search for {Name} found nothing usable", query.Name);
                }
            }

            SaveTitleMatches(known);
        }
    }

    /// <summary>The AniDB id found for the show's title, when it was alike enough (at least <paramref name="threshold"/> percent).</summary>
    public int? GetTitleMatch(TitleQuery query, int threshold) =>
        LoadTitleMatches().TryGetValue(query.Key, out var m) && m.AniDb is int id && m.Score >= threshold ? id : null;

    /// <summary>How alike the title was, for showing why a match was or wasn't made; null when never searched.</summary>
    public (int Score, string? Title)? DescribeTitleMatch(TitleQuery query) =>
        LoadTitleMatches().TryGetValue(query.Key, out var m) ? (m.Score, m.Title) : null;

    private static (int AniDb, int Score, string Title, JsonNode Anime)? Best(TitleQuery query, JsonArray animeList)
    {
        var wanted = new[] { query.Name }.Concat(query.Alternates).Where(n => n.Length > 0).Select(Normalize).Where(n => n.Length > 0).Distinct().ToList();
        (int AniDb, int Score, string Title, JsonNode Anime)? best = null;
        var bestYear = int.MaxValue;
        foreach (var anime in animeList)
        {
            if (anime is null)
            {
                continue;
            }

            int? year = (int?)anime["year"];
            // Same title, different decade: a remake or a namesake. A year off by one is normal (late-year premieres).
            if (query.Year is int y && year is int ay && Math.Abs(y - ay) > 1)
            {
                continue;
            }

            int? aniDb = null;
            foreach (var resource in anime["resources"]?["nodes"]?.AsArray() ?? [])
            {
                if (string.Equals((string?)resource?["site"], "ANIDB", StringComparison.OrdinalIgnoreCase) && int.TryParse(resource?["externalId"]?.ToString(), out var id))
                {
                    aniDb = id;
                    break;
                }
            }

            if (aniDb is null)
            {
                continue;
            }

            var names = new List<string>();
            foreach (var key in new[] { "romaji", "english", "native" })
            {
                if ((string?)anime["title"]?[key] is { Length: > 0 } t)
                {
                    names.Add(t);
                }
            }

            foreach (var s in anime["synonyms"]?.AsArray() ?? [])
            {
                if ((string?)s?["text"] is { Length: > 0 } t)
                {
                    names.Add(t);
                }
            }

            var score = 0;
            foreach (var name in names)
            {
                var n = Normalize(name);
                foreach (var w in wanted)
                {
                    score = Math.Max(score, Similarity(w, n));
                }
            }

            var label = (string?)anime["title"]?["romaji"] ?? (string?)anime["title"]?["english"] ?? names.FirstOrDefault() ?? string.Empty;
            // Highest score wins; for equal scores the earlier anime (a show's first season).
            if (best is null || score > best.Value.Score || (score == best.Value.Score && (year ?? int.MaxValue) < bestYear))
            {
                best = (aniDb.Value, score, label, anime);
                bestYear = year ?? int.MaxValue;
            }
        }

        return best;
    }

    private static string SearchBody(TitleQuery[] batch)
    {
        var declared = string.Join(", ", batch.Select((_, i) => $"$q{i}: String!"));
        var fields = string.Join(" ", batch.Select((_, i) => $"s{i}: search(search: $q{i}, first: 5) {{ {SearchFields} }}"));
        var variables = new JsonObject();
        for (var i = 0; i < batch.Length; i++)
        {
            variables[$"q{i}"] = batch[i].Name;
        }

        return new JsonObject { ["query"] = $"query({declared}) {{ {fields} }}", ["variables"] = variables }.ToJsonString();
    }

    /// <summary>Lower case, no accents, apostrophes dropped, everything else that isn't a letter or digit a single space; a trailing "(2023)" is removed.</summary>
    internal static string Normalize(string title)
    {
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = true;
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark || c is '\'' or '’' or '‘' or '`')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }

        var result = sb.ToString().TrimEnd();
        // "frieren beyond journeys end 2023" from "... (2023)": the year is compared separately.
        if (result.Length > 5 && int.TryParse(result[^4..], out var y) && y is >= 1950 and <= 2100 && result[^5] == ' ' && title.TrimEnd().EndsWith(')'))
        {
            result = result[..^5].TrimEnd();
        }

        return result;
    }

    /// <summary>Percent alike (0 to 100) of two normalized titles: one minus the edit distance over the longer length.</summary>
    internal static int Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a == b)
        {
            return 100;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return (int)Math.Round(100.0 * (1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length)));
    }

    private Dictionary<string, TitleMatch> LoadTitleMatches()
    {
        if (_titleMatches is not null)
        {
            return _titleMatches;
        }

        _titleMatches = [];
        try
        {
            if (File.Exists(TitleMatchFile) && JsonNode.Parse(File.ReadAllText(TitleMatchFile)) is JsonObject o)
            {
                foreach (var (key, v) in o)
                {
                    if (DateTime.TryParse((string?)v?["at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                    {
                        _titleMatches[key] = new TitleMatch((int?)v?["anidb"], (int?)v?["score"] ?? 0, (string?)v?["title"], at, (int?)v?["misses"] ?? 0);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning("OP/ED: ignoring unreadable {File}: {Message}", TitleMatchFile, e.Message);
        }

        return _titleMatches;
    }

    private void SaveTitleMatches(Dictionary<string, TitleMatch> matches)
    {
        var o = new JsonObject();
        foreach (var (key, m) in matches)
        {
            o[key] = new JsonObject
            {
                ["anidb"] = m.AniDb,
                ["score"] = m.Score,
                ["title"] = m.Title,
                ["misses"] = m.Misses,
                ["at"] = m.At.ToString("o", CultureInfo.InvariantCulture),
            };
        }

        File.WriteAllText(TitleMatchFile, o.ToJsonString());
    }
}
