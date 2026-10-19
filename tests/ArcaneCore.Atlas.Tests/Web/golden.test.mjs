// Parity between the browser modules and the C# core. The data comes from the .NET test that runs this file: chunks written
// by GeometryWire.Write and the answers of CameraMath, WorldGrid, LayerCatalog and ViewState. The expected values are the
// C# values, not a restatement of the JS formulas. The test names are checked by the .NET side, so renaming one is a
// change to WebScriptTests too.

import assert from 'node:assert/strict';
import test from 'node:test';
import { Atg1Error, chunkBounds, decodeChunk, splitTileResponse } from '../../../tools/ArcaneCore.Atlas/Web/js/atg1.js';
import * as camera from '../../../tools/ArcaneCore.Atlas/Web/js/camera.js';
import { FALLBACK_LAYERS, GROUPS } from '../../../tools/ArcaneCore.Atlas/Web/js/catalog.js';
import * as tiles from '../../../tools/ArcaneCore.Atlas/Web/js/tiles.js';
import { composePatch, mergePatch } from '../../../tools/ArcaneCore.Atlas/Web/js/util.js';
import { MAX_LOAD_RADIUS, defaultView, normalizeView } from '../../../tools/ArcaneCore.Atlas/Web/js/view.js';
import { encodeChunk } from './support/atg1-writer.mjs';
import { close, closeAll, loadGolden, readGoldenBytes } from './support/golden.mjs';

function sameArray(actual, expected, what) {
    assert.equal(actual.length, expected.length, `${what}: length`);
    for (let i = 0; i < expected.length; i++) {
        if (actual[i] !== expected[i]) {
            assert.fail(`${what}[${i}]: ${actual[i]} !== ${expected[i]}`);
        }
    }
    return expected.length;
}

function attributesObject(map) {
    return Object.fromEntries([...map].map(([k, v]) => [String(k), v]));
}

test('ATG1 decoder matches GeometryWire.Write', async (t) => {
    const golden = loadGolden();
    assert.ok(golden.chunks.length >= 8, `only ${golden.chunks.length} golden chunks`);
    let compared = 0;

    await t.test('every chunk decodes to what the C# writer was given', () => {
        for (const entry of golden.chunks) {
            const bytes = readGoldenBytes(entry.file);
            const chunk = decodeChunk(bytes);
            const e = entry.expect;
            const what = entry.name;
            assert.equal(chunk.layer, e.layer, `${what}: layer`);
            assert.deepEqual(chunk.tile, { map: e.tile[0], x: e.tile[1], y: e.tile[2] }, `${what}: tile`);
            assert.equal(chunk.part, e.part, `${what}: part`);
            assert.equal(chunk.flags, e.flags, `${what}: flags`);
            assert.deepEqual(chunk.origin, e.origin, `${what}: origin`);
            assert.equal(chunk.vertexCount, e.vertexCount, `${what}: vertices`);
            assert.equal(chunk.triangleCount, e.triangleCount, `${what}: triangles`);
            assert.equal(chunk.lineCount, e.lineCount, `${what}: lines`);
            assert.equal(chunk.pointCount, e.pointCount, `${what}: points`);
            assert.equal(chunk.byteLength, e.byteLength, `${what}: byte length`);
            assert.equal(chunk.byteLength, bytes.length, `${what}: file length`);
            assert.deepEqual(attributesObject(chunk.attributes), e.attributes, `${what}: attribute table`);
            assert.deepEqual(chunk.stats, e.stats, `${what}: stats`);
            const bounds = chunkBounds(chunk);
            if (e.bounds === null) {
                assert.equal(bounds, null, `${what}: an empty chunk has no bounds`);
            } else {
                compared += closeAll(bounds.min, e.bounds.min, `${what}: bounds.min`);
                compared += closeAll(bounds.max, e.bounds.max, `${what}: bounds.max`);
            }
            if (e.positions) {
                compared += sameArray(chunk.positions, e.positions, `${what}: positions`);
                compared += sameArray(chunk.triangles, e.triangles, `${what}: triangles`);
                compared += sameArray(chunk.triangleAttributes, e.triangleAttributes, `${what}: triangle attributes`);
                compared += sameArray(chunk.lines, e.lines, `${what}: lines`);
                compared += sameArray(chunk.lineAttributes, e.lineAttributes, `${what}: line attributes`);
                compared += sameArray(chunk.points, e.points, `${what}: points`);
                compared += sameArray(chunk.pointAttributes, e.pointAttributes, `${what}: point attributes`);
            } else {
                const sum = (array) => {
                    let total = 0;
                    for (let i = 0; i < array.length; i++) {
                        total += array[i];
                    }
                    return total;
                };
                assert.equal(sum(chunk.positions), e.sums.positions, `${what}: position sum`);
                assert.equal(sum(chunk.triangles), e.sums.triangles, `${what}: triangle index sum`);
                assert.equal(sum(chunk.triangleAttributes), e.sums.triangleAttributes, `${what}: triangle attribute sum`);
                assert.equal(sum(chunk.lines), e.sums.lines, `${what}: line index sum`);
                assert.equal(sum(chunk.lineAttributes), e.sums.lineAttributes, `${what}: line attribute sum`);
                sameArray(chunk.positions.subarray(0, 9), e.firstPositions, `${what}: first positions`);
                sameArray(chunk.positions.subarray(chunk.positions.length - 9), e.lastPositions, `${what}: last positions`);
                sameArray(chunk.triangles.subarray(0, 9), e.firstTriangles, `${what}: first triangles`);
                sameArray(chunk.triangles.subarray(chunk.triangles.length - 9), e.lastTriangles, `${what}: last triangles`);
                assert.ok(chunk.triangleCount > 10_000 && chunk.vertexCount > 9_000, `${what}: the large chunk is not large`);
                compared += chunk.positions.length;
            }
        }
        assert.ok(compared > 10_000, `only ${compared} values were compared`);
    });

    await t.test('the arrays are views onto the response when it is aligned, copies when it is not', () => {
        const aligned = readGoldenBytes('terrain.bin');
        const view = decodeChunk(aligned);
        assert.equal(view.positions.buffer, aligned.buffer, 'an aligned chunk is viewed in place');
        const shifted = new Uint8Array(aligned.length + 1);
        shifted.set(aligned, 1);
        const odd = shifted.subarray(1);
        assert.equal(odd.byteOffset % 4, 1);
        const copy = decodeChunk(odd);
        assert.notEqual(copy.positions.buffer, odd.buffer, 'a misaligned chunk is copied once');
        sameArray(copy.positions, view.positions, 'misaligned positions');
        sameArray(copy.triangles, view.triangles, 'misaligned triangles');
        assert.deepEqual(copy.stats, view.stats);
    });

    await t.test('a tile response splits into the parts and each part decodes, on every alignment', () => {
        const envelope = readGoldenBytes(golden.envelope.file);
        const parts = splitTileResponse(envelope);
        assert.equal(parts.length, golden.envelope.parts.length);
        const alignments = new Set();
        parts.forEach((part, i) => {
            const entry = golden.chunks.find((c) => c.name === golden.envelope.parts[i]);
            assert.ok(entry, `no golden chunk named ${golden.envelope.parts[i]}`);
            assert.deepEqual([...part], [...readGoldenBytes(entry.file)], `${entry.name}: part bytes`);
            alignments.add(part.byteOffset % 4);
            const chunk = decodeChunk(part);
            assert.equal(chunk.layer, entry.expect.layer);
            assert.equal(chunk.vertexCount, entry.expect.vertexCount);
            assert.deepEqual(chunk.stats, entry.expect.stats);
        });
        assert.ok(alignments.size >= 3, `the envelope only exercised alignments ${[...alignments]}; the misaligned-copy path is untested`);
        assert.throws(() => splitTileResponse(envelope.subarray(0, envelope.length - 1)), Atg1Error);
        assert.throws(() => splitTileResponse(new Uint8Array([2, 0, 0, 0, 0, 0, 0, 0])), Atg1Error);
        assert.throws(() => splitTileResponse(new Uint8Array([0, 0, 0, 0, 1])), Atg1Error);
        assert.deepEqual(splitTileResponse(new Uint8Array([0, 0, 0, 0])), []);
    });

    await t.test('every corrupt variant is refused, as GeometryWire.Read refuses it', () => {
        assert.ok(golden.corrupt.length >= 10, `only ${golden.corrupt.length} corrupt variants`);
        for (const variant of golden.corrupt) {
            assert.throws(() => decodeChunk(readGoldenBytes(variant.file)), Atg1Error, `${variant.name} was accepted`);
        }
        decodeChunk(readGoldenBytes('terrain.bin'));
    });
});

test('ATG1 test writer is byte-identical to GeometryWire.Write', () => {
    const golden = loadGolden();
    let identical = 0;
    let prefixOnly = 0;
    for (const entry of golden.chunks) {
        if (!entry.expect.positions) {
            continue;
        }
        const e = entry.expect;
        const theirs = readGoldenBytes(entry.file);
        const ours = encodeChunk(e);
        if (entry.byteEqual) {
            assert.deepEqual([...ours], [...theirs], `${entry.name}: the JS writer's bytes differ from GeometryWire.Write's`);
            identical++;
        } else {
            // The C# writer escapes non-ASCII in the JSON trailer, the JS one does not: compare everything before the trailer
            // and require both trailers to decode to the same chunk.
            const arrays = e.positions.length + e.triangles.length + e.triangleAttributes.length + e.lines.length
                + e.lineAttributes.length + e.points.length + e.pointAttributes.length;
            const end = 48 + arrays * 4;
            assert.deepEqual([...ours.subarray(0, end)], [...theirs.subarray(0, end)], `${entry.name}: header and arrays`);
            const a = decodeChunk(ours);
            const b = decodeChunk(theirs);
            assert.deepEqual(a.stats, b.stats, `${entry.name}: stats`);
            assert.deepEqual(attributesObject(a.attributes), attributesObject(b.attributes), `${entry.name}: attributes`);
            assert.notDeepEqual([...ours], [...theirs], `${entry.name}: expected the escaped non-ASCII trailer to differ; the case no longer tests that`);
            prefixOnly++;
        }
    }
    assert.ok(identical >= 6, `only ${identical} chunks were compared byte for byte`);
    assert.ok(prefixOnly >= 1, 'the non-ASCII trailer case did not run');
});

test('camera maths matches CameraMath', () => {
    const golden = loadGolden();
    assert.ok(golden.cameras.length >= 40, `only ${golden.cameras.length} golden cameras`);
    const modes = new Set();
    let compared = 0;
    let behind = 0;
    let visible = 0;
    for (const entry of golden.cameras) {
        const c = entry.camera;
        const what = `${c.mode} yaw ${c.yaw} pitch ${c.pitch} distance ${c.distance} aspect ${entry.aspect}`;
        modes.add(c.mode);
        compared += closeAll(camera.eye(c), entry.eye, `${what}: eye`);
        compared += closeAll(camera.direction(c), entry.direction, `${what}: direction`);
        compared += closeAll(camera.up(c), entry.up, `${what}: up`);
        const planes = camera.planes(c);
        close(planes.near, entry.planes.near, `${what}: near`);
        close(planes.far, entry.planes.far, `${what}: far`);
        compared += 2 + closeAll(camera.viewMatrix(c), entry.view, `${what}: view`);
        compared += closeAll(camera.projectionMatrix(c, entry.aspect), entry.projection, `${what}: projection`);
        for (const p of entry.project) {
            const got = camera.project(c, p.world, entry.aspect);
            if (p.result === null) {
                assert.equal(got, null, `${what}: ${p.world} is behind the camera`);
                behind++;
            } else {
                assert.notEqual(got, null, `${what}: ${p.world} is in front of the camera`);
                close(got.x, p.result.x, `${what}: project x`);
                close(got.y, p.result.y, `${what}: project y`);
                close(got.depth, p.result.depth, `${what}: project depth`);
                compared += 3;
                visible++;
            }
        }
        for (const r of entry.rays) {
            const got = r.maxDistance === null
                ? camera.rayThrough(c, r.mapId, r.ndc[0], r.ndc[1], entry.aspect)
                : camera.rayThrough(c, r.mapId, r.ndc[0], r.ndc[1], entry.aspect, r.maxDistance);
            assert.equal(got.mapId, r.mapId);
            compared += closeAll(got.origin, r.origin, `${what}: ray origin`);
            compared += closeAll(got.direction, r.direction, `${what}: ray direction`);
            close(got.maxDistance, r.limit, `${what}: ray limit`);
            compared++;
        }
    }
    assert.deepEqual([...modes].sort(), ['fly', 'orbit', 'top'], 'all three camera modes must be covered');
    assert.ok(behind >= 20 && visible >= 100, `projection cases: ${behind} behind the camera, ${visible} in front`);
    assert.ok(compared > 5_000, `only ${compared} values were compared`);

    // The comparison can fail: nudging the yaw by a thousandth of a degree moves some view matrix element by more than the tolerance.
    const first = golden.cameras[1];
    const nudged = camera.viewMatrix({ ...first.camera, yaw: first.camera.yaw + 0.001 });
    assert.throws(() => closeAll(nudged, first.view, 'nudged view'), /differs from the C# value/);
});

test('tile maths matches WorldGrid', () => {
    const golden = loadGolden();
    const g = golden.tiles;
    assert.equal(tiles.TILES_PER_SIDE, g.constants.tilesPerSide);
    assert.equal(tiles.TILE_SIZE, g.constants.tileSize, 'the tile size is the server float widened, bit for bit');
    assert.equal(tiles.MAP_RESOLUTION, g.constants.mapResolution);
    assert.equal(tiles.SQUARE_SIZE, g.constants.squareSize);

    assert.ok(g.points.length >= 3_000, `only ${g.points.length} golden positions`);
    let inside = 0;
    let outside = 0;
    let naiveDisagrees = 0;
    for (const p of g.points) {
        const got = tiles.tileOf(p.x, p.y);
        const what = `(${p.x}, ${p.y})`;
        if (p.tile === null) {
            assert.equal(got, null, `${what}: outside the map`);
            outside++;
        } else {
            assert.deepEqual(got, { x: p.tile[0], y: p.tile[1] }, `${what}: tile`);
            inside++;
        }
        assert.deepEqual(tiles.cellOf(p.x, p.y), { x: p.cell[0], y: p.cell[1] }, `${what}: cell`);
        assert.deepEqual(tiles.squareOf(p.x, p.y), { x: p.square[0], y: p.square[1] }, `${what}: square`);
        // What a double-precision port would answer; the float32 rounding must matter for the test to have teeth.
        const nx = Math.trunc(32 - p.x / 533.33333);
        const ny = Math.trunc(32 - p.y / 533.33333);
        const naive = nx >= 0 && nx < 64 && ny >= 0 && ny < 64 ? { x: nx, y: ny } : null;
        if (JSON.stringify(naive) !== JSON.stringify(got)) {
            naiveDisagrees++;
        }
    }
    assert.ok(inside > 1_000 && outside > 100, `${inside} positions inside the map and ${outside} outside`);
    assert.ok(naiveDisagrees > 0, 'no golden position separates float32 tile math from double math; add tile-edge positions');

    assert.ok(g.tiles.length >= 8);
    for (const tile of g.tiles) {
        const what = `tile ${tile.x}, ${tile.y}`;
        closeAll(tiles.tileOrigin(tile.x, tile.y), tile.origin, `${what}: origin`);
        const b = tiles.tileBounds(tile.x, tile.y);
        close(b.minX, tile.bounds.minX, `${what}: minX`);
        close(b.maxX, tile.bounds.maxX, `${what}: maxX`);
        close(b.minY, tile.bounds.minY, `${what}: minY`);
        close(b.maxY, tile.bounds.maxY, `${what}: maxY`);
        closeAll(tiles.tileCentre(tile.x, tile.y), tile.centre, `${what}: centre`);
        assert.deepEqual(tiles.tileOf(tile.centre[0], tile.centre[1]), { x: tile.x, y: tile.y }, `${what}: its centre is in it`);
        for (const f of tile.files) {
            assert.equal(tiles.mapFileName(f.map, tile.x, tile.y), f.terrain, `${what}: terrain file`);
            assert.equal(tiles.navTileFileName(f.map, tile.x, tile.y), f.nav, `${what}: navmesh file`);
            assert.equal(tiles.vmapTileFileName(f.map, tile.x, tile.y), f.vmap, `${what}: vmap file`);
        }
    }

    assert.ok(g.around.length >= 6);
    let neighbours = 0;
    for (const a of g.around) {
        const got = tiles.around(a.x, a.y, a.radius).map((tile) => [tile.x, tile.y]);
        assert.deepEqual(got, a.tiles, `around (${a.x}, ${a.y}) radius ${a.radius}`);
        neighbours += got.length;
    }
    assert.ok(neighbours > 50 && g.around.some((a) => a.tiles.length === 0), 'around must cover both clipped and empty results');

    assert.ok(g.grids.length >= 5);
    for (const grid of g.grids) {
        assert.deepEqual(tiles.tileOfGrid(grid.gx, grid.gy), { x: grid.tile[0], y: grid.tile[1] }, `grid ${grid.gx}, ${grid.gy}`);
    }
});

test('layer catalog matches LayerCatalog', () => {
    const golden = loadGolden();
    assert.ok(golden.catalog.length >= 30, `only ${golden.catalog.length} C# layers`);
    assert.equal(FALLBACK_LAYERS.length, golden.catalog.length, 'the fallback catalog and LayerCatalog.All differ in length');
    golden.catalog.forEach((cs, i) => {
        const js = FALLBACK_LAYERS[i];
        assert.equal(js.id, cs.id, `layer ${i}: id (the catalogs must list layers in the same order)`);
        assert.equal(js.group, cs.group, `${cs.id}: group`);
        assert.equal(js.view, cs.view, `${cs.id}: view`);
        assert.equal(js.title, cs.title, `${cs.id}: title`);
        assert.equal(js.defaultColor, cs.defaultColor, `${cs.id}: default colour`);
        assert.equal(js.defaultVisible, cs.defaultVisible, `${cs.id}: default visibility`);
        assert.deepEqual(js.attributeColors, cs.attributeColors, `${cs.id}: attribute colours`);
        assert.equal(js.tiled, cs.tiled, `${cs.id}: tiled`);
    });
    const csharpGroups = [...new Set(golden.catalog.map((l) => l.group))];
    assert.deepEqual(GROUPS.map((x) => x.id), csharpGroups, 'panel groups, in panel order');
});

test('view helpers match ViewState', () => {
    const golden = loadGolden();
    const v = golden.view;
    assert.deepEqual(defaultView(), v.default, 'defaultView() is ViewState.Default');
    assert.deepEqual(normalizeView(v.default), v.default, 'a complete host view passes through unchanged');

    assert.ok(v.patches.length >= 7);
    for (const { patch, result } of v.patches) {
        assert.deepEqual(normalizeView(mergePatch(defaultView(), patch)), result, `view.set ${JSON.stringify(patch)}`);
    }

    assert.ok(v.sequences.length >= 3);
    for (const { patches, result } of v.sequences) {
        let state = defaultView();
        for (const patch of patches) {
            state = normalizeView(mergePatch(state, patch));
        }
        assert.deepEqual(state, result, 'applying the patches one after another');
        const composed = patches.reduce((a, b) => composePatch(a, b));
        assert.deepEqual(normalizeView(mergePatch(defaultView(), composed)), result, 'applying their composition once');
    }

    assert.equal(camera.MIN_DISTANCE, v.limits.minDistance);
    assert.equal(camera.MAX_DISTANCE, v.limits.maxDistance);
    assert.equal(camera.MIN_FOV, v.limits.minFov);
    assert.equal(camera.MAX_FOV, v.limits.maxFov);
    assert.equal(camera.MAX_PITCH, v.limits.maxPitch);
    assert.equal(camera.TOP_EYE_HEIGHT, v.limits.topEyeHeight);
    assert.equal(MAX_LOAD_RADIUS, v.limits.maxLoadRadius);

    assert.ok(golden.mergePatch.length >= 15);
    for (const { target, patch, result } of golden.mergePatch) {
        assert.deepEqual(mergePatch(target, patch), result, `RFC 7396: ${JSON.stringify(target)} + ${JSON.stringify(patch)}`);
    }
});
