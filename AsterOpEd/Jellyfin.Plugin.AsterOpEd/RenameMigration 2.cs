using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AsterOpEd;

/// <summary>
/// The plugin used to be built as Jellyfin.Plugin.OpEd.dll. Jellyfin names a plugin's settings file and data folder after its DLL, so
/// on the first start under the new name, the old settings and data are moved across: the settings file is copied (the
/// old one stays, in case of going back), and the data folder (caches, indexes, everything the plugin keeps) is moved,
/// as it can be large. Nothing happens once the new ones exist.
/// </summary>
internal static class RenameMigration
{
    public const string OldAssemblyName = "Jellyfin.Plugin.OpEd";

    public static void Run<T>(BasePlugin<T> plugin)
        where T : BasePluginConfiguration, new()
    {
        try
        {
            MoveSettings(plugin);
            MoveData(plugin);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leave everything where it was: the plugin starts with fresh settings rather than not at all.
            Console.Error.WriteLine($"{plugin.Name}: couldn't move the settings and data from {OldAssemblyName}: {e.Message}");
        }
    }

    private static void MoveSettings<T>(BasePlugin<T> plugin)
        where T : BasePluginConfiguration, new()
    {
        var current = plugin.ConfigurationFilePath;
        var old = Path.Combine(Path.GetDirectoryName(current) ?? string.Empty, OldAssemblyName + ".xml");
        if (!File.Exists(current) && File.Exists(old))
        {
            File.Copy(old, current);
        }
    }

    private static void MoveData(BasePlugin plugin)
    {
        var current = plugin.DataFolderPath;
        var parent = Path.GetDirectoryName(current);
        if (parent is null || !Directory.Exists(parent) || (Directory.Exists(current) && Directory.EnumerateFileSystemEntries(current).Any()))
        {
            return;
        }

        // Jellyfin uses "{name}" when that folder exists, else "{name}_{version}": the newest old one that has anything in it.
        var old = Directory.EnumerateDirectories(parent)
            .Where(d => Path.GetFileName(d) is var n && (n == OldAssemblyName || n.StartsWith(OldAssemblyName + "_", StringComparison.Ordinal)))
            .Where(d => Directory.EnumerateFileSystemEntries(d).Any())
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (old is null)
        {
            return;
        }

        // Into the unversioned name, which Jellyfin finds again after every update.
        var name = Path.GetFileName(plugin.AssemblyFilePath) is { Length: > 0 } file ? Path.GetFileNameWithoutExtension(file) : null;
        var target = name is null ? current : Path.Combine(parent, name);
        if (Directory.Exists(target))
        {
            if (Directory.EnumerateFileSystemEntries(target).Any())
            {
                return;
            }

            Directory.Delete(target);
        }

        Directory.Move(old, target);
        if (target != current)
        {
            plugin.SetAttributes(plugin.AssemblyFilePath, target, plugin.Version);
        }
    }
}
