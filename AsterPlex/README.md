# Aster for Plex

The Aster Discovery and Tracking plugin's features for a **Plex** server, which can't take plugins: a small
container that runs beside Plex and answers the Aster apps the way the Jellyfin plugin does.

- **Tracking:** each person on the server links their own Simkl, AniList, MyAnimeList, Last.fm, ListenBrainz and
  Hardcover accounts from the app (Settings › Tracking), and what they watch and listen to in **any** Plex app is sent
  there, with the plugin's rules, retries and Recent Sends.
- **Add to Watchlist** on title pages: Simkl's Plan to Watch, AniList's Planning, MyAnimeList's Plan to Watch.

- **A settings page** at `/aster` on the container: sign in with Plex, pick your server, and each person links and
  unlinks their own accounts and sees Recent Sends. The owner also sets the container's public address, which is
  added to Plex's **Custom server access URLs** so the Aster apps find the container with nothing to set up, picks
  which music libraries are audiobooks, and can use the server's own service apps in place of Aster's.

Coming next: the Channels designer.

## How it works

- **No webhooks, no Plex Pass.** The container reads the server's sessions every 10 seconds, as the owner, and turns
  what started, moved on and stopped into the plugin's playback events. Every 5 minutes it also reads Plex's watch
  history, for anything played while it was down. Nothing has to reach the container from Plex.
- **Who's who.** The apps send their Plex token, as they do to Plex. The container matches it to one of the server's
  people (the owner, the owner's Home, and friends the server is shared with) through plex.tv, and keeps each
  person's linked accounts in its data folder, `/config/users`.
- **One of Plex's addresses.** Everything that isn't the container's own API is passed straight through to Plex, so
  the container's address works as a Plex address for any Plex app too.
- **The same code as the plugin.** The service clients, the scrobbler and the API controllers are copies of
  `AsterTracking/Jellyfin.Plugin.AsterTracking`, with Jellyfin's items swapped for `Tracking/PlayedItem.cs`. Keep fixes
  in step with the plugin until the two share a library.

## Run it

```yaml
services:
  aster-plex:
    image: ghcr.io/astermediaplayer/aster-plex:latest
    restart: unless-stopped
    ports: ["8098:8098"]
    environment:
      PLEX_URL: "http://192.168.1.10:32400"
      PLEX_TOKEN: "the server owner's token"
    volumes:
      - ./aster-plex:/config
```

Then open `http://<container>:8098/aster/` and sign in with Plex. Without `PLEX_URL` and `PLEX_TOKEN`, the page sets
the container up instead: sign in as the server's owner and pick the server. See `docker-compose.example.yml`.
`ASTER_PORT` changes the port; `ASTER_DATA` the data folder outside Docker.

### Letting the apps find it

Plex's apps find a server through plex.tv, which hands them every address the server has, including the ones in
Plex's **Custom server access URLs** (Settings › Network). The settings page adds the container's public address
there. The container passes everything that isn't its own through to Plex, so the address works as a Plex address
for any Plex app; the Aster apps also find `/AsterTracking` on it. Give an address that works from away from home too
(behind your reverse proxy, with HTTPS), or the apps only find it at home.

### Anime

AniList and MyAnimeList need to know which anime entry an episode is. The container uses the AniDB id the
[HAMA](https://github.com/ZeroQI/Hama.bundle) agent gives a show matched as one AniDB entry (`anidb-…`), mapped through
ARM like the plugin. Shows from Plex's own TV agent still go to Simkl by their TVDB and TMDB ids.

### Audiobooks

Plex keeps audiobooks in music libraries. Music in a library with "book" in its name counts as audiobooks
(Hardcover's progress) rather than songs (Last.fm, ListenBrainz); `AudiobookLibraries` in `/config/settings.json`
names them instead.

## Build and test

```sh
dotnet build Aster.Plex
dotnet test Aster.Plex.Tests
docker build -t aster-plex .
```

The service app keys come in the same way as the plugin's: `ASTER_SIMKL_CLIENT_ID` and the rest, as build arguments
for an official image. A build without them reads the server's own from `settings.json`.

## License

GPL-3.0, like the plugins.
