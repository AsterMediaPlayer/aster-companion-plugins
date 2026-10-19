# Releasing

A release is a plugin zip and a plugin-repository manifest, published on GitHub Releases by
[`.github/workflows/release.yml`](../.github/workflows/release.yml) when a version tag is pushed. Jellyfin installs from the
manifest, which always lives at

```
https://github.com/peppy6582/jellydisc/releases/latest/download/manifest.json
```

Each release's manifest contains every earlier version as well (the workflow downloads the previous manifest and adds to it), so
a server can still choose an older one.

## Cutting a release

1. **Bump the version** in three places, which must agree (`tools/check_release.py` and CI check this):
   - `Jellyfin.Plugin.DiscMenus/build.yaml`: `version` (four numbers, such as `0.2.0.0`) and a new `changelog` entry
     starting `- 0.2.0.0:` (that entry becomes the release notes);
   - `Jellyfin.Plugin.DiscMenus/Jellyfin.Plugin.DiscMenus.csproj`: `AssemblyVersion` and `FileVersion`.
2. **Merge to `main`** and let CI pass. Its *Plugin package* job builds the same zip a release would.
3. **Tag the commit and push the tag.** A tag `vX.Y.Z` releases version `X.Y.Z.0`:

   ```bash
   git tag v0.2.0 && git push origin v0.2.0
   ```

The workflow then checks that the tag, `build.yaml`, `Plugin.cs` and the project agree, runs the tests, builds the package with
[jprm](https://github.com/oddstr13/jellyfin-plugin-repository-manager), adds it to the manifest, and creates the release. It refuses
to touch a release that already exists. If it fails part-way, fix the cause and delete the tag (`git push --delete origin v0.2.0`
and `git tag -d v0.2.0`) before trying again.

## Things to know

- **`targetAbi`** (in `build.yaml`) is the oldest Jellyfin the plugin loads in. Raise it when the plugin starts needing a newer one.
- **The plugin's `guid`** must be the same in `build.yaml` and `Plugin.cs`, and must never change: Jellyfin treats a different guid as
  a different plugin. `check_release.py` fails if they differ.
- **Releases are not marked pre-release**, even while the project is alpha, because GitHub's `latest` address (which the manifest
  URL uses) skips pre-releases. The release notes and the README say what stage it is.
- **No checksum is typed by hand.** jprm computes it from the zip, and Jellyfin verifies it when installing.
- To try the packaging without publishing: `jprm plugin build Jellyfin.Plugin.DiscMenus --output "$PWD/dist"` (the output path
  must be absolute), or download the *disc-menus-package* artifact from any CI run.
