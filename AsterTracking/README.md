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

    POST   /AsterTracking/Accounts/Simkl/Link            → { "Code", "Url", "CompleteUrl", "Interval", "ExpiresIn" }
    POST   /AsterTracking/Accounts/Simkl/Link/Complete   → 202 until the code's entered at simkl.com/pin, then 200; 410 if it ran out
    GET    /AsterTracking/Accounts                       → what's linked, sends waiting, the last sends
    PUT    /AsterTracking/Accounts/Simkl/Settings        { "Scrobble": false }
    DELETE /AsterTracking/Accounts/Simkl

The dashboard page can do the same for any person. Aster's own Simkl app is built in; Developer apps ▸ Simkl client id
swaps in the server's own. Apps that scrobble to Simkl themselves should stop once `Features.Services` lists `simkl`
and the person is linked here, or each watch is sent twice.

## Building

    dotnet build AsterTracking/Jellyfin.Plugin.AsterTracking -c Release
    python3 tools/check_versions.py AsterTracking/Jellyfin.Plugin.AsterTracking

Released by pushing a tag `astertracking-vX.Y.Z`.
