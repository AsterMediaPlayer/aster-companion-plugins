using System.Text.Json.Nodes;
using Aster.Plex.Plex;
using Aster.Plex.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aster.Plex.Tests;

public class PlexItemsTests
{
    [Theory]
    [InlineData("tmdb://603", "Tmdb", "603")]
    [InlineData("imdb://tt0133093", "Imdb", "tt0133093")]
    [InlineData("tvdb://81189", "Tvdb", "81189")]
    [InlineData("com.plexapp.agents.themoviedb://603?lang=en", "Tmdb", "603")]
    [InlineData("com.plexapp.agents.thetvdb://81189/1/2?lang=en", "Tvdb", "81189")]
    [InlineData("com.plexapp.agents.imdb://tt0133093?lang=en", "Imdb", "tt0133093")]
    [InlineData("com.plexapp.agents.hama://tvdb-81189?lang=en", "Tvdb", "81189")]
    public void ReadsGuids(string guid, string provider, string id) =>
        Assert.Equal((provider, id), PlexItems.Parse(guid));

    [Theory]
    [InlineData("plex://movie/5d776825880197001ec967c6")]
    [InlineData("com.plexapp.agents.hama://anidb-5391?lang=en")]
    [InlineData("tmdb://not-a-number")]
    [InlineData("")]
    public void IgnoresOtherGuids(string guid) => Assert.Equal((null, null), PlexItems.Parse(guid));

    [Fact]
    public void ReadsHamasAniDbId()
    {
        Assert.Equal("5391", PlexItems.HamaAniDb("com.plexapp.agents.hama://anidb-5391?lang=en"));
        Assert.Null(PlexItems.HamaAniDb("com.plexapp.agents.hama://tvdb-81189?lang=en"));
        Assert.Null(PlexItems.HamaAniDb("plex://show/abc"));
    }

    [Fact]
    public void RatingKeysRoundTrip()
    {
        var id = PlexItems.IdFor(123456);
        Assert.Equal(123456, PlexItems.KeyOf(id));
        Assert.Null(PlexItems.KeyOf(Guid.NewGuid()));
    }

    [Fact]
    public async Task AnEpisodeHasItsShowsIds()
    {
        CompanionSettings.Replace(new CompanionSettings { PlexUrl = "http://plex", PlexToken = "owner" });
        var plex = new FakePlex();
        plex.Replies["plex/library/metadata/20"] = Container(new JsonObject
        {
            ["ratingKey"] = "20", ["type"] = "episode", ["title"] = "The Journey's End", ["grandparentTitle"] = "Frieren",
            ["grandparentRatingKey"] = "10", ["parentIndex"] = 1, ["index"] = 1, ["duration"] = 1_440_000,
            ["Guid"] = new JsonArray(new JsonObject { ["id"] = "tvdb://9350138" }),
        });
        plex.Replies["plex/library/metadata/10"] = Container(new JsonObject
        {
            ["ratingKey"] = "10", ["type"] = "show", ["title"] = "Frieren", ["guid"] = "plex://show/abc",
            ["Guid"] = new JsonArray(new JsonObject { ["id"] = "tmdb://209867" }, new JsonObject { ["id"] = "tvdb://424536" }),
        });
        var items = new PlexItems(new PlexServer(plex), NullLogger<PlexItems>.Instance);

        var episode = await items.ItemAsync(20, CancellationToken.None);

        Assert.NotNull(episode);
        Assert.Equal(PlayedKind.Episode, episode.Kind);
        Assert.Equal("209867", episode.GetSeriesProviderId("Tmdb"));
        Assert.Equal("424536", episode.GetSeriesProviderId("Tvdb"));
        Assert.Empty(episode.SeasonProviderIds);
        var title = WatchedTitle.From(episode);
        Assert.NotNull(title);
        Assert.Equal("Frieren S01E01", title.Label);
        Assert.Equal(1, title.Season);
        Assert.False(title.Anime);
    }

    [Fact]
    public async Task HamaAnimeGoesByItsAniDbEntry()
    {
        CompanionSettings.Replace(new CompanionSettings { PlexUrl = "http://plex", PlexToken = "owner" });
        var plex = new FakePlex();
        plex.Replies["plex/library/metadata/21"] = Container(new JsonObject
        {
            ["ratingKey"] = "21", ["type"] = "episode", ["title"] = "Episode 5", ["grandparentTitle"] = "Cowboy Bebop",
            ["grandparentRatingKey"] = "11", ["parentIndex"] = 1, ["index"] = 5,
        });
        plex.Replies["plex/library/metadata/11"] = Container(new JsonObject
        {
            ["ratingKey"] = "11", ["type"] = "show", ["guid"] = "com.plexapp.agents.hama://anidb-23?lang=en",
        });
        var items = new PlexItems(new PlexServer(plex), NullLogger<PlexItems>.Instance);

        var episode = await items.ItemAsync(21, CancellationToken.None);

        Assert.Equal("23", episode!.GetSeasonProviderId("AniDB"));
        var title = WatchedTitle.From(episode)!;
        Assert.True(title.Anime);
        Assert.Equal("23", title.Ids["anidb"]);
        Assert.Equal([5], title.Episodes);
    }

    [Fact]
    public async Task MusicInAnAudiobookLibraryIsABook()
    {
        CompanionSettings.Replace(new CompanionSettings { PlexUrl = "http://plex", PlexToken = "owner" });
        var plex = new FakePlex();
        plex.Replies["plex/library/metadata/30"] = Container(new JsonObject
        {
            ["ratingKey"] = "30", ["type"] = "track", ["title"] = "Part 1", ["parentTitle"] = "Project Hail Mary",
            ["grandparentTitle"] = "Andy Weir", ["librarySectionTitle"] = "Audiobooks", ["duration"] = 3_600_000,
        });
        plex.Replies["plex/library/metadata/31"] = Container(new JsonObject
        {
            ["ratingKey"] = "31", ["type"] = "track", ["title"] = "Born Singer", ["parentTitle"] = "Proof",
            ["grandparentTitle"] = "BTS", ["librarySectionTitle"] = "Music", ["duration"] = 220_000, ["index"] = 3,
        });
        var items = new PlexItems(new PlexServer(plex), NullLogger<PlexItems>.Instance);

        var book = await items.ItemAsync(30, CancellationToken.None);
        var song = await items.ItemAsync(31, CancellationToken.None);

        Assert.Equal(PlayedKind.Audiobook, book!.Kind);
        Assert.Null(Aster.Plex.LastFm.LastFmClient.Song(book));
        Assert.Equal(PlayedKind.Song, song!.Kind);
        var fields = Aster.Plex.LastFm.LastFmClient.Song(song)!;
        Assert.Equal("BTS", fields["artist"]);
        Assert.Equal("Born Singer", fields["track"]);
        Assert.Equal("Proof", fields["album"]);
        Assert.Equal("220", fields["duration"]);
    }

    internal static string Container(JsonObject metadata) =>
        new JsonObject { ["MediaContainer"] = new JsonObject { ["Metadata"] = new JsonArray(metadata) } }.ToJsonString();
}

public class AnimeByTvdbTests
{
    [Fact]
    public async Task AShokoEpisodeFindsItsAniListEntryThroughItsTvdbSeason()
    {
        CompanionSettings.Replace(new CompanionSettings { PlexUrl = "http://plex", PlexToken = "owner" });
        var plex = new FakePlex();
        plex.Replies["arm.haglund.dev/api/v2/thetvdb?id=453028"] =
            """[{"anidb":18854,"anilist":180136,"myanimelist":59741,"themoviedb":270603,"themoviedb-season":1,"thetvdb":453028,"thetvdb-season":1}]""";
        var anime = new Aster.Plex.Tracking.AnimeIds(plex, Microsoft.Extensions.Logging.Abstractions.NullLogger<Aster.Plex.Tracking.AnimeIds>.Instance);
        var episode = new Aster.Plex.Tracking.PlayedItem
        {
            Kind = Aster.Plex.Tracking.PlayedKind.Episode, Name = "I'm Elymas the Heavy Knight", SeriesName = "The Exiled Heavy Knight",
            SeriesProviderIds = new Dictionary<string, string> { ["Tvdb"] = "453028", ["Tmdb"] = "270603" }, ParentIndexNumber = 1, IndexNumber = 1,
        };

        var found = await anime.ForAsync(episode, CancellationToken.None);

        Assert.Equal(180136, found!.Value.Entry.AniList);
        Assert.Equal(59741, found.Value.Entry.MyAnimeList);
        Assert.Equal(1, found.Value.Episode);
    }

    [Fact]
    public async Task ASplitSeasonIsLeftAlone()
    {
        var plex = new FakePlex();
        plex.Replies["arm.haglund.dev/api/v2/thetvdb?id=1"] =
            """[{"anilist":10,"myanimelist":20,"thetvdb":1,"thetvdb-season":1},{"anilist":11,"myanimelist":21,"thetvdb":1,"thetvdb-season":1}]""";
        var anime = new Aster.Plex.Tracking.AnimeIds(plex, Microsoft.Extensions.Logging.Abstractions.NullLogger<Aster.Plex.Tracking.AnimeIds>.Instance);
        var episode = new Aster.Plex.Tracking.PlayedItem
        {
            Kind = Aster.Plex.Tracking.PlayedKind.Episode, SeriesProviderIds = new Dictionary<string, string> { ["Tvdb"] = "1" }, ParentIndexNumber = 1, IndexNumber = 14,
        };

        Assert.Null(await anime.ForAsync(episode, CancellationToken.None));
    }
}

public class AnimeTitleTests
{
    [Fact]
    public async Task AWholeShowStartsWithItsFirstSeasonsEntry()
    {
        var plex = new FakePlex();
        plex.Replies["arm.haglund.dev/api/v2/thetvdb?id=5"] =
            """[{"anilist":20,"myanimelist":21,"thetvdb":5,"thetvdb-season":2},{"anilist":10,"myanimelist":11,"thetvdb":5,"thetvdb-season":1}]""";
        plex.Replies["arm.haglund.dev/api/v2/thetvdb?id=73067"] = "[]";
        var anime = new Aster.Plex.Tracking.AnimeIds(plex, Microsoft.Extensions.Logging.Abstractions.NullLogger<Aster.Plex.Tracking.AnimeIds>.Instance);

        Assert.Equal(new Aster.Plex.Tracking.AnimeEntry(10, 11), await anime.TitleEntryAsync("thetvdb", 5, CancellationToken.None));
        Assert.Null(await anime.TitleEntryAsync("thetvdb", 73067, CancellationToken.None));
    }
}
