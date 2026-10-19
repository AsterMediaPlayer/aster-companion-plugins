using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>
/// OPTIONAL compatibility: if the separately installed "File Transformation" plugin is present, also registers our script injection with it, in
/// case it serves index.html itself ahead of our own middleware (IndexHtmlInjection.cs, which is what normally does the job and needs nothing else).
/// Registers with the "File Transformation" plugin - https://github.com/IAmParadox27/jellyfin-plugin-file-transformation.
/// That plugin lets other plugins modify jellyfin-web's served files (here,
/// index.html) without patching jellyfin-web itself; it's not built into
/// Jellyfin core. Its own plugins load in separate AssemblyLoadContexts, so
/// dependents must call it via reflection rather than a referenced type -
/// see its own PluginInterface.cs, which is exactly what this does.
/// </summary>
public sealed class FileTransformationIntegration : IHostedService
{
    /// <summary>Fixed ID for our registration; stable across restarts so re-registering is idempotent.</summary>
    private static readonly Guid TransformationId = Guid.Parse("2b6f2f6e-6e7b-4f0a-9b0a-3f6a1b2c3d4e");

    private readonly ILogger<FileTransformationIntegration> _logger;

    public FileTransformationIntegration(ILogger<FileTransformationIntegration> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        TryRegister();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void TryRegister()
    {
        AssemblyLoadContext? fileTransformationContext = null;
        Assembly? fileTransformationAssembly = null;

        foreach (var context in AssemblyLoadContext.All)
        {
            var match = SafeAssemblies(context).FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.FileTransformation");
            if (match is not null)
            {
                fileTransformationContext = context;
                fileTransformationAssembly = match;
                break;
            }
        }

        if (fileTransformationAssembly is null || fileTransformationContext is null)
        {
            _logger.LogDebug("File Transformation plugin not installed; not needed, the plugin adds its own script to the web client.");
            return;
        }

        try
        {
            var pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var registerMethod = pluginInterfaceType?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            if (registerMethod is null)
            {
                _logger.LogWarning("File Transformation.PluginInterface.RegisterTransformation not found - incompatible version?");
                return;
            }

            // RegisterTransformation takes a Newtonsoft.Json.Linq.JObject. Each plugin
            // loads in its own isolated AssemblyLoadContext, so a JObject built from a
            // private copy of Newtonsoft.Json we shipped ourselves is a distinct,
            // incompatible runtime type even if textually identical - confirmed: Invoke
            // rejected it with "Object of type 'JObject' cannot be converted to type
            // 'JObject'". Neither we nor File Transformation itself ship a private copy
            // of Newtonsoft.Json (NuGet didn't copy it locally for either build, since
            // Jellyfin's own server process already references it for Swagger/
            // Swashbuckle) - so there is one shared instance for the whole process.
            // Search every load context, not just File Transformation's own, to find it.
            var newtonsoftAssembly = AssemblyLoadContext.All
                .SelectMany(SafeAssemblies)
                .FirstOrDefault(a => a.GetName().Name == "Newtonsoft.Json");
            var jObjectType = newtonsoftAssembly?.GetType("Newtonsoft.Json.Linq.JObject");
            var parseMethod = jObjectType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null);
            if (parseMethod is null)
            {
                _logger.LogWarning("Could not find File Transformation's own Newtonsoft.Json.Linq.JObject.Parse - incompatible version?");
                return;
            }

            var payloadJson = $$"""
                {"id":"{{TransformationId}}","fileNamePattern":"^index\\.html$","transformationEndpoint":"/DiscMenus/web/Transform"}
                """;
            var payload = parseMethod.Invoke(null, [payloadJson]);

            registerMethod.Invoke(null, [payload]);
            _logger.LogInformation("Registered web menu renderer script injection with File Transformation.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register with File Transformation.");
        }
    }

    private static IEnumerable<Assembly> SafeAssemblies(AssemblyLoadContext context)
    {
        try
        {
            return context.Assemblies;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
