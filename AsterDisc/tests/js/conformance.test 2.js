// The conformance vectors (conformance/*.json) must match what the reference renderer produces now. If this fails, the renderer's behaviour
// changed: either that was a mistake, or regenerate the vectors on purpose with `node conformance/generate.js` and review the diff.
const path = require('path');
const fs = require('fs');
const { spawnSync } = require('child_process');
const ROOT = path.resolve(__dirname, '..', '..');
let fail = 0; const check = (ok, m, e) => { if (!ok) fail++; console.log(ok ? 'PASS' : 'FAIL', m, e === undefined ? '' : e); };

const run = spawnSync(process.execPath, [path.join(ROOT, 'conformance/generate.js'), '--check'], { encoding: 'utf8' });
check(run.status === 0, 'the committed vectors match the reference renderer', (run.stdout + run.stderr).trim());

for (const [file, min] of [['paginate', 100], ['cells', 100], ['layout', 20], ['focus', 40], ['time-format', 10], ['safety', 40]]) {
  const j = JSON.parse(fs.readFileSync(path.join(ROOT, 'conformance', file + '.json'), 'utf8'));
  check(j.conformanceVersion === 1 && Array.isArray(j.cases) && j.cases.length >= min && typeof j.description === 'string', file + '.json is well formed with enough cases', j.cases && j.cases.length);
}
console.log('failures:', fail);
process.exit(fail ? 1 : 0);
