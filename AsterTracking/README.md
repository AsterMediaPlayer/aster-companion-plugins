# Aster Discovery and Tracking

Aster Media Player's tracking plugin for Jellyfin: scrobbling and discovery through each person's own Trakt, Simkl,
AniList, MyAnimeList, Last.fm, ListenBrainz and Hardcover accounts, and the switch that turns Aster's Seerr requests on.
The design, and the phases it's built in, are in [SPEC.md](SPEC.md).

## 0.1: the Seerr switch

Dashboard ▸ Plugins ▸ Aster Discovery and Tracking ▸ **Requests (Seerr)**: turn requests on and give Seerr's address. The apps read

    GET /AsterTracking/Features      (any signed-in user)
    → { "Seerr": { "Enabled": true, "Url": "https://requests.example.com" }, "Services": [] }

before showing their Seerr features; without the plugin the call is a 404, which the apps take as off. (From 0.8 the
apps reach Seerr through the plugin, below.)

## 0.2: Simkl

Each person links their own Simkl account; what they then watch, in any app, is added to their Simkl history once a
film or episode is stopped past 85% or marked played. Anime from Shokofin goes by its season's AniDB id (each Shoko
season is one AniDB entry, as Simkl lists anime), other shows by TMDB/IMDb/TVDB with season and episode. A send that
fails while Simkl is down waits in the person's file and is retried every 15 minutes. Tokens live in
`data/AsterTracking/users/<userId>.json`, not in the plugin configuration.

Linking, for the apps' settings (a signed-in person for themselves; an administrator or API key adds `?userId=`):

    POST   /AsterTracking/Accounts/simkl/Link            → { "Code", "Url", "CompleteUrl", "Interval", "ExpiresIn" }
    POST   /AsterTracking/Accounts/Simkl/Link/Complete   → 202 until the code's entered at simkl.com/pin, then 200; 410 if it ran out
    GET    /AsterTracking/Accounts                       → what's linked, sends waiting, the last sends
    PUT    /AsterTracking/Accounts/Simkl/Settings        { "Scrobble": false }
    DELETE /AsterTracking/Accounts/Simkl

The dashboard page can do the same for any person. Official releases carry Aster's own Simkl app; Developer apps ▸ Simkl client
id swaps in the server's own. Apps that scrobble to Simkl themselves should stop once `Features.Services` lists `simkl`
and the person is linked here, or each watch is sent twice.

## 0.3: AniList and MyAnimeList

Each anime episode a person watches moves that season's progress on their AniList and MyAnimeList lists (never back),
Watching, or Completed at the last episode, as the Aster apps do. Both sites list each season apart, so it's the
season that's matched: an AniList or MyAnimeList id the server has for it, else the AniDB id Shokofin gives each
season, mapped through [ARM](https://arm.haglund.dev). Only seasons carrying Shoko's own id count: other metadata
plugins put AniDB guesses on films and cartoons. Specials are skipped.

Neither site has a code sign-in like Simkl's, so linking is done on the dashboard: **Link**, sign in on the page that
opens, then paste back the code it shows.

- **AniList**: an app whose redirect is `https://anilist.co/api/v2/oauth/pin`; AniList itself shows the token
  (a year long, then link again). The apps' AniList app (55643) redirects to `aster://`, which a server can't receive.
- **MyAnimeList**: an app of type "other" whose redirect is `https://astermediaplayer.com/link/myanimelist/`, a page
  that only shows the code. The code is traded with a PKCE verifier only the server has, so it's no use to anyone
  else. Tokens last a month and are refreshed in the background.

    POST /AsterTracking/Accounts/{anilist|myanimelist}/Link            → { "Url", "Paste": true }
    POST /AsterTracking/Accounts/{anilist|myanimelist}/Link/Complete   { "Code": "…" } → 200 linked

A whole pasted address works as well as the code. Until the server has an app for a service (in an official release,
or under Developer apps) the service is left out of `Features.Services` and linking it is a 409.

## 0.4: Last.fm

Songs played in any app show on Last.fm as "now playing" as they start, and are scrobbled, with the time they began,
once they've played half way or four minutes, when they're longer than 30 seconds (Last.fm's rule). The furthest
progress report counts as well as the stop, since some apps report a song stopped only after the next has loaded.
Audiobooks aren't songs, and a song marked played by hand isn't scrobbled.

Linking needs no code: **Link Last.fm** opens last.fm to approve Aster, and the page (or an app, polling
`POST /AsterTracking/Accounts/lastfm/Link/Complete`) waits for the approval. The session key never runs out. Every request
is signed with the API account's shared secret; Developer apps can swap in the server's own key and secret.

## 0.5: ListenBrainz and Hardcover

**ListenBrainz** gets the same songs as Last.fm, by the same rule, with "playing now" as they start. Listens carry the
MusicBrainz recording, track, release, release group and artist ids the server has. People link it by pasting their
user token from listenbrainz.org/settings (no app to register; the token doesn't run out).

**Hardcover** follows audiobooks:
- **Matching:** the book is found by title and author through Hardcover's search, since Jellyfin has no ISBNs for
  `.m4b` audiobooks. The author comes from the item, or from the `audiobooks/Author/…` folder.
- **While listening:** the first progress puts the book in the person's library as Currently Reading. Progress is
  sent every 15 minutes while it plays and again when it stops, as the same share of Hardcover's audiobook edition,
  short of the very end.
- **Finishing:** it's marked Read at 98% or more, or when it's marked played.

Linking is Hardcover's OAuth device flow, a code entered at hardcover.app like Simkl's. Tokens refresh themselves, and
unlinking revokes them. It needs Aster's Hardcover app (type "Mobile, desktop, or CLI", device grant on, scopes
`read:me read:catalog:search read:library write:library`), or the server's own under Developer apps.

## 0.6: Discovery

The lists of each person's linked services, for the Aster apps' Discover, in one shape:

    GET /AsterTracking/Discover                                → [{ Service, List, Kind, Title, Personal }]
    GET /AsterTracking/Discover/{service}/{list}?page=&pageSize=  → { Items, Page, TotalItems, HasMore }

| Kind | Simkl | AniList | MyAnimeList |
|---|---|---|---|
| Watchlist | Plan to Watch (shows, films, anime) | Planning | Plan to Watch |
| Watching | Watching, with episode progress | Current and rewatching | Watching |
| Recommended | | recommendations for titles on the list | suggestions |
| Trending | today's films, shows and anime | trending anime | top airing |
| This Season | | this season's anime (MyAnimeList's for someone without AniList) | |

Each item has `Title`, `Year`, `MediaType` (`movie`/`tv`), `Poster`, `Overview`, `Progress` for Watching, `Anime`, and
`Ids`: `tmdb`, `tvdb`, `imdb`, and for anime `anilist`, `mal`, `anidb` and `tmdbSeason`. Anime from AniList and
MyAnimeList get the others through ARM in one batch per page, so the apps can open Seerr's page by TMDB id.
`InLibrary` is the film or show on this server for this person (anime by Shokofin's AniDB season first, else
TMDB/TVDB/IMDb), so the app can play it instead of requesting it. A later anime season matches the whole show. Lists
are kept 10 minutes per person; a signed-out service answers 410.

## 0.7: Discover built on the server

    GET /AsterTracking/Discover/Home[?fresh=true]   → { Rows: [{ Id, Kind, Title, Subtitle, Items }], Built }

The whole Discover page for the signed-in person, from **their own** plays, favourites and linked accounts only:

| Row (Kind) | Built from |
|---|---|
| Continue Watching on Your Services (`continue`) | the Watching lists |
| Recommended for You (`recommended`) | recommendations for what they've watched recently and their favourites (AniList for Shokofin anime, TMDB for the rest), plus their services' recommendations, scored together. Titles several sources suggest rank higher, server titles count 1.6×, and their genres add a little. Nothing they've watched or started, in any copy |
| Because You Watched *X* (`because`) | the three most recent titles that have recommendations |
| On Your Server, Not Yet Watched (`library`) | unstarted films and shows, by genre affinity, rating, and whether the recommendations suggested them. One per title |
| Your Watchlists (`watchlist`) | the Watchlist lists, merged |
| Trending (`trending`) | TMDB's week, with AniList's trending anime woven in (Simkl's without a TMDB key) |
| This Season's Anime (`season`) | AniList |

Server titles' `Poster` is server-relative (`/Items/{id}/Images/Primary`). The page is kept 30 minutes per person,
and takes a few seconds to build.

**TMDB needs the server admin's own key** (Discover › TMDB API key on the dashboard, free from themoviedb.org for
personal use). Aster has none built in: an app-wide key is a paid commercial licence. Without one, the TMDB parts are
left out.

## 0.8: Seerr through the server

The apps never sign in to Seerr. They call Seerr's own API through the plugin, with their Jellyfin sign-in:

    {GET|POST|PUT|DELETE} /AsterTracking/Seerr/api/v1/{Seerr's path}

The plugin calls Seerr with its API key **as the signed-in person** (`X-Api-User`), matched by the Jellyfin id Seerr
keeps for each user, so Seerr's own permissions, quotas and auto-approval apply. Someone without a Seerr account is
imported from Jellyfin on first use (Seerr's `user/import-from-jellyfin`), with Seerr's defaults for new users. Only
browsing, titles, search and requests pass: `auth/me`, `movie|tv|person|collection/…`, `discover/…`, `search`,
`request…` (create, edit, delete, approve, decline, retry: Seerr checks the person may), `genres`, `studio`,
`network`, `settings/discover`, `service/radarr|sonarr`, and `user/{own id}/quota|requests|watchlist`. Everything
else is 403. `Features.Seerr` is now `{ Enabled, Proxy }`: on only with Seerr's address (as this server reaches it)
and its API key set on the dashboard.

## 0.9: Kept out of Discover

The admin decides what never shows in Discover (dashboard: Discover > Kept out of Discover):

- **Adult titles**, as their source marks them (AniList `isAdult`, MyAnimeList `nsfw: black`, TMDB `adult`). On by default.
- **Blocked genres** and **blocked tags and keywords**, for everyone, plus extra blocks for **the people the admin
  ticks** (kids' accounts, say). Names match whole, ignoring case, and a name blocks whether a source calls it a genre or a tag.
- **What's checked:** AniList's and MyAnimeList's genres and tags, TMDB's genres and keywords, and the library's genres
  and tags for titles on the server. A title listed by several services carries what all of them know. Titles that
  came without genres (Simkl's) get AniList's (anime) or TMDB's (with a TMDB key; kept a day). Each source's names
  count separately: MyAnimeList calls some Ecchi titles Erotica. With requests on, each title's Seerr page (its
  genres and TMDB keywords) counts as well, so the rows and Seerr's pages agree: AniList doesn't call The Great Jahy
  Ecchi, TMDB's keywords do.

Applied to the plugin's lists and Discover page, and to Seerr's answers through the plugin. Lists lose their blocked
titles; a blocked title's Seerr page answers 404. Seerr's lists and search carry only genre ids, so with genre or tag
blocks each title's genres and keywords are looked up from Seerr (eight at a time, kept a day): about 0.2–0.7 s the
first time a page is seen, nothing after. Changing the blocks rebuilds everyone's Discover page.

## Building

    dotnet build AsterTracking/Jellyfin.Plugin.AsterTracking -c Release
    python3 tools/check_versions.py AsterTracking/Jellyfin.Plugin.AsterTracking

Released by pushing a tag `astertracking-vX.Y.Z`.

**Aster's own apps aren't in the source.** Official releases get Aster's registrations on Simkl, AniList,
MyAnimeList, Hardcover and Last.fm from the repository's Actions secrets (`ASTER_SIMKL_CLIENT_ID`,
`ASTER_ANILIST_CLIENT_ID`, `ASTER_MAL_CLIENT_ID`, `ASTER_HARDCOVER_CLIENT_ID`, `ASTER_LASTFM_API_KEY`,
`ASTER_LASTFM_SHARED_SECRET`), which the release workflow passes to the build; it refuses to release without them. A
build from the source has none: fill in Developer apps on the dashboard with your own, or set the same environment
variables when you build. If you publish a build of your own, register your own apps rather than reusing Aster's.
