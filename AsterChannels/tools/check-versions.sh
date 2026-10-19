#!/bin/sh
# Checks that build.yaml, the project and Plugin.cs agree (version and guid),
# and, given a tag like v0.1.0, that it's the version being released.
set -eu
cd "$(dirname "$0")/../Jellyfin.Plugin.AsterChannels"

yaml_version=$(sed -n 's/^version: "\(.*\)"/\1/p' build.yaml)
yaml_guid=$(sed -n 's/^guid: "\(.*\)"/\1/p' build.yaml)
project_version=$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' Jellyfin.Plugin.AsterChannels.csproj)
plugin_guid=$(sed -n 's/.*Guid.Parse("\(.*\)").*/\1/p' Plugin.cs)

fail=0
[ "$yaml_version" = "$project_version" ] || { echo "build.yaml says $yaml_version, the project $project_version" >&2; fail=1; }
[ "$yaml_guid" = "$plugin_guid" ] || { echo "build.yaml's guid $yaml_guid isn't Plugin.cs's $plugin_guid" >&2; fail=1; }
if [ $# -gt 0 ]; then
    [ "${1#v}.0" = "$yaml_version" ] || { echo "The tag $1 isn't version $yaml_version" >&2; fail=1; }
fi
[ $fail = 0 ] && echo "Version $yaml_version, guid $yaml_guid: they agree."
exit $fail
