using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.DiscMenus.Api;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>Installing, upgrading and removing menus from the catalogue: what must be refused matters most.</summary>
public sealed class CatalogueStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-cat-" + Guid.NewGuid().ToString("N"));
    private readonly string _menus;
    private readonly string _backups;
    private readonly CatalogueStore _store;

    public CatalogueStoreTests()
    {
        _menus = Path.Combine(_dir, "menus");
        _backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(Path.Combine(_menus, "assets"));
        _store = new CatalogueStore(_menus, Path.Combine(_menus, "assets"), _backups);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly Guid Id = Guid.Parse("0b311cdd-e7c2-4e27-b4bc-fb1e5acebfc8");

    private static string ExampleJson([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "examples", "static-backdrop.menu.json")));

    private static byte[] Menu(int revision = 1, Guid? id = null, Action<JsonObject>? change = null)
    {
        var doc = JsonNode.Parse(ExampleJson())!.AsObject();
        doc["menuId"] = (id ?? Id).ToString("D");
        doc["revision"] = revision;
        change?.Invoke(doc);
        return new UTF8Encoding(false).GetBytes(doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private CatalogueResult Install(byte[] bytes) => _store.Install(bytes, CatalogueStore.Sha256Of(bytes));

    private string MenuFile(Guid? id = null) => Path.Combine(_menus, "catalogue", $"{id ?? Id:D}.menu.json");

    private string SourceFile(Guid? id = null) => Path.Combine(_menus, "catalogue", $"{id ?? Id:D}.source.json");

    private string[] Backups() => Directory.Exists(_backups) ? Directory.GetFiles(_backups) : Array.Empty<string>();

    [Fact]
    public void InstallsANewMenuAndRemembersWhatItInstalled()
    {
        var bytes = Menu();
        var result = Install(bytes);

        Assert.Equal(CatalogueResultKind.Ok, result.Kind);
        Assert.Equal("installed", result.Action);
        Assert.Equal(bytes, File.ReadAllBytes(MenuFile()));
        var listed = Assert.Single(_store.List());
        Assert.Equal(Id, listed.MenuId);
        Assert.Equal(1, listed.Revision);
        Assert.False(listed.EditedLocally);
        Assert.Equal(CatalogueStore.Sha256Of(bytes), listed.Sha256);
        Assert.Empty(Directory.GetFiles(Path.Combine(_menus, "catalogue"), "*.tmp-*"));
    }

    [Fact]
    public void InstallingTheSameFileAgainChangesNothing()
    {
        var bytes = Menu();
        Install(bytes);
        var again = Install(bytes);
        Assert.Equal(CatalogueResultKind.Ok, again.Kind);
        Assert.Equal("unchanged", again.Action);
        Assert.Empty(Backups());
    }

    [Fact]
    public void AFileThatDoesNotMatchItsFingerprintIsNotWritten()
    {
        var bytes = Menu();
        var tampered = (byte[])bytes.Clone();
        tampered[^3] ^= 1;
        var result = _store.Install(tampered, CatalogueStore.Sha256Of(bytes));
        Assert.Equal(CatalogueResultKind.HashMismatch, result.Kind);
        Assert.False(Directory.Exists(Path.Combine(_menus, "catalogue")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void AFingerprintIsRequired(string? sha)
    {
        Assert.Equal(CatalogueResultKind.BadRequest, _store.Install(Menu(), sha).Kind);
        Assert.False(Directory.Exists(Path.Combine(_menus, "catalogue")));
    }

    [Fact]
    public void TheFingerprintMayBeUpperCase()
    {
        var bytes = Menu();
        Assert.Equal(CatalogueResultKind.Ok, _store.Install(bytes, CatalogueStore.Sha256Of(bytes).ToUpperInvariant()).Kind);
    }

    [Fact]
    public void AnEmptyOrOversizedBodyIsRefused()
    {
        Assert.Equal(CatalogueResultKind.BadRequest, _store.Install(Array.Empty<byte>(), new string('a', 64)).Kind);
        var big = new byte[CatalogueStore.MaxBytes + 1];
        Assert.Equal(CatalogueResultKind.TooLarge, _store.Install(big, CatalogueStore.Sha256Of(big)).Kind);
    }

    [Fact]
    public void ANotUtf8FileIsRefused()
    {
        var bytes = new byte[] { 0xFF, 0xFE, 0x7B, 0x7D };
        Assert.Equal(CatalogueResultKind.BadRequest, Install(bytes).Kind);
    }

    [Fact]
    public void AMenuThePluginWouldNotLoadIsRefusedEvenWithAMatchingFingerprint()
    {
        var bytes = Menu(change: d => d["menus"]!.AsObject().First().Value!["entries"]![0]!["label"] = "<b>bold</b>");
        var result = Install(bytes);
        Assert.Equal(CatalogueResultKind.Invalid, result.Kind);
        Assert.NotEmpty(result.Errors!);
        Assert.False(File.Exists(MenuFile()));
        Assert.Equal(CatalogueResultKind.Invalid, Install(Encoding.UTF8.GetBytes("{ nope")).Kind);
    }

    [Fact]
    public void AHandMadeMenuWithTheSameIdIsNeverOverwrittenOrDuplicated()
    {
        File.WriteAllBytes(Path.Combine(_menus, "my-own.menu.json"), Menu(revision: 5));
        var result = Install(Menu(revision: 9));
        Assert.Equal(CatalogueResultKind.Refused, result.Kind);
        Assert.Contains("my-own.menu.json", result.Message);
        Assert.False(File.Exists(MenuFile()));
        Assert.Equal(Menu(revision: 5), File.ReadAllBytes(Path.Combine(_menus, "my-own.menu.json")));
    }

    [Fact]
    public void ADuplicateInAnotherSubfolderCountsToo()
    {
        Directory.CreateDirectory(Path.Combine(_menus, "mine"));
        File.WriteAllBytes(Path.Combine(_menus, "mine", "x.menu.json"), Menu());
        Assert.Equal(CatalogueResultKind.Refused, Install(Menu(revision: 2)).Kind);
    }

    [Fact]
    public void ANewerRevisionUpgradesAndKeepsABackup()
    {
        Install(Menu(1));
        var v2 = Menu(2);
        var result = Install(v2);
        Assert.Equal("upgraded", result.Action);
        Assert.Equal(2, result.Revision);
        Assert.Equal(v2, File.ReadAllBytes(MenuFile()));
        Assert.Single(Backups());
        var listed = Assert.Single(_store.List());
        Assert.Equal(2, listed.Revision);
        Assert.False(listed.EditedLocally);
    }

    [Fact]
    public void AnOlderRevisionIsRefused()
    {
        Install(Menu(3));
        var result = Install(Menu(2));
        Assert.Equal(CatalogueResultKind.Refused, result.Kind);
        Assert.Equal(Menu(3), File.ReadAllBytes(MenuFile()));
    }

    [Fact]
    public void TheSameRevisionWithDifferentContentIsRefused()
    {
        Install(Menu(1));
        var different = Menu(1, change: d => d["meta"]!["notes"] = "changed without a new revision");
        Assert.Equal(CatalogueResultKind.Refused, Install(different).Kind);
    }

    [Fact]
    public void AMenuEditedHereIsNotOverwrittenByAnUpgrade()
    {
        Install(Menu(1));
        File.AppendAllText(MenuFile(), " ");
        var listed = Assert.Single(_store.List());
        Assert.True(listed.EditedLocally);

        var result = Install(Menu(2));
        Assert.Equal(CatalogueResultKind.Refused, result.Kind);
        Assert.Contains("edited", result.Message);
        Assert.EndsWith(" ", File.ReadAllText(MenuFile()));
    }

    [Fact]
    public void AFileWithoutASourceRecordIsNotOverwritten()
    {
        Install(Menu(1));
        File.Delete(SourceFile());
        Assert.Equal(CatalogueResultKind.Refused, Install(Menu(2)).Kind);
        Assert.Empty(_store.List());
    }

    [Fact]
    public void ADamagedSourceRecordIsTreatedAsMissing()
    {
        Install(Menu(1));
        File.WriteAllText(SourceFile(), "{\"menuId\":\"" + Id + "\",\"revision\":1,\"sha256\":\"short\"}");
        Assert.Equal(CatalogueResultKind.Refused, Install(Menu(2)).Kind);
        File.WriteAllText(SourceFile(), "not json");
        Assert.Empty(_store.List());
    }

    [Fact]
    public void UninstallRemovesTheMenuAndItsRecordAndKeepsABackup()
    {
        Install(Menu());
        var result = _store.Uninstall(Id);
        Assert.Equal("removed", result.Action);
        Assert.False(File.Exists(MenuFile()));
        Assert.False(File.Exists(SourceFile()));
        Assert.Single(Backups());
        Assert.Empty(_store.List());
        Assert.Equal(CatalogueResultKind.Ok, Install(Menu()).Kind); // can be installed again
    }

    [Fact]
    public void UninstallOnlyTouchesWhatTheCatalogueInstalled()
    {
        var hand = Path.Combine(_menus, "mine.menu.json");
        File.WriteAllBytes(hand, Menu());
        Assert.Equal(CatalogueResultKind.NotFound, _store.Uninstall(Id).Kind);
        Assert.True(File.Exists(hand));

        // A file someone put in the catalogue folder by hand has no record, so it isn't ours to delete.
        Directory.CreateDirectory(Path.Combine(_menus, "catalogue"));
        var other = Guid.NewGuid();
        File.WriteAllBytes(MenuFile(other), Menu(id: other));
        Assert.Equal(CatalogueResultKind.NotFound, _store.Uninstall(other).Kind);
        Assert.True(File.Exists(MenuFile(other)));
    }

    [Fact]
    public void ACatalogueFolderThatLinksOutsideIsNotFollowed()
    {
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_menus, "catalogue"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // this platform or filesystem can't make links: nothing to test
        }

        var result = Install(Menu());
        Assert.NotEqual(CatalogueResultKind.Ok, result.Kind);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public void InstalledMenusAreFoundByNormalDiscoveryButTheSourceRecordIsNot()
    {
        Install(Menu());
        var scanned = Directory.GetFiles(_menus, "*.menu.json", SearchOption.AllDirectories);
        Assert.Contains(MenuFile(), scanned);
        Assert.DoesNotContain(SourceFile(), scanned);
        Assert.Empty(Directory.GetFiles(_menus, "*.binding.json", SearchOption.AllDirectories));
    }
}

public sealed class CatalogueMatchingTests
{
    private static LibraryTitle Movie(string name, params (string, string)[] ids) =>
        new(Guid.NewGuid(), name, 2000, true, ids.ToDictionary(i => i.Item1, i => i.Item2));

    private static LibraryTitle Series(string name, params (string, string)[] ids) =>
        new(Guid.NewGuid(), name, 2000, false, ids.ToDictionary(i => i.Item1, i => i.Item2));

    private static CatalogueMatchQuery Query(string type, params (string, string)[] ids) =>
        new(Guid.NewGuid().ToString(), type, ids.ToDictionary(i => i.Item1, i => i.Item2));

    [Fact]
    public void FindsATitleByProviderId()
    {
        var inception = Movie("Inception", ("Tmdb", "27205"), ("Imdb", "tt1375666"));
        var other = Movie("Other", ("Tmdb", "1"));
        var r = CatalogueMatching.Match(new[] { Query("Movie", ("Tmdb", "27205")) }, new[] { other, inception }).Single();
        Assert.Equal("bound", r.State);
        Assert.Equal(inception.Id, r.Titles.Single().Id);
    }

    [Fact]
    public void NamesNeverMatch()
    {
        var r = CatalogueMatching.Match(new[] { Query("Movie", ("Tmdb", "27205")) }, new[] { Movie("Inception") }).Single();
        Assert.Equal("none", r.State);
        Assert.Empty(r.Titles);
    }

    [Fact]
    public void AMovieIsNotASeries()
    {
        var series = Series("A Show", ("Tmdb", "27205"));
        Assert.Equal("none", CatalogueMatching.Match(new[] { Query("Movie", ("Tmdb", "27205")) }, new[] { series }).Single().State);
        Assert.Equal("bound", CatalogueMatching.Match(new[] { Query("Series", ("Tmdb", "27205")) }, new[] { series }).Single().State);
        Assert.Equal("bound", CatalogueMatching.Match(new[] { Query("Season", ("Tmdb", "27205")) }, new[] { series }).Single().State);
    }

    [Fact]
    public void AContradictingIdIsADifferentTitle()
    {
        var t = Movie("Remake", ("Tmdb", "999"), ("Imdb", "tt1375666"));
        Assert.Equal("none", CatalogueMatching.Match(new[] { Query("Movie", ("Tmdb", "27205"), ("Imdb", "tt1375666")) }, new[] { t }).Single().State);
    }

    [Fact]
    public void TwoEquallyGoodTitlesAreAmbiguousNotGuessed()
    {
        var a = Movie("HD", ("Tmdb", "602"));
        var b = Movie("4K", ("Tmdb", "602"));
        var r = CatalogueMatching.Match(new[] { Query("Movie", ("Tmdb", "602")) }, new[] { a, b }).Single();
        Assert.Equal("ambiguous", r.State);
        Assert.Equal(2, r.Titles.Count);
    }

    [Fact]
    public void ProviderNamesAndValuesIgnoreCase()
    {
        var t = Movie("X", ("tmdb", "ABC"));
        Assert.Equal("bound", CatalogueMatching.Match(new[] { Query("Movie", ("Tmdb", "abc")) }, new[] { t }).Single().State);
    }

    [Fact]
    public void ManyEntriesAnswerInOrder()
    {
        var library = Enumerable.Range(0, 3000).Select(i => Movie("M" + i, ("Tmdb", i.ToString()))).ToList();
        var queries = Enumerable.Range(0, 3000).Select(i => Query("Movie", ("Tmdb", (i * 2).ToString()))).ToList();
        var results = CatalogueMatching.Match(queries, library);
        Assert.Equal(queries.Select(q => q.MenuId), results.Select(r => r.MenuId));
        Assert.Equal(1500, results.Count(r => r.State == "bound"));
    }
}

public sealed class CatalogueRequestParsingTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ParsesTheIndexEntryShape()
    {
        var q = DiscMenusCatalogueController.ParseQueries(B("[{\"menuId\":\"a\",\"match\":{\"itemType\":\"Movie\",\"providerIds\":{\"Tmdb\":\"1\",\"Imdb\":5,\"X\":\"\"}},\"extra\":1}]"))!;
        var one = Assert.Single(q);
        Assert.Equal("a", one.MenuId);
        Assert.Equal("Movie", one.ItemType);
        Assert.Equal(new[] { "Tmdb" }, one.ProviderIds.Keys); // non-text and empty ids are dropped
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("nope")]
    [InlineData("[1]")]
    [InlineData("[{\"menuId\":\"a\"}]")]
    [InlineData("[{\"menuId\":1,\"match\":{\"itemType\":\"Movie\",\"providerIds\":{}}}]")]
    [InlineData("[{\"menuId\":\"a\",\"match\":{\"itemType\":\"Movie\"}}]")]
    public void RejectsAnythingElse(string body) => Assert.Null(DiscMenusCatalogueController.ParseQueries(B(body)));

    [Fact]
    public void RejectsTooManyEntries()
    {
        var entry = "{\"menuId\":\"a\",\"match\":{\"itemType\":\"Movie\",\"providerIds\":{\"Tmdb\":\"1\"}}}";
        Assert.Null(DiscMenusCatalogueController.ParseQueries(B("[" + string.Join(",", Enumerable.Repeat(entry, 5001)) + "]")));
    }
}
