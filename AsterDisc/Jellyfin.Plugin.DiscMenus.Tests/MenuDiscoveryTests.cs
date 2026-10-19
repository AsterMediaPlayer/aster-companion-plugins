using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

public class MenuDiscoveryTests
{
    private static DiscoveryCandidate Item(string id, params (string Provider, string Value)[] ids) =>
        new(Guid.Parse(id), ids.ToDictionary(i => i.Provider, i => i.Value));

    private static readonly string A = "aaaaaaaa-0000-0000-0000-000000000001";
    private static readonly string B = "bbbbbbbb-0000-0000-0000-000000000002";
    private static readonly string C = "cccccccc-0000-0000-0000-000000000003";

    private static IReadOnlyDictionary<string, string> Wanted(params (string Provider, string Value)[] ids) =>
        ids.ToDictionary(i => i.Provider, i => i.Value);

    [Fact]
    public void SingleItemWithMatchingIdIsBound()
    {
        var d = MenuDiscovery.Decide(Wanted(("Tmdb", "284053")), new[] { Item(A, ("Tmdb", "284053")), Item(B, ("Tmdb", "603")) });
        Assert.Equal(DiscoveryOutcome.Bound, d.Outcome);
        Assert.Equal(Guid.Parse(A), d.ItemId);
    }

    [Fact]
    public void NothingMatchingIsPending()
    {
        var d = MenuDiscovery.Decide(Wanted(("Tmdb", "284053")), new[] { Item(B, ("Tmdb", "603")) });
        Assert.Equal(DiscoveryOutcome.Pending, d.Outcome);
        Assert.Null(d.ItemId);
        Assert.Empty(d.Candidates);
    }

    [Fact]
    public void NoCandidatesAndNoWantedIdsArePending()
    {
        Assert.Equal(DiscoveryOutcome.Pending, MenuDiscovery.Decide(Wanted(("Tmdb", "1")), Array.Empty<DiscoveryCandidate>()).Outcome);
        Assert.Equal(DiscoveryOutcome.Pending, MenuDiscovery.Decide(Wanted(), new[] { Item(A, ("Tmdb", "1")) }).Outcome);
    }

    [Fact]
    public void TwoEquallyGoodItemsAreAmbiguousNotGuessed()
    {
        var d = MenuDiscovery.Decide(Wanted(("Tmdb", "284053")), new[] { Item(A, ("Tmdb", "284053")), Item(B, ("Tmdb", "284053")) });
        Assert.Equal(DiscoveryOutcome.Ambiguous, d.Outcome);
        Assert.Null(d.ItemId);
        Assert.Equal(2, d.Candidates.Count);
        Assert.Contains(Guid.Parse(A), d.Candidates);
        Assert.Contains(Guid.Parse(B), d.Candidates);
    }

    [Fact]
    public void ItemMatchingMoreOfTheIdsBeatsOneMatchingFewer()
    {
        var d = MenuDiscovery.Decide(
            Wanted(("Tmdb", "284053"), ("Imdb", "tt3501632")),
            new[] { Item(A, ("Tmdb", "284053")), Item(B, ("Tmdb", "284053"), ("Imdb", "tt3501632")) });
        Assert.Equal(DiscoveryOutcome.Bound, d.Outcome);
        Assert.Equal(Guid.Parse(B), d.ItemId);
    }

    [Fact]
    public void ContradictingIdDisqualifiesEvenIfAnotherIdMatches()
    {
        // Same IMDB id but a different TMDB id: a different title, not a match.
        var d = MenuDiscovery.Decide(
            Wanted(("Tmdb", "284053"), ("Imdb", "tt3501632")),
            new[] { Item(A, ("Tmdb", "999"), ("Imdb", "tt3501632")) });
        Assert.Equal(DiscoveryOutcome.Pending, d.Outcome);
    }

    [Fact]
    public void MissingIdsNeitherSupportNorContradict()
    {
        // Item only knows its TMDB id; the menu also lists an IMDB id the item lacks: still a match.
        var d = MenuDiscovery.Decide(Wanted(("Tmdb", "284053"), ("Imdb", "tt3501632")), new[] { Item(A, ("Tmdb", "284053")) });
        Assert.Equal(DiscoveryOutcome.Bound, d.Outcome);
        // An item with no provider ids at all can never match.
        Assert.Equal(DiscoveryOutcome.Pending, MenuDiscovery.Decide(Wanted(("Tmdb", "1")), new[] { Item(A) }).Outcome);
        // Empty-string ids are treated as absent.
        Assert.Equal(DiscoveryOutcome.Pending, MenuDiscovery.Decide(Wanted(("Tmdb", "1")), new[] { Item(A, ("Tmdb", "")) }).Outcome);
    }

    [Fact]
    public void ProviderNamesAndIdsCompareCaseInsensitively()
    {
        var d = MenuDiscovery.Decide(Wanted(("Imdb", "tt3501632")), new[] { Item(A, ("IMDB", "TT3501632")) });
        Assert.Equal(DiscoveryOutcome.Bound, d.Outcome);
    }

    [Fact]
    public void DuplicateCandidateIdsDoNotCreateFalseAmbiguity()
    {
        var d = MenuDiscovery.Decide(Wanted(("Tmdb", "1")), new[] { Item(A, ("Tmdb", "1")), Item(A, ("Tmdb", "1")) });
        Assert.Equal(DiscoveryOutcome.Bound, d.Outcome);
    }

    [Fact]
    public void DecisionDoesNotDependOnCandidateOrder()
    {
        var items = new[] { Item(A, ("Tmdb", "1")), Item(B, ("Tmdb", "1"), ("Imdb", "tt1")), Item(C, ("Tmdb", "2")) };
        var wanted = Wanted(("Tmdb", "1"), ("Imdb", "tt1"));
        Assert.Equal(Guid.Parse(B), MenuDiscovery.Decide(wanted, items).ItemId);
        Assert.Equal(Guid.Parse(B), MenuDiscovery.Decide(wanted, items.Reverse()).ItemId);
    }

    private static BoundEntry Entry(string path, bool isExplicit, int revision, string parent) =>
        new(path, Guid.NewGuid(), revision, isExplicit, Guid.Parse(parent));

    [Fact]
    public void MenusForDifferentItemsDoNotConflict()
    {
        var (winners, losers) = MenuDiscovery.ResolveConflicts(new[] { Entry("a.menu.json", false, 1, A), Entry("b.menu.json", false, 1, B) });
        Assert.Equal(2, winners.Count);
        Assert.Empty(losers);
    }

    [Fact]
    public void HandWrittenBindingBeatsAutomaticEvenIfOlder()
    {
        var (winners, losers) = MenuDiscovery.ResolveConflicts(new[] { Entry("auto.menu.json", false, 9, A), Entry("hand.menu.json", true, 1, A) });
        Assert.Equal("hand.menu.json", Assert.Single(winners).MenuPath);
        Assert.Equal("hand.menu.json", losers["auto.menu.json"]);
    }

    [Fact]
    public void NewerRevisionWinsThenPathOrderBreaksTies()
    {
        var (w1, _) = MenuDiscovery.ResolveConflicts(new[] { Entry("a.menu.json", false, 1, A), Entry("b.menu.json", false, 2, A) });
        Assert.Equal("b.menu.json", Assert.Single(w1).MenuPath);
        var (w2, l2) = MenuDiscovery.ResolveConflicts(new[] { Entry("z.menu.json", false, 3, A), Entry("m.menu.json", false, 3, A) });
        Assert.Equal("m.menu.json", Assert.Single(w2).MenuPath);
        Assert.Equal("m.menu.json", l2["z.menu.json"]);
    }

    [Fact]
    public void ConflictResolutionIsIndependentOfInputOrder()
    {
        var entries = new[]
        {
            Entry("c.menu.json", false, 2, A), Entry("a.menu.json", false, 2, A), Entry("b.menu.json", true, 1, A), Entry("d.menu.json", false, 5, B),
        };
        var (w1, l1) = MenuDiscovery.ResolveConflicts(entries);
        var (w2, l2) = MenuDiscovery.ResolveConflicts(entries.Reverse());
        Assert.Equal(w1.Select(w => w.MenuPath).OrderBy(x => x), w2.Select(w => w.MenuPath).OrderBy(x => x));
        Assert.Equal(l1.OrderBy(k => k.Key), l2.OrderBy(k => k.Key));
        Assert.Contains(w1, w => w.MenuPath == "b.menu.json");
        Assert.Equal(4, l1.Count + w1.Count);
    }
}
