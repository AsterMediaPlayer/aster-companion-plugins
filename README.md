# OP/ED for Jellyfin

A Jellyfin 12 plugin for anime opening and ending themes. You put the theme files in a folder; the plugin matches
them to your shows and seasons, adds song title, artists and episode ranges from [AnimeThemes.moe](https://animethemes.moe),
and serves them to clients through an authenticated API. It doesn't download theme files and doesn't touch Jellyfin's
own theme music/video playback.

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

## Metadata and AnimeThemes

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
| GET | `/OpEd/Themes/{themeId}/Video` | The video file. Supports HTTP range requests, so players can seek |
| GET | `/OpEd/Themes/{themeId}/Audio` | The audio file |
| POST | `/OpEd/Scan` | Rescan now (administrators) |
| GET | `/OpEd/Status` | Last scan report (administrators) |

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
      "AudioUrl": "/OpEd/Themes/4176c129755c36cf/Audio", "AudioContainer": "ogg",
      "MetadataSource": "animethemes"
    }
  ]
}
```

Theme ids are derived from the theme's place in the themes folder, so they stay the same across rescans.
AnimeThemes videos are WebM (VP9/Opus). Apple platforms don't play WebM natively, so a tvOS client needs its own
player for them (for example VLCKit), or the themes need to be supplied as MP4.

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
