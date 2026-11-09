using Aster.Plex.Plex;
using Microsoft.AspNetCore.Http;

namespace Aster.Plex.Tests;

public class PlexAuthTests
{
    [Fact]
    public void ReadsTheTokenTheWaysAppsSendIt()
    {
        Assert.Equal("abc", PlexAuth.TokenOf(With(r => r.Headers["X-Plex-Token"] = "abc")));
        Assert.Equal("abc", PlexAuth.TokenOf(With(r => r.QueryString = new QueryString("?X-Plex-Token=abc"))));
        Assert.Equal("abc", PlexAuth.TokenOf(With(r => r.Headers.Authorization = "MediaBrowser Client=\"Aster\", Device=\"Pixel\", DeviceId=\"1\", Version=\"0.10.10\", Token=\"abc\"")));
        Assert.Equal("abc", PlexAuth.TokenOf(With(r => r.Headers.Authorization = "Bearer abc")));
        Assert.Null(PlexAuth.TokenOf(With(_ => { })));
    }

    private static HttpRequest With(Action<HttpRequest> set)
    {
        var context = new DefaultHttpContext();
        set(context.Request);
        return context.Request;
    }
}
