#!/usr/bin/env node
// Generates the conformance vectors in this folder from the reference renderer (Jellyfin.Plugin.DiscMenus/Web/discmenus.js).
//
//   node conformance/generate.js          write the vectors
//   node conformance/generate.js --check  fail if the files on disk differ from what the renderer produces now
//
// The vectors are language-neutral JSON: inputs and the outputs the reference produces. A client in any language runs its own
// implementation over the inputs and compares. See conformance/README.md. The vectors are regenerated deliberately when the
// reference's behaviour is changed on purpose; CI runs --check so they can never silently drift from it.
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const OUT = __dirname;
const VERSION = 1;

// ---- load the reference renderer with its internals exposed ----------------------------------------------------------
let src = fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/discmenus.js'), 'utf8');
let els = [];
let active = null;
global.window = { addEventListener() {}, location: { hash: '' } };
global.document = { getElementById: () => ({ querySelectorAll: () => els }), addEventListener() {}, removeEventListener() {} };
Object.defineProperty(document, 'activeElement', { get: () => active });
global.setTimeout = () => {};
const MARK = "console.log('[Disc Menus] renderer script loaded');";
if (!src.includes(MARK)) throw new Error('the renderer no longer ends with its load message; update generate.js');
src = src.replace(MARK, `global.__t = {
    paginate: paginate, cellPosition: cellPosition, moveFocus: moveFocus, entriesForPage: entriesForPage,
    effectiveLayout: effectiveLayout, formatTicks: formatTicks,
    SAFE_IMAGE: SAFE_IMAGE, SAFE_AUDIO: SAFE_AUDIO, TMDB_PATH: TMDB_PATH, HEX_COLOR: HEX_COLOR, FANART_ID: FANART_ID, ITEM_ID: ITEM_ID,
    setDoc: function (d) { menuDoc = d; menuPage = {}; }, setPage: function (k, p) { menuPage[k] = p; }
};`);
(0, eval)(src);
const T = global.__t;

const r6 = (x) => (typeof x === 'number' ? Math.round(x * 1e6) / 1e6 : x);
const roundPos = (p) => (p ? Object.fromEntries(Object.entries(p).map(([k, v]) => [k, r6(v)])) : null);

// ---- output helper: one case per line, so diffs are readable ----------------------------------------------------------
function file(name, description, cases, extra) {
    const head = Object.assign({ conformanceVersion: VERSION, description }, extra || {});
    const lines = cases.map((c) => '    ' + JSON.stringify(c));
    const body = JSON.stringify(head, null, 2).replace(/\n}$/, ',\n  "cases": [\n' + lines.join(',\n') + '\n  ]\n}');
    return { name, text: body + '\n' };
}

const outputs = [];

// ---- 1. paginate -----------------------------------------------------------------------------------------------------
// Entry lists are described compactly: n content entries "E1".."En" (action playExtra), then optionally one "Back" (action back)
// and one "Home" (action home), in that order. Explicit cases spell out the full list.
{
    const build = (n, back, home) => [
        ...Array.from({ length: n }, (_, i) => ({ Action: 'playExtra', Label: 'E' + (i + 1) })),
        ...(back ? [{ Action: 'back', Label: 'Back' }] : []),
        ...(home ? [{ Action: 'home', Label: 'Home' }] : []),
    ];
    const run = (entries, slots, maxPerPage) => T.paginate(entries, slots, maxPerPage).map((p) => ({
        items: p.items.map((e) => e.Label), backs: p.backs.map((e) => e.Label), prev: p.prev, more: p.more,
    }));
    const cases = [];
    for (const slots of [4, 5, 6, 8, 12]) {
        for (let n = 0; n <= 14; n++) {
            for (const [back, home] of [[false, false], [true, false], [false, true], [true, true]]) {
                if (slots < 3 + (back ? 1 : 0) + (home ? 1 : 0)) continue; // the loader requires max(4, pinned + 3) cells
                cases.push({ slots, maxPerPage: 0, n, back, home, pages: run(build(n, back, home), slots, 0) });
            }
        }
    }
    for (const maxPerPage of [2, 3, 6]) {
        for (const slots of [5, 9]) {
            for (const n of [0, 1, 5, 7, 13]) {
                cases.push({ slots, maxPerPage, n, back: true, home: false, pages: run(build(n, true, false), slots, maxPerPage) });
            }
        }
    }
    // explicit entry lists: pinning picks only the FIRST back and the FIRST home; later ones are ordinary content; pinned entries keep entry order
    const explicit = [
        { name: 'home before back keeps entry order', entries: [{ Action: 'playExtra', Label: 'A' }, { Action: 'home', Label: 'Home' }, { Action: 'playExtra', Label: 'B' }, { Action: 'back', Label: 'Back' }], slots: 5 },
        { name: 'a second back is ordinary content', entries: [{ Action: 'back', Label: 'Back 1' }, { Action: 'playExtra', Label: 'A' }, { Action: 'back', Label: 'Back 2' }, { Action: 'playExtra', Label: 'B' }], slots: 4 },
        { name: 'submenu entries are content', entries: [{ Action: 'submenu', Label: 'S' }, { Action: 'chapters', Label: 'C' }, { Action: 'playFeature', Label: 'P' }], slots: 4 },
    ];
    for (const e of explicit) cases.push({ name: e.name, slots: e.slots, maxPerPage: 0, entries: e.entries, pages: run(e.entries, e.slots, 0) });
    outputs.push(file('paginate.json',
        'Splitting a menu\'s entries across the pages of a Layout.Flow grid. "slots" is Columns x Rows. Case inputs are either explicit ' +
        '"entries" or n content entries E1..En followed by Back (if "back") and Home (if "home"). "maxPerPage" 0 means no limit (Menu.MaxPerPage). ' +
        'Expected "pages": for each page the labels of its content items, of its pinned back/home entries (in entry order), and whether Previous / More buttons are added.',
        cases));
}

// ---- 2. cell positions -----------------------------------------------------------------------------------------------
{
    const cases = [];
    const anchors = ['top-left', 'top', 'top-right', 'left', 'center', 'right', 'bottom-left', 'bottom', 'bottom-right'];
    const regions = [{ X: 49, Y: 87.5, W: 60, H: 9 }, { X: 50, Y: 52, W: 86, H: 68 }, { X: 10, Y: 20, W: 80, H: 40 }];
    for (const anchor of anchors) {
        for (const [ri, region] of regions.entries()) {
            const flow = { Region: Object.assign({ Anchor: anchor }, region), Columns: ri === 0 ? 5 : 3, Rows: ri === 0 ? 1 : 4 };
            const slots = flow.Columns * flow.Rows;
            for (const slot of [0, 1, slots - 1]) {
                for (const tall of [false, true]) cases.push({ flow, slot, tall, position: roundPos(T.cellPosition(flow, slot, tall)) });
            }
        }
    }
    cases.push({ name: 'no anchor means top-left', flow: { Region: { X: 0, Y: 0, W: 100, H: 50 }, Columns: 4, Rows: 2 }, slot: 5, tall: true,
        position: roundPos(T.cellPosition({ Region: { X: 0, Y: 0, W: 100, H: 50 }, Columns: 4, Rows: 2 }, 5, true)) });
    outputs.push(file('cells.json',
        'The on-screen position of grid cell "slot" (row-major from 0) of a Layout.Flow, as a Position the normal placement rules understand ' +
        '(percent of the screen, Anchor center). "tall" is true for entries that have an image or thumbnail, which get a height. Compare numbers within 1e-6.',
        cases));
}

// ---- 3. layout of whole documents: effective layout, pages, and where every entry goes -------------------------------
const stressDoc = {
    MenuId: '00000000000000000000000000000001', Root: 'main',
    // Document-wide defaults: every menu inherits them key by key, and may override any key (a null value is skipped, not an override).
    Layout: { ButtonStyle: 'arrow', HideTitle: false, Layers: [{ Type: 'panel', Position: { X: 50, Y: 92, W: 90, H: 12, Anchor: 'center' }, Fill: '#000000', Opacity: 0.5 }] },
    Menus: {
        main: { Title: 'Main', Layout: { TitlePosition: { X: 50, Y: 10, Anchor: 'top' }, HideTitle: null }, Entries: [
            { Action: 'playFeature', Label: 'Play', Position: { X: 30, Y: 80, Anchor: 'bottom' } },
            { Action: 'submenu', Label: 'Extras', Menu: 'extras', Position: { X: 70, Y: 80, Anchor: 'bottom' } }] },
        extras: { Title: 'Extras', Layout: { ButtonStyle: 'glow', Flow: { Region: { X: 50, Y: 60, W: 80, H: 40, Anchor: 'center' }, Columns: 4, Rows: 2, MoreLabel: 'More...' } }, Entries: [
            ...Array.from({ length: 9 }, (_, i) => ({ Action: 'playExtra', Label: 'Extra ' + (i + 1) })),
            { Action: 'home', Label: 'Home' }, { Action: 'back', Label: 'Back' }] },
        short: { Title: 'Short', Layout: { Flow: { Region: { X: 50, Y: 50, W: 60, H: 30, Anchor: 'center' }, Columns: 5, Rows: 1, PreviousLabel: 'Back a page' } }, Entries: [
            { Action: 'playExtra', Label: 'One' }, { Action: 'playExtra', Label: 'Two' }, { Action: 'playExtra', Label: 'Three' },
            { Action: 'playExtra', Label: 'Four' }, { Action: 'playExtra', Label: 'Five' }, { Action: 'back', Label: 'Back' }] },
        picture: { Title: 'Picture', Layout: { Flow: { Region: { X: 50, Y: 50, W: 90, H: 60, Anchor: 'center' }, Columns: 3, Rows: 2 } }, MaxPerPage: 2, Entries: [
            { Action: 'playExtra', Label: 'A', Image: 'asset:x/a.webp' }, { Action: 'playExtra', Label: 'B' }, { Action: 'playExtra', Label: 'C', Image: 'asset:x/c.webp' },
            { Action: 'back', Label: 'Back', Image: 'asset:x/back.webp' }] },
    },
};
fs.writeFileSync(path.join(OUT, 'documents', 'layout-stress.renderable.json'), JSON.stringify(stressDoc, null, 2) + '\n');

{
    const fixtures = path.join(ROOT, 'tests/fixtures/renderable');
    const names = fs.readdirSync(fixtures).filter((f) => f.endsWith('.renderable.json')).sort();
    const cases = [];
    const docFiles = ['layout-stress.renderable.json'];
    for (const f of names) {
        fs.copyFileSync(path.join(fixtures, f), path.join(OUT, 'documents', f));
        docFiles.push(f);
    }
    for (const f of docFiles.sort()) {
        const doc = JSON.parse(fs.readFileSync(path.join(OUT, 'documents', f), 'utf8'));
        T.setDoc(doc);
        for (const key of Object.keys(doc.Menus)) {
            const menu = doc.Menus[key];
            const layout = T.effectiveLayout(menu);
            const first = T.entriesForPage(menu, layout, key);
            for (let page = 0; page < first.pages; page++) {
                T.setPage(key, page);
                const r = T.entriesForPage(menu, layout, key);
                cases.push({
                    document: f, menu: key, page: r.page, pages: r.pages, effectiveLayout: layout,
                    entries: r.entries.map((e) => ({ Action: e.Action, Label: e.Label, Position: roundPos(e.Position) })),
                });
            }
        }
    }
    outputs.push(file('layout.json',
        'For each document in documents/ and each menu, each page: the merged layout (document Layout, then the menu\'s Layout, key by key; null skips) ' +
        'and the entries to draw, in drawing order, with their Position (null = flows in the default column). For menus with a Layout.Flow, content items come first, then ' +
        'pinned back/home, then Previous, then More (synthetic actions pagePrev / pageNext), the last four in the final cells. Compare numbers within 1e-6.',
        cases));
}

// ---- 4. directional focus ---------------------------------------------------------------------------------------------
{
    const mk = (name, cx, cy, w = 160, h = 40) => ({ name, left: cx - w / 2, top: cy - h / 2, width: w, height: h });
    const layouts = {
        'row of three': [mk('Play', 420, 970), mk('Scene', 960, 970), mk('Features', 1500, 970)],
        'features column with an off-column Back': [mk('Gag', 150, 324), mk('Deleted', 150, 432), mk('Featurettes', 150, 540), mk('Evolution', 150, 648), mk('Darryl', 150, 756), mk('Back', 1750, 972, 100, 40)],
        'two by two': [mk('a', 300, 300), mk('b', 700, 300), mk('c', 300, 600), mk('d', 700, 600)],
        'three by three': Array.from({ length: 9 }, (_, i) => mk('g' + i, 400 + (i % 3) * 500, 250 + Math.floor(i / 3) * 250, 200, 60)),
        'mixed heights centred': [mk('One', 300, 500, 200, 40), mk('Two', 700, 500, 200, 90), mk('Three', 1100, 500, 200, 40)],
        'mixed heights top aligned': [mk('A', 300, 520, 200, 40), mk('B', 700, 545, 200, 90), mk('C', 1100, 520, 200, 40)],
        'banner of five with pinned Back': [mk('E1', 480, 945, 180, 50), mk('E2', 710, 945, 180, 50), mk('E3', 940, 945, 180, 50), mk('Back', 1170, 945, 180, 50), mk('More', 1400, 945, 180, 50)],
        // Pressing down from C wraps to a button above. A (straight above, 400 px away) and B (200 px to the side, 410 px away) are within the
        // 20 px tie band, so the straighter one, A, wins; D is clearly farther and wins outright. Both DOM orders are covered.
        'wrap tie band, nearer one first': [mk('C', 300, 600), mk('A', 300, 200), mk('B', 500, 190)],
        'wrap tie band, farther one first': [mk('C', 300, 600), mk('B', 500, 190), mk('A', 300, 200)],
        'wrap clearly farther wins': [mk('C', 300, 600), mk('A', 300, 200), mk('B', 500, 190), mk('D', 700, 100)],
        // Pressing right from F: O is in the same row (1000 px away), N is nearer (200 px) but offset by 140 px. Same-row buttons rank first.
        'same row beats a nearer diagonal': [mk('F', 500, 500), mk('N', 700, 640), mk('O', 1500, 500)],
        // Pressing right from F, neither candidate shares its row. P is nearer along the row (300) but 200 off it; Q is 500 along and 60 off.
        // Sideways drift counts double: P scores 300 + 2x200 = 700, Q 500 + 2x60 = 620, so Q wins (with a weight of 1, P would).
        'diagonal drift is weighted double': [mk('F', 500, 500), mk('P', 800, 700), mk('Q', 1000, 560)],
        // G's left edge is 20 px inside F's right edge, within the 0.25 x width (40 px) tolerance, so G still counts as being to the right.
        'a slightly overlapping neighbour is still ahead': [mk('F', 500, 500), mk('G', 640, 500)],
        // U and L are mirror images above and below F's row: exactly equal rank for a press to the right. The one EARLIER in the list wins,
        // so the answer depends on drawing order. Both orders are here.
        'exact tie goes to the earlier button (U first)': [mk('F', 500, 500), mk('U', 900, 460), mk('L', 900, 540)],
        'exact tie goes to the earlier button (L first)': [mk('F', 500, 500), mk('L', 900, 540), mk('U', 900, 460)],
        'staggered': [mk('p', 300, 300), mk('q', 900, 340), mk('r', 400, 700), mk('s', 1000, 660), mk('t', 1500, 500)],
    };
    const dirs = { right: [1, 0], left: [-1, 0], down: [0, 1], up: [0, -1] };
    const cases = [];
    for (const [name, list] of Object.entries(layouts)) {
        for (let from = 0; from < list.length; from++) {
            const expected = {};
            for (const [dn, [dx, dy]] of Object.entries(dirs)) {
                els = list.map((r) => ({ name: r.name, focus() { active = this; }, classList: { contains: () => true },
                    getBoundingClientRect: () => ({ left: r.left, top: r.top, width: r.width, height: r.height, right: r.left + r.width, bottom: r.top + r.height }) }));
                active = els[from];
                T.moveFocus(dx, dy);
                expected[dn] = list.findIndex((r) => r.name === active.name);
            }
            cases.push({ layout: name, rects: list.map((r) => ({ name: r.name, left: r.left, top: r.top, width: r.width, height: r.height })), from, expected });
        }
    }
    outputs.push(file('focus.json',
        'Directional focus. "rects" are the on-screen rectangles of the buttons in DOM (drawing) order, in CSS pixels; "from" is the index of the focused one. ' +
        '"expected" gives, for each direction, the index of the button that takes focus (the same index = nothing moves). Includes wrapping. ' +
        'Constants: a candidate is "ahead" if its edge gap in the direction is >= -0.25 x (width for left/right, height for up/down); ' +
        'buttons overlapping on the cross axis rank before others; wrapping prefers the farthest button behind, with a 20 px band treated as a tie.',
        cases));
}

// ---- 5. time formatting ----------------------------------------------------------------------------------------------
{
    const ticks = [0, 1, 9999999, 10000000, 50000000, 590000000, 600000000, 3000000000, 36000000000, 36010000000, 37290000000, 359999999999, 360000000000, 4500000000000];
    outputs.push(file('time-format.json',
        'Chapter times shown on scene-selection thumbnails. "ticks" are Jellyfin 100-nanosecond ticks; the text is h:mm:ss from one hour, otherwise m:ss (minutes not padded).',
        ticks.map((t) => ({ ticks: t, text: T.formatTicks(t) }))));
}

// ---- 6. what the renderer will accept from untrusted menu data --------------------------------------------------------
{
    const images = ['https://example.com/a.png', 'https://example.com/a b.png', 'http://example.com/a.png', 'javascript:alert(1)', 'data:image/png;base64,AAAA',
        'data:image/jpeg;base64,/9j/4AAQ', 'data:image/webp;base64,UklGRg==', 'data:image/svg+xml;base64,PHN2Zz4=', 'data:image/gif;base64,R0lGOD', 'data:text/html;base64,PGI+',
        'asset:menu/bg.webp', 'asset:a', 'asset:a/b/c/d.png', 'asset:a/b/c/d/e.png', 'asset:../x', 'asset:a/../x', 'asset:/x', 'asset:.hidden', 'asset:' + 'x'.repeat(64), 'asset:' + 'x'.repeat(65),
        'https://x.test/a)b', 'https://x.test/a"b', "https://x.test/a'b", 'https://x.test/<b>', 'https://x.test/a\\b', 'file:///etc/passwd', '//example.com/a.png', '', ' https://example.com/a.png',
        'https://example.com/a.png\n'];
    const audio = ['https://example.com/a.mp3', 'https://example.com/anything', 'asset:menu/loop.wav', 'asset:menu/loop.ogg', 'asset:menu/loop.opus', 'asset:menu/loop.m4a', 'asset:menu/loop.mp3',
        'asset:menu/loop.flac', 'asset:menu/loop', 'asset:menu/loop.WAV', 'asset:a/b/c/d.mp3', 'asset:../x.mp3', 'data:audio/wav;base64,AAAA', 'http://example.com/a.mp3', 'javascript:alert(1)'];
    const colours = ['#3ddc84', '#FFFFFF', '#abcdef', '#fff', '#12345', '#1234567', '3ddc84', 'red', 'rgb(0,0,0)', '#ggg000', '', '#3ddc84 '];
    const fanart = ['47835', '1', '123456789012', '1234567890123', '', '-1', '1.5', '12a', ' 1', '1 ', '١٢٣', '0x10', '1e5', '../1', '1/2'];
    const item = ['0b311cdd-e7c2-4e27-b4bc-fb1e5acebfc8', '0b311cdde7c24e27b4bcfb1e5acebfc8', 'preview', '', '../../etc', '0b311cdd-e7c2-4e27-b4bc-fb1e5acebfc8/x', '0b311cdd/e7c2/4e27/b4bc/fb1e5acebfc8', '0b311cdd.e7c2.4e27.b4bc.fb1e5acebfc8', '0B311CDD-E7C2-4E27-B4BC-FB1E5ACEBFC8'];
    const tmdb = ['/abc123.jpg', '/a-b_c.png', '/abc.gif', '/abc.JPG', 'abc.jpg', '/../abc.jpg', '/a/b.jpg', '/abc.jpg?x=1', ''];
    outputs.push(file('safety.json',
        'Which references from an untrusted menu a client must accept. "image" applies to Image, ImageFocus, Poster and layer/background images (https, small base64 data: ' +
        'images of png/jpeg/webp, or an asset: reference of 1-4 segments of 1-64 characters from A-Za-z0-9._- starting with a letter or digit); "audio" to music and sound files ' +
        '(https, or an asset: reference ending in .mp3 .ogg .opus .m4a or .wav); "colour" to every colour (exactly #rrggbb); "tmdb" to Background.TmdbFilePath; "fanartId" to Background.FanartId (1 to 12 ASCII digits); "itemId" to the library item id put into the fanart path (a GUID, with or without hyphens). ' +
        'Everything not accepted must be ignored (use the default), never fetched or displayed. Matching is against the WHOLE string.',
        [].concat(
            images.map((v) => ({ kind: 'image', value: v, accepted: T.SAFE_IMAGE.test(v) })),
            audio.map((v) => ({ kind: 'audio', value: v, accepted: T.SAFE_AUDIO.test(v) })),
            colours.map((v) => ({ kind: 'colour', value: v, accepted: T.HEX_COLOR.test(v) })),
            tmdb.map((v) => ({ kind: 'tmdb', value: v, accepted: T.TMDB_PATH.test(v) })),
            fanart.map((v) => ({ kind: 'fanartId', value: v, accepted: T.FANART_ID.test(v) })),
            item.map((v) => ({ kind: 'itemId', value: v, accepted: T.ITEM_ID.test(v) })))));
}

// ---- write or check --------------------------------------------------------------------------------------------------
const check = process.argv.includes('--check');
const stale = [];
for (const o of outputs) {
    const p = path.join(OUT, o.name);
    if (check) {
        if (!fs.existsSync(p) || fs.readFileSync(p, 'utf8') !== o.text) stale.push(o.name);
    } else {
        fs.writeFileSync(p, o.text);
    }
}
if (check) {
    // the committed documents must be the ones the vectors were made from
    for (const f of fs.readdirSync(path.join(OUT, 'documents'))) {
        const orig = path.join(ROOT, 'tests/fixtures/renderable', f);
        if (fs.existsSync(orig) && fs.readFileSync(orig, 'utf8') !== fs.readFileSync(path.join(OUT, 'documents', f), 'utf8')) stale.push('documents/' + f);
    }
    if (stale.length) {
        console.error('Out of date: ' + stale.join(', ') + '\nRun `node conformance/generate.js` if the reference renderer changed on purpose.');
        process.exit(1);
    }
    console.log('conformance vectors are up to date (' + outputs.length + ' files)');
} else {
    console.log('wrote ' + outputs.map((o) => o.name).join(', '));
}
