using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AsterTracking.Simkl;
using Jellyfin.Plugin.AsterTracking.Tracking;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AsterTracking.Hardcover;

/// <summary>
/// Hardcover (hardcover.app), through its GraphQL API: an audiobook's listening progress on the person's matching
/// book, Currently Reading while they listen, and Read at the end. Signing in is Hardcover's OAuth device flow (a code
/// entered at hardcover.app), with tokens refreshed as they run out. Audiobooks are matched by title and author through
/// Hardcover's search (the server has no ISBNs for them), remembered for a day.
/// </summary>
public sealed partial class HardcoverClient : ITracker, IProgressTracker
{
    /// <summary>Aster's Hardcover app ("Mobile, desktop, or CLI", device grant on). Blank until it's registered.</summary>
    public const string BuiltInClientId = "";

    private const string Scope = "read:me read:catalog:search read:library write:library";

    private static readonly Uri GraphQl = new("https://api.hardcover.app/v1/graphql");

    private readonly IHttpClientFactory _http;
    private readonly ConcurrentDictionary<string, (Book? Book, DateTimeOffset At)> _books = new();

    public HardcoverClient(IHttpClientFactory http)
    {
        _http = http;
    }

    public string Service => "hardcover";

    public bool Available => ClientId.Length > 0;

    private static string ClientId
    {
        get
        {
            var own = Plugin.Instance?.Configuration.HardcoverClientId?.Trim();
            return string.IsNullOrEmpty(own) ? BuiltInClientId : own;
        }
    }

    private static string Version => typeof(HardcoverClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public LinkedAccount? Account(UserTracking tracking) => tracking.Hardcover;

    public void SetAccount(UserTracking tracking, LinkedAccount? account) => tracking.Hardcover = account;

    /// <summary>Starts a sign-in: the code to enter and where.</summary>
    public async Task<DeviceCode> StartSignInAsync(CancellationToken cancellation)
    {
        var reply = await FormAsync("oauth2/device", new() { ["client_id"] = ClientId, ["scope"] = Scope }, cancellation).ConfigureAwait(false);
        return new DeviceCode
        {
            UserCode = Text(reply, "user_code") ?? throw new TrackerException("Hardcover", HttpStatusCode.BadGateway),
            DeviceCodeValue = Text(reply, "device_code") ?? throw new TrackerException("Hardcover", HttpStatusCode.BadGateway),
            VerificationUrl = Text(reply, "verification_uri") ?? "https://hardcover.app/link",
            CompleteUrl = Text(reply, "verification_uri_complete"),
            Interval = Number(reply, "interval") ?? 5,
            Expires = DateTimeOffset.UtcNow.AddSeconds(Number(reply, "expires_in") ?? 600),
        };
    }

    /// <summary>The account once the code's been entered; null while it hasn't.</summary>
    public async Task<LinkedAccount?> PollAsync(string deviceCode, CancellationToken cancellation)
    {
        JsonNode? reply;
        try
        {
            reply = await FormAsync("oauth2/token", new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode,
                ["client_id"] = ClientId,
            }, cancellation).ConfigureAwait(false);
        }
        catch (OAuthError error) when (error.Code is "authorization_pending" or "slow_down")
        {
            return null;
        }

        var account = Tokens(reply);
        var me = await QueryAsync("{ me { id username } }", new JsonObject(), account.AccessToken, cancellation).ConfigureAwait(false);
        account.Name = Me(me)?["username"]?.GetValue<string>();
        account.Linked = DateTimeOffset.UtcNow;
        return account;
    }

    public async Task<LinkedAccount?> RefreshAsync(LinkedAccount account, CancellationToken cancellation)
    {
        if (account.RefreshToken is not string refresh)
        {
            return null;
        }

        var fresh = Tokens(await FormAsync("oauth2/token", new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = ClientId,
        }, cancellation).ConfigureAwait(false));
        fresh.RefreshToken ??= refresh;
        fresh.Name = account.Name;
        fresh.Linked = account.Linked;
        fresh.Scrobble = account.Scrobble;
        return fresh;
    }

    /// <summary>Signs Aster out on Hardcover, so it stops listing the session among the person's apps.</summary>
    public async Task RevokeAsync(LinkedAccount account, CancellationToken cancellation)
    {
        try
        {
            await FormAsync("oauth2/revoke", new()
            {
                ["token"] = account.RefreshToken ?? account.AccessToken,
                ["token_type_hint"] = account.RefreshToken is null ? "access_token" : "refresh_token",
                ["client_id"] = ClientId,
            }, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TrackerException or SignedOutException or HttpRequestException or TaskCanceledException)
        {
            // Forgotten here either way.
        }
    }

    /// <summary>Audiobooks go by their progress (<see cref="ProgressBodyAsync"/>), not as watched.</summary>
    public Task<string?> BodyAsync(BaseItem item, DateTimeOffset watchedAt, CancellationToken cancellation) => Task.FromResult<string?>(null);

    public Task<string?> ProgressBodyAsync(BaseItem item, long positionTicks, bool finished, CancellationToken cancellation)
    {
        if (item is not MediaBrowser.Controller.Entities.AudioBook book || item.RunTimeTicks is not long runtime || runtime <= 0)
        {
            return Task.FromResult<string?>(null);
        }

        var author = book.Artists.Concat(book.AlbumArtists).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? AuthorFromPath(book.Path);
        var title = string.IsNullOrWhiteSpace(book.Album) ? book.Name : book.Album;
        return Task.FromResult<string?>(new JsonObject
        {
            ["title"] = title,
            ["author"] = author,
            ["fraction"] = finished ? 1 : Math.Clamp((double)positionTicks / runtime, 0, 1),
            ["seconds"] = (long)TimeSpan.FromTicks(runtime).TotalSeconds,
            ["finished"] = finished,
            ["at"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        }.ToJsonString());
    }

    public async Task<HistoryResult> SendAsync(string body, string token, CancellationToken cancellation)
    {
        var request = JsonNode.Parse(body)!;
        var title = request["title"]?.GetValue<string>() ?? string.Empty;
        var author = request["author"]?.GetValue<string>();
        var fraction = request["fraction"]!.GetValue<double>();
        var finished = request["finished"]!.GetValue<bool>();
        var at = request["at"]?.GetValue<string>();

        var me = Me(await QueryAsync("{ me { id } }", new JsonObject(), token, cancellation).ConfigureAwait(false));
        var userId = me?["id"]?.GetValue<int>() ?? throw new SignedOutException(Service);
        if (await FindAsync(title, author, token, cancellation).ConfigureAwait(false) is not Book book)
        {
            return HistoryResult.NotFound;
        }

        var entry = await EntryAsync(userId, book.Id, token, cancellation).ConfigureAwait(false);
        if (entry is null)
        {
            // Into their library as Currently Reading, which starts a read.
            var added = await QueryAsync(
                "mutation($book:Int!){ insert_user_book(object:{book_id:$book, status_id:2}) { id error } }",
                new JsonObject { ["book"] = book.Id }, token, cancellation).ConfigureAwait(false);
            Fail(added?["data"]?["insert_user_book"]);
            entry = await EntryAsync(userId, book.Id, token, cancellation).ConfigureAwait(false) ?? throw new TrackerException("Hardcover", HttpStatusCode.BadGateway);
        }

        if (finished)
        {
            if (entry.Status == 3 && entry.ReadId is null)
            {
                return HistoryResult.Unchanged;
            }

            Fail((await QueryAsync(
                "mutation($id:Int!){ update_user_book(id:$id, object:{status_id:3}) { error } }",
                new JsonObject { ["id"] = entry.Id }, token, cancellation).ConfigureAwait(false))?["data"]?["update_user_book"]);
            return HistoryResult.Added;
        }

        if (entry.Status != 2)
        {
            // Want to Read becomes Currently Reading; a book already Read starts a re-read.
            Fail((await QueryAsync(
                "mutation($id:Int!){ update_user_book(id:$id, object:{status_id:2}) { error } }",
                new JsonObject { ["id"] = entry.Id }, token, cancellation).ConfigureAwait(false))?["data"]?["update_user_book"]);
            entry = await EntryAsync(userId, book.Id, token, cancellation).ConfigureAwait(false) ?? entry;
        }

        // Hardcover's edition can be a different length from the file: progress goes as the same share of its length,
        // short of the very end (which would finish the read: only a real finish does that).
        var length = entry.EditionSeconds ?? book.AudioSeconds ?? request["seconds"]!.GetValue<long>();
        var seconds = (int)Math.Min(fraction * length, length - 60);
        if (seconds <= 0 || (entry.ProgressSeconds is int now && Math.Abs(now - seconds) < 60))
        {
            return HistoryResult.Unchanged;
        }

        JsonNode? result;
        if (entry.ReadId is int read)
        {
            result = (await QueryAsync(
                "mutation($id:Int!,$s:Int!,$at:timestamptz){ update_user_book_read(id:$id, object:{progress_seconds:$s, action_at:$at}) { error } }",
                new JsonObject { ["id"] = read, ["s"] = seconds, ["at"] = at }, token, cancellation).ConfigureAwait(false))?["data"]?["update_user_book_read"];
        }
        else
        {
            result = (await QueryAsync(
                "mutation($id:Int!,$s:Int!,$day:date){ insert_user_book_read(user_book_id:$id, user_book_read:{progress_seconds:$s, started_at:$day}) { error } }",
                new JsonObject { ["id"] = entry.Id, ["s"] = seconds, ["day"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                token, cancellation).ConfigureAwait(false))?["data"]?["insert_user_book_read"];
        }

        Fail(result);
        return HistoryResult.Added;
    }

    /// <summary>The book on Hardcover with this title by this author: the best search hit whose title matches.</summary>
    private async Task<Book?> FindAsync(string title, string? author, string token, CancellationToken cancellation)
    {
        var key = $"{Normal(title)}|{Normal(author ?? string.Empty)}";
        if (_books.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < TimeSpan.FromDays(1))
        {
            return known.Book;
        }

        var reply = await QueryAsync(
            "query($q:String!){ search(query:$q, query_type:\"Book\", per_page:10, page:1) { results } }",
            new JsonObject { ["q"] = string.IsNullOrWhiteSpace(author) ? title : $"{title} {author}" }, token, cancellation).ConfigureAwait(false);
        var results = reply?["data"]?["search"]?["results"];
        if (results is JsonValue raw && raw.TryGetValue<string>(out var json))
        {
            results = JsonNode.Parse(json);
        }

        Book? found = null;
        var wanted = Normal(title);
        var surname = Normal(author ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        foreach (var hit in results?["hits"]?.AsArray() ?? [])
        {
            var document = hit?["document"];
            var name = Normal(document?["title"]?.ToString() ?? string.Empty);
            var authors = Normal(string.Join(' ', document?["author_names"]?.AsArray().Select(a => a?.ToString()) ?? []));
            var titleMatches = name == wanted || name.StartsWith(wanted + " ", StringComparison.Ordinal) || wanted.StartsWith(name + " ", StringComparison.Ordinal);
            if (titleMatches && (surname is null || authors.Contains(surname, StringComparison.Ordinal))
                && int.TryParse(document?["id"]?.ToString(), out var id))
            {
                var audio = document?["audio_seconds"] is JsonValue a && a.TryGetValue<double>(out var s) && s > 0 ? (long)s : (long?)null;
                found = new Book(id, audio);
                break;
            }
        }

        _books[key] = (found, DateTimeOffset.UtcNow);
        return found;
    }

    /// <summary>The person's library entry for a book, with the read in progress and its edition's length.</summary>
    private async Task<Entry?> EntryAsync(int userId, int bookId, string token, CancellationToken cancellation)
    {
        var reply = await QueryAsync(
            "query($u:Int!,$b:Int!){ user_books(where:{user_id:{_eq:$u}, book_id:{_eq:$b}}) { id status_id "
            + "user_book_reads(where:{finished_at:{_is_null:true}}, order_by:{id:desc}, limit:1) { id progress_seconds edition { audio_seconds } } } }",
            new JsonObject { ["u"] = userId, ["b"] = bookId }, token, cancellation).ConfigureAwait(false);
        var entry = reply?["data"]?["user_books"]?.AsArray().FirstOrDefault();
        if (entry is null)
        {
            return null;
        }

        var read = entry["user_book_reads"]?.AsArray().FirstOrDefault();
        return new Entry(
            entry["id"]!.GetValue<int>(),
            entry["status_id"]?.GetValue<int>() ?? 0,
            read?["id"]?.GetValue<int>(),
            read?["progress_seconds"] is JsonValue p && p.TryGetValue<int>(out var progress) ? progress : null,
            read?["edition"]?["audio_seconds"] is JsonValue e && e.TryGetValue<int>(out var edition) && edition > 0 ? edition : null);
    }

    private static JsonNode? Me(JsonNode? reply)
    {
        // "me" is a list of one.
        var me = reply?["data"]?["me"];
        return me is JsonArray list ? list.FirstOrDefault() : me;
    }

    /// <summary>Hardcover's mutations answer 200 with an "error" field when they didn't go through.</summary>
    private static void Fail(JsonNode? result)
    {
        if (result?["error"] is JsonValue error && error.TryGetValue<string>(out var message) && !string.IsNullOrEmpty(message))
        {
            throw new TrackerException("Hardcover", HttpStatusCode.UnprocessableEntity, message);
        }
    }

    private async Task<JsonNode?> QueryAsync(string query, JsonObject variables, string token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQl)
        {
            Content = new StringContent(new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token[7..] : token);
        var reply = await SendAsync(request, cancellation).ConfigureAwait(false);
        if (reply?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var message = errors[0]?["message"]?.ToString() ?? "error";
            if (message.Contains("JWT", StringComparison.OrdinalIgnoreCase) || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                throw new SignedOutException(Service);
            }

            throw new TrackerException("Hardcover", HttpStatusCode.UnprocessableEntity, message);
        }

        return reply;
    }

    private async Task<JsonNode?> FormAsync(string path, Dictionary<string, string> form, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://api.hardcover.app/{path}"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        return await SendAsync(request, cancellation).ConfigureAwait(false);
    }

    private async Task<JsonNode?> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        request.Headers.UserAgent.ParseAdd($"AsterTracking/{Version}");
        var client = _http.CreateClient("AsterTracking.Hardcover");
        client.Timeout = TimeSpan.FromSeconds(20);
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        JsonNode? reply = null;
        try
        {
            reply = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Not JSON: the status says enough.
        }

        // OAuth's errors: { "error": "authorization_pending" }, as RFC 8628 has them.
        if (reply?["error"] is JsonValue code && code.TryGetValue<string>(out var oauth))
        {
            throw oauth switch
            {
                "invalid_grant" or "access_denied" or "expired_token" or "invalid_token" => new SignedOutException(Service),
                _ => new OAuthError(oauth),
            };
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SignedOutException(Service);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException("Hardcover", response.StatusCode);
        }

        return reply;
    }

    private static LinkedAccount Tokens(JsonNode? reply) => new()
    {
        AccessToken = Text(reply, "access_token") ?? throw new TrackerException("Hardcover", HttpStatusCode.BadGateway),
        RefreshToken = Text(reply, "refresh_token"),
        Expires = Number(reply, "expires_in") is double seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null,
    };

    /// <summary>The author folder, as Aster's library lays books out: …/audiobooks/Author/[Series/]Book/file.m4b.</summary>
    private static string? AuthorFromPath(string? path)
    {
        var parts = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var root = Array.FindIndex(parts, p => p.Equals("audiobooks", StringComparison.OrdinalIgnoreCase));
        return root >= 0 && root + 1 < parts.Length - 1 ? parts[root + 1] : null;
    }

    /// <summary>Lower case, letters and digits only, single spaces; "The Hitchhiker's Guide" → "the hitchhikers guide".</summary>
    private static string Normal(string text) =>
        Spaces().Replace(NotWord().Replace(text.ToLowerInvariant().Replace("'", string.Empty, StringComparison.Ordinal).Replace("’", string.Empty, StringComparison.Ordinal), " "), " ").Trim();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static string? Text(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;

    private static double? Number(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private sealed record Book(int Id, long? AudioSeconds);

    private sealed record Entry(int Id, int Status, int? ReadId, int? ProgressSeconds, int? EditionSeconds);

    /// <summary>One of OAuth's named errors that isn't a sign-out ("invalid_client": the app id is wrong).</summary>
    private sealed class OAuthError : TrackerException
    {
        public OAuthError(string code)
            : base("Hardcover", HttpStatusCode.BadRequest, code)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
