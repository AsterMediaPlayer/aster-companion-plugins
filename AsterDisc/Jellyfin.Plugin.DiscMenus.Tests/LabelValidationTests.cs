using System.Text.Json.Nodes;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>
/// Entry labels and menu titles are plain text of 1-80 characters (schema $defs/label). The plugin's loader
/// doesn't run JSON Schema, so it must enforce the rule itself or a menu that the schema rejects would load
/// (and the Menu Editor, which validates with the loader, would stay silent about it).
/// </summary>
public class LabelValidationTests
{
    private static string ExamplePath([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "examples", "example.menu.json"));

    private static void Load(Action<JsonObject> mutate)
    {
        var doc = JsonNode.Parse(File.ReadAllText(ExamplePath()))!.AsObject();
        mutate(doc);
        MenuFileLoader.ParseMenu(doc.ToJsonString(), "test");
    }

    private static void AssertRejected(Action<JsonObject> mutate, string fragment)
    {
        var ex = Assert.Throws<MenuValidationException>(() => Load(mutate));
        Assert.Contains(ex.Errors, e => e.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonNode FirstEntry(JsonObject d) => d["menus"]!["main"]!["entries"]![0]!;

    [Theory]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("")]
    public void RejectsBadEntryLabels(string label) =>
        AssertRejected(d => FirstEntry(d)["label"] = label, "main[0] label");

    [Fact]
    public void RejectsOverLongEntryLabels() =>
        AssertRejected(d => FirstEntry(d)["label"] = new string('x', 81), "main[0] label");

    [Theory]
    [InlineData("<b>Main</b>")]
    [InlineData("")]
    public void RejectsBadMenuTitles(string title) =>
        AssertRejected(d => d["menus"]!["main"]!["title"] = title, "menu 'main' title");

    [Fact]
    public void RejectsOverLongMenuTitles() =>
        AssertRejected(d => d["menus"]!["features"]!["title"] = new string('t', 81), "menu 'features' title");

    [Fact]
    public void AcceptsLabelsAtTheLimitsAndInAnyScript()
    {
        Load(d => FirstEntry(d)["label"] = "x");
        Load(d => FirstEntry(d)["label"] = new string('x', 80));
        Load(d => FirstEntry(d)["label"] = "日本語のタイトル");
        Load(d => FirstEntry(d)["label"] = "Café & Crème \"Brûlée\" (2008) — 50%");
        Load(d => d["menus"]!["main"]!["title"] = new string('t', 80));
    }

    [Fact]
    public void CountsCharactersLikeTheSchemaDoesNotUtf16Units()
    {
        // 80 emoji is 80 characters (160 UTF-16 units): the schema accepts it, so the loader must too.
        Load(d => FirstEntry(d)["label"] = string.Concat(Enumerable.Repeat("😀", 80)));
        // 81 is one over, in either counting.
        AssertRejected(d => FirstEntry(d)["label"] = string.Concat(Enumerable.Repeat("😀", 81)), "main[0] label");
    }

    [Fact]
    public void FlowLabelsFollowTheSameRule()
    {
        AssertRejected(d => d["menus"]!["features"]!["layout"] = new JsonObject
        {
            ["flow"] = new JsonObject
            {
                ["region"] = new JsonObject { ["x"] = 50, ["y"] = 50, ["w"] = 50, ["h"] = 20 },
                ["columns"] = 4,
                ["rows"] = 1,
                ["moreLabel"] = "<More>",
            },
        }, "labels must be");
    }

    [Fact]
    public void EveryShippedExampleStillLoads()
    {
        var dir = Path.GetDirectoryName(ExamplePath())!;
        foreach (var file in Directory.GetFiles(dir, "*.menu.json"))
        {
            MenuFileLoader.LoadMenu(file);
        }
    }
}

public class FanartBackgroundValidationTests
{
    private static string Menu(string background) => """
        {"schemaVersion":1,"menuId":"a7c1e3f2-5b84-4d96-8e21-3f0c9d5a6b47","revision":1,
         "match":{"itemType":"Movie","providerIds":{"Tmdb":"603"}},
         "background":BACKGROUND,
         "root":"main","menus":{"main":{"title":"Main","entries":[{"action":"playFeature","label":"Play"}]}}}
        """.Replace("BACKGROUND", background);

    [Theory]
    [InlineData("{\"source\":\"fanart\",\"fanartId\":\"47835\"}")]
    [InlineData("{\"source\":\"fanart\",\"fanartId\":\"1\",\"dim\":0.5}")]
    [InlineData("{\"source\":\"fanart\",\"fanartId\":\"123456789012\"}")]
    public void AFanartBackgroundWithANumericIdLoads(string background) =>
        Assert.Empty(MenuFileEditor.Validate(Menu(background)));

    [Theory]
    [InlineData("{\"source\":\"fanart\"}")]
    [InlineData("{\"source\":\"fanart\",\"fanartId\":\"\"}")]
    [InlineData("{\"source\":\"fanart\",\"fanartId\":\"12ab\"}")]
    [InlineData("{\"source\":\"fanart\",\"fanartId\":\"1234567890123\"}")]
    [InlineData("{\"source\":\"fanart\",\"fanartId\":\"../1\"}")]
    [InlineData("{\"source\":\"fanart\",\"fanartId\":\"1 \"}")]
    [InlineData("{\"source\":\"fanart\",\"fanartId\":\"https://assets.fanart.tv/a.jpg\"}")]
    public void AFanartBackgroundWithoutAValidIdIsRefused(string background)
    {
        var errors = MenuFileEditor.Validate(Menu(background));
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Message.Contains("fanartId", StringComparison.Ordinal) || e.Message.Contains("JSON", StringComparison.Ordinal));
    }
}
