// The outline module: the parts list built from menu text, problems attached to parts, and finding the text of a part.
const fs = require('fs'); const path = require('path');
const O = require('../../Jellyfin.Plugin.DiscMenus/Web/editor/outline.js');
let fail = 0; const check = (ok, m, e) => { if (!ok) fail++; console.log(ok ? 'PASS' : 'FAIL', m, e === undefined ? '' : e); };
const ROOT = path.resolve(__dirname, '..', '..');
const TEXT = `{
  "root": "main",
  "layout": { "layers": [ { "type": "panel", "position": {"x":0,"y":0} } ] },
  "extras": { "trailer": { "type": "Trailer", "durationSec": 90.4 } },
  "menus": {
    "main": { "title": "Main Menu", "entries": [ { "action": "playFeature", "label": "Play" }, { "action": "submenu", "label": "More", "menu": "more" } ] },
    "more": { "title": "More", "layout": { "layers": [ {"type":"image"} ] }, "entries": [ { "action": "back", "label": "Back" } ] }
  }
}`;

console.log('--- rows');
const rows = O.build(TEXT, []);
const labels = rows.map(r => '  '.repeat(r.depth) + r.label);
const docRow = rows.find(r => r.kind === 'document');
check(rows[0].label === 'Pages (2)' && docRow === rows[rows.length - 1 - rows.filter(r => r.kind === 'layer' && r.path[0] === 'layout').length] && docRow.path.length === 0, 'pages come first and the settings for the whole menu last', rows.map(r => r.label).join('|'));
check(labels.includes('  Main Menu (first) [main]') && labels.includes('    Play - Play feature') && labels.includes('    More - Open menu'), 'menus show their title and which is first; entries show label and action', labels.join(' | '));
check(labels.some(l => /^ {2}trailer - Trailer, 90s$/.test(l)), 'extras show type and rounded duration');
check(rows.filter(r => r.kind === 'layer').length === 2 && rows.some(r => r.kind === 'layer' && r.path.join('/') === 'menus/more/layout/layers/0'), 'layers of the document and of a menu are listed');
check(rows.every(r => r.errors === 0 && !r.hasErrors), 'no problems, none marked');
check(O.build('{ nope', []) === null && O.build('[1]', []) === null && O.build('', []) === null, 'text that is not a menu object gives no outline');

console.log('--- problems');
const withErr = O.build(TEXT, [{ Message: 'x', Path: '/menus/main/entries/1/menu' }, { Message: 'y', Path: '/menus/main/entries/1/label' }, { Message: 'z', Path: '/root' }, { Message: 'bad pointer', Path: 'nonsense' }, { Message: 'none' }, null]);
const find = p => withErr.find(r => r.path.join('/') === p);
check(find('menus/main/entries/1').errors === 2 && find('menus/main/entries/1').hasErrors, 'problems inside a button are counted on that button');
check(find('menus/main').hasErrors && find('menus/main').errors === 0 && find('menus').hasErrors && withErr[0].hasErrors, 'and every part above it is marked as containing one');
check(!find('menus/more').hasErrors && !find('menus/main/entries/0').hasErrors, 'other parts are not marked');
check(withErr.find(r => r.kind === 'document').errors === 1, 'a problem with no row of its own (the root key) counts on the whole menu');
const gone = O.build(TEXT, [{ Message: 'm', Path: '/menus/more/entries/5/label' }]);
check(gone.find(r => r.path.join('/') === 'menus/more').errors === 1, 'a problem about something that is not there counts on its nearest part');

console.log('--- pointers and finding text');
const doc = JSON.parse(TEXT);
check(JSON.stringify(O.pointerToPath('/menus/main/entries/1/menu', doc)) === '["menus","main","entries",1,"menu"]', 'array positions become numbers');
check(JSON.stringify(O.pointerToPath('', doc)) === '[]' && O.pointerToPath('x', doc) === null && O.pointerToPath(null, doc) === null, 'the whole-document pointer is empty; junk is refused');
check(JSON.stringify(O.pointerToPath('/menus/a~1b/entries/0', { menus: { 'a/b': { entries: [1] } } })) === '["menus","a/b","entries",0]', 'escapes are undone');
check(JSON.stringify(O.pointerToPath('/menus/7', { menus: { 7: 1 } })) === '["menus","7"]', 'a number-like key in an object stays a string');
let hit = O.resolve(TEXT, ['menus', 'main', 'entries', 1, 'label']);
check(hit.exact && TEXT.slice(hit.start, hit.end) === '"label": "More"', 'a property is found with its key', TEXT.slice(hit.start, hit.end));
hit = O.resolve(TEXT, ['menus', 'main', 'entries', 1]);
check(TEXT.slice(hit.start, hit.end).startsWith('{ "action": "submenu"') && TEXT.slice(hit.start, hit.end).endsWith('}'), 'an array item is found');
hit = O.resolve(TEXT, ['menus', 'more', 'entries', 0, 'style']);
check(!hit.exact && hit.path.join('/') === 'menus/more/entries/0', 'a missing property falls back to the part that holds it');
hit = O.resolve(TEXT, ['nothing', 'here']);
check(!hit.exact && hit.path.length === 0 && hit.start === 0, 'a missing section falls back to the whole menu');
check(O.resolve('{ nope', ['a']) === null && O.resolve(TEXT, 'x') === null, 'bad text or path gives nothing, not a crash');

console.log('--- hostile and real documents');
const hostile = '{"root":1,"extras":{"a":null,"b":[1],"c":"s"},"menus":{"n":null,"s":"str","a":[],"m":{"title":{"x":1},"entries":[null,1,"x",[],{"action":{}, "label":5},{"label":"<img>"}],"layout":{"layers":[null,5]}}}}';
let rows2; try { rows2 = O.build(hostile, [{ Path: '/menus/m/entries/4' }]); } catch (e) { rows2 = e; }
check(Array.isArray(rows2) && rows2.length > 5, 'nulls, wrong types and odd values never throw', rows2 && rows2.message);
check(Array.isArray(rows2) && rows2.every(r => typeof r.label === 'string'), 'every label is text');
const long = JSON.stringify({ menus: { m: { title: 'T'.repeat(500), entries: [{ action: 'x'.repeat(500), label: 'L'.repeat(500) }] } } });
check(O.build(long, []).every(r => r.label.length <= 80), 'very long labels are clipped');
const dir = path.join(ROOT, 'examples'); let bad = 0, n = 0;
for (const f of fs.readdirSync(dir).filter(f => f.endsWith('.menu.json'))) {
    const t = fs.readFileSync(path.join(dir, f), 'utf8'); const d = JSON.parse(t); const r = O.build(t, []); n++;
    const entries = Object.values(d.menus).reduce((a, m) => a + m.entries.length, 0);
    if (!r || r.filter(x => x.kind === 'entry').length !== entries || r.filter(x => x.kind === 'menu').length !== Object.keys(d.menus).length) bad++;
    for (const row of r) { const h = O.resolve(t, row.path); if (!h || !h.exact) bad++; }
}
check(bad === 0 && n > 3, 'every example menu: every row finds its own text exactly (' + n + ' files)', bad);

console.log('failures: ' + fail); process.exit(fail ? 1 : 0);
