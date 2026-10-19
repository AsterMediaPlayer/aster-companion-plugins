// Runs every *.test.js in this folder (or just those whose name contains an argument, e.g.
// `npm test -- editor`) in its own process, and fails if any check fails or any suite crashes.
// Each suite prints PASS / FAIL lines and a final "failures: N" line.
const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const filter = process.argv[2];
const files = fs.readdirSync(__dirname)
    .filter((f) => f.endsWith('.test.js') && (!filter || f.includes(filter)))
    .sort();

if (files.length === 0) {
    console.error('No test files matched.');
    process.exit(1);
}

let passed = 0;
let failedChecks = 0;
const broken = [];
for (const file of files) {
    const run = spawnSync(process.execPath, [path.join(__dirname, file)], { encoding: 'utf8', timeout: 180000 });
    const output = (run.stdout || '') + (run.stderr || '');
    const pass = (output.match(/^PASS/gm) || []).length;
    const fail = (output.match(/^FAIL/gm) || []).length;
    const summary = /^failures: (\d+)/m.exec(output);
    // A suite that didn't reach its summary line crashed or timed out: that is a failure too.
    const ok = summary !== null && Number(summary[1]) === 0 && fail === 0;
    passed += pass;
    failedChecks += fail;
    console.log(`${ok ? 'ok  ' : 'FAIL'}  ${file.padEnd(32)} ${pass} checks`);
    if (!ok) {
        broken.push(file);
        const detail = output.split('\n').filter((l) => /^FAIL|Error|error/.test(l)).slice(0, 12).join('\n');
        console.log(detail || output.split('\n').slice(-12).join('\n'));
    }
}

console.log(`\n${files.length - broken.length}/${files.length} suites passed, ${passed} checks passed, ${failedChecks} failed.`);
process.exit(broken.length === 0 ? 0 : 1);
