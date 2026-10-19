namespace Jellyfin.Plugin.DiscMenus;

/// <summary>
/// Builds the full-size Menu Designer page. Jellyfin always draws a plugin's configuration page inside its dashboard, so a page that fills the window
/// has to be one the plugin serves itself (as it does the preview). Rather than keep a second copy of the editor, the designer is the editor page's
/// own markup and script placed in a plain document of its own (Web/designer.html): the two cannot drift apart.
/// </summary>
public static class DesignerPage
{
    private const string Placeholder = "@@BODY@@";

    /// <summary>The designer page: <paramref name="template"/> with the editor page's body (everything between its &lt;body&gt; tags) where <c>@@BODY@@</c> is.</summary>
    public static string Build(string template, string editorPage)
    {
        if (!template.Contains(Placeholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The designer template has no place for the editor.");
        }

        var open = editorPage.IndexOf("<body>", StringComparison.OrdinalIgnoreCase);
        var close = editorPage.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (open < 0 || close < open)
        {
            throw new InvalidOperationException("The editor page has no body to build the designer from.");
        }

        var body = editorPage[(open + "<body>".Length)..close];
        return template.Replace(Placeholder, body, StringComparison.Ordinal);
    }
}
