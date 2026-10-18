# Aster Channels for Jellyfin

A **Channels** page in the Jellyfin dashboard for [Aster Media Player](https://github.com/AsterFIN-app/AsterFIN)'s TV channels: your playlists played like live TV, on a schedule every device sees the same.

In Aster, a channel is just a video playlist with tags on it (`aster:channel`, `aster:number=101`, `aster:mode=blocks` and so on). This page edits those tags for you, with dropdowns, checkboxes and drag-to-renumber. It reads and writes them exactly as the app does, so the app, this page and tagging by hand in Jellyfin all change the same channels. You can uninstall the plugin at any time and your channels keep working.

## Install

1. In Jellyfin, open **Dashboard ▸ Plugins ▸ Repositories** and add:
   `https://github.com/AsterFIN-app/jellyfin-plugin-aster-channels/releases/latest/download/manifest.json`
2. Install **Aster Channels** from the catalogue, and restart Jellyfin.
3. **Channels** now appears in the dashboard menu.

It needs Jellyfin 12.1 or later, and an administrator account, as changing playlists is an administrator's job.

## Using it

- **New Channel** makes a channel from a show, a genre, a network, a collection, or one of your playlists.
- **Pick a channel** to change it. The essentials come first: its name, number, mode, logo and breaks. What it plays and everything else are folded under **What It Plays** and **Advanced**.
- **Drag a channel** up or down the list to renumber it.
- **Breaks** play clips from filler playlists between programmes. Tag a playlist `aster:filler`, and tag each clip in it with what it is: `aster:commercial`, `aster:promo`, `aster:trailer`, `aster:interstitial` or `aster:bumper`.
- **Tags** at the bottom of each channel shows exactly what's written on the playlist.

Other users only see a channel in Aster if its playlist is shared with them or public. Channels made with New Channel are public.

Every tag is listed in Aster's [channel tag reference](https://github.com/AsterFIN-app/AsterFIN/blob/main/docs/channel-tags.md).

## Building

```sh
dotnet build Jellyfin.Plugin.AsterChannels -c Release
node --test tests/
```

`Web/channel-tags.js` is a port of the app's `ChannelTags.swift`. `tests/` runs it against the app's own vectors (`channel-tags.json`, from `AsterFINTests/Conformance`), so the two can't drift apart. After the app's tag rules change, run `tools/sync-vectors.sh ../AsterFIN`.

To release, bump the version in `build.yaml` and the project, then push a tag like `v0.1.0`. The Release workflow builds the zip and adds it to the repository manifest.

## License

GPL-3.0, like Jellyfin's plugin template.
