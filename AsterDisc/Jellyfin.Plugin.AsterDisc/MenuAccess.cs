using System.Security.Claims;

namespace Jellyfin.Plugin.AsterDisc;

/// <summary>Who is asking for a menu: a signed-in user, or an API key (which has no user).</summary>
public sealed record MenuCaller(Guid? UserId, bool IsApiKey)
{
    /// <summary>Read from Jellyfin's claims, as AsterOpEd does: "Jellyfin-UserId", and "Jellyfin-IsApiKey" for a key.</summary>
    public static MenuCaller From(ClaimsPrincipal principal)
    {
        var claim = principal.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value;
        Guid? userId = Guid.TryParse(claim, out var id) && id != Guid.Empty ? id : null;
        var isApiKey = principal.Claims.Any(c => c.Type == "Jellyfin-IsApiKey" && c.Value == "True");
        return new MenuCaller(userId, isApiKey);
    }
}

/// <summary>
/// Whether a caller may read a title's menu, kept free of Jellyfin types so every case can be tested. A menu shows a title's button
/// labels, the ids of its extras and its chapter, trailer and theme-song details, so it is only for someone who can see that title:
/// a restricted profile (library access, parental rating, tags) gets the same 404 as for a title that does not exist, and the extras
/// it can't see are left out. An API key, which has no user, sees everything, as it does elsewhere in Jellyfin.
/// </summary>
public static class MenuAccess
{
    /// <summary>
    /// The item whose menu to send for <paramref name="requested"/>, or null for a 404.
    /// </summary>
    /// <param name="exists">Whether the item is in the library at all.</param>
    /// <param name="userCanSee">Whether the caller's user may see an item (only asked for a caller with a user).</param>
    /// <param name="menuItemFor">The item whose bound menu a page for an item shows (its own, or a series' only season's), or null.</param>
    public static Guid? Target(Guid requested, MenuCaller caller, Func<Guid, bool> exists, Func<Guid, bool> userCanSee, Func<Guid, Guid?> menuItemFor)
    {
        var canSee = CanSee(caller, userCanSee);
        if (canSee is null || !exists(requested) || !canSee(requested))
        {
            return null;
        }

        // A series showing its season's menu shows that season's content, so the season must be visible too.
        var target = menuItemFor(requested);
        return target is { } t && (t == requested || canSee(t)) ? t : null;
    }

    /// <summary>What the caller may see, or null when it is neither a user nor an API key (nothing at all).</summary>
    public static Func<Guid, bool>? CanSee(MenuCaller caller, Func<Guid, bool> userCanSee) =>
        caller.UserId is not null ? userCanSee
        : caller.IsApiKey ? _ => true
        : null;
}
