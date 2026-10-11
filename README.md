# OP/ED for Jellyfin

A Jellyfin 12 plugin for anime opening and ending themes. You put the theme files in a folder; the plugin matches
them to your shows and seasons, adds song title, artists and episode ranges from [AnimeThemes.moe](https://animethemes.moe),
and serves them to clients through an authenticated API. Videos that not every client can play (AnimeThemes' WebM,
for example) get an MP4 copy that every client can. It doesn't download theme files and doesn't touch Jellyfin's own
theme music/video playback.

## Themes folder

Set the folder in Dashboard → OP/ED. It is a path *inside the Jellyfin container*, and should be outside every
library's folders so no library scans the themes as ordinary videos.

```
anime_themes/
  Frieren - Beyond Journey's End (2023)/        <- named exactly like the show's folder in your media library
    OP1.webm                                     <- video and audio with the same identity are one theme
    OP1.ogg
    SousouNoFrieren-ED1-TV-NCBD1080.ogg          <- AnimeThemes' own file names work too
    Season 2/                                    <- limits these themes to that library season folder
      OP1.ogg
    anidb-18886/                                 <- or pin one exact AniDB entry
      ED2.webm
```

- File names: `OP`/`ED`, optional number, optional `v2`, optional variant (`ED1-TV`). Source tags such as `NCBD1080`
  are ignored. Video: webm, mp4, mkv, m4v, mov. Audio: ogg, opus, mp3, flac, m4a, aac, wav.
- Show folders are matched through the episodes Jellyfin already knows. For [Shokofin](https://github.com/ShokoAnime/Shokofin)
  libraries, whose folders are virtual, the plugin follows each episode's link back to the real file, so the original
  folder names still work.
- A season is pinned from the subfolder when it maps to one AniDB entry, or from the metadata lookup (the first of the
  show's AniDB entries, earliest first, that has this OP/ED). Otherwise the theme is show-wide.
- Rescans run at startup and every 12 hours (Scheduled Tasks → OP/ED), or with "Save and scan now". The settings page
  lists folders that match no show, files that aren't recognisable OPs/EDs, and themes without metadata.

## Song details

Each theme can carry a song title, artists, the episodes it plays in, and spoiler/NSFW flags. They come from, most
specific first (field by field, so a source only overrides what it sets):

1. **themes.json** in the show's theme folder, written by hand or by the editor in Dashboard → OP/ED:
   ```json
   {
     "OP1":          { "title": "Yuusha", "artists": ["YOASOBI"], "episodes": "1-16" },
     "ED1-TV":       { "title": "bliss", "artists": "milet", "episodes": "1", "spoiler": false },
     "Season 2/OP1": { "title": "lulu.", "artists": ["Mrs. GREEN APPLE"] }
   }
   ```
   Keys are the theme's place in the folder (`OP1`, `OP1v2`, `ED1-TV`, `Season 2/OP1`, `anidb-18886/ED2`); spelling
   is forgiving (`season 02/op1` works). A file that can't be read is listed on the settings page and skipped.
2. **The file name**: `OP1 - Yuusha - YOASOBI [1-16].webm` (title, then artists separated by commas or `&`, then an
   optional `[episodes]`). A variant is glued on with a hyphen and no spaces (`ED1-TV - bliss - milet.ogg`).
3. **AnimeThemes.moe**, below.

The settings page lists every theme with its current details and where they came from. Save writes that theme's entry
to themes.json; Clear removes it so the other sources apply again. Ticking Spoiler/NSFW saves the flag; leaving it
unticked leaves the flag to the other sources. The themes folder must be writable by Jellyfin for the editor.

## Metadata from AnimeThemes

Metadata needs AniDB ids on the seasons (Shokofin sets them). AnimeThemes runs on one small server and has blocked
Jellyfin plugins before because of the load they caused. This plugin follows the approach they asked for: one request
per 50 AniDB ids, two seconds apart, with answers cached for 14 days (configurable). It identifies itself as
`Jellyfin-Plugin-OpEd`. At the time of writing, AnimeThemes refuses requests whose User-Agent contains "Jellyfin", so
metadata lookups fail until they allow this plugin. The plugin doesn't disguise itself to get around that. Themes still
work without metadata; only song title, artists, episode ranges and the spoiler/NSFW flags are missing.

## API

Every endpoint needs a signed-in user (or an API key) and only returns themes for shows the user can see. Send the
usual Jellyfin `Authorization: MediaBrowser Token="…"` header. Fields that are null are left out of the JSON.

| Method | Path | |
|---|---|---|
| GET | `/OpEd/Items/{itemId}/Themes?type=OP\|ED` | Themes for a series, season or episode |
| GET | `/OpEd/Themes/{themeId}` | One theme |
| GET | `/OpEd/Themes/{themeId}/Video` | The original video file. Supports HTTP range requests, so players can seek |
| GET | `/OpEd/Themes/{themeId}/VideoMp4` | The video as MP4 (H.264/HEVC + AAC) that every client can play, with range requests |
| GET | `/OpEd/Themes/{themeId}/Audio` | The audio file |
| POST | `/OpEd/Scan` | Rescan now (administrators) |
| GET | `/OpEd/Status` | Last scan report (administrators) |
| GET | `/OpEd/Admin/Themes` | Every theme with its song details and their source, for the editor (administrators) |
| POST | `/OpEd/Admin/Themes/{themeId}/Details` | Save song details to themes.json: `{Title, Artists[], Episodes, Spoiler, Nsfw}` (administrators) |
| DELETE | `/OpEd/Admin/Themes/{themeId}/Details` | Remove them from themes.json (administrators) |

For a season, the result is that season's themes plus show-wide ones whose season isn't known. For an episode, it's the
same, and each theme also carries `PlaysInEpisode` (from AnimeThemes' episode list, matched against the episode
number). Example:

```json
{
  "ItemId": "03e4ecbedc176e73cd47fb3032c6b189",
  "SeriesId": "03e4ecbedc176e73cd47fb3032c6b189",
  "Themes": [
    {
      "Id": "4176c129755c36cf", "Type": "OP", "Sequence": 1, "Version": 1, "Slug": "OP1",
      "SeriesId": "03e4ecbedc176e73cd47fb3032c6b189", "SeasonId": "…", "AniDbId": 17617,
      "SongTitle": "Yuusha", "Artists": ["YOASOBI"], "Episodes": "1-16",
      "Spoiler": false, "Nsfw": false,
      "HasVideo": true, "HasAudio": true,
      "VideoUrl": "/OpEd/Themes/4176c129755c36cf/Video", "VideoContainer": "webm",
      "VideoMp4Url": "/OpEd/Themes/4176c129755c36cf/VideoMp4", "VideoMp4Status": "ready",
      "AudioUrl": "/OpEd/Themes/4176c129755c36cf/Audio", "AudioContainer": "ogg",
      "MetadataSource": "animethemes"     // or "themes.json", "filename"
    }
  ]
}
```

Theme ids are derived from the theme's place in the themes folder, so they stay the same across rescans.

## Playable video for every client

Clients should play `VideoMp4Url`. AnimeThemes' videos are WebM (VP9/Opus), which Apple's player (AVPlayer, so
Apple TV and iOS) doesn't play. After each scan the plugin makes a copy every client can play, using Jellyfin's own
ffmpeg:

- An MP4/M4V/MOV that is already H.264 or HEVC with AAC audio is served as-is; nothing is copied.
- H.264/HEVC in another container (MKV) is rewrapped into MP4 with the audio converted to AAC. That takes about a second.
- Anything else (VP9, AV1, …) is converted to H.264 High (CRF 19, at most 1080p wide) + AAC stereo with libx264. It
  runs one file at a time at low CPU priority and takes about 15 s for a 90-second 720p OP.

`VideoMp4Status` is `pending`, `converting`, `ready` or `failed`; `VideoMp4Url` appears once it is `ready`. Copies
are kept in the plugin's data folder, are redone when the source file changes, and are deleted once no theme uses them.
A failed conversion isn't retried until its source file changes; its ffmpeg error is kept next to where the copy would
be (`*.mp4.failed`).

## Build

```
docker compose --profile tools run --rm build
```

Output: `Jellyfin.Plugin.OpEd.dll`. Copy only that DLL into a plugin folder such as `plugins/OpEd_0.1.0.0/` and
restart Jellyfin.

## Development note

This plugin was developed primarily with an AI coding assistant (Claude Code) and tested against a live Jellyfin 12.1
server.

License: GPL-3.0.
