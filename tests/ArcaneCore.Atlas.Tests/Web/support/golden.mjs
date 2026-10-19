// Access to the golden directory the .NET test (WebGolden.Export) writes: ATG1 chunks from GeometryWire.Write and the
// answers of CameraMath, WorldGrid, LayerCatalog and ViewState. A missing directory is an error, never a skip: a
// parity test that quietly finds nothing to compare is indistinguishable from one that passes.

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

export const GOLDEN_VARIABLE = 'ARCANECORE_ATLAS_GOLDEN';

let cached = null;

export function goldenDirectory() {
    const dir = process.env[GOLDEN_VARIABLE];
    if (!dir || !fs.existsSync(path.join(dir, 'golden.json'))) {
        throw new Error(`${GOLDEN_VARIABLE} must name a directory that holds golden.json, as written by the .NET test `
            + 'WebScriptTests. Run the ArcaneCore.Atlas.Tests project (it exports the data and runs this file) instead of running node --test alone.');
    }
    return dir;
}

export function loadGolden() {
    cached ??= JSON.parse(fs.readFileSync(path.join(goldenDirectory(), 'golden.json'), 'utf8'));
    return cached;
}

/** The file's bytes in a fresh, aligned Uint8Array (a Buffer from node's pool can start at any offset). */
export function readGoldenBytes(name) {
    return new Uint8Array(fs.readFileSync(path.join(goldenDirectory(), name)));
}

/** JS must match the C# core within 1e-9, scaled for values larger than one so a 40000-yard coordinate is not held to an absolute 1e-9. */
export const TOLERANCE = 1e-9;

export function close(actual, expected, what) {
    assert.ok(Number.isFinite(actual), `${what}: ${actual} is not a finite number`);
    const limit = TOLERANCE * Math.max(1, Math.abs(expected));
    assert.ok(Math.abs(actual - expected) <= limit, `${what}: ${actual} differs from the C# value ${expected} by ${Math.abs(actual - expected)} (limit ${limit})`);
}

export function closeAll(actual, expected, what) {
    assert.equal(actual.length, expected.length, `${what}: length`);
    for (let i = 0; i < expected.length; i++) {
        close(actual[i], expected[i], `${what}[${i}]`);
    }
    return expected.length;
}
