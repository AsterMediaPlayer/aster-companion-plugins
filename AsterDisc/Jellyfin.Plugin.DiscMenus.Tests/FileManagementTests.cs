using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>Duplicate, delete, backups and restore in the Menu Editor, plus the two small helpers they rely on.</summary>
public sealed class FileManagementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-files-" + Guid.NewGuid().ToString("N"));
    private readonly string _menus;
    private readonly string _backups;
    private readonly MenuFileEditor _editor;

    public FileManagementTests()
    {
        _menus = Path.Combine(_dir, "menus");
        _backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(Path.Combine(_menus, "assets"));
        _editor = new MenuFileEditor(_menus, Path.Combine(_menus, "assets"), _backups);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string ExampleJson([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "examples", "example.menu.json")));

    private string Put(string relative, string? content = null)
    {
        var path = Path.Combine(_menus, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content ?? ExampleJson());
        return path;
    }

    private static JsonObject Doc(string json) => JsonNode.Parse(json)!.AsObject();

    // ---- JsonSplice
    [Fact]
    public void SpliceChangesOnlyTheValue()
    {
        const string text = "{\r\n  \"menuId\" : \"old\",\r\n  \"revision\":\t3, \"nested\":{\"revision\":9}\r\n}";
        var once = JsonSplice.ReplaceTopLevel(text, "revision", "4");
        Assert.Equal(text.Replace("\t3,", "\t4,"), once);
        Assert.Equal(text.Replace("\"old\"", "\"new\""), JsonSplice.ReplaceTopLevel(text, "menuId", "\"new\""));
    }

    [Theory]
    [InlineData("[1]", "revision")]
    [InlineData("{\"a\":1}", "revision")]
    [InlineData("{\"revision\":{\"x\":1}}", "revision")]
    [InlineData("{\"revision\":", "revision")]
    public void SpliceRefusesWhatItCannotDoSafely(string text, string name) =>
        Assert.ThrowsAny<JsonException>(() => JsonSplice.ReplaceTopLevel(text, name, "1"));

    // ---- MenuBlankBuilder
    [Theory]
    [InlineData("Movie", null)]
    [InlineData("Series", null)]
    [InlineData("Season", 2)]
    public void ABlankMenuIsValid(string type, int? season)
    {
        var menu = MenuBlankBuilder.Build(type, new Dictionary<string, string> { ["Tmdb"] = "603", ["Imdb"] = "tt0133093" }, 1999, season);
        var json = menu.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Assert.Empty(MenuFileEditor.Validate(json));
        Assert.Equal(1, (int)menu["revision"]!);
        Assert.Equal(type, (string)menu["match"]!["itemType"]!);
    }

    [Fact]
    public void ABlankMenuNeedsAnIdToMatchOn()
    {
        Assert.Throws<ArgumentException>(() => MenuBlankBuilder.Build("Movie", new Dictionary<string, string>(), 2000));
        Assert.Throws<ArgumentException>(() => MenuBlankBuilder.Build("Episode", new Dictionary<string, string> { ["Tmdb"] = "1" }, 2000));
    }

    // ---- Duplicate
    [Fact]
    public void ACopyHasAFreshIdAndRevisionOneAndLeavesTheOriginalAlone()
    {
        var original = ExampleJson();
        var path = Put("a.menu.json", original);
        var result = _editor.Duplicate("a.menu.json");
        Assert.Equal(EditorResultKind.Ok, result.Kind);
        Assert.Equal("a-copy.menu.json", result.File);
        Assert.Equal(original, File.ReadAllText(path));

        var copy = Doc(File.ReadAllText(Path.Combine(_menus, "a-copy.menu.json")));
        Assert.NotEqual((string)Doc(original)["menuId"]!, (string)copy["menuId"]!);
        Assert.Equal(1, (int)copy["revision"]!);
        Assert.Equal(Doc(original)["menus"]!.ToJsonString(), copy["menus"]!.ToJsonString());
    }

    [Fact]
    public void ACopyNeverOverwritesAnExistingFile()
    {
        Put("a.menu.json");
        Put("a-copy.menu.json", "keep me");
        var result = _editor.Duplicate("a.menu.json");
        Assert.Equal("a-copy-2.menu.json", result.File);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_menus, "a-copy.menu.json")));
        Assert.Equal("a-copy-3.menu.json", _editor.Duplicate("a.menu.json").File);
    }

    [Fact]
    public void ACopyStaysInItsFolderExceptForCatalogueMenus()
    {
        Put("mine/b.menu.json");
        Put("catalogue/c.menu.json");
        Assert.Equal("mine/b-copy.menu.json", _editor.Duplicate("mine/b.menu.json").File);
        Assert.Equal("c-copy.menu.json", _editor.Duplicate("catalogue/c.menu.json").File);
    }

    [Theory]
    [InlineData("../x.menu.json")]
    [InlineData("/etc/passwd")]
    [InlineData("a.txt")]
    [InlineData(null)]
    public void BadNamesAreRefusedEverywhere(string? name)
    {
        Assert.NotEqual(EditorResultKind.Ok, _editor.Duplicate(name).Kind);
        Assert.NotEqual(EditorResultKind.Ok, _editor.DeleteUserMenu(name).Kind);
        Assert.NotEqual(EditorResultKind.Ok, _editor.Restore(name, "20260101000000000", null).Kind);
        Assert.Empty(_editor.ListBackups(name));
    }

    [Fact]
    public void AnInvalidMenuIsNotCopied()
    {
        Put("bad.menu.json", "{\"schemaVersion\":1}");
        Assert.Equal(EditorResultKind.Invalid, _editor.Duplicate("bad.menu.json").Kind);
        Assert.False(File.Exists(Path.Combine(_menus, "bad-copy.menu.json")));
    }

    // ---- Delete
    [Fact]
    public void DeletingKeepsABackupAndASaveAfterwardDoesNotRecreateTheFile()
    {
        var path = Put("a.menu.json");
        var version = _editor.Read("a.menu.json").Version;
        Assert.Equal(EditorResultKind.Ok, _editor.DeleteUserMenu("a.menu.json").Kind);
        Assert.False(File.Exists(path));
        Assert.Single(_editor.ListBackups("a.menu.json"));
        Assert.NotEqual(EditorResultKind.Ok, _editor.Save("a.menu.json", ExampleJson(), version).Kind);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CatalogueMenusAreNotDeletedHere()
    {
        var path = Put("catalogue/c.menu.json");
        var result = _editor.DeleteUserMenu("catalogue/c.menu.json");
        Assert.NotEqual(EditorResultKind.Ok, result.Kind);
        Assert.Contains("Catalogue", result.Error);
        Assert.True(File.Exists(path));
    }

    // ---- Backups and restore
    [Fact]
    public void RestoreWritesANewRevisionWithTheCurrentIdAndBacksUpWhatItReplaces()
    {
        Put("a.menu.json");
        var v1 = _editor.Read("a.menu.json");
        var oldBody = v1.Json!;

        var edited = Doc(oldBody);
        edited["revision"] = 2;
        edited["menus"]!["main"]!["title"] = "Edited";
        var saved = _editor.Save("a.menu.json", edited.ToJsonString(), v1.Version);
        Assert.Equal(EditorResultKind.Ok, saved.Kind);

        var backups = _editor.ListBackups("a.menu.json");
        Assert.Single(backups);
        Assert.Equal(1, backups[0].Revision);

        var restored = _editor.Restore("a.menu.json", backups[0].Stamp, saved.Version);
        Assert.Equal(EditorResultKind.Ok, restored.Kind);

        var now = Doc(File.ReadAllText(Path.Combine(_menus, "a.menu.json")));
        Assert.Equal(3, (int)now["revision"]!);
        Assert.Equal((string)Doc(oldBody)["menuId"]!, (string)now["menuId"]!);
        Assert.Equal(Doc(oldBody)["menus"]!.ToJsonString(), now["menus"]!.ToJsonString());
        Assert.Equal(2, _editor.ListBackups("a.menu.json").Count);
    }

    [Fact]
    public void RestoreKeepsTheCurrentIdEvenIfTheBackupHadAnother()
    {
        Put("a.menu.json");
        var current = _editor.Read("a.menu.json");
        var foreign = Doc(current.Json!);
        foreign["menuId"] = Guid.NewGuid().ToString();
        foreign["revision"] = 7;
        Directory.CreateDirectory(_backups);
        File.WriteAllText(Path.Combine(_backups, "a.menu.json.20260101000000000.bak"), foreign.ToJsonString());

        var restored = _editor.Restore("a.menu.json", "20260101000000000", current.Version);
        Assert.Equal(EditorResultKind.Ok, restored.Kind);
        var now = Doc(File.ReadAllText(Path.Combine(_menus, "a.menu.json")));
        Assert.Equal((string)Doc(current.Json!)["menuId"]!, (string)now["menuId"]!);
        Assert.Equal((int)Doc(current.Json!)["revision"]! + 1, (int)now["revision"]!);
    }

    [Fact]
    public void RestoreRefusesAFileThatChangedUnderTheEditor()
    {
        Put("a.menu.json");
        var v1 = _editor.Read("a.menu.json");
        var edited = Doc(v1.Json!);
        edited["revision"] = 2;
        Assert.Equal(EditorResultKind.Ok, _editor.Save("a.menu.json", edited.ToJsonString(), v1.Version).Kind);
        var stamp = _editor.ListBackups("a.menu.json")[0].Stamp;

        Assert.Equal(EditorResultKind.Conflict, _editor.Restore("a.menu.json", stamp, "0000000000000000").Kind);
        Assert.Equal(2, (int)Doc(File.ReadAllText(Path.Combine(_menus, "a.menu.json")))["revision"]!);
    }

    [Theory]
    [InlineData("../../x")]
    [InlineData("2026")]
    [InlineData("20260101000000000/../a")]
    [InlineData(null)]
    public void RestoreOnlyAcceptsARealBackupStamp(string? stamp)
    {
        Put("a.menu.json");
        Assert.NotEqual(EditorResultKind.Ok, _editor.Restore("a.menu.json", stamp, null).Kind);
    }

    [Fact]
    public void RestoreOfAMissingBackupIsNotFound()
    {
        Put("a.menu.json");
        Assert.Equal(EditorResultKind.NotFound, _editor.Restore("a.menu.json", "20200101000000000", null).Kind);
    }

    [Fact]
    public void BackupsOfOtherFilesAreNotListed()
    {
        Put("a.menu.json");
        Put("a.menu.json.menu.json");
        Directory.CreateDirectory(_backups);
        File.WriteAllText(Path.Combine(_backups, "a.menu.json.20260101000000000.bak"), ExampleJson());
        File.WriteAllText(Path.Combine(_backups, "a.menu.json.menu.json.20260101000000001.bak"), ExampleJson());
        File.WriteAllText(Path.Combine(_backups, "a.menu.json.notastamp.bak"), "x");
        var listed = _editor.ListBackups("a.menu.json");
        Assert.Equal("20260101000000000", Assert.Single(listed).Stamp);
    }

    [Fact]
    public void OnlyTheNewestBackupsAreKept()
    {
        Put("a.menu.json");
        var version = _editor.Read("a.menu.json").Version;
        var doc = Doc(ExampleJson());
        for (var i = 2; i < 40; i++)
        {
            doc["revision"] = i;
            var saved = _editor.Save("a.menu.json", doc.ToJsonString(), version);
            Assert.Equal(EditorResultKind.Ok, saved.Kind);
            version = saved.Version;
            Thread.Sleep(2);
        }

        var all = _editor.ListBackups("a.menu.json");
        Assert.Equal(25, all.Count);
        Assert.Equal(all.OrderByDescending(b => b.Stamp, StringComparer.Ordinal).Select(b => b.Stamp), all.Select(b => b.Stamp));
    }

    [Fact]
    public void BackupsMadeInTheSameMillisecondDoNotOverwriteEachOther()
    {
        Put("a.menu.json");
        var version = _editor.Read("a.menu.json").Version;
        var doc = Doc(ExampleJson());
        for (var i = 2; i < 12; i++)
        {
            doc["revision"] = i;
            var saved = _editor.Save("a.menu.json", doc.ToJsonString(), version);
            Assert.Equal(EditorResultKind.Ok, saved.Kind);
            version = saved.Version;
        }

        Assert.Equal(10, _editor.ListBackups("a.menu.json").Count);
    }
}
