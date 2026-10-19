using Jellyfin.Plugin.DiscMenus;
using Jellyfin.Plugin.DiscMenus.Model;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DiscMenus.Tests;

public class DurationMatcherTests
{
    private static ExtraSpec Spec(ExtraType type, double durationSec, double? toleranceSec = null, int? ordinal = null) => new()
    {
        Type = type,
        DurationSec = durationSec,
        ToleranceSec = toleranceSec,
        Ordinal = ordinal,
    };

    [Fact]
    public void UniqueDurationWithinToleranceMatches()
    {
        var extras = new Dictionary<string, ExtraSpec> { ["trailer"] = Spec(ExtraType.Trailer, 148, toleranceSec: 3) };
        var local = new List<LocalExtraCandidate> { new(Guid.NewGuid(), ExtraType.Trailer, 148.5) };

        var result = DurationMatcher.Match(extras, local);

        Assert.Equal(BindingStatus.Matched, result["trailer"].Status);
        Assert.Equal(BindingMethod.Duration, result["trailer"].Method);
        Assert.Equal(local[0].ItemId, result["trailer"].ItemId);
    }

    [Fact]
    public void OutsideToleranceIsUnmatched()
    {
        var extras = new Dictionary<string, ExtraSpec> { ["trailer"] = Spec(ExtraType.Trailer, 148, toleranceSec: 3) };
        var local = new List<LocalExtraCandidate> { new(Guid.NewGuid(), ExtraType.Trailer, 200) };

        var result = DurationMatcher.Match(extras, local);

        Assert.Equal(BindingStatus.Unmatched, result["trailer"].Status);
    }

    [Fact]
    public void DifferentExtraTypeIsNeverMatched()
    {
        var extras = new Dictionary<string, ExtraSpec> { ["trailer"] = Spec(ExtraType.Trailer, 148, toleranceSec: 3) };
        var local = new List<LocalExtraCandidate> { new(Guid.NewGuid(), ExtraType.DeletedScene, 148) };

        var result = DurationMatcher.Match(extras, local);

        Assert.Equal(BindingStatus.Unmatched, result["trailer"].Status);
    }

    [Fact]
    public void AmbiguousDurationFallsBackToOrdinalByAscendingDuration()
    {
        // Two deleted scenes whose expected durations both land within
        // tolerance of BOTH local candidates: duration alone can't
        // disambiguate, so ordinal (position sorted by ascending duration)
        // decides.
        var extras = new Dictionary<string, ExtraSpec>
        {
            ["first"] = Spec(ExtraType.DeletedScene, 145, toleranceSec: 10, ordinal: 1),
            ["second"] = Spec(ExtraType.DeletedScene, 145, toleranceSec: 10, ordinal: 2),
        };
        var rooftop = new LocalExtraCandidate(Guid.NewGuid(), ExtraType.DeletedScene, 142);
        var lobby = new LocalExtraCandidate(Guid.NewGuid(), ExtraType.DeletedScene, 150);
        var local = new List<LocalExtraCandidate> { lobby, rooftop }; // deliberately out of order

        var result = DurationMatcher.Match(extras, local);

        Assert.Equal(BindingMethod.DurationOrdinal, result["first"].Method);
        Assert.Equal(rooftop.ItemId, result["first"].ItemId); // ordinal 1 = lowest duration
        Assert.Equal(BindingMethod.DurationOrdinal, result["second"].Method);
        Assert.Equal(lobby.ItemId, result["second"].ItemId); // ordinal 2 = next lowest
    }

    [Fact]
    public void AmbiguousWithoutOrdinalStaysUnmatched()
    {
        var extras = new Dictionary<string, ExtraSpec>
        {
            ["first"] = Spec(ExtraType.DeletedScene, 145, toleranceSec: 10),
            ["second"] = Spec(ExtraType.DeletedScene, 145, toleranceSec: 10),
        };
        var local = new List<LocalExtraCandidate>
        {
            new(Guid.NewGuid(), ExtraType.DeletedScene, 142),
            new(Guid.NewGuid(), ExtraType.DeletedScene, 150),
        };

        var result = DurationMatcher.Match(extras, local);

        Assert.Equal(BindingStatus.Unmatched, result["first"].Status);
        Assert.Equal(BindingStatus.Unmatched, result["second"].Status);
    }

    [Fact]
    public void OrdinalOutOfRangeStaysUnmatched()
    {
        var extras = new Dictionary<string, ExtraSpec>
        {
            ["first"] = Spec(ExtraType.DeletedScene, 145, toleranceSec: 10, ordinal: 1),
            ["third"] = Spec(ExtraType.DeletedScene, 145, toleranceSec: 10, ordinal: 3), // only 2 candidates exist
        };
        var local = new List<LocalExtraCandidate>
        {
            new(Guid.NewGuid(), ExtraType.DeletedScene, 142),
            new(Guid.NewGuid(), ExtraType.DeletedScene, 150),
        };

        var result = DurationMatcher.Match(extras, local);

        Assert.Equal(BindingStatus.Matched, result["first"].Status);
        Assert.Equal(BindingStatus.Unmatched, result["third"].Status);
    }

    [Fact]
    public void EachCandidateIsConsumedAtMostOnce()
    {
        // Three specs of the same type, tolerance wide enough that all three
        // would "fit" against the single candidate if consumption weren't
        // tracked; only one can actually claim it.
        var extras = new Dictionary<string, ExtraSpec>
        {
            ["a"] = Spec(ExtraType.Clip, 100, toleranceSec: 50),
            ["b"] = Spec(ExtraType.Clip, 200, toleranceSec: 50, ordinal: 1),
            ["c"] = Spec(ExtraType.Clip, 300, toleranceSec: 50, ordinal: 1),
        };
        var only = new LocalExtraCandidate(Guid.NewGuid(), ExtraType.Clip, 120);
        var local = new List<LocalExtraCandidate> { only };

        var result = DurationMatcher.Match(extras, local);

        var matchedCount = result.Values.Count(b => b.Status == BindingStatus.Matched);
        Assert.Equal(1, matchedCount);
    }

    [Fact]
    public void MissingToleranceUsesDefaultOfThreeSeconds()
    {
        var extras = new Dictionary<string, ExtraSpec> { ["trailer"] = Spec(ExtraType.Trailer, 148) };
        var withinDefault = new List<LocalExtraCandidate> { new(Guid.NewGuid(), ExtraType.Trailer, 150.5) };
        var outsideDefault = new List<LocalExtraCandidate> { new(Guid.NewGuid(), ExtraType.Trailer, 152) };

        Assert.Equal(BindingStatus.Matched, DurationMatcher.Match(extras, withinDefault)["trailer"].Status);
        Assert.Equal(BindingStatus.Unmatched, DurationMatcher.Match(extras, outsideDefault)["trailer"].Status);
    }

    [Fact]
    public void EveryExtraKeyGetsAResult()
    {
        var extras = new Dictionary<string, ExtraSpec>
        {
            ["a"] = Spec(ExtraType.Trailer, 100),
            ["b"] = Spec(ExtraType.BehindTheScenes, 200),
        };

        var result = DurationMatcher.Match(extras, Array.Empty<LocalExtraCandidate>());

        Assert.Equal(2, result.Count);
        Assert.All(result.Values, b => Assert.Equal(BindingStatus.Unmatched, b.Status));
    }
}
