using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>The full-size designer is the editor page's own markup and script in a window of its own, so the two cannot drift apart.</summary>
public class DesignerPageTests
{
    private const string Template = "<!doctype html><html><body class=\"discDesigner\"><script src=\"host.js\"></script>@@BODY@@<script>ready()</script></body></html>";

    private const string Editor = "<!DOCTYPE html>\n<html><head><title>Menu Editor</title></head>\n<body>\n    <div id=\"DiscMenusEditorPage\"><button id=\"discEdSave\">Save</button><script type=\"text/javascript\">var x = 1;</script></div>\n</body>\n</html>";

    private static string Resource(string name)
    {
        var assembly = typeof(DesignerPage).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.{name}");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheEditorsBodyGoesWhereThePlaceholderIs()
    {
        var page = DesignerPage.Build(Template, Editor);
        Assert.Contains("<div id=\"DiscMenusEditorPage\">", page);
        Assert.Contains("var x = 1;", page);
        Assert.DoesNotContain("@@BODY@@", page);
        Assert.StartsWith("<!doctype html>", page);
    }

    [Fact]
    public void TheEditorAppearsOnceAndBetweenTheHostAndTheStartScript()
    {
        var page = DesignerPage.Build(Template, Editor);
        Assert.Equal(1, page.Split("DiscMenusEditorPage").Length - 1);
        Assert.True(page.IndexOf("host.js", StringComparison.Ordinal) < page.IndexOf("DiscMenusEditorPage", StringComparison.Ordinal));
        Assert.True(page.IndexOf("DiscMenusEditorPage", StringComparison.Ordinal) < page.IndexOf("ready()", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePartsOfTheEditorOutsideItsBodyAreLeftOut()
    {
        var page = DesignerPage.Build(Template, Editor);
        Assert.DoesNotContain("<title>Menu Editor</title>", page);
        Assert.DoesNotContain("<!DOCTYPE html>", page);
    }

    [Theory]
    [InlineData("<BODY>x</BODY>")]
    [InlineData("<body>x</body>\n")]
    public void BodyTagsAreFoundWhateverTheirCase(string editor) => Assert.Contains(">x<", DesignerPage.Build("<p>@@BODY@@</p>", editor).Replace("<p>x</p>", ">x<"));

    [Fact]
    public void ABodyThatContainsADollarSignOrPlaceholderTextIsCopiedAsItIs()
    {
        var page = DesignerPage.Build(Template, "<body>$1 $& @@BODY@@ \\1</body>");
        Assert.Contains("$1 $& @@BODY@@ \\1", page);
    }

    [Theory]
    [InlineData("no body at all")]
    [InlineData("</body><body>")]
    public void AnEditorWithoutABodyIsAnErrorNotABlankPage(string editor) =>
        Assert.Throws<InvalidOperationException>(() => DesignerPage.Build(Template, editor));

    [Fact]
    public void ATemplateWithoutAPlaceholderIsAnError() =>
        Assert.Throws<InvalidOperationException>(() => DesignerPage.Build("<html></html>", Editor));

    [Fact]
    public void TheRealPageIsBuiltFromTheRealEditor()
    {
        var page = DesignerPage.Build(Resource("Web.designer.html"), Resource("Configuration.editorPage.html"));
        Assert.Contains("id=\"DiscMenusEditorPage\"", page);
        Assert.Contains("editor/designer-host.js", page);
        Assert.Contains("DiscMenusDesignerHost.install", page);
        Assert.Contains("DiscMenusDesignerHost.ready", page);
        Assert.Contains("id=\"discEdOutline\"", page);
        Assert.Equal(1, page.Split("id=\"DiscMenusEditorPage\"").Length - 1);
        Assert.DoesNotContain("@@BODY@@", page);
        Assert.StartsWith("<!doctype html>", page);
    }

    [Fact]
    public void TheDesignerPageCarriesNoSecretsOrAddresses()
    {
        var page = DesignerPage.Build(Resource("Web.designer.html"), Resource("Configuration.editorPage.html"));
        Assert.DoesNotContain("AccessToken\":\"", page);
        Assert.DoesNotContain("http://", page.Replace("http://www.w3.org", string.Empty));
    }

    [Fact]
    public void TheEditorOffersAWayToOpenTheDesigner()
    {
        var editor = Resource("Configuration.editorPage.html");
        Assert.Contains("id=\"discEdOpenFull\"", editor);
        Assert.Contains("DiscMenus/web/designer.html", editor);
    }
}
