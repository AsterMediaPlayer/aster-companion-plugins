using Jellyfin.Plugin.DiscMenus.Model;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>
/// Every shipped example menu must load through the plugin's real loader (JSON
/// parsing plus its semantic and presentation checks), not just pass the JSON
/// Schema in tools/validate.py - the two validators are independent
/// implementations of the same rules, so this keeps them honest with each other.
/// </summary>
public class ExampleMenuTests
{
    // Locate the repo from this source file's own path (recorded by the compiler), not the
    // working directory: in the dev container the tests run from /artifacts, outside the repo.
    private static string ExamplesDir([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var candidate = Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "examples");
        if (File.Exists(Path.Combine(candidate, "example.menu.json")))
        {
            return Path.GetFullPath(candidate);
        }

        throw new DirectoryNotFoundException("Could not find the repo's examples folder from " + thisFile);
    }

    public static IEnumerable<object[]> ExampleFiles() =>
        Directory.GetFiles(ExamplesDir(), "*.menu.json").OrderBy(p => p).Select(p => new object[] { Path.GetFileName(p) });

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void ExampleLoadsThroughRealLoader(string fileName)
    {
        var menu = MenuFileLoader.LoadMenu(Path.Combine(ExamplesDir(), fileName));
        Assert.NotEmpty(menu.Menus);
        Assert.True(menu.Menus.ContainsKey(menu.Root));
    }

    [Fact]
    public void ExamplesCoverTheFeatureMatrix()
    {
        var docs = Directory.GetFiles(ExamplesDir(), "*.menu.json").Select(MenuFileLoader.LoadMenu).ToList();
        var sources = docs.Select(d => d.Background?.Source).ToHashSet();
        Assert.Contains(BackgroundSource.Jellyfin, sources);
        Assert.Contains(BackgroundSource.Color, sources);
        Assert.Contains(BackgroundSource.Trailer, sources);
        Assert.Contains(BackgroundSource.Image, sources);
        Assert.Contains(docs, d => d.Menus.Values.Any(m => (m.Layout?.Flow ?? d.Layout?.Flow) is not null));
        Assert.Contains(docs, d => d.Menus.Values.SelectMany(m => m.Entries).Any(e => e is HomeEntry));
        Assert.Contains(docs, d => d.Audio?.Music?.Source == "file");
        Assert.Contains(docs, d => d.Audio?.Music?.Source == "themeSong");
        Assert.Contains(docs, d => d.Audio?.Sounds?.Preset is not null);
        foreach (var style in new[] { "fade", "slide", "zoom", "wipe" })
        {
            Assert.Contains(docs, d => d.Layout?.Transition?.Style == style);
        }

        Assert.Equal(docs.Count, docs.Select(d => d.MenuId).Distinct().Count());
    }

    [Fact]
    public void BundledAssetsReferencedByExamplesExist()
    {
        var assets = Path.Combine(ExamplesDir(), "assets");
        foreach (var name in new[] { "local-art/background.webp", "local-art/banner.webp", "local-art/ambient.wav" })
        {
            Assert.True(File.Exists(Path.Combine(assets, name)), name + " should be bundled with the examples");
        }
    }
}
