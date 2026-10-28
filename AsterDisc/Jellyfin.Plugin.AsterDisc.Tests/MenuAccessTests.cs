using System.Security.Claims;
using Jellyfin.Plugin.AsterDisc.Api;
using Xunit;

namespace Jellyfin.Plugin.AsterDisc.Tests;

/// <summary>
/// A title's menu (its button labels, the ids of its extras, its chapters, trailers and theme songs) is only for someone who can see that
/// title. Until 0.7.4 GET /DiscMenus/{id}/Menu checked only that the caller was signed in, so a restricted profile that knew a title's id
/// could read its menu.
/// </summary>
public class MenuAccessTests
{
    private static readonly Guid Title = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Season = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Unknown = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static readonly MenuCaller AUser = new(Guid.Parse("dddddddd-0000-0000-0000-000000000004"), false);
    private static readonly MenuCaller AnApiKey = new(null, true);
    private static readonly MenuCaller Nobody = new(null, false);

    private static bool Exists(Guid id) => id == Title || id == Season;

    private static Guid? OwnMenu(Guid id) => id == Title ? Title : null;

    [Fact]
    public void AUserWhoCanSeeTheTitleGetsItsMenu() =>
        Assert.Equal(Title, MenuAccess.Target(Title, AUser, Exists, _ => true, OwnMenu));

    [Fact]
    public void ARestrictedUserGetsNothing() =>
        // Library access, parental rating and tags all come through the one visibility check.
        Assert.Null(MenuAccess.Target(Title, AUser, Exists, id => id != Title, OwnMenu));

    [Fact]
    public void AnUnknownTitleIsNothingEvenForAnApiKey()
    {
        Assert.Null(MenuAccess.Target(Unknown, AUser, Exists, _ => true, _ => Unknown));
        Assert.Null(MenuAccess.Target(Unknown, AnApiKey, Exists, _ => true, _ => Unknown));
    }

    [Fact]
    public void AnApiKeySeesEverythingWithoutAUserCheck() =>
        Assert.Equal(Title, MenuAccess.Target(Title, AnApiKey, Exists, _ => throw new InvalidOperationException("no user to ask about"), OwnMenu));

    [Fact]
    public void ACallerThatIsNeitherAUserNorAKeyGetsNothing() =>
        Assert.Null(MenuAccess.Target(Title, Nobody, Exists, _ => true, OwnMenu));

    [Fact]
    public void ASeriesShowingItsSeasonsMenuNeedsTheSeasonVisibleToo()
    {
        Guid? SeasonsMenu(Guid id) => id == Title ? Season : null;
        Assert.Equal(Season, MenuAccess.Target(Title, AUser, Exists, _ => true, SeasonsMenu));
        Assert.Null(MenuAccess.Target(Title, AUser, Exists, id => id != Season, SeasonsMenu));
    }

    [Fact]
    public void AVisibleTitleWithoutAMenuIsNothing() =>
        Assert.Null(MenuAccess.Target(Title, AUser, Exists, _ => true, _ => null));

    [Fact]
    public void TheCallerIsReadFromJellyfinsClaims()
    {
        static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
            new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

        Assert.Equal(AUser.UserId, MenuCaller.From(Principal(("Jellyfin-UserId", AUser.UserId!.Value.ToString()))).UserId);
        Assert.Equal(new MenuCaller(null, true), MenuCaller.From(Principal(("Jellyfin-UserId", Guid.Empty.ToString()), ("Jellyfin-IsApiKey", "True"))));
        Assert.Equal(new MenuCaller(null, false), MenuCaller.From(Principal(("Jellyfin-UserId", "not-a-guid"))));
        Assert.Equal(new MenuCaller(null, false), MenuCaller.From(Principal()));
    }

    // ---- what a visible menu leaves out
    private static readonly Guid MakingOf = Guid.Parse("0f1e2d3c4b5a69788796a5b4c3d2e1f0");
    private static readonly Guid DelRooftop = Guid.Parse("11112222333344445555666677778888");
    private static readonly Guid DelLobby = Guid.Parse("9999aaaabbbbccccddddeeeeffff0000");

    private static string ExamplesDir([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "examples"));

    private static RenderableMenuDocument Example(Func<Guid, bool>? canSee, IEnumerable<Guid>? themeSongs = null, IEnumerable<RenderableTrailer>? trailers = null) =>
        RenderableBuilder.Assemble(
            MenuFileLoader.LoadMenu(Path.Combine(ExamplesDir(), "example.menu.json")),
            MenuFileLoader.LoadBinding(Path.Combine(ExamplesDir(), "example.binding.json")),
            Title,
            themeSongs,
            trailers,
            null,
            canSee);

    private static IEnumerable<RenderableEntry> Entries(RenderableMenuDocument d) => d.Menus.Values.SelectMany(m => m.Entries);

    [Fact]
    public void ExtrasTheViewerCantSeeAreLeftOut()
    {
        var doc = Example(id => id != DelRooftop);
        Assert.DoesNotContain(Entries(doc), e => e.ItemId == DelRooftop || (e.ItemIds?.Contains(DelRooftop) ?? false));
        Assert.Contains(Entries(doc), e => e.ItemId == MakingOf);
        Assert.Contains(Entries(doc), e => e.ItemIds is { } ids && ids.SequenceEqual(new[] { DelLobby }));
        // The button stays, with nothing to play, as for an extra that isn't matched.
        Assert.Equal(Entries(Example(null)).Count(), Entries(doc).Count());
    }

    [Fact]
    public void TrailersAndThemeSongsTheViewerCantSeeAreLeftOut()
    {
        var hiddenSong = Guid.NewGuid();
        var hiddenTrailer = Guid.NewGuid();
        var shownSong = Guid.NewGuid();
        var doc = Example(
            id => id != hiddenSong && id != hiddenTrailer,
            new[] { hiddenSong, shownSong },
            new[]
            {
                new RenderableTrailer { Kind = "local", ItemId = hiddenTrailer },
                new RenderableTrailer { Kind = "youtube", VideoId = "dQw4w9WgXcQ" },
            });
        Assert.Equal(new[] { shownSong }, doc.ThemeSongs);
        Assert.Equal(new[] { "youtube" }, doc.Trailers!.Select(t => t.Kind));
    }

    [Fact]
    public void WithoutAViewerNothingIsLeftOut()
    {
        // The editor's preview and API keys.
        var doc = Example(null, new[] { Guid.NewGuid() }, new[] { new RenderableTrailer { Kind = "local", ItemId = Guid.NewGuid() } });
        Assert.Contains(Entries(doc), e => e.ItemId == DelRooftop);
        Assert.Single(doc.ThemeSongs!);
        Assert.Single(doc.Trailers!);
    }
}
