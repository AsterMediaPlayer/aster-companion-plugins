# Security policy

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Use GitHub's private reporting instead:
**Security tab → Report a vulnerability** on this repository. Include what you found, how to reproduce it,
and the Jellyfin and plugin versions. You'll get a reply as soon as someone can look at it.

## What counts

Menus are shareable files from untrusted sources, and the plugin adds admin endpoints to a media server, so
these are in scope:

- A menu file that makes the renderer run script, inject markup, load something it shouldn't, or reach
  outside what a menu is allowed to name.
- Any way to read or write a file outside the menus folder, or to read a file type other than the images and
  audio meant to be served from the assets folder.
- A way for a non-admin to reach an admin endpoint, or for an unauthenticated request to reach anything
  beyond the few endpoints that are public on purpose (the renderer script, the preview page, the assets
  folder's images and audio, and the callback File Transformation uses to inject the script).
- Anything that exposes another user's data, an access token, or a server path.

## Supported versions

The project is pre-release: fixes go to `main`.
