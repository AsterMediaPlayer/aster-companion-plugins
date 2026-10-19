"""Checks a Disc Menus menu file against everything that can be checked outside Jellyfin.

Two layers, the same ones the plugin relies on for menus it didn't write:
  1. JSON Schema (schema/menu.schema.json)
  2. semantic(): the cross-reference rules JSON Schema can't express (dangling extra/menu keys, menus
     unreachable from root, positioned-all-or-none, flow grid size, ...)

As a library:   from menucheck import check_menu_file, check_menu_doc
As a command:   python3 tools/menucheck.py [--json] FILE...
                exit status 0 if every file passes, 1 if any fails, 2 for bad usage

The plugin's own loader (MenuFileLoader) is a third implementation of these rules; `tools/MenuCheck` runs
it on the same files. CI for a menu catalogue should run both.
"""
import json, sys, copy, os
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
from jsonschema import Draft202012Validator, FormatChecker

def _no_duplicate_keys(pairs):
    """A repeated key is an error, not "last one wins": the editor and the catalogue both need exactly one answer."""
    seen = set()
    for key, _ in pairs:
        if key in seen:
            raise ValueError(f"the key {key!r} appears more than once in the same object")
        seen.add(key)
    return dict(pairs)


def load(p):
    with open(p, encoding="utf-8") as f:
        return json.load(f, object_pairs_hook=_no_duplicate_keys)

ms, bs = load(os.path.join(ROOT,"schema","menu.schema.json")), load(os.path.join(ROOT,"schema","binding.schema.json"))
Draft202012Validator.check_schema(ms); Draft202012Validator.check_schema(bs)
mv = Draft202012Validator(ms, format_checker=FormatChecker())
bv = Draft202012Validator(bs, format_checker=FormatChecker())

def semantic(m):
    """Cross-reference checks JSON Schema can't express."""
    errs, menus, extras = [], m["menus"], m["extras"]
    if m["root"] not in menus: errs.append(f"root '{m['root']}' not in menus")
    for mk, menu in menus.items():
        for i, e in enumerate(menu["entries"]):
            a = e["action"]
            if a == "playExtra" and e["extra"] not in extras: errs.append(f"{mk}[{i}] unknown extra {e['extra']}")
            if a == "playSequence":
                errs += [f"{mk}[{i}] unknown extra {x}" for x in e["extras"] if x not in extras]
            if a == "submenu" and e["menu"] not in menus: errs.append(f"{mk}[{i}] unknown menu {e['menu']}")
            if a == "chapters" and e.get("menu") and e["menu"] not in menus: errs.append(f"{mk}[{i}] unknown menu {e['menu']}")
    for mk, menu in menus.items():
        placed = [("position" in e) for e in menu["entries"]]
        flow = menu.get("layout", {}).get("flow") or m.get("layout", {}).get("flow")
        if flow and any(placed):
            errs.append(f"menu '{mk}' uses flow layout, so its entries must not have positions")
        elif any(placed) and not all(placed):
            errs.append(f"menu '{mk}' mixes positioned and unpositioned entries (position all or none)")
    for mk, menu in menus.items():
        fl = menu.get("layout", {}).get("flow") or m.get("layout", {}).get("flow")
        if fl:
            actions = [e["action"] for e in menu["entries"]]
            pinned = ("back" in actions) + ("home" in actions)
            need = max(4, pinned + 3)
            if fl["columns"] * fl["rows"] < need:
                errs.append(f"menu '{mk}' needs a flow grid of at least {need} cells for its pinned Back/Home plus paging buttons")
    for where, lay in [("document", m.get("layout"))] + [(k, mn.get("layout")) for k, mn in menus.items()]:
        fl = (lay or {}).get("flow")
        if fl and fl["columns"] * fl["rows"] < 4:
            errs.append(f"{where} flow needs at least 4 cells (columns x rows) to fit paging buttons")
    seen, stack = set(), [m["root"]]
    while stack:
        k = stack.pop()
        if k in seen or k not in menus: continue
        seen.add(k)
        stack += [e["menu"] for e in menus[k]["entries"] if e["action"] == "submenu" or (e["action"] == "chapters" and e.get("menu"))]
    errs += [f"menu '{k}' unreachable from root" for k in menus if k not in seen]
    return errs

def check(validator, doc, sem=False):
    errs = [e.message for e in validator.iter_errors(doc)]
    if sem and not errs: errs = semantic(doc)
    return errs


MAX_ERRORS = 50


def check_menu_doc(doc):
    """Every problem with a parsed menu document, as readable strings. Empty means it passes both layers."""
    if not isinstance(doc, dict):
        return ["a menu must be a JSON object"]
    errs = []
    for e in sorted(mv.iter_errors(doc), key=lambda e: list(e.absolute_path)):
        where = "$" + "".join(f"[{p}]" if isinstance(p, int) else f".{p}" for p in e.absolute_path)
        errs.append(f"{where}: {e.message}")
    if errs:
        return errs[:MAX_ERRORS]
    try:
        return semantic(doc)[:MAX_ERRORS]
    except (KeyError, TypeError, AttributeError) as ex:   # a document the schema passed but semantic() can't read
        return [f"could not run the cross-reference checks ({type(ex).__name__}: {ex})"]


def check_menu_file(path):
    """Read and check one menu file. Returns a list of problems (empty = passes)."""
    try:
        with open(path, "rb") as f:
            raw = f.read()
    except OSError as ex:
        return [f"cannot read file: {ex.strerror or ex}"]
    try:
        doc = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=_no_duplicate_keys)
    except UnicodeDecodeError:
        return ["file is not valid UTF-8"]
    except ValueError as ex:
        return [f"not valid JSON: {ex}"]
    return check_menu_doc(doc)


def main(argv):
    as_json = "--json" in argv
    files = [a for a in argv if not a.startswith("--")]
    unknown = [a for a in argv if a.startswith("--") and a != "--json"]
    if not files or unknown:
        print(__doc__.split("As a command:")[1].split("The plugin")[0].strip(), file=sys.stderr)
        return 2
    results = [(p, check_menu_file(p)) for p in files]
    if as_json:
        print(json.dumps([{"file": p, "ok": not errs, "errors": errs} for p, errs in results], indent=2))
    else:
        for p, errs in results:
            print(("PASS " if not errs else "FAIL ") + p)
            for e in errs:
                print("  - " + e)
    return 0 if all(not errs for _, errs in results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
