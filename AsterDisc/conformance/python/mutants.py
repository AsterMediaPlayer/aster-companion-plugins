"""Proves the conformance vectors can tell a wrong implementation from a right one.

Each mutant makes one small, plausible mistake in disc_menus.py (a wrong constant, a dropped rule). The vectors must fail for every
one. A mutant that survives means a rule is not pinned down by any vector, so a client could get it wrong and still "pass".
    python3 conformance/python/mutants.py        exit 0 = every mutant was caught
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TARGET = os.path.join(HERE, "disc_menus.py")

MUTANTS = [
    ("wrap tie band 20 -> 2", ["wrap_key[0] - 20", "wrap_key[0] + 20"], ["wrap_key[0] - 2", "wrap_key[0] + 2"]),
    ("wrap ignores straightness", [" and across < wrap_key[1]"], [""]),
    ("wrap prefers nearest, not farthest", ["far = -behind ", "far = behind "], None),
    ("focus tolerance 0.25 -> 0", ["tol = 0.25 *"], ["tol = 0 *"]),
    ("focus tolerance 0.25 -> 0.5", ["tol = 0.25 *"], ["tol = 0.5 *"]),
    ("same-row buttons no longer rank first", ["key = (0, ahead) if _overlaps_across(frm, r, dx) else (1, ahead + 2 * across)", ], ["key = (1, ahead + 2 * across)"]),
    ("sideways drift weighted 1, not 2", ["ahead + 2 * across"], ["ahead + 1 * across"]),
    ("the first of equal candidates no longer wins", ["if best_key is None or key < best_key:"], ["if best_key is None or key <= best_key:"]),
    ("a cell is not kept for More", ["cap = min(max(1, room - 1), max_per)"], ["cap = min(max(1, room), max_per)"]),
    ("every back/home entry is pinned, not just the first", ["backs = [e for e in entries if e is first_back or e is first_home]"], ['backs = [e for e in entries if e["Action"] in ("back", "home")]']),
    ("MaxPerPage ignored", ["max_per = max_per_page if max_per_page and max_per_page > 0 else math.inf"], ["max_per = math.inf"]),
    ("Previous does not take a cell", ["room = slots - (len(backs) + (1 if prev else 0))"], ["room = slots - len(backs)"]),
    ("nav buttons not in the last cells", ["slots - len(nav) + j"], ["len(p[\"items\"]) + j"]),
    ("tall cells get no height", ["ch * 0.94 if tall else None"], ["None"]),
    ("cell width 0.94 -> 1", ['"W": cw * 0.94'], ['"W": cw']),
    ("region anchor ignored", ['sx, sy = ANCHOR_SHIFT.get(r.get("Anchor") or "top-left", ANCHOR_SHIFT["top-left"])'], ["sx, sy = 0, 0"]),
    ("a null layout value overrides", ["if v is not None:"], ["if True:"]),
    ("menu layout replaced, not merged", ["for src in (doc.get(\"Layout\"), menu.get(\"Layout\")):"], ["for src in (menu.get(\"Layout\") or doc.get(\"Layout\"),):"]),
    ("custom Previous label ignored", ['flow.get("PreviousLabel") or "Previous"'], ['"Previous"']),
    ("custom More label ignored", ['flow.get("MoreLabel") or "More"'], ['"More"']),
    ("hours not shown", ['(f"{h}:{m:02d}" if h > 0 else f"{m}")'], ['f"{h * 60 + m}"']),
    ("seconds not padded", ['f":{s:02d}"'], ['f":{s}"']),
    ("tmdb path allows gif", ["(?:jpg|png)"], ["(?:jpg|png|gif)"]),
    ("colour allows #rgb", ["#[0-9a-fA-F]{6}"], ["#[0-9a-fA-F]{3,6}"]),
    ("image allows svg data", ["(?:png|jpeg|webp)"], ["(?:png|jpeg|webp|svg\\+xml)"]),
    ("image allows http", ["https://[^"], ["https?://[^"]),
    ("asset segments may be 65 long", ["{0,63}"], ["{0,64}"]),
    ("fanart id allows 13 digits", ["[0-9]{1,12}"], ["[0-9]{1,13}"]),
    ("fanart id may be empty", ["[0-9]{1,12}"], ["[0-9]{0,12}"]),
    ("fanart id allows letters", ["^[0-9]{1,12}"], ["^[0-9a-f]{1,12}"]),
    ("item id allows a path", ["[0-9a-fA-F-]{32,36}"], ["[0-9a-fA-F/.-]{32,40}"]),
    ("asset depth 4 -> 5", ["{{0,3}})"], ["{{0,4}})"]),
    ("audio allows any asset extension", ["\\.(?:mp3|ogg|opus|m4a|wav)"], ["(?:\\.[a-z0-9]+)?"]),
]


def run_vectors():
    p = subprocess.run([sys.executable, os.path.join(HERE, "run_vectors.py")], capture_output=True, text=True)
    return p.returncode, p.stdout.strip().splitlines()[-1] if p.stdout.strip() else p.stderr.strip()


def main():
    with open(TARGET, encoding="utf-8") as f:
        original = f.read()
    code, line = run_vectors()
    if code != 0:
        print("the unmodified implementation fails the vectors:", line)
        return 1
    survived, ineffective = [], []
    try:
        for name, old, new in MUTANTS:
            if new is None:                       # a mutation given as a single substring swap of old[0] -> old[1]
                old, new = [old[0]], [old[1]]
            mutated = original
            for a, b in zip(old, new):
                if a not in mutated:
                    ineffective.append(name)
                    break
                mutated = mutated.replace(a, b)
            else:
                with open(TARGET, "w", encoding="utf-8") as f:
                    f.write(mutated)
                code, line = run_vectors()
                print(("caught  " if code != 0 else "SURVIVED"), name, "-", line)
                if code == 0:
                    survived.append(name)
    finally:
        with open(TARGET, "w", encoding="utf-8") as f:
            f.write(original)
    for name in ineffective:
        print("BROKEN MUTANT (its text is no longer in disc_menus.py):", name)
    print(f"{len(MUTANTS) - len(survived) - len(ineffective)} of {len(MUTANTS)} mutants caught")
    return 1 if survived or ineffective else 0


if __name__ == "__main__":
    sys.exit(main())
