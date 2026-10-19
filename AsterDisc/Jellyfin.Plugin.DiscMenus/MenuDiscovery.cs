namespace Jellyfin.Plugin.DiscMenus;

/// <summary>A library item that might be the title a menu describes, reduced to what matching needs.</summary>
public sealed record DiscoveryCandidate(Guid Id, IReadOnlyDictionary<string, string> ProviderIds);

public enum DiscoveryOutcome
{
    /// <summary>Exactly one library item fits: bind to it.</summary>
    Bound,

    /// <summary>Nothing in the library fits (yet).</summary>
    Pending,

    /// <summary>More than one item fits equally well: needs a person to choose.</summary>
    Ambiguous,
}

public sealed record DiscoveryDecision(DiscoveryOutcome Outcome, Guid? ItemId, IReadOnlyList<Guid> Candidates);

/// <summary>One menu that wants a particular library item, for resolving two menus claiming the same item.</summary>
public sealed record BoundEntry(string MenuPath, Guid MenuId, int Revision, bool Explicit, Guid ParentItemId);

/// <summary>
/// The pure decisions behind automatic menu discovery, kept free of any Jellyfin types so they
/// can be tested exhaustively. Matching uses only provider ids (TMDB / IMDB / TVDB): never file
/// names or paths, in keeping with the menu format's rule that menus are shareable.
/// </summary>
public static class MenuDiscovery
{
    /// <summary>
    /// Chooses among library items for a menu's provider ids. An item counts only if at least one
    /// wanted id matches and none contradicts (an item that has a TMDB id different from the
    /// menu's is a different title, even if its IMDB id happens to match). Items matching more of
    /// the wanted ids win; a tie at the top is ambiguous rather than guessed.
    /// </summary>
    public static DiscoveryDecision Decide(IReadOnlyDictionary<string, string> wanted, IEnumerable<DiscoveryCandidate> candidates)
    {
        var scored = new List<(Guid Id, int Hits)>();
        foreach (var candidate in candidates)
        {
            var hits = 0;
            var contradicted = false;
            foreach (var (provider, value) in wanted)
            {
                var have = candidate.ProviderIds.FirstOrDefault(p => p.Key.Equals(provider, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(have.Value))
                {
                    continue; // item has no such id: neither support nor contradiction
                }

                if (string.Equals(have.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    hits++;
                }
                else
                {
                    contradicted = true;
                }
            }

            if (hits > 0 && !contradicted)
            {
                scored.Add((candidate.Id, hits));
            }
        }

        if (scored.Count == 0)
        {
            return new DiscoveryDecision(DiscoveryOutcome.Pending, null, Array.Empty<Guid>());
        }

        var best = scored.Max(s => s.Hits);
        var top = scored.Where(s => s.Hits == best).Select(s => s.Id).Distinct().ToList();
        return top.Count == 1
            ? new DiscoveryDecision(DiscoveryOutcome.Bound, top[0], top)
            : new DiscoveryDecision(DiscoveryOutcome.Ambiguous, null, top);
    }

    /// <summary>
    /// When several menus resolve to the same library item only one can be shown. A binding the
    /// user wrote by hand beats an automatic one; then the newer menu revision; then file path
    /// order, so the result is deterministic. Returns the winner per item and, for each loser,
    /// the path of the menu that beat it.
    /// </summary>
    public static (IReadOnlyList<BoundEntry> Winners, IReadOnlyDictionary<string, string> LoserToWinner) ResolveConflicts(
        IEnumerable<BoundEntry> entries)
    {
        var winners = new List<BoundEntry>();
        var losers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(e => e.ParentItemId))
        {
            var ordered = group
                .OrderByDescending(e => e.Explicit)
                .ThenByDescending(e => e.Revision)
                .ThenBy(e => e.MenuPath, StringComparer.Ordinal)
                .ToList();
            winners.Add(ordered[0]);
            foreach (var loser in ordered.Skip(1))
            {
                losers[loser.MenuPath] = ordered[0].MenuPath;
            }
        }

        return (winners, losers);
    }
}
