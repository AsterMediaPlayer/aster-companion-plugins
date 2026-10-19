"""Checks that the things a release depends on agree with each other, and can print the release notes.

    python3 tools/check_release.py                  # guid, versions and changelog agree
    python3 tools/check_release.py --tag v0.1.0     # ...and the tag names the version in build.yaml
    python3 tools/check_release.py --notes          # the changelog entry for build.yaml's version, as release notes

The plugin's identity is written in three places that must not drift: build.yaml (what the packaging tool puts in
meta.json), Plugin.cs (what Jellyfin treats as authoritative at startup) and the csproj's assembly version. A tag
"vX.Y.Z" releases version "X.Y.Z.0". Exit 0 = consistent, 1 = something disagrees, 2 = bad usage.
"""
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
PROJECT = "Jellyfin.Plugin.DiscMenus"


def read(root, rel):
    with open(os.path.join(root, rel), encoding="utf-8") as f:
        return f.read()


def field(text, name):
    m = re.search(rf'^{name}:\s*"([^"]*)"', text, re.M)
    return m.group(1) if m else None


def tag_to_version(tag):
    m = re.fullmatch(r"v(\d+)\.(\d+)\.(\d+)", tag)
    return f"{m.group(1)}.{m.group(2)}.{m.group(3)}.0" if m else None


def release_notes(build_yaml, version):
    """The changelog entries that start with "- <version>:", with their indented continuation lines."""
    m = re.search(r"^changelog:\s*[>|]-?\s*\n((?:[ \t]+.*\n?|\n)+)", build_yaml, re.M)
    if not m:
        return None
    lines, out, keep = m.group(1).splitlines(), [], False
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("- "):
            keep = stripped.startswith(f"- {version}:")
        if keep:
            out.append(re.sub(r"^\s{0,4}", "", line))
    return "\n".join(out).strip() or None


def check(root, tag=None):
    problems = []
    build = read(root, f"{PROJECT}/build.yaml")
    plugin = read(root, f"{PROJECT}/Plugin.cs")
    csproj = read(root, f"{PROJECT}/{PROJECT}.csproj")

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
            problems.append(f"{PROJECT}.csproj: {prop} is {v.group(1) if v else 'missing'}, build.yaml says {version}")
    if version and not release_notes(build, version):
        problems.append(f"build.yaml: the changelog has no entry starting '- {version}:'")
    if tag is not None and tag_to_version(tag) != version:
        problems.append(f"the tag {tag} releases version {tag_to_version(tag)}, but build.yaml says {version}")
    return problems


def main(argv):
    tag, notes = None, False
    i = 0
    while i < len(argv):
        if argv[i] == "--tag" and i + 1 < len(argv):
            tag, i = argv[i + 1], i + 2
        elif argv[i] == "--notes":
            notes, i = True, i + 1
        else:
            print(__doc__, file=sys.stderr)
            return 2
    if notes:
        text = release_notes(read(ROOT, f"{PROJECT}/build.yaml"), field(read(ROOT, f"{PROJECT}/build.yaml"), "version"))
        if not text:
            print("no changelog entry for this version", file=sys.stderr)
            return 1
        print(text)
        return 0
    problems = check(ROOT, tag)
    for p in problems:
        print("error:", p, file=sys.stderr)
    if not problems:
        print("release metadata is consistent")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
