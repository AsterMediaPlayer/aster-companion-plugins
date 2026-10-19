"""A second implementation of the menu rules that need no screen, written from docs/CLIENT_GUIDE.md and not from the reference
renderer's code. Its job is to prove that the guide plus the conformance vectors are enough to build a client: if this passes every
vector, the rules in the guide are complete and correct. It is also a readable model for porting to another language.

Pure Python 3, standard library only.
"""
import math
import re

ANCHOR_SHIFT = {
    "top-left": (0, 0), "top": (-50, 0), "top-right": (-100, 0),
    "left": (0, -50), "center": (-50, -50), "right": (-100, -50),
    "bottom-left": (0, -100), "bottom": (-50, -100), "bottom-right": (-100, -100),
}


# ---- paging --------------------------------------------------------------------------------------------------------------
def paginate(entries, slots, max_per_page=0):
    """Pages of a Layout.Flow grid. The first back entry and the first home entry are pinned to every page (in entry order);
    every other entry is content. A page holds `slots` cells: content, then the pinned buttons, Previous (not on page one) and
    More (when more follows) occupy the LAST cells."""
    max_per = max_per_page if max_per_page and max_per_page > 0 else math.inf
    first_back = next((e for e in entries if e["Action"] == "back"), None)
    first_home = next((e for e in entries if e["Action"] == "home"), None)
    backs = [e for e in entries if e is first_back or e is first_home]
    content = [e for e in entries if e is not first_back and e is not first_home]
    pages, taken = [], 0
    while True:
        prev = len(pages) > 0
        room = slots - (len(backs) + (1 if prev else 0))
        left = len(content) - taken
        if left <= min(room, max_per):
            pages.append({"items": content[taken:], "backs": backs, "prev": prev, "more": False})
            return pages
        cap = min(max(1, room - 1), max_per)           # one cell is kept for More
        pages.append({"items": content[taken:taken + cap], "backs": backs, "prev": prev, "more": True})
        taken += cap


def cell_position(flow, slot, tall):
    r = flow["Region"]
    sx, sy = ANCHOR_SHIFT.get(r.get("Anchor") or "top-left", ANCHOR_SHIFT["top-left"])
    left = r["X"] + sx / 100 * r["W"]
    top = r["Y"] + sy / 100 * r["H"]
    cw, ch = r["W"] / flow["Columns"], r["H"] / flow["Rows"]
    return {
        "X": left + (slot % flow["Columns"] + 0.5) * cw,
        "Y": top + (slot // flow["Columns"] + 0.5) * ch,
        "W": cw * 0.94,
        "H": ch * 0.94 if tall else None,
        "Anchor": "center",
    }


def effective_layout(doc, menu):
    """Document layout, then the menu's, key by key; a null value is skipped (it does not remove an inherited one)."""
    merged = {}
    for src in (doc.get("Layout"), menu.get("Layout")):
        for k, v in (src or {}).items():
            if v is not None:
                merged[k] = v
    return merged


def entries_for_page(doc, menu, page):
    """(entries to draw, number of pages, page shown). Entries carry Action, Label and Position (None = default column)."""
    layout = effective_layout(doc, menu)
    flow = layout.get("Flow")
    if not flow:
        return [{"Action": e["Action"], "Label": e["Label"], "Position": e.get("Position")} for e in menu["Entries"]], 1, 0
    slots = flow["Columns"] * flow["Rows"]
    pages = paginate(menu["Entries"], slots, menu.get("MaxPerPage", 0))
    page = min(page, len(pages) - 1)
    p = pages[page]
    nav = list(p["backs"])
    if p["prev"]:
        nav.append({"Action": "pagePrev", "Label": flow.get("PreviousLabel") or "Previous"})
    if p["more"]:
        nav.append({"Action": "pageNext", "Label": flow.get("MoreLabel") or "More"})
    placed = []
    for i, e in enumerate(p["items"]):
        placed.append({"Action": e["Action"], "Label": e["Label"], "Position": cell_position(flow, i, bool(e.get("Image") or e.get("ThumbUrl")))})
    for j, e in enumerate(nav):
        placed.append({"Action": e["Action"], "Label": e["Label"], "Position": cell_position(flow, slots - len(nav) + j, bool(e.get("Image")))})
    return placed, len(pages), page


# ---- focus ---------------------------------------------------------------------------------------------------------------
def _rect(r):
    return {"left": r["left"], "top": r["top"], "width": r["width"], "height": r["height"],
            "right": r["left"] + r["width"], "bottom": r["top"] + r["height"]}


def _distance_along(frm, r, dx, dy):
    """How far r lies in direction (dx, dy) from frm, judged by edges; None if it isn't in that direction."""
    tol = 0.25 * (frm["width"] if dx != 0 else frm["height"])
    if dx == 1:
        gap = r["left"] - frm["right"]
    elif dx == -1:
        gap = frm["left"] - r["right"]
    elif dy == 1:
        gap = r["top"] - frm["bottom"]
    else:
        gap = frm["top"] - r["bottom"]
    if gap < -tol:
        return None
    cx = r["left"] + r["width"] / 2 - (frm["left"] + frm["width"] / 2)
    cy = r["top"] + r["height"] / 2 - (frm["top"] + frm["height"] / 2)
    return cx * dx + cy * dy


def _overlaps_across(frm, r, dx):
    if dx != 0:
        return frm["top"] < r["bottom"] and r["top"] < frm["bottom"]
    return frm["left"] < r["right"] and r["left"] < frm["right"]


def _across(frm, r, dx):
    cx = r["left"] + r["width"] / 2 - (frm["left"] + frm["width"] / 2)
    cy = r["top"] + r["height"] / 2 - (frm["top"] + frm["height"] / 2)
    return abs(cy if dx != 0 else cx)


def move_focus(rects, current, dx, dy):
    """Index of the button that takes focus when direction (dx, dy) is pressed on `current`; `current` if nothing moves."""
    rs = [_rect(r) for r in rects]
    frm = rs[current]
    best, best_key, wrap, wrap_key = None, None, None, None
    for i, r in enumerate(rs):
        if i == current:
            continue
        ahead = _distance_along(frm, r, dx, dy)
        across = _across(frm, r, dx)
        if ahead is not None:
            key = (0, ahead) if _overlaps_across(frm, r, dx) else (1, ahead + 2 * across)
            if best_key is None or key < best_key:      # the first of equals wins
                best, best_key = i, key
            continue
        behind = _distance_along(frm, r, -dx, -dy)      # only buttons really behind are wrap candidates
        if behind is None:
            continue
        far = -behind                                   # more negative = farther behind
        if wrap_key is None or far < wrap_key[0] - 20 or (far < wrap_key[0] + 20 and across < wrap_key[1]):
            wrap, wrap_key = i, (far, across)
    target = best if best is not None else wrap
    return current if target is None else target


# ---- small rules -----------------------------------------------------------------------------------------------------------
def format_ticks(ticks):
    total = int((ticks or 0) // 10_000_000)
    h, m, s = total // 3600, (total % 3600) // 60, total % 60
    return (f"{h}:{m:02d}" if h > 0 else f"{m}") + f":{s:02d}"


_SEG = r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}"
_HTTPS = r"https://[^\s\"'()<>\\]+"
SAFE_IMAGE = re.compile(rf"^(?:{_HTTPS}|data:image/(?:png|jpeg|webp);base64,[A-Za-z0-9+/=]+|asset:{_SEG}(?:/{_SEG}){{0,3}})\Z")
SAFE_AUDIO = re.compile(rf"^(?:{_HTTPS}|asset:{_SEG}(?:/{_SEG}){{0,3}}\.(?:mp3|ogg|opus|m4a|wav))\Z")
HEX_COLOUR = re.compile(r"^#[0-9a-fA-F]{6}\Z")
TMDB_PATH = re.compile(r"^/[A-Za-z0-9_-]+\.(?:jpg|png)\Z")

FANART_ID = re.compile(r"^[0-9]{1,12}\Z", re.ASCII)
ITEM_ID = re.compile(r"^[0-9a-fA-F-]{32,36}\Z")
