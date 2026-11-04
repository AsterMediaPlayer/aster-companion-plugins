# Aster Tracking

Aster Media Player's tracking plugin for Jellyfin: scrobbling and discovery through each person's own Trakt, Simkl,
AniList, MyAnimeList, Last.fm, ListenBrainz and Hardcover accounts, and the switch that turns Aster's Seerr requests on.
The design, and the phases it's built in, are in [SPEC.md](SPEC.md).

## 0.1: the Seerr switch

Dashboard ▸ Plugins ▸ Aster Tracking ▸ **Requests (Seerr)**: turn requests on and give Seerr's address. The apps read

    GET /AsterTracking/Features      (any signed-in user)
    → { "Seerr": { "Enabled": true, "Url": "https://requests.example.com" }, "Services": [] }

before showing their Seerr features. Seerr counts as on only with an http(s) address; without the plugin the call is a
404, which the apps take as off. People sign in to Seerr with their own Jellyfin login, so no Seerr key is kept here.

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

The dashboard page can do the same for any person. Aster's own Simkl app is built in; Developer apps ▸ Simkl client id
swaps in the server's own. Apps that scrobble to Simkl themselves should stop once `Features.Services` lists `simkl`
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

A whole pasted address works as well as the code. Until the server has an app for a service (built in, or under
Developer apps) the service is left out of `Features.Services` and linking it is a 409.

## 0.4: Last.fm

Songs played in any app show on Last.fm as "now playing" as they start, and are scrobbled, with the time they began,
once they've played half way or four minutes, when they're longer than 30 seconds (Last.fm's rule). The furthest
progress report counts as well as the stop, since some apps report a song stopped only after the next has loaded.
Audiobooks aren't songs, and a song marked played by hand isn't scrobbled.

Linking needs no code: **Link Last.fm** opens last.fm to approve Aster, and the page (or an app, polling
`POST /AsterTracking/Accounts/lastfm/Link/Complete`) waits for the approval. The session key never runs out. Every request
is signed with the API account's shared secret; Developer apps can swap in the server's own key and secret.

## Building

    dotnet build AsterTracking/Jellyfin.Plugin.AsterTracking -c Release
    python3 tools/check_versions.py AsterTracking/Jellyfin.Plugin.AsterTracking

Released by pushing a tag `astertracking-vX.Y.Z`.
