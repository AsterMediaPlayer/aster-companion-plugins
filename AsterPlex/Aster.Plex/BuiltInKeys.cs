using System.Reflection;

namespace Aster.Plex;

/// <summary>
/// Aster's own apps on each service, which official images carry so people only have to link. They aren't in the
/// source: the release workflow passes them from the repository's Actions secrets to the build, which writes them into
/// the assembly's metadata (see the project file). A build from the source has none, and its services stay out of
/// <c>Features.Services</c> until the settings name the server's own.
///
/// Anyone running an image can read them out of its DLL, as from any app: they're kept out of the code, not secret
/// from the server.
/// </summary>
internal static class BuiltInKeys
{
    private const string Prefix = "AsterKey.";

    private static readonly Dictionary<string, string> Values = typeof(BuiltInKeys).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(a => a.Key.StartsWith(Prefix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.Value))
        .ToDictionary(a => a.Key[Prefix.Length..], a => a.Value!.Trim(), StringComparer.Ordinal);

    public static string SimklClientId => Get(nameof(SimklClientId));

    public static string AniListClientId => Get(nameof(AniListClientId));

    public static string MyAnimeListClientId => Get(nameof(MyAnimeListClientId));

    public static string HardcoverClientId => Get(nameof(HardcoverClientId));

    public static string LastFmApiKey => Get(nameof(LastFmApiKey));

    public static string LastFmSharedSecret => Get(nameof(LastFmSharedSecret));

    private static string Get(string name) => Values.GetValueOrDefault(name, string.Empty);
}
