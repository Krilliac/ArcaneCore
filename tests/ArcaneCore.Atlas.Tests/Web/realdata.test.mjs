// Real-data checks for the browser code. The .NET fact WebScriptTests.RealTerrainTile_ReachesTheBrowserDecoderIntact
// samples a real server terrain tile through TerrainTile.GetHeight into an ATG1 chunk (GeometryWire.Write), writes it with
// the file counts and probe positions to a temp directory named by ARCANECORE_ATLAS_REAL, and runs this file. The tests
// skip, visibly, when that directory is not given; with it they hold the UI's decoder and tile maths to the server's
// numbers and to the file names the extraction under ARCANECORE_TEST_TERRAIN_DIR really holds (read-only).

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { decodeChunk } from '../../../tools/ArcaneCore.Atlas/Web/js/atg1.js';
import * as tiles from '../../../tools/ArcaneCore.Atlas/Web/js/tiles.js';

const realDir = process.env.ARCANECORE_ATLAS_REAL;
const terrainDir = process.env.ARCANECORE_TEST_TERRAIN_DIR;
const skip = realDir && fs.existsSync(path.join(realDir, 'real.json')) && terrainDir && fs.existsSync(path.join(terrainDir, 'maps'))
    ? false
    : 'ARCANECORE_ATLAS_REAL and ARCANECORE_TEST_TERRAIN_DIR are not both set (run the ArcaneCore.Atlas.Tests real-data fact)';

const load = () => JSON.parse(fs.readFileSync(path.join(realDir, 'real.json'), 'utf8'));

test('a real terrain tile decodes with the server heights', { skip }, () => {
    const real = load();
    const chunk = decodeChunk(new Uint8Array(fs.readFileSync(path.join(realDir, 'chunk.bin'))));
    assert.equal(chunk.layer, 'terrain');
    assert.deepEqual([chunk.tile.map, chunk.tile.x, chunk.tile.y], real.tile);
    assert.deepEqual(chunk.origin, real.origin);
    assert.equal(chunk.vertexCount, real.vertices.length);
    assert.equal(chunk.vertexCount, real.serverZ.length);
    assert.equal(chunk.triangleCount, real.triangleCount);
    assert.equal(chunk.pointCount, real.pointCount);
    assert.ok(chunk.vertexCount > 2048, `${chunk.vertexCount} vertices`);

    // World position = double origin + float32 offset, exactly as GeometryChunk.Vertex computes it.
    let lowest = Infinity;
    let highest = -Infinity;
    for (let i = 0; i < chunk.vertexCount; i++) {
        for (let k = 0; k < 3; k++) {
            const world = chunk.origin[k] + chunk.positions[3 * i + k];
            assert.ok(Math.abs(world - real.vertices[i][k]) <= 1e-9, `vertex ${i} axis ${k}: ${world} vs ${real.vertices[i][k]}`);
        }
        const z = chunk.origin[2] + chunk.positions[3 * i + 2];
        assert.equal(z, real.serverZ[i], `vertex ${i}: height ${z} is not the server's ${real.serverZ[i]}`);
        lowest = Math.min(lowest, z);
        highest = Math.max(highest, z);
    }
    assert.ok(highest - lowest > 5, `the tile is flat (${lowest}..${highest})`);

    // Every vertex lies inside its tile, so the tile maths agrees with where the chunk says it is.
    for (let i = 0; i < chunk.vertexCount; i++) {
        const t = tiles.tileOf(real.vertices[i][0], real.vertices[i][1]);
        assert.deepEqual(t, { x: real.tile[1], y: real.tile[2] }, `vertex ${i} falls in tile ${JSON.stringify(t)}`);
    }
    for (let t = 0; t < chunk.triangleCount * 3; t++) {
        assert.ok(chunk.triangles[t] < chunk.vertexCount);
    }
});

test('cursor readout names the files the extraction holds', { skip }, () => {
    const real = load();
    assert.ok(real.probes.length >= 4);
    for (const probe of real.probes) {
        const readout = tiles.describePosition(probe.map, probe.at[0], probe.at[1]);
        assert.deepEqual([readout.tile.x, readout.tile.y], probe.tile, `${probe.name}: tile`);
        assert.deepEqual(readout.files, probe.files, `${probe.name}: file names`);
        const exists = {
            map: fs.existsSync(path.join(terrainDir, 'maps', readout.files.map)),
            vmap: fs.existsSync(path.join(terrainDir, 'vmaps', readout.files.vmap)),
            mmap: fs.existsSync(path.join(terrainDir, 'mmaps', readout.files.mmap)),
        };
        assert.deepEqual(exists, probe.exists, `${probe.name}: which files exist`);
        assert.equal(exists.map, true, `${probe.name}: ${readout.files.map} is not in the extraction`);
    }
    // The Goldshire inn sits on terrain, under a WMO, on the navmesh: all three files are there.
    assert.deepEqual(real.probes[0].exists, { map: true, vmap: true, mmap: true });
});

test('every extracted file name round-trips through the tile maths', { skip }, () => {
    const real = load();
    // Transport (game object) navmeshes are named by display id, not by tile; the UI does not load them (out of scope).
    //   ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:791  "mmaps/go%04u.mmtile", displayId
    //   ref: vmangos@0e3ff01 src/game/Maps/MoveMap.cpp:383        "mmaps/go%04i.mmtile", displayId
    const transport = /^go\d{4}\.mmtile$/;
    const kinds = [
        { dir: 'maps', suffix: '.map', pattern: /^(\d{3})(\d{2})(\d{2})\.map$/, order: 'xy', name: tiles.mapFileName, count: real.counts.map },
        { dir: 'mmaps', suffix: '.mmtile', pattern: /^(\d{3})(\d{2})(\d{2})\.mmtile$/, other: transport, order: 'xy', name: tiles.navTileFileName, count: real.counts.mmtile },
        { dir: 'vmaps', suffix: '.vmtile', pattern: /^(\d{3})_(\d{2})_(\d{2})\.vmtile$/, order: 'yx', name: tiles.vmapTileFileName, count: real.counts.vmtile },
    ];
    let transports = 0;
    for (const kind of kinds) {
        const folder = path.join(terrainDir, kind.dir);
        const names = fs.existsSync(folder) ? fs.readdirSync(folder) : [];
        let matched = 0;
        for (const file of names) {
            const m = kind.pattern.exec(file);
            if (!m) {
                if (kind.other?.test(file)) {
                    transports++;
                    matched++;
                } else {
                    assert.ok(!file.endsWith(kind.suffix), `${kind.dir}/${file} follows neither the tile naming nor a known exception`);
                }
                continue;
            }
            const map = Number(m[1]);
            const tx = Number(kind.order === 'xy' ? m[2] : m[3]);
            const ty = Number(kind.order === 'xy' ? m[3] : m[2]);
            assert.equal(kind.name(map, tx, ty), file);
            const [cx, cy] = tiles.tileCentre(tx, ty);
            assert.deepEqual(tiles.tileOf(cx, cy), { x: tx, y: ty }, `${file}: centre`);
            matched++;
        }
        // Every file of the kind is accounted for (the .NET side counted them with its own glob).
        assert.equal(matched, kind.count, `${kind.dir}: ${matched} names matched of ${kind.count} files`);
    }
    assert.ok(real.counts.map >= 1000);
    assert.equal(transports, real.counts.transport, 'transport navmesh files');
});
