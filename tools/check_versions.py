"""Checks a plugin's release metadata agrees with itself, and prints its release notes.

    python3 tools/check_versions.py AsterDisc/Jellyfin.Plugin.AsterDisc            # build.yaml, Plugin.cs and the csproj agree
    python3 tools/check_versions.py AsterDisc/Jellyfin.Plugin.AsterDisc v0.6.0     # ...and v0.6.0 releases build.yaml's version
    python3 tools/check_versions.py AsterDisc/Jellyfin.Plugin.AsterDisc --notes    # this version's changelog entry
    python3 tools/check_versions.py AsterDisc/Jellyfin.Plugin.AsterDisc --name     # the plugin's name, from build.yaml

A plugin's identity is written in three places that must not drift: build.yaml (what the packaging tool writes into
meta.json), Plugin.cs (what Jellyfin treats as authoritative) and the csproj's assembly version. A tag "vX.Y.Z" (the
release workflow strips the "asterdisc-" or similar prefix) releases version "X.Y.Z.0".
"""
import os
import re
import sys


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def field(text, name):
    m = re.search(rf'^{name}:\s*"([^"]*)"', text, re.M)
    return m.group(1) if m else None


def release_notes(build_yaml, version):
    """The changelog entry that starts with "- <version>:", with any indented continuation lines."""
    m = re.search(r"^changelog:\s*[>|]-?\s*\n((?:[ \t]+.*\n?|\n)+)", build_yaml, re.M)
    if not m:
        return None
    out, keep = [], False
    for line in m.group(1).splitlines():
        stripped = line.strip()
        if stripped.startswith("- "):
            keep = stripped.startswith(f"- {version}:")
        if keep:
            out.append(re.sub(r"^\s{0,4}", "", line))
    return "\n".join(out).strip() or None


def check(project_dir, tag=None):
    name = os.path.basename(os.path.normpath(project_dir))
    build = read(os.path.join(project_dir, "build.yaml"))
    plugin = read(os.path.join(project_dir, "Plugin.cs"))
    csproj = read(os.path.join(project_dir, f"{name}.csproj"))
    problems = []

    version, guid = field(build, "version"), field(build, "guid")
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        problems.append(f"build.yaml: version {version!r} is not four numbers like 0.1.0.0")
    m = re.search(r'Guid\.Parse\("([0-9a-fA-F-]{36})"\)', plugin)
    if not m:
        problems.append("Plugin.cs: could not find the plugin's Id")
    elif guid is None or m.group(1).lower() != guid.lower():
        problems.append(f"the guid in build.yaml ({guid}) and Plugin.cs ({m.group(1)}) differ: Jellyfin would treat them as two plugins")
    for prop in ("AssemblyVersion", "FileVersion"):
        v = re.search(rf"<{prop}>([^<]*)</{prop}>", csproj)
        if not v or v.group(1) != version:
            problems.append(f"{name}.csproj: {prop} is {v.group(1) if v else 'missing'}, build.yaml says {version}")
    if version and not release_notes(build, version):
        problems.append(f"build.yaml: the changelog has no entry starting '- {version}:'")
    if tag is not None:
        t = re.fullmatch(r"v(\d+)\.(\d+)\.(\d+)", tag)
        released = f"{t.group(1)}.{t.group(2)}.{t.group(3)}.0" if t else None
        if released != version:
            problems.append(f"the tag {tag} releases version {released}, but build.yaml says {version}")
    return problems


def main(argv):
    if not argv or len(argv) > 2:
        print(__doc__, file=sys.stderr)
        return 2
    project_dir = argv[0]
    build = read(os.path.join(project_dir, "build.yaml"))
    if len(argv) == 2 and argv[1] == "--notes":
        text = release_notes(build, field(build, "version"))
        if not text:
            print("no changelog entry for this version", file=sys.stderr)
            return 1
        print(text)
        return 0
    if len(argv) == 2 and argv[1] == "--name":
        print(field(build, "name"))
        return 0
    problems = check(project_dir, argv[1] if len(argv) == 2 else None)
    for p in problems:
        print("error:", p, file=sys.stderr)
    if not problems:
        print(f"{field(build, 'name')} {field(build, 'version')}: release metadata is consistent")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
