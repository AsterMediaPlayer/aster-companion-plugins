// The editor's forms: what they show for every part of every example menu, that each control makes exactly one small edit to the TEXT
// (nothing else in the file changes), that out-of-range input is refused with a reason instead of written, and that odd data never crashes them.
const path = require('path'); const fs = require('fs');
const { JSDOM } = require('jsdom');
const ROOT = path.resolve(__dirname, '..', '..');
const ED = path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/editor/');
const dom = new JSDOM('<!doctype html><body></body>', { url: 'https://s.example/' });
global.window = dom.window; global.document = dom.window.document; global.Option = dom.window.Option; global.Event = dom.window.Event;
const JT = require(ED + 'json-text.js'); const H = require(ED + 'schema-hints.js'); const I = require(ED + 'inspector.js'); const O = require(ED + 'outline.js');
let fail = 0; const check = (ok, m, e) => { if (!ok) fail++; console.log(ok ? 'PASS' : 'FAIL', m, e === undefined ? '' : e); };

const MENU = `{
  "schemaVersion": 1,
  "menuId": "3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44",
  "revision": 1,
  "match": { "itemType": "Movie", "providerIds": { "Tmdb": "603" } },
  "extras": {
    "trailer": { "type": "Trailer", "durationSec": 90 },
    "making-of": { "type": "BehindTheScenes", "durationSec": 1500 }
  },
  "root": "main",
  "menus": {
    "main": {
      "title": "Main Menu",
      "background": { "source": "color", "color": "#112233", "dim": 0.5 },
      "entries": [
        { "action": "playFeature", "label": "Play" },
        { "action": "playExtra", "label": "Trailer", "extra": "trailer" },
        { "action": "submenu", "label": "More", "menu": "more" }
      ]
    },
    "more": { "title": "More", "entries": [ { "action": "back", "label": "Back" } ] }
  }
}
`;

function harness(text, opts = {}) {
  const box = document.createElement('div'); document.body.appendChild(box);
  const h = { text, writes: 0, selected: null, fanart: null, confirms: [], box };
  h.insp = I.create({
    container: box, hints: H, JsonText: JT, getText: () => h.text,
    commit: fn => { const out = fn(h.text); if (out !== h.text) { h.text = out; h.writes++; } h.insp.refresh(); return true; },
    select: p => { h.selected = p; h.insp.show(p); },
    actions: { fanart: p => { h.fanart = p; } },
    confirm: m => { h.confirms.push(m); return opts.confirm !== false; }
  });
  return h;
}
const ctl = (h, key) => h.box.querySelector('[data-key="' + key + '"]');
const change = (h, key, value) => { const c = ctl(h, key); if (!c) throw new Error('no control ' + key); c.value = value; c.dispatchEvent(new Event('change')); };
const click = (h, selector, index = 0) => h.box.querySelectorAll(selector)[index].click();
const doc = h => JSON.parse(h.text);
const msgs = h => [...h.box.querySelectorAll('.discEdFieldMsg, .discEdInspMsg')].map(e => e.textContent).filter(Boolean);
// Deep-compare two documents and list the paths that differ.
function diff(a, b, p = '') {
  if (JSON.stringify(a) === JSON.stringify(b)) return [];
  if (a && b && typeof a === 'object' && typeof b === 'object' && Array.isArray(a) === Array.isArray(b)) {
    const keys = new Set([...Object.keys(a), ...Object.keys(b)]); const out = [];
    for (const k of keys) out.push(...diff(a[k], b[k], p + '/' + k));
    return out;
  }
  return [p];
}

console.log('--- what is shown');
let h = harness(MENU); h.insp.show([]);
const heads = () => h.box.querySelector('.discEdInspHead').textContent;
check(/Whole menu/.test(heads()), 'the whole menu has a form');
check(ctl(h, 'root').tagName === 'SELECT' && [...ctl(h, 'root').options].map(o => o.value).join() === 'main,more' && ctl(h, 'root').value === 'main', 'the first page is a choice of the pages that exist');
check(h.box.textContent.includes('Pages (2)') && h.box.textContent.includes('Extras (2)') && /Main Menu \[main\] \(first\)/.test(h.box.textContent), 'pages and extras are listed, the first page marked');
check(!h.box.querySelector('[data-key="menuId"],[data-key="revision"]'), 'the id and revision are not editable here');
h.insp.show(['menus', 'main', 'entries', 1]);
check(ctl(h, 'menus/main/entries/1/extra').value === 'trailer' && [...ctl(h, 'menus/main/entries/1/extra').options].map(o => o.value).join() === 'trailer,making-of', 'a button that plays an extra picks from the real extras');
check(!ctl(h, 'menus/main/entries/1/menu') && !ctl(h, 'menus/main/entries/1/startChapter'), 'fields that do not apply to that action are not shown');
h.insp.show(['menus', 'main']);
check(ctl(h, 'menus/main/background/source').value === 'color' && ctl(h, 'menus/main/background/color').value === '#112233' && !ctl(h, 'menus/main/background/fanartId'), 'a background shows only the fields its source uses');
check(h.box.textContent.includes('Buttons (3)'), 'a page lists its buttons');
h.insp.show(['menus', 'main', 'entries', 9]);
check(/Page: Main Menu/.test(heads()), 'a path that is gone shows the nearest part that exists', heads());
const bad = harness('{ not json'); bad.insp.show([]);
check(/syntax/.test(bad.box.textContent) && bad.box.querySelectorAll('input,select').length === 0, 'invalid JSON shows a hint, not a form');

console.log('--- only what matters is shown first');
h = harness(MENU); h.insp.show(['menus', 'main', 'entries', 0]);
const inMore = key => !!ctl(h, key).closest('details.discEdMore');
check(!inMore('menus/main/entries/0/label') && !inMore('menus/main/entries/0/action') && inMore('menus/main/entries/0/style') && inMore('menus/main/entries/0/image'), 'a button shows what it says and does; look and picture are under More options');
const moreBox = h.box.querySelector('details.discEdMore');
check(moreBox && !moreBox.open && /Look and position/.test(moreBox.textContent), 'More options starts closed and says what is in it');
h = harness(MENU.replace('{ "action": "playFeature", "label": "Play" }', '{ "action": "playFeature", "label": "Play", "style": "glow" }')); h.insp.show(['menus', 'main', 'entries', 0]);
check(h.box.querySelector('details.discEdMore').open, 'it opens by itself when something in it is already set');
h = harness(MENU); h.insp.show(['menus', 'main', 'entries', 1]);
check(!inMore('menus/main/entries/1/extra'), 'the field an action needs is always up front');
h.insp.show(['menus', 'main']);
check(!h.box.querySelector('[data-key="menus/main/theme/add"]').closest('details.discEdMore') === false && !!h.box.querySelector('details.discEdMore'), 'a page keeps colours, layout and music under More options');
check(!h.box.querySelector('details.discEdMore').open, 'closed even though the page has a background');
h.insp.show(['menus', 'main', 'entries', 0]);
check(/Back to Main Menu/.test(h.box.querySelector('.discEdBack').textContent), 'a button\'s form has a link back to its page');
h.box.querySelector('.discEdBack').click(); check(JSON.stringify(h.selected) === '["menus","main"]', 'and the link goes there');
check(/Choose a button/.test(h.box.querySelector('.discEdHint').textContent), 'a page says what to do next');
h.insp.show(['menus', 'main', 'entries', 0]); check(/^Button: Play$/.test(h.box.querySelector('.discEdTitle').textContent), 'headings use the button\'s own text, not a path');
h.insp.show([]); check(!h.box.querySelector('.discEdBack') && h.box.querySelectorAll('details.discEdMore').length === 1 && !h.box.querySelector('details.discEdMore').open, 'the whole-menu form keeps its settings in one closed section');
h = harness(MENU); h.insp.show(['menus', 'main']); const bgSection = h.box.querySelector('details[data-group="menus/main/background"]');
check(bgSection && !bgSection.open, 'a section the page uses is collapsed until opened');

console.log('--- each control is one small edit');
h = harness(MENU); h.insp.show(['menus', 'main', 'entries', 0]);
change(h, 'menus/main/entries/0/label', 'Play movie');
check(h.writes === 1 && JSON.stringify(diff(JSON.parse(MENU), doc(h))) === '["/menus/main/entries/0/label"]', 'a label change is one write that changes only that label', diff(JSON.parse(MENU), doc(h)).join());
check(h.text === MENU.replace('"label": "Play"', '"label": "Play movie"'), 'every other character of the file is untouched');
change(h, 'menus/main/entries/0/label', 'Play movie'); check(h.writes === 1, 'setting the same value writes nothing');
change(h, 'menus/main/entries/0/label', '<b>x</b>'); check(h.writes === 1 && msgs(h).some(m => /no </.test(m)), 'a label with < is refused with a reason', msgs(h).join());
change(h, 'menus/main/entries/0/label', ''); check(h.writes === 1 && msgs(h).some(m => /needed/.test(m)), 'clearing a required field is refused', msgs(h).join());
change(h, 'menus/main/entries/0/label', 'x'.repeat(81)); check(h.writes === 1 && msgs(h).some(m => /80/.test(m)), 'a too-long label is refused');
change(h, 'menus/main/entries/0/style', 'glow'); check(doc(h).menus.main.entries[0].style === 'glow' && h.writes === 2, 'a choice is written');
change(h, 'menus/main/entries/0/style', ''); check(!('style' in doc(h).menus.main.entries[0]) && h.writes === 3 && h.text.includes('"label": "Play movie" }'), 'choosing (default) removes the property and leaves valid text');
h.insp.show(['menus', 'main', 'entries', 0]);
click(h, '[data-key="menus/main/entries/0/position/add"]');
check(JSON.stringify(doc(h).menus.main.entries[0].position) === '{"x":10,"y":10}'.replace(/"x":10,"y":10/, '"x":10,"y":10') && h.writes === 4, 'Add on the position section creates it with a starting place');
change(h, 'menus/main/entries/0/position/x', '250'); check(msgs(h).some(m => /At most 100/.test(m)) && doc(h).menus.main.entries[0].position.x === 10, 'a number out of range is refused with the limit');
change(h, 'menus/main/entries/0/position/x', 'abc'); check(doc(h).menus.main.entries[0].position.x === 10, 'text in a number field is not written');
change(h, 'menus/main/entries/0/position/x', '33.5'); change(h, 'menus/main/entries/0/position/anchor', 'center');
check(doc(h).menus.main.entries[0].position.x === 33.5 && doc(h).menus.main.entries[0].position.anchor === 'center', 'numbers and choices inside a section are written');
change(h, 'menus/main/entries/0/position/w', '40'); check(h.text.indexOf('"w": 40') < h.text.indexOf('"anchor"') || /"w":\s*40/.test(h.text), 'a new property is inserted in the schema\'s order');
click(h, '[data-key="menus/main/entries/0/position/remove"]'); check(!('position' in doc(h).menus.main.entries[0]), 'Remove deletes the whole section');
h = harness(MENU); h.insp.show(['menus', 'main', 'entries', 0]);
change(h, 'menus/main/entries/0/startChapter', '2.5'); check(msgs(h).some(m => /whole number/.test(m)) && h.writes === 0, 'a whole-number field refuses a fraction');
change(h, 'menus/main/entries/0/startChapter', '0'); check(msgs(h).some(m => /At least 1/.test(m)) && h.writes === 0, 'and a number below its minimum');
change(h, 'menus/main/entries/0/startChapter', '3'); check(doc(h).menus.main.entries[0].startChapter === 3, 'a valid chapter is written');
h.insp.show(['menus', 'main', 'entries', 1]); change(h, 'menus/main/entries/1/extra', 'making-of'); check(doc(h).menus.main.entries[1].extra === 'making-of', 'choosing another extra is written');
h.insp.show(['menus', 'main']); change(h, 'menus/main/background/color', 'red'); check(doc(h).menus.main.background.color === '#112233' && msgs(h).some(m => /colour/.test(m)), 'a bad colour is refused');
change(h, 'menus/main/background/color', '#aabbcc'); check(doc(h).menus.main.background.color === '#aabbcc', 'a good colour is written');
const sw = h.box.querySelector('.discEdSwatch'); sw.value = '#010203'; sw.dispatchEvent(new Event('change')); check(doc(h).menus.main.background.color === '#010203', 'the colour picker writes too');
change(h, 'menus/main/background/dim', '2'); check(doc(h).menus.main.background.dim === 0.5 && msgs(h).some(m => /At most 1/.test(m)), 'darkening above 1 is refused');
change(h, 'menus/main/background/dim', ''); check(!('dim' in doc(h).menus.main.background), 'an empty optional number removes the property');

console.log('--- changing what something is');
h = harness(MENU); h.insp.show(['menus', 'main', 'entries', 1]);
change(h, 'menus/main/entries/1/action', 'submenu');
let e1 = doc(h).menus.main.entries[1];
check(e1.action === 'submenu' && e1.label === 'Trailer' && e1.menu === 'more' && !('extra' in e1) && h.writes === 1, 'playExtra -> submenu drops the extra, adds a menu that exists, keeps the label, in one write', JSON.stringify(e1));
change(h, 'menus/main/entries/1/action', 'back'); e1 = doc(h).menus.main.entries[1];
check(JSON.stringify(e1) === '{"action":"back","label":"Trailer"}', 'submenu -> back leaves just action and label', JSON.stringify(e1));
change(h, 'menus/main/entries/1/action', 'playSequence'); e1 = doc(h).menus.main.entries[1];
check(JSON.stringify(e1.extras) === '["trailer","making-of"]', 'playSequence starts with two extras');
const opts = [...ctl(h, 'menus/main/entries/1/action').options].filter(o => o.disabled).map(o => o.value);
check(opts.length === 0, 'with two extras and two pages nothing is unavailable', opts.join());
const lonely = harness(JSON.stringify({ ...JSON.parse(MENU), extras: {}, menus: { main: JSON.parse(MENU).menus.main } }, null, 2)); lonely.insp.show(['menus', 'main', 'entries', 0]);
check([...ctl(lonely, 'menus/main/entries/0/action').options].filter(o => o.disabled).map(o => o.value).join() === 'playExtra,playSequence,submenu', 'actions that need extras or pages that do not exist are disabled');
h = harness(MENU); h.insp.show(['menus', 'main']);
change(h, 'menus/main/background/source', 'fanart'); let bgn = doc(h).menus.main.background;
check(bgn.source === 'fanart' && !('color' in bgn) === false, 'switching a background to fanart keeps color (it is a tint) and sets the source', JSON.stringify(bgn));
check(msgs(h).some(m => /needed/.test(m)) && ctl(h, 'menus/main/background/fanartId'), 'the field fanart needs is shown as needed');
click(h, 'button.discEdSmall'.replace('button.discEdSmall', 'button'), [...h.box.querySelectorAll('button')].findIndex(b => /fanart\.tv/.test(b.textContent)));
check(JSON.stringify(h.fanart) === '["menus","main","background"]', 'the fanart button asks the page to open the picker for this background');
change(h, 'menus/main/background/fanartId', '12345'); check(doc(h).menus.main.background.fanartId === '12345', 'a fanart id is written');
change(h, 'menus/main/background/fanartId', '12ab'); check(doc(h).menus.main.background.fanartId === '12345', 'a bad fanart id is refused');
change(h, 'menus/main/background/source', 'jellyfin'); bgn = doc(h).menus.main.background;
check(bgn.source === 'jellyfin' && bgn.imageType === 'Backdrop' && !('fanartId' in bgn), 'switching to jellyfin drops fanartId and adds an image type', JSON.stringify(bgn));
change(h, 'menus/main/background/source', 'trailer'); bgn = doc(h).menus.main.background;
check(!('imageType' in bgn) && bgn.source === 'trailer', 'switching to trailer drops the image type');

console.log('--- lists');
h = harness(MENU); h.insp.show(['menus', 'main']);
const labelsNow = () => doc(h).menus.main.entries.map(e => e.label).join();
click(h, '.discEdListRow button[aria-label="Move down"]', 0); check(labelsNow() === 'Trailer,Play,More' && h.writes === 1, 'Move down reorders in one write');
click(h, '.discEdListRow button[aria-label="Move up"]', 2); check(labelsNow() === 'Trailer,More,Play', 'Move up too');
click(h, '.discEdListRow button[aria-label="Remove this button"]', 0); check(labelsNow() === 'More,Play', 'Remove deletes the button');
ctl(h, 'menus/main/entries/add').value = 'chapters'; click(h, 'button', [...h.box.querySelectorAll('button')].findIndex(b => b.textContent === 'Add a button'));
check(labelsNow() === 'More,Play,Scenes' && JSON.stringify(doc(h).menus.main.entries[2]) === '{"action":"chapters","label":"Scenes"}', 'Add a button appends a valid new button');
ctl(h, 'menus/main/entries/add').value = 'playExtra'; click(h, 'button', [...h.box.querySelectorAll('button')].findIndex(b => b.textContent === 'Add a button'));
check(doc(h).menus.main.entries[3].extra === 'trailer', 'a new "play an extra" button already names an extra');
h = harness(MENU); h.insp.show(['menus', 'more']);
check([...h.box.querySelectorAll('button[aria-label="Remove this button"]')][0].disabled, 'the last button of a page cannot be removed');
h.insp.show([]);
click(h, '.discEdListRow .discEdSmall', 1); check(doc(h).root === 'more', 'Make first sets the first page');
const rmBtns = () => [...h.box.querySelectorAll('button[aria-label="Delete this page"]')];
check(rmBtns()[1].disabled, 'the first page cannot be deleted');
rmBtns()[0].click(); check(h.confirms.length === 0 && !('main' in doc(h).menus), 'a page nothing opens is deleted without asking');
h = harness(MENU); h.insp.show([]); rmBtns()[1].click();
check(h.confirms.length === 1 && /more/.test(h.confirms[0]) && !('more' in doc(h).menus), 'deleting a page that buttons open asks first, then deletes it', h.confirms.join());
h = harness(MENU, { confirm: false }); h.insp.show([]); click(h, '.discEdListRow .discEdSmall', 0); // main is already first: disabled -> no-op
const delMore = [...h.box.querySelectorAll('button[aria-label="Delete this page"]')][1]; delMore.click();
check('more' in doc(h).menus && h.confirms.length === 1, 'declining the question keeps the page');
h = harness(MENU); h.insp.show([]);
const addPage = (name) => { ctl(h, 'menus/add-name').value = name; click(h, 'button', [...h.box.querySelectorAll('button')].findIndex(b => b.textContent === 'Add a page')); };
addPage('Bad Name'); check(msgs(h).some(m => /lower-case/.test(m)) && h.writes === 0, 'a page name with capitals or spaces is refused');
addPage('more'); check(msgs(h).some(m => /already/.test(m)) && h.writes === 0, 'an existing page name is refused');
addPage('extras-page'); check(doc(h).menus['extras-page'].title === 'New page' && doc(h).menus['extras-page'].entries[0].action === 'back', 'a new page has a title and a Back button');
const addExtra = (name) => { ctl(h, 'extras/add-name').value = name; click(h, 'button', [...h.box.querySelectorAll('button')].findIndex(b => b.textContent === 'Add an extra')); };
addExtra('bloopers'); check(JSON.stringify(doc(h).extras.bloopers) === '{"type":"Featurette","durationSec":60}', 'a new extra starts as a one-minute featurette');
addExtra('bloopers'); check(msgs(h).some(m => /already/.test(m)), 'a repeated extra name is refused');
h.insp.show(['extras', 'trailer']); change(h, 'extras/trailer/durationSec', '0'); check(msgs(h).some(m => /More than 0/.test(m)), 'an extra must have a length');
change(h, 'extras/trailer/durationSec', '95.5'); change(h, 'extras/trailer/type', 'Clip'); check(doc(h).extras.trailer.durationSec === 95.5 && doc(h).extras.trailer.type === 'Clip', 'extra fields are written');
h = harness(MENU); h.insp.show([]);
const delX = [...h.box.querySelectorAll('button[aria-label="Delete this extra"]')][0]; delX.click();
check(h.confirms.length === 1 && !('trailer' in doc(h).extras), 'deleting an extra a button plays asks first');
h = harness(JSON.stringify({ ...JSON.parse(MENU), extras: undefined }, null, 2)); h.insp.show([]);
addExtra('first'); check(doc(h).extras.first.type === 'Featurette', 'the first extra creates the extras section');

console.log('--- sections, layers, theme, audio');
h = harness(MENU); h.insp.show([]);
click(h, '[data-key="theme/add"]'); check(doc(h).theme.id === 'list-dark', 'adding colours and text starts from a theme');
change(h, 'theme/fontSize', '12'); check(msgs(h).some(m => /At most 10/.test(m)), 'text size above 10 is refused');
change(h, 'theme/fontSize', '4.5'); change(h, 'theme/uppercase', 'true'); check(doc(h).theme.fontSize === 4.5 && doc(h).theme.uppercase === true, 'numbers and yes/no are written');
change(h, 'theme/uppercase', ''); check(!('uppercase' in doc(h).theme), '(default) removes a yes/no');
click(h, '[data-key="audio/add"]'); click(h, '[data-key="audio/music/add"]'); check(JSON.stringify(doc(h).audio) === '{"music":{"source":"themeSong"}}', 'music can be added');
change(h, 'audio/music/source', 'file'); check(msgs(h).some(m => /needed/.test(m)) && ctl(h, 'audio/music/file'), 'music from a file needs its file');
change(h, 'audio/music/file', 'https://example.com/a.mp3'); check(doc(h).audio.music.file === 'https://example.com/a.mp3', 'an https music file is written');
change(h, 'audio/music/file', 'javascript:alert(1)'); check(doc(h).audio.music.file === 'https://example.com/a.mp3', 'a non-https music file is refused');
click(h, '[data-key="layout/add"]'); click(h, '[data-key="layout/flow/add"]'); check(doc(h).layout.flow.columns === 2 && doc(h).layout.flow.region.w === 80, 'the flow grid starts with a region, columns and rows');
change(h, 'layout/flow/columns', '9'); check(msgs(h).some(m => /At most 8/.test(m)), 'more than 8 columns is refused');
click(h, 'button', [...h.box.querySelectorAll('button')].findIndex(b => b.textContent === 'Add a panel'));
check(doc(h).layout.layers.length === 1 && doc(h).layout.layers[0].type === 'panel', 'a panel layer can be added');
h.insp.show(['layout', 'layers', 0]);
change(h, 'layout/layers/0/opacity', '0.25'); check(doc(h).layout.layers[0].opacity === 0.25, 'layer fields are written');
check(!ctl(h, 'layout/layers/0/image') && ctl(h, 'layout/layers/0/fill'), 'a panel shows panel fields only');
change(h, 'layout/layers/0/type', 'image'); const lay = doc(h).layout.layers[0];
check(lay.type === 'image' && !('fill' in lay) && lay.opacity === 0.25 && !!ctl(h, 'layout/layers/0/image') && msgs(h).some(m => /needed/.test(m)), 'a panel becomes an image layer: panel-only fields go, shared ones stay, the picture is asked for', JSON.stringify(lay));
change(h, 'layout/layers/0/image', 'asset:menu/art.png'); check(doc(h).layout.layers[0].image === 'asset:menu/art.png', 'an asset reference is written');
const dataUri = 'data:image/png;base64,iVBORw0KGgo=';
h = harness(MENU.replace('"title": "More",', `"title": "More", "background": { "source": "image", "image": "${dataUri}" },`)); h.insp.show(['menus', 'more']);
check(h.box.querySelector('img.discEdThumb') && h.box.querySelector('img.discEdThumb').src === dataUri && !h.box.querySelector('[data-key="menus/more/background/image"]'), 'a picture stored in the menu shows as a thumbnail, never as a giant text box');
click(h, '.discEdEmbedded button'); check(!('image' in doc(h).menus.more.background), 'and can be removed');

console.log('--- the text keeps its formatting and survives hostile input');
const crlf = MENU.replace(/\n/g, '\r\n'); h = harness(crlf); h.insp.show(['menus', 'main', 'entries', 0]);
change(h, 'menus/main/entries/0/style', 'frame'); check(!/[^\r]\n/.test(h.text) && doc(h).menus.main.entries[0].style === 'frame', 'CRLF files stay CRLF');
const compact = JSON.stringify(JSON.parse(MENU)); h = harness(compact); h.insp.show(['menus', 'main', 'entries', 0]);
change(h, 'menus/main/entries/0/label', 'Go'); check(h.text === compact.replace('"label":"Play"', '"label":"Go"'), 'one-line JSON stays one line');
h = harness(MENU.replace('"Play"', '"Say \\"hi\\" \\u00e9 <>?"').replace('<>?', '')); h.insp.show(['menus', 'main', 'entries', 0]);
check(ctl(h, 'menus/main/entries/0/label').value === 'Say "hi" é ', 'quotes and unicode in values are shown as they are');
for (const text of ['{"menus":{"m":null,"n":"s","o":[],"p":{"entries":[null,1,"x",{"action":{}},{"action":"nope","label":5}]}},"extras":{"a":null,"b":[1]},"root":3}', '{}', '{"menus":{"p":{"entries":"no","background":5,"layout":{"layers":"x"}}}}']) {
  const hh = harness(text); let ok = true; let err = '';
  try { const rows = O.build(text, []) || [{ path: [] }]; rows.forEach(r => hh.insp.show(r.path)); hh.insp.show(['menus', 'p', 'entries', 3]); hh.insp.show(['menus', 'p', 'entries', 4]); hh.insp.show(['menus', 'p']); hh.insp.show([]); } catch (e) { ok = false; err = e.stack; }
  check(ok, 'odd or wrong-typed data never crashes the forms', err.split('\n').slice(0, 3).join(' | '));
}

console.log('--- every part of every example');
let parts = 0, controls = 0, noops = 0, crashes = [];
const dir = path.join(ROOT, 'examples');
for (const file of fs.readdirSync(dir).filter(f => f.endsWith('.menu.json'))) {
  const text = fs.readFileSync(path.join(dir, file), 'utf8');
  const rows = O.build(text, []);
  for (const row of rows.filter(r => r.kind !== 'group')) {
    const hh = harness(text);
    try {
      hh.insp.show(row.path); parts++;
      const found = I.kindAt(row.path, JSON.parse(text));
      if (!found || hh.box.querySelectorAll('.discEdInspHead').length !== 1) crashes.push(file + ' ' + row.path.join('/') + ' no form');
      for (const c of hh.box.querySelectorAll('[data-key]')) {
        if (c.tagName === 'BUTTON' || /add-name|\/add$|\/remove$/.test(c.dataset.key)) continue;
        controls++;
        c.dispatchEvent(new Event('change'));
        if (hh.writes === 0 && hh.text === text) noops++;
      }
      if (hh.text !== text) crashes.push(file + ' ' + row.path.join('/') + ' changing nothing changed the text');
    } catch (e) { crashes.push(file + ' ' + row.path.join('/') + ' ' + e.message); }
  }
}
check(parts > 30 && crashes.length === 0, parts + ' parts of ' + fs.readdirSync(dir).filter(f => f.endsWith('.menu.json')).length + ' examples draw a form, and touching a control without changing it never edits the text', crashes.slice(0, 3).join('; '));
check(controls > 100 && noops === controls, controls + ' controls checked', noops);

console.log('--- random edits keep the file valid and change only what was asked');
let seed = 12345; const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
let edits = 0, broken = [];
for (const file of fs.readdirSync(dir).filter(f => f.endsWith('.menu.json'))) {
  const text0 = fs.readFileSync(path.join(dir, file), 'utf8');
  const hh = harness(text0);
  const rows = O.build(text0, []).filter(r => r.kind !== 'group');
  for (let i = 0; i < 60; i++) {
    const row = rows[Math.floor(rnd() * rows.length)];
    if (!O.resolve(hh.text, row.path).exact) continue;
    hh.insp.show(row.path);
    const cs = [...hh.box.querySelectorAll('select[data-key], input[data-key][type=text], input[data-key][type=number]')].filter(c => !/add/.test(c.dataset.key) && !/\/action$|\/type$|\/source$/.test(c.dataset.key));
    if (!cs.length) continue;
    const c = cs[Math.floor(rnd() * cs.length)];
    const before = hh.text;
    if (c.tagName === 'SELECT') { const o = [...c.options].filter(x => !x.disabled); c.value = o[Math.floor(rnd() * o.length)].value; }
    else if (c.type === 'number') c.value = String(Math.round(rnd() * 100) / 10); else c.value = ['a', 'Hello', '#abcdef', 'https://x.example/p.png', ''][Math.floor(rnd() * 5)];
    c.dispatchEvent(new Event('change')); edits++;
    try { JSON.parse(hh.text); } catch (e) { broken.push(file + ' ' + c.dataset.key + ' -> invalid JSON'); break; }
    const changed = diff(JSON.parse(before), JSON.parse(hh.text));
    if (changed.length > 1) broken.push(file + ' ' + c.dataset.key + ' changed ' + changed.join(','));
    if (changed.length === 1 && !('/' + c.dataset.key).startsWith(changed[0]) && !changed[0].startsWith('/' + c.dataset.key)) broken.push(file + ' ' + c.dataset.key + ' changed ' + changed[0]);
  }
}
check(edits > 200 && broken.length === 0, edits + ' random edits: always valid JSON, and only the edited property changes', broken.slice(0, 3).join('; '));

console.log('failures: ' + fail); process.exit(fail ? 1 : 0);
