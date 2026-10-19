# OP/ED plugin — client integration spec

This document is for whoever builds a client (here: the AsterFIN tvOS app) against the **OP/ED** plugin running on a
Jellyfin 12.1 server. It describes everything a client needs: what the plugin is, authentication, every endpoint,
every JSON field, media formats, and the edge cases. All behaviour described here was verified against the live server
on 2026-10-10 unless marked otherwise.

---

## 1. What the plugin does

- The server owner puts anime opening (OP) and ending (ED) theme files in a themes folder on the server, one folder
  per show. Each **theme** (e.g. "OP1 of Frieren season 1") can have a **video** file, an **audio** file, or both.
- The plugin matches each theme to a Jellyfin **series**, and when possible to a specific **season** (for anime
  libraries, each Jellyfin season corresponds to one AniDB entry).
- Each theme can carry **song details**: song title, artists, which episodes it plays in, and spoiler/NSFW flags. These
  come from the owner (a `themes.json` file, file names, or an editor in the Jellyfin dashboard) or from AnimeThemes.moe.
- Every video gets an **MP4 (H.264 + AAC) copy** and every audio file an **M4A (AAC) copy** that Apple's AVPlayer
  plays natively. The originals might be WebM/VP9 or Ogg/Opus, which AVPlayer can't play. Originals that already play
  on Apple devices are served as they are.
- The client discovers themes via a JSON API keyed by normal Jellyfin item ids and streams the media over HTTP with
  range support (seeking works).

The plugin does **not** use or change Jellyfin's built-in "theme songs / theme videos" feature
(`/Items/{id}/ThemeSongs`, `/ThemeVideos`). Don't use those endpoints for OP/ED; use the endpoints below.

---

## 2. Server and authentication

- Base URL (LAN): `http://192.168.1.240:8096`. All paths below are relative to the server base URL (no `/jellyfin`
  prefix on this server).
- Use the access token the client already has from the normal Jellyfin sign-in
  (`POST /Users/AuthenticateByName` → `AccessToken`). Any Jellyfin user can call the read endpoints; they only see
  themes for series that user has access to.

Two ways to send the token (both verified):

| Where | Format | Use for |
|---|---|---|
| Header | `Authorization: MediaBrowser Token="<accessToken>"` (the usual Jellyfin header; it may also carry `Client=`, `Device=`, `DeviceId=`, `Version=`) | All JSON requests |
| Query string | `?ApiKey=<accessToken>` | Media URLs handed to AVPlayer, which can't add headers |

Do **not** use `X-Emby-Token` or `?api_key=`: Jellyfin 12 rejects both with **401**.

Verification note: the token forms above were tested with a server API key. A user session token is handled the same
way by Jellyfin and is expected to work identically, but that hasn't been separately tested.

---

## 3. Endpoints for clients

All responses are JSON in **PascalCase**. **Fields whose value is null are omitted from the JSON entirely**, not sent
as `null`, so model every optional field as optional. Jellyfin ids are 32-character lowercase hex strings without
dashes (e.g. `03e4ecbedc176e73cd47fb3032c6b189`); treat them as opaque strings.

### 3.1 List themes for a show, season or episode

```
GET /OpEd/Items/{itemId}/Themes
GET /OpEd/Items/{itemId}/Themes?type=OP        (or type=ED; case-insensitive)
```

`{itemId}` is a normal Jellyfin item id. What you get depends on the item type:

| Item | Themes returned |
|---|---|
| Series | All themes of the series |
| Season | Themes belonging to that season, **plus** show-wide themes (those with no `SeasonId`) |
| Episode | Same as its season, and each theme also gets `PlaysInEpisode` (see below) |
| Anything else (movie, etc.) | `200` with `"Themes": []` and no `SeriesId` |

**Response: `ThemeList`**

| Field | Type | Always present | Meaning |
|---|---|---|---|
| `ItemId` | string | yes | The id you asked for |
| `SeriesId` | string | no | The series the item belongs to; omitted for non-TV items |
| `Themes` | `Theme[]` | yes | Possibly empty |

Themes are ordered: themes with a known season first (grouped by AniDB entry), then show-wide ones; within a group OPs
before EDs, then by number, version and variant.

**Status codes**

| Code | When |
|---|---|
| 200 | OK (including an empty list) |
| 401 | Missing or invalid token |
| 404 | Item doesn't exist or the user can't see it |
| 400 | Malformed item id (e.g. not a valid id format); treat like 404 |

### 3.2 Get one theme

```
GET /OpEd/Themes/{themeId}
```

Returns a single `Theme` (without `PlaysInEpisode`). Status: 200, 401, 404 (unknown id or the user can't see the series).

### 3.3 Media

| Endpoint | Content | Content-Type |
|---|---|---|
| `GET /OpEd/Themes/{themeId}/VideoMp4` | **Use this for video.** MP4, H.264 High profile, yuv420p, ≤1080p wide, AAC-LC stereo 192 kbps, `moov` atom at the front (fast start) | `video/mp4` |
| `GET /OpEd/Themes/{themeId}/FullVideoMp4`, `/FullAudioM4a` (0.4.0) | The full-length version (`OP1-Full`), as playable MP4 and AAC. `/FullVideo` and `/FullAudio` give the original files | as above |
| `GET /OpEd/Themes/{themeId}/AudioM4a` | **Use this for audio.** AAC-LC 256 kbps stereo in M4A, or the original file when it's already MP3/AAC | `audio/mp4` (or `audio/mpeg` for an original MP3) |
| `GET /OpEd/Themes/{themeId}/Audio` | The owner's audio file, unchanged (often Ogg/Opus, which tvOS can't play) | per file: `audio/ogg`, `audio/mpeg`, `audio/mp4`, `audio/flac`, `audio/wav` |
| `GET /OpEd/Themes/{themeId}/Video` | The owner's original video file, unchanged (often WebM/VP9, which tvOS can't play) | per file: `video/webm`, `video/mp4`, `video/x-matroska`, `video/quicktime` |

- HTTP range requests are supported: `Range: bytes=…` returns **206 Partial Content**, so AVPlayer can seek and start
  quickly.
- `HEAD` is **not** supported (405). AVPlayer uses ranged GETs, so this doesn't affect it.
- 404 if the theme has no such file, the copy isn't ready yet (`VideoMp4`, `AudioM4a`), or the user can't see the series.
- Build media URLs as `{server}{VideoMp4Url}?ApiKey={accessToken}` (the `*Url` fields are server-relative paths
  without a query string).

### 3.4 The published libraries (0.2.0)

When the owner turns on **Publish the themes as Jellyfin libraries**, the themes are also ordinary Jellyfin items in two
libraries: a **Music Videos** library (`MusicVideo` items, one per theme with a playable MP4) and a **Music** library
(`Audio` items, an album per show). Browse, search, favourite, mark watched and add them to playlists with Jellyfin's own
API, as for anything else.

```
GET /OpEd/Library
→ { "VideosLibraryId": "…", "SongsLibraryId": "…" }     each omitted until that library exists
```

```
GET /OpEd/LibraryItems/{itemId}/Theme
```

The `Theme` behind a published music video or song (for example one met in a playlist or a search), so a client can show
"OP 2 · show", link to the series and blur spoilers. 404 when the item isn't one the plugin published, or the user can't
see it.

Published items carry what the plugin knows: the song as the title, the artists, the show as the album, the season's
premiere year, genres `Anime` plus `Opening`/`Ending`, tags `Opening`/`Ending`, the season name, `Spoiler`, `NSFW`, and for
NSFW videos the owner's chosen rating (default `NC-17`), so Jellyfin's parental controls apply.

---

## 4. The `Theme` object

| Field | Type | Always present | Meaning |
|---|---|---|---|
| `Id` | string | yes | Stable theme id (16 hex chars). Survives rescans as long as the owner doesn't move or rename the theme's slot in the folder. Safe to cache and to store favourites against |
| `Type` | string | yes | `"OP"` or `"ED"` |
| `Sequence` | int | yes | The number: OP**1**, ED**2**. 1 when the file has no number |
| `Version` | int | yes | Version of the same song's sequence (OP1**v2**: different animation). 1 by default |
| `Variant` | string | no | Variant tag, e.g. `"TV"` for a TV-length version (`ED1-TV`) |
| `Slug` | string | yes | Display code: `"OP1"`, `"ED2"`, `"ED1-TV"`. Note: doesn't include the version |
| `SeriesId` | string | no* | Jellyfin series id (*always present for themes the API returns) |
| `SeasonId` | string | no | Jellyfin season id when the theme belongs to one season. Omitted = show-wide (season unknown) |
| `AniDbId` | int | no | AniDB anime id of the season/entry the theme belongs to |
| `SongTitle` | string | no | e.g. `"Yuusha"` |
| `Artists` | string[] | yes | e.g. `["YOASOBI"]`; may be empty |
| `Episodes` | string | no | Episodes the theme plays in, as text: `"1-16"`, `"1-14, 16"`, `"1"`. Numbers are episode numbers *within the theme's season* |
| `PlaysInEpisode` | bool | no | Only in episode requests: `true`/`false` whether this theme plays in that episode (from `Episodes`). Omitted when unknown (no episode list, or the theme's season differs from the episode's) |
| `Spoiler` | bool | yes | The video/song spoils the story; consider blurring or warning before showing |
| `Nsfw` | bool | yes | Contains NSFW content; consider hiding or warning |
| `HasVideo` | bool | yes | A video file exists |
| `HasAudio` | bool | yes | An audio file exists |
| `VideoUrl` | string | no | Path to the original video (`/OpEd/Themes/{Id}/Video`) |
| `VideoContainer` | string | no | Original video's extension: `webm`, `mp4`, `mkv`, `m4v`, `mov` |
| `VideoMp4Url` | string | no | Path to the playable MP4 (`/OpEd/Themes/{Id}/VideoMp4`). **Present only when `VideoMp4Status` is `"ready"`** |
| `VideoMp4Status` | string | no | `"pending"`, `"converting"`, `"ready"` or `"failed"`; omitted when `HasVideo` is false |
| `AudioUrl` | string | no | Path to the original audio file (`/OpEd/Themes/{Id}/Audio`) |
| `AudioM4aUrl` | string | no | Path to the playable audio (`/OpEd/Themes/{Id}/AudioM4a`). **Present only when `AudioM4aStatus` is `"ready"`** |
| `AudioM4aStatus` | string | no | `"pending"`, `"converting"`, `"ready"` or `"failed"`; omitted when `HasAudio` is false |
| `AudioContainer` | string | no | Audio file's extension: `ogg`, `opus`, `mp3`, `flac`, `m4a`, `aac`, `wav` |
| `MetadataSource` | string | no | Where the song details came from: `"themes.json"`, `"filename"`, `"animethemes"`. Omitted = no song details yet |
| `Overlap` | string | no | From AnimeThemes: `"none"`, `"transition"` or `"over"` (episode footage shows through the theme). Omitted when unknown |
| `HasCreditlessVersion` | bool | no | From AnimeThemes: whether a creditless version of this OP/ED exists there. Omitted when unknown |
| `LibraryItemId` | string | no | The `MusicVideo` item this theme was published as (0.2.0). Omitted when it isn't published |
| `SongItemId` | string | no | The `Audio` item this theme's song was published as (0.2.0). Omitted when it isn't published |
| `HasFullVersion` | bool | yes | Whether the owner added a full-length version (`OP1-Full`) (0.4.0) |
| `FullVideoMp4Url` / `FullVideoMp4Status` | string | no | As `VideoMp4Url` / `VideoMp4Status`, for the full version (0.4.0) |
| `FullAudioM4aUrl` / `FullAudioM4aStatus` | string | no | As `AudioM4aUrl` / `AudioM4aStatus`, for the full version (0.4.0) |
| `FullLibraryItemId` / `FullSongItemId` | string | no | The full version's own published items, titled "Song (Full)" (0.4.0) |
| `IsFullVersionItem` | bool | no | Only from `LibraryItems/{itemId}/Theme`: whether that item is the full version (0.4.0) |

`VideoMp4Status` / `AudioM4aStatus` lifecycle: a new file is `pending`, becomes `converting` and then `ready`, usually
within seconds (a 90-second 720p OP video converts in about 15–25 s, its audio in about 1.5 s; files already in an
Apple format are ready almost instantly). Only one file converts at a time, so a batch of new files can take a few
minutes. `failed` means the server couldn't convert it and won't retry
until the owner replaces the file. If the client shows a theme whose status is `pending` or `converting`, re-fetch the
theme (`GET /OpEd/Themes/{id}`) every ~10–15 s, or simply on the next screen load. Don't poll faster.

---

## 5. Media formats on tvOS — what to play

- **Video:** play `VideoMp4Url` (H.264/AAC MP4). Never hand `VideoUrl` to AVPlayer; it may be WebM/VP9.
- **Audio only:** play `AudioM4aUrl` (AAC M4A, or an original MP3). Never hand `AudioUrl` to AVPlayer; it may be
  Ogg/Opus.
- A theme can have just a video, just audio, or both. With only a video, you can still play it audio-only: use
  `VideoMp4Url` and don't show the picture (its audio track is AAC).
- If the `…Url` you want is missing, check its status: `pending`/`converting` means "ready in a moment" (re-fetch
  later); `failed` means that file can't be played on this device.

---

## 6. Example responses (real, from the server)

Series request — `GET /OpEd/Items/03e4ecbedc176e73cd47fb3032c6b189/Themes`:

```json
{
  "ItemId": "03e4ecbedc176e73cd47fb3032c6b189",
  "SeriesId": "03e4ecbedc176e73cd47fb3032c6b189",
  "Themes": [
    {
      "Id": "0eed5cc8d676a9c8",
      "Type": "ED", "Sequence": 1, "Version": 1, "Variant": "TV", "Slug": "ED1-TV",
      "SeriesId": "03e4ecbedc176e73cd47fb3032c6b189",
      "SeasonId": "…", "AniDbId": 17617,
      "SongTitle": "bliss", "Artists": ["milet"], "Episodes": "1",
      "Spoiler": false, "Nsfw": false,
      "HasVideo": false, "HasAudio": true,
      "AudioUrl": "/OpEd/Themes/0eed5cc8d676a9c8/Audio", "AudioContainer": "ogg",
      "AudioM4aUrl": "/OpEd/Themes/0eed5cc8d676a9c8/AudioM4a", "AudioM4aStatus": "ready",
      "MetadataSource": "animethemes"
    },
    {
      "Id": "4176c129755c36cf",
      "Type": "OP", "Sequence": 1, "Version": 1, "Slug": "OP1",
      "SeriesId": "03e4ecbedc176e73cd47fb3032c6b189",
      "SeasonId": "…", "AniDbId": 17617,
      "SongTitle": "Yuusha", "Artists": ["YOASOBI"], "Episodes": "1-16",
      "Spoiler": false, "Nsfw": false,
      "HasVideo": true, "HasAudio": true,
      "VideoUrl": "/OpEd/Themes/4176c129755c36cf/Video", "VideoContainer": "webm",
      "VideoMp4Url": "/OpEd/Themes/4176c129755c36cf/VideoMp4", "VideoMp4Status": "ready",
      "AudioUrl": "/OpEd/Themes/4176c129755c36cf/Audio", "AudioContainer": "ogg",
      "AudioM4aUrl": "/OpEd/Themes/4176c129755c36cf/AudioM4a", "AudioM4aStatus": "ready",
      "MetadataSource": "animethemes"
    },
    {
      "Id": "10c93ded3eea1c53",
      "Type": "OP", "Sequence": 1, "Version": 1, "Slug": "OP1",
      "SeriesId": "03e4ecbedc176e73cd47fb3032c6b189",
      "SeasonId": "30ac88a9678081643d0d32724bc60a54", "AniDbId": 18886,
      "SongTitle": "lulu.", "Artists": ["Mrs. GREEN APPLE"], "Episodes": "1-10",
      "Spoiler": false, "Nsfw": false,
      "HasVideo": false, "HasAudio": true,
      "AudioUrl": "/OpEd/Themes/10c93ded3eea1c53/Audio", "AudioContainer": "ogg",
      "AudioM4aUrl": "/OpEd/Themes/10c93ded3eea1c53/AudioM4a", "AudioM4aStatus": "ready",
      "MetadataSource": "animethemes"
    }
  ]
}
```

(Two themes are both `OP1`: one for season 1, AniDB 17617, and one for season 2, AniDB 18886. Distinguish them by
`SeasonId`/`AniDbId`, never by `Slug` alone.)

Episode request (season 1, episode 5): the season-2 theme is excluded, and each theme carries `PlaysInEpisode`:

```json
{ "ItemId": "…", "SeriesId": "03e4…b189",
  "Themes": [
    { "Id": "0eed5cc8d676a9c8", "Slug": "ED1-TV", "SongTitle": "bliss",  "PlaysInEpisode": false, "…": "…" },
    { "Id": "4176c129755c36cf", "Slug": "OP1",    "SongTitle": "Yuusha", "PlaysInEpisode": true,  "…": "…" }
  ] }
```

Non-TV item: `{"ItemId":"14d3a6f1f4af809451fe629e07b65891","Themes":[]}`. Series without themes: same with `SeriesId`.

---

## 7. Swift reference

The server sends PascalCase keys; the models below use normal Swift camelCase and a decoder that lower-cases each
key's first letter (`AniDbId` → `aniDbId`, `Id` → `id`). (A property literally named `Type` would clash with Swift's
`.Type` metatype syntax, which is why the models don't mirror the JSON names.)

```swift
import AVFoundation
import Foundation

struct OpEdThemeList: Decodable {
    let itemId: String
    let seriesId: String?
    let themes: [OpEdTheme]
}

struct OpEdTheme: Decodable, Identifiable {
    let id: String
    let type: String              // "OP" / "ED"
    let sequence: Int
    let version: Int
    let variant: String?
    let slug: String
    let seriesId: String?
    let seasonId: String?
    let aniDbId: Int?
    let songTitle: String?
    let artists: [String]
    let episodes: String?
    let playsInEpisode: Bool?
    let spoiler: Bool
    let nsfw: Bool
    let hasVideo: Bool
    let hasAudio: Bool
    let videoUrl: String?
    let videoContainer: String?
    let videoMp4Url: String?
    let videoMp4Status: String?
    let audioUrl: String?
    let audioContainer: String?
    let audioM4aUrl: String?
    let audioM4aStatus: String?
    let metadataSource: String?
}

private struct AnyKey: CodingKey {
    var stringValue: String
    var intValue: Int? { nil }
    init(stringValue: String) { self.stringValue = stringValue }
    init?(intValue: Int) { nil }
}

extension JSONDecoder {
    /// Decodes the plugin's PascalCase JSON into camelCase Swift properties.
    static let opEd: JSONDecoder = {
        let d = JSONDecoder()
        d.keyDecodingStrategy = .custom { path in
            let key = path.last!.stringValue
            return AnyKey(stringValue: key.prefix(1).lowercased() + key.dropFirst())
        }
        return d
    }()
}

final class OpEdClient {
    let server: URL          // e.g. http://192.168.1.240:8096
    let accessToken: String  // from AuthenticateByName

    init(server: URL, accessToken: String) {
        self.server = server
        self.accessToken = accessToken
    }

    /// Themes for a series, season or episode. Returns an empty list when the item has none; nil when it's
    /// unknown or the user can't see it.
    func themes(for itemId: String, type: String? = nil) async throws -> OpEdThemeList? {
        // URLComponents, not string concatenation: appendingPathComponent would escape a "?".
        var comps = URLComponents(url: server.appendingPathComponent("OpEd/Items/\(itemId)/Themes"), resolvingAgainstBaseURL: false)!
        if let type { comps.queryItems = [URLQueryItem(name: "type", value: type)] }
        var request = URLRequest(url: comps.url!)
        request.setValue("MediaBrowser Token=\"\(accessToken)\"", forHTTPHeaderField: "Authorization")
        let (data, response) = try await URLSession.shared.data(for: request)
        switch (response as? HTTPURLResponse)?.statusCode {
        case 200: return try JSONDecoder.opEd.decode(OpEdThemeList.self, from: data)
        case 400, 404: return nil
        default: throw URLError(.badServerResponse)
        }
    }

    /// URL AVPlayer can open directly (token in the query string).
    func mediaURL(_ relativePath: String) -> URL {
        var comps = URLComponents(url: server.appendingPathComponent(relativePath), resolvingAgainstBaseURL: false)!
        comps.queryItems = [URLQueryItem(name: "ApiKey", value: accessToken)]
        return comps.url!
    }

    /// What to play on tvOS. Video: the MP4. Audio only: the M4A, or the MP4's audio track when there's no audio file.
    /// nil while the copy is still being made (re-fetch the theme later) or when it couldn't be made.
    func playableURL(for theme: OpEdTheme, audioOnly: Bool = false) -> URL? {
        if audioOnly {
            if let a = theme.audioM4aUrl { return mediaURL(a) }
            if let v = theme.videoMp4Url { return mediaURL(v) }   // play it without showing the picture
            return nil
        }
        if let v = theme.videoMp4Url { return mediaURL(v) }
        if let a = theme.audioM4aUrl { return mediaURL(a) }       // no video: fall back to the song
        return nil
    }
}

// Usage:
// let list = try await client.themes(for: episodeId)
// let op = list?.themes.first { $0.type == "OP" && $0.playsInEpisode == true }
//       ?? list?.themes.first { $0.type == "OP" }
// if let op, let url = client.playableURL(for: op) {
//     player.replaceCurrentItem(with: AVPlayerItem(url: url))
// }
```

---

## 8. UI guidance and edge cases

- **Choosing "the" OP/ED for an episode:** prefer the theme with `PlaysInEpisode == true`. If none says true (no
  episode data), fall back to the theme whose `SeasonId` equals the episode's season, then to show-wide themes, lowest
  `Sequence` first.
- **Display name:** `"\(Slug) · \(SongTitle ?? "Untitled") — \(Artists.joined(separator: ", "))"`; when `SongTitle`
  is missing just show `Slug`. Several themes can share a `Slug` across seasons, so show the season too when listing a
  whole series.
- **Spoiler/NSFW:** honour them (blur thumbnail, ask before playing, or hide).
- **Missing details are normal:** `MetadataSource` omitted means no song info yet. Don't treat that as an error.
- **Not every show has themes:** an empty list is the common case; hide the OP/ED UI rather than showing an empty state.
- **Stability:** theme ids are stable across rescans; if a cached id returns 404, the owner removed or moved the theme;
  re-fetch the item's list.
- **Unknown fields:** the API may gain fields later; ignore any you don't know (Swift `Decodable` does this by default).
- **Thumbnails:** there's no thumbnail endpoint. Use the series/season artwork from Jellyfin
  (`/Items/{SeasonId or SeriesId}/Images/Primary`), or grab a frame from the MP4 client-side.
- **Rate:** these endpoints are cheap (in-memory lookups), but don't poll them faster than every ~10 s.

---

## 9. Admin endpoints (not needed by a normal client)

All require an administrator token.

| Method | Path | Purpose |
|---|---|---|
| POST | `/OpEd/Scan` | Rescan the themes folder now. Returns the scan report; MP4 conversion continues in the background |
| GET | `/OpEd/Status` | `{ "Scanning": bool, "Converting": bool, "Extracting": bool, "Report": { FinishedAt, Themes, WithMetadata, UnmatchedFolders[], UnparsedFiles[], NoMetadata[], Error } }` |
| GET | `/OpEd/Admin/Library` | Publishing status: `{ VideosLibraryId, SongsLibraryId, Videos, Songs, Syncing, LastSync, LastError, Problem }` |
| POST | `/OpEd/Admin/Library/Create` | Creates the Music Videos and Music libraries on the plugin's folder (each only if missing) and publishes; 409 `{ Message }` when the folder can't be used |
| POST | `/OpEd/Admin/Library/Sync` | Publishes now, in the background (202); 409 `{ Message }` when the folder can't be used |
| POST | `/OpEd/Admin/Themes/Rescan` | Body `{ "ThemeIds": [string] }`: picks up replaced files for those themes, makes them playable in place, originals to `_originals` |
| GET | `/OpEd/Admin/Libraries` | TV libraries the clip search can be limited to: `[{ Id, Name }]` |
| POST | `/OpEd/Admin/Clips/Extract` | Cuts missing OPs/EDs out of episodes at their chapter marks, into the review queue (202) |
| GET | `/OpEd/Admin/Clips` | The review queue and the extractor's progress |
| POST | `/OpEd/Admin/Clips/{clipId}/Approve` | Moves the clip into the themes folder (`?sequence=N` files it as OPN/EDN); 409 on a clash |
| POST | `/OpEd/Admin/Clips/Apply` | Indexes approved clips and fetches their metadata, in one batch (202) |
| POST | `/OpEd/Admin/Clips/{clipId}/Reject` | Deletes the clip and doesn't cut that slot again; `?tryAnother=true` cuts it from the next episode instead |
| DELETE | `/OpEd/Admin/Clips/Rejected` | Forgets rejections |
| GET | `/OpEd/Admin/Themes` | Every theme with its song details and their source (for an editor UI) |
| POST | `/OpEd/Admin/Themes/{themeId}/Details` | Body `{ "Title": string?, "Artists": [string]?, "Episodes": string?, "Spoiler": bool?, "Nsfw": bool? }`; saves to the show's themes.json; returns the updated `Theme` |
| DELETE | `/OpEd/Admin/Themes/{themeId}/Details` | Removes that theme's themes.json entry; returns the updated `Theme` |

The server rescans automatically at startup and every 12 hours.
