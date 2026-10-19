using System.Net;
using System.Text;
using Jellyfin.Plugin.DiscMenus.Fanart;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

public sealed class FanartParserTests
{
    private const string Sample = """
        {"name":"The Matrix","tmdb_id":"603","imdb_id":"tt0133093",
         "moviebackground":[
           {"id":"47835","url":"http://assets.fanart.tv/fanart/movies/603/moviebackground/the-matrix-4d.jpg","lang":"en","likes":"3"},
           {"id":"1","url":"https://evil.example/x.jpg","lang":"","likes":"0"},
           {"id":"abc","url":"https://assets.fanart.tv/fanart/a.jpg","lang":"en","likes":"1"},
           {"id":77,"url":"https://assets.fanart.tv/fanart/movies/603/moviebackground/n.png","lang":"00","likes":"x"}],
         "hdmovielogo":[{"id":"900","url":"https://assets.fanart.tv/fanart/movies/603/hdmovielogo/logo.png","lang":"en","likes":"5"}],
         "Bad Category!":[{"id":"5","url":"https://assets.fanart.tv/fanart/a.jpg"}]}
        """;

    [Fact]
    public void KeepsOnlyWellFormedImagesAndNormalisesToHttps()
    {
        var images = FanartParser.ParseListing(Sample);
        Assert.Equal(new[] { "47835", "77", "900" }, images.Select(i => i.Id));
        Assert.Equal("https://assets.fanart.tv/fanart/movies/603/moviebackground/the-matrix-4d.jpg", images[0].Url);
        Assert.Equal("moviebackground", images[0].Category);
        Assert.Equal("en", images[0].Lang);
        Assert.Equal(3, images[0].Likes);
        Assert.Equal(0, images[1].Likes);          // "x" is not a number
        Assert.Equal("hdmovielogo", images[2].Category);
    }

    [Theory]
    [InlineData("https://assets.fanart.tv/fanart/a.jpg", true)]
    [InlineData("http://assets.fanart.tv/fanart/a.png", true)]
    [InlineData("https://ASSETS.FANART.TV/fanart/a.webp", true)]
    [InlineData("https://assets.fanart.tv/fanart/a.jpeg", true)]
    [InlineData("https://evil.example/fanart/a.jpg", false)]
    [InlineData("https://assets.fanart.tv.evil.example/a.jpg", false)]
    [InlineData("https://evilassets.fanart.tv/a.jpg", false)]
    [InlineData("https://fanart.tv/a.jpg", false)]
    [InlineData("https://user:pw@assets.fanart.tv/a.jpg", false)]
    [InlineData("https://assets.fanart.tv:8443/a.jpg", false)]
    [InlineData("https://assets.fanart.tv/a.jpg?x=1", false)]
    [InlineData("https://assets.fanart.tv/a.jpg#f", false)]
    [InlineData("https://assets.fanart.tv/a.svg", false)]
    [InlineData("https://assets.fanart.tv/a.gif", false)]
    [InlineData("https://assets.fanart.tv/a", false)]
    [InlineData("https://assets.fanart.tv/%2e%2e/a.jpg", false)]
    [InlineData("https://assets.fanart.tv/a/../b.jpg", false)]
    [InlineData("ftp://assets.fanart.tv/a.jpg", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("//assets.fanart.tv/a.jpg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyFanartsOwnImageHostIsAccepted(string? url, bool accepted)
    {
        Assert.Equal(accepted, FanartParser.TryNormalizeUrl(url, out var safe));
        Assert.True(!accepted || safe.StartsWith("https://assets.fanart.tv/", StringComparison.Ordinal));
    }

    [Fact]
    public void LongUrlsAreRejected() => Assert.False(FanartParser.TryNormalizeUrl("https://assets.fanart.tv/" + new string('a', 400) + ".jpg", out _));

    [Fact]
    public void AListingIsCapped()
    {
        var items = string.Join(",", Enumerable.Range(1, FanartParser.MaxImages + 500).Select(i => $"{{\"id\":\"{i}\",\"url\":\"https://assets.fanart.tv/a/{i}.jpg\"}}"));
        Assert.Equal(FanartParser.MaxImages, FanartParser.ParseListing($"{{\"moviebackground\":[{items}]}}").Count);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("{\"moviebackground\":\"not an array\"}")]
    [InlineData("{\"moviebackground\":[1,\"x\",null,[]]}")]
    public void StrangeButValidJsonYieldsNothing(string json) => Assert.Empty(FanartParser.ParseListing(json));

    [Fact]
    public void BrokenOrTooDeepJsonThrowsInsteadOfBeingTrusted()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => FanartParser.ParseListing("{ nope"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => FanartParser.ParseListing(string.Concat(Enumerable.Repeat("[", 50)) + string.Concat(Enumerable.Repeat("]", 50))));
    }

    [Fact]
    public void PicturesAreCheckedByTheirFirstBytes()
    {
        Assert.True(FanartParser.LooksLike("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0 }));
        Assert.True(FanartParser.LooksLike("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }));
        Assert.True(FanartParser.LooksLike("image/webp", Encoding.ASCII.GetBytes("RIFF1234WEBPVP8 ")));
        Assert.False(FanartParser.LooksLike("image/jpeg", Encoding.ASCII.GetBytes("<html><body>error</body></html>")));
        Assert.False(FanartParser.LooksLike("image/png", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.False(FanartParser.LooksLike("image/jpeg", Array.Empty<byte>()));
        Assert.False(FanartParser.LooksLike("text/html", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
    }
}

public sealed class FanartTargetTests
{
    private static Dictionary<string, string> Ids(params (string, string)[] p) => p.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void AMovieUsesItsTmdbId()
    {
        var t = FanartTarget.For("Movie", Ids(("Tmdb", "603"), ("Tvdb", "9")), null);
        Assert.Equal(new FanartTarget(FanartKind.Movie, "603"), t);
        Assert.Equal(new FanartTarget(FanartKind.Movie, "603"), FanartTarget.For("Movie", Ids(("tmdb", "603")), null));
    }

    [Fact]
    public void ASeriesUsesItsTvdbId() =>
        Assert.Equal(new FanartTarget(FanartKind.Show, "75978"), FanartTarget.For("Series", Ids(("Tvdb", "75978"), ("Tmdb", "1")), null));

    [Fact]
    public void ASeasonUsesItsSeriesTvdbId() =>
        Assert.Equal(new FanartTarget(FanartKind.Show, "75978"), FanartTarget.For("Season", Ids(("Tvdb", "5")), Ids(("Tvdb", "75978"))));

    [Theory]
    [InlineData("Movie", "Tvdb", "603")]
    [InlineData("Series", "Tmdb", "603")]
    [InlineData("Movie", "Tmdb", "")]
    [InlineData("Movie", "Tmdb", "60 3")]
    [InlineData("Movie", "Tmdb", "../603")]
    [InlineData("Movie", "Tmdb", "1234567890123")]
    [InlineData("Episode", "Tmdb", "603")]
    [InlineData("Boxset", "Tmdb", "603")]
    public void NoUsableIdMeansNoTitle(string type, string provider, string id) =>
        Assert.Null(FanartTarget.For(type, Ids((provider, id)), Ids((provider, id))));

    [Fact]
    public void MissingDictionariesAreFine()
    {
        Assert.Null(FanartTarget.For("Movie", null, null));
        Assert.Null(FanartTarget.For("Season", Ids(("Tvdb", "1")), null));
    }
}

public sealed class FanartServiceTests : IDisposable
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string Listing = """
        {"name":"The Matrix","moviebackground":[
          {"id":"47835","url":"https://assets.fanart.tv/fanart/movies/603/moviebackground/a.jpg","lang":"en","likes":"3"},
          {"id":"47836","url":"https://assets.fanart.tv/fanart/movies/603/moviebackground/b.jpg","lang":"en","likes":"2"},
          {"id":"47837","url":"https://assets.fanart.tv/fanart/movies/603/moviebackground/c.jpg","lang":"en","likes":"1"},
          {"id":"47838","url":"https://assets.fanart.tv/fanart/movies/603/moviebackground/d.jpg","lang":"en","likes":"1"},
          {"id":"99","url":"https://evil.example/x.jpg","lang":"en","likes":"1"}],
         "hdmovielogo":[{"id":"900","url":"https://assets.fanart.tv/fanart/movies/603/hdmovielogo/l.png","lang":"en","likes":"1"}]}
        """;

    private static readonly byte[] Jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5 };
    private static readonly FanartTarget Matrix = new(FanartKind.Movie, "603");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-fanart-" + Guid.NewGuid().ToString("N"));
    private readonly Handler _handler = new();
    private readonly ManualTime _time = new();
    private string? _key = Key;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private FanartService Make(int maxFiles = 500) =>
        new(new HttpClient(_handler), () => _key, () => _dir, _time, NullLogger.Instance, maxFiles);

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Picture(byte[] bytes, string type = "image/jpeg")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return r;
    }

    private void ServeListingAndPictures()
    {
        _handler.Respond = req => req.RequestUri!.Host == "webservice.fanart.tv" ? Json(Listing) : Picture(Jpeg);
    }

    // ---- the listing
    [Fact]
    public async Task WithoutAKeyNoOneIsAsked()
    {
        _key = "  ";
        var lookup = await Make().GetListingAsync(Matrix, default);
        Assert.Equal(FanartStatus.NoKey, lookup.Status);
        Assert.Empty(_handler.Requests);
        Assert.False(Make().HasKey);
    }

    [Fact]
    public async Task TheKeyTravelsInAHeaderAndNeverInTheUrl()
    {
        ServeListingAndPictures();
        var lookup = await Make().GetListingAsync(Matrix, default);
        Assert.Equal(FanartStatus.Ok, lookup.Status);
        Assert.Equal(5, lookup.Images.Count);   // 4 backgrounds and 1 logo: the evil.example entry is dropped
        var r = Assert.Single(_handler.Requests);
        Assert.Equal("https://webservice.fanart.tv/v3/movies/603", r.Url);
        Assert.DoesNotContain(Key, r.Url);
        Assert.Equal(Key, r.Headers["api-key"]);
    }

    [Fact]
    public async Task ShowsUseTheTvPath()
    {
        ServeListingAndPictures();
        await Make().GetListingAsync(new FanartTarget(FanartKind.Show, "75978"), default);
        Assert.Equal("https://webservice.fanart.tv/v3/tv/75978", _handler.Requests.Single().Url);
    }

    [Fact]
    public async Task AListingIsKeptForSixHours()
    {
        ServeListingAndPictures();
        var s = Make();
        await s.GetListingAsync(Matrix, default);
        await s.GetListingAsync(Matrix, default);
        _time.Advance(TimeSpan.FromHours(5.9));
        await s.GetListingAsync(Matrix, default);
        Assert.Single(_handler.Requests);
        _time.Advance(TimeSpan.FromMinutes(10));
        await s.GetListingAsync(Matrix, default);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task ATitleFanartDoesNotKnowIsRememberedForAnHour()
    {
        _handler.Respond = _ => Json("{\"error\":\"not found\"}", HttpStatusCode.NotFound);
        var s = Make();
        Assert.Equal(FanartStatus.NotFound, (await s.GetListingAsync(Matrix, default)).Status);
        await s.GetListingAsync(Matrix, default);
        Assert.Single(_handler.Requests);
        _time.Advance(TimeSpan.FromMinutes(61));
        await s.GetListingAsync(Matrix, default);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FanartStatus.BadKey)]
    [InlineData(HttpStatusCode.Forbidden, FanartStatus.BadKey)]
    [InlineData(HttpStatusCode.TooManyRequests, FanartStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, FanartStatus.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, FanartStatus.Unavailable)]
    public async Task FailuresAreNamedAndOnlyBrieflyRemembered(HttpStatusCode code, FanartStatus expected)
    {
        _handler.Respond = _ => Json("{}", code);
        var s = Make();
        Assert.Equal(expected, (await s.GetListingAsync(Matrix, default)).Status);
        await s.GetListingAsync(Matrix, default);
        Assert.Single(_handler.Requests);              // not hammered
        _time.Advance(TimeSpan.FromMinutes(2));
        await s.GetListingAsync(Matrix, default);
        Assert.Equal(2, _handler.Requests.Count);      // but tried again soon
    }

    [Fact]
    public async Task ChangingTheKeyForgetsWhatTheOldOneGot()
    {
        _handler.Respond = _ => Json("{}", HttpStatusCode.Unauthorized);
        var s = Make();
        Assert.Equal(FanartStatus.BadKey, (await s.GetListingAsync(Matrix, default)).Status);
        _key = "fedcba9876543210fedcba9876543210";
        ServeListingAndPictures();
        Assert.Equal(FanartStatus.Ok, (await s.GetListingAsync(Matrix, default)).Status);
        Assert.Equal("fedcba9876543210fedcba9876543210", _handler.Requests.Last().Headers["api-key"]);
    }

    [Fact]
    public async Task ANetworkFailureIsUnavailableNotAnException()
    {
        _handler.Respond = _ => throw new HttpRequestException("connection refused");
        Assert.Equal(FanartStatus.Unavailable, (await Make().GetListingAsync(Matrix, default)).Status);
    }

    [Fact]
    public async Task ACallerWhoGivesUpIsNotMistakenForFanartBeingDown()
    {
        _handler.Respond = _ => throw new TaskCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make().GetListingAsync(Matrix, cts.Token));
    }

    [Fact]
    public async Task ABrokenOrOversizedListingIsUnavailable()
    {
        _handler.Respond = _ => Json("{ not json");
        Assert.Equal(FanartStatus.Unavailable, (await Make().GetListingAsync(Matrix, default)).Status);

        _handler.Respond = _ => Json("{\"a\":\"" + new string('x', 4 * 1024 * 1024 + 10) + "\"}");
        Assert.Equal(FanartStatus.Unavailable, (await Make().GetListingAsync(new FanartTarget(FanartKind.Movie, "604"), default)).Status);
    }

    [Fact]
    public async Task TheKeyTestAsksAgainEvenWhenAnAnswerIsCached()
    {
        ServeListingAndPictures();
        var s = Make();
        Assert.Equal(FanartStatus.Ok, await s.TestKeyAsync(default));
        Assert.Equal(FanartStatus.Ok, await s.TestKeyAsync(default));
        Assert.Equal(2, _handler.Requests.Count);
        _handler.Respond = _ => Json("{}", HttpStatusCode.Unauthorized);
        Assert.Equal(FanartStatus.BadKey, await s.TestKeyAsync(default));
    }

    [Fact]
    public async Task ManyAtOnceMakeOneRequest()
    {
        var release = new TaskCompletionSource();
        _handler.RespondAsync = async _ => { await release.Task; return Json(Listing); };
        var s = Make();
        var calls = Enumerable.Range(0, 8).Select(_ => s.GetListingAsync(Matrix, default)).ToList();
        await Task.Delay(100);
        release.SetResult();
        var all = await Task.WhenAll(calls);
        Assert.All(all, l => Assert.Equal(FanartStatus.Ok, l.Status));
        Assert.Single(_handler.Requests);
    }

    // ---- the pictures
    [Fact]
    public async Task APictureIsFetchedOnceAndThenServedFromDisk()
    {
        ServeListingAndPictures();
        var s = Make();
        var first = await s.GetImageAsync(Matrix, "47835", default);
        Assert.Equal(FanartStatus.Ok, first.Status);
        Assert.Equal("image/jpeg", first.ContentType);
        Assert.Equal(Jpeg, File.ReadAllBytes(first.Path!));
        Assert.StartsWith(_dir, first.Path);
        Assert.Equal(2, _handler.Requests.Count);                      // listing + picture

        var second = await s.GetImageAsync(Matrix, "47835", default);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(2, _handler.Requests.Count);                      // nothing more
    }

    [Fact]
    public async Task ThePictureRequestGoesToFanartsImageHostWithoutTheKey()
    {
        ServeListingAndPictures();
        await Make().GetImageAsync(Matrix, "900", default);
        var picture = _handler.Requests.Last();
        Assert.Equal("https://assets.fanart.tv/fanart/movies/603/hdmovielogo/l.png", picture.Url);
        Assert.False(picture.Headers.ContainsKey("api-key"));
        Assert.DoesNotContain(Key, picture.Url);
    }

    [Fact]
    public async Task AFileOlderThanThirtyDaysIsFetchedAgain()
    {
        ServeListingAndPictures();
        var s = Make();
        var first = await s.GetImageAsync(Matrix, "47835", default);
        _time.Advance(TimeSpan.FromDays(31));
        File.SetLastWriteTimeUtc(first.Path!, _time.GetUtcNow().UtcDateTime.AddDays(-31));
        await s.GetImageAsync(Matrix, "47835", default);
        Assert.Equal(4, _handler.Requests.Count);                      // listing, picture, listing again (expired), picture again
    }

    [Fact]
    public async Task APictureNotInTheListingIsNotFetchedAndTheEvilEntryWasNeverKept()
    {
        ServeListingAndPictures();
        var s = Make();
        Assert.Equal(FanartStatus.NotFound, (await s.GetImageAsync(Matrix, "123456", default)).Status);
        Assert.Equal(FanartStatus.NotFound, (await s.GetImageAsync(Matrix, "99", default)).Status);   // its URL was on another host
        Assert.Single(_handler.Requests);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("evil.example"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12a")]
    [InlineData("../47835")]
    [InlineData("47835/../1")]
    [InlineData("1234567890123")]
    [InlineData("-1")]
    [InlineData(" 1")]
    public async Task ABadPictureIdAsksNoOne(string id)
    {
        ServeListingAndPictures();
        Assert.Equal(FanartStatus.NotFound, (await Make().GetImageAsync(Matrix, id, default)).Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task APictureThatIsReallyAnErrorPageIsNeverServedOrKept()
    {
        _handler.Respond = req => req.RequestUri!.Host == "webservice.fanart.tv" ? Json(Listing) : Picture(Encoding.ASCII.GetBytes("<html>nope</html>"));
        var s = Make();
        Assert.Equal(FanartStatus.Unavailable, (await s.GetImageAsync(Matrix, "47835", default)).Status);
        Assert.True(!Directory.Exists(_dir) || Directory.GetFiles(_dir).Length == 0);

        _handler.Respond = req => req.RequestUri!.Host == "webservice.fanart.tv" ? Json(Listing) : Picture(Jpeg, "text/html");
        Assert.Equal(FanartStatus.Unavailable, (await s.GetImageAsync(Matrix, "47836", default)).Status);
        Assert.True(!Directory.Exists(_dir) || Directory.GetFiles(_dir).Length == 0);
    }

    [Fact]
    public async Task ATooLargePictureIsRefused()
    {
        _handler.Respond = req =>
        {
            if (req.RequestUri!.Host == "webservice.fanart.tv")
            {
                return Json(Listing);
            }

            var bytes = new byte[9 * 1024 * 1024];
            bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF;
            return Picture(bytes);
        };
        Assert.Equal(FanartStatus.Unavailable, (await Make().GetImageAsync(Matrix, "47835", default)).Status);
        Assert.True(!Directory.Exists(_dir) || Directory.GetFiles(_dir).Length == 0);
    }

    [Fact]
    public async Task APictureServerErrorIsUnavailable()
    {
        _handler.Respond = req => req.RequestUri!.Host == "webservice.fanart.tv" ? Json(Listing) : new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Equal(FanartStatus.Unavailable, (await Make().GetImageAsync(Matrix, "47835", default)).Status);
    }

    [Fact]
    public async Task ListingTroubleIsPassedOnForPictures()
    {
        _handler.Respond = _ => Json("{}", HttpStatusCode.Unauthorized);
        Assert.Equal(FanartStatus.BadKey, (await Make().GetImageAsync(Matrix, "47835", default)).Status);
        _key = null;
        Assert.Equal(FanartStatus.NoKey, (await Make().GetImageAsync(Matrix, "47835", default)).Status);
    }

    [Fact]
    public async Task TheCacheKeepsOnlyTheNewestPictures()
    {
        ServeListingAndPictures();
        var s = Make(maxFiles: 2);
        foreach (var id in new[] { "47835", "47836", "47837", "47838" })
        {
            var r = await s.GetImageAsync(Matrix, id, default);
            File.SetLastWriteTimeUtc(r.Path!, DateTime.UtcNow.AddMinutes(-(50 - int.Parse(id) % 10)));   // later ids are newer
            await Task.Delay(5);
        }

        await s.GetImageAsync(Matrix, "47838", default);
        var left = Directory.GetFiles(_dir).Select(Path.GetFileName).ToList();
        Assert.True(left.Count <= 2, string.Join(",", left));
        Assert.Contains(left, f => f!.Contains("47838"));
    }

    [Fact]
    public async Task CachedFilesNeverEscapeTheCacheFolder()
    {
        ServeListingAndPictures();
        var r = await Make().GetImageAsync(new FanartTarget(FanartKind.Movie, "603"), "47835", default);
        Assert.Equal(_dir, Path.GetDirectoryName(r.Path));
        Assert.Matches("^(Movie|Show)-[0-9]+-[0-9]+\\.(jpg|jpeg|png|webp)$", Path.GetFileName(r.Path!));
    }

    // ---- test doubles
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly object _lock = new();

        public List<(string Url, Dictionary<string, string> Headers)> Requests { get; } = new();

        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }

        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? RespondAsync { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Requests.Add((request.RequestUri!.ToString(), request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return RespondAsync is not null ? await RespondAsync(request) : Respond!(request);
        }
    }
}
