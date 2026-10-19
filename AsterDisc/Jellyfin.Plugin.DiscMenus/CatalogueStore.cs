using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DiscMenus;

public enum CatalogueResultKind
{
    Ok,
    BadRequest,
    TooLarge,
    HashMismatch,
    Invalid,
    Refused,
    NotFound,
}

/// <param name="Action">"installed", "upgraded", "unchanged" or "removed".</param>
public sealed record CatalogueResult(
    CatalogueResultKind Kind,
    string? Action = null,
    string? Message = null,
    IReadOnlyList<EditorError>? Errors = null,
    int? Revision = null);

/// <summary>A menu that was installed from the catalogue, and whether it still is what was installed.</summary>
public sealed record CatalogueInstalledMenu(Guid MenuId, int Revision, string Sha256, bool EditedLocally, string File);

/// <summary>
/// Installs, upgrades and removes menus that come from the online catalogue. They live in the "catalogue"
/// subfolder of the menus folder, one file per menu named after its id, with a small "source" record beside
/// each that remembers what was installed. The rules, all enforced here and not in the page:
/// the file must hash to what the catalogue's index promised; it must load exactly as any menu would; a menu
/// that is already in the menus folder by hand, or that was edited here since it was installed, is never
/// overwritten; an upgrade must be a newer revision; and removal only touches what this installed.
/// Nothing is fetched here: the browser brings the file, so the server never contacts anything.
/// </summary>
public sealed class CatalogueStore
{
    /// <summary>The catalogue's own size limit; real menus are a few KB.</summary>
    public const int MaxBytes = 128 * 1024;

    public const string FolderName = "catalogue";
    private const int MaxFilesScanned = 5000;

    private static readonly Regex Sha256Hex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    private readonly string _root;
    private readonly string _assets;
    private readonly MenuFileEditor _editor;

    public CatalogueStore(string menusRoot, string assetsDir, string backupsDir)
    {
        _root = Path.GetFullPath(menusRoot).TrimEnd(Path.DirectorySeparatorChar);
        _assets = Path.GetFullPath(assetsDir).TrimEnd(Path.DirectorySeparatorChar);
        _editor = new MenuFileEditor(menusRoot, assetsDir, backupsDir);
    }

    private sealed record SourceRecord(Guid MenuId, int Revision, string Sha256);

    public static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string NameOf(Guid id) => $"{FolderName}/{id:D}.menu.json";

    private string SourcePath(Guid id) => Path.Combine(_root, FolderName, $"{id:D}.source.json");

    public CatalogueResult Install(byte[] bytes, string? expectedSha256)
    {
        if (bytes.Length == 0)
        {
            return new CatalogueResult(CatalogueResultKind.BadRequest, Message: "There is no menu in the request.");
        }

        if (bytes.Length > MaxBytes)
        {
            return new CatalogueResult(CatalogueResultKind.TooLarge, Message: "This menu is larger than the catalogue allows.");
        }

        if (expectedSha256 is null || !Sha256Hex.IsMatch(expectedSha256))
        {
            return new CatalogueResult(CatalogueResultKind.BadRequest, Message: "The catalogue's sha256 for this menu is required.");
        }

        var sha = Sha256Of(bytes);
        if (!string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new CatalogueResult(CatalogueResultKind.HashMismatch, Message: "The file doesn't match the fingerprint the catalogue listed, so it wasn't installed.");
        }

        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return new CatalogueResult(CatalogueResultKind.BadRequest, Message: "The menu is not valid UTF-8 text.");
        }

        var errors = MenuFileEditor.Validate(json);
        if (errors.Count > 0)
        {
            return new CatalogueResult(CatalogueResultKind.Invalid, Errors: errors);
        }

        var doc = MenuFileLoader.ParseMenu(json, "catalogue menu");
        var id = doc.MenuId;
        if (FindElsewhere(id) is { } elsewhere)
        {
            return new CatalogueResult(
                CatalogueResultKind.Refused,
                Message: $"A menu with this id is already in your menus folder ({elsewhere}), so the catalogue's copy wasn't installed.");
        }

        var name = NameOf(id);
        var (kind, full, error) = _editor.Resolve(name);
        if (kind != EditorResultKind.Ok)
        {
            return new CatalogueResult(CatalogueResultKind.BadRequest, Message: error);
        }

        string action;
        if (File.Exists(full!))
        {
            var current = File.ReadAllBytes(full!);
            var source = ReadSource(id);
            if (source is null || !string.Equals(source.Sha256, Sha256Of(current), StringComparison.Ordinal))
            {
                return new CatalogueResult(
                    CatalogueResultKind.Refused,
                    Message: "This menu was edited on this server since it was installed, so it won't be overwritten. Uninstall it first, or keep your version.");
            }

            if (doc.Revision < source.Revision)
            {
                return new CatalogueResult(CatalogueResultKind.Refused, Message: $"You already have a newer revision ({source.Revision}).");
            }

            if (doc.Revision == source.Revision)
            {
                return current.AsSpan().SequenceEqual(bytes)
                    ? new CatalogueResult(CatalogueResultKind.Ok, "unchanged", Revision: doc.Revision)
                    : new CatalogueResult(CatalogueResultKind.Refused, Message: "The catalogue's file differs from the installed one but has the same revision, so it wasn't installed.");
            }

            var saved = _editor.Save(name, json, MenuFileEditor.VersionOf(current));
            if (saved.Kind != EditorResultKind.Ok)
            {
                return FromEditor(saved);
            }

            action = "upgraded";
        }
        else
        {
            var created = _editor.Create(name, json);
            if (created.Kind != EditorResultKind.Ok)
            {
                return FromEditor(created);
            }

            action = "installed";
        }

        WriteSource(new SourceRecord(id, doc.Revision, Sha256Of(File.ReadAllBytes(full!))));
        return new CatalogueResult(CatalogueResultKind.Ok, action, Revision: doc.Revision);
    }

    /// <summary>Removes a menu this installed (keeping a backup of its last content). Bindings are left alone.</summary>
    public CatalogueResult Uninstall(Guid menuId)
    {
        if (ReadSource(menuId) is null)
        {
            return new CatalogueResult(CatalogueResultKind.NotFound, Message: "That menu wasn't installed from the catalogue.");
        }

        var deleted = _editor.Delete(NameOf(menuId));
        if (deleted.Kind is not (EditorResultKind.Ok or EditorResultKind.NotFound))
        {
            return FromEditor(deleted);
        }

        File.Delete(SourcePath(menuId));
        return new CatalogueResult(CatalogueResultKind.Ok, "removed");
    }

    public IReadOnlyList<CatalogueInstalledMenu> List()
    {
        var dir = Path.Combine(_root, FolderName);
        var result = new List<CatalogueInstalledMenu>();
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFiles(dir, "*.source.json").Take(MaxFilesScanned))
        {
            var stem = Path.GetFileName(path)[..^".source.json".Length];
            if (!Guid.TryParseExact(stem, "D", out var id) || ReadSource(id) is not { } source)
            {
                continue;
            }

            var menu = Path.Combine(dir, $"{id:D}.menu.json");
            if (!File.Exists(menu))
            {
                continue;
            }

            var edited = !string.Equals(source.Sha256, Sha256Of(File.ReadAllBytes(menu)), StringComparison.Ordinal);
            result.Add(new CatalogueInstalledMenu(id, source.Revision, source.Sha256, edited, NameOf(id)));
        }

        return result.OrderBy(r => r.MenuId).ToList();
    }

    private static CatalogueResult FromEditor(EditorSaveResult r) => r.Kind switch
    {
        EditorResultKind.Invalid => new CatalogueResult(CatalogueResultKind.Invalid, Errors: r.Errors),
        EditorResultKind.TooLarge => new CatalogueResult(CatalogueResultKind.TooLarge, Message: r.Error),
        EditorResultKind.Conflict => new CatalogueResult(CatalogueResultKind.Refused, Message: r.Error),
        EditorResultKind.NotFound => new CatalogueResult(CatalogueResultKind.NotFound, Message: r.Error),
        _ => new CatalogueResult(CatalogueResultKind.BadRequest, Message: r.Error),
    };

    // A menu with this id anywhere else in the menus folder (a hand-made one, or in another subfolder).
    private string? FindElsewhere(Guid id)
    {
        var catalogueDir = Path.Combine(_root, FolderName) + Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateFiles(_root, "*.menu.json", SearchOption.AllDirectories).Take(MaxFilesScanned))
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(_assets + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || full.StartsWith(catalogueDir, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(full);
                if (info.Length > MenuFileEditor.MaxBytes)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllBytes(full));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("menuId", out var value)
                    && Guid.TryParse(value.GetString(), out var other)
                    && other == id)
                {
                    return Path.GetRelativePath(_root, full).Replace('\\', '/');
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // An unreadable file can't be a duplicate we know about.
            }
        }

        return null;
    }

    private SourceRecord? ReadSource(Guid id)
    {
        try
        {
            var path = SourcePath(id);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("menuId", out var mid) || !Guid.TryParse(mid.GetString(), out var parsed) || parsed != id
                || !root.TryGetProperty("revision", out var rev) || !rev.TryGetInt32(out var revision)
                || !root.TryGetProperty("sha256", out var sha) || sha.GetString() is not { } hash || !Sha256Hex.IsMatch(hash))
            {
                return null;
            }

            return new SourceRecord(id, revision, hash.ToLowerInvariant());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private void WriteSource(SourceRecord record)
    {
        var path = SourcePath(record.MenuId);
        var text = JsonSerializer.Serialize(new
        {
            menuId = record.MenuId.ToString("D"),
            revision = record.Revision,
            sha256 = record.Sha256,
            installedUtc = DateTime.UtcNow.ToString("O"),
        });
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
