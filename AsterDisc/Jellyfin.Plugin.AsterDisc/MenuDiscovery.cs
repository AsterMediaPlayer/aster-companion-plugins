namespace Jellyfin.Plugin.AsterDisc;

/// <summary>A library item that might be the title a menu describes, reduced to what matching needs.</summary>
public sealed record DiscoveryCandidate(Guid Id, IReadOnlyDictionary<string, string> ProviderIds);

/// <summary>A season that might be the one an AniDB-matched season menu describes.</summary>
public sealed record SeasonCandidate(Guid Id, int? SeasonNumber, IReadOnlyDictionary<string, string> ProviderIds);

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
/// can be tested exhaustively. Matching uses only provider ids (TMDB / IMDB / TVDB / AniDB): never file
/// names or paths, in keeping with the menu format's rule that menus are shareable.
/// </summary>
public static class MenuDiscovery
{
    /// <summary>The provider-id key Jellyfin and Shokofin use for AniDB, and the one menus use.</summary>
    public const string AniDb = "AniDB";

    /// <summary>A provider id by name, ignoring the name's case; null when absent or empty.</summary>
    public static string? IdOf(IReadOnlyDictionary<string, string>? ids, string provider)
    {
        var value = ids?.FirstOrDefault(p => p.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Whether the ids include a TMDB, IMDB or TVDB id (the ids a menu was matched by before AniDB).</summary>
    public static bool HasStandardId(IReadOnlyDictionary<string, string> ids) =>
        IdOf(ids, "Tmdb") is not null || IdOf(ids, "Imdb") is not null || IdOf(ids, "Tvdb") is not null;

    /// <summary>The ids without AniDB, for matching a season's series (whose AniDB entry is usually not the season's).</summary>
    public static Dictionary<string, string> WithoutAniDb(IReadOnlyDictionary<string, string> ids) =>
        ids.Where(p => !p.Key.Equals(AniDb, StringComparison.OrdinalIgnoreCase)).ToDictionary(p => p.Key, p => p.Value);

    /// <summary>
    /// Chooses the season for a season menu matched by AniDB id. With Shoko/Shokofin every AniDB entry is one season
    /// and season numbers depend on how Shoko groups things, so the season is the one whose OWN AniDB id is the menu's.
    /// The season number only breaks a tie between several such seasons (the same anime in two libraries, say); a
    /// lone match is bound whatever its number.
    /// </summary>
    public static DiscoveryDecision DecideSeasonByAniDb(string aniDbId, int? seasonNumber, IEnumerable<SeasonCandidate> candidates)
    {
        var hits = candidates
            .Where(c => string.Equals(IdOf(c.ProviderIds, AniDb), aniDbId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .ToList();
        if (hits.Count > 1 && seasonNumber is not null)
        {
            var numbered = hits.Where(c => c.SeasonNumber == seasonNumber).ToList();
            if (numbered.Count > 0)
            {
                hits = numbered;
            }
        }

        var ids = hits.Select(c => c.Id).ToList();
        return ids.Count switch
        {
            0 => new DiscoveryDecision(DiscoveryOutcome.Pending, null, Array.Empty<Guid>()),
            1 => new DiscoveryDecision(DiscoveryOutcome.Bound, ids[0], ids),
            _ => new DiscoveryDecision(DiscoveryOutcome.Ambiguous, null, ids),
        };
    }

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
    /// Which bound menu a page shows. An item with its own menu shows that one. A series without one shows its season's
    /// menu when exactly one of its seasons has a menu (a one-season anime under Shokofin, whose menu is matched to the
    /// season by AniDB id, would otherwise have its button only on a season page people rarely open). With menus for
    /// several seasons nothing is guessed. Returns the item whose menu to show, or null for none.
    /// </summary>
    /// <param name="boundSeasons">Every bound menu's item that is a season, with the series it belongs to.</param>
    public static Guid? MenuItemFor(Guid itemId, bool hasOwnMenu, bool isSeries, IEnumerable<(Guid SeasonId, Guid SeriesId)> boundSeasons)
    {
        if (hasOwnMenu)
        {
            return itemId;
        }

        if (!isSeries)
        {
            return null;
        }

        var seasons = boundSeasons.Where(s => s.SeriesId == itemId).Select(s => s.SeasonId).Distinct().Take(2).ToList();
        return seasons.Count == 1 ? seasons[0] : null;
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
