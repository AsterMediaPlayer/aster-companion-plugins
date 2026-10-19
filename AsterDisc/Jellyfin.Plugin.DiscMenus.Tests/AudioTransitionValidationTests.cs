using System.Text.Json.Nodes;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>
/// The plugin's loader re-implements the schema's audio/transition rules (it doesn't run JSON
/// Schema), so these feed mutated copies of a real example through the real loader and check it
/// agrees with tools/validate.py about what is acceptable.
/// </summary>
public class AudioTransitionValidationTests
{
    private static string ExamplePath([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "examples", "example.menu.json"));

    private static void Load(Action<JsonObject> mutate)
    {
        var doc = JsonNode.Parse(File.ReadAllText(ExamplePath()))!.AsObject();
        mutate(doc);
        var temp = Path.Combine(Path.GetTempPath(), $"dm-{Guid.NewGuid():N}.menu.json");
        try
        {
            File.WriteAllText(temp, doc.ToJsonString());
            MenuFileLoader.LoadMenu(temp);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static JsonObject Audio(JsonNode music) => new() { ["music"] = music };

    private static void AssertRejected(Action<JsonObject> mutate, string expectedFragment)
    {
        var ex = Assert.Throws<MenuValidationException>(() => Load(mutate));
        Assert.Contains(ex.Errors, e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("asset:local-art/ambient.mp3")]
    [InlineData("asset:a.ogg")]
    [InlineData("asset:a/b/c/d.opus")]
    [InlineData("asset:x/loop.m4a")]
    [InlineData("asset:x/loop.wav")]
    [InlineData("https://example.com/music/theme.mp3")]
    public void AcceptsGoodMusicFiles(string file) =>
        Load(d => d["audio"] = Audio(new JsonObject { ["source"] = "file", ["file"] = file, ["volume"] = 0.4 }));

    [Fact]
    public void AcceptsThemeSongNoneAndSoundPresets()
    {
        Load(d => d["audio"] = Audio(new JsonObject { ["source"] = "themeSong" }));
        Load(d => d["audio"] = Audio(new JsonObject { ["source"] = "none" }));
        Load(d => d["audio"] = new JsonObject { ["sounds"] = new JsonObject { ["preset"] = "chime", ["volume"] = 0.3, ["select"] = "asset:s/ok.wav" } });
        Load(d => d["menus"]!["main"]!["audio"] = Audio(new JsonObject { ["source"] = "none" }));
    }

    [Theory]
    [InlineData("/mnt/music/a.mp3")]
    [InlineData("asset:x/a.exe")]
    [InlineData("asset:x/a.png")]
    [InlineData("asset:../a.mp3")]
    [InlineData("asset:/etc/a.mp3")]
    [InlineData("asset:.hidden/a.mp3")]
    [InlineData("asset:a/b/c/d/e/f.mp3")]
    [InlineData("http://example.com/a.mp3")]
    [InlineData("https://e.com/a.mp3\")x:url(")]
    [InlineData("data:audio/mp3;base64,AAAA")]
    [InlineData("asset:x/A.MP3")]
    public void RejectsBadMusicFiles(string file) =>
        AssertRejected(d => d["audio"] = Audio(new JsonObject { ["source"] = "file", ["file"] = file }), "music file");

    [Fact]
    public void RejectsFileSourceWithoutFileAndUnknownSource()
    {
        AssertRejected(d => d["audio"] = Audio(new JsonObject { ["source"] = "file" }), "music file");
        AssertRejected(d => d["audio"] = Audio(new JsonObject { ["source"] = "spotify" }), "source");
    }

    [Fact]
    public void RejectsOutOfRangeVolumeAndBadSounds()
    {
        AssertRejected(d => d["audio"] = Audio(new JsonObject { ["source"] = "themeSong", ["volume"] = 2 }), "volume");
        AssertRejected(d => d["audio"] = new JsonObject { ["sounds"] = new JsonObject { ["volume"] = -0.1 } }, "volume");
        AssertRejected(d => d["audio"] = new JsonObject { ["sounds"] = new JsonObject { ["preset"] = "laser" } }, "preset");
        AssertRejected(d => d["audio"] = new JsonObject { ["sounds"] = new JsonObject { ["move"] = "asset:x/click.png" } }, "sound");
        AssertRejected(d => d["menus"]!["features"]!["audio"] = Audio(new JsonObject { ["source"] = "file", ["file"] = "nope" }), "features");
    }

    [Theory]
    [InlineData("none", 0)]
    [InlineData("fade", 300)]
    [InlineData("slide", 2000)]
    [InlineData("rise", 150)]
    [InlineData("zoom", 500)]
    [InlineData("wipe", 400)]
    public void AcceptsTransitions(string style, int ms) =>
        Load(d => d["layout"] = new JsonObject { ["transition"] = new JsonObject { ["style"] = style, ["durationMs"] = ms } });

    [Fact]
    public void RejectsBadTransitions()
    {
        AssertRejected(d => d["layout"] = new JsonObject { ["transition"] = new JsonObject { ["style"] = "explode" } }, "transition style");
        AssertRejected(d => d["layout"] = new JsonObject { ["transition"] = new JsonObject { ["style"] = "fade", ["durationMs"] = 9000 } }, "durationMs");
        AssertRejected(d => d["menus"]!["main"]!["layout"] = new JsonObject { ["transition"] = new JsonObject { ["style"] = "boom" } }, "main");
    }

    private static JsonObject Tmdb(string? path, string? size = null)
    {
        var bg = new JsonObject { ["source"] = "tmdb" };
        if (path is not null)
        {
            bg["tmdbFilePath"] = path;
        }

        if (size is not null)
        {
            bg["tmdbSize"] = size;
        }

        return bg;
    }

    [Theory]
    [InlineData("/6YozDnrA2fXdIbB5nWDFHtB5ZY6.jpg", null)]
    [InlineData("/abc_DEF-123.png", "w780")]
    [InlineData("/abc.jpg", "w1280")]
    [InlineData("/abc.jpg", "original")]
    public void AcceptsTmdbBackgrounds(string path, string? size)
    {
        Load(d => d["background"] = Tmdb(path, size));
        Load(d => d["menus"]!["features"]!["background"] = Tmdb(path, size));
    }

    [Theory]
    [InlineData("/abc.svg")]
    [InlineData("/../etc/passwd.jpg")]
    [InlineData("https://evil.example/a.jpg")]
    [InlineData("abc.jpg")]
    [InlineData("/a/b.jpg")]
    [InlineData("/abc.jpg?x=1")]
    [InlineData("")]
    public void RejectsBadTmdbPaths(string path) =>
        AssertRejected(d => d["background"] = Tmdb(path), "tmdbFilePath");

    [Fact]
    public void RejectsTmdbWithoutPathAndBadSize()
    {
        AssertRejected(d => d["background"] = Tmdb(null), "tmdbFilePath");
        AssertRejected(d => d["background"] = Tmdb("/abc.jpg", "w99999"), "tmdbSize");
        AssertRejected(d => d["menus"]!["main"]!["background"] = Tmdb("/x.gif"), "main");
    }
}
