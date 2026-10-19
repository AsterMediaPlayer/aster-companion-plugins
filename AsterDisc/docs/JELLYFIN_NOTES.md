# Jellyfin 12.x notes

Facts about writing a plugin for **Jellyfin 12** (net10.0), each checked against the real
`Jellyfin.Controller` / `Jellyfin.Model` / `Jellyfin.Common` 12.0.0 packages or a running 12.1 server.
Jellyfin 12 is new: the official docs and the plugin template lagged behind it when this was written
(they still targeted 10.11 / net9.0), and some guesses that looked right turned out wrong. If you
extend the plugin, check a signature against the real assembly before trusting memory or a web search.

A cheap way to check: load the assembly from the NuGet cache with reflection and list the members of a
type, or just build and read the compiler's `CS0234` / `CS0246` error.

## Plugin basics

- `BasePlugin<TConfig>` is in `MediaBrowser.Common.Plugins`; its constructor takes
  `(IApplicationPaths, IXmlSerializer)`. Only `Name` is abstract.
- **Override `Id` and `Description` yourself** (matching `build.yaml`'s guid). `BasePlugin.Id` only
  populates from an assembly-level `[Guid]` attribute (most plugins don't set one), and the plugin
  manager treats the *assembly's* values as authoritative, rewriting `meta.json` to match on every
  startup. An un-overridden `Id` is `Guid.Empty`, which zeroes the manifest and breaks the dashboard
  sidebar entry and the plugin detail page (both are keyed by plugin id).
- `IHasWebPages` is in `MediaBrowser.Model.Plugins`; `IPluginServiceRegistrator` is in
  `MediaBrowser.Controller.Plugins`; `BasePluginConfiguration` is in `MediaBrowser.Model.Plugins`.
- Controllers need no special base class: plain `ControllerBase` + `[ApiController]` is enough, and MVC
  discovers the plugin assembly's controllers on its own (no `FrameworkReference` needed).
- `[Authorize(Policy = "RequiresElevation")]` is a real, registered policy for admin-only endpoints. A bare
  `[Authorize]` means "any signed-in user". `[AllowAnonymous]` is needed for anything a browser fetches
  without Jellyfin's auth header (an `<img>`, a script tag).
- Request bodies that must be accepted as raw text (so half-typed JSON can't trip model binding) are best
  read straight from `Request.Body`.
- API responses are serialised **PascalCase**.
- Plugin install layout: `<config>/data/plugins/<Name>_<version>/` holding the DLL and a `meta.json`.
  `assemblies` in `meta.json` is empty even for working plugins; the folder is scanned. `targetAbi`
  `12.0.0.0` loads fine on 12.1 (it is a minimum, not an exact match).
- Plugin configuration is stored as XML under `<config>/data/plugins/configurations/`; the root element is
  literally `PluginConfiguration` and children are property names.

## Library and items

- `BaseItem.ExtraType` (nullable), `.Path`, `.RunTimeTicks` (nullable), `.ProviderIds`, `.ProductionYear`,
  `.Width`, `.Height`, `.RemoteTrailers` (`IReadOnlyList<MediaUrl>`), `.OwnerId` all exist directly.
- `BaseItem.GetExtras(User)` takes `Jellyfin.Database.Implementations.Entities.User`, **not**
  `Jellyfin.Data.Entities.User` (that type exists too, which makes for a confusing compile error). It needs a
  user; for background work use a query instead:
  `ILibraryManager.GetItemList(new InternalItemsQuery { OwnerIds = [id], IncludeExtras = true })`, then filter
  the result in code (`OwnerId == id`) rather than relying on the query's semantics alone.
- `InternalItemsQuery` has `HasAnyProviderId` / `HasAnyProviderIds` (`Dictionary<string,string>`),
  `IncludeItemTypes` (`BaseItemKind[]`, namespace `Jellyfin.Data.Enums`), `OwnerIds`, `ExtraTypes`,
  `IncludeExtras`, `ParentId`, `Recursive`, `IsVirtualItem`, `Years`. Provider keys are `Tmdb`, `Imdb`, `Tvdb`.
- `ILibraryManager` raises `ItemAdded`, `ItemUpdated` and `ItemRemoved`
  (`EventHandler<ItemChangeEventArgs>`). `ILibraryPostScanTask` exists.
- Chapters: `IChapterManager.GetChapters(Guid itemId)` returns `ChapterInfo`
  (`StartPositionTicks`, `Name`, `ImagePath`, `ImageDateModified`, `ImageTag`). Chapter thumbnails exist only
  if the "Extract chapter images" task has run; the web URL is `/Items/{id}/Images/Chapter/{index}`.
- `ExtraType` (`MediaBrowser.Model.Entities`): `Unknown`, `Clip`, `Trailer`, `BehindTheScenes`,
  `DeletedScene`, `Interview`, `Scene`, `Sample`, `ThemeSong`, `ThemeVideo`, `Featurette`, `Short`. In the
  database it is an integer, in that order starting at 0.
- Item ids appear in both 32-hex ("N") and hyphenated ("D") form; `System.Text.Json`'s built-in `Guid`
  converter only reads the hyphenated form, so accept both with a custom converter built on `Guid.Parse`.
- `ExtraType` and `ImageType` are **not** decorated with a string-enum converter, so using them directly in a
  type deserialised from JSON needs `[JsonConverter(typeof(JsonStringEnumConverter))]` on your property.
- Jellyfin's `GET /Items/{id}/SpecialFeatures` needs a user; with an API key (no user) it throws unless you
  pass `userId`.
- `GET /Items/{id}/RemoteImages?type=Backdrop&providerName=TheMovieDb` lists a title's TMDB images without
  needing a TMDB key.

## Injecting a script into the web client

Jellyfin core has no supported way for a plugin to modify jellyfin-web. This plugin does it with its own
middleware (`IndexHtmlInjection.cs`), so nothing else needs installing:

- A plugin can register an `IStartupFilter` from its `IPluginServiceRegistrator`; the filter puts a middleware at the start of
  the web server's pipeline. Ours wraps `GET /web`, `/web/` and `/web/index.html`, buffers the answer, and adds one
  `<script>` before the last `</body>`. Details that matter: the base URL (`Request.PathBase`) goes into the script address, so a
  configured base URL works; the request is made unconditional and uncompressed (`Accept-Encoding`, `If-None-Match`... are dropped)
  because the page must be read as text and a browser must not keep a copy without the tag; the stale `ETag` and `Last-Modified`
  are removed; only a plain 200 HTML answer is changed; and a page that already has the tag is left alone.
- Jellyfin's own files are never edited.

The community **File Transformation** plugin does the same job for plugins that don't want to write middleware, and this plugin
still registers with it if it is installed (harmless, see "left alone" above). How that registration works:

- Register with `PluginInterface.RegisterTransformation(JObject payload)`; the payload has an `id` (a guid),
  `fileNamePattern` (a regex tested against the path relative to `/web/`, so use `^index\.html$`, anchored),
  and one of `transformationEndpoint` (File Transformation POSTs `{"contents": "..."}` and uses the
  response body as the new text), `transformationPipe`, or a reflection callback.
- **Cross-plugin type identity.** Every plugin loads in its own `AssemblyLoadContext`, so the
  `Newtonsoft.Json.Linq.JObject` your code builds is a *different type* from the one File Transformation
  expects, even though the names and versions match; passing it fails with
  `Object of type 'JObject' cannot be converted to type 'JObject'`. Don't reference Newtonsoft yourself: find
  the already-loaded assembly via `AssemblyLoadContext.All` and build the payload from a JSON string through
  its `JObject.Parse`.
- Injecting into `index.html` itself is a normal top-level page load, so a plain `<script src>` anywhere in
  the document runs; the "script must be inside the page element" rule below applies only to dashboard
  pages.

## jellyfin-web (client side)

- **Plugin config pages:** only the `div[data-role="page"]` element is inserted into the live DOM.
  Anything after it (including a trailing `<script>`) never reaches the page and **silently does nothing**.
  Put the script inside the page div. The classic `pageshow` event, `window.ApiClient` and `window.Dashboard`
  are all still available.
- **Dashboard sidebar** entries come from `GET /web/ConfigurationPages?enableInMainMenu=true`; only
  `EnableInMainMenu` and `MenuIcon` matter today.
- **Routing** is hash-based (`#/details?id=<guid>`); `hashchange` fires on in-app navigation.
- **`playbackManager` is not reachable** from an injected script (it is an ES module inside the bundle; there
  is no global and no AMD `require` bridge). One-click playback instead sends `PlayNow` to the browser's own
  session through the Sessions API (`POST /Sessions/{id}/Playing`, with `StartPositionTicks` to start at a
  chapter), finding the session by `ApiClient.deviceId()`.
- **Theming:** Jellyfin 12 exposes very few runtime CSS variables (a handful, and no colour palette), so a
  script can't theme itself from the user's skin; menus bring their own theme.
- `ApiClient.getImageUrl(itemId, { type, index })` appends `/index` whenever `index != null`, including `0`.
- jQuery is available as `window.jQuery`, but nothing here needs it.
- No Content-Security-Policy is sent by default, so third-party embeds (the YouTube trailer iframe) work; a
  reverse proxy in front of Jellyfin may add one.

## Dependency injection

Plugins can take any registered service in a constructor. Resolve optional ones lazily from `IServiceProvider`
and degrade gracefully if absent. An `IHostedService` registered in `RegisterServices` runs like any other.
