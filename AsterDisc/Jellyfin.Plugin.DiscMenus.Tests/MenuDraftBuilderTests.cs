using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.DiscMenus.Model;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

public class MenuDraftBuilderTests
{
    private static readonly Dictionary<string, string> WallEIds = new() { ["Tmdb"] = "10681", ["Imdb"] = "tt0910970" };

    private static DraftSource Source(
        IEnumerable<DraftExtra>? extras = null, bool chapters = true, int? width = 3840, Dictionary<string, string>? ids = null, string title = "WALL·E") =>
        new(title, 2008, width, ids ?? WallEIds, (extras ?? Array.Empty<DraftExtra>()).ToList(), chapters);

    // The real WALL·E library entry (names are straight from the disc rip).
    private static readonly DraftExtra[] WallEExtras =
    {
        new("The Pixar Story", "Unknown", 5310),
        new("Featurette - title 083", "Featurette", 310),
        new("Featurette - title 084", "Featurette", 347),
        new("Featurette - title 073", "Featurette", 478),
        new("Featurette - title 076", "Featurette", 642),
        new("BnL Short - Cup of Yogurt", "Short", 66),
        new("BnL Short - PR-T", "Short", 90),
        new("BnL Short - Profits", "Short", 107),
        new("BnL Short - title 090", "Short", 119),
        new("BnL Short - title 089", "Short", 149),
        new("BnL Short - title 096", "Short", 189),
    };

    /// <summary>The draft must survive the plugin's own loader: the same checks as a hand-written file.</summary>
    private static MenuDocument Load(JsonObject draft) => MenuFileLoader.ParseMenu(draft.ToJsonString(), "draft");

    [Fact]
    public void WallEDraftPassesTheRealLoaderAndKeepsEveryExtra()
    {
        var doc = Load(MenuDraftBuilder.Build(Source(WallEExtras)));
        Assert.Equal(11, doc.Extras.Count);
        Assert.Equal("10681", doc.Match.ProviderIds["Tmdb"]);
        Assert.Equal("tt0910970", doc.Match.ProviderIds["Imdb"]);
        Assert.Equal(MatchItemType.Movie, doc.Match.ItemType);
        Assert.Equal(ReleaseFormat.UHD, doc.Match.Release!.Format);
        Assert.Equal(2008, doc.Match.Release.Year);
        // Real types and durations are what the matcher will use.
        Assert.Equal(5310, doc.Extras.Values.Single(e => e.Type.ToString() == "Unknown").DurationSec);
        Assert.Equal(6, doc.Extras.Values.Count(e => e.Type.ToString() == "Short"));
        Assert.Equal(4, doc.Extras.Values.Count(e => e.Type.ToString() == "Featurette"));
    }

    [Fact]
    public void ManyExtrasAreGroupedByTypeWithPlayAll()
    {
        var doc = Load(MenuDraftBuilder.Build(Source(WallEExtras)));
        var features = doc.Menus["features"];
        var labels = features.Entries.Select(e => e.Label).ToList();
        Assert.Contains("Featurettes", labels);
        Assert.Contains("Shorts", labels);
        Assert.Contains("The Pixar Story", labels); // a type with a single extra is a direct entry
        Assert.Equal("Back", labels.Last());

        var shorts = doc.Menus["group-shorts"];
        Assert.IsType<PlaySequenceEntry>(shorts.Entries.First());
        Assert.Equal(6, ((PlaySequenceEntry)shorts.Entries.First()).Extras.Count);
        Assert.IsType<BackEntry>(shorts.Entries.Last());
        // every menu is reachable and every extra is used somewhere (the loader enforces reachability)
        var used = doc.Menus.Values.SelectMany(m => m.Entries).OfType<PlayExtraEntry>().Select(e => e.Extra).ToHashSet();
        Assert.Equal(doc.Extras.Keys.ToHashSet(), used);
    }

    [Fact]
    public void MeaninglessRipNamesBecomeNumberedLabelsWithTheirLength()
    {
        var doc = Load(MenuDraftBuilder.Build(Source(WallEExtras)));
        var featurettes = doc.Menus["group-featurettes"].Entries.OfType<PlayExtraEntry>().Select(e => e.Label).ToList();
        // natural order of "title 073, 076, 083, 084": 478s, 642s, 310s, 347s
        Assert.Equal(new[] { "Featurette 1 (7:58)", "Featurette 2 (10:42)", "Featurette 3 (5:10)", "Featurette 4 (5:47)" }, featurettes);
        var shortLabels = doc.Menus["group-shorts"].Entries.OfType<PlayExtraEntry>().Select(e => e.Label).ToList();
        Assert.Contains("Cup of Yogurt", shortLabels);   // meaningful names keep their text, minus the prefix
        Assert.Contains("PR-T", shortLabels);
        Assert.Contains("Profits", shortLabels);
        Assert.Contains(shortLabels, l => l.StartsWith("Short ", StringComparison.Ordinal));
    }

    [Fact]
    public void FewExtrasStayAFlatList()
    {
        var extras = new[] { new DraftExtra("Gag Reel", "BehindTheScenes", 138), new DraftExtra("Deleted Scenes", "DeletedScene", 441) };
        var doc = Load(MenuDraftBuilder.Build(Source(extras)));
        Assert.Equal(new[] { "Gag Reel", "Deleted Scenes", "Back" }, doc.Menus["features"].Entries.Select(e => e.Label));
        Assert.DoesNotContain(doc.Menus.Keys, k => k.StartsWith("group-", StringComparison.Ordinal));
    }

    [Fact]
    public void ChaptersAndExtrasEntriesAppearOnlyWhenThereIsSomethingToShow()
    {
        var bare = Load(MenuDraftBuilder.Build(Source(chapters: false)));
        Assert.Equal(new[] { "Play Movie" }, bare.Menus["main"].Entries.Select(e => e.Label));
        Assert.DoesNotContain("features", bare.Menus.Keys);

        var withChapters = Load(MenuDraftBuilder.Build(Source(chapters: true)));
        Assert.Contains(withChapters.Menus["main"].Entries, e => e is ChaptersEntry { PerPage: 6 });

        var withExtras = Load(MenuDraftBuilder.Build(Source(new[] { new DraftExtra("X", "Featurette", 100) }, chapters: false)));
        Assert.Contains(withExtras.Menus["main"].Entries, e => e is SubmenuEntry { Menu: "features" });
    }

    [Fact]
    public void ThemeMediaAndZeroLengthExtrasAreNotMenuEntries()
    {
        var extras = new[]
        {
            new DraftExtra("Theme", "ThemeSong", 120), new DraftExtra("Theme video", "ThemeVideo", 90),
            new DraftExtra("Empty", "Featurette", 0), new DraftExtra("Real", "Featurette", 200),
        };
        var doc = Load(MenuDraftBuilder.Build(Source(extras)));
        Assert.Equal(new[] { "real" }, doc.Extras.Keys.ToArray());
    }

    [Fact]
    public void FormatFollowsResolutionAndIsOmittedWhenUnknown()
    {
        Assert.Equal(ReleaseFormat.UHD, Load(MenuDraftBuilder.Build(Source(width: 3840))).Match.Release!.Format);
        Assert.Equal(ReleaseFormat.BluRay, Load(MenuDraftBuilder.Build(Source(width: 1920))).Match.Release!.Format);
        Assert.Null(Load(MenuDraftBuilder.Build(Source(width: 720))).Match.Release!.Format);
        Assert.Null(Load(MenuDraftBuilder.Build(Source(width: null))).Match.Release!.Format);
    }

    [Fact]
    public void KeysAreValidAndUniqueEvenForDuplicateAndOddNames()
    {
        var extras = new[]
        {
            new DraftExtra("Same Name", "Featurette", 100), new DraftExtra("Same Name", "Featurette", 200), new DraftExtra("Same Name", "Short", 300),
            new DraftExtra("日本語のタイトル", "Featurette", 400), new DraftExtra("!!!", "Featurette", 500), new DraftExtra("Café Déjà Vu", "Featurette", 600),
            new DraftExtra(new string('x', 400), "Featurette", 700),
        };
        var doc = Load(MenuDraftBuilder.Build(Source(extras)));
        Assert.Equal(extras.Length, doc.Extras.Count);
        Assert.All(doc.Extras.Keys, k => Assert.Matches("^[a-z0-9][a-z0-9_-]{0,63}$", k));
        Assert.Contains("cafe-deja-vu", doc.Extras.Keys);
    }

    [Fact]
    public void HostileNamesNeverReachLabelsAsMarkupOrOverflow()
    {
        var extras = new[] { new DraftExtra("<img src=x onerror=alert(1)>Evil<script>", "Featurette", 100), new DraftExtra(new string('y', 500), "Short", 200) };
        var draft = MenuDraftBuilder.Build(Source(extras, title: "<b>Title</b> " + new string('z', 200)));
        var doc = Load(draft); // loader rejects < > and over-long labels
        Assert.All(doc.Menus.Values.SelectMany(m => m.Entries), e => Assert.DoesNotMatch("[<>]", e.Label));
        Assert.All(doc.Menus.Values.SelectMany(m => m.Entries), e => Assert.InRange(e.Label.Length, 1, 80));
        Assert.All(doc.Menus.Values, m => Assert.DoesNotMatch("[<>]", m.Title));
    }

    [Fact]
    public void GiantExtraListsRespectTheEntryLimit()
    {
        var extras = Enumerable.Range(1, 120).Select(i => new DraftExtra($"Clip {i}", "Featurette", 60 + i)).ToList();
        var doc = Load(MenuDraftBuilder.Build(Source(extras)));
        Assert.All(doc.Menus.Values, m => Assert.True(m.Entries.Count <= 50));
    }

    [Theory]
    [InlineData("Tmdb", "abc")]
    [InlineData("Imdb", "123")]
    [InlineData("Tmdb", "")]
    public void UnusableProviderIdsAreRefused(string provider, string value) =>
        Assert.Throws<ArgumentException>(() => MenuDraftBuilder.Build(Source(ids: new Dictionary<string, string> { [provider] = value })));

    [Fact]
    public void NoProviderIdsAtAllIsRefusedAndOneGoodIdIsEnough()
    {
        Assert.Throws<ArgumentException>(() => MenuDraftBuilder.Build(Source(ids: new Dictionary<string, string>())));
        var doc = Load(MenuDraftBuilder.Build(Source(ids: new Dictionary<string, string> { ["Tmdb"] = "10681", ["Imdb"] = "junk", ["Other"] = "1" })));
        Assert.Single(doc.Match.ProviderIds);
    }

    [Fact]
    public void DraftIsDeterministicApartFromItsIdAndTimestamp()
    {
        var id = Guid.NewGuid();
        var when = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var a = MenuDraftBuilder.Build(Source(WallEExtras) with { MenuId = id, Created = when }).ToJsonString();
        var b = MenuDraftBuilder.Build(Source(WallEExtras.Reverse().ToArray()) with { MenuId = id, Created = when }).ToJsonString();
        Assert.Equal(a, b); // input order doesn't matter
    }

    [Fact]
    public void SlugFoldsDiacriticsAndPunctuation()
    {
        Assert.Equal("wall-e", MenuDraftBuilder.Slug("WALL·E"));
        Assert.Equal("amelie", MenuDraftBuilder.Slug("Amélie"));
        Assert.Equal("", MenuDraftBuilder.Slug("日本語"));
        Assert.Equal("a-b-c", MenuDraftBuilder.Slug("  a / b -- c  "));
    }
}
