using Jellyfin.Plugin.DiscMenus.Model;

namespace Jellyfin.Plugin.DiscMenus.Api;

/// <summary>
/// Turns a menu plus its binding into the fully-resolved document the web renderer consumes. Used by
/// the player endpoint for real viewers and by the editor's live preview, so the preview shows
/// exactly what viewers will get. A null binding (a menu not yet bound to a title) resolves no
/// extras: their entries simply have nothing to play.
/// </summary>
public static class RenderableBuilder
{
    public static RenderableMenuDocument Build(MenuDocument menu, BindingDocument? binding, Guid parentItemId, DiscMenuService service)
    {
        var menus = menu.Menus.ToDictionary(
            kv => kv.Key,
            kv => new RenderableMenu
            {
                Title = kv.Value.Title,
                Background = kv.Value.Background,
                Theme = kv.Value.Theme,
                Layout = kv.Value.Layout,
                Audio = kv.Value.Audio,
                Entries = kv.Value.Entries.Select(e => ToRenderableEntry(e, binding)).ToList(),
            });

        // Library lookups need a real item; a menu with no bound title previews without them.
        var hasItem = parentItemId != Guid.Empty;
        return new RenderableMenuDocument
        {
            MenuId = menu.MenuId,
            Root = menu.Root,
            Background = menu.Background,
            Theme = menu.Theme,
            Layout = menu.Layout,
            Audio = menu.Audio,
            ThemeSongs = hasItem && UsesThemeSong(menu) ? service.GetThemeSongs(parentItemId).ToList() : null,
            Trailers = hasItem && UsesTrailer(menu) ? service.GetTrailers(parentItemId).ToList() : null,
            Chapters = hasItem && UsesChapters(menu) ? service.GetChapters(parentItemId).ToList() : null,
            Menus = menus,
        };
    }

    private static bool UsesThemeSong(MenuDocument menu) =>
        menu.Audio?.Music?.Source == "themeSong"
        || menu.Menus.Values.Any(m => m.Audio?.Music?.Source == "themeSong");

    private static bool UsesChapters(MenuDocument menu) =>
        menu.Menus.Values.SelectMany(m => m.Entries).Any(e => e is ChaptersEntry || e is PlayFeatureEntry { StartChapter: not null });

    private static bool UsesTrailer(MenuDocument menu) =>
        menu.Background?.Source == BackgroundSource.Trailer
        || menu.Menus.Values.Any(m => m.Background?.Source == BackgroundSource.Trailer);

    private static RenderableEntry ToRenderableEntry(MenuEntry entry, BindingDocument? binding)
    {
        var result = ToActionEntry(entry, binding);
        result.Position = entry.Position;
        result.Style = entry.Style;
        result.Image = entry.Image;
        result.ImageFocus = entry.ImageFocus;
        return result;
    }

    private static RenderableEntry ToActionEntry(MenuEntry entry, BindingDocument? binding) => entry switch
    {
        PlayFeatureEntry e => new RenderableEntry { Action = e.Action, Label = e.Label, StartChapter = e.StartChapter },
        PlayExtraEntry e => new RenderableEntry { Action = e.Action, Label = e.Label, ItemId = ResolvedItemId(binding, e.Extra) },
        PlaySequenceEntry e => new RenderableEntry
        {
            Action = e.Action,
            Label = e.Label,
            ItemIds = e.Extras.Select(k => ResolvedItemId(binding, k)).Where(id => id.HasValue).Select(id => id!.Value).ToList(),
        },
        SubmenuEntry e => new RenderableEntry { Action = e.Action, Label = e.Label, Menu = e.Menu },
        ChaptersEntry e => new RenderableEntry { Action = e.Action, Label = e.Label, PerPage = e.PerPage, Menu = e.Menu },
        BackEntry e => new RenderableEntry { Action = e.Action, Label = e.Label },
        HomeEntry e => new RenderableEntry { Action = e.Action, Label = e.Label },
        _ => throw new NotSupportedException($"Unhandled menu entry type '{entry.GetType()}'."),
    };

    private static Guid? ResolvedItemId(BindingDocument? binding, string extraKey) =>
        binding is not null && binding.Bindings.TryGetValue(extraKey, out var b) && b.Status == BindingStatus.Matched ? b.ItemId : null;
}
