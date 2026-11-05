// Lane Y39: where the Local page gets textured terrain from (js/clientsource.js): the host first, the visitor's own client files
// (clientfs/index.js, injected here) when the host has none, and the reader module loaded only when a request needs it.
// Not run in the lane that wrote it (no CI in that lane); the integration gate is the first run.

import assert from 'node:assert/strict';
import test from 'node:test';
import { ApiError } from '../../../tools/ArcaneCore.Atlas/Web/js/api.js';
import { CLIENT_TEXTURE_PREFIX, ClientFilesSource, TEXTURED_TERRAIN_LAYER } from '../../../tools/ArcaneCore.Atlas/Web/js/clientsource.js';

const envelope = () => new Uint8Array(4).buffer;

function fixture({ host = () => ({ state: 'Ok', detail: null, body: envelope() }), local = () => ({ state: 'Ok', detail: null, body: envelope() }), loadFails = 0 } = {}) {
    const calls = { hostTiles: [], hostTextures: [], localTiles: [], localTextures: [], loads: 0, settings: 0, changes: 0 };
    const api = {
        async tile(layer, map, x, y) {
            calls.hostTiles.push([layer, map, x, y]);
            return host(layer);
        },
        async texture(key) {
            calls.hostTextures.push(key);
            return new Uint8Array([1]);
        },
    };
    let failures = loadFails;
    const status = { value: { state: 'none', folderName: '' }, fail: false };
    const timers = [];
    const reader = {
        localTileProvider: () => ({
            async tile(layer, map, x, y) {
                calls.localTiles.push([layer, map, x, y]);
                return local();
            },
        }),
        localTextureProvider: () => ({
            async texture(key) {
                calls.localTextures.push(key);
                return new Uint8Array([2]);
            },
        }),
        async openClientFilesSettings() {
            calls.settings++;
        },
        async clientFilesStatus() {
            if (status.fail) {
                throw new Error('the status could not be read');
            }
            return status.value;
        },
    };
    const source = new ClientFilesSource({
        api,
        onChange: () => calls.changes++,
        schedule: (fn) => timers.push(fn),
        loadReader: async () => {
            calls.loads++;
            if (failures > 0) {
                failures--;
                throw new Error('the module did not load');
            }
            return reader;
        },
    });
    return { source, calls, status, timers };
}

test('clientsource: other layers and host textures never load the reader', async () => {
    const { source, calls } = fixture();
    const tile = await source.tile('ref.terrain', 0, 33, 31);
    assert.equal(tile.state, 'Ok');
    await source.tile('ref.wmo.tex', 0, 1, 1);
    assert.deepEqual(await source.texture('client:Tileset\\Red.blp'), new Uint8Array([1]));
    assert.equal(calls.loads, 0, 'the reader module stays unloaded');
    assert.equal(calls.localTiles.length, 0);
    assert.deepEqual(calls.hostTextures, ['client:Tileset\\Red.blp']);
    assert.deepEqual(calls.hostTiles.map((t) => t[0]), ['ref.terrain', 'ref.wmo.tex']);
});

test('clientsource: a textured terrain tile the host answers is the host answer, and the reader is not asked', async () => {
    const { source, calls } = fixture({ host: () => ({ state: 'Ok', detail: 'host', body: envelope() }) });
    const tile = await source.tile(TEXTURED_TERRAIN_LAYER, 0, 33, 31);
    assert.equal(tile.detail, 'host');
    assert.equal(calls.loads, 0);
    const empty = fixture({ host: () => ({ state: 'Empty', detail: 'nothing', body: envelope() }) });
    assert.equal((await empty.source.tile(TEXTURED_TERRAIN_LAYER, 0, 1, 1)).state, 'Empty', 'Empty is an answer too');
    assert.equal(empty.calls.loads, 0);
});

test('clientsource: when the host has no textured terrain the visitor files answer', async () => {
    for (const hostState of ['NotApplicable', 'Missing', 'Rejected']) {
        const { source, calls } = fixture({
            host: () => ({ state: hostState, detail: 'host says no', body: envelope() }),
            local: () => ({ state: 'Ok', detail: 'local', body: envelope() }),
        });
        const tile = await source.tile(TEXTURED_TERRAIN_LAYER, 0, 33, 31);
        assert.equal(tile.detail, 'local', `after a host ${hostState}`);
        assert.deepEqual(calls.localTiles, [[TEXTURED_TERRAIN_LAYER, 0, 33, 31]]);
    }

    const unknown = fixture({
        host: () => {
            throw new ApiError('unknown layer', { status: 404, code: 'unknown-layer' });
        },
    });
    assert.equal((await unknown.source.tile(TEXTURED_TERRAIN_LAYER, 0, 1, 1)).state, 'Ok', 'a host that does not know the layer (404) falls back');
});

test('clientsource: with no files chosen the host reason is kept, or an empty NotApplicable envelope when the host never answered', async () => {
    const none = () => ({ state: 'NotApplicable', detail: 'No client files were chosen.', body: envelope() });
    const withHost = fixture({ host: () => ({ state: 'Missing', detail: 'host missing', body: envelope() }), local: none });
    const kept = await withHost.source.tile(TEXTURED_TERRAIN_LAYER, 0, 1, 1);
    assert.equal(kept.state, 'Missing');
    assert.equal(kept.detail, 'host missing');

    const withoutHost = fixture({
        host: () => {
            throw new ApiError('unknown layer', { status: 404 });
        },
        local: none,
    });
    const answer = await withoutHost.source.tile(TEXTURED_TERRAIN_LAYER, 0, 1, 1);
    assert.equal(answer.state, 'NotApplicable');
    assert.equal(answer.detail, 'No client files were chosen.');
    assert.equal(answer.body.byteLength, 4, 'a zero-part envelope the scene can split');
    assert.equal(new DataView(answer.body).getUint32(0, true), 0);
});

test('clientsource: a host failure that is not "unknown layer" is not hidden by the fallback', async () => {
    const { source, calls } = fixture({
        host: () => {
            throw new ApiError('server error', { status: 500 });
        },
    });
    await assert.rejects(() => source.tile(TEXTURED_TERRAIN_LAYER, 0, 1, 1), (error) => error instanceof ApiError && error.status === 500);
    assert.equal(calls.loads, 0);
    const other = fixture({
        host: () => {
            throw new TypeError('network');
        },
    });
    await assert.rejects(() => other.source.tile(TEXTURED_TERRAIN_LAYER, 0, 1, 1), TypeError);
});

test('clientsource: baked terrain textures come from the reader, every other key from the host', async () => {
    const { source, calls } = fixture();
    assert.equal(CLIENT_TEXTURE_PREFIX, 'cft:');
    assert.deepEqual(await source.texture('cft:0/33/31/4', { maxSize: 0 }), new Uint8Array([2]));
    assert.deepEqual(calls.localTextures, ['cft:0/33/31/4']);
    assert.deepEqual(await source.texture('gen:terrain/0'), new Uint8Array([1]));
    assert.deepEqual(calls.hostTextures, ['gen:terrain/0']);
    assert.equal(calls.loads, 1);
});

test('clientsource: the reader module loads once, a failed load is retried, and settings open through it', async () => {
    const { source, calls } = fixture({ host: () => ({ state: 'NotApplicable', detail: 'x', body: envelope() }), loadFails: 1 });
    await assert.rejects(() => source.tile(TEXTURED_TERRAIN_LAYER, 0, 1, 1), /did not load/);
    assert.equal((await source.tile(TEXTURED_TERRAIN_LAYER, 0, 1, 1)).state, 'Ok', 'the second request retries the import');
    await source.tile(TEXTURED_TERRAIN_LAYER, 0, 2, 2);
    await source.texture('cft:0/1/1/1');
    await source.openSettings();
    assert.equal(calls.loads, 2, 'one failed load and one that stuck');
    assert.equal(calls.settings, 1);
});

test('clientsource: after the settings opened, a changed folder choice is reported once, an unchanged one is watched and then dropped', async () => {
    const { source, calls, status, timers } = fixture();
    await source.openSettings();
    assert.equal(timers.length, 1, 'one poll is scheduled');
    await source.openSettings();
    assert.equal(timers.length, 1, 'a second open does not start a second watcher');

    await timers.shift()();
    assert.equal(calls.changes, 0, 'nothing changed yet');
    assert.equal(timers.length, 1, 'the next poll is scheduled');

    status.value = { state: 'ready', folderName: 'WoW' };
    await timers.shift()();
    assert.equal(calls.changes, 1, 'the choice changed: the page is told');
    assert.equal(timers.length, 0, 'and the watcher stops');

    await source.openSettings();
    status.value = { state: 'ready', folderName: 'WoW2' };
    assert.equal(timers.length, 1, 'a later open watches again');
    await timers.shift()();
    assert.equal(calls.changes, 2, 'a different folder name counts as a change');
});

test('clientsource: a status read that fails neither stops the watcher nor reports a change', async () => {
    const { source, calls, status, timers } = fixture();
    await source.openSettings();
    status.fail = true;
    await timers.shift()();
    assert.equal(calls.changes, 0, 'a failed read is not a change');
    assert.equal(timers.length, 1, 'the watcher polls again');
    status.fail = false;
    status.value = { state: 'ready', folderName: 'WoW' };
    await timers.shift()();
    assert.equal(calls.changes, 1);
});
