// The schema hints (what the editor's forms know about each part of a menu) are generated from the schema and must be current, complete
// and able to describe every property of every example menu.
const path = require('path'); const fs = require('fs'); const { spawnSync } = require('child_process');
const ROOT = path.resolve(__dirname, '..', '..');
let fail = 0; const check = (ok, m, e) => { if (!ok) fail++; console.log(ok ? 'PASS' : 'FAIL', m, e === undefined ? '' : e); };
const H = require(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/editor/schema-hints.js'));
const schema = JSON.parse(fs.readFileSync(path.join(ROOT, 'schema/menu.schema.json'), 'utf8'));

const run = spawnSync(process.execPath, [path.join(ROOT, 'tools/schema-hints/generate.js'), '--check'], { encoding: 'utf8' });
check(run.status === 0, 'the committed schema-hints.js matches the schema and overrides', (run.stdout + run.stderr).trim());

console.log('--- shape');
const kinds = H.kinds; const names = Object.keys(kinds);
check(['document', 'menu', 'entry', 'background', 'theme', 'audio', 'music', 'sounds', 'layout', 'flow', 'transition', 'position', 'layer', 'extra', 'meta', 'match'].every(k => kinds[k]), 'every part of a menu has a kind', names.join());
let bad = [];
for (const [kn, k] of Object.entries(kinds)) for (const f of k.fields) {
  if (!['string', 'integer', 'number', 'boolean', 'enum', 'object', 'array', 'map'].includes(f.type)) bad.push(kn + '.' + f.name + ' type ' + f.type);
  if (f.type === 'enum' && !(Array.isArray(f.enum) && f.enum.length)) bad.push(kn + '.' + f.name + ' enum');
  if ((f.type === 'object' || f.type === 'map' || (f.type === 'array' && f.kind)) && !kinds[f.kind]) bad.push(kn + '.' + f.name + ' kind ' + f.kind);
  if (f.type === 'object' && f.create === undefined && !['position', 'providerIds'].includes(f.kind) && !f.hidden && false) bad.push('no create ' + kn + '.' + f.name);
}
check(bad.length === 0, 'every field has a known type, enum values and kind', bad.join('; '));
const noLabel = []; for (const [kn, k] of Object.entries(kinds)) for (const f of k.fields) if (!f.hidden && !f.label && !(f.name === 'type' && kn === 'layer' && false)) noLabel.push(kn + '.' + f.name);
check(noLabel.length === 0, 'every visible field has a human label', noLabel.join(', '));
const missingCreate = []; for (const [kn, k] of Object.entries(kinds)) for (const f of k.fields) if (f.type === 'object' && !f.hidden && !f.required && f.create === undefined) missingCreate.push(kn + '.' + f.name);
check(missingCreate.length === 0, 'every optional section says what a new one starts as', missingCreate.join(', '));
check(kinds.layer.fields[0].name === 'type' && kinds.layer.fields[0].enum.join() === 'panel,image', 'a layer\'s type is a field of its own');
check(Object.keys(kinds.entry.variants).join() === schema.$defs.entry.oneOf.map(v => v.properties.action.const).join(), 'a button has one variant per action in the schema');
check(kinds.entry.variants.playExtra.required.includes('extra') && kinds.entry.variants.playSequence.required.includes('extras'), 'required fields per action come from the schema');
check(kinds.background.requiredWhen.values.fanart.join() === 'fanartId' && kinds.background.requiredWhen.values.color.join() === 'color', 'which background source needs which field comes from the schema');
const bg = Object.fromEntries(kinds.background.fields.map(f => [f.name, f]));
check(bg.dim.maximum === 1 && bg.dim.minimum === 0 && bg.fanartId.pattern === '^[0-9]{1,12}$' && kinds.entry.fields.find(f => f.name === 'label').maxLength === 80, 'limits and patterns are carried over');
check(kinds.flow.fields.find(f => f.name === 'region').requireAll.join() === 'x,y,w,h', 'the flow region needs x, y, w and h');

console.log('--- every property of every example menu can be shown');
const missing = new Set(); let docs = 0;
const HIDDEN_DOC = new Set(['schemaVersion', 'menuId', 'revision', 'extras', 'menus']);
function walk(kind, node, where, variantValue) {
  if (!node || typeof node !== 'object' || Array.isArray(node)) return;
  const k = kinds[kind]; const fieldNames = new Set(k.fields.map(f => f.name));
  for (const key of Object.keys(node)) {
    if (kind === 'document' && HIDDEN_DOC.has(key)) continue;
    if (!fieldNames.has(key)) { missing.add(where + '.' + key); continue; }
    const f = k.fields.find(x => x.name === key);
    if (k.variants && k.discriminator && key !== k.discriminator) {
      const v = k.variants[node[k.discriminator]];
      if (!v || (!v.fields.includes(key))) missing.add(where + '.' + key + ' not in variant ' + node[k.discriminator]);
    }
    if (f.type === 'object') walk(f.kind, node[key], where + '.' + key);
    if (f.type === 'array' && f.kind) (node[key] || []).forEach((x, i) => walk(f.kind, x, where + '.' + key + '[' + i + ']'));
  }
}
const dir = path.join(ROOT, 'examples');
for (const file of fs.readdirSync(dir).filter(f => f.endsWith('.menu.json'))) {
  const d = JSON.parse(fs.readFileSync(path.join(dir, file), 'utf8')); docs++;
  walk('document', d, file);
  Object.entries(d.extras || {}).forEach(([k, x]) => walk('extra', x, file + ' extra ' + k));
  Object.entries(d.menus || {}).forEach(([k, m]) => walk('menu', m, file + ' menu ' + k));
}
check(docs > 3 && missing.size === 0, 'no property in ' + docs + ' example menus lacks a form field', [...missing].join('; '));

console.log('--- the generator refuses stale overrides');
const tmp = path.join(require('os').tmpdir(), 'hints-' + process.pid); fs.mkdirSync(tmp, { recursive: true });
fs.cpSync(path.join(ROOT, 'tools/schema-hints'), path.join(tmp, 'tools/schema-hints'), { recursive: true });
fs.cpSync(path.join(ROOT, 'schema'), path.join(tmp, 'schema'), { recursive: true });
fs.mkdirSync(path.join(tmp, 'Jellyfin.Plugin.DiscMenus/Web/editor'), { recursive: true });
const ov = JSON.parse(fs.readFileSync(path.join(tmp, 'tools/schema-hints/overrides.json'), 'utf8'));
ov.kinds.entry.fields.nonexistentField = { label: 'x' };
fs.writeFileSync(path.join(tmp, 'tools/schema-hints/overrides.json'), JSON.stringify(ov));
const bad2 = spawnSync(process.execPath, [path.join(tmp, 'tools/schema-hints/generate.js')], { encoding: 'utf8' });
check(bad2.status !== 0 && /nonexistentField/.test(bad2.stderr), 'an override for a field that does not exist is an error', bad2.stderr.trim().slice(0, 100));
console.log('failures: ' + fail); process.exit(fail ? 1 : 0);
