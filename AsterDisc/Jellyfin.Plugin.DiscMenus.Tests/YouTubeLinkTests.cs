using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

public class YouTubeLinkTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=ue80QwXMRHg", "ue80QwXMRHg")]
    [InlineData("https://www.youtube.com/watch?v=v7MGUNV8MxU", "v7MGUNV8MxU")]
    [InlineData("https://youtube.com/watch?feature=share&v=ue80QwXMRHg&t=5", "ue80QwXMRHg")]
    [InlineData("https://m.youtube.com/watch?v=ue80QwXMRHg", "ue80QwXMRHg")]
    [InlineData("https://youtu.be/ue80QwXMRHg", "ue80QwXMRHg")]
    [InlineData("https://youtu.be/ue80QwXMRHg?t=3", "ue80QwXMRHg")]
    [InlineData("https://www.youtube.com/embed/ue80QwXMRHg", "ue80QwXMRHg")]
    [InlineData("https://www.youtube-nocookie.com/embed/ue80QwXMRHg?rel=0", "ue80QwXMRHg")]
    [InlineData("https://www.youtube.com/shorts/ue80QwXMRHg", "ue80QwXMRHg")]
    [InlineData("http://www.youtube.com/watch?v=ue80QwXMRHg", "ue80QwXMRHg")]
    public void ExtractsVideoId(string url, string expected) =>
        Assert.Equal(expected, YouTubeLink.TryGetVideoId(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://vimeo.com/123456789")]
    [InlineData("https://evil.example/watch?v=ue80QwXMRHg")]
    [InlineData("https://www.youtube.com.evil.example/watch?v=ue80QwXMRHg")]
    [InlineData("https://www.youtube.com/watch")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/watch?v=ue80QwXMRHgTOOLONG")]
    [InlineData("https://www.youtube.com/watch?v=ue80QwXMR\"><")]
    [InlineData("https://www.youtube.com/watch?v=ue80QwXMR%22%3E")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://www.youtube.com/watch?v=ue80QwXMRHg")]
    [InlineData("https://www.youtube.com/channel/UCabcdefghijk")]
    public void RejectsAnythingElse(string? url) =>
        Assert.Null(YouTubeLink.TryGetVideoId(url));
}
