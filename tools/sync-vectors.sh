#!/bin/sh
# Copies the channel-tag vectors from a checkout of the Aster app, so the
# plugin is tested against the app's current rules:
#   tools/sync-vectors.sh ../AsterFIN
set -eu
app="${1:-../AsterFIN}"
cp "$app/AsterFINTests/Conformance/channel-tags.json" "$(dirname "$0")/../tests/channel-tags.json"
node --test "$(dirname "$0")/../tests/"
