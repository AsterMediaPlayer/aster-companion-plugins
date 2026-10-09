# Aurora for Jellyfin

A Jellyfin 12.x plugin that gives the **web client** Apple TV style behaviour:

- **Home hero banner:** full-bleed featured titles with logo, details, Play and More Info, rotating every few seconds.
- **Trailer backgrounds:** a muted trailer fades in behind movie and series pages (local trailer first, YouTube optional).
- **Card tilt:** cards tilt and catch a highlight under the mouse.
- **Genre chips:** a title's genres shown as chips.

It pairs with the Aurora custom CSS (Dashboard → Branding) but works without it. Settings are under Dashboard → Aurora.
Only jellyfin-web and apps that wrap it are affected; native apps (Apple TV, Android TV, Infuse, Swiftfin) draw their own screens.

> **Heads up: this project is vibe coded.** All of the code was written by an AI (Claude, through Claude Code) while
> [Phillip Berryman](https://github.com/peppy6582) steered. It has not been independently reviewed or security-audited.
> It works on the author's server, but expect rough edges and **read the code before you trust it**.

## How it works

The plugin adds a stylesheet and a script tag to jellyfin-web's start page as it is served (no Jellyfin files are
edited). The script watches for page changes and adds features to whatever is on screen. Every feature checks for the
elements it needs and does nothing if they are missing, so a jellyfin-web update degrades to the stock layout rather
than breaking pages.

## Building

`docker compose run --rm build` (see docker-compose.yml), or with the .NET 10 SDK:
`dotnet publish Jellyfin.Plugin.Aurora -c Release -o out`. Copy `Jellyfin.Plugin.Aurora.dll` into
`<jellyfin data>/plugins/Aurora_0.1.0.0/` and restart Jellyfin.

## Licence

GPL-3.0-only.
