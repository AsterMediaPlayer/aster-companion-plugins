# Roadmap

What exists, and what's open. If you want to help, this is the list: items marked **good first** are
self-contained and don't need deep knowledge of the codebase; items marked **big** deserve a short
conversation (open an issue) before you start.

## Where things stand

Working today, on Jellyfin 12.x with the web client:

- Menus as shareable JSON: placement and layout, a paging grid, layers, text styling, five background
  kinds (Jellyfin image, TMDB backdrop, your own image, colour, looping trailer), music and button sounds,
  transitions, scene selection from chapters.
- One-click playback, with the menu reappearing when playback ends; keyboard, remote and gamepad navigation.
- **Automatic discovery:** a menu dropped into the folder is bound to the right title by TMDB / IMDB / TVDB
  id and its extras are matched by duration.
- **Drafts:** a starter menu generated from a title's real extras and chapters.
- A **Menu Editor** on the dashboard with a live preview of the real renderer.
- A **Menu Catalogue** page on the dashboard: browse the community catalogue ([jellydisc-menus](https://github.com/peppy6582/jellydisc-menus),
  public-domain JSON), see which menus are for titles in your library, and install, update or remove them. Your
  library is never sent anywhere (see [the design](DEVELOPMENT.md#the-menu-catalogue-page)).

## Open work

### Menu Editor
- Drag the paging grid's region and its cells on the preview (buttons, the title and layers can be dragged already). 
- Open the designer for a menu straight from a title's page or the Menu Catalogue (it opens from the editor today).
- Undo and redo within an editing session. 

### Discovery and library
- Tell apart several copies of one title (for example a 4K and a 1080p file as separate items) by scoring
  release format and edition, instead of reporting them as ambiguous. **big**
- A "Change item" control on the dashboard for ambiguous menus.
- Items Jellyfin merges as multiple versions: decide which version a menu applies to.
- Drafts for series and seasons (movies only for now).

### Rendering
- Looping video from a file in the assets folder as a background.
- A trailer `poster` taken from a Jellyfin image (today only an `asset:` or `https` image).
- Fanart (and TMDB) pictures for button images and banner layers, not only backgrounds; a TMDB backdrop picker like the fanart one.
- An accessibility pass: screen-reader announcements, focus order, contrast checks. **good first**
- Clients other than the web client: the menu format is client-agnostic, but only jellyfin-web renders it.
  See "Other clients" below. **big**

### Sharing
- "Install this menu" from a title's own page (the catalogue page on the dashboard exists; this would bring it to where the
  title is shown).
- An export that strips anything local before sharing, plus a checker for the rules in
  [Authoring menus](AUTHORING.md#rules-for-shareable-menus).
- An allowlist of image hosts for shared menus, and a menu contribution policy.

### Packaging
- Releases are published by tag (see [Releasing](RELEASING.md)); v0.1.0 is out. What remains is listing the plugin in Jellyfin's
  default plugin catalogue.

### Other clients
- A client implementer's guide ([CLIENT_GUIDE.md](CLIENT_GUIDE.md)) and conformance vectors ([conformance/](../conformance/README.md)) exist. What's left is vectors for
  the visual and audible rules, a reference client that isn't a web page, and feedback from the first real implementer. **big**

### Quality
- More example menus in different styles. **good first**
- Screenshots for the documentation (made from the shipped examples, never from studio artwork).
  **good first**
- Translating the editor and dashboard UI strings.
- More tests wherever you find a gap; every bug fix should come with one.

## Not planned

- Playing real disc menus (`VIDEO_TS` / BDMV navigation): this recreates the *experience* with new, light
  assets, and deliberately doesn't touch disc content or copy protection.
- Bundling any studio artwork, music or video.
