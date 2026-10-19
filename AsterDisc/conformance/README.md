# Conformance suite

Language-neutral test vectors for the parts of the Disc Menus renderer that need no screen, for people building a client. They are
**generated from the reference renderer** ([`Web/discmenus.js`](../Jellyfin.Plugin.DiscMenus/Web/discmenus.js)): each case is an input and the
exact output the reference produces. Run your implementation over the inputs and compare. The rules themselves are explained in the
[Client implementer's guide](../docs/CLIENT_GUIDE.md).

Everything here is plain JSON. Each file has `conformanceVersion`, a `description` (read it: it states the conventions) and a list of `cases`, one per
line. Compare numbers within **1e-6**.

| File | Cases | What each case is |
|---|---|---|
| [`paginate.json`](paginate.json) | 318 | entries and a grid size → the pages: content labels, pinned buttons, whether Previous / More appear |
| [`cells.json`](cells.json) | 163 | a `Layout.Flow`, a cell number, tall or not → the cell's `Position` |
| [`layout.json`](layout.json) | 27 | a screen of a sample document (see [`documents/`](documents)), a page → the merged layout and every entry's final `Position`, in drawing order |
| [`focus.json`](focus.json) | 62 | button rectangles and a focused one → which button each direction (right, left, down, up) moves focus to |
| [`time-format.json`](time-format.json) | 14 | Jellyfin ticks → the chapter time text |
| [`safety.json`](safety.json) | 90 | a string from untrusted menu data, and what it is meant to be (image, audio, colour, TMDB path, fanart id, item id) → accepted or not |

[`documents/`](documents) contains resolved documents: the golden outputs of the server's builder for the example menus, plus `layout-stress.renderable.json`, built to
exercise inheritance, paging with pinned buttons, `MaxPerPage` and artwork cells.

## Using it

Read the files, run your code over the inputs, compare. A tiny runner is all you need; the Python one in [`python/`](python) is about 60 lines
([`run_vectors.py`](python/run_vectors.py)) over an implementation of the rules ([`disc_menus.py`](python/disc_menus.py)) that also serves as a readable model to port from.

```bash
python3 conformance/python/run_vectors.py     # the second implementation against every vector
python3 conformance/python/mutants.py         # proves the vectors catch small mistakes (every mutant must be caught)
node conformance/generate.js --check          # the vectors still match the reference renderer (CI runs this)
```

## What this does not cover

Pictures, animation and sound (the guide describes them, but only by eye and ear can you compare), playback, and platform input handling. Text-sized buttons also depend
on font metrics; the focus vectors use fixed rectangles.

## When the reference changes

The vectors are regenerated **on purpose** (`node conformance/generate.js`), reviewed like any change, and `conformanceVersion` is raised if an old client would now
fail. CI fails if the files differ from what the reference produces, so they can't drift silently. If you find a case where the reference does something that looks wrong,
open an issue before relying on it: the vectors describe what it *does*, and a bug there becomes part of the contract unless it's fixed.
