using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DiscMenus.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>
/// Loads disc-menu definitions from disk and resolves their playExtra/
/// playSequence entries to this server's local Special Features via the
/// paired binding file.
/// </summary>
public sealed class DiscMenuService : IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DiscMenuService> _logger;

    private readonly IServiceProvider _services;

    public DiscMenuService(ILibraryManager libraryManager, ILogger<DiscMenuService> logger, IServiceProvider services)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _services = services;
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemUpdated += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    /// <summary>Menu art lives in an "assets" folder beside the menu/binding files (never scanned as menus).</summary>
    public static string AssetsPath => Path.Combine(MenusPath, "assets");

    private static string MenusPath =>
        string.IsNullOrEmpty(Plugin.Instance?.Configuration.MenusPath)
            ? Path.Combine(Plugin.Instance!.DataFolderPath, "menus")
            : Plugin.Instance.Configuration.MenusPath;

    /// <summary>
    /// Every menu that is currently bound to a library item, keyed by that item's id: menus with a
    /// hand-written "*.binding.json" beside them plus those the plugin bound automatically. Served
    /// from a cached index that rebuilds when menu/binding files or the library change.
    /// </summary>
    public IReadOnlyDictionary<Guid, (MenuDocument Menu, BindingDocument Binding, string BindingPath)> ScanBindings() =>
        GetSnapshot().Bound.ToDictionary(kv => kv.Key, kv => (kv.Value.Menu, kv.Value.Binding, kv.Value.BindingPath));

    // ---- Automatic discovery ---------------------------------------------------
    // A menu file is bound to a library item by its TMDB/IMDB/TVDB ids. A binding file written by
    // hand next to the menu always wins (existing behaviour); otherwise the plugin finds the item
    // itself and keeps the binding in its own data folder, so shareable menu files stay clean and
    // a menu dropped into the folder just works.

    private sealed record BoundMenu(MenuDocument Menu, BindingDocument Binding, string BindingPath, string MenuPath, bool Auto);

    private sealed record Snapshot(
        string Signature,
        DateTime BuiltUtc,
        IReadOnlyDictionary<Guid, BoundMenu> Bound,
        IReadOnlyList<Api.MenuStatus> Statuses);

    private static readonly TimeSpan LibraryRebuildThrottle = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private Snapshot? _snapshot;
    private volatile bool _libraryDirty;

    private static string AutoBindingsPath => Path.Combine(Plugin.Instance!.DataFolderPath, "bindings");

    // Any library change might add the title a menu is waiting for, or a new extra. Just flag it:
    // the index is rebuilt lazily (and throttled) the next time something asks for it.
    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e) => _libraryDirty = true;

    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemUpdated -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
    }

    /// <summary>The outcome of building (or writing) a draft menu: the draft and its file name, or why not.</summary>
    public sealed record DraftOutcome(JsonObject? Draft, string? FileName, int StatusCode, string? Error);

    /// <summary>
    /// Builds a starter menu for a movie from what the library has for it: provider ids, real extras
    /// with types and durations, chapters. The result is validated by the same loader that reads
    /// hand-written menus before it is returned. Nothing is written.
    /// </summary>
    public DraftOutcome BuildDraft(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return new DraftOutcome(null, null, 404, "No such library item.");
        }

        if (item is not MediaBrowser.Controller.Entities.Movies.Movie)
        {
            return new DraftOutcome(null, null, 400, "Drafts are only built for movies for now.");
        }

        var extras = _libraryManager.GetItemList(new InternalItemsQuery { OwnerIds = new[] { item.Id }, IncludeExtras = true })
            .Where(i => i.OwnerId == item.Id && i.ExtraType is not null && i.RunTimeTicks is not null)
            .Select(i => new DraftExtra(i.Name ?? string.Empty, i.ExtraType!.Value.ToString(), TimeSpan.FromTicks(i.RunTimeTicks!.Value).TotalSeconds))
            .ToList();

        try
        {
            var draft = MenuDraftBuilder.Build(new DraftSource(
                item.Name ?? string.Empty,
                item.ProductionYear,
                item.Width > 0 ? item.Width : null,
                ProviderIdsOf(item),
                extras,
                GetChapters(item.Id).Count > 0));
            MenuFileLoader.ParseMenu(draft.ToJsonString(), "generated draft");

            var stem = MenuDraftBuilder.Slug(item.Name ?? string.Empty);
            if (stem.Length == 0)
            {
                stem = item.Id.ToString("N");
            }

            var fileName = stem + (item.ProductionYear is { } y ? "-" + y : string.Empty) + ".menu.json";
            return new DraftOutcome(draft, fileName, 200, null);
        }
        catch (ArgumentException ex)
        {
            return new DraftOutcome(null, null, 400, ex.Message);
        }
        catch (MenuValidationException ex)
        {
            _logger.LogError("A generated draft failed validation: {Errors}", string.Join("; ", ex.Errors));
            return new DraftOutcome(null, null, 500, "The generated draft failed validation: " + string.Join("; ", ex.Errors.Take(3)));
        }
    }

    /// <summary>
    /// Writes a draft into the menus folder, where discovery picks it up and binds it. Never
    /// overwrites a file, and refuses a title that already has a menu unless <paramref name="force"/>.
    /// </summary>
    public DraftOutcome WriteDraft(Guid itemId, bool force)
    {
        var outcome = BuildDraft(itemId);
        if (outcome.Draft is null)
        {
            return outcome;
        }

        if (!force && GetSnapshot().Bound.TryGetValue(itemId, out var existing))
        {
            return new DraftOutcome(null, outcome.FileName, 409,
                $"This title already has a menu ('{Path.GetFileName(existing.MenuPath)}'). Pass force=true to add another anyway.");
        }

        var path = Path.Combine(MenusPath, outcome.FileName!);
        try
        {
            Directory.CreateDirectory(MenusPath);
            // CreateNew: fails rather than overwrite, even if another request raced us to the name.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(outcome.Draft.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) when (File.Exists(path))
        {
            return new DraftOutcome(null, outcome.FileName, 409, $"'{outcome.FileName}' already exists; it was not overwritten.");
        }

        InvalidateSnapshot();
        return outcome;
    }

    /// <summary>A title in the library that a menu could be made for.</summary>
    public sealed record TitleHit(Guid Id, string Name, int? Year, string Type, bool HasIds);

    /// <summary>Movies and series whose name matches, for the editor's "new menu for a title" search (at most 15, best matches first).</summary>
    public IReadOnlyList<TitleHit> SearchTitles(string? query)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length < 2 || q.Length > 100)
        {
            return Array.Empty<TitleHit>();
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            SearchTerm = q,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
            Limit = 15,
        })
        .Select(i => new TitleHit(
            i.Id,
            i.Name ?? string.Empty,
            i.ProductionYear,
            i is MediaBrowser.Controller.Entities.Movies.Movie ? "Movie" : "Series",
            ProviderIdsOf(i).Any(p => p.Key.ToLowerInvariant() is "tmdb" or "imdb" or "tvdb" && !string.IsNullOrEmpty(p.Value))))
        .ToList();
    }

    /// <summary>
    /// Writes an EMPTY menu for a title (its ids, a Main Menu with Play) into the menus folder and returns its file name. Never overwrites: a taken name
    /// gets "-2", "-3"... The result is checked by the same loader that reads every menu.
    /// </summary>
    public DraftOutcome WriteBlank(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return new DraftOutcome(null, null, 404, "No such library item.");
        }

        var type = item switch
        {
            MediaBrowser.Controller.Entities.Movies.Movie => "Movie",
            MediaBrowser.Controller.Entities.TV.Series => "Series",
            MediaBrowser.Controller.Entities.TV.Season => "Season",
            _ => null,
        };
        if (type is null)
        {
            return new DraftOutcome(null, null, 400, "A menu can be made for a movie, a series or a season.");
        }

        var ids = item is MediaBrowser.Controller.Entities.TV.Season season && _libraryManager.GetItemById(season.ParentId) is { } series
            ? ProviderIdsOf(series)
            : ProviderIdsOf(item);
        try
        {
            var menu = MenuBlankBuilder.Build(type, ids, item.ProductionYear, (item as MediaBrowser.Controller.Entities.TV.Season)?.IndexNumber);
            var json = menu.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            MenuFileLoader.ParseMenu(json, "generated menu");

            var stem = MenuDraftBuilder.Slug(item.Name ?? string.Empty);
            if (stem.Length == 0)
            {
                stem = item.Id.ToString("N");
            }

            if (item.ProductionYear is { } y)
            {
                stem += "-" + y;
            }

            var editor = CreateEditor();
            for (var i = 1; i <= 50; i++)
            {
                var file = stem + (i == 1 ? string.Empty : "-" + i) + ".menu.json";
                var created = editor.Create(file, json);
                if (created.Kind == EditorResultKind.Ok)
                {
                    InvalidateSnapshot();
                    return new DraftOutcome(menu, file, 200, null);
                }

                if (created.Kind != EditorResultKind.Conflict)
                {
                    return new DraftOutcome(null, null, 400, created.Error ?? "The menu could not be created.");
                }
            }

            return new DraftOutcome(null, null, 409, "There are already too many menus with that name.");
        }
        catch (ArgumentException ex)
        {
            return new DraftOutcome(null, null, 400, ex.Message);
        }
        catch (MenuValidationException ex)
        {
            _logger.LogError("A generated empty menu failed validation: {Errors}", string.Join("; ", ex.Errors));
            return new DraftOutcome(null, null, 500, "The generated menu failed validation: " + string.Join("; ", ex.Errors.Take(3)));
        }
    }

    /// <summary>The menu editor's file access, pointed at the current menus folder and the plugin's backup folder.</summary>
    public MenuFileEditor CreateEditor() => new(MenusPath, AssetsPath, Path.Combine(Plugin.Instance!.DataFolderPath, "backups"));

    /// <summary>Installs and removes menus that come from the online catalogue, in the menus folder's "catalogue" subfolder.</summary>
    public CatalogueStore CreateCatalogueStore() => new(MenusPath, AssetsPath, Path.Combine(Plugin.Instance!.DataFolderPath, "backups"));

    /// <summary>How many local extras (special features) each of these library items has.</summary>
    public IReadOnlyDictionary<Guid, int> CountLocalExtras(IEnumerable<Guid> itemIds)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var id in itemIds.Distinct())
        {
            counts[id] = _libraryManager.GetItemList(new InternalItemsQuery { OwnerIds = new[] { id }, IncludeExtras = true })
                .Count(i => i.OwnerId == id && i.ExtraType is not null);
        }

        return counts;
    }

    /// <summary>The fanart.tv title a library item corresponds to (movies by TMDB id, series and seasons by TVDB id), if it has one.</summary>
    public Fanart.FanartTarget? GetFanartTarget(Guid itemId) => _libraryManager.GetItemById(itemId) switch
    {
        MediaBrowser.Controller.Entities.Movies.Movie m => Fanart.FanartTarget.For("Movie", ProviderIdsOf(m), null),
        MediaBrowser.Controller.Entities.TV.Series s => Fanart.FanartTarget.For("Series", ProviderIdsOf(s), null),
        MediaBrowser.Controller.Entities.TV.Season season when _libraryManager.GetItemById(season.ParentId) is { } series
            => Fanart.FanartTarget.For("Season", null, ProviderIdsOf(series)),
        _ => null,
    };

    /// <summary>Every movie and series in the library with its provider ids, for matching the catalogue against it.</summary>
    public IReadOnlyList<LibraryTitle> GetLibraryTitles() =>
        _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
        })
        .Select(i => new LibraryTitle(i.Id, i.Name, i.ProductionYear, i is MediaBrowser.Controller.Entities.Movies.Movie, ProviderIdsOf(i)))
        .ToList();

    /// <summary>Called after the editor writes a file, so the discovery index is rebuilt on next use.</summary>
    public void NotifyFilesChanged() => InvalidateSnapshot();

    /// <summary>The binding and item a menu file is currently bound to, if it is the one shown for its title.</summary>
    public BoundTitle? FindBindingFor(string fullMenuPath)
    {
        foreach (var (parentId, bound) in GetSnapshot().Bound)
        {
            if (string.Equals(bound.MenuPath, fullMenuPath, StringComparison.Ordinal))
            {
                return new BoundTitle(parentId, bound.Binding, _libraryManager.GetItemById(parentId)?.Name);
            }
        }

        return null;
    }

    /// <summary>A library item a menu file is bound to, with that binding's resolved extras.</summary>
    public sealed record BoundTitle(Guid ParentItemId, BindingDocument Binding, string? ItemName);

    /// <summary>Every menu file's discovery status, rebuilding the index first if files changed.</summary>
    public IReadOnlyList<Api.MenuStatus> GetStatuses() => GetSnapshot().Statuses;

    /// <summary>Forces a fresh look at the menus folder and the library (the "Scan now" button).</summary>
    public IReadOnlyList<Api.MenuStatus> Rescan()
    {
        lock (_gate)
        {
            _snapshot = null;
        }

        return GetSnapshot().Statuses;
    }

    private void InvalidateSnapshot()
    {
        lock (_gate)
        {
            _snapshot = null;
        }
    }

    private Snapshot GetSnapshot()
    {
        lock (_gate)
        {
            var signature = ComputeSignature();
            var current = _snapshot;
            var libraryStale = _libraryDirty && current is not null && DateTime.UtcNow - current.BuiltUtc > LibraryRebuildThrottle;
            if (current is null || current.Signature != signature || libraryStale)
            {
                _libraryDirty = false;
                var built = BuildSnapshot();
                // Building can write binding files, so take the signature again afterwards:
                // otherwise our own writes would look like an outside change and rebuild twice.
                current = built with { Signature = ComputeSignature() };
                _snapshot = current;
            }

            return current;
        }
    }

    // Cheap fingerprint of every menu/binding file (path, size, modified time). Menu edits made
    // on disk show up on the next request without any file-watcher.
    private static string ComputeSignature()
    {
        var sb = new StringBuilder();
        var assetsPrefix = Path.GetFullPath(AssetsPath) + Path.DirectorySeparatorChar;

        void Add(string dir)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                if ((!path.EndsWith(".menu.json", StringComparison.Ordinal) && !path.EndsWith(".binding.json", StringComparison.Ordinal))
                    || Path.GetFullPath(path).StartsWith(assetsPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var info = new FileInfo(path);
                sb.Append(path).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|').Append(info.Length).Append(';');
            }
        }

        Add(MenusPath);
        Add(AutoBindingsPath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private Snapshot BuildSnapshot()
    {
        var statuses = new List<Api.MenuStatus>();
        var claims = new List<(BoundEntry Entry, BoundMenu Bound, Api.MenuStatus Status)>();
        var root = MenusPath;

        if (Directory.Exists(root))
        {
            var assetsPrefix = Path.GetFullPath(AssetsPath) + Path.DirectorySeparatorChar;
            foreach (var menuPath in Directory.EnumerateFiles(root, "*.menu.json", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                if (Path.GetFullPath(menuPath).StartsWith(assetsPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var file = Path.GetRelativePath(root, menuPath);
                MenuDocument menu;
                try
                {
                    menu = MenuFileLoader.LoadMenu(menuPath);
                }
                catch (Exception ex) when (ex is JsonException or MenuValidationException or IOException)
                {
                    var why = ex is MenuValidationException v ? string.Join("; ", v.Errors.Take(3)) : ex.Message;
                    _logger.LogWarning("Menu file '{File}' could not be loaded: {Why}", file, why);
                    statuses.Add(new Api.MenuStatus { MenuFile = file, State = "invalid", Message = "This menu file can't be loaded: " + why });
                    continue;
                }

                var handPath = menuPath[..^".menu.json".Length] + ".binding.json";
                if (File.Exists(handPath))
                {
                    try
                    {
                        var binding = MenuFileLoader.LoadBinding(handPath);
                        if (binding.MenuId != menu.MenuId)
                        {
                            _logger.LogWarning("Binding '{Binding}' menuId does not match '{Menu}'", handPath, menuPath);
                            statuses.Add(new Api.MenuStatus
                            {
                                MenuFile = file,
                                MenuId = menu.MenuId,
                                State = "invalid",
                                Source = "manual",
                                Message = "The binding file next to this menu belongs to a different menu (menuId doesn't match).",
                            });
                            continue;
                        }

                        claims.Add(Claim(menu, binding, handPath, menuPath, file, auto: false));
                    }
                    catch (Exception ex) when (ex is JsonException or MenuValidationException or IOException)
                    {
                        _logger.LogWarning("Binding '{Binding}' could not be loaded: {Why}", handPath, ex.Message);
                        statuses.Add(new Api.MenuStatus
                        {
                            MenuFile = file,
                            MenuId = menu.MenuId,
                            State = "invalid",
                            Source = "manual",
                            Message = "The binding file next to this menu can't be loaded: " + ex.Message,
                        });
                    }

                    continue;
                }

                var (bound, status) = ResolveAutomatically(menu, menuPath, file);
                if (bound is not null)
                {
                    claims.Add(Claim(menu, bound, menuPath, file, auto: true));
                }
                else
                {
                    statuses.Add(status!);
                }
            }
        }

        // Two menus can't both be shown on one item: a hand-written binding beats an automatic one,
        // then the newer revision, then file order.
        var (_, losers) = MenuDiscovery.ResolveConflicts(claims.Select(c => c.Entry));
        var byParent = new Dictionary<Guid, BoundMenu>();
        foreach (var (entry, bound, status) in claims)
        {
            if (losers.TryGetValue(entry.MenuPath, out var winnerPath))
            {
                statuses.Add(new Api.MenuStatus
                {
                    MenuFile = status.MenuFile,
                    MenuId = status.MenuId,
                    State = "conflict",
                    Source = status.Source,
                    Title = status.Title,
                    Edition = status.Edition,
                    ItemId = status.ItemId,
                    ItemName = status.ItemName,
                    Message = $"Another menu ('{Path.GetFileName(winnerPath)}') is already used for this title.",
                });
            }
            else
            {
                byParent[entry.ParentItemId] = bound;
                statuses.Add(status);
            }
        }

        return new Snapshot(
            string.Empty,
            DateTime.UtcNow,
            byParent,
            statuses.OrderBy(s => s.MenuFile, StringComparer.Ordinal).ToList());
    }

    private (BoundEntry Entry, BoundMenu Bound, Api.MenuStatus Status) Claim(
        MenuDocument menu, BindingDocument binding, string bindingPath, string menuPath, string file, bool auto)
        => Claim(menu, new BoundMenu(menu, binding, bindingPath, menuPath, auto), menuPath, file, auto);

    private (BoundEntry Entry, BoundMenu Bound, Api.MenuStatus Status) Claim(
        MenuDocument menu, BoundMenu bound, string menuPath, string file, bool auto)
    {
        var binding = bound.Binding;
        var item = _libraryManager.GetItemById(binding.ParentItemId);
        var status = new Api.MenuStatus
        {
            MenuFile = file,
            MenuId = menu.MenuId,
            State = "bound",
            Source = auto ? "auto" : "manual",
            Title = menu.Menus.TryGetValue(menu.Root, out var rootMenu) ? rootMenu.Title : menu.Root,
            Edition = menu.Match.Release?.Edition,
            ItemId = binding.ParentItemId,
            ItemName = item?.Name,
            ExtrasTotal = menu.Extras.Count,
            ExtrasMatched = menu.Extras.Keys.Count(k => binding.Bindings.TryGetValue(k, out var b) && b.Status == BindingStatus.Matched),
            Message = item is null ? "The bound item is no longer in the library." : null,
        };
        return (new BoundEntry(menuPath, menu.MenuId, menu.Revision, !auto, binding.ParentItemId), bound, status);
    }

    // Finds (or re-validates) the library item for a menu without a hand-written binding, and keeps
    // that menu's extras matched. Returns a bound menu, or the status explaining why not.
    private (BoundMenu? Bound, Api.MenuStatus? Status) ResolveAutomatically(MenuDocument menu, string menuPath, string file)
    {
        var autoPath = Path.Combine(AutoBindingsPath, menu.MenuId.ToString("D") + ".binding.json");
        var title = menu.Menus.TryGetValue(menu.Root, out var rootMenu) ? rootMenu.Title : menu.Root;
        var edition = menu.Match.Release?.Edition;
        BindingDocument? binding = null;

        if (File.Exists(autoPath))
        {
            try
            {
                var existing = MenuFileLoader.LoadBinding(autoPath);
                if (existing.MenuId == menu.MenuId
                    && _libraryManager.GetItemById(existing.ParentItemId) is { } boundItem
                    && StillMatches(menu.Match, boundItem))
                {
                    binding = existing;
                }
            }
            catch (Exception ex) when (ex is JsonException or MenuValidationException or IOException)
            {
                _logger.LogWarning("Automatic binding '{Binding}' is unreadable and will be recreated: {Why}", autoPath, ex.Message);
            }
        }

        if (binding is null)
        {
            var (decision, note) = Discover(menu.Match);
            switch (decision.Outcome)
            {
                case DiscoveryOutcome.Pending:
                    return (null, new Api.MenuStatus
                    {
                        MenuFile = file,
                        MenuId = menu.MenuId,
                        State = "pending",
                        Title = title,
                        Edition = edition,
                        Message = note ?? "No matching title in the library yet. It will be linked as soon as it appears.",
                    });
                case DiscoveryOutcome.Ambiguous:
                    return (null, new Api.MenuStatus
                    {
                        MenuFile = file,
                        MenuId = menu.MenuId,
                        State = "ambiguous",
                        Title = title,
                        Edition = edition,
                        Message = "More than one library item fits this menu equally well, so none was chosen.",
                        Candidates = decision.Candidates
                            .Select(id => _libraryManager.GetItemById(id))
                            .Where(i => i is not null)
                            .Select(i => new Api.MenuStatusCandidate { Id = i!.Id, Name = i.Name, Year = i.ProductionYear })
                            .ToList(),
                    });
            }

            binding = new BindingDocument
            {
                SchemaVersion = 1,
                MenuId = menu.MenuId,
                MenuRevision = menu.Revision,
                ParentItemId = decision.ItemId!.Value,
                ResolvedAt = DateTimeOffset.UtcNow,
            };
            SaveAutoBinding(autoPath, binding);
            _logger.LogInformation("Menu '{File}' automatically bound to library item {Item}", file, binding.ParentItemId);
        }

        binding = AutoMatchExtras(menu, binding, autoPath);
        return (new BoundMenu(menu, binding, autoPath, menuPath, Auto: true), null);
    }

    private static void SaveAutoBinding(string path, BindingDocument binding)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MenuFileLoader.SaveBinding(path, binding);
    }

    private static Dictionary<string, string> ProviderIdsOf(BaseItem item) =>
        item.ProviderIds is null ? new Dictionary<string, string>() : new Dictionary<string, string>(item.ProviderIds);

    // Finds the library item for a menu by provider id. Never by file name or path.
    private (DiscoveryDecision Decision, string? Note) Discover(MatchInfo match)
    {
        var kind = match.ItemType == MatchItemType.Movie ? BaseItemKind.Movie : BaseItemKind.Series;
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            HasAnyProviderId = new Dictionary<string, string>(match.ProviderIds),
            Recursive = true,
            IsVirtualItem = false,
        });

        var decision = MenuDiscovery.Decide(
            match.ProviderIds,
            items.Select(i => new DiscoveryCandidate(i.Id, ProviderIdsOf(i))));

        if (match.ItemType != MatchItemType.Season || decision.Outcome != DiscoveryOutcome.Bound)
        {
            return (decision, null);
        }

        // A season menu is identified by its series' ids plus the season number.
        var seasons = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = decision.ItemId!.Value,
            IncludeItemTypes = new[] { BaseItemKind.Season },
            IsVirtualItem = false,
        });
        var season = seasons.FirstOrDefault(s => s.IndexNumber == match.SeasonNumber);
        return season is null
            ? (new DiscoveryDecision(DiscoveryOutcome.Pending, null, Array.Empty<Guid>()),
                $"The series is in the library but has no season {match.SeasonNumber} yet.")
            : (new DiscoveryDecision(DiscoveryOutcome.Bound, season.Id, new[] { season.Id }), null);
    }

    // Is a previously bound item still the title this menu describes? (Metadata can be corrected.)
    private bool StillMatches(MatchInfo match, BaseItem item)
    {
        if (match.ItemType == MatchItemType.Season)
        {
            return item is MediaBrowser.Controller.Entities.TV.Season season
                && season.IndexNumber == match.SeasonNumber
                && _libraryManager.GetItemById(season.ParentId) is { } series
                && MenuDiscovery.Decide(match.ProviderIds, new[] { new DiscoveryCandidate(series.Id, ProviderIdsOf(series)) }).Outcome == DiscoveryOutcome.Bound;
        }

        var rightKind = match.ItemType == MatchItemType.Movie
            ? item is MediaBrowser.Controller.Entities.Movies.Movie
            : item is MediaBrowser.Controller.Entities.TV.Series;
        return rightKind
            && MenuDiscovery.Decide(match.ProviderIds, new[] { new DiscoveryCandidate(item.Id, ProviderIdsOf(item)) }).Outcome == DiscoveryOutcome.Bound;
    }

    // Local extras of an item without needing a user (background work has none). Matching only ever
    // fills in extras that aren't already Matched/Manual/Ignored, and only writes when something
    // actually changed, so rebuilding the index doesn't churn files.
    private BindingDocument AutoMatchExtras(MenuDocument menu, BindingDocument binding, string bindingPath)
    {
        if (_libraryManager.GetItemById(binding.ParentItemId) is not { } parent)
        {
            return binding;
        }

        var candidates = WithDuration(
                _libraryManager.GetItemList(new InternalItemsQuery { OwnerIds = new[] { parent.Id }, IncludeExtras = true })
                    .Where(i => i.OwnerId == parent.Id))
            .Select(x => new LocalExtraCandidate(x.Item.Id, x.Type, x.DurationSec))
            .ToList();

        var pending = menu.Extras
            .Where(kv => !binding.Bindings.TryGetValue(kv.Key, out var existing)
                || existing.Status is not (BindingStatus.Matched or BindingStatus.Ignored))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var suggestions = DurationMatcher.Match(pending, candidates);

        var changed = suggestions.Any(kv =>
            !binding.Bindings.TryGetValue(kv.Key, out var existing)
            || existing.Status != kv.Value.Status
            || existing.ItemId != kv.Value.ItemId);
        if (!changed && binding.MenuRevision == menu.Revision)
        {
            return binding;
        }

        var updated = new BindingDocument
        {
            SchemaVersion = binding.SchemaVersion,
            MenuId = binding.MenuId,
            MenuRevision = menu.Revision,
            ParentItemId = binding.ParentItemId,
            ResolvedAt = DateTimeOffset.UtcNow,
            Bindings = new Dictionary<string, ExtraBinding>(binding.Bindings),
        };
        foreach (var (key, suggestion) in suggestions)
        {
            updated.Bindings[key] = suggestion;
        }

        SaveAutoBinding(bindingPath, updated);
        return updated;
    }

    /// <summary>
    /// Runs the duration matcher against a parent item's local extras and
    /// writes any newly-matched (or newly-unmatched) bindings back to disk.
    /// Bindings already Manual or Ignored are left untouched. There is no
    /// linking UI yet; this only produces auto-match suggestions.
    /// </summary>
    /// <param name="user">
    /// BaseItem.GetExtras requires a user for permission-scoping. Pass the
    /// requesting admin (from a future API endpoint) or a designated
    /// system/admin account for unattended runs.
    /// </param>
    public AutoMatchResult? RunAutoMatch(Guid parentItemId, User user)
    {
        if (!ScanBindings().TryGetValue(parentItemId, out var pair))
        {
            return null;
        }

        var (menu, _, bindingPath) = pair;
        // The cached binding is shared with concurrent readers, so edit a fresh copy from disk.
        var binding = MenuFileLoader.LoadBinding(bindingPath);
        if (_libraryManager.GetItemById(parentItemId) is not { } parentItem)
        {
            return null;
        }

        var candidates = WithDuration(parentItem.GetExtras(user))
            .Select(x => new LocalExtraCandidate(x.Item.Id, x.Type, x.DurationSec))
            .ToList();

        var pending = menu.Extras
            .Where(kv => !binding.Bindings.TryGetValue(kv.Key, out var existing)
                || existing.Status is not (BindingStatus.Matched or BindingStatus.Ignored))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var suggestions = DurationMatcher.Match(pending, candidates);
        foreach (var (key, suggestion) in suggestions)
        {
            binding.Bindings[key] = suggestion;
        }

        binding.ResolvedAt = DateTimeOffset.UtcNow;
        MenuFileLoader.SaveBinding(bindingPath, binding);
        InvalidateSnapshot();

        return new AutoMatchResult
        {
            Matched = suggestions.Values.Count(b => b.Status == BindingStatus.Matched),
            Unmatched = suggestions.Values.Count(b => b.Status == BindingStatus.Unmatched),
            Skipped = binding.Bindings.Count - suggestions.Count,
        };
    }

    /// <summary>
    /// Resolves every playExtra/playSequence entry reachable from a parent
    /// item's bound menu to a local BaseItem: the item's Special Features as
    /// defined by its disc menu.
    /// </summary>
    public IReadOnlyList<BaseItem> GetSpecialFeatures(Guid parentItemId)
    {
        if (!ScanBindings().TryGetValue(parentItemId, out var pair))
        {
            return Array.Empty<BaseItem>();
        }

        var (menu, binding, _) = pair;
        var extraKeys = menu.Menus.Values
            .SelectMany(m => m.Entries)
            .SelectMany(ExtraKeysOf)
            .Distinct();

        var items = new List<BaseItem>();
        foreach (var key in extraKeys)
        {
            if (!binding.Bindings.TryGetValue(key, out var b)
                || b.Status != BindingStatus.Matched
                || b.ItemId is not { } itemId)
            {
                continue;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>The item's own Jellyfin theme songs (local audio extras of type ThemeSong), in library order.</summary>
    public IReadOnlyList<Guid> GetThemeSongs(Guid parentItemId)
    {
        var songs = _libraryManager.GetItemList(new InternalItemsQuery
        {
            OwnerIds = new[] { parentItemId },
            ExtraTypes = new[] { ExtraType.ThemeSong },
            IncludeExtras = true,
        });

        // Filtered again in code: don't rely on the query's owner/extra-type semantics alone.
        return songs.Where(i => i.ExtraType == ExtraType.ThemeSong && i.OwnerId == parentItemId).Select(i => i.Id).ToList();
    }

    /// <summary>
    /// The feature's chapters, in order. Resolved lazily from the server's chapter manager so that
    /// if it were ever unavailable the menu still works, just without scene selection data.
    /// </summary>
    public IReadOnlyList<Api.RenderableChapter> GetChapters(Guid parentItemId)
    {
        var manager = _services.GetService(typeof(MediaBrowser.Controller.Chapters.IChapterManager))
            as MediaBrowser.Controller.Chapters.IChapterManager;
        if (manager is null)
        {
            _logger.LogWarning("Chapter manager unavailable; scene selection will have no chapters");
            return Array.Empty<Api.RenderableChapter>();
        }

        return manager.GetChapters(parentItemId)
            .Select((c, i) => new Api.RenderableChapter
            {
                Index = i,
                Name = c.Name,
                StartTicks = c.StartPositionTicks,
                HasImage = !string.IsNullOrEmpty(c.ImagePath),
                ImageStamp = string.IsNullOrEmpty(c.ImagePath) ? null : c.ImageDateModified.Ticks,
            })
            .ToList();
    }

    /// <summary>
    /// Trailers for a parent item, in the order a menu's trailerIndex counts them: local
    /// trailer files first, then the YouTube trailers Jellyfin has stored in its metadata.
    /// Non-YouTube remote trailers are skipped (they cannot be embedded safely).
    /// </summary>
    public IReadOnlyList<Api.RenderableTrailer> GetTrailers(Guid parentItemId)
    {
        var result = new List<Api.RenderableTrailer>();
        if (_libraryManager.GetItemById(parentItemId) is not { } parent)
        {
            return result;
        }

        // Filtered again in code: don't rely on the query's owner/extra-type semantics alone.
        var locals = _libraryManager.GetItemList(new InternalItemsQuery
        {
            OwnerIds = new[] { parentItemId },
            ExtraTypes = new[] { ExtraType.Trailer },
            IncludeExtras = true,
        });
        foreach (var item in locals.Where(i => i.ExtraType == ExtraType.Trailer && i.OwnerId == parentItemId))
        {
            result.Add(new Api.RenderableTrailer { Kind = "local", ItemId = item.Id, Name = item.Name });
        }

        foreach (var remote in parent.RemoteTrailers ?? Array.Empty<MediaUrl>())
        {
            if (YouTubeLink.TryGetVideoId(remote.Url) is { } videoId)
            {
                result.Add(new Api.RenderableTrailer { Kind = "youtube", VideoId = videoId, Name = remote.Name });
            }
        }

        return result;
    }

    /// <summary>
    /// Every local extra under a parent item that has a type and a runtime,
    /// regardless of whether it's already claimed by a binding - the pool an
    /// admin picks from when manually linking an unmatched key.
    /// </summary>
    public IReadOnlyList<(BaseItem Item, ExtraType Type, double DurationSec)> GetLocalExtrasWithDuration(Guid parentItemId, User user)
    {
        if (_libraryManager.GetItemById(parentItemId) is not { } parentItem)
        {
            return Array.Empty<(BaseItem, ExtraType, double)>();
        }

        return WithDuration(parentItem.GetExtras(user)).ToList();
    }

    /// <summary>Manually links an extra key to a specific local item, overriding any prior auto-match.</summary>
    public bool SetManualBinding(Guid parentItemId, string extraKey, Guid itemId) => TryUpdateBinding(
        parentItemId,
        extraKey,
        new ExtraBinding
        {
            Status = BindingStatus.Matched,
            ItemId = itemId,
            Method = BindingMethod.Manual,
            Confidence = 1.0,
        });

    /// <summary>Marks an extra key as intentionally unresolved; RunAutoMatch will never touch it again.</summary>
    public bool IgnoreBinding(Guid parentItemId, string extraKey) =>
        TryUpdateBinding(parentItemId, extraKey, new ExtraBinding { Status = BindingStatus.Ignored });

    /// <summary>Clears a manual link or an ignore flag back to Unmatched, so RunAutoMatch will consider it again.</summary>
    public bool ResetBinding(Guid parentItemId, string extraKey) =>
        TryUpdateBinding(parentItemId, extraKey, new ExtraBinding { Status = BindingStatus.Unmatched });

    private bool TryUpdateBinding(Guid parentItemId, string extraKey, ExtraBinding newBinding)
    {
        if (!ScanBindings().TryGetValue(parentItemId, out var pair))
        {
            return false;
        }

        var (menu, _, bindingPath) = pair;
        if (!menu.Extras.ContainsKey(extraKey))
        {
            return false;
        }

        var binding = MenuFileLoader.LoadBinding(bindingPath);
        binding.Bindings[extraKey] = newBinding;
        binding.ResolvedAt = DateTimeOffset.UtcNow;
        MenuFileLoader.SaveBinding(bindingPath, binding);
        InvalidateSnapshot();
        return true;
    }

    private static IEnumerable<(BaseItem Item, ExtraType Type, double DurationSec)> WithDuration(IEnumerable<BaseItem> items) =>
        items.Where(e => e.ExtraType is not null && e.RunTimeTicks is not null)
            .Select(e => (e, e.ExtraType!.Value, TimeSpan.FromTicks(e.RunTimeTicks!.Value).TotalSeconds));

    private static IEnumerable<string> ExtraKeysOf(MenuEntry entry) => entry switch
    {
        PlayExtraEntry e => new[] { e.Extra },
        PlaySequenceEntry e => e.Extras,
        _ => Array.Empty<string>(),
    };
}

public sealed class AutoMatchResult
{
    public required int Matched { get; init; }

    public required int Unmatched { get; init; }

    /// <summary>Bindings already Manual or Ignored, left untouched by this run.</summary>
    public required int Skipped { get; init; }
}
