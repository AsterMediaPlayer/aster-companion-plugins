using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.AsterDisc.Tests;

/// <summary>
/// Every endpoint a request without a Jellyfin session can reach. Each one runs on Jellyfin's own origin, so one that sends back
/// anything the caller chose is a way to run script as the signed-in user: until 0.7.2, POST web/Transform (the File Transformation
/// callback) returned the posted text as text/html, and a hostile page could submit a form to it and read the user's access token.
/// Adding an anonymous endpoint means adding it here on purpose, and it may only read: GET, nothing from the body.
/// </summary>
public class AnonymousEndpointTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "DiscMenusController.GetAsset",            // the assets folder's images and audio, by extension, with nosniff
        "DiscMenusController.GetPreviewPage",      // embedded page, no input
        "DiscMenusController.GetDesignerPage",     // embedded page, no input
        "DiscMenusController.GetEditorScript",     // embedded scripts, name checked against a pattern
        "DiscMenusController.GetRendererScript",   // embedded script, no input
        "DiscMenusFanartController.GetImage",      // a cached fanart.tv picture, ids validated
    };

    private static IEnumerable<MethodInfo> AnonymousActions() =>
        typeof(DiscMenuService).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t =>
            {
                var classAnonymous = t.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
                var classAuthorized = t.GetCustomAttribute<AuthorizeAttribute>() is not null;
                return t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                    .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                        || classAnonymous
                        || (!classAuthorized && m.GetCustomAttribute<AuthorizeAttribute>() is null));
            });

    private static string Name(MethodInfo m) => m.DeclaringType!.Name + "." + m.Name;

    [Fact]
    public void OnlyTheKnownEndpointsAreAnonymous()
    {
        var unexpected = AnonymousActions().Select(Name).Where(n => !Allowed.Contains(n)).ToList();
        Assert.True(unexpected.Count == 0, "Anonymous endpoints not on the list: " + string.Join(", ", unexpected));
    }

    [Fact]
    public void AnonymousEndpointsOnlyRead()
    {
        foreach (var action in AnonymousActions())
        {
            var verbs = action.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).ToList();
            Assert.True(verbs.All(v => v is "GET" or "HEAD"), Name(action) + " is anonymous and accepts " + string.Join("/", verbs));
            Assert.DoesNotContain(action.GetParameters(), p => p.GetCustomAttribute<FromBodyAttribute>() is not null || p.GetCustomAttribute<FromFormAttribute>() is not null);
        }
    }

    [Fact]
    public void TheFileTransformationCallbackIsGone()
    {
        // Our own middleware puts the script into index.html; nothing needs an HTTP callback that echoes a page back.
        Assert.DoesNotContain(AnonymousActions(), m => m.GetCustomAttributes<HttpMethodAttribute>().Any(a => a.Template?.Contains("Transform", StringComparison.OrdinalIgnoreCase) == true));
        Assert.Null(typeof(DiscMenuService).Assembly.GetType("Jellyfin.Plugin.AsterDisc.FileTransformationIntegration"));
    }
}
