"""Runs every conformance vector against conformance/python/disc_menus.py. Exit 0 = all pass.   python3 conformance/python/run_vectors.py"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import disc_menus as dm  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
CONF = os.path.abspath(os.path.join(HERE, ".."))
failures = 0
checked = 0


def load(name):
    with open(os.path.join(CONF, name), encoding="utf-8") as f:
        return json.load(f)


def fail(what, detail):
    global failures
    failures += 1
    if failures <= 15:
        print("FAIL", what, detail)


def same(a, b, tol=1e-6):
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(same(a[k], b[k], tol) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(same(x, y, tol) for x, y in zip(a, b))
    if isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool) and not isinstance(b, bool):
        return abs(a - b) <= tol
    return a == b


def content(n, back, home):
    return ([{"Action": "playExtra", "Label": f"E{i + 1}"} for i in range(n)]
            + ([{"Action": "back", "Label": "Back"}] if back else []) + ([{"Action": "home", "Label": "Home"}] if home else []))


for c in load("paginate.json")["cases"]:
    checked += 1
    entries = c["entries"] if "entries" in c else content(c["n"], c["back"], c["home"])
    got = [{"items": [e["Label"] for e in p["items"]], "backs": [e["Label"] for e in p["backs"]], "prev": p["prev"], "more": p["more"]}
           for p in dm.paginate(entries, c["slots"], c["maxPerPage"])]
    if got != c["pages"]:
        fail("paginate", {k: c.get(k) for k in ("slots", "maxPerPage", "n", "back", "home", "name")})

for c in load("cells.json")["cases"]:
    checked += 1
    if not same(dm.cell_position(c["flow"], c["slot"], c["tall"]), c["position"]):
        fail("cell", c)

docs = {}
for c in load("layout.json")["cases"]:
    checked += 1
    doc = docs.setdefault(c["document"], json.load(open(os.path.join(CONF, "documents", c["document"]), encoding="utf-8")))
    menu = doc["Menus"][c["menu"]]
    entries, pages, page = dm.entries_for_page(doc, menu, c["page"])
    if not (same(dm.effective_layout(doc, menu), c["effectiveLayout"]) and pages == c["pages"] and page == c["page"] and same(entries, c["entries"])):
        fail("layout", (c["document"], c["menu"], c["page"]))

DIRS = {"right": (1, 0), "left": (-1, 0), "down": (0, 1), "up": (0, -1)}
for c in load("focus.json")["cases"]:
    checked += 1
    for name, (dx, dy) in DIRS.items():
        got = dm.move_focus(c["rects"], c["from"], dx, dy)
        if got != c["expected"][name]:
            fail("focus", (c["layout"], c["from"], name, "got", got, "want", c["expected"][name]))

for c in load("time-format.json")["cases"]:
    checked += 1
    if dm.format_ticks(c["ticks"]) != c["text"]:
        fail("time", c)

PATTERNS = {"image": dm.SAFE_IMAGE, "audio": dm.SAFE_AUDIO, "colour": dm.HEX_COLOUR, "tmdb": dm.TMDB_PATH, "fanartId": dm.FANART_ID, "itemId": dm.ITEM_ID}
for c in load("safety.json")["cases"]:
    checked += 1
    if bool(PATTERNS[c["kind"]].match(c["value"])) != c["accepted"]:
        fail("safety", c)

print(f"{checked} vectors checked, {failures} failed")
sys.exit(1 if failures else 0)
