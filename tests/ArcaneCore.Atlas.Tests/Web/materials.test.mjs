// Lane A1 in the browser modules: the ATG1 v2 and ATX1 decoders held to the bytes the C# writers produce, the texture store's
// memory accounting (with a recording fake GL), and the material registry. The test names in the first two describe-blocks are
// checked by WebScriptTests, so renaming one is a change there too. Synthetic data only; nothing here is a client file.

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { Atg1Error, decodeChunk } from '../../../tools/ArcaneCore.Atlas/Web/js/atg1.js';
import { Atx1Error, FORMAT, decodeTexture } from '../../../tools/ArcaneCore.Atlas/Web/js/atx1.js';
import { MaterialRegistry, createMaterialRegistry, materialKind, registerMaterialKind } from '../../../tools/ArcaneCore.Atlas/Web/js/materials/index.js';
import { TextureStore } from '../../../tools/ArcaneCore.Atlas/Web/js/materials/textures.js';
import { encodeChunkV2, trailerOffset } from './support/atg2-writer.mjs';
import { encodeTexture, probeTexture } from './support/atx1-writer.mjs';
import { FakeGl } from './support/fakegl.mjs';
import { loadGolden, readGoldenBytes } from './support/golden.mjs';

const WEB = fileURLToPath(new URL('../../../tools/ArcaneCore.Atlas/Web/', import.meta.url));

function sameArray(actual, expected, what) {
    assert.ok(actual, `${what}: missing`);
    assert.equal(actual.length, expected.length, `${what}: length`);
    for (let i = 0; i < expected.length; i++) {
        assert.ok(Object.is(actual[i], expected[i]) || actual[i] === expected[i], `${what}[${i}]: ${actual[i]} !== ${expected[i]}`);
    }
    return expected.length;
}

const STREAM_NAMES = ['normals', 'uv0', 'uv1', 'color0', 'color1', 'boneIndices', 'boneWeights', 'submesh'];

/** The chunk as plain arrays for encodeChunkV2, from what the decoder returned. */
function asEncoderInput(chunk, expect) {
    const input = {
        layer: chunk.layer,
        tile: [chunk.tile.map, chunk.tile.x, chunk.tile.y],
        part: chunk.part,
        origin: chunk.origin,
        positions: chunk.positions,
        triangles: chunk.triangles,
        triangleAttributes: chunk.triangleAttributes,
        batches: expect.batches,
        materials: expect.materials,
        attributes: Object.fromEntries([...chunk.attributes].map(([k, v]) => [String(k), v])),
        stats: chunk.stats,
        model: expect.model ?? undefined,
    };
    for (const name of STREAM_NAMES) {
        if (expect[name]) {
            input[name] = chunk[name];
        }
    }
    return input;
}

test('ATG1 v2 decoder matches GeometryWire.Write', async (t) => {
    const golden = loadGolden();
    const entries = golden.v2[0].chunks;
    assert.ok(entries.length >= 3, `only ${entries.length} v2 golden chunks`);
    let compared = 0;

    await t.test('every v2 chunk decodes to what the C# writer was given', () => {
        for (const entry of entries) {
            const bytes = readGoldenBytes(entry.file);
            const chunk = decodeChunk(bytes);
            const e = entry.expect;
            const what = entry.name;
            assert.equal(chunk.version, 2, `${what}: version`);
            assert.equal(chunk.layer, e.layer, `${what}: layer`);
            assert.equal(chunk.byteLength, e.byteLength, `${what}: byte length`);
            assert.equal(chunk.byteLength, bytes.length, `${what}: file length`);
            assert.equal(chunk.vertexCount, e.vertexCount, `${what}: vertices`);
            assert.equal(chunk.triangleCount, e.triangleCount, `${what}: triangles`);
            compared += sameArray(chunk.positions, e.positions, `${what}: positions`);
            compared += sameArray(chunk.triangles, e.triangles, `${what}: indices`);
            compared += sameArray(chunk.triangleAttributes, e.triangleAttributes, `${what}: triangle attributes`);
            for (const name of STREAM_NAMES) {
                if (e[name] === null) {
                    assert.equal(chunk[name] ?? null, null, `${what}: ${name} should be absent`);
                } else {
                    compared += sameArray(chunk[name], e[name], `${what}: ${name}`);
                }
            }
            assert.deepEqual(chunk.batches.map((b) => [b.firstTriangle, b.triangleCount, b.material, b.tag]), e.batches, `${what}: batches`);
            assert.deepEqual(chunk.materials, e.materials, `${what}: materials`);
            assert.deepEqual(chunk.model ?? null, e.model, `${what}: model`);
        }
        assert.ok(compared > 100, `only ${compared} values compared`);
    });

    await t.test('a v1 chunk still decodes as version 1 with no v2 members', () => {
        const chunk = decodeChunk(readGoldenBytes('terrain.bin'));
        assert.equal(chunk.version, 1);
        for (const name of STREAM_NAMES) {
            assert.equal(chunk[name] ?? null, null, name);
        }
        assert.ok(!chunk.batches || chunk.batches.length === 0);
    });

    await t.test('the batch ranges and material indices are within the chunk', () => {
        for (const entry of entries) {
            const chunk = decodeChunk(readGoldenBytes(entry.file));
            for (const b of chunk.batches) {
                assert.ok(b.firstTriangle + b.triangleCount <= chunk.triangleCount, `${entry.name}: batch past the triangles`);
                assert.ok(b.material < chunk.materials.length, `${entry.name}: material index`);
            }
        }
    });
});

test('ATG1 v2 test writer is byte-identical to GeometryWire.Write', () => {
    const golden = loadGolden();
    let compared = 0;
    for (const entry of golden.v2[0].chunks) {
        const bytes = readGoldenBytes(entry.file);
        const chunk = decodeChunk(bytes);
        const written = encodeChunkV2(asEncoderInput(chunk, entry.expect));
        const cut = trailerOffset(bytes);
        assert.equal(trailerOffset(written), cut, `${entry.name}: trailer position`);
        assert.deepEqual(written.subarray(0, cut), bytes.subarray(0, cut), `${entry.name}: every byte before the JSON`);
        const json = (b) => JSON.parse(new TextDecoder().decode(b.subarray(cut + 4)));
        const ours = json(written);
        const theirs = json(bytes);
        assert.deepEqual(ours.materials, theirs.materials, `${entry.name}: materials`);
        assert.deepEqual(ours.model ?? null, theirs.model ?? null, `${entry.name}: model`);
        assert.deepEqual(ours.attributes, theirs.attributes, `${entry.name}: attributes`);
        compared += cut;
    }
    assert.ok(compared > 500, `only ${compared} bytes compared`);
});

test('ATX1 decoder matches TextureWire.Write', async (t) => {
    const golden = loadGolden();
    const entries = golden.textures.entries;
    assert.ok(entries.length >= 4, `only ${entries.length} texture goldens`);

    await t.test('every texture decodes to what the C# writer was given', () => {
        for (const entry of entries) {
            const bytes = readGoldenBytes(entry.file);
            const texture = decodeTexture(bytes);
            const e = entry.expect;
            const what = entry.name;
            assert.equal(texture.format, e.format, `${what}: format`);
            assert.equal(texture.width, e.width, `${what}: width`);
            assert.equal(texture.height, e.height, `${what}: height`);
            assert.equal(texture.layers, e.layers, `${what}: layers`);
            assert.equal(texture.flags, e.flags, `${what}: flags`);
            assert.equal(texture.levels.length, e.levels.length, `${what}: level count`);
            texture.levels.forEach((level, i) => assert.deepEqual([...level], e.levels[i], `${what}: level ${i}`));
            assert.deepEqual(texture.meta, e.meta, `${what}: meta`);
        }
    });

    await t.test('format numbers are the documented ones', () => {
        assert.deepEqual(FORMAT, { RGBA8: 1, DXT1: 2, DXT3: 3, DXT5: 4, R8: 5 });
    });
});

test('ATX1 test writer is byte-identical to TextureWire.Write', () => {
    const golden = loadGolden();
    let compared = 0;
    for (const entry of golden.textures.entries) {
        const bytes = readGoldenBytes(entry.file);
        const texture = decodeTexture(bytes);
        const written = encodeTexture(texture);
        const jsonBytes = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(16, true);
        const cut = bytes.length - jsonBytes;
        assert.deepEqual(written.subarray(0, cut), bytes.subarray(0, cut), `${entry.name}: every byte before the JSON`);
        assert.deepEqual(JSON.parse(new TextDecoder().decode(written.subarray(cut))), JSON.parse(new TextDecoder().decode(bytes.subarray(cut))), `${entry.name}: json`);
        compared += cut;
    }
    assert.ok(compared > 100, `only ${compared} bytes compared`);
});

test('corrupt ATG1 v2 and ATX1 files are refused by the browser decoders', async (t) => {
    const golden = loadGolden();
    assert.equal(golden.v2[0].corrupt.length, 7);
    assert.equal(golden.textures.corrupt.length, 7);

    await t.test('every corrupt v2 chunk throws Atg1Error', () => {
        for (const entry of golden.v2[0].corrupt) {
            assert.throws(() => decodeChunk(readGoldenBytes(entry.file)), Atg1Error, entry.name);
        }
    });

    await t.test('every corrupt texture throws Atx1Error', () => {
        for (const entry of golden.textures.corrupt) {
            assert.throws(() => decodeTexture(readGoldenBytes(entry.file)), Atx1Error, entry.name);
        }
    });

    await t.test('every truncation of a texture and of a v2 chunk is refused', () => {
        const texture = readGoldenBytes(golden.textures.entries[0].file);
        for (let length = 0; length < texture.length; length++) {
            assert.throws(() => decodeTexture(texture.subarray(0, length)), Atx1Error, `texture cut at ${length}`);
        }
        const chunk = readGoldenBytes(golden.v2[0].chunks[1].file);
        for (let length = 0; length < chunk.length; length++) {
            assert.throws(() => decodeChunk(chunk.subarray(0, length)), Atg1Error, `chunk cut at ${length}`);
        }
    });

    await t.test('an unaligned buffer decodes the same as an aligned one', () => {
        const bytes = readGoldenBytes(golden.v2[0].chunks[0].file);
        const shifted = new Uint8Array(bytes.length + 3);
        shifted.set(bytes, 3);
        const a = decodeChunk(bytes);
        const b = decodeChunk(shifted.subarray(3));
        assert.deepEqual([...b.positions], [...a.positions]);
        assert.deepEqual([...b.uv0], [...a.uv0]);
    });
});

// ---------------------------------------------------------------------------------------------------------------------------
// TextureStore

function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((res, rej) => {
        resolve = res;
        reject = rej;
    });
    return { promise, resolve, reject };
}

/** A fetch function the test controls: every call is recorded and waits until the test settles it. */
function controlledFetch() {
    const calls = [];
    const fn = (key, options) => {
        const d = deferred();
        calls.push({ key, options, ...d });
        return d.promise;
    };
    fn.calls = calls;
    return fn;
}

/** A texture of w x h RGBA8 with no mip chain: w*h*4 bytes. */
const rgba = (w, h) => encodeTexture({ format: FORMAT.RGBA8, width: w, height: h, levels: [new Uint8Array(w * h * 4)], meta: {} });

const tick = () => new Promise((resolve) => setImmediate(resolve));

test('TextureStore returns a placeholder at once, then the texture when it arrives', async () => {
    const gl = new FakeGl();
    const fetchTexture = controlledFetch();
    const store = new TextureStore({ gl, fetchTexture, budgetBytes: 1 << 20 });

    store.beginFrame();
    const first = store.get('client:a');
    store.endFrame();
    assert.equal(first.ready, false);
    assert.ok(first.texture, 'a placeholder texture must be usable immediately');
    const standIn = first.texture; // the entry is live: it is updated in place when the texture arrives
    assert.equal(fetchTexture.calls.length, 1);

    fetchTexture.calls[0].resolve(rgba(4, 4));
    await store.whenIdle();
    store.beginFrame();
    const second = store.get('client:a');
    store.endFrame();
    assert.equal(second.ready, true);
    assert.notEqual(second.texture, standIn);
    assert.equal(gl.uploads.at(-1).width, 4);
    store.dispose();
});

test('TextureStore fetches a key once however many batches ask for it while it is in flight', async () => {
    const gl = new FakeGl();
    const fetchTexture = controlledFetch();
    const store = new TextureStore({ gl, fetchTexture, budgetBytes: 1 << 20 });

    store.beginFrame();
    for (let i = 0; i < 50; i++) {
        store.get('client:same');
    }
    store.endFrame();
    store.beginFrame();
    store.get('client:same');
    store.endFrame();

    assert.equal(fetchTexture.calls.length, 1);
    assert.equal(store.stats().inFlight, 1);
    fetchTexture.calls[0].resolve(rgba(2, 2));
    await store.whenIdle();
    assert.equal(store.stats().fetches, 1);
    store.dispose();
});

test('TextureStore runs at most four fetches at a time and finishes them all', async () => {
    const gl = new FakeGl();
    let running = 0;
    let peak = 0;
    const fetchTexture = async () => {
        running++;
        peak = Math.max(peak, running);
        await tick();
        await tick();
        running--;
        return rgba(2, 2);
    };
    const store = new TextureStore({ gl, fetchTexture, budgetBytes: 1 << 20 });

    store.beginFrame();
    for (let i = 0; i < 20; i++) {
        store.get(`client:t${i}`);
    }
    store.endFrame();
    await store.whenIdle();

    assert.equal(peak, 4);
    assert.equal(store.stats().resident, 20);
    assert.equal(store.stats().fetches, 20);
    store.dispose();
});

test('TextureStore keeps its resident bytes under the budget and deletes what it evicts', async () => {
    const gl = new FakeGl();
    const MB = 1024 * 1024;
    const fetchTexture = async () => rgba(512, 512); // 1 MiB each
    const store = new TextureStore({ gl, fetchTexture, budgetBytes: 4 * MB });

    for (let i = 0; i < 20; i++) {
        store.beginFrame();
        store.get(`client:big${i}`);
        store.endFrame();
        await store.whenIdle();
        store.beginFrame();
        store.endFrame();
        assert.ok(store.stats().bytes <= 4 * MB, `frame ${i}: ${store.stats().bytes} bytes resident`);
    }

    const stats = store.stats();
    assert.ok(stats.resident <= 4, `${stats.resident} textures resident under a 4 MiB budget of 1 MiB textures`);
    assert.ok(stats.evictions >= 16, `${stats.evictions} evictions`);
    assert.equal(gl.deleted, stats.evictions, 'every eviction deletes its GL texture');
    assert.equal(gl.live.size - 1, stats.resident, 'live GL textures are the resident ones plus the placeholder');
    store.dispose();
});

test('TextureStore evicts the least recently used texture first', async () => {
    const gl = new FakeGl();
    const MB = 1024 * 1024;
    const store = new TextureStore({ gl, fetchTexture: async () => rgba(512, 512), budgetBytes: 3 * MB });
    for (const key of ['client:a', 'client:b', 'client:c']) {
        store.beginFrame();
        store.get(key);
        store.endFrame();
        await store.whenIdle();
    }
    store.beginFrame();
    store.get('client:a'); // touched again: b is now the oldest
    store.endFrame();
    store.beginFrame();
    store.get('client:d');
    store.endFrame();
    await store.whenIdle();
    store.beginFrame(); // an empty frame: d has arrived, 4 MiB is over the 3 MiB budget, the least recently used goes
    store.endFrame();
    store.beginFrame();
    const a = store.get('client:a').ready;
    const b = store.get('client:b').ready;
    store.endFrame();

    assert.equal(a, true, 'a was used last frame and stays');
    assert.equal(b, false, 'b was the least recently used and went');
    store.dispose();
});

test('TextureStore does not evict textures used one frame ago to make room for a generated array, but does once they are two frames idle', async () => {
    const gl = new FakeGl();
    const MB = 1024 * 1024;
    const store = new TextureStore({ gl, fetchTexture: async () => rgba(512, 512), budgetBytes: 3 * MB });
    for (const key of ['client:a', 'client:b', 'client:c']) {
        store.beginFrame();
        store.get(key);
        store.endFrame();
        await store.whenIdle();
    }
    store.beginFrame();
    for (const key of ['client:a', 'client:b', 'client:c']) {
        store.get(key);
    }
    store.endFrame();
    store.beginFrame(); // all three were used one frame ago and the budget is full
    assert.equal(store.acquireArray('gen:one-ago', { width: 16, height: 16 }), null, 'one-frame-old textures are pinned');
    assert.equal(store.stats().evictions, 0);
    store.endFrame();
    store.beginFrame(); // now they are two frames idle
    assert.ok(store.acquireArray('gen:two-ago', { width: 16, height: 16 }), 'two-frame-idle textures give way');
    assert.equal(store.stats().evictions, 1);
    store.endFrame();
    store.dispose();
});

test('TextureStore keeps a texture used one frame ago over one unused for two frames or more', async () => {
    const gl = new FakeGl();
    const MB = 1024 * 1024;
    const store = new TextureStore({ gl, fetchTexture: async () => rgba(512, 512), budgetBytes: 3 * MB });
    for (const key of ['client:a', 'client:b', 'client:c']) {
        store.beginFrame();
        store.get(key);
        store.endFrame();
        await store.whenIdle();
    }
    store.beginFrame();
    store.get('client:b'); // b was used one frame before the eviction, c two, a four
    store.endFrame();
    store.beginFrame();
    store.get('client:d');
    store.endFrame();
    await store.whenIdle();
    store.beginFrame(); // d has arrived: 4 MiB against 3 MiB, exactly one texture goes and it is a, the one idle longest
    store.endFrame();

    const stats = store.stats();
    assert.equal(stats.resident, 3);
    assert.equal(stats.evictions, 1);
    store.beginFrame();
    const ready = ['a', 'b', 'c', 'd'].map((k) => store.get(`client:${k}`).ready);
    store.endFrame();
    assert.deepEqual(ready, [false, true, true, true]);
    store.dispose();
});

test('TextureStore fetches each texture at most twice over 100 static frames', async () => {
    const MB = 1024 * 1024;
    const counts = new Map();
    const run = async (budget, textureSide) => {
        counts.clear();
        const gl = new FakeGl();
        const store = new TextureStore({
            gl,
            budgetBytes: budget,
            fetchTexture: async (key) => {
                counts.set(key, (counts.get(key) ?? 0) + 1);
                return rgba(textureSide, textureSide);
            },
        });
        for (let frame = 0; frame < 100; frame++) {
            store.beginFrame();
            for (let i = 0; i < 20; i++) {
                store.get(`client:s${i}`);
            }
            store.endFrame();
            await tick();
            await tick();
        }
        await store.whenIdle();
        const peak = store.stats().bytes;
        store.dispose();
        return peak;
    };

    const fits = await run(4 * MB, 128); // 20 x 64 KiB = 1.25 MiB: everything fits, so everything is fetched exactly once
    assert.equal(counts.size, 20);
    assert.ok([...counts.values()].every((n) => n === 1), `a working set that fits was refetched: ${JSON.stringify([...counts])}`);
    assert.ok(fits <= 4 * MB);

    const slightlyOver = await run(4 * MB, 256); // 20 x 256 KiB = 5 MiB against a 4 MiB budget
    assert.ok([...counts.values()].every((n) => n <= 2), `a slightly over-budget working set refetched: ${JSON.stringify([...counts])}`);
    assert.ok(slightlyOver <= 4 * MB, `${slightlyOver} bytes resident`);

    const farOver = await run(4 * MB, 512); // 20 x 1 MiB = 20 MiB: must not thrash
    assert.ok([...counts.values()].every((n) => n <= 2), `an over-budget working set thrashed: ${JSON.stringify([...counts])}`);
    assert.ok(farOver <= 4 * MB, `${farOver} bytes resident`);
});

test('TextureStore remembers a missing texture and does not ask again', async () => {
    const gl = new FakeGl();
    let calls = 0;
    const fetchTexture = async () => {
        calls++;
        throw Object.assign(new Error('not found'), { status: 404 });
    };
    const store = new TextureStore({ gl, fetchTexture, budgetBytes: 1 << 20 });

    for (let frame = 0; frame < 30; frame++) {
        store.beginFrame();
        const entry = store.get('client:nothing');
        store.endFrame();
        assert.ok(entry.texture);
        await tick();
    }

    assert.equal(calls, 1);
    assert.equal(store.stats().missing, 1);
    store.beginFrame();
    assert.equal(store.get('client:nothing').missing, true);
    store.endFrame();
    store.dispose();
});

test('TextureStore treats a corrupt texture as missing instead of throwing', async () => {
    const gl = new FakeGl();
    const store = new TextureStore({ gl, fetchTexture: async () => Uint8Array.from([1, 2, 3]), budgetBytes: 1 << 20 });

    store.beginFrame();
    store.get('client:bad');
    store.endFrame();
    await store.whenIdle();

    assert.equal(store.stats().missing, 1);
    assert.equal(store.stats().resident, 0);
    store.dispose();
});

test('TextureStore asks for DXT only when the S3TC extension exists, and uploads it compressed', async () => {
    const dxtBytes = encodeTexture({ format: FORMAT.DXT1, width: 4, height: 4, flags: 1, levels: [new Uint8Array(8)], meta: {} });
    const withS3tc = new FakeGl({ s3tc: true });
    const asked = [];
    const a = new TextureStore({ gl: withS3tc, budgetBytes: 1 << 20, fetchTexture: async (key, options) => (asked.push(options.fmt), dxtBytes) });
    a.beginFrame();
    a.get('client:dxt');
    a.endFrame();
    await a.whenIdle();
    assert.deepEqual(asked, ['dxt']);
    assert.equal(withS3tc.compressedUploads.length, 1);
    assert.equal(withS3tc.compressedUploads[0].internalFormat, 0x83F1);
    assert.equal(withS3tc.compressedUploads[0].bytes, 8);
    a.dispose();

    // The same DXT1 block without the hasAlpha flag is opaque: it takes the RGB format.
    const opaqueBytes = encodeTexture({ format: FORMAT.DXT1, width: 4, height: 4, flags: 0, levels: [new Uint8Array(8)], meta: {} });
    const rgbGl = new FakeGl({ s3tc: true });
    const c = new TextureStore({ gl: rgbGl, budgetBytes: 1 << 20, fetchTexture: async () => opaqueBytes });
    c.beginFrame();
    c.get('client:opaque');
    c.endFrame();
    await c.whenIdle();
    assert.equal(rgbGl.compressedUploads.length, 1);
    assert.equal(rgbGl.compressedUploads[0].internalFormat, 0x83F0);
    c.dispose();

    const without = new FakeGl({ s3tc: false });
    const asked2 = [];
    const b = new TextureStore({ gl: without, budgetBytes: 1 << 20, fetchTexture: async (key, options) => (asked2.push(options.fmt), rgba(4, 4)) });
    b.beginFrame();
    b.get('client:dxt');
    b.endFrame();
    await b.whenIdle();
    assert.deepEqual(asked2, ['rgba']);
    assert.equal(without.compressedUploads.length, 0);
    b.dispose();
});

test('TextureStore passes the size cap to the host and deletes everything on dispose', async () => {
    const gl = new FakeGl();
    const seen = [];
    const store = new TextureStore({ gl, budgetBytes: 1 << 20, maxSize: 512, fetchTexture: async (key, options) => (seen.push(options.maxSize), rgba(2, 2)) });
    store.beginFrame();
    store.get('client:x');
    store.get('client:y');
    store.endFrame();
    await store.whenIdle();

    store.dispose();

    assert.deepEqual(seen, [512, 512]);
    assert.equal(gl.live.size, 0, 'dispose must delete every GL texture, the placeholder included');
    assert.equal(gl.created, gl.deleted);
});

test('TextureStore never touches IndexedDB, localStorage, sessionStorage or the Cache API', async () => {
    const trap = (name) => new Proxy({}, { get: () => { throw new Error(`${name} was used`); } });
    const saved = {};
    for (const name of ['indexedDB', 'localStorage', 'sessionStorage', 'caches']) {
        saved[name] = Object.getOwnPropertyDescriptor(globalThis, name);
        Object.defineProperty(globalThis, name, { value: trap(name), configurable: true, writable: true });
    }
    try {
        const gl = new FakeGl();
        const store = new TextureStore({ gl, budgetBytes: 1 << 20, fetchTexture: async () => rgba(2, 2) });
        store.beginFrame();
        store.get('client:p');
        store.endFrame();
        await store.whenIdle();
        store.dispose();
    } finally {
        for (const [name, descriptor] of Object.entries(saved)) {
            if (descriptor) {
                Object.defineProperty(globalThis, name, descriptor);
            } else {
                delete globalThis[name];
            }
        }
    }
});

test('TextureStore refuses nonsense options', () => {
    const gl = new FakeGl();
    const ok = { gl, fetchTexture: async () => rgba(2, 2) };
    assert.throws(() => new TextureStore({ ...ok, budgetBytes: 0 }), RangeError);
    assert.throws(() => new TextureStore({ ...ok, budgetBytes: -1 }), RangeError);
    assert.throws(() => new TextureStore({ ...ok, budgetBytes: Number.NaN }), RangeError);
    assert.throws(() => new TextureStore({ gl, budgetBytes: 1024 }), TypeError);
    assert.throws(() => new TextureStore({ fetchTexture: ok.fetchTexture, budgetBytes: 1024 }), TypeError);
});

test('TextureStore setBudget evicts down to a smaller budget at the next frame end', async () => {
    const gl = new FakeGl();
    const MB = 1024 * 1024;
    const store = new TextureStore({ gl, budgetBytes: 8 * MB, fetchTexture: async () => rgba(512, 512) });
    for (let i = 0; i < 6; i++) {
        store.beginFrame();
        store.get(`client:z${i}`);
        store.endFrame();
        await store.whenIdle();
    }
    assert.equal(store.stats().resident, 6);

    store.setBudget(2 * MB);
    store.beginFrame();
    store.endFrame();

    assert.ok(store.stats().bytes <= 2 * MB);
    assert.throws(() => store.setBudget(0), RangeError);
    store.dispose();
});

// ---------------------------------------------------------------------------------------------------------------------------
// Materials

test('the shared material registry has a real generic material and the lane files fill their kinds without touching the index', () => {
    const registry = createMaterialRegistry();
    const generic = registry.get('generic');

    assert.equal(generic.stub, false);
    for (const member of ['programs', 'layout', 'bind', 'pass']) {
        assert.equal(typeof generic[member], 'function', `generic.${member} is part of the impl interface`);
    }
    assert.equal(generic.pass({ blend: 0 }), 'opaque');
    assert.equal(generic.pass({ blend: 1 }), 'alphakey');
    assert.equal(generic.pass({ blend: 2 }), 'blend');
    assert.equal(generic.pass({ blend: 6 }), 'blend');
    assert.deepEqual(generic.layout().map((a) => a.stream), ['position', 'uv0', 'color0']);
    for (const kind of ['terrain', 'wmo', 'm2', 'skinned', 'character', 'nonsense']) {
        assert.equal(materialKind(kind), generic, `${kind} draws as generic until a lane registers it`);
    }
    assert.deepEqual([...registry.kinds()], ['generic']);

    const WEB_JS = path.join(WEB, 'js', 'materials');
    for (const file of ['terrain', 'model', 'skinned', 'character']) {
        const text = fs.readFileSync(path.join(WEB_JS, `${file}.js`), 'utf8');
        assert.match(text, /export function register\(\) \{\}/, `${file}.js is an empty register() stub`);
    }
    const index = fs.readFileSync(path.join(WEB_JS, 'index.js'), 'utf8');
    for (const file of ['terrain', 'model', 'skinned', 'character']) {
        assert.ok(index.includes(`'./${file}.js'`), `index.js imports ${file}.js`);
    }

    const impl = { kind: 'seam-test', stub: false, programs() {}, layout() {}, bind() {}, pass() {} };
    registerMaterialKind(impl);
    assert.equal(materialKind('seam-test'), impl);
    assert.throws(() => registerMaterialKind({ kind: 'seam-test', stub: false }), Error);
});

test('the legacy material registry class (stubs and replace) still behaves', () => {
    const registry = new MaterialRegistry();
    registry.register({ kind: 'generic', stub: false });
    registry.register({ kind: 'terrain', stub: true });
    assert.equal(registry.resolve('terrain'), registry.get('generic'));
    assert.deepEqual([...registry.kinds()].sort(), ['generic', 'terrain']);
});

test('the material registry refuses duplicates and replaces a stub only by registering over it explicitly', () => {
    const registry = new MaterialRegistry();
    const generic = { kind: 'generic', stub: false };
    registry.register(generic);

    assert.throws(() => registry.register({ kind: 'generic', stub: false }), Error);
    assert.throws(() => registry.register({ kind: '', stub: false }), TypeError);
    registry.register({ kind: 'terrain', stub: true });
    const real = { kind: 'terrain', stub: false };
    registry.replace(real);
    assert.equal(registry.get('terrain'), real);
    assert.throws(() => registry.replace({ kind: 'unregistered', stub: false }), Error);
});

test('the web assets stay self-contained: same CSP, no external URLs, no workers, no persistent storage (outside js/clientfs)', () => {
    const html = fs.readFileSync(path.join(WEB, 'index.html'), 'utf8');
    const csp = /<meta http-equiv="Content-Security-Policy" content="([^"]*)">/.exec(html);
    assert.ok(csp, 'the CSP meta tag is gone');
    assert.equal(csp[1], "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; base-uri 'none'; form-action 'none'");

    const files = [];
    const walk = (dir) => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) {
                walk(full);
            } else if (/\.(js|html|css|mjs)$/.test(entry.name)) {
                files.push(full);
            }
        }
    };
    walk(WEB);
    assert.ok(files.length >= 20, `only ${files.length} web files scanned`);
    for (const file of files) {
        const text = fs.readFileSync(file, 'utf8');
        const relative = path.relative(WEB, file);
        const withoutCspMeta = text.replace(/<meta http-equiv="Content-Security-Policy"[^>]*>/, '');
        assert.ok(!/\bhttps?:\/\//.test(withoutCspMeta), `${relative} names an external URL`);
        // js/clientfs is the browser-side client-file reader: its Workers and cache are its own, covered by clientfs.test.mjs.
        if (relative.split(path.sep).slice(0, 2).join('/') === 'js/clientfs') {
            continue;
        }
        assert.ok(!/\bnew\s+(Shared)?Worker\b|\bimportScripts\b/.test(text), `${relative} starts a worker`);
        assert.ok(!/\bindexedDB\b|\blocalStorage\b|\bcaches\.(open|match)\b/.test(text), `${relative} uses persistent storage`);
    }
});

// ---------------------------------------------------------------------------------------------------------------------------
// TextureStore: arrays and the over-budget step-down

test('TextureStore uploads a multi-layer texture from the host as a TEXTURE_2D_ARRAY, layer by layer', async () => {
    const gl = new FakeGl();
    const layers = 3;
    const levels = [new Uint8Array(4 * 4 * 4 * layers), new Uint8Array(2 * 2 * 4 * layers), new Uint8Array(4 * layers)];
    const bytes = encodeTexture({ format: FORMAT.RGBA8, width: 4, height: 4, layers, levels, meta: {} });
    const store = new TextureStore({ gl, budgetBytes: 1 << 20, fetchTexture: async () => bytes });

    store.beginFrame();
    store.get('client:array');
    await store.whenIdle();
    store.beginFrame();
    const entry = store.get('client:array');

    assert.equal(entry.ready, true);
    assert.equal(entry.target, gl.TEXTURE_2D_ARRAY);
    assert.equal(entry.layers, 3);
    assert.deepEqual(gl.storage3D.map((a) => [a.levels, a.width, a.height, a.depth]), [[3, 4, 4, 3]]);
    assert.equal(gl.layerUploads.length, 9, 'three levels x three layers');
    assert.equal(gl.layerUploads[0].bytes, 64);
    assert.equal(store.stats().bytes, levels.reduce((n, l) => n + l.length, 0));
    store.dispose();
});

test('TextureStore generated arrays are budgeted, grow in steps of 64 layers by copying on the GPU, and release their bytes', () => {
    const gl = new FakeGl();
    const MB = 1024 * 1024;
    const store = new TextureStore({ gl, budgetBytes: 64 * MB, fetchTexture: async () => rgba(4, 4) });
    store.beginFrame();

    const array = store.acquireArray('gen:terrain-array:t1', { width: 16, height: 16, layers: 2 });
    assert.ok(array, 'an array that fits is granted');
    assert.equal(array.capacity, 64);
    assert.equal(array.target, gl.TEXTURE_2D_ARRAY);
    const perLevel = (w, h) => w * h * 4 * 64;
    const bytes64 = perLevel(16, 16) + perLevel(8, 8) + perLevel(4, 4) + perLevel(2, 2) + perLevel(1, 1);
    assert.equal(store.stats().bytes, bytes64);
    assert.equal(store.stats().arrayReallocs, 0);

    assert.equal(store.uploadLayer('gen:terrain-array:t1', 0, new Uint8Array(16 * 16 * 4)), true);
    assert.equal(store.uploadLayer('gen:terrain-array:t1', 1, new Uint8Array(16 * 16 * 4)), true);
    assert.throws(() => store.uploadLayer('gen:terrain-array:t1', 0, new Uint8Array(3)), RangeError);
    assert.equal(store.uploadLayer('gen:terrain-array:t1', 64, new Uint8Array(16 * 16 * 4)), false, 'past the capacity');
    assert.equal(store.growArray('gen:terrain-array:t1', 10), true, 'room already');
    assert.equal(store.stats().arrayReallocs, 0);

    const before = array.texture;
    assert.equal(store.growArray('gen:terrain-array:t1', 65), true);
    assert.equal(store.stats().arrayReallocs, 1);
    assert.equal(array.capacity, 128);
    assert.notEqual(array.texture, before);
    assert.equal(gl.layerCopies.length, 2, 'the two used layers were copied');
    assert.equal(gl.framebuffersCreated, gl.framebuffersDeleted);
    assert.equal(store.stats().bytes, bytes64 * 2);

    assert.equal(store.release('gen:terrain-array:t1'), true);
    assert.equal(store.stats().bytes, 0);
    store.dispose();
});

test('TextureStore refuses an array that does not fit the budget and a growth that would need old + new at once', () => {
    const gl = new FakeGl();
    const store = new TextureStore({ gl, budgetBytes: 900 * 1024, fetchTexture: async () => rgba(4, 4) });
    store.beginFrame();

    assert.equal(store.acquireArray('gen:big', { width: 256, height: 256, layers: 1 }), null, '64 layers of 256 x 256 is far over the budget');
    const small = store.acquireArray('gen:small', { width: 32, height: 32, layers: 1 }); // 64 x 32 x 32 x 4 x 4/3 = ~350 KiB
    assert.ok(small);
    assert.equal(store.growArray('gen:small', 65), false, 'old (350 KiB) + new (700 KiB) is over 900 KiB');
    assert.equal(small.capacity, 64, 'a refused growth changes nothing');
    assert.equal(store.stats().arrayReallocs, 0);
    store.dispose();
});

test('TextureStore asks the host for textures one maxSize step smaller once the textures in use exceed the budget, and takes the step back after 120 calm frames', async () => {
    const MB = 1024 * 1024;
    const gl = new FakeGl();
    const sizes = [];
    const store = new TextureStore({
        gl,
        budgetBytes: 2 * MB,
        maxSize: 512,
        fetchTexture: async (key, options) => {
            sizes.push(options.maxSize);
            return rgba(512, 512); // 1 MiB each: three do not fit
        },
    });
    for (let frame = 0; frame < 6; frame++) {
        store.beginFrame();
        for (let i = 0; i < 3; i++) {
            store.get(`client:p${i}`);
        }
        store.endFrame();
        await tick();
        await tick();
    }
    assert.equal(store.stats().steppedDown, true);
    assert.equal(store.stats().effectiveMaxSize, 256);
    assert.equal(sizes[0], 512, 'the first requests use the configured cap');
    assert.ok(sizes.includes(256), 'later requests are one step smaller');
    assert.equal(store.stats().overBudget, true);

    // The working set shrinks to one texture: after 120 frames under 80 % of the budget the cap returns.
    for (let frame = 0; frame < 125; frame++) {
        store.beginFrame();
        store.get('client:p0');
        store.endFrame();
        await tick();
    }
    assert.equal(store.stats().steppedDown, false);
    assert.equal(store.stats().effectiveMaxSize, 512);
    store.dispose();
});
