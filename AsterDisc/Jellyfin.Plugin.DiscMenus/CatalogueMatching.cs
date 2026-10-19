namespace Jellyfin.Plugin.DiscMenus;

/// <summary>A movie or series in the library, reduced to what matching needs.</summary>
public sealed record LibraryTitle(Guid Id, string Name, int? Year, bool IsMovie, IReadOnlyDictionary<string, string> ProviderIds);

/// <param name="ItemType">"Movie", "Series" or "Season" (a season menu is matched by its series' ids).</param>
public sealed record CatalogueMatchQuery(string MenuId, string ItemType, IReadOnlyDictionary<string, string> ProviderIds);

/// <param name="State">"bound" (exactly one title), "ambiguous" (several fit equally) or "none".</param>
public sealed record CatalogueMatchResult(string MenuId, string State, IReadOnlyList<LibraryTitle> Titles);

/// <summary>
/// Which catalogue menus are for titles in this library, using the same rule as automatic discovery
/// (<see cref="MenuDiscovery.Decide"/>): provider ids only, never names. The catalogue's list comes to the server, so the
/// library itself never leaves it.
/// </summary>
public static class CatalogueMatching
{
    public static IReadOnlyList<CatalogueMatchResult> Match(IReadOnlyList<CatalogueMatchQuery> queries, IReadOnlyList<LibraryTitle> library)
    {
        // Index by (provider, value) so a big catalogue against a big library stays cheap.
        var byId = new Dictionary<(string, string), List<LibraryTitle>>();
        foreach (var title in library)
        {
            foreach (var (provider, value) in title.ProviderIds)
            {
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                var key = (provider.ToLowerInvariant(), value.ToLowerInvariant());
                if (!byId.TryGetValue(key, out var list))
                {
                    byId[key] = list = new List<LibraryTitle>();
                }

                list.Add(title);
            }
        }

        var results = new List<CatalogueMatchResult>(queries.Count);
        foreach (var query in queries)
        {
            var wantMovie = string.Equals(query.ItemType, "Movie", StringComparison.Ordinal);
            var found = new Dictionary<Guid, LibraryTitle>();
            foreach (var (provider, value) in query.ProviderIds)
            {
                if (byId.TryGetValue((provider.ToLowerInvariant(), value.ToLowerInvariant()), out var list))
                {
                    foreach (var title in list.Where(t => t.IsMovie == wantMovie))
                    {
                        found[title.Id] = title;
                    }
                }
            }

            var decision = MenuDiscovery.Decide(query.ProviderIds, found.Values.Select(t => new DiscoveryCandidate(t.Id, t.ProviderIds)));
            var titles = decision.Candidates.Select(id => found[id]).ToList();
            results.Add(new CatalogueMatchResult(
                query.MenuId,
                decision.Outcome switch
                {
                    DiscoveryOutcome.Bound => "bound",
                    DiscoveryOutcome.Ambiguous => "ambiguous",
                    _ => "none",
                },
                titles));
        }

        return results;
    }
}
