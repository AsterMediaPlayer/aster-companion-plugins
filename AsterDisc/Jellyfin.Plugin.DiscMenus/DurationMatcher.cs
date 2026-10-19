using Jellyfin.Plugin.DiscMenus.Model;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>
/// A local Special Feature available to match against a menu's expected
/// extras: just enough to match by, deliberately no file name/path (see
/// README's "Matching" section).
/// </summary>
public readonly record struct LocalExtraCandidate(Guid ItemId, ExtraType Type, double DurationSec);

/// <summary>
/// Matches a menu's expected extras to local candidates by type + duration
/// (± tolerance), falling back to ordinal position (by ascending duration,
/// among same-type candidates) when duration alone is ambiguous. Pure and
/// side-effect free; DiscMenuService owns discovering candidates and
/// persisting the result.
/// </summary>
public static class DurationMatcher
{
    private const double DefaultToleranceSec = 3.0;

    /// <summary>
    /// Returns a suggestion for every key in <paramref name="extras"/>: either
    /// a Matched binding (method Duration or DurationOrdinal) or an Unmatched
    /// one. Each candidate is consumed by at most one key. Duration matches
    /// are resolved first, across all specs of a type, before any ordinal
    /// fallback runs, so a precise duration match can't be preempted by a
    /// less certain ordinal one.
    /// </summary>
    public static IReadOnlyDictionary<string, ExtraBinding> Match(
        IReadOnlyDictionary<string, ExtraSpec> extras,
        IReadOnlyList<LocalExtraCandidate> candidates)
    {
        var results = new Dictionary<string, ExtraBinding>();
        var pool = new List<LocalExtraCandidate>(candidates);

        foreach (var typeGroup in extras.GroupBy(kv => kv.Value.Type))
        {
            var specsOfType = typeGroup.ToList();

            // Pass 1: a spec matches if exactly one remaining candidate of its
            // type falls within tolerance of its durationSec.
            foreach (var (key, spec) in specsOfType)
            {
                var tolerance = spec.ToleranceSec ?? DefaultToleranceSec;
                var within = pool
                    .Where(c => c.Type == typeGroup.Key && Math.Abs(c.DurationSec - spec.DurationSec) <= tolerance)
                    .ToList();
                if (within.Count != 1)
                {
                    continue;
                }

                var candidate = within[0];
                results[key] = MatchedBinding(candidate, BindingMethod.Duration, Confidence(Math.Abs(candidate.DurationSec - spec.DurationSec), tolerance));
                pool.Remove(candidate);
            }

            // Pass 2: remaining specs with an explicit ordinal fall back to
            // position-by-ascending-duration among what's left of this type.
            var remainingOfType = pool.Where(c => c.Type == typeGroup.Key).OrderBy(c => c.DurationSec).ToList();
            foreach (var (key, spec) in specsOfType)
            {
                if (results.ContainsKey(key) || spec.Ordinal is not { } ordinal)
                {
                    continue;
                }

                var index = ordinal - 1;
                if (index < 0 || index >= remainingOfType.Count || !pool.Contains(remainingOfType[index]))
                {
                    continue;
                }

                var candidate = remainingOfType[index];
                results[key] = MatchedBinding(candidate, BindingMethod.DurationOrdinal, 0.75);
                pool.Remove(candidate);
            }
        }

        foreach (var key in extras.Keys.Where(k => !results.ContainsKey(k)))
        {
            results[key] = new ExtraBinding { Status = BindingStatus.Unmatched };
        }

        return results;
    }

    private static ExtraBinding MatchedBinding(LocalExtraCandidate candidate, BindingMethod method, double confidence) => new()
    {
        Status = BindingStatus.Matched,
        ItemId = candidate.ItemId,
        Method = method,
        Confidence = confidence,
        ObservedDurationSec = candidate.DurationSec,
    };

    /// <summary>1.0 at a perfect match, floor of 0.85 at the tolerance boundary.</summary>
    private static double Confidence(double deltaSec, double toleranceSec)
    {
        if (toleranceSec <= 0)
        {
            return deltaSec == 0 ? 1.0 : 0.85;
        }

        var ratio = Math.Clamp(deltaSec / toleranceSec, 0, 1);
        return Math.Round(1.0 - (ratio * 0.15), 2);
    }
}
