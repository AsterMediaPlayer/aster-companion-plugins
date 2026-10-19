# Example menus

Each `*.menu.json` here is a complete, shareable menu. They are validated against
`schema/menu.schema.json` and loaded through the plugin's own loader by the test suite, so what
you see here is known to work. The extras listed in them are illustrative, not real disc
listings.

| File | Background | Shows off |
|---|---|---|
| [`example.menu.json`](example.menu.json) | the title's Jellyfin backdrop | the original minimal example, paired with [`example.binding.json`](example.binding.json) |
| [`fanart-background.menu.json`](fanart-background.menu.json) | **fanart.tv** pictures as backgrounds, one per page | the server looks the picture up with the administrator's fanart.tv key and serves it (needs a key and real ids: the ones here are placeholders) |
| [`static-backdrop.menu.json`](static-backdrop.menu.json) | **static**: Jellyfin backdrop, dimmed | the simplest menu: a plain list of buttons, no art files; fade transitions, click sounds |
| [`solid-color-glow.menu.json`](solid-color-glow.menu.json) | **solid colour** | hand-placed centred buttons, serif/uppercase text with a glow highlight, three menu levels with both `back` and `home`; slide transitions, chime sounds |
| [`trailer-background.menu.json`](trailer-background.menu.json) | **trailer video** (looping, muted) | a translucent bar layer, buttons inside it, an automatic paged grid (More / Previous) with Home pinned; wipe transitions, beep sounds, the item's Jellyfin theme song as music |
| [`local-art.menu.json`](local-art.menu.json) | **your own image** via `asset:` | background + banner images served from the menus `assets` folder (bundled in [`assets/local-art/`](assets/local-art/)), looping background music from a bundled file, zoom transitions, a styled scene-selection screen |
| [`thor-ragnarok.menu.json`](thor-ragnarok.menu.json) | trailer + poster still | a real menu using everything at once, with a banner declared once and inherited by every menu |

## Using one

1. Copy the `.menu.json` into your server's menus folder (the plugin's `MenusPath`). That's it for a
   title you already have: the plugin finds the matching movie or series in your library by the
   TMDB / IMDB / TVDB ids in the menu's `match`, links the menu's extras to that title's local
   Special Features by type and duration, and the **Disc Menu** button appears on it. If the title
   isn't in your library yet it waits, and links itself when the title is added.
2. Check the plugin's page in the Jellyfin dashboard: the **Menu Files** table shows each menu's
   status (bound, waiting for the title, needs review, not used, or can't be loaded) and how many
   extras were linked. **Scan now** forces a re-check.
3. If you are adapting an example for your own title, change `match` to that title's ids and give
   the menu a fresh `menuId` (any UUID) so it doesn't collide with the example.
4. For menus that use `asset:` images or audio, copy their files into
   `<MenusPath>/assets/<folder>/`. For `local-art.menu.json` that means copying `assets/local-art/`
   from this folder.

If a menu is **ambiguous** (for example you hold two copies of the same movie) the plugin will not
guess: the table lists the candidates. To choose, or to override what discovery did, write a binding
by hand next to the menu as `<same name>.binding.json`. A hand-written binding always wins:

```json
{
  "schemaVersion": 1,
  "menuId": "<the menuId from the menu file>",
  "menuRevision": 1,
  "parentItemId": "<your library item's Jellyfin id>",
  "bindings": {}
}
```

then run **Auto-Match** on the plugin's page to link its extras. Binding files are local to your
server and are never shared.

## Notes

- **Artwork you can't redistribute isn't included.** `thor-ragnarok.menu.json` refers to
  `asset:thor-ragnarok/background.webp` and `menu-banner.webp`; supply your own images with those
  names, or edit the references. Without them the menu still works, just without the pictures.
- **Trailer backgrounds** use the item's own trailers: local trailer files first, then the YouTube
  trailers Jellyfin has stored for the item. `trailerIndex` picks which. YouTube playback needs
  internet access, and trailers that disallow embedding fall back to the `poster` (or a dark screen
  if none is set). Captions are always switched off for background trailers.
- **Audio is opt-in.** Menus without an `audio` block are silent. `local-art` bundles a short generated
  ambient loop (`assets/local-art/ambient.wav`) so its music works out of the box; the other examples use
  the built-in synthesised button sounds, which need no files. `themeSong` music only plays if the item
  actually has a Jellyfin theme song.
- Images are never embedded in menu files: an `image` is an `https://` URL, a small inline
  `data:` image, or an `asset:` reference. See the main README's "Authoring a disc-style layout".
