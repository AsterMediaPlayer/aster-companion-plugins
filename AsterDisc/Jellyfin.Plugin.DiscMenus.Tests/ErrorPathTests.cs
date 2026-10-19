using System.Text.Json.Nodes;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>Every problem the editor reports says where it is, as a JSON Pointer, so clicking it can select the text.</summary>
public class ErrorPathTests
{
    private const string Good = """
        {"schemaVersion":1,"menuId":"a7c1e3f2-5b84-4d96-8e21-3f0c9d5a6b47","revision":1,
         "match":{"itemType":"Movie","providerIds":{"Tmdb":"603"}},
         "extras":{"a":{"type":"Trailer","durationSec":10}},
         "root":"main",
         "menus":{
           "main":{"title":"Main","entries":[{"action":"playFeature","label":"Play"},{"action":"submenu","label":"More","menu":"more"},{"action":"playExtra","label":"A","extra":"a"}]},
           "more":{"title":"More","entries":[{"action":"back","label":"Back"}]}}}
        """;

    private static string Mutate(Action<JsonObject> change)
    {
        var doc = JsonNode.Parse(Good)!.AsObject();
        change(doc);
        return doc.ToJsonString();
    }

    private static JsonObject Menus(JsonObject d) => d["menus"]!.AsObject();

    private static JsonNode Entry(JsonObject d, string menu, int i) => Menus(d)[menu]!["entries"]![i]!;

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { (Action<JsonObject>)(d => d["root"] = "nope"), "/root" };
        yield return new object[] { (Action<JsonObject>)(d => Entry(d, "main", 0)["label"] = "<b>"), "/menus/main/entries/0/label" };
        yield return new object[] { (Action<JsonObject>)(d => Menus(d)["more"]!["title"] = ""), "/menus/more/title" };
        yield return new object[] { (Action<JsonObject>)(d => Entry(d, "main", 1)["menu"] = "ghost"), "/menus/main/entries/1/menu" };
        yield return new object[] { (Action<JsonObject>)(d => Entry(d, "main", 2)["extra"] = "ghost"), "/menus/main/entries/2/extra" };
        yield return new object[] { (Action<JsonObject>)(d => Entry(d, "main", 0)["style"] = "wavy"), "/menus/main/entries/0/style" };
        yield return new object[] { (Action<JsonObject>)(d => Entry(d, "main", 0)["image"] = "javascript:1"), "/menus/main/entries/0/image" };
        yield return new object[] { (Action<JsonObject>)(d => { foreach (var e in Menus(d)["main"]!["entries"]!.AsArray()) { e!["position"] = new JsonObject { ["x"] = 1, ["y"] = 1 }; } Entry(d, "main", 1)["position"]!["anchor"] = "middle"; }), "/menus/main/entries/1/position/anchor" };
        yield return new object[] { (Action<JsonObject>)(d => Menus(d)["orphan"] = JsonNode.Parse("""{"title":"O","entries":[{"action":"back","label":"B"}]}""")), "/menus/orphan" };
        yield return new object[] { (Action<JsonObject>)(d => d["background"] = JsonNode.Parse("""{"source":"fanart","fanartId":"abc"}""")), "/background/fanartId" };
        yield return new object[] { (Action<JsonObject>)(d => Menus(d)["more"]!["background"] = JsonNode.Parse("""{"source":"image","image":"nope"}""")), "/menus/more/background/image" };
        yield return new object[] { (Action<JsonObject>)(d => d["theme"] = JsonNode.Parse("""{"id":"t","accent":"red"}""")), "/theme" };
        yield return new object[] { (Action<JsonObject>)(d => d["theme"] = JsonNode.Parse("""{"id":"t","fontSize":99}""")), "/theme/fontSize" };
        yield return new object[] { (Action<JsonObject>)(d => d["audio"] = JsonNode.Parse("""{"music":{"source":"file","file":"x","volume":0.5}}""")), "/audio/music/file" };
        yield return new object[] { (Action<JsonObject>)(d => d["layout"] = JsonNode.Parse("""{"transition":{"style":"spin"}}""")), "/layout/transition/style" };
        yield return new object[] { (Action<JsonObject>)(d => d["layout"] = JsonNode.Parse("""{"titlePosition":{"x":500,"y":1}}""")), "/layout/titlePosition" };
        yield return new object[] { (Action<JsonObject>)(d => d["layout"] = JsonNode.Parse("""{"layers":[{"type":"panel","position":{"x":0,"y":0}},{"type":"image","position":{"x":0,"y":0},"image":"bad"}]}""")), "/layout/layers/1/image" };
        yield return new object[] { (Action<JsonObject>)(d => Menus(d)["more"]!["layout"] = JsonNode.Parse("""{"flow":{"region":{"x":0,"y":0,"w":50,"h":50},"columns":1,"rows":1}}""")), "/menus/more/layout/flow" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AProblemPointsAtItsText(Action<JsonObject> change, string? expected)
    {
        var errors = MenuFileEditor.Validate(Mutate(change));
        if (expected is null)
        {
            Assert.Empty(errors);
            return;
        }

        Assert.Contains(errors, e => e.Path == expected);
    }

    [Fact]
    public void ASyntaxProblemKeepsItsLineAndColumn()
    {
        var error = Assert.Single(MenuFileEditor.Validate("{\n  \"a\": ,\n}"));
        Assert.Equal(2, error.Line);
        Assert.NotNull(error.Column);
    }

    [Fact]
    public void ATypeMismatchPointsAtTheValue()
    {
        var error = Assert.Single(MenuFileEditor.Validate(Good.Replace("\"revision\":1", "\"revision\":\"one\"", StringComparison.Ordinal)));
        Assert.Equal("/revision", error.Path);
    }

    [Theory]
    [InlineData("$.menus.main.entries[0].label", "/menus/main/entries/0/label")]
    [InlineData("$", "")]
    [InlineData("$.a", "/a")]
    [InlineData("$['we/ird'].x", "/we~1ird/x")]
    [InlineData("nonsense", null)]
    [InlineData(null, null)]
    public void JsonPathsBecomePointers(string? path, string? pointer) => Assert.Equal(pointer, ErrorPaths.FromJsonPath(path));
}
