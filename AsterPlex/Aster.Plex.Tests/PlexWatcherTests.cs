using System.Text.Json.Nodes;
using Aster.Plex.Plex;
using Aster.Plex.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Aster.Plex.Tests;

public class PlexWatcherTests
{
    private readonly FakePlex _plex = new();
    private readonly PlexWatcher _watcher;
    private readonly List<(string Kind, PlaybackEvent Event)> _heard = new();

    public PlexWatcherTests()
    {
        CompanionSettings.Replace(new CompanionSettings { PlexUrl = "http://plex", PlexToken = "owner" });
        _plex.Replies["plex.tv/api/v2/user"] = """{"id": 500, "username": "owner", "title": "Owner"}""";
        _plex.Replies["plex.tv/api/home/users"] = """<MediaContainer><User id="501" title="Kid" /></MediaContainer>""";
        _plex.Replies["plex/identity"] = """{"MediaContainer": {"machineIdentifier": "M1"}}""";
        _plex.Replies["plex.tv/api/servers/M1/shared_servers"] = """<MediaContainer><SharedServer userID="777" username="friend" accessToken="friend-server-token" /></MediaContainer>""";
        _plex.Replies["plex/library/metadata/31"] = PlexItemsTests.Container(new JsonObject
        {
            ["ratingKey"] = "31", ["type"] = "track", ["title"] = "Born Singer", ["parentTitle"] = "Proof",
            ["grandparentTitle"] = "BTS", ["librarySectionTitle"] = "Music", ["duration"] = 220_000,
        });
        _plex.Replies["plex/library/metadata/40"] = PlexItemsTests.Container(new JsonObject
        {
            ["ratingKey"] = "40", ["type"] = "movie", ["title"] = "The Matrix", ["year"] = 1999, ["duration"] = 8_160_000,
            ["Guid"] = new JsonArray(new JsonObject { ["id"] = "tmdb://603" }),
        });
        var server = new PlexServer(_plex);
        var people = new PlexPeople(server, NullLogger<PlexPeople>.Instance);
        _watcher = new PlexWatcher(server, new PlexItems(server, NullLogger<PlexItems>.Instance), people, NullLogger<PlexWatcher>.Instance);
        _watcher.PlaybackStart += (_, e) => _heard.Add(("start", e));
        _watcher.PlaybackProgress += (_, e) => _heard.Add(("progress", e));
        _watcher.PlaybackStopped += (_, e) => _heard.Add(("stop", e));
        _watcher.MarkedPlayed += (_, e) => _heard.Add(("played", e));
    }

    [Fact]
    public async Task ASessionStartsMovesOnAndStops()
    {
        Sessions(Session("1", 31, account: 777, offset: 1_000));
        await _watcher.SessionsAsync(CancellationToken.None);
        Sessions(Session("1", 31, account: 777, offset: 120_000));
        await _watcher.SessionsAsync(CancellationToken.None);
        Sessions();
        await _watcher.SessionsAsync(CancellationToken.None);

        Assert.Equal(["start", "progress", "progress", "stop"], _heard.Select(h => h.Kind));
        var stop = _heard[^1].Event;
        Assert.Equal(PlexPeople.IdFor(777), stop.Users[0].Id);
        Assert.Equal("Born Singer", stop.Item.Name);
        Assert.True(stop.PlaybackPositionTicks >= TimeSpan.FromSeconds(120).Ticks);
        Assert.False(stop.PlayedToCompletion);
    }

    [Fact]
    public async Task TheOwnerIsAccountOne()
    {
        Sessions(Session("2", 40, account: 1, offset: 8_155_000));
        await _watcher.SessionsAsync(CancellationToken.None);
        Sessions();
        await _watcher.SessionsAsync(CancellationToken.None);

        var stop = _heard.Single(h => h.Kind == "stop").Event;
        Assert.Equal(PlexPeople.IdFor(500), stop.Users[0].Id);
        Assert.True(stop.PlayedToCompletion);
    }

    [Fact]
    public async Task TheNextSongInTheSameSessionStopsTheLast()
    {
        Sessions(Session("3", 31, account: 777, offset: 200_000));
        await _watcher.SessionsAsync(CancellationToken.None);
        Sessions(Session("3", 40, account: 777, offset: 0));
        await _watcher.SessionsAsync(CancellationToken.None);

        Assert.Equal(["start", "progress", "stop", "start", "progress"], _heard.Select(h => h.Kind));
        Assert.Equal("Born Singer", _heard[2].Event.Item.Name);
        Assert.Equal("The Matrix", _heard[3].Event.Item.Name);
    }

    [Fact]
    public async Task SomeoneNotOnTheServerIsIgnored()
    {
        Sessions(Session("4", 31, account: 999, offset: 0));
        await _watcher.SessionsAsync(CancellationToken.None);

        Assert.Empty(_heard);
    }

    [Fact]
    public async Task TokensAreMatchedToPeople()
    {
        var people = new PlexPeople(new PlexServer(_plex), NullLogger<PlexPeople>.Instance);

        Assert.True((await people.ForTokenAsync("owner", CancellationToken.None))!.Owner);
        Assert.Equal(777, (await people.ForTokenAsync("friend-server-token", CancellationToken.None))!.PlexId);
    }

    private void Sessions(params JsonObject[] sessions) =>
        _plex.Replies["plex/status/sessions"] = new JsonObject { ["MediaContainer"] = new JsonObject { ["Metadata"] = new JsonArray(sessions) } }.ToJsonString();

    private static JsonObject Session(string key, long ratingKey, long account, long offset) => new()
    {
        ["sessionKey"] = key,
        ["ratingKey"] = ratingKey.ToString(),
        ["viewOffset"] = offset,
        ["User"] = new JsonObject { ["id"] = account.ToString() },
        ["Player"] = new JsonObject { ["state"] = "playing" },
    };
}
