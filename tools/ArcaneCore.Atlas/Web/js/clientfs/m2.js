// M2 models (MD20, version 256..260) of the 1.12.1 client, read in a Worker: a port of Reference/Client/Models/M2ModelFile.cs. The
// render vertices and normals, the first embedded view's triangles and the bounding (collision) triangles and vertices the vmap
// extractor keeps. Positions are in model space exactly as stored (flat Float32Array triples).
//
// Not here: bone tracks and animation sequences. Their field layout for 1.12.1 could not be checked against a spec or real data from
// this repository (docs/areas/atlas-client-files.md, "Deferred deliverables").
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
