// The tile window: what is fetched (nearest first, bounded), how each answer is recorded (a missing file is never shown as
// empty space), when a failed fetch is retried, when tiles are evicted, and the GPU upload budget. A fake host API and a
// fake renderer stand in for the network and WebGL; the clock is a variable.

import assert from 'node:assert/strict';
import test from 'node:test';
import { Scene, STATES, normalizeState } from '../../../tools/ArcaneCore.Atlas/Web/js/scene.js';
import { boxChunk, encodeChunk, encodeEnvelope } from './support/atg1-writer.mjs';

const flush = () => new Promise((resolve) => setImmediate(resolve));
const envelope = (...chunks) => encodeEnvelope(chunks.map(encodeChunk)).buffer;
const box = (x, y, layer = 'terrain') => boxChunk({ layer, tile: [0, x, y], origin: [-9000, 0, 0], min: [-9001, 0, 0], max: [-9000, 1, 1], attributes: { 0: { colorKey: 'ground' } } });

function harness({ concurrency = 2 } = {}) {
    let now = 0;
    const calls = [];
    const api = {
        tile(layer, map, x, y, signal) {
            return new Promise((resolve, reject) => {
                const call = { layer, map, x, y, signal, resolve, reject };
                calls.push(call);
                signal.addEventListener('abort', () => reject(Object.assign(new Error('aborted'), { name: 'AbortError' })));
            });
        },
    };
    const renderer = {
        stats: { gpuBytes: 0 },
        created: [],
        destroyed: [],
        failNext: false,
        costMs: 0,
        createChunk(decoded) {
            if (this.failNext) {
                this.failNext = false;
                throw new Error('out of memory');
            }
            now += this.costMs;
            const chunk = { decoded, bytes: decoded.byteLength, triangleCount: decoded.triangleCount, lineCount: decoded.lineCount, bounds: { min: [-9001, 0, 0], max: [-9000, 1, 1] } };
            this.created.push(chunk);
            this.stats.gpuBytes += chunk.bytes;
            return chunk;
        },
        destroyChunk(chunk) {
            this.destroyed.push(chunk);
            this.stats.gpuBytes -= chunk.bytes;
        },
    };
    const scene = new Scene({ api, renderer, concurrency, clock: () => now });
    const changes = [];
    scene.on('change', () => changes.push(now));
    return { scene, renderer, calls, changes, tick: (ms) => { now += ms; }, now: () => now };
}

const WINDOW = { map: 0, focusTile: { x: 49, y: 31 }, radius: 1, layers: ['terrain'] };
const answer = (call, state, body, detail = '') => call.resolve({ state, detail, body });

test('states: header text maps to a known state', () => {
    assert.deepEqual(STATES, ['Ok', 'Empty', 'Missing', 'Rejected', 'Unknown', 'NotApplicable', 'Error']);
    assert.equal(normalizeState('ok'), 'Ok');
    assert.equal(normalizeState(' MISSING '), 'Missing');
    assert.equal(normalizeState('notapplicable'), 'NotApplicable');
    assert.equal(normalizeState('weird'), 'Unknown');
    assert.equal(normalizeState(null), 'Unknown');
});

test('the window is fetched nearest first, a few requests at a time', async () => {
    const { scene, calls } = harness({ concurrency: 2 });
    scene.update(WINDOW);
    assert.equal(calls.length, 2, 'only two requests are in flight');
    assert.deepEqual(calls.map((c) => [c.x, c.y]), [[-1, -1], [49, 31]], 'the global tile, then the tile under the camera');
    assert.equal(scene.busy, true);
    assert.equal(scene.statusOf('terrain', 49, 31).loading, true);
    assert.equal(scene.statusOf('terrain', 49, 31).state, 'Unknown');
    assert.equal(scene.statusOf('terrain', 1, 1), null, 'a tile nobody asked about has no status');

    answer(calls[0], 'Missing', envelope());
    await flush();
    assert.equal(calls.length, 3, 'a finished request makes room for the next');
    assert.deepEqual([calls[2].x, calls[2].y], [48, 31], 'the nearest neighbours follow');
    answer(calls[1], 'Ok', envelope(box(49, 31)));
    await flush();
    assert.equal(calls.length, 4);
    assert.equal(scene.statusOf('terrain', -1, -1).state, 'Missing');
    assert.equal(scene.statusOf('terrain', -1, -1).fetched, true);
    const ok = scene.statusOf('terrain', 49, 31);
    assert.deepEqual([ok.state, ok.primitives, ok.fetched], ['Ok', 24, true], '12 triangles and 12 edges');
    assert.equal(scene.entries.size, 0, 'nothing is on the GPU until pump runs');
});

test('decoded chunks are uploaded by pump and counted', async () => {
    const { scene, renderer, calls, changes } = harness({ concurrency: 1 });
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Empty', envelope());
    await flush();
    answer(calls[1], 'Ok', envelope(box(49, 31), box(49, 31)));
    await flush();
    assert.equal(scene.pump(10), false, 'everything fitted in the budget');
    assert.equal(renderer.created.length, 2);
    assert.equal(scene.chunksFor('terrain').length, 2);
    assert.equal(scene.chunksFor('nav.poly').length, 0);
    assert.equal(scene.allChunks().length, 2);
    assert.ok(changes.length >= 2);
    assert.deepEqual(scene.loadedBounds('terrain'), { min: [-9001, 0, 0], max: [-9000, 1, 1] });
    assert.equal(scene.loadedBounds('nav.poly'), null);
    assert.deepEqual(scene.loadedBounds(), scene.loadedBounds('terrain'), 'no layer given: every loaded chunk');

    const counts = scene.countsFor('terrain');
    assert.deepEqual([counts.Ok, counts.total], [1, 1]);
    assert.deepEqual(scene.totals(['terrain']), { loaded: 1, missing: 0, rejected: 0, empty: 0, errors: 0, triangles: 24, lines: 24, loading: 0 });
    assert.equal(scene.pump(10), false, 'nothing left to upload');
    assert.equal(scene.busy, false);
    assert.equal(await scene.idle(), true);
});

test('missing and empty tiles are remembered and not asked for again; failures are retried after five seconds', async () => {
    const { scene, calls, tick } = harness({ concurrency: 10 });
    scene.update({ ...WINDOW, radius: 0 });
    assert.equal(calls.length, 2);
    answer(calls[0], 'Missing', envelope(), 'no file');
    answer(calls[1], 'Empty', envelope());
    await flush();
    scene.update({ ...WINDOW, radius: 0 });
    assert.equal(calls.length, 2, 'a tile the host said is missing or empty is not fetched again');
    assert.deepEqual(scene.countsFor('terrain').Empty, 1);
    assert.equal(scene.statusOf('terrain', -1, -1).detail, 'no file');

    scene.update({ ...WINDOW, radius: 0, layers: ['terrain', 'liquid'] });
    assert.equal(calls.length, 4, 'a newly wanted layer is fetched');
    calls[2].reject(new Error('connection reset'));
    answer(calls[3], 'Ok', envelope(box(49, 31, 'liquid')));
    await flush();
    const failed = scene.statusOf('liquid', -1, -1);
    assert.deepEqual([failed.state, failed.detail], ['Error', 'connection reset']);
    assert.match(scene.errors.at(-1), /^liquid 0\/-1\/-1: connection reset$/);
    scene.update({ ...WINDOW, radius: 0, layers: ['terrain', 'liquid'] });
    assert.equal(calls.length, 4, 'a failed tile waits before it is retried');
    tick(4999);
    scene.update({ ...WINDOW, radius: 0, layers: ['terrain', 'liquid'] });
    assert.equal(calls.length, 4);
    tick(2);
    scene.update({ ...WINDOW, radius: 0, layers: ['terrain', 'liquid'] });
    assert.equal(calls.length, 5, 'and then tried again');
    assert.deepEqual([calls[4].layer, calls[4].x], ['liquid', -1]);
});

test('a chunk the UI refuses is an error with the reason, not an upload', async () => {
    const { scene, renderer, calls } = harness();
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Ok', Uint8Array.of(1, 0, 0, 0, 9, 9, 9, 9).buffer);
    answer(calls[1], 'Ok', envelope(box(49, 31)));
    await flush();
    const refused = scene.statusOf('terrain', -1, -1);
    assert.equal(refused.state, 'Error');
    assert.match(refused.detail, /^The host sent a chunk this UI refuses: /);
    assert.equal(scene.errors.length, 1);
    scene.pump(10);
    assert.equal(renderer.created.length, 1, 'the good tile still uploads');
});

test('tiles that leave the wanted set are aborted or dropped, and come back by refetching', async () => {
    const { scene, renderer, calls, tick } = harness({ concurrency: 3 });
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Missing', envelope());
    answer(calls[1], 'Ok', envelope(box(49, 31)));
    await flush();
    scene.pump(10);
    assert.equal(scene.allChunks().length, 1);

    // Moving away: the tile under the old camera is far outside the new window.
    tick(100);
    scene.update({ ...WINDOW, focusTile: { x: 40, y: 31 }, radius: 0 });
    assert.equal(renderer.destroyed.length, 0, 'a tile is kept for half a second after it stops being wanted');
    tick(600);
    scene.update({ ...WINDOW, focusTile: { x: 40, y: 31 }, radius: 0 });
    assert.equal(renderer.destroyed.length, 1, 'then its GPU memory is released');
    assert.equal(scene.statusOf('terrain', 49, 31), null, 'and it is forgotten, so coming back fetches it again');
    const before = calls.length;
    scene.update({ ...WINDOW, radius: 0 });
    assert.equal(calls.filter((c) => c.x === 49 && c.y === 31).length, 2, 'the tile is requested again');
    assert.ok(calls.length > before);

    // A request in flight for a tile that is no longer wanted is cancelled and leaves no status behind.
    const inflight = calls.at(-1);
    scene.update({ ...WINDOW, radius: 0, layers: [] });
    assert.equal(inflight.signal.aborted, true);
    await flush();
    assert.equal(scene.statusOf('terrain', inflight.x, inflight.y), null);
    assert.equal(scene.errors.length, 0, 'an abort is not an error');
});

test('a tile just outside the window is kept, one far away is not', async () => {
    const { scene, renderer, calls, tick } = harness({ concurrency: 4 });
    scene.update({ ...WINDOW, focusTile: { x: 49, y: 31 }, radius: 0 });
    answer(calls[0], 'Missing', envelope());
    answer(calls[1], 'Ok', envelope(box(49, 31)));
    await flush();
    scene.pump(10);
    tick(2000);
    scene.update({ ...WINDOW, focusTile: { x: 50, y: 31 }, radius: 0 });
    assert.equal(renderer.destroyed.length, 0, 'the neighbouring tile is within radius + 1 and stays loaded');
    scene.update({ ...WINDOW, focusTile: { x: 52, y: 31 }, radius: 0 });
    assert.equal(renderer.destroyed.length, 1, 'three tiles away it goes');
});

test('invalidating a tile drops it and fetches it again', async () => {
    const { scene, renderer, calls } = harness({ concurrency: 4 });
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Missing', envelope());
    answer(calls[1], 'Ok', envelope(box(49, 31)));
    await flush();
    scene.pump(10);
    scene.invalidate('terrain', 5, 49, 31);
    assert.equal(scene.allChunks().length, 1, 'another map\'s tile is not ours to drop');
    scene.invalidate('terrain', 0, 49, 31);
    assert.equal(scene.allChunks().length, 0);
    assert.equal(renderer.destroyed.length, 1);
    assert.equal(scene.statusOf('terrain', 49, 31), null);
    scene.update({ ...WINDOW, radius: 0 });
    assert.equal(calls.filter((c) => c.x === 49).length, 2, 'the next update refetches it');
    answer(calls.at(-1), 'Ok', envelope(box(49, 31)));
    await flush();
    scene.pump(10);
    scene.invalidateLayer('terrain');
    assert.equal(scene.entries.size, 0, 'invalidating a layer drops all of its tiles');
    scene.invalidateLayer('nothing');
});

test('peeked states fill in tiles nobody has fetched, and never overwrite a fetch', async () => {
    const { scene, calls, changes } = harness({ concurrency: 1 });
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Ok', envelope(box(-1, -1)));
    await flush();
    const before = changes.length;
    scene.mergePeek('terrain', {
        tiles: [
            { tile: [0, 48, 31], state: 'missing', detail: 'no file', primitives: 0 },
            { tile: [0, -1, -1], state: 'Missing' },
            { tile: [3, 1, 1], state: 'Ok' },
            { tile: [0, 49], state: 'Ok' },
            { tile: [0, 49, 31], state: 'Ok', primitives: '12' },
        ],
    });
    const peeked = scene.statusOf('terrain', 48, 31);
    assert.deepEqual([peeked.state, peeked.detail, peeked.peeked], ['Missing', 'no file', true]);
    assert.equal(scene.statusOf('terrain', -1, -1).state, 'Ok', 'a fetched tile keeps its answer');
    assert.equal(scene.statusOf('terrain', 49, 31).state, 'Unknown', 'a tile being fetched keeps its loading state');
    assert.equal(scene.statusOf('terrain', 1, 1), null, 'another map\'s tile is ignored');
    assert.equal(changes.length, before + 1);
    scene.mergePeek('terrain', null);
    scene.mergePeek('terrain', { tiles: 'x' });
    assert.equal(changes.length, before + 1, 'nothing merged, nothing announced');
});

test('changing map throws away the old map\'s tiles and ignores its late answers', async () => {
    const { scene, renderer, calls } = harness({ concurrency: 4 });
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Missing', envelope());
    answer(calls[1], 'Ok', envelope(box(49, 31)));
    await flush();
    scene.pump(10);
    const stale = calls.length;
    scene.update({ ...WINDOW, radius: 1 });
    const pending = calls.slice(stale);
    assert.ok(pending.length > 0);
    scene.update({ map: 1, focusTile: { x: 32, y: 32 }, radius: 0, layers: ['terrain'] });
    assert.equal(renderer.destroyed.length, 1, 'the old map\'s GPU chunks are released');
    assert.equal(scene.entries.size, 0);
    assert.ok(pending.every((c) => c.signal.aborted), 'its requests are cancelled');
    assert.equal(scene.statusOf('terrain', 49, 31), null);
    const fresh = calls.slice(stale + pending.length);
    assert.deepEqual(fresh.map((c) => c.map), [1, 1]);
    pending[0].resolve({ state: 'Ok', detail: '', body: envelope(box(49, 31)) });
    await flush();
    assert.equal(scene.uploads.length, 0, 'an answer for the old map is dropped');
    assert.equal(scene.errors.length, 0);
});

test('uploads stop at the time budget and carry on the next frame', async () => {
    const { scene, renderer, calls } = harness({ concurrency: 10 });
    renderer.costMs = 6;
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Ok', envelope(box(-1, -1), box(-1, -1)));
    answer(calls[1], 'Ok', envelope(box(49, 31), box(49, 31)));
    await flush();
    assert.equal(scene.pump(10), true, 'the budget ran out with chunks left');
    assert.ok(renderer.created.length >= 2 && renderer.created.length < 4);
    assert.equal(scene.busy, true, 'queued uploads count as work in progress');
    assert.equal(scene.pump(1000), false);
    assert.equal(renderer.created.length, 4);
    assert.equal(scene.busy, false);
});

test('a failed GPU upload is an error on the tile and frees what was created', async () => {
    const { scene, renderer, calls, tick } = harness({ concurrency: 10 });
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Missing', envelope());
    answer(calls[1], 'Ok', envelope(box(49, 31), box(49, 31)));
    await flush();
    let created = 0;
    const original = renderer.createChunk.bind(renderer);
    renderer.createChunk = (decoded) => {
        created++;
        if (created === 2) {
            throw new Error('out of memory');
        }
        return original(decoded);
    };
    scene.pump(10);
    const status = scene.statusOf('terrain', 49, 31);
    assert.deepEqual([status.state, status.detail], ['Error', 'GPU upload failed: out of memory']);
    assert.equal(renderer.destroyed.length, 1, 'the chunk already created for that tile is released');
    assert.equal(scene.allChunks().length, 0);
    assert.match(scene.errors.at(-1), /GPU upload failed/);
    const requests = calls.length;
    scene.update({ ...WINDOW, radius: 0 });
    assert.equal(calls.length, requests, 'a tile that failed to upload is not refetched on every update');
    tick(5001);
    scene.update({ ...WINDOW, radius: 0 });
    assert.equal(calls.length, requests + 1, 'it is retried after the wait');
});

test('GPU memory over the ceiling evicts the idle tiles that were wanted longest ago', async () => {
    const { scene, renderer, calls, tick } = harness({ concurrency: 10 });
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Missing', envelope());
    answer(calls[1], 'Ok', envelope(box(49, 31)));
    await flush();
    scene.pump(10);
    tick(10_000);
    renderer.stats.gpuBytes = 2 * 1024 ** 3;
    // The tile is wanted by nothing now but inside radius + 1, so only the memory pressure can drop it.
    scene.update({ ...WINDOW, focusTile: { x: 50, y: 31 }, radius: 0, layers: ['liquid'] });
    assert.equal(scene.entries.size, 0);
    assert.equal(renderer.destroyed.length, 1);
});

test('idle resolves when the work is done, or false when it is not', async () => {
    const { scene, calls } = harness({ concurrency: 10 });
    assert.equal(await scene.idle(), true, 'an empty scene is idle');
    scene.update({ ...WINDOW, radius: 0 });
    assert.equal(await scene.idle(20), false, 'unanswered requests time out');
    const done = scene.idle(5000);
    answer(calls[0], 'Missing', envelope());
    answer(calls[1], 'Missing', envelope());
    await flush();
    assert.equal(scene.busy, true, 'decoded answers waiting for pump still count as work');
    scene.pump(10);
    assert.equal(await done, true);
});

test('a tile that is decoded and waiting for pump is not fetched a second time', async () => {
    const { scene, calls } = harness({ concurrency: 10 });
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Missing', envelope());
    answer(calls[1], 'Ok', envelope(box(49, 31)));
    await flush();
    assert.equal(scene.allChunks().length, 0, 'nothing has been uploaded yet');
    scene.update({ ...WINDOW, radius: 0 });
    scene.update({ ...WINDOW, radius: 0 });
    assert.equal(calls.length, 2, 'camera updates between the answer and the upload do not repeat the request');
});

test('a rejected tile stays rejected until the host announces a change', async () => {
    const { scene, calls } = harness({ concurrency: 10 });
    scene.update({ ...WINDOW, radius: 0 });
    answer(calls[0], 'Missing', envelope());
    answer(calls[1], 'Rejected', envelope(), 'tile failed the reader');
    await flush();
    scene.pump(10);
    assert.equal(scene.statusOf('terrain', 49, 31).state, 'Rejected');
    assert.equal(scene.statusOf('terrain', 49, 31).detail, 'tile failed the reader');
    for (let i = 0; i < 5; i++) {
        scene.update({ ...WINDOW, radius: 0 });
    }
    assert.equal(calls.length, 2, 'rejection is the host\'s answer, not a failure to retry');
    assert.equal(scene.countsFor('terrain').Rejected, 1);
    scene.invalidate('terrain', 0, 49, 31);
    scene.update({ ...WINDOW, radius: 0 });
    assert.equal(calls.length, 3, 'a tile.invalidated event makes the tile worth asking about again');
});

test('layer information falls back to the built-in catalog', () => {
    const { scene } = harness();
    assert.equal(scene.infoOf('terrain').title, 'Terrain');
    assert.equal(scene.infoOf('nope'), null);
    scene.setLayerInfos([{ id: 'terrain', title: 'Ground' }]);
    assert.equal(scene.infoOf('terrain').title, 'Ground');
    assert.equal(scene.infoOf('liquid').title, 'Terrain liquid', 'layers the host list lacks still resolve');
});
