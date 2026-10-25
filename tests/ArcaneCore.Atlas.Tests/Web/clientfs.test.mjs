// Lane D4 (devui-assets): the browser-side reader of the visitor's own WoW 1.12.1 folder, Web/js/clientfs/*.
// Design: D:/ArcaneCore-lanes/_land/atlas-devui-design.md, sections 0.5, 0.5.1 and lane D4 (r1-r3).
//
// Run through ClientFsFixtureTests.cs, which first writes the synthetic client folder (MPQs built by MpqTestWriter, ADTs by
// SyntheticTerrain, PE files with a version resource, malformed archives, and the ATG1 bytes ClientTerrainGeometry produces)
// into a temp directory named by ARCANECORE_ATLAS_CLIENTFS. Nothing here is a client byte; a missing fixture directory or a
// missing browser FAILS the tests (it never skips them). Only the two real-data tests skip, with a printed reason, when
// ARCANECORE_TEST_CLIENT_DIR is unset.
//
// The module contracts these tests pin (the lane implements exactly these exports; everything else is free):
//
//   limits.js    LIMITS (frozen): maxFileBytes 64 MiB, maxEntries 2^20, maxSectorShift 15, fileTimeoutMs 5000, tileTimeoutMs 30000.
//                class ClientFsError(code, message) with .code in 'bad-archive' | 'bad-file' | 'unsupported-compression' |
//                'timeout' | 'unknown-layer'.
//   mpq.js       openArchive(blob, {name}) -> archive {name, has(path), read(path) -> Uint8Array|null}
//                openChain([{name, blob}]) -> chain {order: names lowest priority first, has, read, archiveOf(path) -> name|null}
//                loadOrder(names) -> names in MpqFileSystem.LoadOrder order (backup.MPQ left out)
//                censusMasks(archive) -> {'0x02': n, ...} compression-mask counts over the archive's sectors/units
//   explode.js   explode(streamBytes, expectedSize) -> Uint8Array (throws ClientFsError 'bad-file')
//   dbc.js       parseDbc(bytes) -> {recordCount, fieldCount, recordSize, stringBlockSize, cell(r, f), string(offset), localized(r, first)}
//   adt.js       buildTerrainAtg1(adtBytes, {map,x,y}, areaName) -> Uint8Array == GeometryWire.Write(ClientTerrainGeometry.BuildTerrain(..))
//                buildLiquidAtg1(adtBytes, {map,x,y}) -> Uint8Array | null
//                buildTile(chain, layer, map, x, y) -> {state, detail, parts: Uint8Array[]}  (Api.tile's state names)
//   validate.js  validateClientFolder(folder) -> {ok, errors[], warnings[], build, version}; parseExeVersion(bytes) -> {text, build}|null
//   scope.js     scanHandle(dirHandle), scanFileList(fileList) -> folder {name, kind, hasData, archives:[{name,size,lastModified,blob}],
//                dataFolders:[names], exe:{name,size,lastModified,blob}|null}; the only module that touches a handle or FileList
//   store.js     cacheKey(path, {name,size,lastModified}), LruCache(maxBytes) {set(key, value, bytes), get(key), bytes},
//                LRU_DEFAULT_BYTES, defaultCacheBudget({quota})
//   pool.js      poolSizeFor(hardwareConcurrency), WorkerPool({size, spawn, fileTimeoutMs?, tileTimeoutMs?}) {run(op, payload, {kind, key}),
//                spawned, isBad(key)}; posts {id, op, ...payload}, expects {id, ok, result} | {id, ok:false, error:{code, message}}
//   worker.js    module Worker; answers {id, op:'ping'} with {id, ok:true, result:'pong'}
//   index.js     openClientFilesDialog(), showFirstEntryDialogIfNeeded() -> Promise<boolean opened>, openClientFilesSettings(),
//                clientFilesStatus() -> {state: none|skipped|needs-permission|ready|error, folderName, build, version, warnings,
//                cacheBytes, cacheBudgetBytes, archiveReads, cacheHits}, localTileProvider() -> {tile(layer, map, x, y, signal)}
//                returning Api.tile's {state, detail, body}. DOM hooks: [data-clientfs="dialog|error|reallow|settings"],
//                input[type=file][webkitdirectory], input[type=range][data-clientfs="budget"]; buttons by their visible text.

import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { Browser, browserFromEnvironment } from './support/browser.mjs';
import {
    Harness,
    PAGE_HELPERS,
    PAGE_HELPERS_NO_PICKER,
    REAL_VARIABLE,
    canaryNeedles,
    clickText,
    clientfs,
    findCanaries,
    loadFixtures,
    localCsp,
    mockFileList,
    mockHandle,
    publicCsp,
    textOf,
} from './support/clientfs-fixtures.mjs';

const WEB_ATG1 = new URL('../../../tools/ArcaneCore.Atlas/Web/js/atg1.js', import.meta.url);
const ERROR_NO_DATA = 'No Data folder here - pick the folder that contains WoW.exe and Data';
const ERROR_BC = 'This is a Burning Crusade or later client; Atlas needs 1.12.1';
const ERROR_BLOCKED = 'Your browser blocked access - click Re-allow';
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const sha = (bytes) => crypto.createHash('sha256').update(bytes).digest('hex');
const sameBytes = (a, b) => Buffer.compare(Buffer.from(a), Buffer.from(b)) === 0;

function firstDifference(a, b) {
    const n = Math.min(a.length, b.length);
    for (let i = 0; i < n; i++) {
        if (a[i] !== b[i]) {
            return `first difference at byte ${i} of ${a.length}/${b.length} (${a[i]} vs ${b[i]})`;
        }
    }
    return `lengths differ: ${a.length} vs ${b.length}`;
}

/** Asserts that `promiseOrFn` rejects with a ClientFsError of one of `codes`. */
async function refuses(run, codes, what) {
    const { ClientFsError } = await clientfs('limits');
    const list = Array.isArray(codes) ? codes : [codes];
    let error;
    try {
        await run();
    } catch (e) {
        error = e;
    }
    assert.ok(error, `${what}: nothing was thrown`);
    assert.ok(error instanceof ClientFsError, `${what}: threw ${error?.name ?? typeof error}: ${error?.message} instead of a ClientFsError`);
    assert.ok(list.includes(error.code), `${what}: error code ${error.code} is not one of ${list.join('|')} (${error.message})`);
    assert.ok(typeof error.message === 'string' && error.message.length > 0, `${what}: the error has no message`);
    return error;
}

/** The folder object scope.js hands on, built from a path -> bytes map without going through scope.js. */
function folderOf(files, { dataFolders = [], name = 'WoW' } = {}) {
    const archives = [];
    let exe = null;
    let hasData = false;
    for (const [p, bytes] of files) {
        const segments = p.split('/');
        const entry = { name: segments.at(-1), size: bytes.length, lastModified: 1_700_000_000_000, blob: new Blob([bytes]) };
        if (segments.length === 2 && segments[0].toLowerCase() === 'data' && /\.mpq$/i.test(entry.name)) {
            archives.push(entry);
        } else if (segments.length === 1 && entry.name.toLowerCase() === 'wow.exe') {
            exe = entry;
        }
    }
    hasData = [...files.keys()].some((p) => p.toLowerCase().startsWith('data/')) || dataFolders.length > 0;
    return { name, kind: 'handle', hasData, archives, dataFolders, exe };
}

async function validate(folder) {
    const { validateClientFolder } = await clientfs('validate');
    return validateClientFolder(folder);
}

// ==================================================================================================================
// limits.js

test('limits.js states the parser limits of design 0.5', async () => {
    const { LIMITS, ClientFsError } = await clientfs('limits');
    assert.equal(LIMITS.maxFileBytes, 64 * 1024 * 1024);
    assert.equal(LIMITS.maxEntries, 1 << 20);
    assert.equal(LIMITS.maxSectorShift, 15);
    assert.equal(LIMITS.fileTimeoutMs, 5000);
    assert.equal(LIMITS.tileTimeoutMs, 30000);
    assert.ok(Object.isFrozen(LIMITS), 'the limits must not be mutable at run time');
    const error = new ClientFsError('bad-file', 'message');
    assert.ok(error instanceof Error);
    assert.equal(error.code, 'bad-file');
    assert.equal(error.message, 'message');
});

// ==================================================================================================================
// mpq.js

test('mpq.js reads every member of the synthetic archives byte for byte', async () => {
    const fx = loadFixtures();
    const { openArchive } = await clientfs('mpq');
    let expected = 0;
    let compared = 0;
    for (const [name, info] of Object.entries(fx.manifest.archives)) {
        const archive = await openArchive(fx.blob(info.file), { name });
        assert.equal(archive.name, name);
        for (const member of info.members) {
            if (member.deleted) {
                continue;
            }
            expected++;
            assert.ok(archive.has(member.path), `${name} should hold ${member.path}`);
            const bytes = await archive.read(member.path);
            assert.ok(bytes instanceof Uint8Array, `${name}:${member.path} did not read`);
            assert.equal(bytes.length, member.size, `${name}:${member.path} size`);
            assert.equal(sha(bytes), member.sha256, `${name}:${member.path} content`);
            compared++;
        }
        assert.equal(await archive.read('No\\Such\\File.bin'), null);
        assert.equal(archive.has('No\\Such\\File.bin'), false);
    }
    assert.equal(compared, expected);
    assert.ok(compared >= 20, `only ${compared} members compared`);
    // The four tile ADTs are stored four different ways (zlib sectors, encrypted + key-adjusted, raw sectors, single unit).
    assert.equal(fx.manifest.tiles.length, 4);
});

test('mpq.js orders archives like MpqFileSystem.LoadOrder', async () => {
    const { loadOrder } = await clientfs('mpq');
    // base, dbc, interface, misc, model, sound, speech, terrain, texture, wmo; other non-patch archives by name; then
    // patch.MPQ (level 1), patch-2.MPQ ... by numeric level; backup.MPQ is never mounted (MpqFileSystem.cs:14-40, 96-135).
    const names = ['patch-10.MPQ', 'patch-2.MPQ', 'terrain.MPQ', 'Fonts.MPQ', 'patch.MPQ', 'base.MPQ', 'DBC.mpq', 'backup.MPQ',
        'texture.MPQ', 'wmo.MPQ', 'model.MPQ', 'misc.MPQ', 'patch-3.MPQ'];
    assert.deepEqual(loadOrder(names), ['base.MPQ', 'DBC.mpq', 'misc.MPQ', 'model.MPQ', 'terrain.MPQ', 'texture.MPQ', 'wmo.MPQ',
        'Fonts.MPQ', 'patch.MPQ', 'patch-2.MPQ', 'patch-3.MPQ', 'patch-10.MPQ']);
    assert.deepEqual(loadOrder([]), []);
});

test('mpq.js mounts a patch chain in MpqFileSystem priority order', async () => {
    const fx = loadFixtures();
    const { openChain } = await clientfs('mpq');
    const names = ['patch-2.MPQ', 'patch.MPQ', 'wmo.MPQ', 'texture.MPQ', 'terrain.MPQ', 'model.MPQ', 'dbc.MPQ'];
    const chain = await openChain(names.map((name) => ({ name, blob: fx.blob(fx.manifest.archives[name].file) })));
    assert.deepEqual(chain.order, ['dbc.MPQ', 'model.MPQ', 'terrain.MPQ', 'texture.MPQ', 'wmo.MPQ', 'patch.MPQ', 'patch-2.MPQ']);
    for (const item of fx.manifest.chain) {
        const bytes = await chain.read(item.path);
        if (item.text === null) {
            assert.equal(bytes, null, `${item.path} must resolve to absent`);
            assert.equal(chain.has(item.path), false);
            assert.equal(chain.archiveOf(item.path), null);
        } else {
            assert.equal(new TextDecoder().decode(bytes), item.text, `${item.path} must come from ${item.archive}`);
            assert.equal(chain.archiveOf(item.path), item.archive);
            assert.equal(chain.has(item.path), true);
        }
    }
    // patch.MPQ's deletion marker hides Gone.txt although dbc.MPQ still holds it.
    const base = await openChainArchive(fx, 'dbc.MPQ');
    assert.ok(base.has('Text\\Gone.txt'));
});

async function openChainArchive(fx, name) {
    const { openArchive } = await clientfs('mpq');
    return openArchive(fx.blob(fx.manifest.archives[name].file), { name });
}

test('mpq.js refuses every malformed archive with a typed error inside the worker timeout', async () => {
    const fx = loadFixtures();
    const { openArchive } = await clientfs('mpq');
    const { LIMITS } = await clientfs('limits');

    // The control: the unmutated archive reads fine, so each failure below is caused by its mutation.
    const control = fx.manifest.malformedControl;
    const good = await openArchive(fx.blob(control.file), { name: 'control.MPQ' });
    const bytes = await good.read(control.path);
    assert.equal(bytes.length, control.size);
    assert.equal(sha(bytes), control.sha256);

    const cases = fx.manifest.malformedArchives;
    assert.ok(cases.length >= 20, `only ${cases.length} malformed archives`);
    assert.ok(cases.some((c) => c.name === 'block-beyond-declared-archive'), 'the archiveSize block-bound case is part of the set');
    for (const c of cases) {
        const started = performance.now();
        await refuses(async () => {
            const archive = await openArchive(fx.blob(c.file), { name: `${c.name}.MPQ` });
            await archive.read(c.read);
        }, c.codes, `${c.name} (${c.limit})`);
        const elapsed = performance.now() - started;
        assert.ok(elapsed < LIMITS.fileTimeoutMs, `${c.name} took ${elapsed} ms, past the ${LIMITS.fileTimeoutMs} ms worker timeout`);
    }
});

test('mpq.js refuses bzip2 with unsupported-compression until the census has run', async () => {
    const fx = loadFixtures();
    const { openArchive, censusMasks } = await clientfs('mpq');
    const c = fx.manifest.malformedArchives.find((m) => m.name === 'bzip2-refused');
    const archive = await openArchive(fx.blob(c.file), { name: 'bzip.MPQ' });
    const error = await refuses(() => archive.read(c.read), 'unsupported-compression', 'bzip2 sector');
    assert.match(error.message, /bzip2/i);
    assert.deepEqual(await censusMasks(archive), { '0x10': 1 });
});

test('mpq.js census counts the compression masks of an archive', async () => {
    const fx = loadFixtures();
    const { censusMasks } = await clientfs('mpq');
    const census = await censusMasks(await openChainArchive(fx, 'dbc.MPQ'));
    const keys = Object.keys(census);
    assert.deepEqual(keys, ['0x02'], `dbc.MPQ was written with zlib only, got ${JSON.stringify(census)}`);
    assert.ok(census['0x02'] > 0);
});

test('explode.js expands every StormLib-made PKWARE vector', async () => {
    const fx = loadFixtures();
    const { explode } = await clientfs('explode');
    let checked = 0;
    for (const v of fx.manifest.pkware) {
        const plain = fx.read(v.plain);
        assert.equal(plain.length, v.plainLength);
        const out = await explode(fx.read(v.stream), plain.length);
        assert.ok(sameBytes(out, plain), `${v.name}: ${firstDifference(out, plain)}`);
        checked++;
    }
    assert.ok(checked >= 6, `only ${checked} vectors`);
});

test('explode.js refuses wrong sizes, truncation and bad headers', async () => {
    const fx = loadFixtures();
    const { explode } = await clientfs('explode');
    const v = fx.manifest.pkware.find((p) => p.name.startsWith('V1/')); // 2000 bytes, ramp
    const stream = fx.read(v.stream);
    const size = v.plainLength;
    await refuses(() => explode(stream, size - 1), 'bad-file', 'expected size one too small');
    await refuses(() => explode(stream, size + 1), 'bad-file', 'expected size one too large');
    await refuses(() => explode(stream.slice(0, stream.length >> 1), size), 'bad-file', 'truncated stream');
    await refuses(() => explode(new Uint8Array(0), size), 'bad-file', 'empty stream');
    await refuses(() => explode(Uint8Array.of(0), size), 'bad-file', 'one byte stream');
    const dictionary = stream.slice();
    dictionary[1] = 7; // PKWARE DCL dictionary size field is 4, 5 or 6
    await refuses(() => explode(dictionary, size), 'bad-file', 'dictionary field 7');
    const mode = stream.slice();
    mode[0] = 2; // literal mode is 0 (binary) or 1 (ASCII)
    await refuses(() => explode(mode, size), 'bad-file', 'literal mode 2');
});

// ==================================================================================================================
// dbc.js

test('dbc.js parses the synthetic tables and refuses damaged ones', async () => {
    const fx = loadFixtures();
    const { parseDbc } = await clientfs('dbc');
    const archive = await openChainArchive(fx, 'dbc.MPQ');
    const expect = fx.manifest.dbc;

    const cdi = parseDbc(await archive.read('DBFilesClient\\CreatureDisplayInfo.dbc'));
    assert.equal(cdi.recordCount, expect.creatureDisplayInfo.records);
    assert.equal(cdi.fieldCount, 12);
    assert.equal(cdi.recordSize, 48);
    assert.equal(cdi.stringBlockSize, expect.creatureDisplayInfo.stringBlockSize);
    expect.creatureDisplayInfo.cells.forEach((row, r) => row.forEach((value, f) => {
        assert.equal(cdi.cell(r, f), value, `cell ${r},${f}`);
    }));
    for (const [offset, text] of Object.entries(expect.creatureDisplayInfo.strings)) {
        assert.equal(cdi.string(Number(offset)), text, `string at ${offset}`);
    }

    const map = parseDbc(await archive.read('DBFilesClient\\Map.dbc'));
    assert.equal(map.fieldCount, 42);
    assert.equal(map.string(map.cell(0, 1)), expect.map.directory);
    assert.equal(map.localized(0, 4), expect.map.name);
    const area = parseDbc(await archive.read('DBFilesClient\\AreaTable.dbc'));
    assert.equal(area.fieldCount, 25);
    assert.equal(area.cell(0, 0), expect.area.areaId);
    assert.equal(area.localized(0, 11), expect.area.name);

    // Damage. u32 header fields: records@4, fields@8, recordSize@12, stringBlock@16.
    const good = await archive.read('DBFilesClient\\CreatureDisplayInfo.dbc');
    const patched = (offset, value) => {
        const copy = good.slice();
        new DataView(copy.buffer).setUint32(offset, value, true);
        return copy;
    };
    const damaged = {
        'bad magic': patched(0, 0x58585858),
        'record count larger than the file': patched(4, 0xFFFFFFFF),
        'record count 2^24 + 1': patched(4, (1 << 24) + 1),
        'zero fields': patched(8, 0),
        'record size not fields * 4': patched(12, 47),
        'string block larger than the file': patched(16, 0x7FFFFFFF),
        'trailing byte': Uint8Array.of(...good, 0),
        'shorter than the header': good.slice(0, 19),
        empty: new Uint8Array(0),
    };
    for (const [what, bytes] of Object.entries(damaged)) {
        await refuses(() => parseDbc(bytes), 'bad-file', what);
    }
    await refuses(() => cdi.string(cdi.stringBlockSize), 'bad-file', 'string offset at the end of the block');
    await refuses(() => cdi.string(0xFFFFFFFF), 'bad-file', 'string offset far outside the block');
});

// ==================================================================================================================
// adt.js: JS output equals the C# output

const toTile = (t) => ({ map: t.map, x: t.x, y: t.y });
const areaNames = (id) => (id === 12 ? 'Elwynn Forest' : null);

test('adt.js terrain and liquid ATG1 equal ClientTerrainGeometry byte for byte on synthetic tiles', async () => {
    const fx = loadFixtures();
    const { buildTerrainAtg1, buildLiquidAtg1 } = await clientfs('adt');
    const { decodeChunk } = await import(WEB_ATG1.href);
    const withLiquid = fx.manifest.tiles.filter((t) => t.liquid !== null);
    assert.equal(withLiquid.length, 3, 'three synthetic tiles with holes and liquid');
    assert.equal(fx.manifest.tiles.length, 4);
    let terrain = 0;
    let liquid = 0;
    for (const tile of fx.manifest.tiles) {
        assert.ok(tile.holeChunks > 0, 'every synthetic tile has holes');
        const adt = fx.read(tile.adt);
        const expected = fx.read(tile.terrain);
        const got = buildTerrainAtg1(adt, toTile(tile), areaNames);
        assert.ok(got instanceof Uint8Array);
        assert.ok(sameBytes(got, expected), `terrain ${tile.map}/${tile.x}/${tile.y}: ${firstDifference(got, expected)}`);
        const decoded = decodeChunk(expected);
        assert.equal(decoded.triangleCount, tile.terrainTriangles);
        assert.ok(decoded.triangleCount > 0);
        terrain++;

        const wet = buildLiquidAtg1(adt, toTile(tile));
        if (tile.liquid === null) {
            assert.equal(wet, null, 'a tile without a visible liquid square has no liquid chunk');
            continue;
        }
        const expectedLiquid = fx.read(tile.liquid);
        assert.ok(wet instanceof Uint8Array);
        assert.ok(sameBytes(wet, expectedLiquid), `liquid ${tile.map}/${tile.x}/${tile.y}: ${firstDifference(wet, expectedLiquid)}`);
        assert.equal(decodeChunk(expectedLiquid).triangleCount, tile.liquidTriangles);
        assert.ok(tile.liquidTriangles > 0);
        liquid++;
    }
    assert.equal(terrain, 4);
    assert.equal(liquid, 3);
});

test('adt.js area names reach the chunk attributes and a view with an odd byteOffset gives the same bytes', async () => {
    const fx = loadFixtures();
    const { buildTerrainAtg1 } = await clientfs('adt');
    const tile = fx.manifest.tiles[0]; // chunks 3 (area 12) and 4 (area 99, not in AreaTable) carry areas
    const adt = fx.read(tile.adt);
    const expected = fx.read(tile.terrain);
    assert.ok(Buffer.from(expected).includes('Elwynn Forest'), 'the C# chunk names the area');
    const without = buildTerrainAtg1(adt, toTile(tile), () => null);
    assert.ok(!sameBytes(without, expected), 'the area lookup must change the output');
    assert.ok(!Buffer.from(without).includes('Elwynn Forest'));

    const shifted = new Uint8Array(adt.length + 3);
    shifted.set(adt, 3);
    const view = shifted.subarray(3);
    assert.equal(view.byteOffset % 4, 3);
    assert.ok(sameBytes(buildTerrainAtg1(view, toTile(tile), areaNames), expected));
});

test('adt.js refuses malformed ADTs with bad-file inside the tile timeout', async () => {
    const fx = loadFixtures();
    const { buildTerrainAtg1, buildLiquidAtg1 } = await clientfs('adt');
    const { LIMITS } = await clientfs('limits');
    const tile = { map: 0, x: 33, y: 31 };
    const cases = fx.manifest.malformedAdts;
    assert.equal(cases.length, 6);
    for (const c of cases) {
        const bytes = fx.read(c.file);
        const started = performance.now();
        await refuses(() => buildTerrainAtg1(bytes, tile, areaNames), 'bad-file', `terrain of ${c.name}`);
        await refuses(() => buildLiquidAtg1(bytes, tile), 'bad-file', `liquid of ${c.name}`);
        assert.ok(performance.now() - started < LIMITS.tileTimeoutMs, `${c.name} outlived the tile timeout`);
    }
});

test('adt.js builds a tile from the archive chain with Api.tile states', async () => {
    const fx = loadFixtures();
    const { openChain } = await clientfs('mpq');
    const { buildTile } = await clientfs('adt');
    const names = ['dbc.MPQ', 'terrain.MPQ', 'patch.MPQ', 'patch-2.MPQ'];
    const chain = await openChain(names.map((name) => ({ name, blob: fx.blob(fx.manifest.archives[name].file) })));
    const { terrain, liquid } = fx.manifest.layers;

    let ok = 0;
    for (const tile of fx.manifest.tiles) {
        const t = await buildTile(chain, terrain, tile.map, tile.x, tile.y);
        assert.equal(t.state, 'Ok');
        assert.equal(t.parts.length, 1);
        assert.ok(sameBytes(t.parts[0], fx.read(tile.terrain)), `terrain ${tile.x}/${tile.y}: ${firstDifference(t.parts[0], fx.read(tile.terrain))}`);

        const w = await buildTile(chain, liquid, tile.map, tile.x, tile.y);
        if (tile.liquid === null) {
            assert.equal(w.state, 'Empty');
            assert.deepEqual(w.parts, []);
        } else {
            assert.equal(w.state, 'Ok');
            assert.equal(w.parts.length, 1);
            assert.ok(sameBytes(w.parts[0], fx.read(tile.liquid)), `liquid ${tile.x}/${tile.y}`);
        }
        ok++;
    }
    assert.equal(ok, 4);

    const a = fx.manifest.absentTile;
    for (const layer of [terrain, liquid]) {
        const missing = await buildTile(chain, layer, a.map, a.x, a.y);
        assert.equal(missing.state, 'Missing');
        assert.deepEqual(missing.parts, []);
        assert.equal(typeof missing.detail, 'string');
        const noMap = await buildTile(chain, layer, 999, 33, 31);
        assert.equal(noMap.state, 'Missing', 'a map id Map.dbc does not list has no terrain');
    }
    await refuses(() => buildTile(chain, 'tiles', 0, 33, 31), 'unknown-layer', 'a layer the client files do not build');
});

test('adt.js does not remember a rejected Map.dbc/AreaTable.dbc load for the chain', async () => {
    const fx = loadFixtures();
    const { openChain } = await clientfs('mpq');
    const { buildTile } = await clientfs('adt');
    const names = ['dbc.MPQ', 'terrain.MPQ', 'patch.MPQ', 'patch-2.MPQ'];
    const real = await openChain(names.map((name) => ({ name, blob: fx.blob(fx.manifest.archives[name].file) })));
    let failures = 1;
    const chain = Object.create(real, {
        read: { value: async (p) => { if (failures-- > 0) { throw new Error('transient read failure'); } return real.read(p); } },
    });
    const tile = fx.manifest.tiles[0];
    await assert.rejects(() => buildTile(chain, fx.manifest.layers.terrain, tile.map, tile.x, tile.y), /transient read failure/);
    const again = await buildTile(chain, fx.manifest.layers.terrain, tile.map, tile.x, tile.y);
    assert.equal(again.state, 'Ok', 'the second request retries instead of replaying the cached rejection');
});

// ==================================================================================================================
// validate.js

test('validate.js accepts the 1.12.1 folder and reads the version resource', async () => {
    const fx = loadFixtures();
    const result = await validate(folderOf(fx.tree()));
    assert.deepEqual(result.errors, []);
    assert.deepEqual(result.warnings, []);
    assert.equal(result.ok, true);
    assert.equal(result.build, 5875);
    assert.equal(result.version, '1.12.1.5875');

    const noExe = await validate(folderOf(fx.tree({ 'WoW.exe': null })));
    assert.equal(noExe.ok, true, noExe.errors.join(' | '));
    assert.equal(noExe.build, 5875, 'the build comes from the DBC layouts when there is no WoW.exe');
    assert.equal(noExe.version, null);
});

test('validate.js treats archive names case-insensitively, ignores extra archives and never needs optional ones', async () => {
    const fx = loadFixtures();
    const tree = fx.tree({
        'Data/dbc.MPQ': null, 'Data/DBC.mpq': 'archives/dbc.MPQ',
        'Data/patch-2.MPQ': null,
        'Data/Fonts.MPQ': 'archives/wmo.MPQ',
        'Data/patch-3.MPQ': 'archives/patch.MPQ',
    });
    const result = await validate(folderOf(tree));
    assert.equal(result.ok, true, result.errors.join(' | '));
    assert.deepEqual(result.warnings, []);
});

test('validate.js refuses a folder without Data', async () => {
    const fx = loadFixtures();
    const onlyExe = new Map([['WoW.exe', fx.read('exe/WoW-1.12.1.5875.exe')]]);
    const result = await validate(folderOf(onlyExe, { name: 'WoW' }));
    assert.equal(result.ok, false);
    assert.equal(result.errors[0], ERROR_NO_DATA);

    const empty = await validate(folderOf(new Map()));
    assert.equal(empty.ok, false);
    assert.equal(empty.errors[0], ERROR_NO_DATA);
});

test('validate.js refuses a folder missing a required archive and names it', async () => {
    const fx = loadFixtures();
    for (const required of ['dbc', 'model', 'texture', 'terrain', 'wmo', 'patch']) {
        const result = await validate(folderOf(fx.tree({ [`Data/${required}.MPQ`]: null })));
        assert.equal(result.ok, false, `${required}.MPQ missing must be refused`);
        assert.ok(result.errors.some((e) => e.includes(`${required}.MPQ`)), `no error names ${required}.MPQ: ${result.errors.join(' | ')}`);
    }
});

test('validate.js refuses a 2.x+ client by its archives', async () => {
    const fx = loadFixtures();
    for (const extra of ['common.MPQ', 'expansion.MPQ', 'lichking.MPQ']) {
        const result = await validate(folderOf(fx.tree({ [`Data/${extra}`]: 'archives/patch.MPQ' })));
        assert.equal(result.ok, false, `${extra} means a 2.x+ client`);
        assert.ok(result.errors.includes(ERROR_BC), `${extra}: ${result.errors.join(' | ')}`);
    }
    const lower = await validate(folderOf(fx.tree({ 'Data/COMMON.mpq': 'archives/patch.MPQ' })));
    assert.ok(lower.errors.includes(ERROR_BC), 'archive names are case-insensitive');
});

test('validate.js only warns for a Data/<locale> folder alone', async () => {
    const fx = loadFixtures();
    const result = await validate(folderOf(fx.tree(), { dataFolders: ['enUS'] }));
    assert.equal(result.ok, true, `a localized 1.12 folder is unverified, not refused: ${result.errors.join(' | ')}`);
    assert.deepEqual(result.errors, []);
    assert.equal(result.warnings.length, 1);
    assert.match(result.warnings[0], /enUS/);

    const withBc = await validate(folderOf(fx.tree({ 'Data/expansion.MPQ': 'archives/patch.MPQ' }), { dataFolders: ['enUS'] }));
    assert.equal(withBc.ok, false, 'a locale folder plus an expansion archive is still refused');
});

test('validate.js warns for another 1.12 build and refuses a different layout with the build number', async () => {
    const fx = loadFixtures();
    const other = await validate(folderOf(fx.tree({ 'WoW.exe': 'exe/WoW-1.12.2.6005.exe' })));
    assert.equal(other.ok, true, `1.12.2 shares the 5875 layouts: ${other.errors.join(' | ')}`);
    assert.equal(other.version, '1.12.2.6005');
    assert.equal(other.warnings.length, 1);
    assert.match(other.warnings[0], /1\.12\.2\.6005/);

    // 2.4.3.8606 with a CreatureDisplayInfo.dbc of another layout.
    const bc = await validate(folderOf(fx.tree({ 'WoW.exe': 'exe/WoW-2.4.3.8606.exe', 'Data/dbc.MPQ': 'archives/dbc-badcdi.MPQ' })));
    assert.equal(bc.ok, false);
    assert.equal(bc.errors[0], 'This is build 8606 (expected 5875); Atlas needs the 1.12.1 client');

    // Without a WoW.exe the layout still decides.
    const noExe = await validate(folderOf(fx.tree({ 'WoW.exe': null, 'Data/dbc.MPQ': 'archives/dbc-badcdi.MPQ' })));
    assert.equal(noExe.ok, false);
    assert.match(noExe.errors[0], /^This is build .+ \(expected 5875\); Atlas needs the 1\.12\.1 client$/);

    const badMap = await validate(folderOf(fx.tree({ 'Data/dbc.MPQ': 'archives/dbc-badmap.MPQ' })));
    assert.equal(badMap.ok, false, 'Map.dbc with 40 fields is not the 5875 layout');
    assert.match(badMap.errors[0], /expected 5875/);
});

test('validate.js names a damaged archive header', async () => {
    const fx = loadFixtures();
    const damaged = await validate(folderOf(fx.tree({ 'Data/dbc.MPQ': 'archives/dbc-damaged.MPQ' })));
    assert.equal(damaged.ok, false);
    assert.equal(damaged.errors[0], 'Archive dbc.MPQ is damaged (header)');

    const mixed = await validate(folderOf(fx.tree({ 'Data/dbc.MPQ': null, 'Data/DBC.mpq': 'archives/dbc-damaged.MPQ' })));
    assert.equal(mixed.errors[0], 'Archive DBC.mpq is damaged (header)', 'the archive is named as the folder spells it');

    for (const c of ['truncated-header', 'empty', 'not-an-mpq', 'hash-count-over-2^20']) {
        const entry = fx.manifest.malformedArchives.find((m) => m.name === c);
        const result = await validate(folderOf(fx.tree({ 'Data/terrain.MPQ': entry.file })));
        assert.equal(result.ok, false, c);
        assert.equal(result.errors[0], 'Archive terrain.MPQ is damaged (header)', c);
    }
});

test('validate.js reads PE version resources defensively', async () => {
    const fx = loadFixtures();
    const { parseExeVersion } = await clientfs('validate');
    assert.deepEqual(parseExeVersion(fx.read('exe/WoW-1.12.1.5875.exe')), { text: '1.12.1.5875', build: 5875 });
    assert.deepEqual(parseExeVersion(fx.read('exe/WoW-1.12.2.6005.exe')), { text: '1.12.2.6005', build: 6005 });
    assert.deepEqual(parseExeVersion(fx.read('exe/WoW-2.4.3.8606.exe')), { text: '2.4.3.8606', build: 8606 });
    assert.equal(parseExeVersion(fx.read(fx.manifest.exe.notPe)), null);
    assert.equal(parseExeVersion(fx.read(fx.manifest.exe.noResources)), null);
    assert.equal(parseExeVersion(new Uint8Array(0)), null);
    const good = fx.read('exe/WoW-1.12.1.5875.exe');
    for (const cut of [2, 0x40, 0x84, 0x1A0, 0x200, 0x210, 0x240, 0x280]) {
        assert.doesNotThrow(() => parseExeVersion(good.slice(0, cut)), `a PE cut at ${cut} must not throw`);
        assert.equal(parseExeVersion(good.slice(0, cut)), null, `a PE cut at ${cut} has no complete version resource`);
    }
    const wild = good.slice();
    new DataView(wild.buffer).setUint32(0x3C, 0x7FFFFFF0, true); // e_lfanew past the file
    assert.equal(parseExeVersion(wild), null);
});

// ==================================================================================================================
// scope.js: the only module that touches the handle or the FileList

const FORBIDDEN = /(^|\/)(wtf|interface|logs|screenshots|account|canaryacc)(\/|$)|config\.wtf/i;
const ALLOWED_PATH = /^(data(\/[^/]+\.mpq)?|wow\.exe)?$/i;
const LISTING = new Set(['entries', 'values', 'keys', 'iterate']);

test('scope.js opens only Data/*.MPQ and WoW.exe from a directory handle', async () => {
    const fx = loadFixtures();
    const { scanHandle } = await clientfs('scope');
    const { handle, log } = mockHandle(fx.tree());
    const folder = await scanHandle(handle);

    assert.equal(folder.name, 'WoW');
    assert.equal(folder.hasData, true);
    assert.deepEqual(folder.dataFolders, []);
    assert.deepEqual(folder.archives.map((a) => a.name).sort(), ['dbc.MPQ', 'model.MPQ', 'patch-2.MPQ', 'patch.MPQ', 'terrain.MPQ', 'texture.MPQ', 'wmo.MPQ']);
    for (const a of folder.archives) {
        const source = fx.tree().get(`Data/${a.name}`);
        assert.equal(a.size, source.length);
        assert.equal(a.lastModified, 1_700_000_000_000);
        assert.ok(a.blob instanceof Blob);
    }
    assert.equal(folder.exe.name, 'WoW.exe');
    assert.equal(folder.exe.size, fx.read('exe/WoW-1.12.1.5875.exe').length);

    assert.ok(log.length > 0, 'the mock recorded nothing; the scan did not go through the handle');
    for (const entry of log) {
        assert.match(entry.path, ALLOWED_PATH, `scope.js touched ${entry.op} ${entry.path}`);
        if (LISTING.has(entry.op)) {
            assert.match(entry.path, /^(data)?$/i, `listing is allowed on the root and Data only, not ${entry.path}`);
        }
        if (entry.op === 'getFile') {
            assert.match(entry.path, /^(data\/[^/]+\.mpq|wow\.exe)$/i);
        }
    }
    assert.equal(log.filter((e) => e.op === 'read').length, 0, 'scanning must not read any bytes');
});

test('scope.js never opens or lists the WTF canary folder through scan and validation', async () => {
    const fx = loadFixtures();
    const { scanHandle } = await clientfs('scope');
    const { handle, log } = mockHandle(fx.tree(), { emptyDirectories: ['Data/enUS', 'Screenshots', 'WTF/Account/CANARYACC/SavedVariables'] });
    const folder = await scanHandle(handle);
    const result = await validate(folder);
    assert.equal(result.ok, true, result.errors.join(' | '));

    // Names only: Data/enUS is reported, never opened or listed.
    assert.deepEqual(folder.dataFolders, ['enUS']);
    const touched = log.map((e) => `${e.op} ${e.path}`);
    assert.deepEqual(touched.filter((t) => FORBIDDEN.test(t)), [], 'forbidden folders were touched');
    assert.equal(log.filter((e) => /^data\/enus/i.test(e.path)).length, 0, 'Data/enUS must not be opened or listed');
    assert.equal(log.filter((e) => LISTING.has(e.op) && !/^(data)?$/i.test(e.path)).length, 0);
    assert.equal(log.filter((e) => e.op === 'getDirectoryHandle' && !/^data$/i.test(e.path)).length, 0, 'only Data is ever opened as a folder');
    // The only files whose bytes are read are archives and WoW.exe.
    const read = log.filter((e) => e.op === 'read');
    assert.ok(read.length > 0, 'validation read nothing');
    assert.ok(read.every((e) => /^(data\/[^/]+\.mpq|wow\.exe)$/i.test(e.path)), `read: ${[...new Set(read.map((e) => e.path))].join(', ')}`);
    assert.equal(JSON.stringify({ ...folder, archives: folder.archives.map((a) => a.name), exe: folder.exe?.name }).match(/canary|wtf|account/i), null);
});

test('scope.js finds Data and WoW.exe whatever their letter case', async () => {
    const fx = loadFixtures();
    const { scanHandle, scanFileList } = await clientfs('scope');
    const lower = new Map([...fx.tree()].map(([p, bytes]) => [p.startsWith('Data/') ? `data/${p.slice(5).toLowerCase()}` : p === 'WoW.exe' ? 'wow.exe' : p, bytes]));
    const { handle } = mockHandle(lower);
    const folder = await scanHandle(handle);
    assert.equal(folder.hasData, true);
    assert.equal(folder.archives.length, 7);
    assert.equal(folder.exe.name, 'wow.exe');
    assert.ok(folder.archives.every((a) => a.name === a.name.toLowerCase()), 'archives keep the spelling the folder uses');

    const list = mockFileList(lower).list;
    const fromList = scanFileList(list);
    assert.equal((await fromList).archives.length, 7);
});

test('scope.js filters a webkitdirectory FileList by relative path before handing any File on', async () => {
    const fx = loadFixtures();
    const { scanFileList } = await clientfs('scope');
    const files = fx.tree({
        'Data/enUS/locale-enUS.MPQ': 'archives/patch.MPQ',
        'Data/sub/hidden.MPQ': 'archives/patch.MPQ',
        'Data/readme.txt': 'wtf/Config.wtf',
        'WoW.exe.bak': 'exe/WoW-2.4.3.8606.exe',
    });
    const { list, log } = mockFileList(files);
    assert.ok(list.length > 12, 'the list holds the WTF/Interface/Logs files too');

    const spied = [];
    const originals = {};
    for (const level of ['log', 'info', 'warn', 'error', 'debug']) {
        originals[level] = console[level];
        console[level] = (...args) => spied.push(args.map(String).join(' '));
    }
    let folder;
    try {
        folder = await scanFileList(list);
    } finally {
        Object.assign(console, originals);
    }

    assert.equal(folder.kind, 'filelist');
    assert.equal(folder.name, 'WoW');
    assert.equal(folder.hasData, true);
    assert.deepEqual(folder.archives.map((a) => a.name).sort(), ['dbc.MPQ', 'model.MPQ', 'patch-2.MPQ', 'patch.MPQ', 'terrain.MPQ', 'texture.MPQ', 'wmo.MPQ']);
    assert.deepEqual([...folder.dataFolders].sort(), ['enUS', 'sub']);
    assert.equal(folder.exe.name, 'WoW.exe');

    // Dropped entries leave no trace: no count, no log line, no read.
    assert.deepEqual(Object.keys(folder).sort(), ['archives', 'dataFolders', 'exe', 'hasData', 'kind', 'name']);
    assert.deepEqual(spied, [], 'filtered-out entries must not be logged');
    assert.equal(log.length, 0, 'scanning a FileList must not read any file');
    assert.equal(JSON.stringify({ ...folder, archives: folder.archives.map((a) => a.name), exe: folder.exe.name }).match(/canary|wtf|interface|logs/i), null);
});

test('scope.js reports no Data when the picked folder is Data itself or empty', async () => {
    const fx = loadFixtures();
    const { scanHandle, scanFileList } = await clientfs('scope');
    const inner = new Map([['dbc.MPQ', fx.read('archives/dbc.MPQ')], ['patch.MPQ', fx.read('archives/patch.MPQ')]]);

    const picked = await scanHandle(mockHandle(inner, { rootName: 'Data' }).handle);
    assert.equal(picked.hasData, false);
    assert.deepEqual(picked.archives, []);
    assert.equal(picked.exe, null);

    const asList = await scanFileList(mockFileList(inner, { rootName: 'Data' }).list);
    assert.equal(asList.hasData, false);
    assert.deepEqual(asList.archives, []);

    const empty = await scanFileList([]);
    assert.equal(empty.hasData, false);
    assert.equal(empty.exe, null);
    assert.deepEqual((await scanHandle(mockHandle(new Map()).handle)).archives, []);
});

// ==================================================================================================================
// store.js and pool.js

test('store.js keys cache entries by path, archive name, size and lastModified', async () => {
    const { cacheKey } = await clientfs('store');
    const archive = { name: 'terrain.MPQ', size: 123456, lastModified: 1_700_000_000_000 };
    const key = cacheKey('World\\Maps\\Azeroth\\Azeroth_31_33.adt', archive);
    assert.equal(typeof key, 'string');
    assert.equal(cacheKey('world/maps/azeroth/azeroth_31_33.adt', archive), key, 'the logical path is case- and separator-insensitive');
    assert.notEqual(cacheKey('World\\Maps\\Azeroth\\Azeroth_31_34.adt', archive), key);
    assert.notEqual(cacheKey('World\\Maps\\Azeroth\\Azeroth_31_33.adt', { ...archive, name: 'patch.MPQ' }), key);
    assert.notEqual(cacheKey('World\\Maps\\Azeroth\\Azeroth_31_33.adt', { ...archive, size: archive.size + 1 }), key);
    assert.notEqual(cacheKey('World\\Maps\\Azeroth\\Azeroth_31_33.adt', { ...archive, lastModified: archive.lastModified + 1 }), key);
});

test('store.js evicts the least recently used entry beyond its byte budget', async () => {
    const { LruCache, LRU_DEFAULT_BYTES } = await clientfs('store');
    assert.equal(LRU_DEFAULT_BYTES, 512 * 1024 * 1024);
    const cache = new LruCache(100);
    cache.set('a', 'A', 40);
    cache.set('b', 'B', 40);
    assert.equal(cache.get('a'), 'A'); // a is now the most recent
    cache.set('c', 'C', 40); // 120 > 100: b (least recent) goes
    assert.equal(cache.get('b'), undefined);
    assert.equal(cache.get('a'), 'A');
    assert.equal(cache.get('c'), 'C');
    assert.equal(cache.bytes, 80);
    cache.set('c', 'C2', 10); // replacing an entry replaces its cost
    assert.equal(cache.get('c'), 'C2');
    assert.equal(cache.bytes, 50);
    assert.equal(cache.get('missing'), undefined);
});

test('store.js default cache budget is 2 GiB capped by the storage estimate', async () => {
    const { defaultCacheBudget } = await clientfs('store');
    const GiB = 1024 ** 3;
    assert.equal(defaultCacheBudget({ quota: 100 * GiB }), 2 * GiB);
    assert.equal(defaultCacheBudget({ quota: 2 * GiB }), 2 * GiB);
    assert.equal(defaultCacheBudget({ quota: GiB }), GiB);
    assert.equal(defaultCacheBudget({ quota: 0 }), 0);
});

/** An in-memory OPFS (just what OpfsCache calls); `stats.listings` counts directory scans, `stats.gate` holds every writable.close(). */
function mockOpfs() {
    const stats = { listings: 0, gate: null };
    let clock = 1_000;
    const missing = () => Object.assign(new Error('not found'), { name: 'NotFoundError' });
    const makeDirectory = () => {
        const files = new Map();
        const describe = (file) => ({ size: file.data.byteLength, lastModified: file.time, arrayBuffer: async () => file.data.slice().buffer });
        return {
            files,
            kind: 'directory',
            async *entries() {
                stats.listings++;
                for (const [name, file] of [...files]) {
                    yield [name, { kind: 'file', getFile: async () => describe(file) }];
                }
            },
            async getFileHandle(name, { create = false } = {}) {
                if (!files.has(name)) {
                    if (!create) {
                        throw missing();
                    }
                    files.set(name, { data: new Uint8Array(0), time: ++clock });
                }
                const file = files.get(name);
                return {
                    kind: 'file',
                    getFile: async () => describe(file),
                    createWritable: async () => {
                        let pending = null;
                        return {
                            write: async (bytes) => { pending = new Uint8Array(bytes); },
                            close: async () => {
                                await stats.gate;
                                file.data = pending;
                                file.time = ++clock;
                            },
                        };
                    },
                };
            },
            async removeEntry(name) {
                if (!files.delete(name)) {
                    throw missing();
                }
            },
        };
    };
    const directories = new Map();
    const root = {
        async getDirectoryHandle(name, { create = false } = {}) {
            if (!directories.has(name)) {
                if (!create) {
                    throw missing();
                }
                directories.set(name, makeDirectory());
            }
            return directories.get(name);
        },
        async removeEntry(name) {
            if (!directories.delete(name)) {
                throw missing();
            }
        },
    };
    const names = (directory) => [...(directories.get(directory)?.files.keys() ?? [])];
    return { stats, root, names };
}

async function withOpfs(body) {
    const opfs = mockOpfs();
    const saved = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { storage: { getDirectory: async () => opfs.root } } });
    try {
        await body(opfs);
    } finally {
        if (saved) {
            Object.defineProperty(globalThis, 'navigator', saved);
        } else {
            delete globalThis.navigator;
        }
    }
}

test('store.js OpfsCache serialises concurrent writes, keeps the byte count exact and never re-lists the directory', async () => {
    const { OpfsCache, CACHE_DIRECTORY } = await clientfs('store');
    await withOpfs(async (opfs) => {
        const cache = new OpfsCache(() => 250);
        const results = await Promise.all(Array.from({ length: 8 }, (_, i) => cache.set(`k${i}`, new Uint8Array(100).fill(i))));
        assert.deepEqual(results, Array(8).fill(true));
        assert.equal(opfs.names(CACHE_DIRECTORY).length, 2, 'only two 100-byte entries fit 250 bytes');
        assert.equal(await cache.size(), 200);
        assert.equal(opfs.stats.listings, 0, 'the directory did not exist at the first scan and is never listed again, however many writes hit the budget');
        assert.ok(await cache.get('k7'), 'the newest write survives');
        assert.ok(await cache.get('k6'));
        assert.equal(await cache.get('k0'), null, 'the oldest write was evicted first');
        // Rewriting an entry replaces its size and makes it the newest.
        await cache.set('k6', new Uint8Array(40));
        assert.equal(await cache.size(), 140);
        await cache.set('k9', new Uint8Array(120)); // 140 + 120 > 250
        assert.equal(await cache.get('k7'), null, 'k7 is now the oldest write');
        assert.ok(await cache.get('k6'));
        // A fresh instance rebuilds the same count from the directory.
        const again = new OpfsCache(() => 250);
        assert.equal(await again.size(), 160);
        assert.equal(opfs.stats.listings, 1, 'a fresh instance scans the existing directory once');
    });
});

test('store.js OpfsCache.set drops a write whose folder was forgotten while it was in flight', async () => {
    const { OpfsCache, CACHE_DIRECTORY } = await clientfs('store');
    await withOpfs(async (opfs) => {
        const cache = new OpfsCache(() => 1000);
        const heldWrite = async (isCurrent, queueClear) => {
            let release;
            opfs.stats.gate = new Promise((resolve) => { release = resolve; });
            const write = cache.set('tile', new Uint8Array(64), isCurrent);
            const cleared = queueClear ? cache.clear() : Promise.resolve(); // Forget folder's clear, queued behind the write
            while (opfs.names(CACHE_DIRECTORY).length === 0) {
                await new Promise((resolve) => setTimeout(resolve, 1)); // the write has reached its (held) close
            }
            return { write, cleared, release: () => { opfs.stats.gate = null; release(); } };
        };

        // Forget (the generation bump plus clear) lands inside the write window.
        let current = true;
        const first = await heldWrite(() => current, true);
        current = false;
        first.release();
        assert.equal(await first.write, false);
        await first.cleared;
        assert.deepEqual(opfs.names(CACHE_DIRECTORY), [], 'nothing may remain after Forget');
        assert.equal(await cache.size(), 0);

        // The guard alone also removes a write that finished after the generation moved on.
        let still = true;
        const second = await heldWrite(() => still, false);
        still = false;
        second.release();
        assert.equal(await second.write, false);
        assert.deepEqual(opfs.names(CACHE_DIRECTORY), []);
        assert.equal(await cache.size(), 0);

        // A write that starts after the generation moved on never touches OPFS.
        assert.equal(await cache.set('tile3', new Uint8Array(8), () => false), false);
        assert.deepEqual(opfs.names(CACHE_DIRECTORY), []);
    });
});

test('pool.js sizes the worker pool min(4, hardwareConcurrency - 1)', async () => {
    const { poolSizeFor } = await clientfs('pool');
    assert.equal(poolSizeFor(2), 1);
    assert.equal(poolSizeFor(3), 2);
    assert.equal(poolSizeFor(4), 3);
    assert.equal(poolSizeFor(5), 4);
    assert.equal(poolSizeFor(8), 4);
    assert.equal(poolSizeFor(24), 4);
});

class FakeWorker {
    constructor() {
        this.posted = [];
        this.terminated = false;
        this.listeners = new Map();
        this.onmessage = null;
        this.onerror = null;
    }

    postMessage(message) {
        this.posted.push(message);
    }

    terminate() {
        this.terminated = true;
    }

    addEventListener(type, fn) {
        this.listeners.set(type, [...(this.listeners.get(type) ?? []), fn]);
    }

    removeEventListener(type, fn) {
        this.listeners.set(type, (this.listeners.get(type) ?? []).filter((f) => f !== fn));
    }

    reply(data) {
        const event = { data };
        this.onmessage?.(event);
        for (const fn of this.listeners.get('message') ?? []) {
            fn(event);
        }
    }
}

function fakePool(WorkerPool, options = {}) {
    const workers = [];
    const pool = new WorkerPool({
        size: 1,
        spawn: () => {
            const w = new FakeWorker();
            workers.push(w);
            return w;
        },
        ...options,
    });
    return { pool, workers };
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

test('pool.js runs jobs and reports typed worker errors', async () => {
    const { WorkerPool } = await clientfs('pool');
    const { pool, workers } = fakePool(WorkerPool, { size: 2 });
    const first = pool.run('read', { path: 'a' }, { kind: 'file', key: 'a' });
    await flush();
    const sent = workers.flatMap((w) => w.posted);
    assert.equal(sent.length, 1);
    assert.equal(sent[0].op, 'read');
    assert.equal(sent[0].path, 'a');
    assert.equal(typeof sent[0].id, 'number');
    workers.find((w) => w.posted.length).reply({ id: sent[0].id, ok: true, result: 42 });
    assert.equal(await first, 42);

    const second = pool.run('read', { path: 'b' }, { kind: 'file', key: 'b' });
    await flush();
    const carrier = workers.find((w) => w.posted.some((m) => m.path === 'b'));
    const message = carrier.posted.find((m) => m.path === 'b');
    carrier.reply({ id: message.id, ok: false, error: { code: 'bad-file', message: 'damaged' } });
    const error = await refuses(() => second, 'bad-file', 'worker error');
    assert.equal(error.message, 'damaged');
    assert.equal(pool.isBad('b'), false, 'an error reply is not a timeout; only timeouts mark a file bad');
});

test('pool.js terminates a timed-out worker, respawns it and marks the file bad for the session', async () => {
    const { WorkerPool } = await clientfs('pool');
    const { pool, workers } = fakePool(WorkerPool, { fileTimeoutMs: 40, tileTimeoutMs: 80 });
    const hung = pool.run('read', { path: 'bad.blp' }, { kind: 'file', key: 'bad.blp' });
    await flush();
    assert.equal(workers.length, 1);
    await refuses(() => hung, 'timeout', 'a job nobody answers');
    assert.equal(workers[0].terminated, true, 'the stuck Worker is terminated');
    assert.equal(workers.length, 2, 'a fresh Worker is spawned');
    assert.equal(pool.spawned, 2);
    assert.equal(pool.isBad('bad.blp'), true);

    const postedBefore = workers[1].posted.length;
    await refuses(() => pool.run('read', { path: 'bad.blp' }, { kind: 'file', key: 'bad.blp' }), 'bad-file', 'a file marked bad is refused at once');
    assert.equal(workers[1].posted.length, postedBefore, 'a bad file is never sent to a Worker again');

    const fine = pool.run('read', { path: 'fine' }, { kind: 'file', key: 'fine' });
    await flush();
    const message = workers[1].posted.at(-1);
    assert.equal(message.path, 'fine');
    workers[1].reply({ id: message.id, ok: true, result: 'ok' });
    assert.equal(await fine, 'ok');
});

test('pool.js times files out after 5 s and terrain tiles after 30 s', async (t) => {
    const { WorkerPool } = await clientfs('pool');
    const { LIMITS } = await clientfs('limits');
    assert.equal(LIMITS.fileTimeoutMs, 5000);
    assert.equal(LIMITS.tileTimeoutMs, 30000);
    t.mock.timers.enable({ apis: ['setTimeout'] });
    for (const [kind, limit] of [['file', 5000], ['tile', 30000]]) {
        const { pool } = fakePool(WorkerPool);
        let outcome = 'pending';
        pool.run('job', {}, { kind, key: `k-${kind}` }).then(() => { outcome = 'resolved'; }, (e) => { outcome = e.code; });
        await flush();
        t.mock.timers.tick(limit - 1);
        await flush();
        assert.equal(outcome, 'pending', `${kind} job must still be running at ${limit - 1} ms`);
        t.mock.timers.tick(1);
        await flush();
        assert.equal(outcome, 'timeout', `${kind} job must time out at ${limit} ms`);
    }
});

// ==================================================================================================================
// The canary detector and the census over a real client

test('the canary detector finds raw, hex, base64 and url-encoded canaries at any alignment', async () => {
    const fx = loadFixtures();
    const canaries = fx.manifest.canaries;
    assert.ok(canaries.length >= 6);
    const noise = Buffer.from('GET /api/tile/ref.terrain/0/33/31 HTTP/1.1\r\nHost: x\r\n\r\n');
    assert.deepEqual(findCanaries(noise, canaries), [], 'clean traffic reports nothing');
    let seen = 0;
    for (const canary of canaries) {
        const needles = canaryNeedles(canary);
        const kinds = new Set(needles.map((n) => n.kind));
        for (const kind of ['raw', 'hex', 'base64', 'percent']) {
            assert.ok(kinds.has(kind), `${canary.label} has no ${kind} form`);
        }
        for (const needle of needles) {
            const hit = findCanaries(Buffer.concat([noise, Buffer.from('xy'), Buffer.from(needle.value, 'latin1'), Buffer.from('zz')]), canaries);
            assert.ok(hit.some((h) => h.canary === canary.label && h.kind === needle.kind), `${canary.label}/${needle.kind} not detected`);
            seen++;
        }
        // base64 of the canary embedded at three different alignments inside a larger payload.
        for (let pad = 0; pad < 3; pad++) {
            const payload = Buffer.concat([Buffer.alloc(pad, 0x41), Buffer.from(canary.hex, 'hex'), Buffer.from('tail')]);
            const hit = findCanaries(Buffer.from(payload.toString('base64')), canaries);
            assert.ok(hit.some((h) => h.canary === canary.label && h.kind.startsWith('base64')), `${canary.label} base64 at alignment ${pad}`);
        }
    }
    assert.ok(seen >= 6 * 6, `only ${seen} needle checks`);
});

const clientDir = process.env.ARCANECORE_TEST_CLIENT_DIR;
const realSkip = clientDir && fs.existsSync(path.join(clientDir, 'Data')) && process.env[REAL_VARIABLE]
    ? false
    : 'ARCANECORE_TEST_CLIENT_DIR is not set to a real 1.12.1 client (or its sample manifest was not written); real-data parity and the compression census were not run.';
if (realSkip) {
    console.log(`# SKIP clientfs real data: ${realSkip}`);
}

async function realChain() {
    const { openChain } = await clientfs('mpq');
    const dataDir = path.join(clientDir, 'Data');
    const entries = [];
    for (const name of fs.readdirSync(dataDir)) {
        if (/\.mpq$/i.test(name) && !/^backup\.mpq$/i.test(name)) {
            entries.push({ name, blob: await fs.openAsBlob(path.join(dataDir, name)) });
        }
    }
    return { chain: await openChain(entries), entries };
}

test('real client: the JS chain reads the sampled files and the patch order exactly as the C# MpqFileSystem', { skip: realSkip, timeout: 600_000 }, async () => {
    const real = JSON.parse(fs.readFileSync(path.join(process.env[REAL_VARIABLE], 'real.json'), 'utf8'));
    const { chain } = await realChain();
    assert.deepEqual(chain.order, real.order);
    assert.ok(real.samples.length >= 40, `only ${real.samples.length} samples`);
    for (const sample of real.samples) {
        const bytes = await chain.read(sample.path);
        assert.ok(bytes, `${sample.path} is missing`);
        assert.equal(bytes.length, sample.size, sample.path);
        assert.equal(sha(bytes), sample.sha256, sample.path);
        assert.equal(chain.archiveOf(sample.path), sample.archive, sample.path);
    }
    for (const p of real.deleted) {
        assert.equal(await chain.read(p), null, `${p} is hidden by a patch.MPQ deletion marker`);
    }
});

test('real client: census of compression masks per archive; bzip2 stays refused when unset', { timeout: 600_000 }, async () => {
    const fx = loadFixtures();
    const { openArchive, censusMasks } = await clientfs('mpq');
    // The always-on half: whatever the census says, bzip2 is refused until it has run over a real install.
    const c = fx.manifest.malformedArchives.find((m) => m.name === 'bzip2-refused');
    const archive = await openArchive(fx.blob(c.file), { name: 'bzip.MPQ' });
    await refuses(() => archive.read(c.read), 'unsupported-compression', 'bzip2 sector');
    if (realSkip) {
        console.log(`# census skipped: ${realSkip} bzip2 stays refused.`);
        return;
    }
    const { entries } = await realChain();
    let total = 0;
    for (const { name, blob } of entries) {
        const census = await censusMasks(await openArchive(blob, { name }));
        console.log(`# census ${name}: ${JSON.stringify(census)}`);
        total += Object.values(census).reduce((a, b) => a + b, 0);
    }
    assert.ok(total > 0, 'a real client has compressed sectors');
});

// ==================================================================================================================
// Browser: headless Chromium against the loopback harness

const executable = browserFromEnvironment();

/** Opens a harness + page; `helpers` is PAGE_HELPERS or PAGE_HELPERS_NO_PICKER. Resolves once index.js has loaded. */
async function openPage(browser, harness, { helpers = PAGE_HELPERS } = {}) {
    const page = await browser.newPage('about:blank');
    await page.send('Page.addScriptToEvaluateOnNewDocument', { source: helpers });
    await page.send('Page.setInterceptFileChooserDialog', { enabled: true }).catch(() => {});
    await navigate(page, `${harness.origin}/`);
    return page;
}

async function navigate(page, url) {
    await page.evaluate('window.__old = true').catch(() => {});
    await page.send('Page.navigate', { url });
    await page.waitFor('!window.__old && (window.__bootDone || window.__bootError)', 'the harness page to boot');
    const error = await page.evaluate('window.__bootError || null');
    if (error) {
        throw new Error(`Web/js/clientfs/index.js did not load in the browser: ${error}`);
    }
}

const reload = (page, harness) => navigate(page, `${harness.origin}/?r=${Math.random().toString(16).slice(2)}`);
const problems = (page) => page.problems.filter((p) => !p.includes('ERR_ABORTED'));

async function withBrowser(fn, { mode = 'public', helpers = PAGE_HELPERS } = {}) {
    assert.ok(executable, 'ARCANECORE_TEST_BROWSER does not name a Chrome or Edge executable; the clientfs browser tests cannot run (they never skip).');
    const fx = loadFixtures();
    const harness = await new Harness({ mode, fixtureDir: fx.dir }).start();
    let browser = null;
    try {
        browser = await Browser.launch(executable);
        const page = await openPage(browser, harness, { helpers });
        await fn({ fx, harness, browser, page });
    } finally {
        await browser?.close();
        await harness.stop();
    }
}

const stageTree = (page, fx, overrides = {}) => {
    const entries = {};
    for (const [p, relative] of Object.entries(fx.manifest.tree)) {
        entries[p] = relative;
    }
    for (const [p, value] of Object.entries(overrides)) {
        if (value === null) {
            delete entries[p];
        } else {
            entries[p] = value;
        }
    }
    return page.evaluate(`window.__t.stage(${JSON.stringify(entries)})`);
};

const status = (page) => page.evaluate('cf.clientFilesStatus()');
const dialogOpen = (page) => page.evaluate('!!document.querySelector(\'[data-clientfs="dialog"]\')');

async function pickAndWait(page) {
    assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), true, 'the first visit shows the dialog');
    await page.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the first-entry dialog');
    await clickText(page, 'Choose folder');
    await page.waitFor('(async () => (await cf.clientFilesStatus()).state === "ready")()', 'the folder to validate', 60_000);
}

async function assertScopeAudit(page) {
    const calls = await page.evaluate('window.__fsCalls');
    const bad = calls.filter((c) => FORBIDDEN.test(`${c.arg ?? ''}`) || FORBIDDEN.test(`${c.dir ?? ''}/`));
    assert.deepEqual(bad, [], 'the picked folder\'s WTF, Interface, Logs or Screenshots were touched');
    assert.equal(calls.filter((c) => c.arg && /^data$/i.test(c.arg) === false && /\.mpq$/i.test(c.arg) === false && /^wow\.exe$/i.test(c.arg) === false
        && ['getDirectoryHandle', 'getFileHandle'].includes(c.op) && c.dir === '__testwow').length, 0, 'only Data, *.MPQ and WoW.exe are opened by name');
}

test('a module Worker starts under the public CSP header', async () => {
    await withBrowser(async ({ page }) => {
        const header = await page.evaluate("fetch('/app/js/clientfs/worker.js').then((r) => r.headers.get('content-security-policy'))");
        assert.equal(header, publicCsp());
        assert.match(header, /worker-src 'self'/);
        const reply = await page.evaluate(`new Promise((resolve, reject) => {
            const w = new Worker('/app/js/clientfs/worker.js', { type: 'module' });
            const timer = setTimeout(() => reject(new Error('the Worker never answered')), 10000);
            w.onmessage = (e) => { clearTimeout(timer); w.terminate(); resolve(e.data); };
            w.onerror = (e) => { clearTimeout(timer); reject(new Error('worker error: ' + e.message)); };
            w.postMessage({ id: 1, op: 'ping' });
        })`);
        assert.deepEqual(reply, { id: 1, ok: true, result: 'pong' });
        assert.deepEqual(problems(page), []);

        // Control: the same policy must stop a blob: Worker, otherwise "it started" proves nothing.
        const blocked = await page.evaluate(`new Promise((resolve) => {
            const url = URL.createObjectURL(new Blob(['postMessage(1)'], { type: 'text/javascript' }));
            let w;
            try { w = new Worker(url); } catch (e) { resolve('threw ' + e.name); return; }
            const timer = setTimeout(() => { w.terminate(); resolve('started'); }, 3000);
            w.onerror = () => { clearTimeout(timer); resolve('blocked'); };
            w.onmessage = () => { clearTimeout(timer); w.terminate(); resolve('started'); };
        })`);
        assert.notEqual(blocked, 'started', 'a blob: Worker started, so the CSP is not being enforced in this test');
    }, { mode: 'public' });
});

test('a module Worker starts under the local meta CSP (script-src fallback)', async () => {
    await withBrowser(async ({ page }) => {
        assert.equal(await page.evaluate("fetch('/app/js/clientfs/worker.js').then((r) => r.headers.get('content-security-policy'))"), null);
        assert.equal(await page.evaluate("document.querySelector('meta[http-equiv=\"Content-Security-Policy\"]').content"), localCsp());
        assert.doesNotMatch(localCsp(), /worker-src/, 'Local\'s policy has no worker-src; the fallback to script-src is what is being proven');
        const reply = await page.evaluate(`new Promise((resolve, reject) => {
            const w = new Worker('/app/js/clientfs/worker.js', { type: 'module' });
            const timer = setTimeout(() => reject(new Error('the Worker never answered')), 10000);
            w.onmessage = (e) => { clearTimeout(timer); w.terminate(); resolve(e.data); };
            w.onerror = (e) => { clearTimeout(timer); reject(new Error('worker error: ' + e.message)); };
            w.postMessage({ id: 7, op: 'ping' });
        })`);
        assert.deepEqual(reply, { id: 7, ok: true, result: 'pong' });
        const blocked = await page.evaluate(`new Promise((resolve) => {
            const url = URL.createObjectURL(new Blob(['postMessage(1)'], { type: 'text/javascript' }));
            let w;
            try { w = new Worker(url); } catch (e) { resolve('threw ' + e.name); return; }
            const timer = setTimeout(() => { w.terminate(); resolve('started'); }, 3000);
            w.onerror = () => { clearTimeout(timer); resolve('blocked'); };
            w.onmessage = () => { clearTimeout(timer); w.terminate(); resolve('started'); };
        })`);
        assert.notEqual(blocked, 'started', 'a blob: Worker started under the meta policy, so the CSP is not being enforced');
    }, { mode: 'local' });
});

test('dialog flows in headless Chromium (showDirectoryPicker mocked)', async (t) => {
    await withBrowser(async ({ fx, harness, page }) => {
        await stageTree(page, fx);

        await t.test('first entry shows the text and the three buttons', async () => {
            assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), true);
            await page.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog');
            const text = await page.evaluate(textOf('[data-clientfs="dialog"]'));
            for (const sentence of [
                'Atlas draws the world from your own World of Warcraft 1.12.1 (build 5875) installation.',
                'Choose your WoW folder',
                'Files are read in this browser only and are never uploaded.',
                'You can skip this and see an untextured map.',
            ]) {
                assert.ok(text.includes(sentence), `the dialog does not say "${sentence}": ${text}`);
            }
            const role = await page.evaluate('(() => { const d = document.querySelector(\'[data-clientfs="dialog"]\'); return d.getAttribute("role") === "dialog" || d.tagName === "DIALOG"; })()');
            assert.equal(role, true, 'the dialog is not exposed as a dialog');
            const buttons = await page.evaluate('[...document.querySelectorAll(\'[data-clientfs="dialog"] button\')].map((b) => b.textContent.trim())');
            for (const label of ['Choose folder', 'Skip', 'Why?']) {
                assert.ok(buttons.includes(label), `no "${label}" button in ${JSON.stringify(buttons)}`);
            }

            await clickText(page, 'Why?');
            const why = await page.evaluate(textOf('[data-clientfs="dialog"]'));
            assert.match(why, /Data[\\/]\*\.MPQ/, 'Why? must name what is read');
            assert.match(why, /WoW\.exe/);
            assert.match(why, /cache/i, 'Why? must say where the parsed cache is kept');
        });

        await t.test('choosing a folder validates it, stores the handle and calls the picker as specified', async () => {
            await clickText(page, 'Choose folder');
            await page.waitFor('(async () => (await cf.clientFilesStatus()).state === "ready")()', 'the folder to validate', 60_000);
            assert.deepEqual(await page.evaluate('window.__pickerCalls'), [{ id: 'wow', mode: 'read' }]);
            const s = await status(page);
            assert.equal(s.folderName, '__testwow');
            assert.equal(s.build, 5875);
            assert.equal(s.version, '1.12.1.5875');
            assert.deepEqual(s.warnings, []);
            assert.equal(await dialogOpen(page), false, 'the dialog closes after a valid pick');
            const records = await page.evaluate('window.__t.handleRecords()');
            assert.equal(records.count, 1, 'one handle in atlas-client/handles');
            assert.equal(records.holdsHandle, true);
            assert.ok(records.stores.includes('handles'));
            await assertScopeAudit(page);
        });

        await t.test('a later visit asks for one click to re-allow access and calls requestPermission from that click', async () => {
            await reload(page, harness);
            await page.evaluate("window.__perm.query = 'prompt'");
            assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), false, 'a stored choice means no first-entry dialog');
            assert.equal((await status(page)).state, 'needs-permission');
            await page.waitFor('!!document.querySelector(\'[data-clientfs="reallow"]\')', 'the Re-allow access banner');
            assert.match(await page.evaluate(textOf('[data-clientfs="reallow"]')), /Re-allow access/);
            assert.equal(await page.evaluate('window.__perm.requestCalls.length'), 0, 'requestPermission needs a user gesture, so it is not called before the click');
            await clickText(page, 'Re-allow access');
            await page.waitFor('(async () => (await cf.clientFilesStatus()).state === "ready")()', 'access to be restored');
            const calls = await page.evaluate('window.__perm.requestCalls');
            assert.equal(calls.length, 1);
            assert.deepEqual(calls[0].options, { mode: 'read' });
            assert.equal(calls[0].active, true, 'requestPermission ran inside the click\'s user activation');
            assert.equal(await page.evaluate('!!document.querySelector(\'[data-clientfs="reallow"]\')'), false);
        });

        await t.test('a denied re-allow keeps the banner and says the browser blocked access', async () => {
            await reload(page, harness);
            await page.evaluate("window.__perm.query = 'prompt'; window.__perm.request = 'denied'");
            await page.evaluate('cf.showFirstEntryDialogIfNeeded()');
            await page.waitFor('!!document.querySelector(\'[data-clientfs="reallow"]\')', 'the banner');
            await clickText(page, 'Re-allow access');
            await page.waitFor(`${textOf('[data-clientfs="error"]')} === ${JSON.stringify(ERROR_BLOCKED)}`, 'the blocked message');
            assert.equal(await page.evaluate('!!document.querySelector(\'[data-clientfs="reallow"]\')'), true, 'the banner stays so the user can try again');
            assert.equal((await status(page)).state, 'needs-permission');
        });

        assert.deepEqual(problems(page), [], 'console errors, exceptions or CSP violations');
    });
});

test('validation errors reach the dialog and nothing is stored or cached', async () => {
    const variants = [
        { name: 'no Data folder', overrides: Object.fromEntries(Object.keys(loadFixtures().manifest.tree).filter((p) => p !== 'WoW.exe').map((p) => [p, null])), expected: ERROR_NO_DATA },
        { name: 'wrong build', overrides: { 'WoW.exe': 'exe/WoW-2.4.3.8606.exe', 'Data/dbc.MPQ': 'archives/dbc-badcdi.MPQ' }, expected: 'This is build 8606 (expected 5875); Atlas needs the 1.12.1 client' },
        { name: 'damaged header', overrides: { 'Data/dbc.MPQ': 'archives/dbc-damaged.MPQ' }, expected: 'Archive dbc.MPQ is damaged (header)' },
        { name: 'a 2.x+ client', overrides: { 'Data/expansion.MPQ': 'archives/patch.MPQ', 'Data/common.MPQ': 'archives/patch.MPQ' }, expected: ERROR_BC },
    ];
    for (const variant of variants) {
        await withBrowser(async ({ fx, page }) => {
            await stageTree(page, fx, variant.overrides);
            assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), true);
            await page.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog');
            await clickText(page, 'Choose folder');
            await page.waitFor(`${textOf('[data-clientfs="error"]')} !== null`, `the error for ${variant.name}`, 60_000);
            assert.equal(await page.evaluate(textOf('[data-clientfs="error"]')), variant.expected, variant.name);
            assert.notEqual((await status(page)).state, 'ready', variant.name);
            assert.equal(await dialogOpen(page), true, `${variant.name}: the dialog stays open so the user can pick again or skip`);
            const records = await page.evaluate('window.__t.handleRecords()');
            assert.equal(records.count, 0, `${variant.name}: validation comes before the handle is stored`);
            assert.deepEqual(await page.evaluate('window.__t.opfsNames()'), [], `${variant.name}: validation comes before anything is cached`);
            assert.deepEqual(problems(page), [], variant.name);
        });
    }
});

test('a locale folder alone is a warning, not a refusal, in the dialog', async () => {
    await withBrowser(async ({ fx, page }) => {
        await stageTree(page, fx);
        await page.evaluate("window.__t.makeDirectory('Data/enUS')");
        await pickAndWait(page);
        const s = await status(page);
        assert.equal(s.warnings.length, 1);
        assert.match(s.warnings[0], /enUS/);
        await assertScopeAudit(page);
        const listed = await page.evaluate('window.__fsCalls.filter((c) => c.dir === "enUS")');
        assert.deepEqual(listed, [], 'the locale folder is reported by name only and never opened');
    });
});

test('Skip keeps the untextured view and the dialog stays closed on the next visit', async () => {
    await withBrowser(async ({ fx, harness, page }) => {
        await stageTree(page, fx);
        assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), true);
        await page.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog');
        await clickText(page, 'Skip');
        await page.waitFor('!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog to close');
        assert.equal((await status(page)).state, 'skipped');
        assert.deepEqual(await page.evaluate('window.__pickerCalls'), [], 'Skip must not open the picker');
        assert.equal((await page.evaluate('window.__t.handleRecords()')).count, 0);

        await reload(page, harness);
        assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), false, 'a skipped visitor is not asked again');
        assert.equal((await status(page)).state, 'skipped');
        const envelope = await page.evaluate(`cf.localTileProvider().tile('ref.terrain', 0, 33, 31).then((r) => ({ state: r.state, parts: new DataView(r.body).getUint32(0, true), bytes: r.body.byteLength }))`);
        assert.notEqual(envelope.state, 'Ok', 'no client files, no textured terrain');
        assert.deepEqual({ parts: envelope.parts, bytes: envelope.bytes }, { parts: 0, bytes: 4 });
        assert.deepEqual(problems(page), []);
    });
});

/** Hashes everything of a decoded ATG1 chunk except its stats (the C# core stamps path/archive/bytes/buildMs there). Runs in node and, via toString, in the page. */
async function digestChunk(chunk, sha) {
    const enc = new TextEncoder();
    const head = JSON.stringify({
        layer: chunk.layer, tile: chunk.tile, part: chunk.part, flags: chunk.flags, origin: chunk.origin,
        counts: [chunk.vertexCount, chunk.triangleCount, chunk.lineCount, chunk.pointCount], attributes: [...chunk.attributes.entries()],
    });
    const parts = [enc.encode(head)];
    for (const a of [chunk.positions, chunk.triangles, chunk.triangleAttributes, chunk.lines, chunk.lineAttributes, chunk.points, chunk.pointAttributes]) {
        parts.push(new Uint8Array(a.buffer, a.byteOffset, a.byteLength));
    }
    const all = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
    let offset = 0;
    for (const p of parts) {
        all.set(p, offset);
        offset += p.length;
    }
    return sha(all);
}

test('localTileProvider returns Api.tile envelopes whose geometry equals the C# ATG1 (through the Worker pool)', async () => {
    await withBrowser(async ({ fx, page }) => {
        await stageTree(page, fx);
        await pickAndWait(page);
        const { decodeChunk } = await import(WEB_ATG1.href);
        const nodeSha = async (bytes) => crypto.createHash('sha256').update(bytes).digest('hex');
        const results = await page.evaluate(`(async () => {
            const { decodeChunk, splitTileResponse } = await import('/app/js/atg1.js');
            ${digestChunk.toString()}
            const sha = async (bytes) => [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map((b) => b.toString(16).padStart(2, '0')).join('');
            const provider = cf.localTileProvider();
            const out = [];
            for (const [layer, x, y] of ${JSON.stringify(fx.manifest.tiles.flatMap((t) => [[fx.manifest.layers.terrain, t.x, t.y], [fx.manifest.layers.liquid, t.x, t.y]]))}) {
                const r = await provider.tile(layer, 0, x, y, new AbortController().signal);
                const parts = splitTileResponse(r.body);
                out.push({ layer, x, y, state: r.state, detail: r.detail, parts: await Promise.all(parts.map(async (p) => digestChunk(decodeChunk(p), sha))) });
            }
            for (const layer of [${JSON.stringify(fx.manifest.layers.terrain)}, ${JSON.stringify(fx.manifest.layers.liquid)}]) {
                const a = ${JSON.stringify(fx.manifest.absentTile)};
                const r = await provider.tile(layer, a.map, a.x, a.y, new AbortController().signal);
                out.push({ layer, x: a.x, y: a.y, state: r.state, detail: r.detail, parts: splitTileResponse(r.body).length });
            }
            return out;
        })()`);
        let terrain = 0;
        let liquid = 0;
        for (const tile of fx.manifest.tiles) {
            const t = results.find((r) => r.layer === fx.manifest.layers.terrain && r.x === tile.x && r.y === tile.y);
            assert.equal(t.state, 'Ok');
            assert.deepEqual(t.parts, [await digestChunk(decodeChunk(fx.read(tile.terrain)), nodeSha)], `terrain ${tile.x}/${tile.y}`);
            terrain++;
            const w = results.find((r) => r.layer === fx.manifest.layers.liquid && r.x === tile.x && r.y === tile.y);
            if (tile.liquid === null) {
                assert.equal(w.state, 'Empty');
                assert.deepEqual(w.parts, []);
            } else {
                assert.equal(w.state, 'Ok');
                assert.deepEqual(w.parts, [await digestChunk(decodeChunk(fx.read(tile.liquid)), nodeSha)], `liquid ${tile.x}/${tile.y}`);
                liquid++;
            }
        }
        assert.equal(terrain, 4);
        assert.equal(liquid, 3);
        for (const r of results.filter((x) => x.x === fx.manifest.absentTile.x)) {
            assert.equal(r.state, 'Missing');
            assert.equal(r.parts, 0);
        }
        assert.deepEqual(problems(page), []);
    });
});

test('the second load is served from the OPFS cache without reading an archive, and a changed archive misses it', async () => {
    await withBrowser(async ({ fx, harness, page }) => {
        await stageTree(page, fx);
        await pickAndWait(page);
        const tile = fx.manifest.tiles[0];
        const request = `(async () => {
            const r = await cf.localTileProvider().tile(${JSON.stringify(fx.manifest.layers.terrain)}, 0, ${tile.x}, ${tile.y}, new AbortController().signal);
            const digest = [...new Uint8Array(await crypto.subtle.digest('SHA-256', r.body))].map((b) => b.toString(16).padStart(2, '0')).join('');
            return { state: r.state, digest, status: await cf.clientFilesStatus() };
        })()`;
        const first = await page.evaluate(request);
        assert.equal(first.state, 'Ok');
        assert.ok(first.status.archiveReads > 0, 'the first load must read the archives');
        assert.ok(first.status.cacheBytes > 0, 'and leave a parsed result in the cache');
        assert.ok((await page.evaluate('window.__t.opfsNames()')).length >= 1, 'the cache lives in OPFS');

        await reload(page, harness);
        const second = await page.evaluate(request);
        assert.equal(second.state, 'Ok');
        assert.equal(second.digest, first.digest, 'the cached envelope is identical');
        assert.equal(second.status.archiveReads, 0, 'a cache hit must not read any archive');
        assert.ok(second.status.cacheHits >= 1);

        // The same bytes written again move lastModified, which is part of the key: the entry no longer matches.
        await page.evaluate("window.__t.rewrite('Data/terrain.MPQ')");
        await reload(page, harness);
        const third = await page.evaluate(request);
        assert.equal(third.state, 'Ok');
        assert.equal(third.digest, first.digest);
        assert.ok(third.status.archiveReads > 0, 'a changed archive (lastModified) must be read again');
        assert.deepEqual(problems(page), []);
    });
});

test('the webkitdirectory fallback caches parsed results and asks again only when the cache misses', async () => {
    await withBrowser(async ({ fx, harness, page }) => {
        assert.equal(await page.evaluate('typeof window.showDirectoryPicker'), 'undefined', 'this run has no File System Access API');
        assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), true);
        await page.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog');
        await clickText(page, 'Choose folder');
        await page.waitFor('!!document.querySelector(\'input[type=file][webkitdirectory]\')', 'the fallback input');
        // Every file of the folder goes into the FileList, WTF canaries included; scope.js must drop what it does not need.
        const count = await page.evaluate(`window.__t.chooseFiles(${JSON.stringify(fx.manifest.tree)})`);
        assert.equal(count, Object.keys(fx.manifest.tree).length);
        await page.waitFor('(async () => (await cf.clientFilesStatus()).state === "ready")()', 'the folder to validate', 60_000);
        assert.equal((await status(page)).build, 5875);
        assert.equal((await page.evaluate('window.__t.handleRecords()')).count, 0, 'no directory handle exists on this path');

        const tile = fx.manifest.tiles[1];
        const request = (x, y) => `(async () => {
            const r = await cf.localTileProvider().tile(${JSON.stringify(fx.manifest.layers.terrain)}, 0, ${x}, ${y}, new AbortController().signal);
            const digest = [...new Uint8Array(await crypto.subtle.digest('SHA-256', r.body))].map((b) => b.toString(16).padStart(2, '0')).join('');
            return { state: r.state, digest, status: await cf.clientFilesStatus() };
        })()`;
        const first = await page.evaluate(request(tile.x, tile.y));
        assert.equal(first.state, 'Ok');
        assert.ok(first.status.archiveReads > 0);

        // File objects die with the page: after a reload only the cache is left.
        await reload(page, harness);
        const cached = await page.evaluate(request(tile.x, tile.y));
        assert.equal(cached.state, 'Ok');
        assert.equal(cached.digest, first.digest);
        assert.equal(cached.status.archiveReads, 0);
        assert.ok(cached.status.cacheHits >= 1);
        assert.equal(await dialogOpen(page), false, 'a cache hit does not bother the visitor');

        // A tile that was never cached needs the files again, so the dialog comes back.
        const other = fx.manifest.tiles[2];
        await page.evaluate(`window.__pending = cf.localTileProvider().tile(${JSON.stringify(fx.manifest.layers.terrain)}, 0, ${other.x}, ${other.y}, new AbortController().signal).then(() => 'settled', () => 'rejected'); true`);
        await page.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog to reappear on a cache miss', 15_000);
        assert.deepEqual(problems(page), []);
    }, { helpers: PAGE_HELPERS_NO_PICKER });
});

test('Skip on a cache-miss dialog is remembered for the session', async () => {
    await withBrowser(async ({ fx, harness, page }) => {
        assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), true);
        await page.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog');
        await clickText(page, 'Choose folder');
        await page.waitFor('!!document.querySelector(\'input[type=file][webkitdirectory]\')', 'the fallback input');
        await page.evaluate(`window.__t.chooseFiles(${JSON.stringify(fx.manifest.tree)})`);
        await page.waitFor('(async () => (await cf.clientFilesStatus()).state === "ready")()', 'the folder to validate', 60_000);
        await reload(page, harness);

        const layer = JSON.stringify(fx.manifest.layers.terrain);
        const ask = (tile) => `cf.localTileProvider().tile(${layer}, 0, ${tile.x}, ${tile.y}, new AbortController().signal).then((r) => r.state, () => 'rejected')`;
        await page.evaluate(`window.__first = ${ask(fx.manifest.tiles[2])}; true`);
        await page.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog on a cache miss', 15_000);
        await clickText(page, 'Skip');
        await page.waitFor('!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog to close');
        assert.equal(await page.evaluate('window.__first'), 'NotApplicable');

        // The next uncached tile resolves at once and the modal stays closed.
        assert.equal(await page.evaluate(ask(fx.manifest.tiles[1])), 'NotApplicable');
        assert.equal(await dialogOpen(page), false, 'a declined visitor is not asked again this session');
        assert.deepEqual(problems(page), []);
    }, { helpers: PAGE_HELPERS_NO_PICKER });
});

test('localTileProvider rejects at once when its signal aborts while the request waits for files', async () => {
    await withBrowser(async ({ fx, harness, page }) => {
        assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), true);
        await page.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog');
        await clickText(page, 'Choose folder');
        await page.waitFor('!!document.querySelector(\'input[type=file][webkitdirectory]\')', 'the fallback input');
        await page.evaluate(`window.__t.chooseFiles(${JSON.stringify(fx.manifest.tree)})`);
        await page.waitFor('(async () => (await cf.clientFilesStatus()).state === "ready")()', 'the folder to validate', 60_000);
        await reload(page, harness);

        const tile = fx.manifest.tiles[2];
        const outcome = await page.evaluate(`(async () => {
            const controller = new AbortController();
            const pending = cf.localTileProvider().tile(${JSON.stringify(fx.manifest.layers.terrain)}, 0, ${tile.x}, ${tile.y}, controller.signal).then(() => 'settled', (e) => e.name);
            await new Promise((r) => setTimeout(r, 500));
            controller.abort();
            return Promise.race([pending, new Promise((r) => setTimeout(() => r('still waiting'), 2000))]);
        })()`);
        assert.equal(outcome, 'AbortError');
    }, { helpers: PAGE_HELPERS_NO_PICKER });
});

const sha256Page = `const sha256 = async (bytes) => [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map((b) => b.toString(16).padStart(2, '0')).join('');`;

test('the Worker mounts a tile chain in MpqFileSystem load order whatever order the archives arrive in', async () => {
    await withBrowser(async ({ fx, page }) => {
        const patched = fx.manifest.patchedTile;
        const tree = { ...fx.manifest.tree, 'Data/patch-3.MPQ': patched.archive };
        // Alphabetical-descending and reversed load order: the lowest-priority archive arrives last, patch-3 first.
        const names = Object.keys(tree).filter((p) => p.startsWith('Data/')).map((p) => p.slice(5)).sort().reverse();
        const expectedPatched = crypto.createHash('sha256').update(fx.read(patched.terrain)).digest('hex');
        const original = fx.manifest.tiles.find((t) => t.x === patched.x && t.y === patched.y);
        const expectedOriginal = crypto.createHash('sha256').update(fx.read(original.terrain)).digest('hex');
        assert.notEqual(expectedPatched, expectedOriginal, 'the patch must change the tile or the test proves nothing');
        const run = (order) => page.evaluate(`(async () => {
            ${sha256Page}
            const tree = ${JSON.stringify(tree)};
            const archives = [];
            for (const name of ${JSON.stringify(order)}) {
                const blob = await (await fetch('/__fixtures/' + tree['Data/' + name])).blob();
                archives.push({ name, size: blob.size, lastModified: 1700000000000, blob });
            }
            const worker = new Worker('/app/js/clientfs/worker.js', { type: 'module' });
            try {
                const reply = await new Promise((resolve, reject) => {
                    const timer = setTimeout(() => reject(new Error('the Worker never answered')), 20000);
                    worker.onmessage = (e) => { clearTimeout(timer); resolve(e.data); };
                    worker.onerror = (e) => { clearTimeout(timer); reject(new Error('worker error: ' + e.message)); };
                    worker.postMessage({ id: 1, op: 'tile', archives, layer: ${JSON.stringify(fx.manifest.layers.terrain)}, map: ${patched.map}, x: ${patched.x}, y: ${patched.y} });
                });
                if (!reply.ok) { return { error: reply.error }; }
                return { state: reply.result.state, parts: reply.result.parts.length, digest: await sha256(reply.result.parts[0]) };
            } finally { worker.terminate(); }
        })()`);
        const loadOrdered = ['dbc.MPQ', 'model.MPQ', 'texture.MPQ', 'terrain.MPQ', 'wmo.MPQ', 'patch.MPQ', 'patch-2.MPQ', 'patch-3.MPQ'];
        for (const [label, order] of [['reversed', names], ['load order', loadOrdered], ['patches first', [...loadOrdered.slice(5), ...loadOrdered.slice(0, 5)]]]) {
            const r = await run(order);
            assert.equal(r.error, undefined, `${label}: ${JSON.stringify(r.error)}`);
            assert.equal(r.state, 'Ok', label);
            assert.equal(r.parts, 1, label);
            assert.equal(r.digest, expectedPatched, `${label}: the tile must come from patch-3.MPQ, the highest-priority archive`);
        }
        assert.deepEqual(problems(page), []);
    });
});

test('localTileProvider resolves a patched tile from the patch when the FileList arrives in non-load order', async () => {
    await withBrowser(async ({ fx, page }) => {
        const patched = fx.manifest.patchedTile;
        const base = { ...fx.manifest.tree, 'Data/patch-3.MPQ': patched.archive };
        // Data archives alphabetically descending (patch-3 first, dbc last), everything else after them.
        const dataNames = Object.keys(base).filter((p) => p.startsWith('Data/')).sort().reverse();
        const tree = {};
        for (const p of dataNames) {
            tree[p] = base[p];
        }
        for (const p of Object.keys(base).filter((k) => !k.startsWith('Data/'))) {
            tree[p] = base[p];
        }
        assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), true);
        await page.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog');
        await clickText(page, 'Choose folder');
        await page.waitFor('!!document.querySelector(\'input[type=file][webkitdirectory]\')', 'the fallback input');
        await page.evaluate(`window.__t.chooseFiles(${JSON.stringify(tree)})`);
        await page.waitFor('(async () => (await cf.clientFilesStatus()).state === "ready")()', 'the folder to validate', 60_000);
        const digest = await page.evaluate(`(async () => {
            ${sha256Page}
            const r = await cf.localTileProvider().tile(${JSON.stringify(fx.manifest.layers.terrain)}, ${patched.map}, ${patched.x}, ${patched.y}, new AbortController().signal);
            const view = new DataView(r.body);
            const length = view.getUint32(4, true);
            return { state: r.state, parts: view.getUint32(0, true), digest: await sha256(new Uint8Array(r.body, 8, length)) };
        })()`);
        assert.equal(digest.state, 'Ok');
        assert.equal(digest.parts, 1);
        assert.equal(digest.digest, crypto.createHash('sha256').update(fx.read(patched.terrain)).digest('hex'), 'the patched ADT must win over terrain.MPQ');
        assert.deepEqual(problems(page), []);
    }, { helpers: PAGE_HELPERS_NO_PICKER });
});

test('Forget folder while a tile is in flight leaves no cache entry behind', async () => {
    await withBrowser(async ({ fx, page }) => {
        await stageTree(page, fx);
        // The pool keeps its Workers, so the holding Worker class must be in place before the first Worker is spawned.
        await page.evaluate(`(() => {
            window.__held = [];
            window.__hold = false;
            const Base = window.Worker;
            window.Worker = class extends Base {
                set onmessage(handler) {
                    super.onmessage = (event) => { if (window.__hold && event.data?.ok && event.data.result?.parts) { window.__held.push(() => handler(event)); } else { handler(event); } };
                }
            };
            return true;
        })()`);
        await pickAndWait(page);
        const tile = fx.manifest.tiles[1];
        await page.evaluate('cf.openClientFilesSettings()');
        await page.waitFor('[...document.querySelectorAll(\'[data-clientfs] button\')].some((b) => b.textContent.trim() === "Forget folder")', 'the settings panel');
        // Hold every Worker reply so the tile is provably in flight when Forget is clicked.
        await page.evaluate(`(() => {
            window.__hold = true;
            window.__tile = cf.localTileProvider().tile(${JSON.stringify(fx.manifest.layers.terrain)}, 0, ${tile.x}, ${tile.y}, new AbortController().signal).then((r) => r.state, (e) => 'rejected ' + e.message);
            return true;
        })()`);
        await page.waitFor('window.__held.length === 1', 'the Worker to answer the held tile');
        // Click Forget and deliver the held reply in the same task: the reply resumes the tile while Forget is still deleting.
        await page.evaluate(`(() => {
            [...document.querySelectorAll('[data-clientfs] button')].find((b) => b.textContent.trim() === 'Forget folder').click();
            for (const release of window.__held.splice(0)) { release(); }
            return true;
        })()`);
        const settled = await page.evaluate('window.__tile');
        assert.notEqual(settled, 'Ok', 'a tile built for a forgotten folder must not be handed out as if the folder were still there');
        await page.waitFor('(async () => (await cf.clientFilesStatus()).state === "none")()', 'the folder to be forgotten');
        await new Promise((resolve) => setTimeout(resolve, 500));
        assert.deepEqual(await page.evaluate('window.__t.opfsNames()'), [], 'no OPFS cache entry may appear after Forget');
        assert.equal((await status(page)).cacheBytes, 0);
        assert.equal((await page.evaluate('window.__t.handleRecords()')).count, 0);
        assert.deepEqual(problems(page), []);
    });
});

test('settings show folder, build and cache size; Forget folder deletes the handle and the cache; the budget slider is capped', async () => {
    await withBrowser(async ({ fx, page }) => {
        await stageTree(page, fx);
        await pickAndWait(page);
        const tile = fx.manifest.tiles[0];
        await page.evaluate(`cf.localTileProvider().tile(${JSON.stringify(fx.manifest.layers.terrain)}, 0, ${tile.x}, ${tile.y}, new AbortController().signal)`);

        await page.evaluate('cf.openClientFilesSettings()');
        await page.waitFor('!!document.querySelector(\'[data-clientfs="settings"]\')', 'the settings panel');
        const text = await page.evaluate(textOf('[data-clientfs="settings"]'));
        assert.ok(text.includes('__testwow'), `the folder name is missing: ${text}`);
        assert.ok(text.includes('5875'), `the build is missing: ${text}`);
        assert.match(text, /\d+(\.\d+)?\s?(B|KB|KiB|MB|MiB|GB|GiB)\b/, 'the cache size is missing');
        const buttons = await page.evaluate('[...document.querySelectorAll(\'[data-clientfs="settings"] button\')].map((b) => b.textContent.trim())');
        assert.ok(buttons.includes('Change folder') && buttons.includes('Forget folder'), JSON.stringify(buttons));

        // Cache budget: default 2 GiB capped by the storage estimate; the slider moves it and cannot pass the quota.
        const quota = await page.evaluate('navigator.storage.estimate().then((e) => e.quota)');
        const before = await status(page);
        assert.equal(before.cacheBudgetBytes, Math.min(2 * 1024 ** 3, quota));
        const target = await page.evaluate(`(() => {
            const s = document.querySelector('input[type=range][data-clientfs="budget"]');
            if (!s) return null;
            const goal = Number(s.value) === Number(s.min) ? s.max : s.min;
            s.value = goal;
            s.dispatchEvent(new Event('input', { bubbles: true }));
            s.dispatchEvent(new Event('change', { bubbles: true }));
            return goal;
        })()`);
        assert.notEqual(target, null, 'no budget slider');
        const after = await status(page);
        assert.notEqual(after.cacheBudgetBytes, before.cacheBudgetBytes, 'moving the slider changes the budget');
        assert.ok(after.cacheBudgetBytes <= quota && after.cacheBudgetBytes >= 0);

        // Change folder reopens the picker and re-validates.
        await clickText(page, 'Change folder');
        await page.waitFor('window.__pickerCalls.length === 2 && (async () => (await cf.clientFilesStatus()).state === "ready")()', 'the picker to run again', 60_000);

        // Forget folder.
        await page.evaluate('cf.openClientFilesSettings()');
        await clickText(page, 'Forget folder');
        await page.waitFor('(async () => (await cf.clientFilesStatus()).state === "none")()', 'the folder to be forgotten');
        assert.equal((await page.evaluate('window.__t.handleRecords()')).count, 0, 'the handle is deleted');
        assert.deepEqual(await page.evaluate('window.__t.opfsNames()'), [], 'the OPFS cache is deleted');
        assert.equal((await status(page)).cacheBytes, 0);
        assert.equal(await page.evaluate('cf.showFirstEntryDialogIfNeeded()'), true, 'a forgotten folder means a first visit again');
        assert.deepEqual(problems(page), []);
    });
});

test('never uploaded: no request, header, body or WebSocket frame carries a canary, and no body passes 4 KiB', async () => {
    const fx = loadFixtures();
    const harness = await new Harness({ mode: 'public', fixtureDir: fx.dir }).start();
    assert.ok(executable, 'ARCANECORE_TEST_BROWSER does not name a Chrome or Edge executable; this test never skips.');
    let browser = null;
    try {
        browser = await Browser.launch(executable);
        const login =`(async () => {
            const response = await fetch('/api/public/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ user: 'tester' }) });
            const ws = new WebSocket('ws://' + location.host + '/api/ws');
            const welcome = await new Promise((resolve, reject) => {
                ws.onopen = () => ws.send(JSON.stringify({ type: 'hello' }));
                ws.onmessage = (e) => resolve(e.data);
                ws.onerror = () => reject(new Error('websocket failed'));
                setTimeout(() => reject(new Error('no welcome frame')), 8000);
            });
            ws.close();
            return { status: response.status, welcome };
        })()`;
        const requestTile = `(async () => {
            let ok = 0;
            for (const t of ${JSON.stringify(fx.manifest.tiles)}) {
                const r = await cf.localTileProvider().tile(${JSON.stringify(fx.manifest.layers.terrain)}, 0, t.x, t.y, new AbortController().signal);
                if (r.state === 'Ok') ok++;
            }
            return ok;
        })()`;

        // Scenario A: the handle path, with the full folder (WTF canaries included) on disk.
        const a = await openPage(browser, harness, { helpers: PAGE_HELPERS });
        await stageTree(a, fx);
        await pickAndWait(a);
        assert.equal(await a.evaluate(requestTile), 4);
        assert.deepEqual(await a.evaluate(login), { status: 200, welcome: '{"type":"welcome"}' });
        await a.evaluate('cf.openClientFilesSettings()');
        await a.waitFor('!!document.querySelector(\'[data-clientfs="settings"]\')', 'settings');
        await a.evaluate(`(() => { const s = document.querySelector('input[type=range][data-clientfs="budget"]'); s.value = s.min; s.dispatchEvent(new Event('input', { bubbles: true })); s.dispatchEvent(new Event('change', { bubbles: true })); })()`);
        await clickText(a, 'Change folder');
        await a.waitFor('window.__pickerCalls.length === 2', 'the second pick');
        await a.waitFor('(async () => (await cf.clientFilesStatus()).state === "ready")()', 'ready again', 60_000);
        assert.deepEqual(problems(a), []);
        // Scenario B starts from a visitor who has never been here: wipe IndexedDB and OPFS of the origin.
        await a.send('Storage.clearDataForOrigin', { origin: harness.origin, storageTypes: 'all' });

        // Scenario B: the FileList path in a fresh page (no picker), every file handed to the input.
        const b = await openPage(browser, harness, { helpers: PAGE_HELPERS_NO_PICKER });
        assert.equal(await b.evaluate('cf.showFirstEntryDialogIfNeeded()'), true);
        await b.waitFor('!!document.querySelector(\'[data-clientfs="dialog"]\')', 'the dialog');
        await clickText(b, 'Choose folder');
        await b.waitFor('!!document.querySelector(\'input[type=file][webkitdirectory]\')', 'the fallback input');
        await b.evaluate(`window.__t.chooseFiles(${JSON.stringify(fx.manifest.tree)})`);
        await b.waitFor('(async () => (await cf.clientFilesStatus()).state === "ready")()', 'the fallback pick to validate', 60_000);
        assert.equal(await b.evaluate(requestTile), 4);
        assert.deepEqual(await b.evaluate(login), { status: 200, welcome: '{"type":"welcome"}' });
        assert.deepEqual(problems(b), []);
    } finally {
        await browser?.close();
        await harness.stop();
    }

    // The recording must have seen real traffic, including the Worker's own script request and both WebSocket directions.
    assert.ok(harness.requests.length > 20, `only ${harness.requests.length} requests were recorded`);
    assert.ok(harness.requests.some((r) => r.url.includes('/app/js/clientfs/worker.js')), 'the Worker script request was not recorded');
    assert.ok(harness.requests.some((r) => r.method === 'POST' && r.url === '/api/public/login' && r.body.length > 0), 'the login POST was not recorded');
    assert.ok(harness.frames.some((f) => f.direction === 'client') && harness.frames.some((f) => f.direction === 'server'), 'WebSocket frames are missing a direction');
    assert.ok(harness.maxBodyBytes() <= 4096, `a request body or frame of ${harness.maxBodyBytes()} bytes was sent`);

    const leaks = [];
    for (const { where, data } of harness.haystacks()) {
        for (const hit of findCanaries(data, fx.manifest.canaries)) {
            leaks.push(`${hit.canary} (${hit.kind}) in ${where}`);
        }
    }
    assert.deepEqual(leaks, [], 'client-derived bytes left the browser');
});

test('the recording catches a deliberate leak in every encoding (positive control)', async () => {
    await withBrowser(async ({ fx, harness, page }) => {
        const [mpq, , , config, , blp] = fx.manifest.canaries;
        const raw = Buffer.from(config.hex, 'hex').toString('latin1');
        const base64 = Buffer.from(blp.hex, 'hex').toString('base64');
        const urlEncoded = [...Buffer.from(blp.hex, 'hex')].map((b) => `%${b.toString(16).padStart(2, '0')}`).join('');
        const mpqHex = mpq.hex;
        await page.evaluate(`(async () => {
            await fetch('/api/public/login', { method: 'POST', body: ${JSON.stringify(raw)} });
            await fetch('/api/public/login', { method: 'POST', body: ${JSON.stringify(base64)} });
            await fetch('/api/public/login?leak=${urlEncoded}', { method: 'POST' });
            const ws = new WebSocket('ws://' + location.host + '/api/ws');
            await new Promise((resolve) => { ws.onopen = () => { ws.send(${JSON.stringify(mpqHex)}); resolve(); }; });
            await new Promise((resolve) => setTimeout(resolve, 300));
            ws.close();
        })()`);
        await sleep(200);
        const labels = new Set();
        for (const { data } of harness.haystacks()) {
            for (const hit of findCanaries(data, fx.manifest.canaries)) {
                labels.add(`${hit.canary}/${hit.kind}`);
            }
        }
        for (const wanted of ['config-wtf/raw', 'blp-bytes/base64', 'blp-bytes/percent', 'mpq-member-name/hex']) {
            assert.ok(labels.has(wanted), `the leak ${wanted} was not detected; found ${[...labels].join(', ')}`);
        }
    });
});
