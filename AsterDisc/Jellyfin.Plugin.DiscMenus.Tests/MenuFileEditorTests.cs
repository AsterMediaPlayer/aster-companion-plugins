using System.Text.Json.Nodes;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

public sealed class MenuFileEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-editor-" + Guid.NewGuid().ToString("N"));
    private readonly string _menus;
    private readonly string _assets;
    private readonly string _backups;
    private readonly MenuFileEditor _editor;

    public MenuFileEditorTests()
    {
        _menus = Path.Combine(_dir, "menus");
        _assets = Path.Combine(_menus, "assets");
        _backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(_assets);
        _editor = new MenuFileEditor(_menus, _assets, _backups);
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

    private static string Edit(Action<JsonObject> change, string? from = null)
    {
        var doc = JsonNode.Parse(from ?? ExampleJson())!.AsObject();
        change(doc);
        return doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    // ---- names: everything the client sends goes through Resolve
    [Theory]
    [InlineData("thor.menu.json")]
    [InlineData("sub/dir/thor (2008).menu.json")]
    [InlineData("Amélie_2001-final.menu.json")]
    public void AcceptsOrdinaryMenuNames(string name) =>
        Assert.Equal(EditorResultKind.Ok, _editor.Resolve(name).Kind);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("thor.json")]
    [InlineData("thor.menu.json.bak")]
    [InlineData(".menu.json")]
    [InlineData("../outside.menu.json")]
    [InlineData("sub/../../outside.menu.json")]
    [InlineData("./thor.menu.json")]
    [InlineData("sub/./thor.menu.json")]
    [InlineData("/etc/passwd.menu.json")]
    [InlineData("/thor.menu.json")]
    [InlineData("sub//thor.menu.json")]
    [InlineData("..\\outside.menu.json")]
    [InlineData("sub\\thor.menu.json")]
    [InlineData("C:thor.menu.json")]
    [InlineData("~/thor.menu.json")]
    [InlineData(".hidden.menu.json")]
    [InlineData(".git/x.menu.json")]
    [InlineData("a\0b.menu.json")]
    [InlineData("thor.menu.json\0.png")]
    [InlineData("assets/thor.menu.json")]
    [InlineData("assets/sub/thor.menu.json")]
    [InlineData("a<b>.menu.json")]
    [InlineData("a;b.menu.json")]
    [InlineData("a%2e%2e/b.menu.json")]
    public void RejectsEverythingThatCouldEscapeOrIsNotAMenu(string? name) =>
        Assert.Equal(EditorResultKind.BadName, _editor.Resolve(name).Kind);

    [Fact]
    public void RejectsOverlongNames() =>
        Assert.Equal(EditorResultKind.BadName, _editor.Resolve(new string('a', 250) + ".menu.json").Kind);

    [Fact]
    public void ResolvedPathsAreAlwaysInsideTheMenusFolder()
    {
        foreach (var name in new[] { "a.menu.json", "x/y/z.menu.json", "dots.in.name.menu.json" })
        {
            var (_, full, _) = _editor.Resolve(name);
            Assert.StartsWith(Path.GetFullPath(_menus) + Path.DirectorySeparatorChar, full);
        }
    }

    [Fact]
    public void ASymlinkLeadingOutsideTheFolderIsRefused()
    {
        var outside = Path.Combine(_dir, "secret.menu.json");
        File.WriteAllText(outside, ExampleJson());
        File.CreateSymbolicLink(Path.Combine(_menus, "link.menu.json"), outside);
        Assert.Equal(EditorResultKind.BadName, _editor.Resolve("link.menu.json").Kind);
        Assert.Equal(EditorResultKind.BadName, _editor.Read("link.menu.json").Kind);
        Assert.Equal(EditorResultKind.BadName, _editor.Save("link.menu.json", ExampleJson(), "x").Kind);
    }

    [Fact]
    public void ASymlinkStayingInsideTheFolderIsAllowed()
    {
        Put("real.menu.json");
        File.CreateSymbolicLink(Path.Combine(_menus, "alias.menu.json"), Path.Combine(_menus, "real.menu.json"));
        Assert.Equal(EditorResultKind.Ok, _editor.Resolve("alias.menu.json").Kind);
    }

    // ---- list / read
    [Fact]
    public void ListsMenuFilesRecursivelyExcludingAssetsAndOtherFiles()
    {
        Put("b.menu.json");
        Put("a.menu.json");
        Put("sub/c.menu.json");
        Put("assets/art.menu.json");          // art folder: never a menu
        File.WriteAllText(Path.Combine(_menus, "x.binding.json"), "{}");
        File.WriteAllText(Path.Combine(_menus, "notes.txt"), "hi");
        var files = _editor.List();
        Assert.Equal(new[] { "a.menu.json", "b.menu.json", "sub/c.menu.json" }, files.Select(f => f.File));
        Assert.All(files, f => Assert.Equal(16, f.Version.Length));
    }

    [Fact]
    public void ListIsEmptyWhenTheFolderIsMissing() =>
        Assert.Empty(new MenuFileEditor(Path.Combine(_dir, "nope"), Path.Combine(_dir, "nope", "assets"), _backups).List());

    [Fact]
    public void ReadReturnsContentAndAVersionThatTracksTheBytes()
    {
        Put("a.menu.json");
        var first = _editor.Read("a.menu.json");
        Assert.Equal(EditorResultKind.Ok, first.Kind);
        Assert.Equal(ExampleJson(), first.Json);
        File.AppendAllText(Path.Combine(_menus, "a.menu.json"), " ");
        Assert.NotEqual(first.Version, _editor.Read("a.menu.json").Version);
        Assert.Equal(EditorResultKind.NotFound, _editor.Read("missing.menu.json").Kind);
    }

    [Fact]
    public void ReadStripsAByteOrderMark()
    {
        File.WriteAllBytes(Path.Combine(_menus, "bom.menu.json"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(ExampleJson())).ToArray());
        Assert.Equal(ExampleJson(), _editor.Read("bom.menu.json").Json);
    }

    [Fact]
    public void TooLargeFilesAreNotReadOrListed()
    {
        File.WriteAllText(Path.Combine(_menus, "big.menu.json"), new string('x', MenuFileEditor.MaxBytes + 1));
        Assert.Equal(EditorResultKind.TooLarge, _editor.Read("big.menu.json").Kind);
        Assert.Empty(_editor.List());
    }

    // ---- validation
    [Fact]
    public void ValidMenusHaveNoErrors() => Assert.Empty(MenuFileEditor.Validate(ExampleJson()));

    [Fact]
    public void SyntaxErrorsReportTheLineAndColumn()
    {
        var broken = ExampleJson().Replace("\"revision\": 1,", "\"revision\": 1 \"oops\": 2,");
        var error = Assert.Single(MenuFileEditor.Validate(broken));
        Assert.NotNull(error.Line);
        Assert.NotNull(error.Column);
        Assert.Equal(broken.Split('\n').ToList().FindIndex(l => l.Contains("oops")) + 1, error.Line);
    }

    [Fact]
    public void SemanticProblemsAreListedByTheRealLoader()
    {
        var bad = Edit(d => d["menus"]!["main"]!["entries"]!.AsArray().Add(new JsonObject { ["action"] = "submenu", ["label"] = "x", ["menu"] = "nowhere" }));
        Assert.Contains(MenuFileEditor.Validate(bad), e => e.Message.Contains("nowhere") && e.Line is null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    public void GarbageIsReportedNeverThrown(string json) => Assert.NotEmpty(MenuFileEditor.Validate(json));

    [Fact]
    public void EditingRulesProtectBindings()
    {
        var original = ExampleJson();
        Assert.Contains(MenuFileEditor.Validate(Edit(d => d["menuId"] = Guid.NewGuid().ToString()), original), e => e.Message.Contains("menuId"));
        Assert.Contains(MenuFileEditor.Validate(Edit(d => d["revision"] = 0), original), e => e.Message.Contains("revision"));
        Assert.Empty(MenuFileEditor.Validate(Edit(d => d["revision"] = 5), original));
        // the same menuId in a different case is the same id
        Assert.Empty(MenuFileEditor.Validate(Edit(d => d["menuId"] = d["menuId"]!.GetValue<string>().ToUpperInvariant()), original));
    }

    [Fact]
    public void OversizedJsonIsRefusedBeforeParsing() =>
        Assert.Contains("too large", Assert.Single(MenuFileEditor.Validate(new string(' ', MenuFileEditor.MaxBytes + 1))).Message);

    // ---- save
    [Fact]
    public void SaveWritesTheFileKeepsABackupAndReturnsTheNewVersion()
    {
        var path = Put("a.menu.json");
        var before = _editor.Read("a.menu.json");
        var edited = Edit(d => { d["revision"] = 2; d["meta"]!["notes"] = "edited"; });
        var result = _editor.Save("a.menu.json", edited, before.Version);
        Assert.Equal(EditorResultKind.Ok, result.Kind);
        Assert.Equal(edited, File.ReadAllText(path));
        Assert.Equal(result.Version, _editor.Read("a.menu.json").Version);
        var backup = Assert.Single(Directory.GetFiles(_backups));
        Assert.Equal(ExampleJson(), File.ReadAllText(backup));
        Assert.Matches(@"^a\.menu\.json\.\d{17}\.bak$", Path.GetFileName(backup));
        Assert.Empty(Directory.GetFiles(_menus, "*.tmp-*"));
    }

    [Fact]
    public void SaveWithAStaleVersionIsAConflictAndChangesNothing()
    {
        var path = Put("a.menu.json");
        var stale = _editor.Read("a.menu.json").Version;
        File.WriteAllText(path, Edit(d => d["meta"]!["notes"] = "changed on disk by someone else"));
        var onDisk = File.ReadAllText(path);
        var result = _editor.Save("a.menu.json", Edit(d => d["revision"] = 2), stale);
        Assert.Equal(EditorResultKind.Conflict, result.Kind);
        Assert.Equal(_editor.Read("a.menu.json").Version, result.Version);
        Assert.Equal(onDisk, File.ReadAllText(path));
        Assert.False(Directory.Exists(_backups) && Directory.GetFiles(_backups).Length > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SaveWithoutAVersionIsRefused(string? version)
    {
        Put("a.menu.json");
        Assert.Equal(EditorResultKind.Conflict, _editor.Save("a.menu.json", ExampleJson(), version).Kind);
    }

    [Fact]
    public void InvalidMenusAreNeverWritten()
    {
        var path = Put("a.menu.json");
        var version = _editor.Read("a.menu.json").Version;
        var result = _editor.Save("a.menu.json", "{ not json", version);
        Assert.Equal(EditorResultKind.Invalid, result.Kind);
        Assert.NotEmpty(result.Errors!);
        Assert.Equal(ExampleJson(), File.ReadAllText(path));
        Assert.False(Directory.Exists(_backups) && Directory.GetFiles(_backups).Length > 0);
    }

    [Fact]
    public void ChangingTheMenuIdOrLoweringTheRevisionIsRefusedOnSave()
    {
        Put("a.menu.json");
        var v = _editor.Read("a.menu.json").Version;
        Assert.Equal(EditorResultKind.Invalid, _editor.Save("a.menu.json", Edit(d => d["menuId"] = Guid.NewGuid().ToString()), v).Kind);
        Assert.Equal(EditorResultKind.Invalid, _editor.Save("a.menu.json", Edit(d => d["revision"] = 0), v).Kind);
    }

    [Fact]
    public void SavingIdenticalContentIsANoOpWithoutABackup()
    {
        Put("a.menu.json");
        var v = _editor.Read("a.menu.json").Version;
        var result = _editor.Save("a.menu.json", ExampleJson(), v);
        Assert.Equal(EditorResultKind.Ok, result.Kind);
        Assert.Equal(v, result.Version);
        Assert.False(Directory.Exists(_backups) && Directory.GetFiles(_backups).Length > 0);
    }

    [Fact]
    public void SaveCannotCreateNewFilesOrEscapeTheFolder()
    {
        Assert.Equal(EditorResultKind.NotFound, _editor.Save("new.menu.json", ExampleJson(), "x").Kind);
        Assert.Equal(EditorResultKind.BadName, _editor.Save("../evil.menu.json", ExampleJson(), "x").Kind);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.menu.json")));
        Assert.False(File.Exists(Path.Combine(_menus, "new.menu.json")));
    }

    [Fact]
    public void OversizedSaveIsRefused()
    {
        Put("a.menu.json");
        var v = _editor.Read("a.menu.json").Version;
        Assert.Equal(EditorResultKind.TooLarge, _editor.Save("a.menu.json", new string(' ', MenuFileEditor.MaxBytes + 1), v).Kind);
    }

    [Fact]
    public void OnlyTheNewestBackupsAreKeptAndFilesNeverShareBackups()
    {
        Put("a.menu.json");
        Put("sub/a.menu.json");
        for (var i = 2; i < 2 + 30; i++)
        {
            var v = _editor.Read("a.menu.json").Version;
            Assert.Equal(EditorResultKind.Ok, _editor.Save("a.menu.json", Edit(d => d["revision"] = i), v).Kind);
            Thread.Sleep(2);
        }

        var sv = _editor.Read("sub/a.menu.json").Version;
        Assert.Equal(EditorResultKind.Ok, _editor.Save("sub/a.menu.json", Edit(d => d["revision"] = 2), sv).Kind);
        var names = Directory.GetFiles(_backups).Select(Path.GetFileName).ToList();
        Assert.Equal(25, names.Count(n => n!.StartsWith("a.menu.json.", StringComparison.Ordinal)));
        Assert.Equal(1, names.Count(n => n!.StartsWith("sub~a.menu.json.", StringComparison.Ordinal)));
    }

    [Fact]
    public void SequentialEditsWithTheReturnedVersionKeepWorking()
    {
        Put("a.menu.json");
        var version = _editor.Read("a.menu.json").Version!;
        for (var i = 2; i <= 5; i++)
        {
            var result = _editor.Save("a.menu.json", Edit(d => d["revision"] = i), version);
            Assert.Equal(EditorResultKind.Ok, result.Kind);
            version = result.Version!;
        }

        Assert.Equal(5, JsonNode.Parse(_editor.Read("a.menu.json").Json!)!["revision"]!.GetValue<int>());
    }
}

public sealed class MenuFileEditorCreateDeleteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-editor-cd-" + Guid.NewGuid().ToString("N"));
    private readonly string _menus;
    private readonly string _backups;
    private readonly MenuFileEditor _editor;

    public MenuFileEditorCreateDeleteTests()
    {
        _menus = Path.Combine(_dir, "menus");
        _backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(Path.Combine(_menus, "assets"));
        _editor = new MenuFileEditor(_menus, Path.Combine(_menus, "assets"), _backups);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string ExampleJson([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "examples", "example.menu.json")));

    [Fact]
    public void CreatesANewFileInASubfolder()
    {
        var r = _editor.Create("sub/new.menu.json", ExampleJson());
        Assert.Equal(EditorResultKind.Ok, r.Kind);
        Assert.True(File.Exists(Path.Combine(_menus, "sub", "new.menu.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_menus, "sub"), "*.tmp-*"));
    }

    [Fact]
    public void NeverOverwrites()
    {
        _editor.Create("a.menu.json", ExampleJson());
        Assert.Equal(EditorResultKind.Conflict, _editor.Create("a.menu.json", ExampleJson()).Kind);
    }

    [Theory]
    [InlineData("../x.menu.json")]
    [InlineData("sub/../../x.menu.json")]
    [InlineData("/etc/x.menu.json")]
    [InlineData("assets/x.menu.json")]
    [InlineData(".hidden.menu.json")]
    [InlineData("x.json")]
    [InlineData("")]
    public void RefusesNamesOutsideTheMenusFolder(string name)
    {
        Assert.Equal(EditorResultKind.BadName, _editor.Create(name, ExampleJson()).Kind);
        Assert.Equal(EditorResultKind.BadName, _editor.Delete(name).Kind);
        Assert.False(File.Exists(Path.Combine(_dir, "x.menu.json")));
    }

    [Fact]
    public void RefusesAMenuThatWouldNotLoad()
    {
        var r = _editor.Create("bad.menu.json", "{ nope");
        Assert.Equal(EditorResultKind.Invalid, r.Kind);
        Assert.False(File.Exists(Path.Combine(_menus, "bad.menu.json")));
    }

    [Fact]
    public void DeleteKeepsABackup()
    {
        _editor.Create("a.menu.json", ExampleJson());
        Assert.Equal(EditorResultKind.Ok, _editor.Delete("a.menu.json").Kind);
        Assert.False(File.Exists(Path.Combine(_menus, "a.menu.json")));
        Assert.Single(Directory.GetFiles(_backups));
        Assert.Equal(EditorResultKind.NotFound, _editor.Delete("a.menu.json").Kind);
    }
}
