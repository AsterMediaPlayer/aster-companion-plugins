using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>One problem found in a menu being edited. Line/column are set for JSON syntax errors.</summary>
public sealed record EditorError(string Message, int? Line = null, int? Column = null, string? Path = null);

/// <summary>A menu file in the menus folder.</summary>
/// <param name="File">Path relative to the menus folder, with forward slashes.</param>
/// <param name="Version">Fingerprint of the file's current content, used to detect that it changed under an editor.</param>
public sealed record EditorFile(string File, string Version, long Size, DateTime ModifiedUtc);

public enum EditorResultKind
{
    Ok,
    BadName,
    NotFound,
    TooLarge,
    Invalid,
    Conflict,
}

public sealed record EditorReadResult(EditorResultKind Kind, string? Json = null, string? Version = null, string? Error = null);

public sealed record EditorSaveResult(
    EditorResultKind Kind,
    string? Version = null,
    IReadOnlyList<EditorError>? Errors = null,
    string? Error = null);

/// <summary>A saved earlier version of a menu file. <paramref name="Stamp"/> (yyyyMMddHHmmssfff, UTC) identifies it.</summary>
public sealed record EditorBackup(string Stamp, long Size, int? Revision, DateTime ModifiedUtc);

/// <summary>The outcome of duplicating a menu: the new file's name and version.</summary>
public sealed record EditorCopyResult(
    EditorResultKind Kind,
    string? File = null,
    string? Version = null,
    IReadOnlyList<EditorError>? Errors = null,
    string? Error = null);

/// <summary>
/// The file side of the live menu editor: lists, reads and saves menu files, and checks what is
/// being saved. Takes its folders as parameters (rather than reading plugin settings) so it can be
/// tested against temporary directories. Everything an admin can type reaches the disk only through
/// <see cref="Resolve"/>, which confines names to "*.menu.json" files inside the menus folder.
/// </summary>
public sealed class MenuFileEditor
{
    /// <summary>Largest menu the editor will read, validate or write (real menus are a few KB).</summary>
    public const int MaxBytes = 512 * 1024;

    private const int KeepBackups = 25;
    private const string MenuSuffix = ".menu.json";
    private const string CatalogueFolder = "catalogue";

    private static readonly Regex Segment = new(@"^[\p{L}\p{N} ._()\-]+$", RegexOptions.Compiled);

    private readonly string _root;
    private readonly string _assets;
    private readonly string _backups;

    public MenuFileEditor(string menusRoot, string assetsDir, string backupsDir)
    {
        _root = Path.GetFullPath(menusRoot).TrimEnd(Path.DirectorySeparatorChar);
        _assets = Path.GetFullPath(assetsDir).TrimEnd(Path.DirectorySeparatorChar);
        _backups = Path.GetFullPath(backupsDir);
    }

    /// <summary>Short fingerprint of file content: changes whenever the bytes do.</summary>
    public static string VersionOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    private bool UnderAssets(string fullPath) =>
        fullPath.StartsWith(_assets + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Every menu file, newest information first-class: path, version, size and modified time.</summary>
    public IReadOnlyList<EditorFile> List()
    {
        if (!Directory.Exists(_root))
        {
            return Array.Empty<EditorFile>();
        }

        var files = new List<EditorFile>();
        foreach (var path in Directory.EnumerateFiles(_root, "*" + MenuSuffix, SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var full = Path.GetFullPath(path);
            if (UnderAssets(full))
            {
                continue;
            }

            var info = new FileInfo(full);
            if (info.Length > MaxBytes)
            {
                continue;
            }

            files.Add(new EditorFile(
                Path.GetRelativePath(_root, full).Replace(Path.DirectorySeparatorChar, '/'),
                VersionOf(File.ReadAllBytes(full)),
                info.Length,
                info.LastWriteTimeUtc));
        }

        return files;
    }

    /// <summary>
    /// Turns a name the client sent into a path inside the menus folder, or says why not. Only
    /// "*.menu.json" files, only below the menus folder, never in the assets folder, no dot-segments,
    /// no absolute paths, and symlinks must not lead out of the folder.
    /// </summary>
    public (EditorResultKind Kind, string? FullPath, string? Error) Resolve(string? name)
    {
        const string Bad = "That isn't a valid menu file name.";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !name.EndsWith(MenuSuffix, StringComparison.Ordinal)
            || name.Length == MenuSuffix.Length)
        {
            return (EditorResultKind.BadName, null, Bad);
        }

        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment.StartsWith('.') || !Segment.IsMatch(segment))
            {
                return (EditorResultKind.BadName, null, Bad);
            }
        }

        var full = Path.GetFullPath(Path.Combine(_root, name));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || UnderAssets(full))
        {
            return (EditorResultKind.BadName, null, Bad);
        }

        // A symlink inside the folder must not be a way out of it.
        try
        {
            var info = new FileInfo(full);
            if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                var targetFull = Path.GetFullPath(target.FullName);
                if (!targetFull.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || UnderAssets(targetFull))
                {
                    return (EditorResultKind.BadName, null, Bad);
                }
            }
        }
        catch (IOException)
        {
            return (EditorResultKind.BadName, null, Bad);
        }

        return (EditorResultKind.Ok, full, null);
    }

    public EditorReadResult Read(string? name)
    {
        var (kind, full, error) = Resolve(name);
        if (kind != EditorResultKind.Ok)
        {
            return new EditorReadResult(kind, Error: error);
        }

        var info = new FileInfo(full!);
        if (!info.Exists)
        {
            return new EditorReadResult(EditorResultKind.NotFound, Error: "No such menu file.");
        }

        if (info.Length > MaxBytes)
        {
            return new EditorReadResult(EditorResultKind.TooLarge, Error: "This file is too large to edit here.");
        }

        var bytes = File.ReadAllBytes(full!);
        return new EditorReadResult(EditorResultKind.Ok, Decode(bytes), VersionOf(bytes));
    }

    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Everything wrong with a menu's JSON, as the plugin's real loader would see it, plus rules for
    /// editing an existing file: its menuId can't change (bindings are tied to it) and its revision
    /// can't go down. Empty means it would load.
    /// </summary>
    public static IReadOnlyList<EditorError> Validate(string json, string? originalJson = null)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            return new[] { new EditorError("This menu is too large to edit here.") };
        }

        var errors = new List<EditorError>();
        try
        {
            MenuFileLoader.ParseMenu(json, "menu");
        }
        catch (JsonException ex)
        {
            var text = ex.Message;
            var at = text.IndexOf(" Path:", StringComparison.Ordinal);
            if (at > 0)
            {
                text = text[..at].Trim();
            }

            errors.Add(new EditorError(
                text + (string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? string.Empty : $" (at {ex.Path})"),
                ex.LineNumber is { } line ? (int)line + 1 : null,
                ex.BytePositionInLine is { } col ? (int)col + 1 : null,
                ErrorPaths.FromJsonPath(ex.Path)));
        }
        catch (MenuValidationException ex)
        {
            errors.AddRange(ex.Errors.Select(e => new EditorError(e, Path: ErrorPaths.FromMessage(e))));
        }

        if (errors.Count == 0 && originalJson is not null)
        {
            try
            {
                using var before = JsonDocument.Parse(originalJson);
                using var after = JsonDocument.Parse(json);
                var oldId = before.RootElement.TryGetProperty("menuId", out var oi) ? oi.GetString() : null;
                var newId = after.RootElement.TryGetProperty("menuId", out var ni) ? ni.GetString() : null;
                if (oldId is not null && !string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new EditorError("menuId can't be changed here: this server's bindings are tied to it.", Path: "/menuId"));
                }

                if (before.RootElement.TryGetProperty("revision", out var oldRev) && oldRev.TryGetInt32(out var oldRevision)
                    && after.RootElement.TryGetProperty("revision", out var newRev) && newRev.TryGetInt32(out var newRevision)
                    && newRevision < oldRevision)
                {
                    errors.Add(new EditorError($"revision can't go down (it was {oldRevision}).", Path: "/revision"));
                }
            }
            catch (JsonException)
            {
                // The original on disk wasn't parseable: nothing to protect, let the save fix it.
            }
        }

        return errors;
    }

    /// <summary>
    /// Saves an edited menu over an existing file. Refuses (without touching the file) if the JSON
    /// would not load, if the file changed since <paramref name="expectedVersion"/> was read, or if it
    /// is too large. The previous content is kept as a timestamped backup, and the new content is
    /// written to a temporary file and moved into place so a crash can't leave half a menu.
    /// </summary>
    public EditorSaveResult Save(string? name, string json, string? expectedVersion)
    {
        var (kind, full, error) = Resolve(name);
        if (kind != EditorResultKind.Ok)
        {
            return new EditorSaveResult(kind, Error: error);
        }

        var info = new FileInfo(full!);
        if (!info.Exists)
        {
            return new EditorSaveResult(EditorResultKind.NotFound, Error: "No such menu file.");
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            return new EditorSaveResult(EditorResultKind.TooLarge, Error: "This menu is too large to save here.");
        }

        var currentBytes = File.ReadAllBytes(full!);
        var currentVersion = VersionOf(currentBytes);
        if (string.IsNullOrEmpty(expectedVersion) || !string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
        {
            return new EditorSaveResult(
                EditorResultKind.Conflict,
                currentVersion,
                Error: "This file changed on the server since you opened it.");
        }

        var errors = Validate(json, Decode(currentBytes));
        if (errors.Count > 0)
        {
            return new EditorSaveResult(EditorResultKind.Invalid, Errors: errors);
        }

        var newBytes = new UTF8Encoding(false).GetBytes(json);
        if (newBytes.AsSpan().SequenceEqual(currentBytes))
        {
            return new EditorSaveResult(EditorResultKind.Ok, currentVersion); // nothing to do
        }

        Backup(name!, currentBytes);
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, newBytes);
            File.Move(temp, full!, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return new EditorSaveResult(EditorResultKind.Ok, VersionOf(newBytes));
    }

    /// <summary>
    /// Creates a new menu file, which must not exist yet (it never overwrites). Validated exactly as a saved
    /// menu is, written to a temporary file and moved into place, and the folders it needs are created. Used
    /// for menus installed from the catalogue, which live in a subfolder.
    /// </summary>
    public EditorSaveResult Create(string? name, string json)
    {
        var (kind, full, error) = Resolve(name);
        if (kind != EditorResultKind.Ok)
        {
            return new EditorSaveResult(kind, Error: error);
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            return new EditorSaveResult(EditorResultKind.TooLarge, Error: "This menu is too large.");
        }

        if (ParentEscapes(full!))
        {
            return new EditorSaveResult(EditorResultKind.BadName, Error: "That isn't a valid menu file name.");
        }

        if (File.Exists(full!))
        {
            return new EditorSaveResult(EditorResultKind.Conflict, Error: "A menu file with that name already exists.");
        }

        var errors = Validate(json);
        if (errors.Count > 0)
        {
            return new EditorSaveResult(EditorResultKind.Invalid, Errors: errors);
        }

        var bytes = new UTF8Encoding(false).GetBytes(json);
        Directory.CreateDirectory(Path.GetDirectoryName(full!)!);
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, full!, overwrite: false);
        }
        catch (IOException) when (File.Exists(full!))
        {
            return new EditorSaveResult(EditorResultKind.Conflict, Error: "A menu file with that name already exists.");
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return new EditorSaveResult(EditorResultKind.Ok, VersionOf(bytes));
    }

    /// <summary>Removes a menu file, keeping its last content as a backup.</summary>
    public EditorSaveResult Delete(string? name)
    {
        var (kind, full, error) = Resolve(name);
        if (kind != EditorResultKind.Ok)
        {
            return new EditorSaveResult(kind, Error: error);
        }

        if (!File.Exists(full!) || ParentEscapes(full!))
        {
            return new EditorSaveResult(EditorResultKind.NotFound, Error: "No such menu file.");
        }

        Backup(name!, File.ReadAllBytes(full!));
        File.Delete(full!);
        return new EditorSaveResult(EditorResultKind.Ok);
    }

    // A folder inside the menus folder that is really a link to somewhere else must not become a way out of it.
    private bool ParentEscapes(string fullPath)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(fullPath)!);
            while (dir is not null && dir.FullName.Length > _root.Length)
            {
                if (dir.Exists && dir.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    var targetFull = Path.GetFullPath(target.FullName).TrimEnd(Path.DirectorySeparatorChar);
                    if (!(targetFull + Path.DirectorySeparatorChar).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        || UnderAssets(targetFull + Path.DirectorySeparatorChar))
                    {
                        return true;
                    }
                }

                dir = dir.Parent;
            }

            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static readonly Regex StampPattern = new(@"^\d{17}$", RegexOptions.Compiled);

    private static int? RevisionOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("revision", out var r) && r.TryGetInt32(out var n) ? n : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? MenuIdOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("menuId", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes a copy of a menu beside the original (or in the menus folder itself, for one installed from the catalogue): a fresh <c>menuId</c> and
    /// revision 1, everything else exactly as it was written. Never overwrites: the name is the original's plus "-copy", "-copy-2", and so on.
    /// </summary>
    public EditorCopyResult Duplicate(string? name)
    {
        var read = Read(name);
        if (read.Kind != EditorResultKind.Ok)
        {
            return new EditorCopyResult(read.Kind, Error: read.Error);
        }

        var errors = Validate(read.Json!);
        if (errors.Count > 0)
        {
            return new EditorCopyResult(EditorResultKind.Invalid, Errors: errors);
        }

        string text;
        try
        {
            text = JsonSplice.ReplaceTopLevel(read.Json!, "menuId", "\"" + Guid.NewGuid().ToString("D") + "\"");
            text = JsonSplice.ReplaceTopLevel(text, "revision", "1");
        }
        catch (JsonException ex)
        {
            return new EditorCopyResult(EditorResultKind.Invalid, Errors: new[] { new EditorError("This menu can't be copied: " + ex.Message) });
        }

        var stem = Path.GetFileName(name!)[..^MenuSuffix.Length];
        var folder = name!.StartsWith(CatalogueFolder + "/", StringComparison.Ordinal) || !name.Contains('/') ? string.Empty : name[..(name.LastIndexOf('/') + 1)];
        for (var i = 1; i <= 99; i++)
        {
            var candidate = folder + stem + "-copy" + (i == 1 ? string.Empty : "-" + i) + MenuSuffix;
            var created = Create(candidate, text);
            if (created.Kind == EditorResultKind.Ok)
            {
                return new EditorCopyResult(EditorResultKind.Ok, candidate, created.Version);
            }

            if (created.Kind != EditorResultKind.Conflict)
            {
                return new EditorCopyResult(created.Kind, Errors: created.Errors, Error: created.Error);
            }
        }

        return new EditorCopyResult(EditorResultKind.Conflict, Error: "There are already too many copies of this menu.");
    }

    /// <summary>
    /// Deletes a menu the user made or copied (keeping a backup). Menus installed from the catalogue are not deleted here: removing one also has
    /// to remove its record of what was installed, which the Menu Catalogue page does.
    /// </summary>
    public EditorSaveResult DeleteUserMenu(string? name)
    {
        if (name is not null && name.StartsWith(CatalogueFolder + "/", StringComparison.Ordinal))
        {
            return new EditorSaveResult(EditorResultKind.BadName, Error: "This menu was installed from the catalogue. Remove it on the Menu Catalogue page.");
        }

        return Delete(name);
    }

    /// <summary>The saved earlier versions of a menu, newest first.</summary>
    public IReadOnlyList<EditorBackup> ListBackups(string? name)
    {
        var (kind, _, _) = Resolve(name);
        if (kind != EditorResultKind.Ok || !Directory.Exists(_backups))
        {
            return Array.Empty<EditorBackup>();
        }

        var pattern = new Regex("^" + Regex.Escape(name!.Replace('/', '~')) + @"\.(\d{17})\.bak$");
        var found = new List<EditorBackup>();
        foreach (var path in Directory.EnumerateFiles(_backups))
        {
            var m = pattern.Match(Path.GetFileName(path));
            if (!m.Success)
            {
                continue;
            }

            var info = new FileInfo(path);
            int? revision = null;
            if (info.Length <= MaxBytes)
            {
                revision = RevisionOf(Decode(File.ReadAllBytes(path)));
            }

            found.Add(new EditorBackup(m.Groups[1].Value, info.Length, revision, info.LastWriteTimeUtc));
        }

        return found.OrderByDescending(b => b.Stamp, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Puts an earlier version back as the current one. It is saved as a NEW revision (the current revision plus one) with the current
    /// <c>menuId</c>, never as the old revision number: a revision that goes down is refused everywhere, and bindings are tied to the id.
    /// The version being replaced is itself backed up, and a file that changed since <paramref name="expectedVersion"/> is a conflict.
    /// </summary>
    public EditorSaveResult Restore(string? name, string? stamp, string? expectedVersion)
    {
        var (kind, _, error) = Resolve(name);
        if (kind != EditorResultKind.Ok)
        {
            return new EditorSaveResult(kind, Error: error);
        }

        if (stamp is null || !StampPattern.IsMatch(stamp))
        {
            return new EditorSaveResult(EditorResultKind.BadName, Error: "That isn't a valid backup.");
        }

        var current = Read(name);
        if (current.Kind != EditorResultKind.Ok)
        {
            return new EditorSaveResult(current.Kind, Error: current.Error);
        }

        var backupPath = Path.Combine(_backups, name!.Replace('/', '~') + "." + stamp + ".bak");
        if (!File.Exists(backupPath))
        {
            return new EditorSaveResult(EditorResultKind.NotFound, Error: "No such backup.");
        }

        if (new FileInfo(backupPath).Length > MaxBytes)
        {
            return new EditorSaveResult(EditorResultKind.TooLarge, Error: "That backup is too large to restore here.");
        }

        var text = Decode(File.ReadAllBytes(backupPath));
        try
        {
            var next = (RevisionOf(current.Json!) ?? RevisionOf(text) ?? 0) + 1;
            text = JsonSplice.ReplaceTopLevel(text, "revision", next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (MenuIdOf(current.Json!) is { } currentId)
            {
                text = JsonSplice.ReplaceTopLevel(text, "menuId", JsonSerializer.Serialize(currentId));
            }
        }
        catch (JsonException)
        {
            return new EditorSaveResult(EditorResultKind.Invalid, Errors: new[] { new EditorError("That backup isn't a menu that can be restored.") });
        }

        return Save(name, text, expectedVersion);
    }

    private void Backup(string name, byte[] content)
    {
        Directory.CreateDirectory(_backups);
        var stem = name.Replace('/', '~'); // '~' can't appear in a name, so stems never collide
        // Two saves in the same millisecond must not share a name (the second would overwrite the first backup): step to the next free one.
        var stamp = DateTime.UtcNow;
        string target;
        while (File.Exists(target = Path.Combine(_backups, $"{stem}.{stamp:yyyyMMddHHmmssfff}.bak")))
        {
            stamp = stamp.AddMilliseconds(1);
        }

        File.WriteAllBytes(target, content);

        // Keep the newest few per file; the timestamp in the name sorts chronologically.
        foreach (var old in Directory.EnumerateFiles(_backups, stem + ".*.bak")
                     .Where(p => Regex.IsMatch(Path.GetFileName(p), "^" + Regex.Escape(stem) + @"\.\d{17}\.bak$"))
                     .OrderByDescending(p => p, StringComparer.Ordinal)
                     .Skip(KeepBackups))
        {
            File.Delete(old);
        }
    }
}
