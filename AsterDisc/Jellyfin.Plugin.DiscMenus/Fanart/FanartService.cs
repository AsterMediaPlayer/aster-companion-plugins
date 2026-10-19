using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscMenus.Fanart;

/// <summary>
/// Looks up a title's artwork on fanart.tv and serves it from a local cache, so a client never contacts fanart.tv: only this
/// server does, with the administrator's own API key. The key is sent in a request header (never in a URL, so it can't land in
/// a log), only to fanart.tv's API, and is never returned to anyone. Everything fanart.tv answers is untrusted: URLs are
/// validated, sizes capped, and bytes checked to be the picture they claim to be.
/// </summary>
public sealed class FanartService
{
    public const string ApiBase = "https://webservice.fanart.tv/v3/";

    private const int MaxListingBytes = 4 * 1024 * 1024;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan ListingLife = TimeSpan.FromHours(6);
    private static readonly TimeSpan MissingLife = TimeSpan.FromHours(1);
    private static readonly TimeSpan TroubleLife = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ImageLife = TimeSpan.FromDays(30);

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _cacheDir;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _maxFiles;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _upstream = new(4);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, FanartLookup Result)> _listings = new();
    private string? _lastKey;

    public FanartService(HttpClient http, Func<string?> apiKey, Func<string> cacheDir, TimeProvider time, ILogger logger, int maxFiles = 500, long maxBytes = 500L * 1024 * 1024)
    {
        _http = http;
        _apiKey = apiKey;
        _cacheDir = cacheDir;
        _time = time;
        _logger = logger;
        _maxFiles = maxFiles;
        _maxBytes = maxBytes;
    }

    /// <summary>Is there a key at all (so the page can say so)? The key itself never leaves this class.</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(_apiKey());

    /// <summary>The service as Jellyfin builds it: the plugin's configured key, and a cache in the plugin's data folder.</summary>
    public FanartService(IHttpClientFactory httpFactory, ILogger<FanartService> logger)
        : this(
            httpFactory.CreateClient(),
            () => Plugin.Instance?.Configuration.FanartApiKey,
            () => Path.Combine(Plugin.Instance!.DataFolderPath, "cache", "fanart"),
            TimeProvider.System,
            logger)
    {
    }

    public async Task<FanartLookup> GetListingAsync(FanartTarget target, CancellationToken ct, bool bypassCache = false)
    {
        var key = _apiKey()?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return new FanartLookup(FanartStatus.NoKey, Array.Empty<FanartImage>());
        }

        // A different key (the administrator fixed it) must not be answered from what the old one got.
        if (!string.Equals(_lastKey, key, StringComparison.Ordinal))
        {
            _listings.Clear();
            _lastKey = key;
        }

        var cacheKey = $"{target.Kind}/{target.ProviderId}";
        if (!bypassCache && TryCached(cacheKey, out var hit))
        {
            return hit;
        }

        var gate = _locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!bypassCache && TryCached(cacheKey, out hit))
            {
                return hit; // another request fetched it while we waited
            }

            var result = await FetchListingAsync(target, key, ct).ConfigureAwait(false);
            var life = result.Status switch
            {
                FanartStatus.Ok => ListingLife,
                FanartStatus.NotFound => MissingLife,
                _ => TroubleLife,
            };
            _listings[cacheKey] = (_time.GetUtcNow() + life, result);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Asks fanart.tv about a title everyone has, ignoring the cache: "does my key work?"</summary>
    public async Task<FanartStatus> TestKeyAsync(CancellationToken ct)
    {
        var lookup = await GetListingAsync(new FanartTarget(FanartKind.Movie, "603"), ct, bypassCache: true).ConfigureAwait(false);
        return lookup.Status;
    }

    public async Task<FanartImageResult> GetImageAsync(FanartTarget target, string imageId, CancellationToken ct)
    {
        if (imageId.Length is < 1 or > 12 || !imageId.All(char.IsAsciiDigit))
        {
            return new FanartImageResult(FanartStatus.NotFound);
        }

        var listing = await GetListingAsync(target, ct).ConfigureAwait(false);
        if (listing.Status != FanartStatus.Ok)
        {
            return new FanartImageResult(listing.Status);
        }

        var image = listing.Images.FirstOrDefault(i => i.Id == imageId);
        if (image is null)
        {
            return new FanartImageResult(FanartStatus.NotFound);
        }

        var contentType = FanartParser.ContentTypeFor(image.Url)!;
        var ext = Path.GetExtension(image.Url).ToLowerInvariant();
        var cacheDir = _cacheDir();
        var file = Path.Combine(cacheDir, $"{target.Kind}-{target.ProviderId}-{imageId}{ext}");
        if (File.Exists(file) && _time.GetUtcNow() - File.GetLastWriteTimeUtc(file) < ImageLife)
        {
            return new FanartImageResult(FanartStatus.Ok, file, contentType);
        }

        var gate = _locks.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(file) && _time.GetUtcNow() - File.GetLastWriteTimeUtc(file) < ImageLife)
            {
                return new FanartImageResult(FanartStatus.Ok, file, contentType);
            }

            var bytes = await DownloadAsync(image.Url, contentType, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                return new FanartImageResult(FanartStatus.Unavailable);
            }

            Directory.CreateDirectory(cacheDir);
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
                File.Move(temp, file, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }

            Prune(cacheDir);
            return new FanartImageResult(FanartStatus.Ok, file, contentType);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryCached(string cacheKey, out FanartLookup result)
    {
        if (_listings.TryGetValue(cacheKey, out var entry) && entry.Until > _time.GetUtcNow())
        {
            result = entry.Result;
            return true;
        }

        result = new FanartLookup(FanartStatus.Unavailable, Array.Empty<FanartImage>());
        return false;
    }

    private async Task<FanartLookup> FetchListingAsync(FanartTarget target, string key, CancellationToken ct)
    {
        var none = Array.Empty<FanartImage>();
        var path = target.Kind == FanartKind.Movie ? "movies" : "tv";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}{path}/{target.ProviderId}");
        request.Headers.TryAddWithoutValidation("api-key", key);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        await _upstream.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return new FanartLookup(FanartStatus.NotFound, none);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    _logger.LogWarning("fanart.tv rejected the configured API key");
                    return new FanartLookup(FanartStatus.BadKey, none);
                case HttpStatusCode.TooManyRequests:
                    return new FanartLookup(FanartStatus.RateLimited, none);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("fanart.tv answered {Status} for a title lookup", (int)response.StatusCode);
                return new FanartLookup(FanartStatus.Unavailable, none);
            }

            var body = await ReadLimitedAsync(response.Content, MaxListingBytes, timeout.Token).ConfigureAwait(false);
            if (body is null)
            {
                return new FanartLookup(FanartStatus.Unavailable, none);
            }

            return new FanartLookup(FanartStatus.Ok, FanartParser.ParseListing(Encoding.UTF8.GetString(body)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning("fanart.tv could not be reached: {Reason}", ex.GetType().Name);
            return new FanartLookup(FanartStatus.Unavailable, none);
        }
        finally
        {
            _upstream.Release();
        }
    }

    // The picture's bytes, or null if it isn't one we'll serve. The key is NOT sent: images are public.
    private async Task<byte[]?> DownloadAsync(string url, string expectedType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "image/*");
        await _upstream.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            {
                return null;
            }

            var bytes = await ReadLimitedAsync(response.Content, MaxImageBytes, timeout.Token).ConfigureAwait(false);
            return bytes is not null && FanartParser.LooksLike(expectedType, bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning("a fanart.tv image could not be fetched: {Reason}", ex.GetType().Name);
            return null;
        }
        finally
        {
            _upstream.Release();
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    // Keeps the cache to a sensible size by removing the oldest pictures first.
    private void Prune(string cacheDir)
    {
        try
        {
            var files = new DirectoryInfo(cacheDir).GetFiles().Where(f => !f.Name.Contains(".tmp-", StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            for (var i = 0; i < files.Count; i++)
            {
                total += files[i].Length;
                if (i >= _maxFiles || total > _maxBytes)
                {
                    files[i].Delete();
                }
            }
        }
        catch (IOException)
        {
            // pruning is housekeeping; a file in use will go next time
        }
    }
}
