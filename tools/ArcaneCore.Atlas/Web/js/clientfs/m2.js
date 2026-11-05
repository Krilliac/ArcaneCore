// M2 models (MD20, version 256..260) of the 1.12.1 client, read in a Worker: a port of Reference/Client/Models/M2ModelFile.cs. The
// render vertices and normals, the first embedded view's triangles and the bounding (collision) triangles and vertices the vmap
// extractor keeps. Positions are in model space exactly as stored (flat Float32Array triples).
//
// Animation (parseM2Animation, a separate parse so a damaged animation block never costs a model its mesh): global sequences, the
// sequence table and the bone tree with its translation / rotation / scale tracks, laid out as the pre-Wrath M2 spec has them
// (wowdev.wiki/M2 and pywowlib file_formats/m2_format.py, read while writing this; neither is vendored here). NOT checked against a
// real 1.12.1 file: the tests use synthetic files built to that layout. Renderer bone streams (skinning, playback) are not here.
//
// Every offset and count is checked against the file; a damaged file is refused with ClientFsError 'bad-file'. No shared state.

import { badFile } from './limits.js';

export const M2_MIN_VERSION = 256;
export const M2_MAX_VERSION = 260;

// Header field offsets: vmangos contrib/vmap_extractor/vmapextract/modelheaders.h ModelHeader order, confirmed on a real file by
// M2ModelFile.cs. Some synthetic headers place vertices at 0x3C and views at 0x44; both are accepted exactly as the C# does.
const OFFSET_NAME = 0x08;
const OFFSET_ALT_VERTICES = 0x3C;
const OFFSET_VERTICES = 0x44;
const OFFSET_ALT_VIEWS = 0x44;
const OFFSET_VIEWS = 0x4C;
const OFFSET_BOUNDING_TRIANGLES = 0xEC;
const OFFSET_BOUNDING_VERTICES = 0xF4;
const MINIMUM_HEADER_BYTES = 0x104;
const VERTEX_SIZE = 48;
const VERTEX_NORMAL_OFFSET = 20;
const VIEW_SIZE = 44;

const latin1 = new TextDecoder('latin1');

const readArray = (view, at) => [view.getUint32(at, true), view.getUint32(at + 4, true)];

/** Whether `count` elements of `size` bytes starting at `offset` lie inside a `length`-byte file (an empty array only needs its offset inside). */
function inBounds(length, offset, count, size) {
    if (count === 0) {
        return offset <= length;
    }
    return offset + (count * size) <= length;
}

function requireRange(length, offset, count, size, what) {
    if (count !== 0 && offset + (count * size) > length) {
        throw badFile(`M2: ${what} (${count} x ${size} bytes at ${offset}) end past the ${length}-byte file.`);
    }
}

function readName(bytes, view) {
    const [count, offset] = readArray(view, OFFSET_NAME);
    if (count === 0 || offset >= bytes.length || count > bytes.length - offset) {
        return '';
    }
    const raw = bytes.subarray(offset, offset + count);
    const nul = raw.indexOf(0);
    return latin1.decode(nul < 0 ? raw : raw.subarray(0, nul));
}

const box = (positions) => {
    const min = [0, 0, 0];
    const max = [0, 0, 0];
    const n = positions.length / 3;
    if (n > 0) {
        for (let axis = 0; axis < 3; axis++) {
            min[axis] = positions[axis];
            max[axis] = positions[axis];
        }
        for (let i = 1; i < n; i++) {
            for (let axis = 0; axis < 3; axis++) {
                min[axis] = Math.min(min[axis], positions[(3 * i) + axis]);
                max[axis] = Math.max(max[axis], positions[(3 * i) + axis]);
            }
        }
    }
    return { min, max };
};

const finite3 = (view, at) => Number.isFinite(view.getFloat32(at, true)) && Number.isFinite(view.getFloat32(at + 4, true)) && Number.isFinite(view.getFloat32(at + 8, true));

/**
 * Parses a model. Throws ClientFsError 'bad-file' for an empty, truncated, wrong-version or inconsistent file.
 * @returns {{name: string, vertices: Float32Array, normals: Float32Array, viewCount: number, viewLookup: Uint16Array,
 *   renderIndices: Int32Array, boundingIndices: Uint16Array, boundingVertices: Float32Array, hasVisualMesh: boolean,
 *   hasCollisionHull: boolean, renderBox: object, boundingBox: object}}
 *   `renderIndices` are view 0's triangle corners as indices into `vertices` (three per triangle).
 */
export function parseM2(bytes) {
    if (bytes.length === 0) {
        throw badFile('M2 file is empty (0 bytes).');
    }
    if (bytes.length < MINIMUM_HEADER_BYTES || bytes[0] !== 0x4D || bytes[1] !== 0x44 || bytes[2] !== 0x32 || bytes[3] !== 0x30) {
        throw badFile('M2: not an MD20 model (missing magic or header truncated).');
    }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const length = bytes.length;
    const version = view.getUint32(4, true);
    if (version < M2_MIN_VERSION || version > M2_MAX_VERSION) {
        throw badFile(`M2: version ${version} is not the 1.12.1 version ${M2_MIN_VERSION}.`);
    }
    const name = readName(bytes, view);

    let [vertexCount, vertexOffset] = readArray(view, OFFSET_VERTICES);
    let [viewCount, viewOffset] = readArray(view, OFFSET_VIEWS);
    const [altVertCount, altVertOffset] = readArray(view, OFFSET_ALT_VERTICES);
    const [altViewCount, altViewOffset] = readArray(view, OFFSET_ALT_VIEWS);
    const primaryValid = (vertexCount === 0 && vertexOffset === 0)
        || (inBounds(length, vertexOffset, vertexCount, VERTEX_SIZE) && (viewCount === 0 || inBounds(length, viewOffset, 1, VIEW_SIZE)));
    if ((!primaryValid || (vertexCount === 0 && vertexOffset === 0) || (viewCount === 0 && viewOffset === 0 && altVertCount > 0))
        && (altVertCount > 0 || altViewCount > 0)
        && inBounds(length, altVertOffset, altVertCount, VERTEX_SIZE)) {
        vertexCount = altVertCount;
        vertexOffset = altVertOffset;
        viewCount = altViewCount;
        viewOffset = altViewOffset;
    }

    const [boundingIndexCount, boundingIndexOffset] = readArray(view, OFFSET_BOUNDING_TRIANGLES);
    const [boundingVertexCount, boundingVertexOffset] = readArray(view, OFFSET_BOUNDING_VERTICES);
    requireRange(length, boundingIndexOffset, boundingIndexCount, 2, 'bounding triangles');
    requireRange(length, boundingVertexOffset, boundingVertexCount, 12, 'bounding vertices');
    if (boundingIndexCount === 0 && boundingVertexCount === 0) {
        requireRange(length, vertexOffset, vertexCount, VERTEX_SIZE, 'vertices');
    }

    let vertices = new Float32Array(0);
    let normals = new Float32Array(0);
    if (vertexCount > 0 && inBounds(length, vertexOffset, vertexCount, VERTEX_SIZE)) {
        vertices = new Float32Array(vertexCount * 3);
        normals = new Float32Array(vertexCount * 3);
        for (let i = 0; i < vertexCount; i++) {
            const at = vertexOffset + (i * VERTEX_SIZE);
            if (!finite3(view, at)) {
                throw badFile('M2: a vertex is not finite.');
            }
            const normalFinite = finite3(view, at + VERTEX_NORMAL_OFFSET);
            for (let axis = 0; axis < 3; axis++) {
                vertices[(3 * i) + axis] = view.getFloat32(at + (4 * axis), true);
                normals[(3 * i) + axis] = normalFinite ? view.getFloat32(at + VERTEX_NORMAL_OFFSET + (4 * axis), true) : 0;
            }
        }
    }

    let viewLookup = new Uint16Array(0);
    let renderIndices = new Int32Array(0);
    if (viewCount > 0 && vertices.length > 0 && inBounds(length, viewOffset, 1, VIEW_SIZE)) {
        const [lookupCount, lookupOffset] = readArray(view, viewOffset);
        const [triangleCount, triangleOffset] = readArray(view, viewOffset + 8);
        if (lookupCount > 0 && triangleCount > 0 && inBounds(length, lookupOffset, lookupCount, 2) && inBounds(length, triangleOffset, triangleCount, 2)) {
            if (triangleCount % 3 !== 0) {
                throw badFile(`M2: view 0 lists ${triangleCount} triangle indices, not a multiple of 3.`);
            }
            const lookup = new Uint16Array(lookupCount);
            for (let i = 0; i < lookupCount; i++) {
                lookup[i] = view.getUint16(lookupOffset + (2 * i), true);
                if (lookup[i] >= vertices.length / 3) {
                    throw badFile(`M2: view vertex lookup entry ${i} (${lookup[i]}) points past the ${vertices.length / 3} vertices.`);
                }
            }
            const indices = new Int32Array(triangleCount);
            for (let i = 0; i < triangleCount; i++) {
                const local = view.getUint16(triangleOffset + (2 * i), true);
                if (local >= lookup.length) {
                    throw badFile(`M2: view triangle index ${i} (${local}) points past the ${lookup.length} lookup entries.`);
                }
                indices[i] = lookup[local];
            }
            viewLookup = lookup;
            renderIndices = indices;
        }
    }

    const boundingIndices = new Uint16Array(boundingIndexCount);
    for (let i = 0; i < boundingIndexCount; i++) {
        boundingIndices[i] = view.getUint16(boundingIndexOffset + (2 * i), true);
    }
    const boundingVertices = new Float32Array(boundingVertexCount * 3);
    for (let i = 0; i < boundingVertices.length; i++) {
        boundingVertices[i] = view.getFloat32(boundingVertexOffset + (4 * i), true);
    }
    if (boundingVertexCount > 0) {
        if (boundingIndices.length % 3 !== 0) {
            throw badFile(`M2: ${boundingIndices.length} bounding triangle indices is not a multiple of 3.`);
        }
        for (const index of boundingIndices) {
            if (index >= boundingVertexCount) {
                throw badFile(`M2: a bounding triangle index (${index}) points past the ${boundingVertexCount} bounding vertices.`);
            }
        }
    }
    for (const v of boundingVertices) {
        if (!Number.isFinite(v)) {
            throw badFile('M2: a vertex is not finite.');
        }
    }

    return {
        name,
        vertices,
        normals,
        viewCount,
        viewLookup,
        renderIndices,
        boundingIndices,
        boundingVertices,
        hasVisualMesh: vertices.length > 0 && renderIndices.length > 0,
        hasCollisionHull: boundingIndices.length > 0 && boundingVertices.length > 0,
        renderBox: box(vertices),
        boundingBox: box(boundingVertices),
    };
}

// ---- animation ---------------------------------------------------------------------------------------------------------------
// Header: global sequences (u32 durations) 0x14, sequences 0x1C, bones 0x34 (each an {count, offset} pair).
const OFFSET_GLOBAL_SEQUENCES = 0x14;
const OFFSET_SEQUENCES = 0x1C;
const OFFSET_BONES = 0x34;
const SEQUENCE_SIZE = 68;
const TRACK_SIZE = 28; // u16 interpolation, i16 global sequence, ranges {n, ofs}, timestamps {n, ofs}, values {n, ofs}
const BONE_PREFIX_WITH_CRC = 16; // i32 key bone, u32 flags, i16 parent, u16 submesh, u32 name crc
const BONE_PREFIX_WITHOUT_CRC = 12;
const BONE_TAIL = (3 * TRACK_SIZE) + 12; // translation, rotation, scale tracks, then the pivot

function readTrack(view, at, length, components, what) {
    const interpolation = view.getUint16(at, true);
    const globalSequence = view.getInt16(at + 2, true);
    const [rangeCount, rangeOffset] = readArray(view, at + 4);
    const [stampCount, stampOffset] = readArray(view, at + 12);
    const [valueCount, valueOffset] = readArray(view, at + 20);
    requireRange(length, rangeOffset, rangeCount, 8, `${what} interpolation ranges`);
    requireRange(length, stampOffset, stampCount, 4, `${what} timestamps`);
    requireRange(length, valueOffset, valueCount, 4 * components, `${what} values`);
    if (interpolation > 3) {
        throw badFile(`M2: ${what} has interpolation type ${interpolation} (0 none, 1 linear, 2 hermite, 3 bezier).`);
    }
    const ranges = new Uint32Array(rangeCount * 2);
    for (let i = 0; i < ranges.length; i++) {
        ranges[i] = view.getUint32(rangeOffset + (4 * i), true);
    }
    const timestamps = new Uint32Array(stampCount);
    for (let i = 0; i < stampCount; i++) {
        timestamps[i] = view.getUint32(stampOffset + (4 * i), true);
    }
    const values = new Float32Array(valueCount * components);
    for (let i = 0; i < values.length; i++) {
        const value = view.getFloat32(valueOffset + (4 * i), true);
        if (!Number.isFinite(value)) {
            throw badFile(`M2: ${what} has a value that is not finite.`);
        }
        values[i] = value;
    }
    return { interpolation, globalSequence, ranges, timestamps, values, components };
}

function readBones(view, length, count, offset, prefix) {
    const size = prefix + BONE_TAIL;
    requireRange(length, offset, count, size, 'bones');
    const bones = [];
    for (let i = 0; i < count; i++) {
        const at = offset + (i * size);
        const parent = view.getInt16(at + 8, true);
        if (parent < -1 || parent >= count) {
            throw badFile(`M2: bone ${i} names parent ${parent}, outside the ${count} bones.`);
        }
        const tracks = at + prefix;
        const pivot = tracks + (3 * TRACK_SIZE);
        bones.push({
            keyBoneId: view.getInt32(at, true),
            flags: view.getUint32(at + 4, true),
            parent,
            submeshId: view.getUint16(at + 10, true),
            nameCrc: prefix === BONE_PREFIX_WITH_CRC ? view.getUint32(at + 12, true) : null,
            translation: readTrack(view, tracks, length, 3, `bone ${i} translation`),
            rotation: readTrack(view, tracks + TRACK_SIZE, length, 4, `bone ${i} rotation`), // [x, y, z, w] floats
            scale: readTrack(view, tracks + (2 * TRACK_SIZE), length, 3, `bone ${i} scale`),
            pivot: [view.getFloat32(pivot, true), view.getFloat32(pivot + 4, true), view.getFloat32(pivot + 8, true)],
        });
    }
    return bones;
}

/**
 * Parses the animation tables of a model: global sequences, sequences, bones and their tracks.
 *
 * A track keeps the file's flat layout (pre-Wrath: one timestamps array and one values array for the whole model, plus `ranges`,
 * the {min, max} pairs the spec lists as interpolation ranges, flattened). `interpolation` is 0 none, 1 linear, 2 hermite, 3 bezier;
 * for 2 and 3 the spec stores in/out tangents beside each value. How `ranges` map to sequences, the rotation component order and
 * the tangent layout are as the spec describes and are UNVERIFIED against a real 1.12.1 model.
 *
 * Whether a bone record carries the 4-byte name CRC (the wiki marks it as present from TBC on, pywowlib reads it for every version)
 * is decided from the file: the bone array is read with the CRC (112-byte records) and, when that does not fit the file or names an
 * impossible parent, track or interpolation, again without it (108 bytes). `boneRecordSize` reports which one held.
 *
 * @returns {{name: string, globalSequences: Uint32Array, sequences: object[], bones: object[], boneRecordSize: number}} plain data
 *   (typed arrays and objects only), so it can cross the Worker boundary. Throws ClientFsError 'bad-file'.
 */
export function parseM2Animation(bytes) {
    if (bytes.length === 0) {
        throw badFile('M2 file is empty (0 bytes).');
    }
    if (bytes.length < MINIMUM_HEADER_BYTES || bytes[0] !== 0x4D || bytes[1] !== 0x44 || bytes[2] !== 0x32 || bytes[3] !== 0x30) {
        throw badFile('M2: not an MD20 model (missing magic or header truncated).');
    }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const length = bytes.length;
    const version = view.getUint32(4, true);
    if (version < M2_MIN_VERSION || version > M2_MAX_VERSION) {
        throw badFile(`M2: version ${version} is not the 1.12.1 version ${M2_MIN_VERSION}.`);
    }

    const [globalCount, globalOffset] = readArray(view, OFFSET_GLOBAL_SEQUENCES);
    requireRange(length, globalOffset, globalCount, 4, 'global sequences');
    const globalSequences = new Uint32Array(globalCount);
    for (let i = 0; i < globalCount; i++) {
        globalSequences[i] = view.getUint32(globalOffset + (4 * i), true);
    }

    const [sequenceCount, sequenceOffset] = readArray(view, OFFSET_SEQUENCES);
    requireRange(length, sequenceOffset, sequenceCount, SEQUENCE_SIZE, 'sequences');
    const sequences = [];
    for (let i = 0; i < sequenceCount; i++) {
        const at = sequenceOffset + (i * SEQUENCE_SIZE);
        const start = view.getUint32(at + 4, true);
        const end = view.getUint32(at + 8, true);
        if (end < start) {
            throw badFile(`M2: sequence ${i} ends (${end}) before it starts (${start}).`);
        }
        sequences.push({
            id: view.getUint16(at, true),
            variationIndex: view.getUint16(at + 2, true),
            start,
            end,
            duration: end - start,
            moveSpeed: view.getFloat32(at + 12, true),
            flags: view.getUint32(at + 16, true),
            frequency: view.getInt16(at + 20, true),
            replayMin: view.getUint32(at + 24, true),
            replayMax: view.getUint32(at + 28, true),
            blendTime: view.getUint32(at + 32, true),
            boundsMin: [view.getFloat32(at + 36, true), view.getFloat32(at + 40, true), view.getFloat32(at + 44, true)],
            boundsMax: [view.getFloat32(at + 48, true), view.getFloat32(at + 52, true), view.getFloat32(at + 56, true)],
            boundsRadius: view.getFloat32(at + 60, true),
            variationNext: view.getInt16(at + 64, true),
            aliasNext: view.getUint16(at + 66, true),
        });
    }

    const [boneCount, boneOffset] = readArray(view, OFFSET_BONES);
    let bones = [];
    let prefix = BONE_PREFIX_WITH_CRC;
    if (boneCount > 0) {
        try {
            bones = readBones(view, length, boneCount, boneOffset, BONE_PREFIX_WITH_CRC);
        } catch (withCrc) {
            prefix = BONE_PREFIX_WITHOUT_CRC;
            try {
                bones = readBones(view, length, boneCount, boneOffset, BONE_PREFIX_WITHOUT_CRC);
            } catch {
                throw withCrc;
            }
        }
    }
    return { name: readName(bytes, view), globalSequences, sequences, bones, boneRecordSize: prefix + BONE_TAIL };
}
