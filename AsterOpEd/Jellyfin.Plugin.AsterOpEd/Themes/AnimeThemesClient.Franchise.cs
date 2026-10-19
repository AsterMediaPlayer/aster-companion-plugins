using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AsterOpEd.Themes;

// A show's franchise as AnimeThemes groups it ("Sousou no Frieren" and "Sousou no Frieren 2nd Season"), so that the seasons of
// a library without per-season ids can be matched to their own entries.
public sealed partial class AnimeThemesClient
{
    private const string FranchiseQuery = "query($site: ResourceSite!, $ids:[Int!]){ findAnimeByExternalSite(site: $site, id: $ids) { resources { nodes { site externalId } } "
        + "series { nodes { anime { nodes { year season format resources { nodes { site externalId } } } } } } } }";

    /// <summary>One anime of a franchise: its AniDB id, first-aired year and the month its season starts (null when AnimeThemes has no season), and format ("TV", "OVA", ...).</summary>
    public sealed record FranchiseEntry(int AniDb, int Year, int? Month, string? Format);

    /// <summary>
    /// Fetches, in batches, the franchise of every AniDB id whose cached franchise is missing or due: found ones after
    /// <paramref name="refreshDays"/>, empty ones sooner (1, 3, 7, then 30 days), like the themes themselves.
    /// </summary>
    public async Task PrefetchFranchiseAsync(IEnumerable<int> aniDbIds, int refreshDays, CancellationToken ct)
    {
        var stale = aniDbIds.Distinct().Where(id => !File.Exists(FranchiseFile(id)) || File.GetLastWriteTimeUtc(FranchiseFile(id)) < Cutoff(FranchiseMisses(id), refreshDays)).Order().ToList();
        if (stale.Count == 0 || RefusedRecently())
        {
            return;
        }

        _logger.LogInformation("OP/ED: fetching franchises from AnimeThemes for {Count} AniDB entries in {Batches} request(s)", stale.Count, (stale.Count + BatchSize - 1) / BatchSize);
        foreach (var batch in stale.Chunk(BatchSize))
        {
            var body = new JsonObject
            {
                ["query"] = FranchiseQuery,
                ["variables"] = new JsonObject { ["site"] = "ANIDB", ["ids"] = new JsonArray([.. batch.Select(i => (JsonNode)JsonValue.Create(i)!)]) },
            }.ToJsonString();
            if (await PostAsync(body, "findAnimeByExternalSite", ct).ConfigureAwait(false) is not JsonArray animeList)
            {
                if (_refused)
                {
                    return;
                }

                continue;
            }

            var found = batch.ToDictionary(id => id, _ => new List<FranchiseEntry>());
            foreach (var anime in animeList)
            {
                var own = AniDbIds(anime?["resources"]?["nodes"]?.AsArray());
                var siblings = new List<FranchiseEntry>();
                foreach (var series in anime?["series"]?["nodes"]?.AsArray() ?? [])
                {
                    foreach (var sibling in series?["anime"]?["nodes"]?.AsArray() ?? [])
                    {
                        if (AniDbIds(sibling?["resources"]?["nodes"]?.AsArray()).FirstOrDefault() is int id && (int?)sibling?["year"] is int year
                            && !siblings.Exists(s => s.AniDb == id))
                        {
                            siblings.Add(new FranchiseEntry(id, year, MonthOf((string?)sibling?["season"]), (string?)sibling?["format"]));
                        }
                    }
                }

                foreach (var id in own.Where(found.ContainsKey))
                {
                    found[id] = siblings;
                }
            }

            foreach (var (id, entries) in found)
            {
                var o = new JsonObject
                {
                    ["misses"] = entries.Count > 0 ? 0 : FranchiseMisses(id) + 1,
                    ["entries"] = new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject { ["anidb"] = e.AniDb, ["year"] = e.Year, ["month"] = e.Month, ["format"] = e.Format })]),
                };
                await File.WriteAllTextAsync(FranchiseFile(id), o.ToJsonString(), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The cached franchise of an AniDB anime (itself included); null when never fetched, empty when AnimeThemes has no series for it.</summary>
    public List<FranchiseEntry>? GetFranchise(int aniDbId)
    {
        var file = FranchiseFile(aniDbId);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var result = new List<FranchiseEntry>();
            foreach (var e in JsonNode.Parse(File.ReadAllText(file))?["entries"]?.AsArray() ?? [])
            {
                if ((int?)e?["anidb"] is int id && (int?)e?["year"] is int year)
                {
                    result.Add(new FranchiseEntry(id, year, (int?)e?["month"], (string?)e?["format"]));
                }
            }

            return result;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private string FranchiseFile(int aniDbId) => Path.Combine(_cacheDir, $"franchise-{aniDbId}.json");

    private int FranchiseMisses(int aniDbId)
    {
        try
        {
            return File.Exists(FranchiseFile(aniDbId)) ? (int?)JsonNode.Parse(File.ReadAllText(FranchiseFile(aniDbId)))?["misses"] ?? 0 : 0;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return 0;
        }
    }

    private static List<int> AniDbIds(JsonArray? resources)
    {
        var ids = new List<int>();
        foreach (var resource in resources ?? [])
        {
            if (string.Equals((string?)resource?["site"], "ANIDB", StringComparison.OrdinalIgnoreCase) && int.TryParse(resource?["externalId"]?.ToString(), out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static int? MonthOf(string? season) => season?.ToUpperInvariant() switch { "WINTER" => 1, "SPRING" => 4, "SUMMER" => 7, "FALL" => 10, _ => null };
}
