using Xunit;

namespace Jellyfin.Plugin.AsterDisc.Tests;

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

    // ---- AniDB (Shoko/Shokofin). Real ids from a Shokofin library: "Bad Girl" (2025) is AniDB 18716, Shoko series 66.
    private static readonly (string, string)[] ShokofinBadGirl = { ("AniDB", "18716"), ("Shoko Series", "66"), ("Shoko Group", "122"), ("Custom", "shoko://series/66") };

    [Fact]
    public void AnAniDbOnlyTitleIsBoundByItsAniDbId()
    {
        var d = MenuDiscovery.Decide(Wanted(("AniDB", "18716")), new[] { Item(A, ShokofinBadGirl), Item(B, ("AniDB", "17330")), Item(C, ("Tmdb", "18716")) });
        Assert.Equal(DiscoveryOutcome.Bound, d.Outcome);
        Assert.Equal(Guid.Parse(A), d.ItemId);
    }

    [Fact]
    public void AniDbKeyCaseDoesNotMatter()
    {
        Assert.Equal(Guid.Parse(A), MenuDiscovery.Decide(Wanted(("AniDB", "18716")), new[] { Item(A, ("anidb", "18716")) }).ItemId);
        Assert.Equal(Guid.Parse(A), MenuDiscovery.Decide(Wanted(("anidb", "18716")), new[] { Item(A, ("ANIDB", "18716")) }).ItemId);
    }

    [Fact]
    public void TwoTitlesWithTheSameAniDbIdAreAmbiguous()
    {
        var d = MenuDiscovery.Decide(Wanted(("AniDB", "18716")), new[] { Item(A, ShokofinBadGirl), Item(B, ("AniDB", "18716")) });
        Assert.Equal(DiscoveryOutcome.Ambiguous, d.Outcome);
        Assert.Equal(2, d.Candidates.Count);
    }

    [Fact]
    public void AniDbFollowsTheUsualScoringAndContradictionRules()
    {
        // A different AniDB id is a different title even though the TVDB id agrees...
        var contradicted = MenuDiscovery.Decide(Wanted(("AniDB", "18716"), ("Tvdb", "1")), new[] { Item(A, ("AniDB", "99"), ("Tvdb", "1")) });
        Assert.Equal(DiscoveryOutcome.Pending, contradicted.Outcome);

        // ...an item with only one of the ids still fits, and the one with both wins.
        var d = MenuDiscovery.Decide(Wanted(("AniDB", "18716"), ("Tvdb", "1")), new[] { Item(A, ShokofinBadGirl), Item(B, ("AniDB", "18716"), ("Tvdb", "1")) });
        Assert.Equal(Guid.Parse(B), d.ItemId);
    }

    private static SeasonCandidate Season(string id, int? number, params (string Provider, string Value)[] ids) =>
        new(Guid.Parse(id), number, ids.ToDictionary(i => i.Provider, i => i.Value));

    [Fact]
    public void AnAniDbSeasonIsFoundByItsOwnAniDbId()
    {
        // One Shokofin series made of several AniDB entries, each its own season (ids illustrative).
        var seasons = new[] { Season(A, 1, ("AniDB", "14107")), Season(B, 2, ("AniDB", "16030")), Season(C, 3, ("AniDB", "17123")) };
        var d = MenuDiscovery.DecideSeasonByAniDb("16030", null, seasons);
        Assert.Equal(DiscoveryOutcome.Bound, d.Outcome);
        Assert.Equal(Guid.Parse(B), d.ItemId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void AnAniDbSeasonMatchIgnoresTheSeasonNumberWhenThereIsOnlyOne(int? number)
    {
        // Shoko can regroup and renumber seasons; the AniDB id is what identifies it.
        var d = MenuDiscovery.DecideSeasonByAniDb("16030", number, new[] { Season(A, 1, ("AniDB", "14107")), Season(B, 2, ("AniDB", "16030")) });
        Assert.Equal(DiscoveryOutcome.Bound, d.Outcome);
        Assert.Equal(Guid.Parse(B), d.ItemId);
    }

    [Fact]
    public void TheSeasonNumberBreaksATieBetweenSeasonsWithTheSameAniDbId()
    {
        var seasons = new[] { Season(A, 1, ("AniDB", "18716")), Season(B, 3, ("AniDB", "18716")) };
        Assert.Equal(Guid.Parse(B), MenuDiscovery.DecideSeasonByAniDb("18716", 3, seasons).ItemId);

        var noNumber = MenuDiscovery.DecideSeasonByAniDb("18716", null, seasons);
        Assert.Equal(DiscoveryOutcome.Ambiguous, noNumber.Outcome);
        Assert.Equal(2, noNumber.Candidates.Count);

        // A number that fits neither doesn't rule them both out: still a person's choice.
        Assert.Equal(DiscoveryOutcome.Ambiguous, MenuDiscovery.DecideSeasonByAniDb("18716", 9, seasons).Outcome);
    }

    [Fact]
    public void AnAniDbSeasonWithNoSuchSeasonIsPending()
    {
        // The specials season (0) has no provider ids at all under Shokofin.
        var seasons = new[] { Season(A, 0), Season(B, 1, ("AniDB", "14107")), Season(C, 2, ("Tmdb", "16030")) };
        var d = MenuDiscovery.DecideSeasonByAniDb("16030", 2, seasons);
        Assert.Equal(DiscoveryOutcome.Pending, d.Outcome);
        Assert.Empty(d.Candidates);
    }

    [Fact]
    public void AnAniDbSeasonMatchToleratesKeyCaseAndDuplicates()
    {
        var d = MenuDiscovery.DecideSeasonByAniDb("16030", null, new[] { Season(A, 2, ("anidb", "16030")), Season(A, 2, ("AniDB", "16030")) });
        Assert.Equal(DiscoveryOutcome.Bound, d.Outcome);
        Assert.Equal(Guid.Parse(A), d.ItemId);
    }

    [Fact]
    public void IdHelpers()
    {
        Assert.Equal("1", MenuDiscovery.IdOf(Wanted(("anidb", "1")), "AniDB"));
        Assert.Null(MenuDiscovery.IdOf(Wanted(("AniDB", "")), "AniDB"));
        Assert.Null(MenuDiscovery.IdOf(null, "AniDB"));
        Assert.False(MenuDiscovery.HasStandardId(Wanted(ShokofinBadGirl)));
        Assert.True(MenuDiscovery.HasStandardId(Wanted(("AniDB", "1"), ("tvdb", "2"))));
        Assert.Equal(new[] { "Tvdb" }, MenuDiscovery.WithoutAniDb(Wanted(("anidb", "1"), ("Tvdb", "2"))).Keys);
    }

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
