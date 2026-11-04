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

## Building

    dotnet build AsterTracking/Jellyfin.Plugin.AsterTracking -c Release
    python3 tools/check_versions.py AsterTracking/Jellyfin.Plugin.AsterTracking

Released by pushing a tag `astertracking-vX.Y.Z`.
