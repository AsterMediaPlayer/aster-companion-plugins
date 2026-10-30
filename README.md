# Aster companion plugins for Jellyfin

Server plugins that go with [Aster Media Player](https://github.com/AsterMediaPlayer/Aster-Media-Player), all installed from one
plugin repository:

| Plugin | What it does |
|---|---|
| [**Aster Channels**](AsterChannels/README.md) | A Channels page in the dashboard for Aster's TV channels: make and edit them, drag to renumber. For Jellyfin, and for Emby. |
| [**Aster Disc**](AsterDisc/README.md) | Shareable disc menus for special features, with a menu editor and a community catalogue. (Formerly Disc Menus.) |
| [**Aster OP-ED**](AsterOpEd/README.md) | Anime openings and endings matched to your shows, with song details, playable copies for every client, and Jellyfin libraries of them. (Formerly OP/ED.) |
| [**Aster Aurora**](AsterAurora/README.md) | Apple TV style behaviour for the Jellyfin web client: home hero banner, trailer backgrounds, Up Next rows, quality badges, an Episodes row. (Formerly Aurora.) |

## Install

1. In Jellyfin, open **Dashboard ▸ Plugins ▸ Repositories** and add:
   `https://raw.githubusercontent.com/AsterMediaPlayer/aster-companion-plugins/main/manifest.json`
2. Install the plugins you want from the catalogue, and restart Jellyfin.

They need Jellyfin 12.

**Coming from Disc Menus, OP/ED or Aurora?** Add this repository and update as usual. The plugins keep their ids, so they update in
place, and on first start each moves its settings and data across from its old name. You can then remove the old
repository (jellydisc or jellyfin-oped) from your list.

## Layout

Each plugin is a folder of its own, with its project, tests, tools and docs:

```
AsterChannels/   Jellyfin.Plugin.AsterChannels, Emby.Plugin.AsterChannels
AsterDisc/       Jellyfin.Plugin.AsterDisc, its tests, schema, examples and tools
AsterOpEd/       Jellyfin.Plugin.AsterOpEd
AsterAurora/     Jellyfin.Plugin.AsterAurora
tools/           check_versions.py, shared by the release workflow
manifest.json    the plugin repository: every plugin, every version
```

## Releasing

1. Bump the plugin's `version` in its `build.yaml` (four numbers, such as `0.6.0.0`), add a changelog entry starting
   `- 0.6.0.0:`, and set the same `AssemblyVersion` and `FileVersion` in its csproj.
   `python3 tools/check_versions.py AsterDisc/Jellyfin.Plugin.AsterDisc` checks they agree.
2. Merge to `main` and let CI pass.
3. Push a tag naming the plugin and the version (`X.Y.Z` releases `X.Y.Z.0`):

   ```bash
   git tag asterdisc-v0.6.0 && git push origin asterdisc-v0.6.0      # or asterchannels-v… / asteroped-v… / asteraurora-v…
   ```

The release workflow tests that plugin, builds its zip with jprm, publishes the GitHub release, and adds the version to
`manifest.json` on main. Every release also carries a copy of the manifest.

## License

GPL-3.0. See each plugin's folder for its own notes.
