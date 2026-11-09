using System.Text.Json.Nodes;
using Aster.Plex.AniList;
using Aster.Plex.Plex;
using Aster.Plex.Simkl;
using Aster.Plex.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aster.Plex.Tests;

/// <summary>A film watched in any Plex app, heard from Plex's sessions, reaches the person's Simkl history.</summary>
public class ScrobbleTests
{
    [Fact]
    public async Task AFilmWatchedOnPlexGoesToSimkl()
    {
        CompanionSettings.Replace(new CompanionSettings { PlexUrl = "http://plex", PlexToken = "owner", SimklClientId = "simkl-app" });
        var plex = new FakePlex();
        plex.Replies["plex.tv/api/v2/user"] = """{"id": 500, "title": "Owner"}""";
        plex.Replies["plex/identity"] = """{"MediaContainer": {"machineIdentifier": "M1"}}""";
        plex.Replies["plex/library/metadata/40"] = PlexItemsTests.Container(new JsonObject
        {
            ["ratingKey"] = "40", ["type"] = "movie", ["title"] = "The Matrix", ["year"] = 1999, ["duration"] = 8_160_000,
            ["Guid"] = new JsonArray(new JsonObject { ["id"] = "tmdb://603" }, new JsonObject { ["id"] = "imdb://tt0133093" }),
        });
        plex.Replies["api.simkl.com/sync/history"] = """{"added": {"movies": 1}}""";

        var folder = Path.Combine(Path.GetTempPath(), "aster-plex-test-" + Guid.NewGuid().ToString("N"));
        var store = new AccountStore(folder);
        var owner = PlexPeople.IdFor(500);
        await store.UpdateAsync(owner, t => t.Simkl = new LinkedAccount { AccessToken = "simkl-token", Linked = DateTimeOffset.UtcNow });

        var server = new PlexServer(plex);
        var items = new PlexItems(server, NullLogger<PlexItems>.Instance);
        var watcher = new PlexWatcher(server, items, new PlexPeople(server, NullLogger<PlexPeople>.Instance), NullLogger<PlexWatcher>.Instance);
        var anime = new AnimeIds(plex, NullLogger<AnimeIds>.Instance);
        var trackers = new ITracker[] { new SimklClient(plex), new AniListClient(plex, anime) };
        using var scrobbler = new Scrobbler(watcher, items, store, new TokenService(store), trackers, NullLogger<Scrobbler>.Instance);
        await scrobbler.StartAsync(CancellationToken.None);

        plex.Replies["plex/status/sessions"] = """{"MediaContainer": {"Metadata": [{"sessionKey": "9", "ratingKey": "40", "viewOffset": 7500000, "User": {"id": "1"}, "Player": {"state": "playing"}}]}}""";
        await watcher.SessionsAsync(CancellationToken.None);
        plex.Replies["plex/status/sessions"] = """{"MediaContainer": {"size": 0}}""";
        await watcher.SessionsAsync(CancellationToken.None);

        var log = await Eventually(async () => (await store.GetAsync(owner)).Log.FirstOrDefault());
        Assert.Equal("simkl", log.Service);
        Assert.Equal("The Matrix (1999)", log.Title);
        Assert.Equal("added", log.Result);
        var sent = JsonNode.Parse(plex.Sent["api.simkl.com/sync/history"])!;
        Assert.Equal(603, sent["movies"]![0]!["ids"]!["tmdb"]!.GetValue<long>());
        Assert.Equal("tt0133093", sent["movies"]![0]!["ids"]!["imdb"]!.GetValue<string>());
        Directory.Delete(folder, true);
    }

    private static async Task<T> Eventually<T>(Func<Task<T?>> look)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await look() is T found)
            {
                return found;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("never happened");
    }
}
