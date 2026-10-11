#!/usr/bin/env bash
# Builds the plugin and packages it for download:
#   dist/jellyfin-oped_<version>.zip   the plugin (just the DLL; Jellyfin supplies every other assembly)
#   manifest.json                      plugin repository manifest, gains an entry for this version
#
# Attach the zip to a GitHub release tagged v<version>; people then add
#   https://raw.githubusercontent.com/peppy6582/jellyfin-oped/main/manifest.json
# under Dashboard -> Plugins -> Repositories. The manifest's checksum is the zip's MD5, so re-running this after the
# release is published means re-uploading the zip too.
set -euo pipefail
cd "$(dirname "$0")/.."

meta=Jellyfin.Plugin.OpEd/build.yaml
field() { sed -n "s/^$1: *\"\(.*\)\" *$/\1/p" "$meta" | head -1; }
version=$(field version)
guid=$(field guid)
name=$(field name)
owner=$(field owner)
overview=$(field overview)
category=$(field category)
target_abi=$(field targetAbi)
repo=peppy6582/jellyfin-oped
# The `description: >` and `changelog: >` blocks: indented lines joined with spaces.
block() { awk -v key="$1" '$0 ~ "^"key": *>" {on=1; next} on && /^[^ ]/ {on=0} on {sub(/^ +/, ""); printf "%s ", $0}' "$meta" | sed 's/ *$//'; }
description=$(block description)
changelog=$(block changelog)

out=${OPED_OUT:-/mnt/cache/appdata/jellyfin-oped-build}   # where `docker compose run --rm build` leaves the DLL
docker compose run --rm build
dll="$out/Jellyfin.Plugin.OpEd.dll"

mkdir -p dist
zip_name="jellyfin-oped_${version}.zip"
rm -f "dist/$zip_name"
(cd "$out" && zip -q -X "$OLDPWD/dist/$zip_name" Jellyfin.Plugin.OpEd.dll)
checksum=$(md5sum "dist/$zip_name" | cut -d' ' -f1)
url="https://github.com/$repo/releases/download/v${version}/${zip_name}"
now=$(date -u +%Y-%m-%dT%H:%M:%SZ)

entry=$(jq -n --arg version "$version" --arg changelog "$changelog" --arg abi "$target_abi" \
  --arg url "$url" --arg sum "$checksum" --arg ts "$now" \
  '{version: $version, changelog: $changelog, targetAbi: $abi, sourceUrl: $url, checksum: $sum, timestamp: $ts}')

[ -f manifest.json ] || echo '[]' > manifest.json
jq --arg guid "$guid" --arg name "$name" --arg owner "$owner" --arg overview "$overview" \
   --arg description "$description" --arg category "$category" --argjson entry "$entry" '
  (if any(.[]; .guid == $guid) then . else . + [{guid: $guid, name: $name, description: $description, overview: $overview,
     owner: $owner, category: $category, versions: []}] end)
  | map(if .guid == $guid
        then .name = $name | .description = $description | .overview = $overview | .owner = $owner | .category = $category
             | .versions = ([$entry] + (.versions | map(select(.version != $entry.version))))
        else . end)' manifest.json > manifest.json.tmp
mv manifest.json.tmp manifest.json

echo
echo "dist/$zip_name  md5 $checksum"
echo "Next: create release v${version} on GitHub and attach dist/$zip_name; commit manifest.json."
