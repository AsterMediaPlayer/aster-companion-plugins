# Authoring menus

A disc menu is a single JSON file, `<name>.menu.json`. This page is the reference for what can go in
one. The authoritative definition is [`schema/menu.schema.json`](../schema/menu.schema.json); the
[example menus](../examples/README.md) show every feature in working form, and the dashboard's
**Menu Editor** lets you edit one with a live preview (see [below](#the-menu-editor)).

- [The two files: menu and binding](#the-two-files-menu-and-binding)
- [Menus, entries and actions](#menus-entries-and-actions)
- [Placement and layout](#placement-and-layout)
- [Backgrounds](#backgrounds)
- [Scene selection](#scene-selection)
- [Music and button sounds](#music-and-button-sounds)
- [Transitions](#transitions)
- [Rules for shareable menus](#rules-for-shareable-menus)
- [Automatic discovery](#automatic-discovery)
- [Drafting a menu from your library](#drafting-a-menu-from-your-library)
- [The Menu Editor](#the-menu-editor)
- [Matching extras](#matching-extras)

## The two files: menu and binding

Menus are split in two so a menu can be shared publicly without leaking anything about your server.

| File | Shareable? | Contains |
|---|---|---|
| `*.menu.json` ([schema](../schema/menu.schema.json)) | **Yes** | The look and structure of the menu, which title it is for (TMDB / IMDB / TVDB ids plus release details such as format and year), and the *extras* it expects, each described by type and duration. Never file names, paths or local item ids. |
| `*.binding.json` ([schema](../schema/binding.schema.json)) | **Never** | Maps each of a menu's extra keys to *this server's* Jellyfin item ids. Local state. |

You normally never write a binding by hand: the plugin creates and maintains it for you (see
[Automatic discovery](#automatic-discovery)). A hand-written one next to a menu overrides it.

## Menus, entries and actions

A menu document has a `root` menu key and a `menus` object of named menus. Each menu has a `title` and a
list of `entries`. An entry has an `action` and a `label` (plain text only: `<` and `>` are not allowed):

| `action` | Does | Extra fields |
|---|---|---|
| `playFeature` | Plays the main movie | `startChapter` (1-based) to start partway in |
| `playExtra` | Plays one extra | `extra`: a key from the document's `extras` |
| `playSequence` | Plays several extras in order ("Play All") | `extras`: two or more keys |
| `submenu` | Opens another menu | `menu`: a key from `menus` |
| `chapters` | Opens scene selection | `perPage`, `menu` (see [Scene selection](#scene-selection)) |
| `back` | Returns to the parent menu | |
| `home` | Jumps to the root menu from any depth | |

The `extras` object lists what the release is expected to contain, keyed by a menu-local name:
`{ "making-of": { "type": "BehindTheScenes", "durationSec": 1843 } }`. The type is Jellyfin's `ExtraType`
(`Clip`, `Trailer`, `BehindTheScenes`, `DeletedScene`, `Interview`, `Scene`, `Sample`, `ThemeSong`,
`ThemeVideo`, `Featurette`, `Short`, `Unknown`).

Navigation works with a mouse, the keyboard, a TV remote and a gamepad. Arrow keys move to the nearest
button in that direction by on-screen position, so any layout is navigable. The Back key steps back a
page, then up a menu.

## Placement and layout

By default a menu's entries flow in a plain column. To recreate a real disc menu, add placement data.
Everything here is optional and additive, so simpler menus keep working.

- **Per entry:**
  - `position` is `{ x, y, w?, h?, anchor? }`, with all numbers as **percentages (0-100) of the menu
    screen**, so a layout scales to any display. `anchor` says which point of the button `x`/`y` refers to:
    `top-left` (default), `top`, `top-right`, `left`, `center`, `right`, `bottom-left`, `bottom`,
    `bottom-right`.
  - `style` is how the highlight is drawn: `text`, `frame`, `glow` or `arrow`.
  - `image` / `imageFocus` replace the text with artwork (the label becomes its accessible name).
- **Per menu**, in `layout`: `titlePosition`, `hideTitle`, `buttonStyle` (the default `style`), `layers`,
  `flow` and `transition`.
- **All or none:** within one menu either every entry has a `position` or none does. A menu using `flow`
  must have no positions at all.
- **Text styling**, in `theme`: `accent` (the highlight colour), `textColor`, `font` (one of `sans`,
  `serif`, `condensed`, `wide`, `mono`; never an arbitrary font name), `fontSize` (percent of the screen
  height), `uppercase`, `bold`, `letterSpacing`, `align`.
- **Layers**, in `layout.layers`: decorative pieces drawn behind the buttons, in order. A `panel` has a
  `position`, `fill`, `opacity`, `borderColor`, `borderWidth`, `radius`; an `image` has a `position`, an
  `image` reference, `fit` (`contain`, `fill`, `cover`) and `opacity`. They are not focusable.
- **Shared look:** a top-level `layout` (and `theme`, `background`, `audio`) is the default for every menu;
  a menu's own value overrides it field by field. Declare the banner and button style once and every
  submenu inherits them.
- **Automatic grid with paging (`layout.flow`):** `{ region, columns, rows, moreLabel?, previousLabel? }`.
  Entries without a `position` fill the region's cells left to right, top to bottom, so a submenu can sit
  inside the same banner without hand-placing anything. If they don't all fit, a **More** button (and
  **Previous** on later pages) is added in the grid's last cells. The first `back` and the first `home`
  entry are pinned to every page, like a disc's Return button. The grid needs at least
  `max(4, pinned + 3)` cells so the paging buttons never crowd out every entry.

## Backgrounds

`background` (top level, or per menu) has a `source`:

| `source` | Shows | Fields |
|---|---|---|
| `jellyfin` | One of the title's own Jellyfin images | `imageType` (e.g. `Backdrop`), `index` |
| `tmdb` | A TMDB backdrop, fetched from TMDB's image server | `tmdbFilePath` (`/abc123.jpg`), `tmdbSize` (`w780`, `w1280` default, `original`) |
| `fanart` | A picture from [fanart.tv](https://fanart.tv), looked up and served by your Jellyfin server | `fanartId`: fanart.tv's numeric image id (needs the server's fanart.tv key, see below) |
| `image` | Your own picture | `image`: an `asset:` reference or `https://` URL |
| `color` | A flat colour | `color` |
| `trailer` | The title's own trailer, looping behind the menu | `trailerIndex`, `muted`, `poster` |

All accept `dim` (0-1) to darken the picture for legibility. Different menus can have different
backgrounds; the renderer preloads them and crossfades between them. A title's TMDB backdrops can be
listed with `GET /Items/{id}/RemoteImages?type=Backdrop&providerName=TheMovieDb`.

**fanart.tv backgrounds.** `fanart` shows a picture from fanart.tv's artwork for the title: backgrounds, but also logos, clear art, discs and banners. Unlike `tmdb`, it is the **server**
that fetches it, with the administrator's own free fanart.tv API key (Dashboard, Disc Menus, *fanart.tv API key*), and keeps a copy in its cache; viewers never contact fanart.tv. That makes it
work for any client that asks the server, and keeps fanart.tv's traffic and the key on your server. Details:

- **The easy way: the Menu Editor's picker.** Open a menu that is linked to a library title, press **Fanart…** above the preview, choose a category (*Backgrounds* is listed first), choose whether the
  picture is for **every page** or **one page**, set how much to darken it, and click a picture. The editor changes only the `background` property in your text (your formatting and everything else stay as they
  were), refreshes the preview, and offers **Undo**; press **Save** to keep it. Pictures are shown best-liked first, twelve at a time.
- **Finding an id by hand.** `fanartId` is the numeric image id from fanart.tv's listing. As an administrator, `GET /DiscMenus/Fanart/List/{itemId}` lists every picture's `Id`, `Category`
  (such as `moviebackground` or `hdmovielogo`), language and likes. Movies are looked up by their TMDB id and series and seasons by their series' TVDB id, so the title needs one.
- **Without a key**, or for an id fanart.tv doesn't have for that title, the page shows the plain dark background. A shared menu that uses `fanart` works for anyone who has set a key.
- **The key** is stored in the plugin's settings, sent only to fanart.tv's API (in a header, never in a link, so it cannot appear in a log), never shown again in the page and never sent to a client.
  The *Test the saved key* button checks it. fanart.tv's own terms apply to your use of their service.
- **Safety.** Only pictures on fanart.tv's own image host are fetched; each is size-limited and checked to really be an image before it is kept or served.
  `GET /DiscMenus/Fanart/{itemId}/{imageId}` is how a client gets one (it needs no sign-in, like the asset route, and answers a plain 404 for anything it can't serve).

**Trailer backgrounds.** `trailerIndex` (default 0) counts the title's local trailer files first, then
the YouTube trailers Jellyfin has stored for it. YouTube trailers play in a muted, looping, borderless
privacy-enhanced embed (`youtube-nocookie.com`), faded in only once the player reports it is playing;
local files play in a plain `<video>`. The `poster` image shows until the video plays and stays if the
title has no usable trailer or embedding is refused. The video sits in its own layer, so it keeps
looping across submenus and pages and stops when the menu closes. Captions are always suppressed.
Caveats: YouTube playback needs internet access and tells Google the viewer watched it; some trailers
disallow embedding; a local trailer only plays if the browser can decode the file directly.

## Scene selection

A `chapters` entry, `{ "action": "chapters", "label": "Scene Selection", "perPage": 6 }`, opens a screen
generated from the feature's chapters: one button per chapter, paged with More / Previous, with Back
pinned. Each chapter starts the movie at that chapter. Buttons show Jellyfin's chapter thumbnail when it
has extracted them (the "Extract chapter images" task), otherwise the chapter name and start time. With
no further settings it uses a built-in 3x3 grid.

Add `"menu": "<key>"` to style the screen with one of your own menus: that menu's title, background,
theme and layout are used, the chapters are placed first (at most `perPage` per page), and its own
entries (typically `home` / `back`) stay pinned. Set `"layers": []` in that menu's layout to switch off
an inherited banner for the screen.

## Music and button sounds

Audio is **opt-in**: nothing plays unless a menu asks. A top-level `audio` is the default for every menu;
a menu's own `audio` replaces it per part (its `music` replaces the default music, its `sounds` the
default sounds).

- **`music`:** `{ "source": "file", "file": "asset:folder/track.mp3", "volume": 0.5 }`, or
  `{ "source": "themeSong" }` for the title's own Jellyfin theme song (silent if it has none), or
  `{ "source": "none" }` to silence a menu. A file is an `https://` URL or an `asset:` reference ending in
  `.mp3`, `.ogg`, `.opus`, `.m4a` or `.wav`. Music loops with fades, keeps playing across menus that name
  the same track, and crossfades when the track changes.
- **`sounds`:** `{ "preset": "click|chime|beep|none", "volume": 0.5, "move"?, "select"?, "back"? }`. The
  presets are synthesised in the browser, so a menu needs no files for basic feedback; any of
  move / select / back can be replaced by an audio file.

Everything stops when the menu closes or playback starts. Browsers only allow audio after a click, which
the viewer's click on **Disc Menu** provides.

## Transitions

`layout.transition` is `{ "style": "none|fade|slide|rise|zoom|wipe", "durationMs": 300 }`. Moving into a
submenu or the next page animates forward; Back and Previous animate the other way. Only the buttons and
title animate: the background, a trailer video and any layers shared by both menus stay put. The
viewer's "reduce motion" setting is honoured.

## Rules for shareable menus

Menus are meant to be downloaded and shared, so they are treated as **untrusted input**: the plugin's
loader and the browser both re-check everything, and a menu can contain data but never code.

- **Each key once.** A property may appear only once in an object, and a menu or extra key only once in its map. A repeated key (which JSON itself would resolve by keeping the last one) is an error, so a form can never edit "the wrong one".
- **No binaries.** An image or audio reference is an `https://` URL, an `asset:<folder>/<file>`
  reference to a file in the server's menu `assets` folder, or (images only) a small
  `data:image/(png|jpeg|webp);base64,` URI. SVG is refused. URLs may not contain whitespace, quotes,
  parentheses or angle brackets.
- **Only studio-free art.** Don't put studio artwork, music or video into a shared menu or its assets. A
  menu should be layout plus ids.
- **Privacy.** An `https://` image or audio file is fetched by the viewer's browser from that host, so the
  host sees the viewer's IP address; use `asset:` files, `data:` icons or the title's own Jellyfin images
  if that matters. TMDB images and YouTube trailers are likewise fetched from those services. `fanart` pictures are the exception:
  your server fetches them, so viewers never contact fanart.tv.
- **Assets** live in `<menus folder>/assets/<folder>/` and are served (images and audio only, no path
  escapes) by `GET /DiscMenus/Assets/<folder>/<file>`.

## Automatic discovery

Drop a `*.menu.json` into the menus folder and the plugin does the rest:

- **Finds the title** by the menu's `match` provider ids (TMDB / IMDB / TVDB), through Jellyfin's own
  provider-id lookup, never by file name or path. An item counts if at least one id matches and none
  contradicts (an item with a different TMDB id is a different title even if an IMDB id coincides);
  matching more ids wins; two equally good items are **ambiguous** and nothing is chosen. A season menu
  matches its series by id and then the season number.
- **Links the extras** by type and duration, without needing a signed-in user. Only extras that aren't
  already matched, manual or ignored are touched, and files are only written when something changes.
- **Keeps its own bindings** in the plugin's data folder (`bindings/<menuId>.binding.json`), so shareable
  menu files stay untouched. A `<name>.binding.json` written by hand next to a menu always takes priority
  and is never modified automatically.
- **Stays current:** the index is cached and rebuilt when a menu or binding file changes, or (throttled to
  once per five seconds) after the library changes, so a title or extra added later gets linked on its
  own, and a corrected id re-binds. If two menus claim one title, a hand-written binding wins, then the
  newer `revision`, then file order; the other is reported as "not used".
- **Shows its work:** `GET /DiscMenus/Status` (admin) and the **Menu Files** table on the plugin's
  dashboard page list every menu file as bound, waiting for its title, needs review, not used, or can't be
  loaded; `POST /DiscMenus/Scan` and the **Scan now** button force a re-check.

If a menu is ambiguous (for example you hold two copies of the same movie) nothing is bound. To choose,
write a binding by hand next to the menu as `<same name>.binding.json`:

```json
{
  "schemaVersion": 1,
  "menuId": "<the menuId from the menu file>",
  "menuRevision": 1,
  "parentItemId": "<your library item's Jellyfin id>",
  "bindings": {}
}
```

then use **Auto-Match** on the plugin's dashboard page to link its extras. (Telling apart several copies
automatically, by format and edition, is on the [roadmap](ROADMAP.md).)

## Drafting a menu from your library

`GET /DiscMenus/Draft/{itemId}` (admin) previews a starter menu built from what the library has for a
movie: its TMDB / IMDB ids, its real local extras with their types and durations, and a Scene Selection
entry if it has chapters. Up to eight extras are listed flat; more are grouped by type (Featurettes,
Shorts, ...) with a Play All, and a type with a single extra becomes a direct entry. Labels come from the
files' own names with rip clutter removed (`Featurette - title 083` becomes `Featurette 1 (5:10)` so the
files can be told apart).

`POST /DiscMenus/Draft/{itemId}` writes it into the menus folder as `<title>-<year>.menu.json`, where
discovery binds it and matches the extras straight away. It never overwrites a file and refuses a title
that already has a menu unless `force=true`; every draft passes the same validation as a hand-written
menu. A draft is a starting point: edit the labels and design, and don't share it as it is (its labels
come from your rip, not the disc).

## The Menu Editor

A **Menu Editor** page sits in the Jellyfin dashboard sidebar. Pick a menu file, edit its JSON, and watch
the live preview update beside it.

- **The preview is the real viewer.** It runs the same renderer in a 1920x1080 frame scaled to fit, so
  layouts, text sizes, clicks, remote keys, transitions, backgrounds, trailers and scene selection behave
  exactly as for viewers. A **Show menu** picker jumps to any menu in the file and follows you as you
  click through the preview. If the file is bound to a library title the preview uses that title's real
  extras, chapters and trailers; otherwise it says what it lacks. Anything the menu "plays" is reported
  and never reaches your own session. It starts with sound off.
- **Errors as you type.** Problems come from the same loader the server uses, with line and column for
  syntax errors (click to jump there). Every other problem names the part it is about (for example
  `menus.main.entries[2].menu`): click it to select that text. While the text is invalid the preview keeps
  the last good version.
- **Parts and forms.** The list on the left shows the parts of the menu (extras, pages, buttons, decorative
  layers); problems are marked on the part they are in. Choose one to select its text and edit it in the
  form below: the text of a button, what it does, where it sits, a background, colours, music, the layout
  grid and more, with choices taken from your real extras and pages and the schema's limits explained
  next to the field. A form change edits only that part of the text, so the rest of the file keeps your
  formatting, and Ctrl+Z takes it back in one step. Add, move and remove buttons, pages, extras and layers.
  A picture stored inside the menu shows as a thumbnail, never as a block of text.
- **Saving is careful.** It writes only existing `*.menu.json` files inside the menus folder; refuses text
  that wouldn't load, a changed `menuId` (bindings are tied to it) or a lower `revision`; bumps the
  revision for you when you changed something; keeps the previous version as a timestamped backup (the
  newest 25 per file, in the plugin's data folder); and checks the file hasn't changed on disk since you
  opened it, offering to load the server's version or overwrite if it has. Ctrl+S saves.
- **Managing files.** **New…** finds a movie or series in your library and makes an empty menu for it (its
  ids and a Main Menu with Play) or, for a movie, a starter built from its real extras and chapters; it never
  overwrites a file. **Duplicate** copies the open menu beside it with a fresh `menuId` and revision 1.
  **Delete** removes a menu you made (a backup is kept); menus installed from the catalogue are removed on
  the Menu Catalogue page instead. **Backups…** lists the saved earlier versions; **Restore** puts one back
  as a new revision with the current `menuId` (a revision never goes down) and backs up what it replaces.
- **Full-size designer.** **Open full size** (in the bar) opens the same editor in a window of its own: the outline and form as a sidebar
  on the left and the preview as large as the window allows, with the text as a drawer on the right and panels floating over the page.
  Jellyfin always draws a plugin's dashboard page inside its own frame, so this is a separate page the plugin serves
  (`/DiscMenus/web/designer.html`, optionally `?file=<menu file>`). It is the editor page's own markup and code, so the two always match. It
  uses the sign-in your browser already has for Jellyfin (it must be an administrator's); with none, or one the server refuses, the page
  says so and links to Jellyfin. It opens the saved version of the menu, so save first if you have unsaved changes in the dashboard editor.
- **Arranging on the preview.** **Arrange** turns the preview into a canvas: click a button, the title or a decorative layer to select
  it (the outline and form follow), drag it to move it, drag a round handle to resize a layer or a button you gave a width or height.
  Positions are percentages of the screen, snapped to half a percent and to the centre lines (hold **Alt** for no snapping); the arrow
  keys nudge the selection by half a percent (**Shift** for five); **Esc** lets go. A move is one edit of the text, so one **Undo**
  takes it back. A page that lays its buttons out itself can't have them dragged: **Let me place the buttons freely** gives each
  button the place it has now (and removes that page's own automatic grid, because a grid and positions can't be mixed). Moving
  a title that takes its place from the whole menu's layout gives that page its own copy instead of moving every page's title.
  The note under the preview explains, for the page you are on, why its buttons can't be dragged and offers the next step: for a grid
  that pages it gives the numbers (for example "5 across by 1 down, 5 spots, but 6 buttons") with **Make the grid big enough** and **Make it
  big enough and place freely**; for any grid **Show this page's grid settings** opens its columns, rows and area in the form. A page that
  a Scene Selection button names as its style keeps its grid (it is where the scene pictures go). The generated scene-selection screen can't be arranged. Arranging is hidden on narrow windows.
- **Pictures, sounds and TMDB backdrops.** A picture or sound field has a **Choose or upload…** button: it lists the files uploaded for
  this menu (kept in `assets/<menuId>/` on the server), lets you upload another and uses the one you pick as an `asset:` reference. Pictures
  are png, jpg or webp up to 5 MB; sounds are mp3, ogg, opus, m4a or wav up to 25 MB; at most 200 files and 100 MB per menu. The server
  checks that a file really is what its name says (no SVG, no HTML dressed as a picture), reduces the name to the characters an `asset:`
  reference allows, and never replaces an existing file. **Uploaded files are public**: assets are served without sign-in (a browser
  cannot send one when it loads a picture), so anyone who can reach your server and knows the address can fetch them. Upload only what you
  have the right to share. A TMDB background has **Pick a TMDB backdrop…**, which lists the title's backdrops from Jellyfin's own
  TheMovieDb provider; viewers' browsers load the chosen picture from TMDB's image server.
- **Admin API:** `GET /DiscMenus/Editor/Files`, `GET /DiscMenus/Editor/File?name=`,
  `PUT /DiscMenus/Editor/File?name=&version=` (the body is the menu text), `DELETE /DiscMenus/Editor/File?name=`,
  `GET /DiscMenus/Editor/Titles?q=`, `POST /DiscMenus/Editor/New?item=&kind=blank|draft`,
  `POST /DiscMenus/Editor/Duplicate?name=`, `GET /DiscMenus/Editor/Backups?name=`,
  `POST /DiscMenus/Editor/Restore?name=&backup=&version=`, `GET|POST|DELETE /DiscMenus/Editor/Assets?menu=<menuId>&name=` (POST
  takes the file as the raw body) and
  `POST /DiscMenus/Editor/Preview?file=` (the body is the menu text; returns exactly what the player
  endpoint would serve for it).

Not in the editor yet: a guided way to build a grid or banner, and arranging scene-selection screens. See the [roadmap](ROADMAP.md).

## Checking a menu from the command line

`python3 tools/menucheck.py my.menu.json` checks a menu against the schema and the cross-reference rules without
a Jellyfin server (it needs `pip install jsonschema`); `dotnet run --project tools/MenuCheck -c Release -- my.menu.json`
checks it with the plugin's own loader. See the [development guide](DEVELOPMENT.md#checking-a-menu-file).

## Matching extras

Extras are matched by `type` (Jellyfin's `ExtraType`) plus `durationSec` (within `toleranceSec`, default
3 seconds), with `ordinal` (position among same-type extras, sorted by duration) as a tiebreaker, then
manual linking from the dashboard. Never by file name: a menu author's file organisation won't match
anyone else's.
