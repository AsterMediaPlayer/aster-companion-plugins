import json, sys, copy, os
# The schemas, semantic() and check() live in menucheck.py so any file can be checked; this script keeps the
# project's own self-tests (the examples and the must-be-rejected cases).
from menucheck import ROOT, load, ms, bs, mv, bv, semantic, check

m, b = load(os.path.join(ROOT,"examples","example.menu.json")), load(os.path.join(ROOT,"examples","example.binding.json"))
def mut(f):
    d = copy.deepcopy(m); f(d); return d
neg = {
  "label with HTML": mut(lambda d: d["menus"]["main"]["entries"][0].update(label="<img onerror=x>")),
  "filename sneaks into extra": mut(lambda d: d["extras"]["trailer"].update(file="t.mkv")),
  "no provider IDs": mut(lambda d: d["match"].update(providerIds={})),
  "tmdb bg without path": mut(lambda d: d.update(background={"source":"tmdb"})),
  "playExtra missing key": mut(lambda d: d["menus"]["main"]["entries"].append({"action":"playExtra","label":"x"})),
  "wrong field for action": mut(lambda d: d["menus"]["main"]["entries"].append({"action":"back","label":"x","menu":"main"})),
  "dangling extra ref": mut(lambda d: d["menus"]["features"]["entries"][0].update(extra="nope")),
  "orphan menu": mut(lambda d: d["menus"].update(orphan={"title":"O","entries":[{"action":"back","label":"Back"}]})),
  "season w/o number": mut(lambda d: d["match"].update(itemType="Season")),
  "position out of range": mut(lambda d: d["menus"]["main"]["entries"][0].update(position={"x":120,"y":10})),
  "position missing y": mut(lambda d: d["menus"]["main"]["entries"][0].update(position={"x":10})),
  "bad anchor": mut(lambda d: d["menus"]["main"]["entries"][0].update(position={"x":1,"y":1,"anchor":"middle"})),
  "unknown button style": mut(lambda d: d["menus"]["main"]["entries"][0].update(style="neon")),
  "image is a file path": mut(lambda d: d["menus"]["main"]["entries"][0].update(image="/mnt/user/art/play.png")),
  "image is http not https": mut(lambda d: d["menus"]["main"]["entries"][0].update(image="http://example.com/a.png")),
  "image url with quote/paren": mut(lambda d: d["menus"]["main"]["entries"][0].update(image='https://example.com/a.png");x:url(')),
  "svg data uri": mut(lambda d: d["menus"]["main"]["entries"][0].update(image="data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=")),
  "mixed positioned/unpositioned": mut(lambda d: d["menus"]["features"]["entries"][0].update(position={"x":10,"y":10})),
  "unknown font": mut(lambda d: d["theme"].update(font="Comic Sans MS")),
  "font stack injection": mut(lambda d: d["theme"].update(font="x;background:url(//evil)")),
  "bad text colour": mut(lambda d: d["theme"].update(textColor="red")),
  "fontSize too big": mut(lambda d: d["theme"].update(fontSize=50)),
  "unknown layer type": mut(lambda d: d["menus"]["main"].update(layout={"layers":[{"type":"script","position":{"x":0,"y":0}}]})),
  "layer without position": mut(lambda d: d["menus"]["main"].update(layout={"layers":[{"type":"panel","fill":"#000000"}]})),
  "panel with image field": mut(lambda d: d["menus"]["main"].update(layout={"layers":[{"type":"panel","position":{"x":0,"y":0},"image":"https://e.com/a.png"}]})),
  "image layer w/ path": mut(lambda d: d["menus"]["main"].update(layout={"layers":[{"type":"image","position":{"x":0,"y":0},"image":"/etc/passwd"}]})),
  "image bg without image": mut(lambda d: d.update(background={"source":"image"})),
  "asset path traversal": mut(lambda d: d["menus"]["main"]["entries"][0].update(image="asset:../../etc/passwd")),
  "asset absolute path": mut(lambda d: d["menus"]["main"]["entries"][0].update(image="asset:/etc/passwd")),
  "asset hidden file": mut(lambda d: d["menus"]["main"]["entries"][0].update(image="asset:.secret/x.png")),
  "asset too deep": mut(lambda d: d["menus"]["main"]["entries"][0].update(image="asset:a/b/c/d/e/f.png")),
  "layer unknown fit": mut(lambda d: d["menus"]["main"].update(layout={"layers":[{"type":"image","position":{"x":0,"y":0},"image":"asset:a/b.webp","fit":"stretchy"}]})),
  "flow with positioned entries": mut(lambda d: (d["menus"]["features"].update(layout={"flow":{"region":{"x":50,"y":50,"w":50,"h":10},"columns":3,"rows":1}}), d["menus"]["features"]["entries"][0].update(position={"x":1,"y":1}))),
  "flow too small": mut(lambda d: d["menus"]["features"].update(layout={"flow":{"region":{"x":50,"y":50,"w":50,"h":10},"columns":3,"rows":1}})),
  "flow region without size": mut(lambda d: d["menus"]["features"].update(layout={"flow":{"region":{"x":50,"y":50},"columns":4,"rows":1}})),
  "flow zero columns": mut(lambda d: d["menus"]["features"].update(layout={"flow":{"region":{"x":50,"y":50,"w":50,"h":10},"columns":0,"rows":5}})),
  "flow html label": mut(lambda d: d["menus"]["features"].update(layout={"flow":{"region":{"x":50,"y":50,"w":50,"h":10},"columns":4,"rows":2,"moreLabel":"<b>More"}})),
  "flow unknown field": mut(lambda d: d["menus"]["features"].update(layout={"flow":{"region":{"x":50,"y":50,"w":50,"h":10},"columns":4,"rows":2,"perPage":3}})),
  "trailerIndex negative": mut(lambda d: d.update(background={"source":"trailer","trailerIndex":-1})),
  "trailerIndex too big": mut(lambda d: d.update(background={"source":"trailer","trailerIndex":99})),
  "poster is a file path": mut(lambda d: d.update(background={"source":"trailer","poster":"/mnt/art/p.png"})),
  "muted not boolean": mut(lambda d: d.update(background={"source":"trailer","muted":"yes"})),
  "home with a menu target": mut(lambda d: d["menus"]["features"]["entries"].append({"action":"home","label":"x","menu":"main"})),
  "back+home need 5 cells": mut(lambda d: (d["menus"]["features"].update(layout={"flow":{"region":{"x":50,"y":50,"w":50,"h":10},"columns":4,"rows":1}}), d["menus"]["features"]["entries"].append({"action":"home","label":"Home"}))),
  "chapters styling menu missing": mut(lambda d: d["menus"]["main"]["entries"][1].update(menu="nope")),
  "chapters menu not a key": mut(lambda d: d["menus"]["main"]["entries"][1].update(menu="Bad Key!")),
  "chapters perPage zero": mut(lambda d: d["menus"]["main"]["entries"][1].update(perPage=0)),
  "music file source without file": mut(lambda d: d.update(audio={"music":{"source":"file"}})),
  "music file is a path": mut(lambda d: d.update(audio={"music":{"source":"file","file":"/mnt/music/a.mp3"}})),
  "music file wrong extension": mut(lambda d: d.update(audio={"music":{"source":"file","file":"asset:x/a.exe"}})),
  "music file traversal": mut(lambda d: d.update(audio={"music":{"source":"file","file":"asset:../a.mp3"}})),
  "music http not https": mut(lambda d: d.update(audio={"music":{"source":"file","file":"http://e.com/a.mp3"}})),
  "music unknown source": mut(lambda d: d.update(audio={"music":{"source":"spotify"}})),
  "music volume too loud": mut(lambda d: d.update(audio={"music":{"source":"themeSong","volume":2}})),
  "sounds unknown preset": mut(lambda d: d.update(audio={"sounds":{"preset":"laser"}})),
  "sound file wrong type": mut(lambda d: d.update(audio={"sounds":{"move":"asset:x/click.png"}})),
  "sounds unknown field": mut(lambda d: d.update(audio={"sounds":{"hover":"asset:x/a.mp3"}})),
  "transition unknown style": mut(lambda d: d["menus"]["main"].update(layout={"transition":{"style":"explode"}})),
  "transition too long": mut(lambda d: d["menus"]["main"].update(layout={"transition":{"style":"fade","durationMs":9000}})),
  "transition without style": mut(lambda d: d["menus"]["main"].update(layout={"transition":{"durationMs":200}})),
  "tmdb path is svg": mut(lambda d: d.update(background={"source":"tmdb","tmdbFilePath":"/abc.svg"})),
  "tmdb path traversal": mut(lambda d: d.update(background={"source":"tmdb","tmdbFilePath":"/../etc/passwd.jpg"})),
  "tmdb path is a url": mut(lambda d: d.update(background={"source":"tmdb","tmdbFilePath":"https://evil.example/a.jpg"})),
  "tmdb size unknown": mut(lambda d: d.update(background={"source":"tmdb","tmdbFilePath":"/abc.jpg","tmdbSize":"w99999"})),
  "fanart without id": mut(lambda d: d.update(background={"source":"fanart"})),
  "fanart id has letters": mut(lambda d: d.update(background={"source":"fanart","fanartId":"12ab"})),
  "fanart id is empty": mut(lambda d: d.update(background={"source":"fanart","fanartId":""})),
  "fanart id is too long": mut(lambda d: d.update(background={"source":"fanart","fanartId":"1234567890123"})),
  "fanart id is a path": mut(lambda d: d.update(background={"source":"fanart","fanartId":"../1"})),
  "fanart id is a url": mut(lambda d: d.update(background={"source":"fanart","fanartId":"https://assets.fanart.tv/a.jpg"})),
  "fanart id is a number, not text": mut(lambda d: d.update(background={"source":"fanart","fanartId":47835})),
  "fanart on a page without id": mut(lambda d: d["menus"]["main"].update(background={"source":"fanart","dim":0.3})),
  "bad uuid": mut(lambda d: d.update(menuId="not-a-uuid")),
}
ok = True
for k, v in {"example menu": check(mv, m, True), "example binding": check(bv, b)}.items():
    print(("PASS " if not v else "FAIL ") + k, v or ""); ok &= not v
for k, d in neg.items():
    e = check(mv, d, True); print(("PASS " if e else "FAIL ") + "rejects " + k); ok &= bool(e)
# Every shipped example menu must pass the schema AND the semantic rules, and every
# asset: image it references must actually be bundled (except art we can't redistribute).
import glob, re
ALLOW_MISSING_ASSETS = {"thor-ragnarok.menu.json"}
ids = {}
def asset_refs(node):
    if isinstance(node, dict):
        for v in node.values(): yield from asset_refs(v)
    elif isinstance(node, list):
        for v in node: yield from asset_refs(v)
    elif isinstance(node, str) and node.startswith("asset:"):
        yield node[6:]
for path in sorted(glob.glob(os.path.join(ROOT, "examples", "*.menu.json"))):
    name = os.path.basename(path); doc = load(path)
    errs = check(mv, doc, True)
    missing = [] if name in ALLOW_MISSING_ASSETS else [a for a in asset_refs(doc) if not os.path.isfile(os.path.join(ROOT, "examples", "assets", a))]
    if missing: errs += [f"asset not bundled: {a}" for a in missing]
    if doc["menuId"] in ids: errs.append(f"menuId duplicates {ids[doc['menuId']]}")
    ids[doc["menuId"]] = name
    print(("PASS " if not errs else "FAIL ") + "example " + name, errs or ""); ok &= not errs
bneg = copy.deepcopy(b); bneg["bindings"]["trailer"] = {"status":"matched"}
e = check(bv, bneg); print(("PASS " if e else "FAIL ") + "rejects matched binding w/o itemId"); ok &= bool(e)
sys.exit(0 if ok else 1)
