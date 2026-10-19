using System.Reflection;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>
/// A repeated key used to mean "the last one wins" in every layer. The Menu Editor's forms edit text in place, so "which of the two did
/// the form change?" must never arise: the loader refuses a repeated key anywhere, including inside the maps (menus, extras, provider ids).
/// </summary>
public class DuplicateKeyTests
{
    private const string Good = """
        {"schemaVersion":1,"menuId":"a7c1e3f2-5b84-4d96-8e21-3f0c9d5a6b47","revision":1,
         "match":{"itemType":"Movie","providerIds":{"Tmdb":"603","Imdb":"tt0133093"}},
         "extras":{"a":{"type":"Trailer","durationSec":10},"b":{"type":"Featurette","durationSec":20}},
         "root":"main",
         "menus":{
           "main":{"title":"Main","entries":[{"action":"playFeature","label":"Play"},{"action":"submenu","label":"More","menu":"more"}]},
           "more":{"title":"More","entries":[{"action":"back","label":"Back"}]}}}
        """;

    [Fact]
    public void TheBaseDocumentIsValid() => Assert.Empty(MenuFileEditor.Validate(Good));

    [Theory]
    [InlineData("\"revision\":1,", "\"revision\":1,\"revision\":2,")]
    [InlineData("\"Tmdb\":\"603\",", "\"Tmdb\":\"603\",\"Tmdb\":\"604\",")]
    [InlineData("\"a\":{\"type\":\"Trailer\",\"durationSec\":10},", "\"a\":{\"type\":\"Trailer\",\"durationSec\":10},\"a\":{\"type\":\"Trailer\",\"durationSec\":11},")]
    [InlineData("\"more\":{\"title\":\"More\"", "\"main\":{\"title\":\"Again\",\"entries\":[{\"action\":\"back\",\"label\":\"B\"}]},\"more\":{\"title\":\"More\"")]
    [InlineData("{\"action\":\"playFeature\",\"label\":\"Play\"}", "{\"action\":\"playFeature\",\"label\":\"Play\",\"label\":\"Again\"}")]
    [InlineData("\"itemType\":\"Movie\",", "\"itemType\":\"Movie\",\"itemType\":\"Series\",")]
    public void ARepeatedKeyIsRefusedWhereverItIs(string original, string withDuplicate)
    {
        Assert.Contains(original, Good, StringComparison.Ordinal);
        var errors = MenuFileEditor.Validate(Good.Replace(original, withDuplicate, StringComparison.Ordinal));
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheErrorSaysWhere()
    {
        var text = Good.Replace("\"revision\":1,", "\"revision\":1,\n\"revision\":2,", StringComparison.Ordinal);
        var error = Assert.Single(MenuFileEditor.Validate(text));
        Assert.NotNull(error.Line);
    }

    [Fact]
    public void KeysThatDifferInCaseOnlyAreDifferentKeys()
    {
        // JSON keys are case-sensitive; a menu keyed "Main" and "main" is two menus (the schema's key pattern forbids upper case anyway)
        var text = Good.Replace("\"more\":{\"title\":\"More\"", "\"MORE\":{\"title\":\"X\",\"entries\":[{\"action\":\"back\",\"label\":\"B\"}]},\"more\":{\"title\":\"More\"", StringComparison.Ordinal);
        Assert.DoesNotContain(MenuFileEditor.Validate(text), e => e.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnExampleMenuStillLoads()
    {
        var examples = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(TestPath())!, "..", "examples"));
        foreach (var file in Directory.GetFiles(examples, "*.menu.json"))
        {
            Assert.Empty(MenuFileEditor.Validate(File.ReadAllText(file)));
        }
    }

    private static string TestPath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

/// <summary>The editor's script modules must really be inside the plugin DLL (that is all that ships) and nothing else is reachable by name.</summary>
public class EditorModuleResourceTests
{
    [Theory]
    [InlineData("json-text")]
    [InlineData("text-adapter")]
    [InlineData("outline")]
    [InlineData("schema-hints")]
    [InlineData("inspector")]
    [InlineData("pictures")]
    [InlineData("designer-host")]
    public void TheModuleIsEmbedded(string name)
    {
        var assembly = typeof(MenuFileLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.Web.editor.{name}.js");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var text = reader.ReadToEnd();
        Assert.True(text.Length > 500);
        Assert.StartsWith("/*", text.TrimStart('﻿'), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEditorScriptOnDiskIsEmbedded()
    {
        var dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(TestPath())!, "..", "Jellyfin.Plugin.DiscMenus", "Web", "editor"));
        var assembly = typeof(MenuFileLoader).Assembly;
        var embedded = assembly.GetManifestResourceNames().ToHashSet();
        foreach (var file in Directory.GetFiles(dir, "*.js"))
        {
            Assert.Contains($"{assembly.GetName().Name}.Web.editor.{Path.GetFileName(file)}", embedded);
        }
    }

    [Fact]
    public void NoOtherNameResolvesToAResource()
    {
        var assembly = typeof(MenuFileLoader).Assembly;
        foreach (var name in new[] { "../discmenus", "discmenus", "nope", "json-text.js", "..%2f..%2fsecrets" })
        {
            Assert.Null(assembly.GetManifestResourceStream($"{assembly.GetName().Name}.Web.editor.{name}.js"));
        }
    }

    private static string TestPath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
