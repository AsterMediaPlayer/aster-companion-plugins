// The in-place JSON editor behind the Menu Editor's forms and pickers: every operation changes only what it must, refuses what it can't do
// safely, and (property test) always means exactly what the same edit means on the parsed data.
const path = require('path');
const fs = require('fs');
const assert = require('assert');
const ROOT = path.resolve(__dirname, '..', '..');
const J = require(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/editor/json-text.js'));
let fail = 0; const check = (ok, m, e) => { if (!ok) fail++; console.log(ok ? 'PASS' : 'FAIL', m, e === undefined ? '' : e); };
const code = (fn) => { try { fn(); return 'none'; } catch (e) { return e.code || 'other:' + e.message; } };

const PRETTY = `{
  "a": 1,
  "list": [ 1, 2, 3 ],
  "multi": [
    "x",
    "y"
  ],
  "obj": { "k": "v", "n": null },
  "deep": {
    "inner": {
      "z": true
    }
  }
}
`;

console.log('--- reading');
check(J.get(PRETTY, ['a']) === '1' && J.get(PRETTY, ['obj', 'k']) === '"v"' && J.get(PRETTY, ['multi', 1]) === '"y"', 'get returns the value\'s own text');
check(J.getValue(PRETTY, ['list']).join() === '1,2,3' && J.getValue(PRETTY, ['nope']) === undefined && J.getValue(PRETTY, ['list', 9]) === undefined, 'getValue parses it; a missing path is undefined');
const loc = J.locate(PRETTY, ['deep', 'inner', 'z']);
check(loc.line === 11 && PRETTY.slice(loc.start, loc.end) === 'true' && loc.keyStart < loc.start, 'locate gives the span, its line, and where the key starts', JSON.stringify(loc));
check(J.locate(PRETTY, ['missing']) === null, 'locate of nothing is null');
check(J.get('{"a":1,"a":2}', ['a']) === '2', 'reading a duplicated key follows JSON: the last one wins');
check(code(() => J.parse('{ nope')) === 'syntax' && J.parse('{"a":1,"a":2}').duplicates.length === 1, 'parse reports syntax errors, and lists duplicates instead of failing');
try { J.parse('{\n  "a": 1,\n  "b": }'); } catch (e) { check(e.code === 'syntax' && e.line === 3, 'a syntax error knows its line', e.message + ' line ' + e.line); }

console.log('--- set');
let out = J.set(PRETTY, ['a'], '99');
check(out === PRETTY.replace('"a": 1', '"a": 99'), 'replacing a value changes only that value');
out = J.set(PRETTY, ['obj', 'k'], '{ "x": [1] }');
check(JSON.parse(out).obj.k.x[0] === 1 && out.startsWith(PRETTY.slice(0, PRETTY.indexOf('"k": "v"') + 5)), 'a value can become an object');
out = J.set(PRETTY, ['b'], '"new"');
check(out.includes('"deep": {\n    "inner": {\n      "z": true\n    }\n  },\n  "b": "new"\n}'), 'a new top-level key goes after the last property at its indent', out.slice(-90));
out = J.set(PRETTY, ['b'], '"new"', { after: ['a'] });
check(out.startsWith('{\n  "a": 1,\n  "b": "new",\n  "list"'), 'opts.after puts it after the preferred property');
out = J.set(PRETTY, ['b'], '"new"', { after: ['missing', 'obj'] });
check(out.includes('"n": null },\n  "b": "new",\n  "deep"'), 'the first preferred property that exists wins');
out = J.set(PRETTY, ['obj', 'added'], '5');
check(out.includes('"obj": { "k": "v", "n": null, "added": 5 }'), 'a one-line object stays one line');
out = J.set('{"a":{}}', ['a', 'b'], '1'); check(out === '{"a":{ "b": 1 }}', 'an empty object is filled', out);
out = J.set(PRETTY, ['deep', 'inner', 'y', 'w'], '1');
check(JSON.parse(out).deep.inner.y.w === 1 && out.includes('"z": true,\n      "y": { "w": 1 }'), 'missing objects on the way are created', out.slice(150));
out = J.set(PRETTY, ['multi', 2], '"z"'); check(JSON.parse(out).multi.join() === 'x,y,z', 'setting the position just past the end appends');
check(code(() => J.set(PRETTY, ['multi', 5], '"z"')) === 'path', 'but not further out');
check(code(() => J.set(PRETTY, ['a', 'b'], '1')) === 'path' && code(() => J.set(PRETTY, ['list', 'x'], '1')) === 'path', 'cannot go into a number, or name a key inside a list');
check(code(() => J.set(PRETTY, [], '1')) === 'path' && code(() => J.set(PRETTY, ['a'], '{ nope')) === 'value' && code(() => J.set(PRETTY, ['b', 3], '1')) === 'path', 'refuses the root, a bad value, and an array that does not exist');
const crlf = PRETTY.replace(/\n/g, '\r\n');
out = J.set(crlf, ['b'], '1'); check(!/[^\r]\n/.test(out) && JSON.parse(out).b === 1, 'CRLF files keep CRLF');
const tabs = '{\n\t"a": 1,\n\t"b": 2\n}';
out = J.set(tabs, ['c'], '3'); check(out === '{\n\t"a": 1,\n\t"b": 2,\n\t"c": 3\n}', 'tab indentation is copied', JSON.stringify(out));
const hostile = '{"s": "has { braces }, \\"quotes\\" and \\"b\\": 1", "b": {"x": 1}}';
out = J.set(hostile, ['b', 'x'], '2'); check(JSON.parse(out).b.x === 2 && JSON.parse(out).s === JSON.parse(hostile).s, 'braces, commas and quotes inside strings do not confuse it');
check(J.set('{"a":"\\u00e9 ü 😀"}', ['b'], '1').includes('😀'), 'non-ASCII text is preserved');

console.log('--- remove');
out = J.remove(PRETTY, ['a']); check(out === PRETTY.replace('  "a": 1,\n', ''), 'removing the first property takes its line with it');
out = J.remove(PRETTY, ['deep']); check(out.endsWith('"obj": { "k": "v", "n": null }\n}\n'), 'removing the last property takes the comma before it', out.slice(-60));
out = J.remove(PRETTY, ['obj', 'n']); check(out.includes('"obj": { "k": "v" }'), 'one-line object loses an item cleanly');
out = J.remove(PRETTY, ['multi', 0]); check(out.includes('"multi": [\n    "y"\n  ]'), 'a list item goes with its separator');
out = J.remove(PRETTY, ['list', 2]); check(out.includes('"list": [ 1, 2 ]'), 'the last item of a one-line list');
out = J.remove('{"a":{"only":1}}', ['a', 'only']); check(out === '{"a":{}}', 'the only property leaves an empty object');
out = J.remove('{"a":[1]}', ['a', 0]); check(out === '{"a":[]}', 'the only item leaves an empty list');
check(code(() => J.remove(PRETTY, [])) === 'path' && code(() => J.remove(PRETTY, ['nope'])) === 'path', 'refuses the root and a missing path');

console.log('--- lists');
out = J.insertInArray(PRETTY, ['list'], 1, '9'); check(out.includes('"list": [ 1, 9, 2, 3 ]'), 'insert in the middle uses the existing separator');
out = J.insertInArray(PRETTY, ['list'], 0, '9'); check(out.includes('"list": [ 9, 1, 2, 3 ]'), 'insert at the start');
out = J.insertInArray(PRETTY, ['list'], 99, '9'); check(out.includes('"list": [ 1, 2, 3, 9 ]'), 'an index past the end appends');
out = J.insertInArray(PRETTY, ['multi'], 1, '"m"'); check(out.includes('"multi": [\n    "x",\n    "m",\n    "y"\n  ]'), 'a multi-line list keeps its layout');
out = J.insertInArray('{"a":[]}', ['a'], 0, '1'); check(out === '{"a":[1]}', 'into an empty list');
out = J.insertInArray('{"a":[\n  1\n]}', ['a'], 5, '2'); check(out === '{"a":[\n  1,\n  2\n]}', 'one multi-line item gives the layout', JSON.stringify(out));
out = J.moveInArray(PRETTY, ['multi'], 0, 1); check(out.includes('"multi": [\n    "y",\n    "x"\n  ]'), 'moving swaps with the layout intact');
out = J.moveInArray(PRETTY, ['list'], 2, 0); check(out.includes('"list": [ 3, 1, 2 ]'), 'move to the front');
check(J.moveInArray(PRETTY, ['list'], 1, 1) === PRETTY, 'moving to where it is changes nothing');
check(code(() => J.moveInArray(PRETTY, ['list'], 0, 3)) === 'path' && code(() => J.moveInArray(PRETTY, ['a'], 0, 1)) === 'path' && code(() => J.insertInArray(PRETTY, ['obj'], 0, '1')) === 'path', 'refuses out-of-range moves and things that are not lists');
const nestedItems = '{"e":[{"label":"A","p":{"x":1}},{"label":"B"}]}';
out = J.moveInArray(nestedItems, ['e'], 1, 0); check(JSON.parse(out).e[0].label === 'B' && JSON.stringify(JSON.parse(out).e[1]) === JSON.stringify(JSON.parse(nestedItems).e[0]), 'whole objects move intact');

console.log('--- refusals');
check(code(() => J.set('{ nope', ['a'], '1')) === 'syntax' && code(() => J.remove('', ['a'])) === 'syntax' && code(() => J.insertInArray('[1,', [], 0, '1')) === 'syntax', 'no edit runs on text that is not JSON');
for (const bad of ['{"a":1,"a":2}', '{"x":{"k":1,"k":2}}', '{"l":[{"d":1,"d":2}]}']) {
  check(code(() => J.set(bad, ['z'], '1')) === 'duplicate' && code(() => J.remove(bad, ['a'])) === 'duplicate', 'no edit runs while a key is repeated: ' + bad);
}
try { J.set('{"x":{"k":1,\n "k":2}}', ['z'], '1'); } catch (e) { check(/"k" appears more than once in x/.test(e.message) && e.line === 2, 'the duplicate error names the key and where', e.message); }
check(code(() => J.set('['.repeat(70) + ']'.repeat(70), [0], '1')) === 'syntax', 'absurdly deep nesting is refused, not a stack overflow');
check(code(() => J.set('{"a":1}\n// comment', ['a'], '2')) === 'syntax' && code(() => J.set('{"a":1,}', ['a'], '2')) === 'syntax', 'comments and trailing commas are not JSON, and are refused');
check(code(() => J.set('﻿{"a":1}', ['a'], '2')) === 'syntax', 'a byte-order mark is refused');
check(code(() => J.set(null, ['a'], '2')) === 'syntax' && code(() => J.set(undefined, ['a'], '2')) === 'syntax', 'non-text input is refused');

console.log('--- property test: random edits on every example menu');
function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
const examples = fs.readdirSync(path.join(ROOT, 'examples')).filter(f => f.endsWith('.menu.json')).sort();
const docs = fs.readdirSync(path.join(ROOT, 'conformance/documents')).filter(f => f.endsWith('.json')).sort().map(f => path.join(ROOT, 'conformance/documents', f));
const files = examples.map(f => path.join(ROOT, 'examples', f)).concat(docs);
function collect(v, p, acc) { acc.push({ path: p, value: v }); if (Array.isArray(v)) v.forEach((x, i) => collect(x, p.concat([i]), acc)); else if (v && typeof v === 'object') Object.keys(v).forEach(k => collect(v[k], p.concat([k]), acc)); return acc; }
function randomValue(r, depth) { const c = Math.floor(r() * (depth > 1 ? 4 : 6)); if (c === 0) return Math.floor(r() * 1000) / 4; if (c === 1) return ['', 'x', 'q"uote', 'back\\slash', 'é ü 😀', '{ [ , ] }', 'line\nbreak'][Math.floor(r() * 7)]; if (c === 2) return r() < 0.5; if (c === 3) return null; if (c === 4) return Array.from({ length: Math.floor(r() * 3) }, () => randomValue(r, depth + 1)); const o = {}; for (let i = Math.floor(r() * 3); i > 0; i--) o['k' + Math.floor(r() * 5)] = randomValue(r, depth + 1); return o; }
const at = (v, p) => p.reduce((x, k) => x[k], v);
function diffWindow(a, b) { let p = 0; while (p < a.length && p < b.length && a[p] === b[p]) p++; let s = 0; while (s < a.length - p && s < b.length - p && a[a.length - 1 - s] === b[b.length - 1 - s]) s++; return { removed: a.length - p - s, added: b.length - p - s }; }
let ran = 0, wrong = 0, wide = 0, refused = 0; const firstWrong = [];
files.forEach((file, fi) => {
  const original = fs.readFileSync(file, 'utf8');
  for (const variant of [original, original.replace(/\n/g, '\r\n')]) {
    const r = rng(1000 + fi * 7 + (variant === original ? 0 : 1));
    let text = variant, model = JSON.parse(variant);
    for (let step = 0; step < 150; step++) {
      const all = collect(model, [], []).filter(x => x.path.length > 0);
      const containers = collect(model, [], []).filter(x => Array.isArray(x.value) || (x.value && typeof x.value === 'object'));
      const kind = ['set', 'setnew', 'remove', 'insert', 'move'][Math.floor(r() * 5)];
      let before = text, expected, valueText = null, oldLen = 0;
      try {
        if (kind === 'set' && all.length) {
          const t = all[Math.floor(r() * all.length)]; const v = randomValue(r, 0); valueText = JSON.stringify(v, null, r() < 0.5 ? 2 : 0);
          expected = JSON.parse(JSON.stringify(model)); at(expected, t.path.slice(0, -1))[t.path[t.path.length - 1]] = v; oldLen = J.get(text, t.path).length; text = J.set(text, t.path, valueText);
        } else if (kind === 'setnew') {
          const objs = containers.filter(x => !Array.isArray(x.value)); const c = objs[Math.floor(r() * objs.length)]; const key = 'new' + Math.floor(r() * 1e6); const v = randomValue(r, 0); valueText = JSON.stringify(v);
          expected = JSON.parse(JSON.stringify(model)); at(expected, c.path)[key] = v; text = J.set(text, c.path.concat([key]), valueText, r() < 0.5 ? { after: ['title', 'match'] } : undefined);
        } else if (kind === 'remove' && all.length) {
          const t = all[Math.floor(r() * all.length)]; expected = JSON.parse(JSON.stringify(model)); const par = at(expected, t.path.slice(0, -1)); const k = t.path[t.path.length - 1];
          if (Array.isArray(par)) par.splice(k, 1); else delete par[k]; valueText = JSON.stringify(t.value); oldLen = J.get(text, t.path).length; text = J.remove(text, t.path);
        } else if (kind === 'insert') {
          const arrs = containers.filter(x => Array.isArray(x.value)); if (!arrs.length) continue; const c = arrs[Math.floor(r() * arrs.length)]; const idx = Math.floor(r() * (c.value.length + 2)); const v = randomValue(r, 0); valueText = JSON.stringify(v);
          expected = JSON.parse(JSON.stringify(model)); at(expected, c.path).splice(Math.min(idx, c.value.length), 0, v); text = J.insertInArray(text, c.path, idx, valueText);
        } else if (kind === 'move') {
          const arrs = containers.filter(x => Array.isArray(x.value) && x.value.length > 1); if (!arrs.length) continue; const c = arrs[Math.floor(r() * arrs.length)]; const from = Math.floor(r() * c.value.length), to = Math.floor(r() * c.value.length);
          expected = JSON.parse(JSON.stringify(model)); const a = at(expected, c.path); const [it] = a.splice(from, 1); a.splice(to, 0, it); valueText = JSON.stringify(it); oldLen = J.locate(text, c.path.concat([Math.max(from, to)])).end - J.locate(text, c.path.concat([Math.min(from, to)])).start; text = J.moveInArray(text, c.path, from, to);
        } else continue;
      } catch (e) { refused++; wrong++; if (firstWrong.length < 3) firstWrong.push(kind + ' threw ' + e.code + ': ' + e.message); text = before; continue; }
      ran++;
      try { assert.deepStrictEqual(JSON.parse(text), expected); } catch (e) { wrong++; if (firstWrong.length < 3) firstWrong.push(kind + ' gave a different meaning in ' + path.basename(file)); text = before; continue; }
      const w = diffWindow(before, text); const allowance = Math.max(valueText ? valueText.length : 0, oldLen) + 400;
      if (w.removed > allowance || w.added > allowance) { wide++; if (firstWrong.length < 3) firstWrong.push(kind + ' reformatted more than it should in ' + path.basename(file) + ': ' + JSON.stringify(w)); }
      if (variant !== original && /[^\r]\n/.test(text)) { wrong++; if (firstWrong.length < 3) firstWrong.push(kind + ' broke CRLF in ' + path.basename(file)); }
      model = expected;
    }
  }
});
check(ran > 3000, 'ran thousands of random edits across ' + files.length + ' files (LF and CRLF)', ran);
check(wrong === 0, 'every edit means exactly what the same edit means on the parsed data, and nothing threw', wrong + ' ' + firstWrong.join(' | '));
check(wide === 0, 'no edit changed more of the file than the edit itself (no reformatting)', wide + ' ' + firstWrong.join(' | '));
console.log('--- the syntax-error finder agrees with JSON.parse');
{
  const r = rng(424242); let disagree = 0, valid = 0, invalid = 0; const examples2 = [];
  const alphabet = ['{', '}', '[', ']', ',', ':', '"', '\\', ' ', '\n', 'a', '1', '-', 'e', '.', 't', 'n', 'u', '\u00e9', '\u0000'];
  files.forEach(f => { const src = fs.readFileSync(f, 'utf8'); for (let k = 0; k < 120; k++) {
    let s = src; const edits = 1 + Math.floor(r() * 3);
    for (let e = 0; e < edits; e++) { const p = Math.floor(r() * s.length); const c = r(); s = c < 0.34 ? s.slice(0, p) + s.slice(p + 1) : c < 0.67 ? s.slice(0, p) + alphabet[Math.floor(r() * alphabet.length)] + s.slice(p) : s.slice(0, p) + s.slice(p + 1 + Math.floor(r() * 5)); }
    let ok = true; try { JSON.parse(s); } catch (e) { ok = false; }
    const found = J.findError(s); const mine = found === null;
    if (ok) valid++; else invalid++;
    if (mine !== ok) { disagree++; if (examples2.length < 2) examples2.push(JSON.stringify(s.slice(0, 60)) + ' parse=' + ok + ' finder=' + mine); }
    else if (!ok && !(found.index >= 0 && found.index <= s.length)) { disagree++; }
  } });
  for (const s of ['', ' ', 'null', '01', '1.', '.5', '-', '1e', '"\\x"', '"\\u12"', '[,]', '{,}', '[1 2]', '{"a":1 "b":2}', '\u2028', '"\u2028"', 'NaN', 'true false']) { let ok = true; try { JSON.parse(s); } catch (e) { ok = false; } if ((J.findError(s) === null) !== ok) { disagree++; examples2.push('edge ' + JSON.stringify(s)); } }
  check(disagree === 0, 'on ' + valid + ' valid and ' + invalid + ' invalid mutated menus (and edge cases) it agrees with JSON.parse, with an in-range position', disagree + ' ' + examples2.join(' | '));
  check(valid > 100 && invalid > 1000, 'the fuzz covered both valid and invalid text', valid + '/' + invalid);
}
console.log('failures:', fail); process.exit(fail ? 1 : 0);
