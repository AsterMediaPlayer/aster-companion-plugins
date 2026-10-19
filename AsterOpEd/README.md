# OP/ED for Jellyfin

Anime openings and endings for your Jellyfin 12 library.

Drop theme files in a folder. The plugin matches them to your shows and seasons, fills in song titles, artists and
episode ranges from [AnimeThemes.moe](https://animethemes.moe), and serves everything to clients through an
authenticated API. Files some clients can't play (AnimeThemes' WebM and Ogg, for example) get MP4/M4A copies that
every client can.

What it doesn't do: download themes for you (though it can cut missing ones out of your own episodes, see
[Clips](#clips-from-chapters)), or touch Jellyfin's built-in theme music and video.

## Install

Requires Jellyfin 12.

**From the plugin repository (easiest)**

1. Dashboard → Plugins → Repositories → add this URL:
   ```
   https://raw.githubusercontent.com/peppy6582/jellyfin-oped/main/manifest.json
   ```
2. Catalog → Metadata → OP/ED → Install.
3. Restart Jellyfin.

**By hand**

1. Download `jellyfin-oped_<version>.zip` from the [releases](https://github.com/peppy6582/jellyfin-oped/releases).
2. Unzip `Jellyfin.Plugin.OpEd.dll` into a new `OpEd_<version>` folder under Jellyfin's `data/plugins/`.
3. Restart Jellyfin.

Either way, finish in Dashboard → OP/ED by choosing your themes folder.

## Set up your themes folder

Pick a folder in Dashboard → OP/ED. Two rules:

- Give it as a path *inside the Jellyfin container*.
- Keep it outside your library folders, so libraries don't scan the themes as regular videos.

Inside it, make one folder per show, named exactly like that show's folder in your library:

```
anime_themes/
  Frieren - Beyond Journey's End (2023)/        <- same name as the show's library folder
    OP1.webm                                     <- a video and audio file with the same name are one theme
    OP1.ogg
    SousouNoFrieren-ED1-TV-NCBD1080.ogg          <- AnimeThemes' own file names work too
    Season 2/                                    <- only applies to that season's folder
      OP1.ogg
    anidb-18886/                                 <- or target one exact AniDB entry
      ED2.webm
```

**File names.** Start with `OP` or `ED`, then an optional number (`OP2`), optional `v2`, and an optional variant
(`ED1-TV`). Tags like `NCBD1080` are ignored.
Video: webm, mp4, mkv, m4v, mov. Audio: ogg, opus, mp3, flac, m4a, aac, wav.

**Matching.** Folders are matched against episodes Jellyfin already knows. With
[Shokofin](https://github.com/ShokoAnime/Shokofin), whose library folders are virtual, the plugin follows each episode
back to its real file, so your original folder names still work.

**Seasons.** A theme is tied to one season when its subfolder maps to a single AniDB entry, or when the metadata lookup
finds the first of the show's AniDB entries that has that OP/ED. Otherwise it applies to the whole show.

**Scanning.** Scans run at startup and every 12 hours (Scheduled Tasks → OP/ED), or on demand with **Save and scan
now**. The settings page also lists anything it couldn't use: folders matching no show, files that don't look like an
OP/ED, and themes with no metadata.

## Clips from chapters

Missing a theme? The plugin can cut it out of your own episodes, using their chapter markers. Nothing is cut
automatically. Run **Cut OP/ED clips from chapters** (Scheduled Tasks) or **Look for clips now** (Dashboard → OP/ED).

**Before you start:** tick the libraries to search under **Libraries to search**. None are searched until you do.

**What it does**

1. For each library season, it works out which OPs/EDs are expected. It uses AnimeThemes' list for that AniDB entry,
   or just OP1 and ED1 if there's no list.
2. It skips any that already have a theme, a clip awaiting review, or a rejected clip.
3. For each gap, it takes the first episode in that theme's episode range with a matching chapter, and re-encodes just
   that chapter to MP4 (H.264 + AAC, Japanese audio preferred) plus an M4A of the audio.

**Which chapters count**

| Looking for | Chapter names | Length |
|---|---|---|
| Opening | `OP`, `Opening` (an optional number works: `OP2`) | 30–200 s |
| Ending | `ED`, `Ending`, plus `Credits`, `End Credits`, `Outro`, `Closing` | 30–150 s for the extra names, 30–200 s otherwise |

`Intro` and `Preview` chapters are ignored on purpose, since they usually mean something else.

**Review queue** (the **Review** tab). Clips are held in the plugin's data folder (never the themes folder, so they
aren't served). They are grouped by show, with **Approve all** and **Reject all** for a whole show, and each clip plays
on request beside what AnimeThemes says it should be. Click a clip to select it, then press **A** to approve, **R** to
reject, **T** to reject and try another episode, **P** to preview, and the arrow keys to move.

- **Approve** moves it into the themes folder as an ordinary theme. Clips are cut as OP1/ED1 unless AnimeThemes or the
  chapter name says otherwise, so you can change the number in the row's number box first.
- **Reject** deletes it and the slot isn't cut again, until you choose **Forget rejections**.
- **Reject, try another episode** is for clips with text or credits running over them. It deletes the clip and cuts the
  same slot from the next usable episode, skipping ones already tried. When none are left it tells you, and you can
  add the file yourself.

When you've finished the queue, press **Add to library**. A bar with that button stays at the bottom of every tab while
approved clips are waiting. It does one scan for the whole batch and requests only the song details that are missing.
It is never pressed for you, because indexing contacts AnimeThemes.

**Same OP or a different one?** Approving a clip records its length, and later clips for the same show are compared
against it. Within 3 s is flagged "possibly the same OP", and clearly different is "probably a different OP". For
themes AnimeThemes doesn't list, the number box is pre-filled to match (same number if the length matches, next free
number if not). Treat it as a hint: different themes can be equally long, creditless AnimeThemes files run a few
seconds off in-episode chapters, and only approved clips are compared.

Without AnimeThemes data, a season can only yield OP1/ED1 (or the number in the chapter name).

## Song details

Each theme can carry a song title, artists, the episodes it plays in, and spoiler/NSFW flags. The plugin checks three
sources, most specific first. Each field is decided separately, so a source only overrides what it actually sets.

**1. themes.json** in the show's theme folder. Edit it by hand, or use the editor in Dashboard → OP/ED:

```json
{
  "OP1":          { "title": "Yuusha", "artists": ["YOASOBI"], "episodes": "1-16" },
  "ED1-TV":       { "title": "bliss", "artists": "milet", "episodes": "1", "spoiler": false },
  "Season 2/OP1": { "title": "lulu.", "artists": ["Mrs. GREEN APPLE"] }
}
```

- Keys are the theme's place in the folder: `OP1`, `OP1v2`, `ED1-TV`, `Season 2/OP1`, `anidb-18886/ED2`. Spelling is
  forgiving (`season 02/op1` works).
- An unreadable file is listed on the settings page and skipped.
- To put a theme in a specific season, add its AniDB id: `"OP3": { "anidb": 18886 }`. That works wherever the file
  sits, and metadata is then looked up for that entry. In the editor, each theme says how it got its season (picked by
  you, from an `anidb-N` folder, found on AnimeThemes, or the only possible season), with a link to check the AniDB
  entry. To change it, use the **Season** dropdown (the show's seasons that have an AniDB id) or type any AniDB id.

**2. The file name:** `OP1 - Yuusha - YOASOBI [1-16].webm`. That's title, then artists separated by commas or `&`,
then optional `[episodes]`. Attach a variant with a hyphen and no spaces: `ED1-TV - bliss - milet.ogg`.

**3. AnimeThemes.moe**, see below.

**Using the editor** (the **Library** tab). Themes are grouped by show; search, or filter to the ones that need
details, were matched to a season by a guess, or were edited by hand. **Edit** opens a theme with its current details
and where each came from. **Save** writes that theme's entry to themes.json. **Clear** removes it so the other sources apply again. Ticking
Spoiler or NSFW saves the flag, and leaving it unticked defers to the other sources. Jellyfin needs write access to the
themes folder for the editor to work.

**Replacing a file.** Downloaded a better version of an OP, say a creditless one? Put it in the show's folder in place
of the old one (any supported format), then press **Rescan files** on that show's header in the Library tab. The plugin
picks up the new file and, if it doesn't already play everywhere, makes an MP4 or M4A of it right there in the themes
folder under the same name. The file you replaced goes into an `_originals` folder next to it. Nothing is deleted, and
AnimeThemes isn't contacted. Folders starting with an underscore are ignored when scanning. (Files you never press this
for are still served through a playable copy kept in the plugin's data folder.)

## Metadata from AnimeThemes

Song titles, artists, episode ranges and spoiler/NSFW flags come from [AnimeThemes.moe](https://animethemes.moe), a
community project. Please credit it wherever you show this data. This plugin isn't affiliated with or endorsed by
AnimeThemes.

The lookup works from AniDB ids on your seasons (Shokofin sets them). If a season or show only has an AniList,
MyAnimeList or Kitsu id (from Jellyfin's own metadata providers), AnimeThemes is asked which AniDB entry that is, and
the rest works the same. A show with only a show-level id gets one entry for the whole show, so its themes stay
show-wide, unless the seasons can be told apart: a season with no id of its own is matched to the show's own
AnimeThemes entry for that season when that entry started within three months of the season's first episode, TV
series only. If the dates do not line up nothing is guessed, and you can set the season by hand in the editor. It uses their GraphQL API (the JSON API is
deprecated) and keeps the load on their server light:

- One request per 50 AniDB ids, at least two seconds apart, never in parallel (their limit is 90 per minute).
- A show with themes but no usable id at all is searched for by its title (ten shows per request, same pacing), and the
  best match is accepted only if its title is at least 80% alike (configurable) and its year is within one. The show
  name is what gets sent. A title match never replaces an id, and the editor labels it so you can correct it.
- Answers are cached on disk per AniDB id for 14 days (configurable). Anime AnimeThemes has no themes for are usually
  just not added yet, so they are asked again sooner: after 1, 3 and 7 days, then every 30.
- After a refusal (403), it stops asking for a day.
- It never downloads video or audio from AnimeThemes.

Requests identify as `OpEd-Themes (+https://github.com/peppy6582)`, the agent string agreed with AnimeThemes. It must
not contain "jellyfin", which their filter refuses, so don't change it without checking with them. If they ask for
different handling (another agent string, an API key, another data source), the plugin will be changed.

**Opting out.** To stop title searches only, untick **Match shows by title when they have no ID**. To never contact AnimeThemes, untick **Look up song, artists and episodes on AnimeThemes.moe** in
Dashboard → OP/ED. Everything else keeps working, and you can fill in song details with the editor.

## API

Every endpoint needs a signed-in user (or an API key) and only returns themes for shows the user can see. Send the
usual Jellyfin `Authorization: MediaBrowser Token="…"` header. Fields that are null are left out of the JSON.

| Method | Path | |
|---|---|---|
| GET | `/OpEd/Items/{itemId}/Themes?type=OP\|ED` | Themes for a series, season or episode |
| GET | `/OpEd/Themes/{themeId}` | One theme |
| GET | `/OpEd/Themes/{themeId}/Video` | The original video file. Supports HTTP range requests, so players can seek |
| GET | `/OpEd/Themes/{themeId}/VideoMp4` | The video as MP4 (H.264/HEVC + AAC) that every client can play, with range requests |
| GET | `/OpEd/Themes/{themeId}/AudioM4a` | The audio as AAC M4A (or an original MP3) that every client can play, with range requests |
| GET | `/OpEd/Themes/{themeId}/Audio` | The original audio file |
| POST | `/OpEd/Scan` | Rescan now (administrators) |
| GET | `/OpEd/Status` | Last scan report (administrators) |
| GET | `/OpEd/Admin/Themes` | Every theme with its song details and their source, for the editor (administrators) |
| POST | `/OpEd/Admin/Themes/{themeId}/Details` | Save song details to themes.json: `{Title, Artists[], Episodes, Spoiler, Nsfw}` (administrators) |
| DELETE | `/OpEd/Admin/Themes/{themeId}/Details` | Remove them from themes.json (administrators) |
| POST | `/OpEd/Admin/Themes/Rescan` | Pick up files you replaced for the given themes: `{ThemeIds[]}`. Rescans, makes them playable in the themes folder and moves the originals to `_originals` (nothing is deleted); no AnimeThemes requests; runs in the background (administrators) |

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
      "Spoiler": false, "Nsfw": false, "Overlap": "none", "HasCreditlessVersion": true,
      "HasVideo": true, "HasAudio": true,
      "VideoUrl": "/OpEd/Themes/4176c129755c36cf/Video", "VideoContainer": "webm",
      "VideoMp4Url": "/OpEd/Themes/4176c129755c36cf/VideoMp4", "VideoMp4Status": "ready",
      "AudioUrl": "/OpEd/Themes/4176c129755c36cf/Audio", "AudioContainer": "ogg",
      "MetadataSource": "animethemes"     // or "themes.json", "filename"
    }
  ]
}
```

`Overlap` is `none`, `transition` (a little episode footage at the start or end) or `over` (the song plays over the episode). It is
the cleanest version AnimeThemes has for that OP/ED, and `HasCreditlessVersion` says whether it has a creditless one. Both come
from AnimeThemes and are left out when unknown.

Theme ids are derived from the theme's place in the themes folder, so they stay the same across rescans.

## Playable video and audio for every client

Clients should play `VideoMp4Url` and `AudioM4aUrl`. AnimeThemes' videos are WebM (VP9/Opus), which Apple's player (AVPlayer, so
Apple TV and iOS) doesn't play. After each scan the plugin makes a copy every client can play, using Jellyfin's own
ffmpeg:

- An MP4/M4V/MOV that is already H.264 or HEVC with AAC audio is served as-is; nothing is copied.
- H.264/HEVC in another container (MKV) is rewrapped into MP4 with the audio converted to AAC. That takes about a second.
- Anything else (VP9, AV1, …) is converted to H.264 High (CRF 19, at most 1080p wide) + AAC stereo with libx264. It
  runs one file at a time at low CPU priority and takes about 15 s for a 90-second 720p OP.

Audio: MP3 and AAC (.mp3, .m4a, .aac) are served as-is; anything else (AnimeThemes' Opus-in-Ogg, Vorbis, FLAC,
WAV) is converted to AAC-LC 256 kbps stereo M4A, about 1.5 s per song.

`VideoMp4Status`/`AudioM4aStatus` are `pending`, `converting`, `ready` or `failed`; `VideoMp4Url`/`AudioM4aUrl` appear
once it is `ready`. Copies
are kept in the plugin's data folder, are redone when the source file changes, and are deleted once no theme uses them.
A failed conversion isn't retried until its source file changes; its ffmpeg error is kept next to where the copy would
be (`*.mp4.failed`).

## Jellyfin libraries

Turn on **Publish the themes as Jellyfin libraries** on the Setup tab, choose a folder for them, and press **Save and
create the libraries**. The plugin adds two ordinary libraries and keeps them up to date after every scan:

- **Anime Openings & Endings** (Music Videos): one item per theme with a playable video. The file is a link to the playable
  copy, so it takes no extra space, with a `.nfo` (song, artists, the show as album, season, episodes, genres
  `Anime` and `Opening`/`Ending`) and a picture taken from the video.
- **Anime Theme Songs** (Music): every theme's song, an album per show with the show's art. These are small copies,
  because Jellyfin's music libraries read the details from inside the file.

Playlists, favourites, watched state, search and parental controls then work as for anything else. Spoiler and NSFW
themes are included and tagged `Spoiler`/`NSFW`; NSFW videos get the rating you choose (default NC-17), so users whose
parental controls stop below it don't see them. The libraries' internet metadata and image fetchers are off, so nothing
replaces what the plugin wrote.

The folder must be outside the themes folder and every other library; the plugin refuses one that overlaps. Inside it,
`Videos/{Show}/` and `Songs/{Show}/` are the plugin's: changes made there by hand are overwritten.

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

## Releasing

Bump `version` in `Jellyfin.Plugin.OpEd/build.yaml` and the csproj, commit, then push a matching tag (`git tag v0.1.0.0 && git push origin v0.1.0.0`).
`.github/workflows/release.yml` builds the plugin, creates the GitHub release with `jellyfin-oped_<version>.zip`, and
records the version and the zip's MD5 in `manifest.json` on main.

To package by hand instead: `bash scripts/package.sh` writes `dist/jellyfin-oped_<version>.zip` (just the DLL) and updates
`manifest.json`; attach the zip to a release yourself.
