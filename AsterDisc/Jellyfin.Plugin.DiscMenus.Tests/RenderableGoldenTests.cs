using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.DiscMenus.Api;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>
/// "Golden" files: what <see cref="RenderableBuilder"/> produces (serialised the way Jellyfin serialises API
/// responses: PascalCase, nulls omitted) for every example menu bound to nothing. tests/js/preview-adapter.test.js
/// requires the JavaScript adapter (tools/preview/menu-to-renderable.js) to produce exactly the same, so the
/// adapter cannot drift from the real server code without a test failing.
///
/// If you change what RenderableBuilder produces on purpose, regenerate the files:
///     UPDATE_GOLDEN=1 dotnet test Jellyfin.Plugin.DiscMenus.Tests --filter RenderableGoldenTests
/// and update the adapter until its test passes again.
/// </summary>
public class RenderableGoldenTests
{
    // Jellyfin writes a GUID as 32 hex digits with no hyphens ("N" format); System.Text.Json's default is the
    // hyphenated form. Checked against a running Jellyfin 12.1 server, whose responses carry the "N" form.
    private sealed class NFormatGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Guid.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("N"));
    }

    private static readonly JsonSerializerOptions ServerStyle = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new NFormatGuidConverter() },
    };

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

    private static string ExamplesDir => Path.Combine(RepoRoot(), "examples");

    private static string GoldenDir => Path.Combine(RepoRoot(), "tests", "fixtures", "renderable");

    public static IEnumerable<object[]> Examples() =>
        Directory.GetFiles(ExamplesDir, "*.menu.json").OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new object[] { Path.GetFileName(p)[..^".menu.json".Length] });

    private static string Render(string exampleName)
    {
        var menu = MenuFileLoader.LoadMenu(Path.Combine(ExamplesDir, exampleName + ".menu.json"));
        // No binding and no item: nothing is looked up in a library, so no service is needed.
        var doc = RenderableBuilder.Build(menu, null, Guid.Empty, null!);
        return JsonSerializer.Serialize(doc, ServerStyle);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void RenderableOutputMatchesTheGoldenFile(string name)
    {
        var actual = Render(name);
        var golden = Path.Combine(GoldenDir, name + ".renderable.json");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenDir);
            File.WriteAllText(golden, actual + "\n");
            return;
        }

        Assert.True(File.Exists(golden), $"missing {golden}: run with UPDATE_GOLDEN=1 to create it");
        var expected = JsonNode.Parse(File.ReadAllText(golden));
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(actual)), $"{name}: RenderableBuilder output no longer matches {golden}");
    }

    [Fact]
    public void EveryExampleHasAGoldenFileAndNothingIsLeftOver()
    {
        var examples = Directory.GetFiles(ExamplesDir, "*.menu.json").Select(p => Path.GetFileName(p)[..^".menu.json".Length]).OrderBy(x => x);
        var goldens = Directory.Exists(GoldenDir)
            ? Directory.GetFiles(GoldenDir, "*.renderable.json").Select(p => Path.GetFileName(p)[..^".renderable.json".Length]).OrderBy(x => x)
            : Enumerable.Empty<string>();
        Assert.Equal(examples, goldens);
    }
}
