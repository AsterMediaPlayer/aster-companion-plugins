# Aster Aurora for Jellyfin

A Jellyfin 12.x plugin that gives the **web client** Apple TV style behaviour:

- **Home hero banner:** full-bleed featured titles with logo, details, Play and More Info, rotating every few seconds.
- **Trailer backgrounds:** a muted trailer fades in behind movie and series pages (local trailer first, YouTube optional). A button beside the sound button clears the page away to watch it; Escape, a click or a scroll brings the page back.
- **Opening backgrounds:** an anime show's opening loops behind its page instead, from the
  [Aster OP/ED](../AsterOpEd) plugin (the season you'd watch next; never one marked as a spoiler or NSFW).
- **Card tilt:** cards tilt and catch a highlight under the mouse.
- **Genre chips:** a title's genres shown as chips.
- **Up Next rows:** Continue Watching and Next Up cards with the title's logo over clean backdrop art.
- **Quality badges:** 4K, Dolby Vision, HDR10, Dolby Atmos, 5.1 and CC at a glance on details pages.
- **Episodes row:** a show's episodes in one row, with season tabs.
- **Back button** in the top-left corner.
- **Aster app details:** episode pages titled with the episode's own name and an "S2, E2" line, "N min left" beside a
  started title's buttons, and Resume on the banner for one.
- **No flash of the stock layout:** the custom CSS applies from the very first frame.

It pairs with the [Aster skin](https://github.com/AsterMediaPlayer/aster-skin) (or the older Aurora custom CSS) in Dashboard → Branding, taking its colours, but works without either. Settings are under Dashboard → Aster Aurora.
Only jellyfin-web and apps that wrap it are affected; native apps (Apple TV, Android TV, Infuse, Swiftfin) draw their own screens.

> **Heads up: this project is vibe coded.** All of the code was written by an AI (Claude, through Claude Code) while
> [Phillip Berryman](https://github.com/peppy6582) steered. It has not been independently reviewed or security-audited.
> It works on the author's server, but expect rough edges and **read the code before you trust it**.

## Install

1. In Jellyfin, open **Dashboard ▸ Plugins ▸ Repositories** and add:
   `https://raw.githubusercontent.com/AsterMediaPlayer/aster-companion-plugins/main/manifest.json`
2. Install **Aster Aurora** from the catalogue (General), and restart Jellyfin.

It needs Jellyfin 12.

**Coming from Aurora?** Aster Aurora is the same plugin under a new name and keeps its plugin id, so it updates in place.
On first start it copies your Aurora settings across. Remove the old Aurora install folder if Jellyfin leaves one behind.

## How it works

The plugin adds a stylesheet and a script tag to jellyfin-web's start page as it is served (no Jellyfin files are
edited). The script watches for page changes and adds features to whatever is on screen. Every feature checks for the
elements it needs and does nothing if they are missing, so a jellyfin-web update degrades to the stock layout rather
than breaking pages.

## Building

With the .NET 10 SDK: `dotnet publish Jellyfin.Plugin.AsterAurora -c Release -o out`, or `docker compose run --rm build`
(see docker-compose.yml). To install a build by hand, copy `Jellyfin.Plugin.AsterAurora.dll` into a new
`Aster Aurora_<version>` folder under Jellyfin's `data/plugins/`, and restart Jellyfin.

To release, see the repository [README](../README.md#releasing): push a tag like `asteraurora-v0.4.0`.

## Licence

GPL-3.0-only.
