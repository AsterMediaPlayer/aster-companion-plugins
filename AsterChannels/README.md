# Aster Channels for Jellyfin

A **Channels** page in the Jellyfin dashboard for [Aster Media Player](https://github.com/AsterMediaPlayer/Aster-Media-Player)'s TV channels: your playlists played like live TV, on a schedule every device sees the same.

In Aster, a channel is just a video playlist with tags on it (`aster:channel`, `aster:number=101`, `aster:mode=blocks` and so on). This page edits those tags for you, with dropdowns, checkboxes and drag-to-renumber. It reads and writes them exactly as the app does, so the app, this page and tagging by hand in Jellyfin all change the same channels. You can uninstall the plugin at any time and your channels keep working.

## Install

1. In Jellyfin, open **Dashboard ▸ Plugins ▸ Repositories** and add:
   `https://raw.githubusercontent.com/AsterMediaPlayer/aster-companion-plugins/main/manifest.json`
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

Every tag is listed in Aster's [channel tag reference](https://github.com/AsterMediaPlayer/Aster-Media-Player/blob/main/docs/channel-tags.md).

## Emby

`Emby.Plugin.AsterChannels` is the same Channels page for **Emby 4.8 and later**: the same list, New Channel,
editor and drag-to-renumber, writing the same `aster:` tags, which Aster reads on Emby too. Live TV groups are
Jellyfin-only and aren't in it.

**Install.** Emby has no third-party plugin catalogue, so:

1. Download `Emby.Plugin.AsterChannels.dll` from this repository's
   [releases](https://github.com/AsterMediaPlayer/aster-companion-plugins/releases) (tags `asterchannels-emby-v…`).
2. Put it in Emby's `plugins` folder (in Docker, usually `/config/plugins`).
3. Restart Emby. **Channels** appears in the dashboard's server menu.

It needs an administrator account, and stores nothing of its own: uninstall it any time and the channels stay.

**How it differs from the Jellyfin page** (`Web/channels.js` is the Jellyfin page with only these changes; keep the
two in step): Emby is asked for the `Tags` field but returns and accepts tags as `TagItems`; libraries come from
`Users/{id}/Views`; new playlists are made with query-string parameters, then filled 200 items at a time; and
playlists are listed without `mediaTypes`, which makes Emby ignore `includeItemTypes`. The tag rules are the
Jellyfin plugin's `Web/channel-tags.js`, embedded as is, so both plugins follow the app's tests. The page itself is
loaded the way Emby's own plugins are: `Configuration/channelsPage.js` is the view controller.

Build: `dotnet build Emby.Plugin.AsterChannels -c Release` (it targets netstandard2.0 against Emby's 4.8.10 SDK).

## Building

```sh
dotnet build Jellyfin.Plugin.AsterChannels -c Release
node --test tests/*.test.js
```

`Web/channel-tags.js` is a port of the app's `ChannelTags.swift`. `tests/` runs it against the app's own vectors (`channel-tags.json`, from `AsterFINTests/Conformance`), so the two can't drift apart. After the app's tag rules change, run `tools/sync-vectors.sh ../AsterFIN`.

To release, bump the version in `build.yaml` and the project, then push a tag like `asterchannels-v0.1.2`. The Release workflow builds the zip and adds it to the repository manifest.

## License

GPL-3.0, like Jellyfin's plugin template.
