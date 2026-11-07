// The M2 and WMO readers of the client-file reader (clientfs/m2.js, wmo.js, model.js), ports of M2ModelFile, WmoRootFile and
// WmoGroupFile. Synthetic files only, built here to the layouts those C# readers parse. Not run in the lane that wrote them (no CI
// there); the integration gate is the first run.

import assert from 'node:assert/strict';
import test from 'node:test';
import { ClientFsError } from '../../../tools/ArcaneCore.Atlas/Web/js/clientfs/limits.js';
import { parseM2, parseM2Animation } from '../../../tools/ArcaneCore.Atlas/Web/js/clientfs/m2.js';
import { readModel, transferables } from '../../../tools/ArcaneCore.Atlas/Web/js/clientfs/model.js';
import { groupFilePath, groupName, isCollidable, parseWmoGroup, parseWmoRoot, scanChunks, stringAt, WmoMaterialFlags } from '../../../tools/ArcaneCore.Atlas/Web/js/clientfs/wmo.js';

const refuses = (fn, what) => assert.throws(fn, (error) => error instanceof ClientFsError && error.code === 'bad-file', what);

// ---- builders ---------------------------------------------------------------------------------------------------------
/** A growing little-endian byte buffer. */
function writer() {
    const bytes = [];
    const view = new DataView(new ArrayBuffer(8));
    const out = {
        bytes,
        get length() { return bytes.length; },
        u8(v) { bytes.push(v & 0xFF); return out; },
        u16(v) { bytes.push(v & 0xFF, (v >> 8) & 0xFF); return out; },
        u32(v) { bytes.push(v & 0xFF, (v >>> 8) & 0xFF, (v >>> 16) & 0xFF, (v >>> 24) & 0xFF); return out; },
        i32(v) { return out.u32(v >>> 0); },
        f32(v) { view.setFloat32(0, v, true); for (let i = 0; i < 4; i++) { bytes.push(view.getUint8(i)); } return out; },
        raw(list) { bytes.push(...list); return out; },
        str(text, pad = 0) { for (const c of text) { bytes.push(c.charCodeAt(0)); } for (let i = text.length; i < pad; i++) { bytes.push(0); } return out; },
        done() { return Uint8Array.from(bytes); },
    };
    return out;
}

/** An M2: header (0x104 bytes), then 48-byte vertices, one view, bounding triangles and vertices. */
function m2Bytes({ vertices, lookup, triangles, boundingIndices = [], boundingVertices = [], version = 256, name = 'Test' }) {
    const header = new Uint8Array(0x104);
    const hv = new DataView(header.buffer);
    header.set([0x4D, 0x44, 0x32, 0x30]);
    hv.setUint32(4, version, true);
    const body = writer();
    const base = 0x104;
    const put = (list) => { const at = base + body.length; list(); return at; };
    const nameOffset = put(() => body.str(`${name}\0`));
    const vertexOffset = put(() => vertices.forEach(([x, y, z, nx = 0, ny = 0, nz = 1]) => {
        body.f32(x).f32(y).f32(z).u32(0).u32(0).f32(nx).f32(ny).f32(nz).f32(0).f32(0).f32(0).f32(0);
    }));
    const lookupOffset = put(() => lookup.forEach((v) => body.u16(v)));
    const triangleOffset = put(() => triangles.forEach((v) => body.u16(v)));
    const viewOffset = put(() => body.u32(lookup.length).u32(lookupOffset).u32(triangles.length).u32(triangleOffset).raw(new Array(28).fill(0)));
    const bIndexOffset = put(() => boundingIndices.forEach((v) => body.u16(v)));
    const bVertexOffset = put(() => boundingVertices.forEach(([x, y, z]) => body.f32(x).f32(y).f32(z)));
    hv.setUint32(0x08, name.length + 1, true);
    hv.setUint32(0x0C, nameOffset, true);
    hv.setUint32(0x44, vertices.length, true);
    hv.setUint32(0x48, vertexOffset, true);
    hv.setUint32(0x4C, 1, true);
    hv.setUint32(0x50, viewOffset, true);
    hv.setUint32(0xEC, boundingIndices.length, true);
    hv.setUint32(0xF0, bIndexOffset, true);
    hv.setUint32(0xF4, boundingVertices.length, true);
    hv.setUint32(0xF8, bVertexOffset, true);
    const file = new Uint8Array(header.length + body.length);
    file.set(header);
    file.set(body.done(), header.length);
    return file;
}

/** One WMO chunk: the tag is stored reversed. */
const chunk = (tag, payload) => writer().raw([...tag].reverse().map((c) => c.charCodeAt(0))).u32(payload.length).raw([...payload]).done();
const concat = (...parts) => Uint8Array.from(parts.flatMap((p) => [...p]));

function mohd({ groups = 2 } = {}) {
    const w = writer();
    [1, groups, 0, 0, 1, 2, 1, 0xFF112233, 77].forEach((v) => w.u32(v));
    [-1, -2, -3, 4, 5, 6].forEach((v) => w.f32(v));
    return w.u32(0x8).done();
}

function rootBytes({ version = 17 } = {}) {
    const mogi = writer();
    mogi.u32(1).f32(0).f32(0).f32(0).f32(1).f32(1).f32(1).i32(0);
    mogi.u32(2).f32(0).f32(0).f32(0).f32(2).f32(2).f32(2).i32(4);
    const mods = writer().str('Set_A', 20).u32(0).u32(1).u32(0);
    const modn = writer().str('World\\Doodad\\Tree.mdx\0');
    const modd = writer().u32(0x05000000 | 0).f32(1).f32(2).f32(3).f32(0).f32(0).f32(0).f32(1).f32(1.5).u32(0xAABBCCDD);
    return concat(
        chunk('MVER', writer().u32(version).done()),
        chunk('MOHD', mohd()),
        chunk('MOGN', writer().str('Inn\0Cellar\0').done()),
        chunk('MOGI', mogi.done()),
        chunk('MODS', mods.done()),
        chunk('MODN', modn.done()),
        chunk('MODD', modd.done()),
    );
}

function groupBytes({ indices = [0, 1, 2, 2, 1, 3], flags = [0x20, 0x08], vertices = [[0, 0, 0], [1, 0, 0], [0, 1, 0], [1, 1, 5]], liquid = null } = {}) {
    const header = writer().u32(0).u32(0).u32(0x80).f32(0).f32(0).f32(0).f32(1).f32(1).f32(5).u16(3).u16(4).u16(1).u16(2).u16(3).u16(0).u32(0x01020304).u32(9).u32(4321).u32(0).u32(0).done();
    assert.equal(header.length, 68);
    const mopy = writer();
    flags.forEach((f, i) => mopy.u8(f).u8(i));
    const movt = writer();
    vertices.forEach(([x, y, z]) => movt.f32(x).f32(y).f32(z));
    const movi = writer();
    indices.forEach((i) => movi.u16(i));
    const parts = [chunk('MVER', writer().u32(17).done()), chunk('MOGP', header), chunk('MOPY', mopy.done()), chunk('MOVI', movi.done()), chunk('MOVT', movt.done()), chunk('MODR', writer().u16(7).u16(9).done())];
    if (liquid !== null) {
        parts.push(chunk('MLIQ', liquid));
    }
    return concat(...parts);
}

// ---- m2.js ------------------------------------------------------------------------------------------------------------------
test('m2.js reads vertices, normals, view 0 triangles through the lookup, and the bounding hull', () => {
    const bytes = m2Bytes({
        vertices: [[0, 0, 0], [1, 0, 0, 0, 1, 0], [0, 2, 0], [0, 0, 3]],
        lookup: [3, 2, 1, 0],
        triangles: [0, 1, 2, 2, 1, 3],
        boundingIndices: [0, 1, 2],
        boundingVertices: [[-1, -1, -1], [1, -1, -1], [0, 1, 2]],
    });
    const m = parseM2(bytes);
    assert.equal(m.name, 'Test');
    assert.equal(m.viewCount, 1);
    assert.deepEqual([...m.vertices], [0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 3]);
    assert.deepEqual([...m.normals.subarray(3, 6)], [0, 1, 0]);
    assert.deepEqual([...m.viewLookup], [3, 2, 1, 0]);
    assert.deepEqual([...m.renderIndices], [3, 2, 1, 1, 2, 0], 'local triangle indices go through the lookup to global vertices');
    assert.equal(m.hasVisualMesh, true);
    assert.equal(m.hasCollisionHull, true);
    assert.deepEqual(m.renderBox, { min: [0, 0, 0], max: [1, 2, 3] });
    assert.deepEqual(m.boundingBox, { min: [-1, -1, -1], max: [1, 1, 2] });
    assert.deepEqual([...m.boundingIndices], [0, 1, 2]);
});

test('m2.js refuses what M2ModelFile refuses', () => {
    const good = { vertices: [[0, 0, 0], [1, 0, 0], [0, 1, 0]], lookup: [0, 1, 2], triangles: [0, 1, 2] };
    refuses(() => parseM2(new Uint8Array(0)), 'empty');
    refuses(() => parseM2(new Uint8Array(0x104)), 'no magic');
    refuses(() => parseM2(m2Bytes({ ...good, version: 300 })), 'a version past 260');
    refuses(() => parseM2(m2Bytes({ ...good, version: 100 })), 'a version below 256');
    refuses(() => parseM2(m2Bytes({ ...good, triangles: [0, 1] })), 'indices not a multiple of 3');
    refuses(() => parseM2(m2Bytes({ ...good, triangles: [0, 1, 9] })), 'a triangle index past the lookup');
    refuses(() => parseM2(m2Bytes({ ...good, lookup: [0, 1, 7] })), 'a lookup entry past the vertices');
    refuses(() => parseM2(m2Bytes({ ...good, vertices: [[Number.NaN, 0, 0], [1, 0, 0], [0, 1, 0]] })), 'a non-finite vertex');
    refuses(() => parseM2(m2Bytes({ ...good, boundingIndices: [0, 1], boundingVertices: [[0, 0, 0], [1, 0, 0]] })), 'bounding indices not a multiple of 3');
    refuses(() => parseM2(m2Bytes({ ...good, boundingIndices: [0, 1, 5], boundingVertices: [[0, 0, 0], [1, 0, 0], [0, 1, 0]] })), 'a bounding index past the bounding vertices');
    const truncated = m2Bytes({ ...good, boundingIndices: [0, 1, 2], boundingVertices: [[0, 0, 0], [1, 0, 0], [0, 1, 0]] });
    refuses(() => parseM2(truncated.subarray(0, truncated.length - 4)), 'bounding vertices past the end of the file');
});

test('m2.js zeroes a non-finite normal instead of refusing the file', () => {
    const m = parseM2(m2Bytes({ vertices: [[0, 0, 0, Number.NaN, 0, 1], [1, 0, 0], [0, 1, 0]], lookup: [0, 1, 2], triangles: [0, 1, 2] }));
    assert.deepEqual([...m.normals.subarray(0, 3)], [0, 0, 0]);
    assert.deepEqual([...m.normals.subarray(3, 6)], [0, 0, 1]);
});

// ---- m2.js animation ----------------------------------------------------------------------------------------------------------
/** A pre-Wrath M2Track: u16 type, i16 global sequence, then {count, offset} for ranges, timestamps and values. */
function trackBytes(type, globalSequence, ranges, stamps, values) {
    const w = writer().u16(type).u16(globalSequence & 0xFFFF);
    for (const [n, ofs] of [ranges, stamps, values]) {
        w.u32(n).u32(ofs);
    }
    return w.done();
}

/** A model with 2 global sequences, 2 sequences and 2 bones (a root with rotation and translation keys, a child with none). */
function animationBytes({ withCrc = true, parent = 0, badInterpolation = false } = {}) {
    const header = new Uint8Array(0x104);
    const hv = new DataView(header.buffer);
    header.set([0x4D, 0x44, 0x32, 0x30]);
    hv.setUint32(4, 256, true);
    const GLOBAL = 0x104;
    const SEQUENCES = GLOBAL + 8;
    const RANGES = SEQUENCES + (2 * 68);
    const ROT_STAMPS = RANGES + 8;
    const ROT_VALUES = ROT_STAMPS + 12;
    const POS_STAMPS = ROT_VALUES + 48;
    const POS_VALUES = POS_STAMPS + 8;
    const BONES = POS_VALUES + 24;
    hv.setUint32(0x14, 2, true); hv.setUint32(0x18, GLOBAL, true);
    hv.setUint32(0x1C, 2, true); hv.setUint32(0x20, SEQUENCES, true);
    hv.setUint32(0x34, 2, true); hv.setUint32(0x38, BONES, true);

    const body = writer().u32(1000).u32(2000);
    const sequence = (id, variation, start, end, flags, next) => body
        .u16(id).u16(variation).u32(start).u32(end).f32(2.5).u32(flags).u16(0x7FFF).u16(0).u32(1).u32(3).u32(150)
        .f32(-1).f32(-2).f32(-3).f32(4).f32(5).f32(6).f32(7).u16(next & 0xFFFF).u16(0);
    sequence(4, 0, 0, 1500, 0x20, -1);
    sequence(5, 1, 1500, 2000, 0, 0);
    body.u32(0).u32(3); // one range over the three rotation keys
    [0, 500, 1000].forEach((t) => body.u32(t));
    [[0, 0, 0, 1], [0, 0.5, 0, 0.5], [0, 1, 0, 0]].forEach((q) => q.forEach((v) => body.f32(v)));
    [0, 1500].forEach((t) => body.u32(t));
    [[1, 2, 3], [4, 5, 6]].forEach((v) => v.forEach((c) => body.f32(c)));

    const prefix = (key, flags, parentBone, submesh, crc) => {
        body.i32(key).u32(flags).u16(parentBone & 0xFFFF).u16(submesh);
        if (withCrc) {
            body.u32(crc);
        }
    };
    const pivot = (x, y, z) => body.f32(x).f32(y).f32(z);
    prefix(0, 0x8, -1, 0, 0xAABBCCDD);
    body.raw(trackBytes(1, -1, [0, 0], [2, POS_STAMPS], [2, POS_VALUES]))
        .raw(trackBytes(badInterpolation ? 9 : 1, -1, [1, RANGES], [3, ROT_STAMPS], [3, ROT_VALUES]))
        .raw(trackBytes(0, -1, [0, 0], [0, 0], [0, 0]));
    pivot(0.5, 1.5, 2.5);
    prefix(-1, 0, parent, 7, 0x01020304);
    body.raw(trackBytes(0, 1, [0, 0], [0, 0], [0, 0])).raw(trackBytes(0, -1, [0, 0], [0, 0], [0, 0])).raw(trackBytes(0, -1, [0, 0], [0, 0], [0, 0]));
    pivot(0, 0, 0);
    return concat(header, body.done());
}

test('m2.js reads the animation tables: global sequences, sequences, bones and their flat tracks', () => {
    const anim = parseM2Animation(animationBytes());
    assert.deepEqual([...anim.globalSequences], [1000, 2000]);
    assert.equal(anim.sequences.length, 2);
    assert.equal(anim.sequences[0].id, 4);
    assert.equal(anim.sequences[0].start, 0);
    assert.equal(anim.sequences[0].end, 1500);
    assert.equal(anim.sequences[0].duration, 1500);
    assert.equal(anim.sequences[0].moveSpeed, 2.5);
    assert.equal(anim.sequences[0].flags, 0x20);
    assert.equal(anim.sequences[0].frequency, 0x7FFF);
    assert.equal(anim.sequences[0].replayMax, 3);
    assert.equal(anim.sequences[0].blendTime, 150);
    assert.deepEqual(anim.sequences[0].boundsMin, [-1, -2, -3]);
    assert.deepEqual(anim.sequences[0].boundsMax, [4, 5, 6]);
    assert.equal(anim.sequences[0].boundsRadius, 7);
    assert.equal(anim.sequences[0].variationNext, -1);
    assert.equal(anim.sequences[1].variationIndex, 1);
    assert.equal(anim.sequences[1].duration, 500);

    assert.equal(anim.boneRecordSize, 112);
    assert.equal(anim.bones.length, 2);
    const root = anim.bones[0];
    assert.equal(root.keyBoneId, 0);
    assert.equal(root.flags, 8);
    assert.equal(root.parent, -1);
    assert.equal(root.nameCrc, 0xAABBCCDD);
    assert.deepEqual(root.pivot, [0.5, 1.5, 2.5]);
    assert.equal(root.translation.interpolation, 1);
    assert.deepEqual([...root.translation.timestamps], [0, 1500]);
    assert.deepEqual([...root.translation.values], [1, 2, 3, 4, 5, 6]);
    assert.equal(root.translation.components, 3);
    assert.equal(root.rotation.components, 4);
    assert.deepEqual([...root.rotation.timestamps], [0, 500, 1000]);
    assert.deepEqual([...root.rotation.ranges], [0, 3]);
    assert.deepEqual([...root.rotation.values].slice(4, 8), [0, 0.5, 0, 0.5]);
    assert.equal(root.scale.timestamps.length, 0);
    const child = anim.bones[1];
    assert.equal(child.parent, 0);
    assert.equal(child.submeshId, 7);
    assert.equal(child.keyBoneId, -1);
    assert.equal(child.translation.globalSequence, 1);
});

test('m2.js reads bones with or without the 4-byte name CRC, deciding from the file', () => {
    const without = parseM2Animation(animationBytes({ withCrc: false }));
    assert.equal(without.boneRecordSize, 108);
    assert.equal(without.bones[0].nameCrc, null);
    assert.deepEqual([...without.bones[0].rotation.timestamps], [0, 500, 1000]);
    assert.equal(without.bones[1].parent, 0);
    assert.equal(without.bones[1].submeshId, 7);
});

test('m2.js refuses a damaged animation block', () => {
    refuses(() => parseM2Animation(new Uint8Array(0)), 'empty');
    refuses(() => parseM2Animation(animationBytes({ parent: 5 })), 'a parent outside the bones (with and without the CRC)');
    refuses(() => parseM2Animation(animationBytes({ badInterpolation: true })), 'an unknown interpolation type');
    const truncated = animationBytes();
    refuses(() => parseM2Animation(truncated.subarray(0, truncated.length - 40)), 'the file ends inside the bone array');
    const notM2 = animationBytes();
    notM2[0] = 0x4E;
    refuses(() => parseM2Animation(notM2), 'wrong magic');
    const nan = animationBytes();
    new DataView(nan.buffer).setUint32(0x104 + 8 + 136 + 8 + 12 + 4, 0x7FC00000, true);
    refuses(() => parseM2Animation(nan), 'a value that is not finite');
    const backwards = animationBytes();
    new DataView(backwards.buffer).setUint32(0x104 + 8 + 8, 0, true);
    new DataView(backwards.buffer).setUint32(0x104 + 8 + 4, 9, true);
    refuses(() => parseM2Animation(backwards), 'a sequence that ends before it starts');
});

// ---- wmo.js -----------------------------------------------------------------------------------------------------------------
test('wmo.js scans chunks with reversed tags and clamps a final chunk that overruns the file', () => {
    const data = concat(chunk('MVER', writer().u32(17).done()), writer().raw([0x58, 0x58, 0x58, 0x4D]).u32(100).u8(1).u8(2).done());
    const chunks = scanChunks(data);
    assert.deepEqual(chunks.map((c) => [c.tag, c.size, c.truncated]), [['MVER', 4, false], ['MXXX', 2, true]]);
    assert.equal(scanChunks(concat(chunk('MVER', writer().u32(17).done()), Uint8Array.from([1, 2, 3]))).length, 1, 'fewer than 8 trailing bytes are ignored');
    refuses(() => scanChunks(data, 0, data.length + 1), 'a range past the file');
    assert.equal(stringAt(Uint8Array.from([65, 66, 0, 67, 0]), 3), 'C');
    assert.equal(stringAt(Uint8Array.from([65, 66]), 0), 'AB', 'no NUL: the rest of the blob');
    assert.equal(stringAt(Uint8Array.from([65]), 5), null);
});

test('wmo.js reads a root file: header, groups, group names, doodad sets and doodads', () => {
    const root = parseWmoRoot(rootBytes());
    assert.equal(root.version, 17);
    assert.equal(root.header.groupCount, 2);
    assert.equal(root.header.ambientColor, 0xFF112233);
    assert.equal(root.header.wmoId, 77);
    assert.deepEqual(root.header.boundsMin, [-1, -2, -3]);
    assert.deepEqual(root.header.boundsMax, [4, 5, 6]);
    assert.equal(root.header.flags, 8);
    assert.equal(root.groups.length, 2);
    assert.equal(root.groups[1].nameOffset, 4);
    assert.deepEqual(root.groups[1].boundsMax, [2, 2, 2]);
    assert.equal(groupName(root.groupNames, root.groups[0].nameOffset), 'Inn');
    assert.equal(groupName(root.groupNames, root.groups[1].nameOffset), 'Cellar');
    assert.equal(groupName(root.groupNames, 999), null);
    assert.deepEqual(root.doodadSets, [{ name: 'Set_A', firstDoodad: 0, doodadCount: 1 }]);
    assert.equal(root.doodads.length, 1);
    assert.equal(root.doodads[0].modelPath, 'World\\Doodad\\Tree.mdx');
    assert.equal(root.doodads[0].flags, 5);
    assert.deepEqual(root.doodads[0].position, [1, 2, 3]);
    assert.deepEqual(root.doodads[0].rotation, [0, 0, 0, 1]);
    assert.equal(root.doodads[0].scale, 1.5);
    assert.equal(root.doodads[0].color, 0xAABBCCDD);
});

test('wmo.js refuses a root without MVER or MOHD, with another version, or cut off inside a chunk', () => {
    refuses(() => parseWmoRoot(new Uint8Array(0)), 'empty');
    refuses(() => parseWmoRoot(chunk('MOHD', mohd())), 'no MVER');
    refuses(() => parseWmoRoot(chunk('MVER', writer().u32(17).done())), 'no MOHD');
    refuses(() => parseWmoRoot(rootBytes({ version: 16 })), 'version 16');
    refuses(() => parseWmoRoot(chunk('MVER', writer().u32(17).done()).subarray(0, 10)), 'MVER cut off');
    refuses(() => parseWmoRoot(concat(chunk('MVER', writer().u32(17).done()), chunk('MOHD', new Uint8Array(10)))), 'a short MOHD');
});

test('wmo.js group file paths and collision flags follow the extractor', () => {
    assert.equal(groupFilePath('World\\wmo\\Inn.wmo', 7), 'World\\wmo\\Inn_007.wmo');
    assert.equal(groupFilePath('World\\wmo\\Inn.WMO', 120), 'World\\wmo\\Inn_120.wmo');
    assert.equal(groupFilePath('Inn', 0), 'Inn_000.wmo');
    assert.throws(() => groupFilePath('', 0), TypeError);
    assert.throws(() => groupFilePath('a.wmo', -1), RangeError);
    assert.equal(isCollidable(WmoMaterialFlags.Collision), true);
    assert.equal(isCollidable(WmoMaterialFlags.Render), true, 'a render face that is not a detail face');
    assert.equal(isCollidable(WmoMaterialFlags.Render | WmoMaterialFlags.Detail), false);
    assert.equal(isCollidable(0), false);
});

test('wmo.js reads a group file: header, triangles, vertices, doodad references and bounds', () => {
    const group = parseWmoGroup(groupBytes());
    assert.equal(group.flags, 0x80);
    assert.deepEqual(group.boundsMax, [1, 1, 5]);
    assert.equal(group.portalStart, 3);
    assert.equal(group.portalCount, 4);
    assert.equal(group.transitionBatchCount, 1);
    assert.equal(group.interiorBatchCount, 2);
    assert.equal(group.exteriorBatchCount, 3);
    assert.equal(group.fogIds, 0x01020304);
    assert.equal(group.liquidType, 9);
    assert.equal(group.groupId, 4321);
    assert.equal(group.triangleCount, 2);
    assert.deepEqual([...group.triangleFlags], [0x20, 0x08]);
    assert.deepEqual([...group.triangleMaterials], [0, 1]);
    assert.deepEqual([...group.indices], [0, 1, 2, 2, 1, 3]);
    assert.equal(group.vertices.length, 12);
    assert.deepEqual([...group.doodadReferences], [7, 9]);
    assert.deepEqual(group.vertexBounds, { min: [0, 0, 0], max: [1, 1, 5] });
    assert.equal(group.liquid, null);
});

test('wmo.js keeps only 3 * triangles indices and refuses short, out-of-range and non-finite groups', () => {
    const extra = parseWmoGroup(groupBytes({ indices: [0, 1, 2, 2, 1, 3, 0, 0, 0] }));
    assert.equal(extra.indices.length, 6, 'the surplus MOVI entries are dropped');
    refuses(() => parseWmoGroup(groupBytes({ indices: [0, 1, 2] })), 'MOVI shorter than MOPY needs');
    refuses(() => parseWmoGroup(groupBytes({ indices: [0, 1, 2, 2, 1, 9] })), 'an index past the vertices');
    refuses(() => parseWmoGroup(groupBytes({ vertices: [[0, 0, 0], [1, 0, 0], [0, 1, 0], [Number.POSITIVE_INFINITY, 0, 0]] })), 'a non-finite vertex');
    refuses(() => parseWmoGroup(new Uint8Array(0)), 'empty');
    refuses(() => parseWmoGroup(chunk('MVER', writer().u32(17).done())), 'no MOGP');
    refuses(() => parseWmoGroup(concat(chunk('MVER', writer().u32(16).done()), chunk('MOGP', new Uint8Array(68)))), 'a group of another version');
    refuses(() => parseWmoGroup(concat(chunk('MVER', writer().u32(17).done()), chunk('MOGP', new Uint8Array(20)))), 'a short MOGP header');
});

test('wmo.js reads the MLIQ liquid grid and refuses a grid that does not fit its chunk', () => {
    const liquid = writer().i32(2).i32(2).i32(1).i32(1).f32(10).f32(20).f32(30).u16(5);
    [1.5, 2.5, 3.5, 4.5].forEach((h) => liquid.u32(0).f32(h));
    liquid.u8(0x0F);
    const grid = parseWmoGroup(groupBytes({ liquid: liquid.done() })).liquid;
    assert.equal(grid.verticesX, 2);
    assert.equal(grid.tilesY, 1);
    assert.deepEqual(grid.corner, [10, 20, 30]);
    assert.equal(grid.materialId, 5);
    assert.deepEqual([...grid.heights], [1.5, 2.5, 3.5, 4.5]);
    assert.deepEqual([...grid.tileFlags], [0x0F]);

    const big = writer().i32(100).i32(100).i32(1).i32(1).f32(0).f32(0).f32(0).u16(0).done();
    refuses(() => parseWmoGroup(groupBytes({ liquid: big })), 'a grid larger than its chunk');
    const negative = writer().i32(-1).i32(2).i32(1).i32(1).f32(0).f32(0).f32(0).u16(0).done();
    refuses(() => parseWmoGroup(groupBytes({ liquid: negative })), 'a negative grid size');
    refuses(() => parseWmoGroup(groupBytes({ liquid: new Uint8Array(10) })), 'a chunk shorter than the header');
});

// ---- model.js ---------------------------------------------------------------------------------------------------------------
test('model.js reads a file from the chain with the parser of its kind and refuses unknown kinds and missing files', async () => {
    const m2 = m2Bytes({ vertices: [[0, 0, 0], [1, 0, 0], [0, 1, 0]], lookup: [0, 1, 2], triangles: [0, 1, 2] });
    const files = new Map([['world\\a.m2', m2], ['world\\inn.wmo', rootBytes()], ['world\\inn_000.wmo', groupBytes()]]);
    const chain = { async read(path) { return files.get(path.toLowerCase().replaceAll('/', '\\')) ?? null; } };
    assert.equal((await readModel(chain, 'm2', 'World\\a.m2')).hasVisualMesh, true);
    assert.equal((await readModel(chain, 'wmo-root', 'World/Inn.wmo')).header.groupCount, 2);
    assert.equal((await readModel(chain, 'wmo-group', groupFilePath('World\\Inn.wmo', 0))).triangleCount, 2);
    await assert.rejects(() => readModel(chain, 'mdx', 'World\\a.m2'), (e) => e instanceof ClientFsError && e.code === 'bad-file');
    await assert.rejects(() => readModel(chain, 'toString', 'World\\a.m2'), (e) => e instanceof ClientFsError, 'a prototype member is not a kind');
    await assert.rejects(() => readModel(chain, 'm2', 'World\\missing.m2'), (e) => e instanceof ClientFsError && /not in the client archives/.test(e.message));
    await assert.rejects(() => readModel(chain, 'm2', ''), (e) => e instanceof ClientFsError);
    await assert.rejects(() => readModel(chain, 'wmo-root', 'World\\a.m2'), (e) => e instanceof ClientFsError, 'a damaged file is refused by its parser');
});

test('every model kind survives the Worker boundary: the result structured-clones with its transfer list (m2, m2-animation, wmo-root, wmo-group)', async () => {
    const m2 = m2Bytes({ vertices: [[0, 0, 0], [1, 0, 0], [0, 1, 0]], lookup: [0, 1, 2], triangles: [0, 1, 2], boundingIndices: [0, 1, 2], boundingVertices: [[0, 0, 0], [1, 0, 0], [0, 1, 0]] });
    const files = new Map([['world\\a.m2', m2], ['world\\anim.m2', animationBytes()], ['world\\inn.wmo', rootBytes()], ['world\\inn_000.wmo', groupBytes()]]);
    const chain = { async read(path) { return files.get(path.toLowerCase().replaceAll('/', '\\')) ?? null; } };
    for (const [kind, path] of [['m2', 'World\\a.m2'], ['m2-animation', 'World\\anim.m2'], ['wmo-root', 'World\\Inn.wmo'], ['wmo-group', 'World\\Inn_000.wmo']]) {
        const result = await readModel(chain, kind, path);
        const transfer = transferables(result);
        assert.ok(transfer.length > 0, `${kind}: typed arrays are transferred`);
        const before = JSON.stringify(result, (k, v) => (ArrayBuffer.isView(v) ? Array.from(v) : v));
        // The Worker's postMessage(result, transfer) uses the same structured clone; a function property throws DataCloneError here.
        const copy = structuredClone(result, { transfer });
        assert.equal(JSON.stringify(copy, (k, v) => (ArrayBuffer.isView(v) ? Array.from(v) : v)), before, `${kind}: the clone equals the parsed result`);
        for (const buffer of transfer) {
            assert.equal(buffer.byteLength, 0, `${kind}: the transferred buffer is detached`);
        }
    }
});
