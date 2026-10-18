// Runs channel-tags.js against the Aster app's own vectors, so the plugin
// reads and writes channel tags exactly as the app does.
//
//   node --test tests/
//
// The vectors are the app's AsterFINTests/Conformance/channel-tags.json,
// copied here as tests/channel-tags.json; tools/sync-vectors.sh copies them
// again from a checkout of the app.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const tags = require('../Jellyfin.Plugin.AsterChannels/Web/channel-tags.js');

const vectors = JSON.parse(fs.readFileSync(path.join(__dirname, 'channel-tags.json'), 'utf8'));

test('the vectors are there', () => {
    assert.ok(vectors.cases.length >= 10);
});

for (const vector of vectors.cases) {
    test(vector.name, () => {
        const settings = tags.read(vector.tags);
        assert.strictEqual(settings.isChannel, vector.isChannel);
        assert.deepStrictEqual(tags.applied(settings, vector.tags), vector.written);
        // Writing is settled: what was written writes the same again.
        assert.deepStrictEqual(tags.applied(tags.read(vector.written), vector.written), vector.written);
    });
}

test('filler playlists', () => {
    assert.ok(tags.isFillerPlaylist(['aster:filler']));
    assert.ok(tags.isFillerPlaylist(['Asterfin:Filler=on']));
    assert.ok(!tags.isFillerPlaylist(['aster:filler=off']));
    assert.ok(!tags.isFillerPlaylist(['aster:channel', 'filler']));
});

test('numbers carry on after the highest', () => {
    assert.deepStrictEqual(
        tags.numbers([{ id: 'a', name: 'Zed', number: null }, { id: 'b', name: 'Bee', number: 7 }, { id: 'c', name: 'Ant', number: null }]),
        { b: 7, c: 8, a: 9 }
    );
});
