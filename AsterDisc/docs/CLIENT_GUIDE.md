# Client implementer's guide

How to show Disc Menus in a Jellyfin client other than the web client: a native app for phones, TVs, consoles or desktops.

> **Read this first.** The web renderer ([`Web/discmenus.js`](../Jellyfin.Plugin.DiscMenus/Web/discmenus.js)) is the reference. This guide was
> written from it and checked against it, and the project is [vibe coded](../README.md): expect mistakes. **Where this guide and the
> reference disagree, the reference wins, and please open an issue.** The rules that need no screen (paging, grid cells, which button
> takes focus, time formatting, what to accept from untrusted data) are pinned down by a [conformance suite](#7-the-conformance-suite)
> of 600+ machine-checked cases, so for those the vectors, not this prose, are the authority.

- [1. What a client does](#1-what-a-client-does)
- [2. Talking to the server](#2-talking-to-the-server)
- [3. The document](#3-the-document)
- [4. Rules you must implement](#4-rules-you-must-implement)
- [5. Look and sound](#5-look-and-sound)
- [6. Web to native: what to substitute](#6-web-to-native-what-to-substitute)
- [7. The conformance suite](#7-the-conformance-suite)
- [8. Known gaps, ambiguities and compatibility](#8-known-gaps-ambiguities-and-compatibility)

## 1. What a client does

A **menu** is a small JSON file written by a person (see [Authoring menus](AUTHORING.md)): screens, buttons, layout, backgrounds, music. The
plugin on the Jellyfin server matches a menu to a library title by TMDB / IMDb / TVDB id, links its buttons to the title's real extras, and
serves the result. **A client never sees the menu file.** It sees a *resolved document*: the presentation parts of the menu, with the
server's item ids filled in.

```
 menu file ──► server (plugin) ──► resolved document ──► your client ──► draws screens, handles input,
 (shared)       matches + links       GET /DiscMenus/<id>/Menu           └─► plays items with your own player
```

Your job, in order:

1. When showing a movie, series or season, ask `GET /DiscMenus/{itemId}/Menu`. `404` means no menu: show nothing extra.
2. If there is one, offer it (the web client adds a **Disc Menu** button to the details page; it replaces nothing).
3. Draw the **root** screen, handle input, move between screens, and for the buttons that play something, play it with your own player.
4. When playback ends, come back to the menu where the viewer left it.

The format is data, not code: positions are percentages of the screen, and backgrounds, fonts, transitions and sounds are *named choices*,
so any UI toolkit can draw them. A menu can never run anything on your client.

### A minimal client

Enough for most menus, in the order that gets pictures on screen fastest:

1. Fetch and parse the document ([§2](#2-talking-to-the-server), [§3](#3-the-document)).
2. Resolve each screen's settings ([§4.1](#41-resolving-inherited-settings)) and place buttons by percentage ([§4.2](#42-coordinates-and-placement)).
3. Navigation: a stack of screens, directional focus, Select, Back ([§4.4](#44-navigation-and-actions)).
4. Playback: play the feature, an extra, a queue, a chapter ([§4.5](#45-actions-and-playback)).
5. Paging grids ([§4.3](#43-paging-grids-layoutflow)) and scene selection ([§4.6](#46-scene-selection)).
6. Backgrounds, theme, layers ([§5](#5-look-and-sound)), then transitions and audio, which are optional polish.

## 2. Talking to the server

Requirements: Jellyfin 12.x with the Disc Menus plugin installed. Authentication is the normal Jellyfin client authentication.

### `GET /DiscMenus/{itemId}/Menu`

- `itemId`: the id of a Movie, Series or Season (either GUID format). Only the item a menu is *bound to* has one.
- **200**: the resolved document ([§3](#3-the-document)). **404**: no menu for this item (also what you get when the plugin is not
  installed, so treat it as "nothing to show", never as an error). **401**: not signed in.
- Any signed-in user may call it. It sets no cache headers; the server rebuilds its index when menu files or the library change, so
  re-fetch when a viewer opens the title rather than caching for long.
- There is no endpoint for "which items have menus" for ordinary users, so ask per item.

**JSON conventions** (Jellyfin's standard serializer): property names are **PascalCase**, properties that would be `null` are **omitted**,
and GUIDs are written as **32 hex digits without hyphens** (`ac8363be07f34682846d703d101b5c0d`). Parse GUIDs leniently (with or without hyphens).
Enums are strings. Ignore properties you don't know, and treat unknown enum values as "use the default".

### `GET /DiscMenus/Assets/{path}`

Pictures and sounds that a menu ships with. A menu refers to them as `asset:<folder>/<file>` (1 to 4 segments, see [§4.8](#48-untrusted-data)); you
fetch `/DiscMenus/Assets/<folder>/<file>`, each segment URL-encoded. **This route needs no authentication** (an image or audio element cannot
send headers). It returns `404` for anything missing or not allowed. Types: `.webp` `.png` `.jpg` `.jpeg` images; `.mp3` `.ogg` `.opus` `.m4a` `.wav`
audio. It supports HTTP range requests and revalidation (`Cache-Control: no-cache` with `Last-Modified`).

### Standard Jellyfin routes a menu uses

| For | Route |
|---|---|
| A `jellyfin` background | `GET /Items/{itemId}/Images/{ImageType}/{Index}` (authenticated) |
| Chapter thumbnails | `GET /Items/{itemId}/Images/Chapter/{Index}?maxWidth=480&tag={ImageStamp}`, only when `HasImage` is true |
| A local trailer | `GET /Videos/{trailerItemId}/stream?static=true` |
| Theme-song music | `GET /Audio/{themeSongId}/stream?static=true` |
| Playing things | your own player and the normal playback and session APIs |

A `tmdb` background is fetched from `image.tmdb.org` ([§5.1](#51-backgrounds)). Other pictures are `https` links or inline data ([§4.8](#48-untrusted-data)).

### `GET /DiscMenus/Fanart/{itemId}/{imageId}`

For a `fanart` background. `itemId` is the item you asked for the menu of (a GUID) and `imageId` the background's `FanartId` (1 to 12 digits; validate both before building the URL). The server looks the picture up on
fanart.tv with its administrator's key, keeps a copy, and returns the image (`image/jpeg`, `image/png` or `image/webp`, `Cache-Control: public, max-age=86400`). **It needs no authentication** (like the asset route), and answers
a plain **404** for everything else: no key set, no such picture, fanart.tv unreachable, an item that has no usable id. Treat a 404 as "show the plain background", never as an error to display.

## 3. The document

The tables list every property the server can send. "Required" means always present; everything else is omitted when not set. Percentages
and other units are explained in the sections that use them.

### Document

| Property | Type | Notes |
|---|---|---|
| `MenuId` | GUID | The menu's identity. |
| `Root` | string | Key in `Menus` of the first screen. |
| `Menus` | map: key → Menu | Required. Keys match `^[a-z0-9][a-z0-9_-]{0,63}$`. |
| `Background`, `Theme`, `Layout`, `Audio` | objects | Document-wide **defaults**; each screen may override ([§4.1](#41-resolving-inherited-settings)). |
| `Chapters` | Chapter[] | Only present if some button needs chapters. May be empty. |
| `Trailers` | Trailer[] | Only present if a background uses a trailer. |
| `ThemeSongs` | GUID[] | Only present if the music is `themeSong`. May be empty. |

The document does **not** contain the item id: it is the one you asked for. "The feature" always means that item.

### Menu (`Menus[key]`)

| Property | Type | Notes |
|---|---|---|
| `Title` | string | Required. |
| `Entries` | Entry[] | Required: the buttons, in order. |
| `Background`, `Theme`, `Layout`, `Audio` | objects | Overrides ([§4.1](#41-resolving-inherited-settings)). |
| `MaxPerPage` | int | Not sent by the server for menus from files; used for the generated scene-selection screens ([§4.6](#46-scene-selection)). `0`/absent = no limit. |

### Entry

| Property | Type | Notes |
|---|---|---|
| `Action` | string | Required, see below. |
| `Label` | string | Required. Plain text (never markup). Also the accessible name. |
| `Position` | Position | Optional. Absent = the button flows in the default column or a `Layout.Flow` grid. |
| `Style` | `text` `frame` `glow` `arrow` | Optional. Falls back to `Layout.ButtonStyle`, then `frame`. |
| `Image`, `ImageFocus` | picture reference | Optional button artwork, and what to show while focused ([§5.2](#52-theme-and-buttons)). |

Fields by action. Anything not listed is absent.

| `Action` | Extra properties | What it means |
|---|---|---|
| `playFeature` | `StartChapter` (int, **1-based**, optional) | Play the item you asked for, from the start or from `Chapters[StartChapter-1].StartTicks`. |
| `playExtra` | `ItemId` (GUID, optional) | Play that extra. **Absent when the extra isn't matched** in this library: show the button disabled or say it is unavailable. |
| `playSequence` | `ItemIds` (GUID[]) | Play these in order as a queue. Extras that aren't matched are already dropped. **May be empty**: nothing to play. |
| `submenu` | `Menu` (key) | Push that screen. |
| `chapters` | `Menu` (key, optional), `PerPage` (int, default 6, max 24) | Build a scene-selection screen from `Chapters` ([§4.6](#46-scene-selection)). `Menu` names a screen whose title, background, theme and layout style it. |
| `back` | none | Go back one screen. |
| `home` | none | Return to the root. |

Two more actions exist **only inside the client**: `playChapter` (one per chapter, in scene selection) and the paging buttons `pagePrev` / `pageNext`
([§4.3](#43-paging-grids-layoutflow)). Do not expect them from the server. `Sub` (a caption) and `ThumbUrl` (a chapter thumbnail) are likewise
produced by the client for chapter buttons; **ignore any `ThumbUrl` that arrives in a document** and build it yourself.

### Position

`X`, `Y` (required), `W`, `H` (optional), `Anchor` (optional, default `top-left`): all numbers are **percent of the screen**. `Anchor` is one of
`top-left` `top` `top-right` `left` `center` `right` `bottom-left` `bottom` `bottom-right`.

### Chapter, Trailer

| Chapter | |
|---|---|
| `Index` | int, 0-based; used in the thumbnail URL |
| `Name` | string, may be absent |
| `StartTicks` | long, Jellyfin ticks (100 ns) |
| `HasImage` | bool: a thumbnail exists |
| `ImageStamp` | long, cache-buster, only with an image |

| Trailer | |
|---|---|
| `Kind` | `local` or `youtube` |
| `ItemId` | GUID, for `local` |
| `VideoId` | string, for `youtube` |
| `Name` | string, may be absent |

`StartChapter` in the menu file counts from 1; `Chapters[]` counts from 0. Local trailers come first, then YouTube.

### Background, Theme, Layout, Audio

| Background | |
|---|---|
| `Source` | Required: `jellyfin` `tmdb` `fanart` `color` `image` `trailer` |
| `Dim` | 0 to 1: how much black to lay over a picture. Default 0.4 (0.25 for `trailer`) |
| `ImageType`, `Index` | for `jellyfin`: Jellyfin image type (default `Backdrop`) and index (default 0) |
| `TmdbFilePath`, `TmdbSize` | for `tmdb`: a path like `/abc123.jpg`, size `w780` / `w1280` (default) / `original` |
| `Image` | for `image`: a picture reference |
| `Color` | for `color`: `#rrggbb` |
| `TrailerIndex`, `Muted`, `Poster` | for `trailer`: index into `Trailers` (default 0), muted (default **true**), a picture shown until the video plays |
| `FanartId` | for `fanart`: fanart.tv's numeric image id, 1 to 12 digits |

| Theme | |
|---|---|
| `Id` | Informational. The reference ignores it. |
| `Accent`, `TextColor` | `#rrggbb`. Defaults `#3ddc84` and `#ffffff` |
| `Align` | `left` (default) `center` `right` |
| `Font` | `sans` `serif` `condensed` `wide` `mono` |
| `FontSize` | percent of screen height, 1 to 10 |
| `Uppercase`, `Bold` | bool |
| `LetterSpacing` | em, 0 to 1 |

| Layout | |
|---|---|
| `TitlePosition` | Position for the screen title |
| `HideTitle` | bool |
| `ButtonStyle` | `text` `frame` `glow` `arrow` |
| `Flow` | `Region` (a Position with X, Y, W, H), `Columns` 1 to 8, `Rows` 1 to 12, `MoreLabel`, `PreviousLabel`: an automatic paging grid ([§4.3](#43-paging-grids-layoutflow)) |
| `Layers` | array of Layer |
| `Transition` | `Style` `none` `fade` `slide` `rise` `zoom` `wipe`; `DurationMs` 0 to 2000, default 300 |

| Layer | |
|---|---|
| `Type` | `panel` or `image` (anything else: skip it) |
| `Position` | where |
| `Fill`, `BorderColor` | `#rrggbb` (panel) |
| `BorderWidth`, `Radius` | percent of screen height (panel) |
| `Opacity` | 0 to 1 |
| `Image`, `Fit` | a picture reference, and `contain` (default) `cover` `fill` (image) |

| Audio | |
|---|---|
| `Music` | `{ Source: file / themeSong / none, File, Volume }` (volume 0 to 1, default 0.5) |
| `Sounds` | `{ Preset: none / click / chime / beep, Volume, Move, Select, Back }` (each of the last three is an audio reference) |

Nothing plays unless `Audio` is present. Real examples of whole documents are in [`conformance/documents/`](../conformance/documents).

## 4. Rules you must implement

### 4.1 Resolving inherited settings

The server does **not** merge document-wide defaults with a screen's own settings; you do. For the screen being shown:

| Setting | Rule |
|---|---|
| Theme | the screen's `Theme`, else the document's, else defaults. **Whole object**, not field by field. |
| Background | the screen's `Background`, else the document's. **Whole object.** |
| Layout | start from the document's `Layout`, then apply the screen's `Layout` **key by key**; a key whose value is `null` or missing is skipped (it does *not* remove an inherited one). A key is replaced whole: `Flow` and `Layers` are never merged inside. |
| Audio | `Music` from the screen's `Audio`, else the document's; `Sounds` likewise: **per part**. |
| Button style | the entry's `Style`, else the merged `Layout.ButtonStyle`, else `frame`. |

(Conformance: [`layout.json`](../conformance/layout.json) includes the merged layout of every screen.)

### 4.2 Coordinates and placement

- **Everything is a percentage of the full menu screen**, with the origin at the top left. The menu fills the display.
- A `Position` puts the element's **anchor point** at (`X`%, `Y`%): its left edge at `X`, its top at `Y`, then it is shifted by the anchor, as a percent of
  *its own* size:

  | Anchor | shift x | shift y |
  |---|---|---|
  | `top-left` (default) | 0 | 0 |
  | `top` | -50 | 0 |
  | `top-right` | -100 | 0 |
  | `left` | 0 | -50 |
  | `center` | -50 | -50 |
  | `right` | -100 | -50 |
  | `bottom-left` | 0 | -100 |
  | `bottom` | -50 | -100 |
  | `bottom-right` | -100 | -100 |

  An unknown anchor means `top-left`. `W` and `H`, when present, are the element's size in percent of the screen; without them it is as big as its
  content.
- **A screen is either fully positioned or fully flowed.** The server's checks reject a menu that mixes positioned and unpositioned buttons, so
  you can treat "every shown entry has a `Position`" as "positioned".
- **Default list** (no positions, no flow): a column of buttons with 0.5em between them and a padding of 4em around the screen, centred vertically
  as one block together with the title (which sits above the list with 0.75em below it). Horizontal alignment follows `Theme.Align`.
- **Title**: shown unless `Layout.HideTitle`; text is the screen's `Title`. With `Layout.TitlePosition` it is placed like any element; otherwise it is the first
  item of the column. Size: 2em, or `1.6 × FontSize` percent of screen height if `Theme.FontSize` is set.
- **Layers** are drawn first, behind the buttons, and take no input ([§5.3](#53-layers)).

### 4.3 Paging grids (`Layout.Flow`)

A flowed screen places its buttons automatically in a grid, adding **More** and **Previous** buttons when it needs more than one page. The loader
guarantees the grid has at least `max(4, pinned + 3)` cells, where `pinned` is the number of pinned buttons below.

`slots = Columns × Rows`. The **first** `back` entry and the **first** `home` entry are *pinned*: they appear on every page. Every other entry
(including a second `back`) is ordinary content.

```
paginate(entries, slots, maxPerPage):            # maxPerPage 0 or absent = no limit
    backs   = entries that are the first back or the first home, in ENTRY order
    content = all other entries, in order
    pages = [] ; taken = 0
    loop:
        prev = (pages is not empty)
        room = slots - len(backs) - (1 if prev else 0)       # cells left for content
        left = len(content) - taken
        if left <= min(room, maxPerPage or infinity):
            pages.append(items = content[taken:], backs, prev, more = false) ; return pages
        cap = min(max(1, room - 1), maxPerPage or infinity)  # one cell is kept for "More"
        pages.append(items = content[taken : taken + cap], backs, prev, more = true)
        taken += cap
```

A page's buttons, in drawing order: its content items, then its pinned buttons (in entry order), then **Previous** (if `prev`), then **More** (if `more`).
The content takes cells `0, 1, 2…` in row-major order; the **last `n` cells** go to the `n` other buttons in that order (there may be gaps between).
Previous and More are the synthetic actions `pagePrev` and `pageNext`, labelled `Flow.PreviousLabel` / `Flow.MoreLabel` or `Previous` / `More`.

Cell `slot` (0-based, row-major) is placed at:

```
shift      = ANCHOR_SHIFT[Region.Anchor or "top-left"]
left       = Region.X + shift.x/100 * Region.W          top = Region.Y + shift.y/100 * Region.H
cellW      = Region.W / Columns                          cellH = Region.H / Rows
Position.X = left + (slot % Columns + 0.5) * cellW
Position.Y = top  + (slot div Columns + 0.5) * cellH
Position.W = cellW * 0.94
Position.H = cellH * 0.94 if the entry has an Image (or is a chapter thumbnail), else absent
Position.Anchor = center
```

Conformance: [`paginate.json`](../conformance/paginate.json), [`cells.json`](../conformance/cells.json), [`layout.json`](../conformance/layout.json).

**Paging state.** Keep a current page per screen. Entering a screen from a button starts it on page 1 with the first button focused. **More** and
**Previous** change the page by one and focus the first button. Going **Back** to a screen you came from shows it on the page and with the focus you
left it on. **Home** forgets all pages (but not remembered focus).

### 4.4 Navigation and actions

State: a **stack** of screens (starts as `[Root]`; the top is shown), a page number and a remembered focus index per screen. Opening the menu begins
with the root on its first page and the first button focused.

Input, as the reference maps it (adapt it to your platform):

| Meaning | Keys | Gamepad |
|---|---|---|
| Move focus | arrow keys | d-pad, or the left stick past 0.6 |
| Select | Enter, numpad Enter, Space | A |
| Back | Escape, Backspace, "browser back", TV-remote back (webOS 461, Tizen 10009) | B |

Holding a direction on a gamepad repeats: first after 400 ms, then every 120 ms. A moved focus plays the `move` sound.

**Back** (key or pad): if the current screen is on a page after the first, go back one page (focus the first button); else pop the stack; at the root,
close the menu. The **on-screen** `back` button always goes up one screen (it does not step pages), and at the root it closes the menu.
**Home** (`home` action): the stack becomes `[Root]`, all page memory is cleared, the focus remembered for the root is kept.

**Select** on a button runs its action:

| Action | Effect |
|---|---|
| `submenu` | if `Menu` exists, push it (page 1, nothing remembered); otherwise do nothing |
| `back` / `home` | as above |
| `pagePrev` / `pageNext` | page ∓ 1 |
| `chapters` | build and push a scene-selection screen ([§4.6](#46-scene-selection)); if there are no chapters, tell the viewer |
| `playFeature`, `playExtra`, `playSequence`, `playChapter` | start playback ([§4.5](#45-actions-and-playback)) |

Cycles are allowed (a screen may link back to one already on the stack).

**Focus.** After every screen is drawn, focus the remembered button (an index into the *displayed* order) or the first. The key rule is *geometric*:
pressing a direction moves to the nearest button in that direction. Reference algorithm, on the buttons' on-screen rectangles in drawing order
(`from` = the focused one; for left/right use width, for up/down use height):

```
along(from, r, dx, dy):                          # how far r lies in the pressed direction, or none
    tol = 0.25 * (from.width if dx != 0 else from.height)
    gap = r.left - from.right          if dx ==  1      # edge to edge
          from.left - r.right          if dx == -1
          r.top - from.bottom          if dy ==  1
          from.top - r.bottom          otherwise
    if gap < -tol: return none                   # (the tolerance lets slightly overlapping neighbours count as ahead)
    return (centre(r) - centre(from)) · (dx, dy)

across(from, r) = |centre offset on the OTHER axis|
overlaps(from, r) = rectangles overlap on the other axis (strict inequalities)

for each other button r:
    ahead = along(from, r, dx, dy)
    if ahead is not none:
        key = (0, ahead) if overlaps(from, r)  else (1, ahead + 2 * across)     # same row/column first; sideways drift counts double
        best = r if key is smaller than best's key       # lexicographic; on an exact tie the EARLIER button keeps it
    else:
        behind = along(from, r, -dx, -dy)                # only buttons truly behind us can be wrap targets
        if behind is none: skip
        far = -behind                                    # more negative = farther behind
        wrap = r if no wrap yet, or far < wrap.far - 20, or (far < wrap.far + 20 and across < wrap.across)
target = best, else wrap, else nothing (focus stays)
```

Wrapping goes to the *farthest* button behind you, with a 20-pixel band counted as a tie that the straighter button wins; the result is that Up exactly reverses Down
on real layouts, including the off-column Back button. The constants are in CSS pixels; use the same units or scale them. Conformance: [`focus.json`](../conformance/focus.json).
Pointer input is simpler: moving the mouse over a button focuses it (only for a *real* move, so a menu appearing under a resting cursor doesn't steal focus), clicking selects it.

### 4.5 Actions and playback

| Button | Play |
|---|---|
| `playFeature` | the item you asked for, from `0` or from `Chapters[StartChapter-1].StartTicks` |
| `playExtra` | `ItemId` (if absent: unavailable) |
| `playSequence` | `ItemIds` in order as a queue (if empty: unavailable) |
| a chapter (scene selection) | the feature from that chapter's `StartTicks` |

The reference renderer starts playback by sending a `PlayNow` command to the browser's own session and then polls the session to notice when playback
ends, then reopens the menu. **A native client should not copy that.** Use your own player, report progress through the normal Jellyfin playback
APIs, and when the player stops (or the queue finishes), bring the menu back showing the same screen and page the viewer left (keep the stack,
pages and focus across playback). Re-fetch the document then; menu or library may have changed. When something can't play, tell the viewer; don't fail silently.

### 4.6 Scene selection

A `chapters` button generates a screen from `Chapters` (if there are none, tell the viewer "This title has no chapter markers" and stay put):

- One button per chapter, in order: label `Name`, or `Chapter N` (1-based) when it has none; caption (`Sub`) = the start time formatted as `h:mm:ss`, or `m:ss`
  under an hour (minutes not padded: `0:05`, `12:07`, `1:02:09`; [`time-format.json`](../conformance/time-format.json)); thumbnail = `/Items/{feature}/Images/Chapter/{Index}?maxWidth=480&tag={ImageStamp}` when `HasImage`.
  Thumbnail buttons are 16:9 pictures with the label and time below.
- Then the entries of the styling screen `Menu` (normally `back` and/or `home`), or a single `back` button labelled "Back" if there is none.
- The screen takes its title (falling back to the button's label), background, theme and layout from `Menu`. It is paged with `MaxPerPage = PerPage` (default 6).
- If neither `Menu`'s layout nor the document's layout has a `Flow`, use this default grid: `slots = PerPage + 3`; `Columns` = `max(1, PerPage)` for `PerPage ≤ 3`, 3 for `≤ 8`, otherwise 4;
  `Rows = ceil(slots / Columns)`; `Region = { X: 50, Y: 52, W: 86, H: 68, Anchor: center }`.
- Selecting a chapter plays the feature from its `StartTicks`.

### 4.7 Opening and closing

Open the menu from a user action. Start on the root with the intro transition ([§5.4](#54-transitions)). Closing (Back at the root, or the viewer leaving) stops the music, the trailer and
any transition. The reference also has an on-screen close button (top right).

### 4.8 Untrusted data

A menu is data **anyone can write**. Treat every string as hostile:

- **Text is text.** Labels, titles and captions are never markup, never a format string, never a URL. No menu setting can supply CSS, a font family, a script or a command; fonts,
  transitions and sound presets are chosen **by name from a fixed list**.
- **Pictures** (`Image`, `ImageFocus`, `Poster`, layer and background images) are accepted only as: an `https://` URL with no whitespace, quotes, parentheses, angle brackets or
  backslash; a base64 `data:` image of **png, jpeg or webp** only (never svg or gif); or `asset:<seg>(/<seg>){0,3}` with each segment 1 to 64 characters of `A-Z a-z 0-9 . _ -`
  starting with a letter or digit (so no `..`). Resolve `asset:` to `/DiscMenus/Assets/…`. Anything else: ignore it.
- **Audio** files: an `https://` URL as above, or an `asset:` reference ending in `.mp3 .ogg .opus .m4a` or `.wav`. No `data:` audio.
- **Colours** are exactly `#rrggbb` (six hex digits; not `#rgb`, not names, not `rgb()`); otherwise use the default.
- **fanart ids** (`FanartId`) are 1 to 12 ASCII digits and nothing else, and the item id you put in the `/DiscMenus/Fanart/` path must be a GUID (32 to 36 hex digits and hyphens). Validate both before building the URL.
- **TMDB paths** match `^/[A-Za-z0-9_-]+\.(jpg|png)$` and the URL is built as `https://image.tmdb.org/t/p/{size}{path}`.
- **Numbers** must be numbers and finite; clamp where a range is given.

The exact accept/reject cases (including the fanart id and item id) are in [`safety.json`](../conformance/safety.json). Matching is against the **whole** string.

## 5. Look and sound

These rules are described from the web reference and are *not* checked by the vectors, because they are about pictures and sound. Where a value is a
CSS unit, the unit is given so you can translate: **vh** is 1% of the screen height, **em** is the button font size.

### 5.1 Backgrounds

| `Source` | Draw |
|---|---|
| none given, or unknown | flat `#101010` |
| `fanart` | `GET /DiscMenus/Fanart/{item}/{FanartId}` (the server looks the picture up and serves it; see [§2](#2-talking-to-the-server)), as `image`. A `404` (no key configured, unknown id) means flat `#101010`. Never contact fanart.tv yourself |
| `color` | `Color` flat (`#101010` if invalid) |
| `image` | the picture, **cover**-fitted and centred over `#101010`, with a black layer of alpha `Dim` (default 0.4) on top |
| `jellyfin` | `/Items/{item}/Images/{ImageType or Backdrop}/{Index or 0}`, as `image` |
| `tmdb` | `https://image.tmdb.org/t/p/{size}{path}`, as `image` |
| `trailer` | a looping video, see below, with a black layer of alpha `Dim` (default **0.25**) over it |

- **Preload** every picture of every screen when the menu opens, so changing screens doesn't wait on the network.
- **Crossfade** between screens whose backgrounds differ: the old background fades out over the transition's duration while the new one is already in place (only when a
  transition style other than `none` is set and animation is allowed).
- **Trailer.** The video sits in its own layer *under* the menu and **keeps playing across screens**: don't restart it unless the trailer, `Muted` or `Poster` changes; remove it when the next
  screen has no trailer background, and on close. Choose `Trailers[TrailerIndex or 0]`; if there is none, show only `Poster`. It is **muted unless `Muted` is explicitly false**.
  Cover-fit a 16:9 video. Fade it in (about 0.8 s) when it starts playing, with `Poster` underneath until then (and as the fallback if it fails). **Subtitles and captions off.**
  Local trailers loop a normal stream; YouTube trailers (the reference embeds the privacy-enhanced player with autoplay, looping, controls and captions off) need a web view or your own extraction. Both are optional: showing
  `Poster` only is acceptable.

### 5.2 Theme and buttons

- **Fonts** (named): `sans` system UI sans-serif; `serif` Georgia-like; `condensed` Arial Narrow-like; `wide` Verdana-like; `mono` a monospace face. Substitute the nearest equivalent on your platform; unknown names mean the default.
- **Size**: button text is **1.25em** unless `FontSize` is set, which is a percentage of the **screen height** (vh). The screen title is **2em**, or `1.6 × FontSize` vh. `Uppercase`, `Bold` (weight 700),
  `LetterSpacing` (em).
- **Colours**: `TextColor` (default white); `Accent` (default `#3ddc84`) for focus. Text has a soft dark shadow (`0 2px 6px` at 80% black) so it reads on any background.
- **Button shape**: padding 0.5em × 1.25em, 2 px border (transparent unless focused), corner radius 0.4em. `Align` sets the text alignment.

| Style | Idle | Focused |
|---|---|---|
| `frame` (default) | 50% black background | 75% black background and an accent border |
| `text` | plain text | text turns accent |
| `glow` | plain text | accent glow around the text (`0 0 10px` and `0 0 22px`) |
| `arrow` | plain text | an accent "▶" appears before the text |

- **Artwork buttons** (`Image` is valid): the label is **not drawn** (it is only the accessible name). No padding or border. With a `W`/`H` position the picture fills it (contain-fitted); without, at most 40% of
  the screen width by 25% of the height. When focused show `ImageFocus` instead; with none, brighten the picture (about +15%) and give it an accent glow.
- **Chapter thumbnail buttons**: column layout, 16:9 cover-fitted thumbnail above a caption "Label · Sub", 35% black background (65% and an accent border when focused), small text (1.6 vh).
- Focus follows the keyboard, the gamepad and a *real* pointer move; it is always visible.

### 5.3 Layers

`Layout.Layers` are drawn **behind everything**, in order, ignoring input, and are rebuilt only when the layer list changes (they fade in over the transition's duration when they appear).
`panel`: `Fill` (transparent if missing), a border of `BorderWidth` vh in `BorderColor` (only if the width is above 0), corners of `Radius` vh. `image`: the picture, `Fit` (`fill` / `cover` / `contain`, default
contain). Both take `Opacity` and a `Position`. An unknown `Type`, or an image that isn't safe ([§4.8](#48-untrusted-data)), is skipped.

### 5.4 Transitions

`Layout.Transition = { Style, DurationMs }`: default style `none`, default duration 300 ms. **No animation at all** if the style is `none`, the duration is 0, or the platform's reduce-motion setting is on.
Only the screen (title and buttons) animates; background, video and layers do not. The old screen plays its *out* animation and the new its *in* animation **at the same time**, the out one with ease-in and the
in one with ease-out. `s` is +1 going forward (submenu, More) and -1 going back (Back, Home, Previous).

| Style | Out | In |
|---|---|---|
| `fade` | opacity 1 → 0 | 0 → 1 |
| `slide` | fade out and move 8% × s to the left | fade in from 8% × s to the right |
| `rise` | fade out and move 6% × s up | fade in from 6% × s below |
| `zoom` | fade out and scale to 1.08 (0.92 going back) | scale from 0.92 (1.08 going back) to 1 while fading in |
| `wipe` | fade out | reveal from left to right (from right to left going back) |

The first time the menu opens there is no old screen: play only the *in* animation. Reopening after playback: no animation, the old screen is replaced at once. A screen that is leaving takes no more input.

### 5.5 Audio

- **Opt-in**: nothing plays unless the (merged) `Audio` is present. Start sound from a user action (platforms that block autoplay need that).
- **Music** (`Music`): `file` (a safe audio reference) or `themeSong` (the **first** of `ThemeSongs`; none = silence). Always loops. Volume 0 to 1, default 0.5. The same track continuing across screens just
  fades to the new volume over 400 ms; a different track fades the old one out over 600 ms while the new one fades in over 800 ms from zero. Closing the menu fades the music out over 400 ms.
- **Button sounds** (`Sounds`): volume 0 to 1 (default 0.5). A file for `Move`, `Select` or `Back` wins over the preset. Kinds: `move` when focus moves (drop a move sound if one played less than 45 ms ago),
  `select` for activating a button, `back` for `back`, `home`, `pagePrev` and the Back key. The synthesised presets, as (frequency Hz, wave, duration s, gain, start offset s):

  | Preset | `move` | `select` | `back` |
  |---|---|---|---|
  | `click` | 1500 square 0.025 g0.5 | 1100 square 0.04 g0.6, then 700 square 0.05 g0.5 at 0.04 | 600 square 0.05 g0.6 |
  | `chime` | 1320 sine 0.12 g0.4 | 880 sine 0.2 g0.6, then 1320 sine 0.3 g0.5 at 0.08 | 660 sine 0.2 g0.5, then 440 sine 0.25 g0.5 at 0.08 |
  | `beep` | 880 sine 0.04 g0.5 | 1040 sine 0.09 g0.6 | 520 sine 0.09 g0.6 |

  Each tone ramps its gain exponentially from 0.0001 up to `max(0.0002, volume × g × 0.3)` over the first 5 ms, then down to 0.0001 by its end. If synthesis is awkward on your platform, pre-render these as short samples.
- Sounds stop with the menu.

## 6. Web to native: what to substitute

| In the web reference | A native client |
|---|---|
| CSS units (vh, vw, em), `transform: translate(%)`, `clip-path` | your layout units; compute percentages of the screen yourself |
| `getBoundingClientRect` for focus | the laid-out frames of your buttons (text-sized buttons depend on font metrics, so exact focus results on text buttons can differ slightly between platforms; image and flowed cells are exact) |
| Web Animations, `prefers-reduced-motion` | your animation system, the OS reduce-motion setting |
| Browser keyboard and gamepad events | your platform's remote and controller events |
| `Sessions/PlayNow` plus polling to detect the end | your own player and its end-of-playback event |
| `api_key` in URLs | your authenticated requests |
| YouTube iframe and `postMessage` | optional: a web view, or `Poster` only |
| WebAudio-synthesised sounds | synthesis or pre-rendered samples |
| `Dashboard.alert` | your platform's dialog |

## 7. The conformance suite

[`conformance/`](../conformance/README.md) holds language-neutral JSON test vectors generated from the reference renderer: the inputs and the exact outputs. Run your implementation over the inputs and compare.

| File | Pins down |
|---|---|
| `paginate.json` | paging ([§4.3](#43-paging-grids-layoutflow)) |
| `cells.json` | grid cell positions |
| `layout.json` | settings inheritance and the entries and positions of every page of the sample documents in `documents/` |
| `focus.json` | directional focus, wrapping and tie-breaking ([§4.4](#44-navigation-and-actions)) |
| `time-format.json` | chapter times |
| `safety.json` | what to accept from untrusted data ([§4.8](#48-untrusted-data)) |

A second, independent implementation of these rules in Python ([`conformance/python/`](../conformance/python)) was written from this guide and passes all of them; a *mutation test* confirms that the vectors fail
for every small mistake tried (a wrong constant, a dropped rule). That is the evidence the rules above are complete.

**Not covered**: anything visual or audible (§5), playback, and platform behaviour. Compare those by eye and ear against the web client, and feel free to propose more vectors.

## 8. Known gaps, ambiguities and compatibility

- **`fanart` backgrounds** need the server administrator to have set a fanart.tv key; without one every `fanart` page shows plain dark. `Theme.Id` is informational only.
- **No version field on the resolved document.** Ignore unknown properties and treat unknown enum values as defaults; the menu *file* carries `schemaVersion`, which the server handles for you.
- **The server does not check that the signed-in user may see the item** before returning its menu: the menu holds no secrets (titles, ids and layout), but treat it as presentation only, and let normal item permissions govern playback.
- **A `back` button on the root screen closes the menu** (in the reference); decide what your platform expects there.
- **Exact focus on text buttons** depends on font metrics and so can differ between platforms. The vectors use fixed rectangles.
- **Scene selection with no chapters** is only an alert in the reference; show something sensible.
- **Menu authors can't know your platform**: pictures are tied to 16:9 percentages. Keep the menu 16:9 and letterbox, or crop, as you see fit, and say which you chose.
- **Stability.** The plugin is an early alpha, and the document's shape can still change. The intention is that changes will add properties and enum values rather than rename or remove them, but that is not
  promised yet; the conformance vectors carry a `conformanceVersion` so you can tell when something moved. If something here is unclear, wrong or missing, **open an issue**: the guide is meant to be shaped by people who build clients.
