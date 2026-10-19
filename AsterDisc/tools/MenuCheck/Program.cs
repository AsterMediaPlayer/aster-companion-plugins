using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.DiscMenus;

// Checks menu files with the plugin's own loader: the same parsing and the same rules the server applies
// when it loads a menu. Exit status: 0 every file loads, 1 at least one doesn't, 2 bad usage.
//
//   MenuCheck [--json] FILE...
//
// --json prints [{ "file", "ok", "errors": [...] }] instead of text, for scripts.

var asJson = args.Contains("--json");
var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
var unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a != "--json").ToList();
if (files.Count == 0 || unknown.Count > 0)
{
    Console.Error.WriteLine("usage: MenuCheck [--json] FILE...");
    return 2;
}

var results = files.Select(f => (File: f, Errors: Check(f))).ToList();
if (asJson)
{
    Console.WriteLine(JsonSerializer.Serialize(
        results.Select(r => new { file = r.File, ok = r.Errors.Count == 0, errors = r.Errors }),
        new JsonSerializerOptions { WriteIndented = true }));
}
else
{
    foreach (var (file, errors) in results)
    {
        Console.WriteLine((errors.Count == 0 ? "PASS " : "FAIL ") + file);
        foreach (var e in errors)
        {
            Console.WriteLine("  - " + e);
        }
    }
}

return results.All(r => r.Errors.Count == 0) ? 0 : 1;

static List<string> Check(string path)
{
    string text;
    try
    {
        var bytes = File.ReadAllBytes(path);
        // A UTF-8 byte order mark is tolerated, as in the Python checker; invalid UTF-8 is not.
        text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
    }
    catch (DecoderFallbackException)
    {
        return new List<string> { "file is not valid UTF-8" };
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return new List<string> { "cannot read file: " + ex.Message };
    }

    try
    {
        MenuFileLoader.ParseMenu(text, path);
        return new List<string>();
    }
    catch (MenuValidationException ex)
    {
        return ex.Errors.ToList();
    }
    catch (JsonException ex)
    {
        var at = ex.Message.IndexOf(" Path:", StringComparison.Ordinal);
        var message = at > 0 ? ex.Message[..at].Trim() : ex.Message;
        return new List<string>
        {
            $"{message}{(string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? string.Empty : $" (at {ex.Path})")}"
            + (ex.LineNumber is { } line ? $" [line {line + 1}]" : string.Empty),
        };
    }
}
