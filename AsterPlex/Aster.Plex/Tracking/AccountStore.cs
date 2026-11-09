using System.Text.Json;

namespace Aster.Plex.Tracking;

/// <summary>
/// Each person's linked accounts, sends waiting to be retried and recent sends, kept apart per person in
/// <c>data/AsterTracking/users/&lt;userId&gt;.json</c>. They aren't in the plugin's configuration, which every
/// administrator sees on the dashboard and backups copy.
/// </summary>
public sealed class AccountStore
{
    private const int LogLength = 30;
    private const int PendingLimit = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AccountStore()
        : this(Path.Combine(CompanionSettings.DataPath, "users"))
    {
    }

    public AccountStore(string folder)
    {
        _folder = folder;
    }

    public async Task<UserTracking> GetAsync(Guid userId)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return Read(userId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Reads, changes and saves one person's file in one go.</summary>
    public async Task<UserTracking> UpdateAsync(Guid userId, Action<UserTracking> change)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var tracking = Read(userId);
            change(tracking);
            if (tracking.Log.Count > LogLength)
            {
                tracking.Log.RemoveRange(0, tracking.Log.Count - LogLength);
            }

            if (tracking.Pending.Count > PendingLimit)
            {
                tracking.Pending.RemoveRange(0, tracking.Pending.Count - PendingLimit);
            }

            Directory.CreateDirectory(_folder);
            var path = FilePath(userId);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(tracking, Json)).ConfigureAwait(false);
            File.Move(temporary, path, true);
            return tracking;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Everyone with a file, for retrying their waiting sends.</summary>
    public IEnumerable<Guid> UserIds() =>
        Directory.Exists(_folder)
            ? Directory.EnumerateFiles(_folder, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => Guid.TryParse(name, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToList()
            : [];

    private string FilePath(Guid userId) => Path.Combine(_folder, userId.ToString("N") + ".json");

    private UserTracking Read(Guid userId)
    {
        var path = FilePath(userId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<UserTracking>(File.ReadAllText(path), Json) ?? new UserTracking()
            : new UserTracking();
    }
}

/// <summary>One person's tracking.</summary>
public sealed class UserTracking
{
    public LinkedAccount? Simkl { get; set; }

    public LinkedAccount? AniList { get; set; }

    public LinkedAccount? MyAnimeList { get; set; }

    public LinkedAccount? LastFm { get; set; }

    public LinkedAccount? ListenBrainz { get; set; }

    public LinkedAccount? Hardcover { get; set; }

    /// <summary>Sends that failed while a service was down, retried later.</summary>
    public List<PendingSend> Pending { get; set; } = new();

    /// <summary>The last sends, newest last.</summary>
    public List<SendLog> Log { get; set; } = new();
}

/// <summary>A linked account on one service.</summary>
public sealed class LinkedAccount
{
    public string AccessToken { get; set; } = string.Empty;

    public string? RefreshToken { get; set; }

    public DateTimeOffset? Expires { get; set; }

    /// <summary>The account's name on the service.</summary>
    public string? Name { get; set; }

    public DateTimeOffset Linked { get; set; }

    /// <summary>Whether what this person watches is sent to this service.</summary>
    public bool Scrobble { get; set; } = true;
}

public sealed class PendingSend
{
    public string Service { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>The request body, as it was first sent (with its watched time); null when it couldn't be made yet (the anime id map was down).</summary>
    public string? Body { get; set; }

    /// <summary>The item, for making the body later.</summary>
    public Guid ItemId { get; set; }

    public DateTimeOffset WatchedAt { get; set; }

    public DateTimeOffset Added { get; set; }

    public int Attempts { get; set; }
}

public sealed class SendLog
{
    public DateTimeOffset At { get; set; }

    public string Service { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>"added", "already there", "waiting to retry", "not found", "signed out", or the failure.</summary>
    public string Result { get; set; } = string.Empty;
}
