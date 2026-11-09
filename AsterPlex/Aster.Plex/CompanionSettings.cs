using System.Text.Json;

namespace Aster.Plex;

/// <summary>
/// The container's settings, kept in <c>settings.json</c> in its data folder (<c>/config</c> in Docker). Environment
/// variables set at start (<c>PLEX_URL</c>, <c>PLEX_TOKEN</c>, <c>PUBLIC_URL</c>) fill in what the file doesn't have,
/// so a compose file alone is enough to run it. The service app fields are the Jellyfin plugin's Developer apps, with
/// the same names, so the service clients copied from it read them unchanged.
/// </summary>
public sealed class CompanionSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static CompanionSettings _current = new();

    /// <summary>The settings as they are now. Replaced whole on a save, so a reader never sees half a change.</summary>
    public static CompanionSettings Current => _current;

    /// <summary>Where the container reaches Plex, e.g. http://plex:32400 or http://192.168.1.10:32400.</summary>
    public string PlexUrl { get; set; } = string.Empty;

    /// <summary>
    /// The server owner's Plex token. With it the container sees everyone's sessions and history, and knows the
    /// server's people. Kept in this file only, never shown back on the settings page.
    /// </summary>
    public string PlexToken { get; set; } = string.Empty;

    /// <summary>
    /// The address people's apps reach this container at, from outside the house as well as in it
    /// (https://aster.example.com). It's added to Plex's Custom server access URLs, which is how the apps find it.
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>Plex libraries whose music is audiobooks (names or section ids); blank takes any music library with "book" in its name.</summary>
    public List<string> AudiobookLibraries { get; set; } = new();

    public string SimklClientId { get; set; } = string.Empty;

    public string AniListClientId { get; set; } = string.Empty;

    public string MyAnimeListClientId { get; set; } = string.Empty;

    public string LastFmApiKey { get; set; } = string.Empty;

    public string LastFmSharedSecret { get; set; } = string.Empty;

    public string HardcoverClientId { get; set; } = string.Empty;

    /// <summary>The data folder: <c>ASTER_DATA</c>, else <c>/config</c> when it exists (Docker), else <c>./data</c>.</summary>
    public static string DataPath { get; } = Environment.GetEnvironmentVariable("ASTER_DATA") is { Length: > 0 } set
        ? set
        : Directory.Exists("/config") ? "/config" : Path.Combine(AppContext.BaseDirectory, "data");

    private static string FilePath => Path.Combine(DataPath, "settings.json");

    /// <summary>Reads the file, filling gaps from the environment.</summary>
    public static void Load()
    {
        var settings = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<CompanionSettings>(File.ReadAllText(FilePath), Json) ?? new CompanionSettings()
            : new CompanionSettings();
        settings.PlexUrl = Fill(settings.PlexUrl, "PLEX_URL");
        settings.PlexToken = Fill(settings.PlexToken, "PLEX_TOKEN");
        settings.PublicUrl = Fill(settings.PublicUrl, "PUBLIC_URL");
        _current = settings;
    }

    /// <summary>Changes and saves the settings in one go.</summary>
    public static void Update(Action<CompanionSettings> change)
    {
        lock (Gate)
        {
            var copy = JsonSerializer.Deserialize<CompanionSettings>(JsonSerializer.Serialize(_current, Json), Json)!;
            change(copy);
            Directory.CreateDirectory(DataPath);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(copy, Json));
            File.Move(temporary, FilePath, true);
            _current = copy;
        }
    }

    /// <summary>Puts settings in place without saving them (tests).</summary>
    internal static void Replace(CompanionSettings settings) => _current = settings;

    private static string Fill(string value, string variable) =>
        !string.IsNullOrWhiteSpace(value) ? value.Trim() : (Environment.GetEnvironmentVariable(variable) ?? string.Empty).Trim();
}
