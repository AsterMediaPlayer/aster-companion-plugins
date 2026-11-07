# Aster Discovery and Tracking: design spec (draft, 2026-11-03)

A Jellyfin plugin in `aster-companion-plugins/AsterTracking/` that:

1. **scrobbles and tracks** what each Jellyfin user plays, to the services that user linked;
2. **serves discovery lists** (trending, recommended, watchlist) from those services to the Aster apps;
3. **switches Seerr on for the Aster apps**, and holds Seerr's connection, so a client's existing Seerr code
   stays off unless this plugin says otherwise.

Decided with the owner (2026-11-03): Jellyfin first (Emby port later, as with Aster Channels); **per-user accounts**;
first release covers **Trakt, Simkl, AniList, MyAnimeList, Last.fm, ListenBrainz and Hardcover**; **Seerr's
connection lives in the plugin**.

## Platform

- `Jellyfin.Plugin.AsterTracking`, `net10.0`, `Jellyfin.Controller` 12.1.0, targetAbi `12.1.0.0` (as AsterChannels).
- New GUID in `build.yaml` and `Plugin.Id`; tag `astertracking-vX.Y.Z` added to `release.yml`; `manifest.json` entry.
- Admin dashboard page, `Configuration/trackingPage.html` (menu section "server", icon `sync`).
- API controller under `/AsterTracking/…`, `[Authorize]` (any signed-in user) except the admin-only routes, which are
  `[Authorize(Policy = "RequiresElevation")]`.

## Services

| Service | Media | Sign-in | Developer app needed | What it does |
|---|---|---|---|---|
| Trakt | movies, TV (anime as TV) | OAuth **device code** (code on trakt.tv/activate) | yes: client id + secret | scrobble start/pause/stop with progress; history; ratings; watchlist; recommendations, trending |
| Simkl | movies, TV, anime | **PIN/device** flow (simkl.com/pin) | yes: client id | history sync on completion; watchlist; trending |
| AniList | anime (and manga, unused) | OAuth **PIN** (redirect `https://anilist.co/api/v2/oauth/pin`, the user pastes the token) | yes: client id (+ secret) | list progress (episodes watched), status Watching/Completed; trending, recommendations |
| MyAnimeList | anime | OAuth2 **PKCE**, redirect to a page on astermediaplayer.com that shows the code to paste | yes: client id (app type "other", no secret) | `my_list_status` episodes + status; seasonal, suggestions |
| Last.fm | music | `auth.getToken` → the user approves at last.fm → `auth.getSession` | yes: API key + shared secret (requests signed) | now playing; scrobble |
| ListenBrainz | music | the **user's token** pasted | no | playing now; listens |
| Hardcover | audiobooks | the **user's API token** pasted (GraphQL) | no | reading progress (%), status Reading/Read |

**Developer apps (decided):** Aster's own registrations on Simkl, AniList, MyAnimeList, Hardcover and Last.fm are
**built into official releases** (from the repository's Actions secrets, never the source), so users just link; the
admin page has fields to override them with the server's own, and a build from the source needs them.

No public write API (export only, not in scope): Letterboxd, IMDb, TV Time, Serializd, Goodreads, StoryGraph.

## Accounts (per user)

- Each Jellyfin user links their own accounts. Linking is driven through the plugin API, so the **Aster apps'
  settings** can show "Link Trakt" (code + URL), and the admin page can also link for a chosen user.
- Tokens are kept **per user in the plugin's data folder** (`data/AsterTracking/users/<userId>.json`), not in the plugin
  configuration XML (which the dashboard shows to any admin and backups copy). They are refreshed before expiry
  (Trakt, AniList: long-lived; MAL: 31-day access + refresh; Simkl, Last.fm, ListenBrainz, Hardcover: no expiry).
- Per user and per service: enabled, scrobble on/off, which libraries count (a kids' library excluded, say), and
  "mark watched at" threshold (default 80%; Trakt's own rule is 80%).

API (signed-in user, for themselves; admins may pass `userId`):

- `GET  /AsterTracking/Accounts` → each service: linked?, account name, settings.
- `POST /AsterTracking/Accounts/{service}/Link` → `{ url, code, expiresIn, interval }` (device/PIN flows) or
  `{ url }` (paste flows).
- `POST /AsterTracking/Accounts/{service}/Link/Complete` → body `{ token | code }` for paste flows; polls for device flows.
- `DELETE /AsterTracking/Accounts/{service}` → unlink (revoke where the service allows).
- `PUT  /AsterTracking/Accounts/{service}/Settings`.

## Scrobbling

- Listens to `ISessionManager` PlaybackStart / PlaybackProgress / PlaybackStopped and to `IUserDataManager`
  UserDataSaved (an item marked played by hand). Works for **every** client, not only Aster.
- **Video:** Trakt `/scrobble/start|pause|stop` with progress%; at the threshold (or a manual "played") → history on
  Trakt and Simkl, episode progress on AniList and MAL.
- **Music:** "now playing" at start; a scrobble once played ≥ 50% or ≥ 4 minutes and the track is longer than 30 s (the
  Last.fm rule, which ListenBrainz follows).
- **Audiobooks:** Hardcover progress % every few minutes while playing and on stop; "Read" at the end of the last part.
- **Matching:** provider ids on the item (Tmdb, Tvdb, Imdb; MusicBrainz for music). **Anime:** Shokofin gives AniDB ids;
  AniDB → AniList / MAL / Kitsu through the anime-offline-database mapping file (cached, refreshed weekly). The episode
  number sent is the AniDB episode number (Shokofin's), so split cours line up with AniList/MAL entries.
- **Reliability:** a per-user queue in the data folder, retried with backoff while a service is down; duplicates
  dropped (the same item and play within the same session); a log per user of the last 50 sends, shown on the pages.
- **Import (later):** pull watched history from Trakt/Simkl into Jellyfin's played state (opt-in, admin-confirmed).

## Discovery

- `GET /AsterTracking/Discover` → the lists available to this user, given their links: `{ service, list, title, mediaType }`.
- `GET /AsterTracking/Discover/{service}/{list}?mediaType=&page=` → normalised items:
  `{ title, year, mediaType, ids: { tmdb, tvdb, imdb, anilist, mal }, poster, overview, inLibrary: { itemId } | null,
  request: { canRequest, status } | null }`.
  - `inLibrary` is matched by provider ids against the user's own libraries, so the app can play instead of request.
  - `request` is filled only when Seerr is enabled (below), from Seerr's own status for that TMDB id.
- Lists: Trakt trending/popular/anticipated/recommended/watchlist; Simkl trending/watchlist; AniList trending/this
  season/recommended for you (from the user's list); MAL seasonal/suggestions; Last.fm/ListenBrainz top and recommended
  artists (music discovery, phase 2).
- Responses are cached per user (10 min for personal lists, 1 h for trending).

## Seerr

Admin page, "Requests (Seerr)": **Enable**, Seerr URL (the address the apps use).

- `GET /AsterTracking/Features` (any signed-in user) → `{ "Seerr": { "Enabled", "Url" }, "Services": [...] }` (Jellyfin's PascalCase). **The Aster apps
  check this before turning their Seerr code on**; with the plugin missing, the call 404s, which means "off".
- **Decided:** the apps sign the user in to Seerr with their Jellyfin login, so the plugin only hands out
  `{ enabled, url }`: no API key and no request proxy in the plugin. (Discovery's `request` status, phase 5, can come
  from the app's own Seerr session, or from an optional admin API key added then.)

## Phases

1. **Skeleton + Seerr switch:** plugin, admin page, `Features` endpoint, Seerr settings. The apps
   can start gating Seerr right away.
2. **Accounts + Trakt + Simkl:** linking (device/PIN), token storage, video scrobbling, history, watchlist.
3. **AniList + MyAnimeList:** AniDB mapping, episode progress, status.
4. **Last.fm + ListenBrainz + Hardcover:** music and audiobooks.
5. **Discovery endpoints** and in-library/Seerr matching; then history import; then the Emby port.

Each phase is testable on the home server (Jellyfin 12.1, Shokofin anime, music and audiobook libraries) before a tag.
