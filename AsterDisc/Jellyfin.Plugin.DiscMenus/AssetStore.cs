using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>What happened to an upload or a delete.</summary>
public enum AssetResultKind
{
    Ok,
    BadFolder,
    BadName,
    BadType,
    TooLarge,
    QuotaExceeded,
    NotFound,
}

/// <summary>One file in a menu's asset folder. <paramref name="Ref"/> is what a menu writes to use it (<c>asset:folder/name</c>).</summary>
public sealed record AssetFile(string Name, string Ref, string Kind, long Size, DateTime ModifiedUtc);

public sealed record AssetResult(AssetResultKind Kind, AssetFile? File = null, string? Error = null);

/// <summary>
/// The menu's own pictures and sounds, kept in <c>assets/&lt;folder&gt;/</c> (the folder is the menu's id). Everything an admin can upload goes
/// through <see cref="Save"/>, which only accepts raster images (png, jpeg, webp) and audio (mp3, ogg, opus, m4a, wav) whose first bytes really are
/// that format, with size limits per file and per menu. SVG and anything else that a browser could run as a page is refused by both the extension
/// and the contents. Names are reduced to the characters an <c>asset:</c> reference allows, and an existing file is never replaced: a taken name gets
/// "-2", "-3"... Files here are served without sign-in (an image or audio element cannot send one), so the editor tells the admin that uploads are public.
/// </summary>
public sealed class AssetStore
{
    public const long MaxImageBytes = 5L * 1024 * 1024;
    public const long MaxAudioBytes = 25L * 1024 * 1024;
    public const long MaxFolderBytes = 100L * 1024 * 1024;
    public const int MaxFilesPerFolder = 200;

    // The same shape the schema and the loader accept for each part of an asset reference.
    private static readonly Regex Part = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> KindOfExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image",
        [".jpg"] = "image",
        [".jpeg"] = "image",
        [".webp"] = "image",
        [".mp3"] = "audio",
        [".ogg"] = "audio",
        [".opus"] = "audio",
        [".m4a"] = "audio",
        [".wav"] = "audio",
    };

    private readonly string _root;

    public AssetStore(string assetsRoot)
    {
        _root = Path.GetFullPath(assetsRoot);
    }

    /// <summary>Whether a folder name is one a menu could name in an asset reference.</summary>
    public static bool IsFolderName(string? folder) => folder is not null && Part.IsMatch(folder);

    /// <summary>"image" or "audio" for an allowed file name, else null.</summary>
    public static string? KindOf(string name) => KindOfExtension.TryGetValue(Path.GetExtension(name), out var kind) ? kind : null;

    /// <summary>
    /// Turns what the admin's computer calls a file into a name an asset reference can hold: letters, digits, dot, dash and underscore only,
    /// starting with a letter or digit, no longer than 64 characters, the extension lower-cased. Null if nothing usable is left.
    /// </summary>
    public static string? CleanName(string? original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return null;
        }

        var file = original.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..];
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(file);
        stem = Regex.Replace(stem, "[^A-Za-z0-9._-]+", "-").Trim('-', '.', '_');
        stem = Regex.Replace(stem, @"\.{2,}", ".");
        if (stem.Length == 0 || ext.Length < 2 || !ext.Skip(1).All(char.IsAsciiLetterOrDigit))
        {
            return null;
        }

        if (stem.Length > 64 - ext.Length)
        {
            stem = stem[..(64 - ext.Length)].TrimEnd('-', '.', '_');
        }

        var name = stem + ext;
        return Part.IsMatch(name) ? name : null;
    }

    /// <summary>True if the first bytes are really the format the extension claims.</summary>
    public static bool ContentMatches(string extension, byte[] head)
    {
        bool Starts(params byte[] magic) => head.Length >= magic.Length && head.AsSpan(0, magic.Length).SequenceEqual(magic);
        bool At(int offset, string text) => head.Length >= offset + text.Length && System.Text.Encoding.ASCII.GetBytes(text).AsSpan().SequenceEqual(head.AsSpan(offset, text.Length));

        switch (extension.ToLowerInvariant())
        {
            case ".png":
                return Starts(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
            case ".jpg":
            case ".jpeg":
                return Starts(0xFF, 0xD8, 0xFF);
            case ".webp":
                return At(0, "RIFF") && At(8, "WEBP");
            case ".wav":
                return At(0, "RIFF") && At(8, "WAVE");
            case ".ogg":
            case ".opus":
                return At(0, "OggS");
            case ".m4a":
                return At(4, "ftyp");
            case ".mp3":
                // an ID3 tag, or an MPEG audio frame header (eleven set bits)
                return At(0, "ID3") || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0);
            default:
                return false;
        }
    }

    private string? FolderPath(string? folder) => IsFolderName(folder) ? Path.Combine(_root, folder!) : null;

    /// <summary>The files in a folder, images and audio only, newest first. A folder that doesn't exist yet is just empty.</summary>
    public IReadOnlyList<AssetFile> List(string? folder)
    {
        var dir = FolderPath(folder);
        if (dir is null || !Directory.Exists(dir))
        {
            return Array.Empty<AssetFile>();
        }

        return new DirectoryInfo(dir).EnumerateFiles()
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && Part.IsMatch(f.Name) && KindOf(f.Name) is not null)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new AssetFile(f.Name, $"asset:{folder}/{f.Name}", KindOf(f.Name)!, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    /// <summary>
    /// Stores an upload (read asynchronously: the web server refuses synchronous reads of a request body). <paramref name="content"/> is read to the end but never past the size limit for its kind (+1 byte to notice).
    /// Nothing is written unless the name, type, size and the file's first bytes all check out.
    /// </summary>
    public async Task<AssetResult> SaveAsync(string? folder, string? originalName, Stream content, CancellationToken cancellationToken = default)
    {
        var dir = FolderPath(folder);
        if (dir is null)
        {
            return new AssetResult(AssetResultKind.BadFolder, Error: "That isn't a valid menu folder.");
        }

        var clean = CleanName(originalName);
        if (clean is null)
        {
            return new AssetResult(AssetResultKind.BadName, Error: "That file name can't be used. Use letters, numbers, dots, dashes and underscores.");
        }

        var kind = KindOf(clean);
        if (kind is null)
        {
            return new AssetResult(AssetResultKind.BadType, Error: "Only png, jpg, webp pictures and mp3, ogg, opus, m4a, wav sounds can be uploaded.");
        }

        var limit = kind == "image" ? MaxImageBytes : MaxAudioBytes;
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit + 1 - buffer.Length)), cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                return new AssetResult(AssetResultKind.TooLarge, Error: $"That {kind} is larger than {limit / (1024 * 1024)} MB.");
            }
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (bytes.Length == 0 || !ContentMatches(Path.GetExtension(clean), bytes[..Math.Min(bytes.Length, 16)].ToArray()))
        {
            return new AssetResult(AssetResultKind.BadType, Error: "The file's contents aren't the kind of file its name says.");
        }

        Directory.CreateDirectory(dir);
        var existing = new DirectoryInfo(dir).EnumerateFiles().ToList();
        if (existing.Count >= MaxFilesPerFolder || existing.Sum(f => f.Length) + bytes.Length > MaxFolderBytes)
        {
            return new AssetResult(AssetResultKind.QuotaExceeded, Error: "This menu already has as many files as it can hold. Delete some first.");
        }

        var stem = Path.GetFileNameWithoutExtension(clean);
        var ext = Path.GetExtension(clean);
        for (var i = 1; i <= 99; i++)
        {
            var name = i == 1 ? clean : $"{Truncate(stem, 64 - ext.Length - 3)}-{i}{ext}";
            var target = Path.Combine(dir, name);
            try
            {
                // CreateNew: never replaces a file, even if two uploads race for the same name.
                using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (File.Exists(target))
            {
                continue;
            }

            var info = new FileInfo(target);
            return new AssetResult(AssetResultKind.Ok, new AssetFile(name, $"asset:{folder}/{name}", kind, info.Length, info.LastWriteTimeUtc));
        }

        return new AssetResult(AssetResultKind.QuotaExceeded, Error: "There are already too many files with that name.");
    }

    /// <summary>Deletes one file of a menu. Only a file this store would list can be deleted.</summary>
    public AssetResult Delete(string? folder, string? name)
    {
        var dir = FolderPath(folder);
        if (dir is null)
        {
            return new AssetResult(AssetResultKind.BadFolder, Error: "That isn't a valid menu folder.");
        }

        if (name is null || !Part.IsMatch(name) || KindOf(name) is null)
        {
            return new AssetResult(AssetResultKind.BadName, Error: "That isn't a file this editor manages.");
        }

        var path = Path.Combine(dir, name);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return new AssetResult(AssetResultKind.NotFound, Error: "No such file.");
        }

        info.Delete();
        return new AssetResult(AssetResultKind.Ok);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..Math.Max(1, max)];
}
