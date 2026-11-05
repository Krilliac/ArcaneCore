// WMO root and group files of the 1.12.1 client, read in a Worker: a port of Reference/Client/Models (WowChunks, WmoRootFile,
// WmoGroupFile). Nothing here draws; a model draw path calls these readers for the group geometry, the doodad sets and the liquid grid.
//
// Vectors are flat Float32Array triples (x, y, z), indices are Uint16Array, exactly as stored (nothing is reordered or filtered).
// Every size and offset is checked against the file; a damaged file is refused with ClientFsError 'bad-file', never read out of range.
// Thread affinity: Worker or page, no shared state. The results are plain data and safe to share once built.

import { badFile } from './limits.js';

/** The WMO version of every 1.12.1 file. */
export const WMO_VERSION = 17;
/** Bytes of the MOGP header before its sub-chunks begin. */
export const MOGP_HEADER_SIZE = 68;

const CHUNK_HEADER = 8;
const MOHD_SIZE = 64;
const MOGI_SIZE = 32;
const MODS_SIZE = 32;
const MODD_SIZE = 40;
const MLIQ_HEADER = 30;

/** MOPY per-triangle flag bits (vmangos wmo.h WMO_MATERIAL_*). */
export const WmoMaterialFlags = Object.freeze({ NoCameraCollision: 0x02, Detail: 0x04, Collision: 0x08, Render: 0x20 });

/** MOGP flag bits the vmap extractor looks at. */
export const WmoGroupFlags = Object.freeze({ Unreachable: 0x80, Antiportal: 0x4000000 });

/** The extractor's non-precise rule: collidable when the collision bit is set, or when it is a render face that is not a detail face. */
export function isCollidable(flags) {
    const renderFace = (flags & WmoMaterialFlags.Render) !== 0 && (flags & WmoMaterialFlags.Detail) === 0;
    return (flags & WmoMaterialFlags.Collision) !== 0 || renderFace;
}

const latin1 = new TextDecoder('latin1');

function dataView(bytes) {
    return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
}

/**
 * Every chunk whose header starts in [start, end): {tag, offset, size, truncated}. The tag is stored byte-reversed on disk (REVM is
 * MVER). A final chunk whose size overruns `end` is clamped and flagged `truncated`; fewer than 8 trailing bytes are ignored.
 */
export function scanChunks(bytes, start = 0, end = bytes.length) {
    if (start < 0 || end > bytes.length || start > end) {
        throw badFile(`Chunk range ${start}..${end} does not fit a ${bytes.length}-byte file.`);
    }
    const view = dataView(bytes);
    const chunks = [];
    let position = start;
    while (end - position >= CHUNK_HEADER) {
        const tag = String.fromCharCode(bytes[position + 3], bytes[position + 2], bytes[position + 1], bytes[position]);
        const size = view.getUint32(position + 4, true);
        const payload = position + CHUNK_HEADER;
        const remaining = end - payload;
        const truncated = size > remaining;
        const clamped = truncated ? remaining : size;
        chunks.push({ tag, offset: payload, size: clamped, truncated });
        position = payload + clamped;
    }
    return chunks;
}

/** The payload of a chunk a reader depends on; throws when the file ended inside it. */
function required(bytes, chunk, context) {
    if (chunk.truncated) {
        throw badFile(`${context}: chunk ${chunk.tag} is cut off by the end of the file.`);
    }
    return bytes.subarray(chunk.offset, chunk.offset + chunk.size);
}

/** The NUL-terminated Latin-1 string at `offset` of a string blob, or null past its end. */
export function stringAt(blob, offset) {
    if (offset < 0 || offset >= blob.length) {
        return null;
    }
    const tail = blob.subarray(offset);
    const nul = tail.indexOf(0);
    return latin1.decode(nul < 0 ? tail : tail.subarray(0, nul));
}

const vector = (view, at) => [view.getFloat32(at, true), view.getFloat32(at + 4, true), view.getFloat32(at + 8, true)];

/** `Dir\Building.wmo` to `Dir\Building_007.wmo`: the group file of a zero-based group index. */
export function groupFilePath(rootPath, groupIndex) {
    if (typeof rootPath !== 'string' || rootPath.trim() === '') {
        throw new TypeError('rootPath must be a non-empty string.');
    }
    if (!Number.isInteger(groupIndex) || groupIndex < 0) {
        throw new RangeError('groupIndex must be a non-negative integer.');
    }
    const stem = /\.wmo$/i.test(rootPath) ? rootPath.slice(0, -4) : rootPath;
    return `${stem}_${String(groupIndex).padStart(3, '0')}.wmo`;
}

/**
 * Parses a WMO root file.
 * @returns {{version: number, header: object, groups: object[], groupNames: Uint8Array, doodadSets: object[], doodads: object[]}}
 *   `groupName(offset)` is on the result as a method. Throws ClientFsError 'bad-file' for an empty, truncated or unsupported file.
 */
export function parseWmoRoot(bytes) {
    if (bytes.length === 0) {
        throw badFile('WMO root file is empty (0 bytes).');
    }
    let version = 0;
    let haveVersion = false;
    let header = null;
    let groupNames = new Uint8Array(0);
    let groups = [];
    let doodadSets = [];
    let doodadNames = new Uint8Array(0);
    let doodadChunk = null;

    for (const chunk of scanChunks(bytes)) {
        switch (chunk.tag) {
            case 'MVER': {
                const payload = required(bytes, chunk, 'WMO root');
                if (payload.length < 4) {
                    throw badFile('WMO root: MVER chunk is shorter than 4 bytes.');
                }
                version = dataView(payload).getUint32(0, true);
                haveVersion = true;
                break;
            }
            case 'MOHD': {
                const payload = required(bytes, chunk, 'WMO root');
                if (payload.length < MOHD_SIZE) {
                    throw badFile(`WMO root: MOHD chunk is ${payload.length} bytes, expected at least ${MOHD_SIZE}.`);
                }
                const view = dataView(payload);
                header = {
                    textureCount: view.getUint32(0, true),
                    groupCount: view.getUint32(4, true),
                    portalCount: view.getUint32(8, true),
                    lightCount: view.getUint32(12, true),
                    doodadNameCount: view.getUint32(16, true),
                    doodadDefinitionCount: view.getUint32(20, true),
                    doodadSetCount: view.getUint32(24, true),
                    ambientColor: view.getUint32(28, true),
                    wmoId: view.getUint32(32, true),
                    boundsMin: vector(view, 36),
                    boundsMax: vector(view, 48),
                    flags: view.getUint32(60, true),
                };
                break;
            }
            case 'MOGN':
                groupNames = required(bytes, chunk, 'WMO root').slice();
                break;
            case 'MOGI': {
                const payload = required(bytes, chunk, 'WMO root');
                const view = dataView(payload);
                groups = [];
                for (let offset = 0; offset + MOGI_SIZE <= payload.length; offset += MOGI_SIZE) {
                    groups.push({
                        flags: view.getUint32(offset, true),
                        boundsMin: vector(view, offset + 4),
                        boundsMax: vector(view, offset + 16),
                        nameOffset: view.getInt32(offset + 28, true),
                    });
                }
                break;
            }
            case 'MODS': {
                const payload = required(bytes, chunk, 'WMO root');
                const view = dataView(payload);
                doodadSets = [];
                for (let offset = 0; offset + MODS_SIZE <= payload.length; offset += MODS_SIZE) {
                    const name = payload.subarray(offset, offset + 20);
                    const nul = name.indexOf(0);
                    doodadSets.push({
                        name: latin1.decode(nul < 0 ? name : name.subarray(0, nul)),
                        firstDoodad: view.getUint32(offset + 20, true),
                        doodadCount: view.getUint32(offset + 24, true),
                    });
                }
                break;
            }
            case 'MODN':
                doodadNames = required(bytes, chunk, 'WMO root').slice();
                break;
            case 'MODD':
                doodadChunk = chunk; // parsed after the loop so MODN may come in either order
                break;
            default:
                break;
        }
    }

    if (!haveVersion) {
        throw badFile('WMO root: no MVER chunk.');
    }
    if (version !== WMO_VERSION) {
        throw badFile(`WMO root: version ${version} is not the 1.12.1 version ${WMO_VERSION}.`);
    }
    if (header === null) {
        throw badFile('WMO root: no MOHD chunk.');
    }

    const doodads = [];
    if (doodadChunk !== null) {
        const payload = required(bytes, doodadChunk, 'WMO root');
        const view = dataView(payload);
        for (let offset = 0; offset + MODD_SIZE <= payload.length; offset += MODD_SIZE) {
            const packed = view.getUint32(offset, true);
            const nameOffset = packed & 0x00FFFFFF;
            doodads.push({
                nameOffset,
                flags: packed >>> 24,
                modelPath: stringAt(doodadNames, nameOffset) ?? '',
                position: vector(view, offset + 4),
                rotation: [view.getFloat32(offset + 16, true), view.getFloat32(offset + 20, true), view.getFloat32(offset + 24, true), view.getFloat32(offset + 28, true)],
                scale: view.getFloat32(offset + 32, true),
                color: view.getUint32(offset + 36, true),
            });
        }
    }

    return { version, header, groups, groupNames, doodadSets, doodads, groupName: (offset) => stringAt(groupNames, offset) };
}

function readLiquid(payload) {
    if (payload.length < MLIQ_HEADER) {
        throw badFile(`WMO group: MLIQ chunk is ${payload.length} bytes, shorter than its ${MLIQ_HEADER}-byte header.`);
    }
    const view = dataView(payload);
    const xverts = view.getInt32(0, true);
    const yverts = view.getInt32(4, true);
    const xtiles = view.getInt32(8, true);
    const ytiles = view.getInt32(12, true);
    if (xverts < 0 || yverts < 0 || xtiles < 0 || ytiles < 0) {
        throw badFile('WMO group: MLIQ has a negative grid size.');
    }
    const vertexCount = xverts * yverts;
    const tileCount = xtiles * ytiles;
    const remaining = payload.length - MLIQ_HEADER;
    if (vertexCount > Math.floor(remaining / 8) || tileCount > remaining - (vertexCount * 8)) {
        throw badFile(`WMO group: MLIQ grid (${xverts}x${yverts} vertices, ${xtiles}x${ytiles} tiles) needs more bytes than the chunk's ${payload.length}.`);
    }
    const heights = new Float32Array(vertexCount);
    for (let i = 0; i < vertexCount; i++) {
        heights[i] = view.getFloat32(MLIQ_HEADER + (8 * i) + 4, true);
    }
    const tileStart = MLIQ_HEADER + (vertexCount * 8);
    return {
        verticesX: xverts,
        verticesY: yverts,
        tilesX: xtiles,
        tilesY: ytiles,
        corner: vector(view, 16),
        materialId: view.getUint16(28, true),
        heights,
        tileFlags: payload.slice(tileStart, tileStart + tileCount),
    };
}

/**
 * Parses a WMO group file: the MOGP header and the geometry sub-chunks (MOPY, MOVI, MOVT, MODR, MLIQ). Triangles are in WMO-local
 * space exactly as stored. Throws ClientFsError 'bad-file' for an empty, truncated or inconsistent file.
 * @returns {object} {nameOffset, descriptionOffset, flags, boundsMin, boundsMax, portalStart, portalCount, transitionBatchCount,
 *   interiorBatchCount, exteriorBatchCount, fogIds, liquidType, groupId, triangleFlags: Uint8Array, triangleMaterials: Uint8Array,
 *   indices: Uint16Array, vertices: Float32Array, doodadReferences: Uint16Array, liquid, triangleCount, vertexBounds}
 */
export function parseWmoGroup(bytes) {
    if (bytes.length === 0) {
        throw badFile('WMO group file is empty (0 bytes).');
    }
    let mogp = null;
    for (const chunk of scanChunks(bytes)) {
        if (chunk.tag === 'MVER') {
            const payload = required(bytes, chunk, 'WMO group');
            if (payload.length < 4 || dataView(payload).getUint32(0, true) !== WMO_VERSION) {
                throw badFile(`WMO group: version is not the 1.12.1 version ${WMO_VERSION}.`);
            }
        } else if (chunk.tag === 'MOGP') {
            mogp = chunk;
            break;
        }
    }
    if (mogp === null) {
        throw badFile('WMO group: no MOGP chunk.');
    }
    if (bytes.length - mogp.offset < MOGP_HEADER_SIZE) {
        throw badFile(`WMO group: MOGP header needs ${MOGP_HEADER_SIZE} bytes, the file has ${bytes.length - mogp.offset}.`);
    }
    const header = new DataView(bytes.buffer, bytes.byteOffset + mogp.offset, MOGP_HEADER_SIZE);

    let triangleFlags = new Uint8Array(0);
    let triangleMaterials = new Uint8Array(0);
    let allIndices = new Uint16Array(0);
    let vertices = new Float32Array(0);
    let doodadReferences = new Uint16Array(0);
    let liquid = null;

    // The extractor reads the geometry sub-chunks as if they were top-level chunks until the end of the file.
    for (const chunk of scanChunks(bytes, mogp.offset + MOGP_HEADER_SIZE, bytes.length)) {
        switch (chunk.tag) {
            case 'MOPY': {
                const payload = required(bytes, chunk, 'WMO group');
                const count = payload.length >> 1;
                triangleFlags = new Uint8Array(count);
                triangleMaterials = new Uint8Array(count);
                for (let i = 0; i < count; i++) {
                    triangleFlags[i] = payload[2 * i];
                    triangleMaterials[i] = payload[(2 * i) + 1];
                }
                break;
            }
            case 'MOVI': {
                const payload = required(bytes, chunk, 'WMO group');
                const view = dataView(payload);
                allIndices = new Uint16Array(payload.length >> 1);
                for (let i = 0; i < allIndices.length; i++) {
                    allIndices[i] = view.getUint16(2 * i, true);
                }
                break;
            }
            case 'MOVT': {
                const payload = required(bytes, chunk, 'WMO group');
                const view = dataView(payload);
                vertices = new Float32Array(Math.floor(payload.length / 12) * 3);
                for (let i = 0; i < vertices.length; i++) {
                    vertices[i] = view.getFloat32(4 * i, true);
                }
                break;
            }
            case 'MODR': {
                const payload = required(bytes, chunk, 'WMO group');
                const view = dataView(payload);
                doodadReferences = new Uint16Array(payload.length >> 1);
                for (let i = 0; i < doodadReferences.length; i++) {
                    doodadReferences[i] = view.getUint16(2 * i, true);
                }
                break;
            }
            case 'MLIQ':
                liquid = readLiquid(required(bytes, chunk, 'WMO group'));
                break;
            default:
                break;
        }
    }

    const expected = triangleFlags.length * 3;
    if (allIndices.length < expected) {
        throw badFile(`WMO group: MOVI holds ${allIndices.length} indices but MOPY lists ${triangleFlags.length} triangles (${expected} needed).`);
    }
    const indices = allIndices.length === expected ? allIndices : allIndices.slice(0, expected);
    const vertexCount = vertices.length / 3;
    for (const index of indices) {
        if (index >= vertexCount) {
            throw badFile(`WMO group: a triangle index (${index}) points past the ${vertexCount} vertices.`);
        }
    }
    for (const v of vertices) {
        if (!Number.isFinite(v)) {
            throw badFile('WMO group: a vertex is not finite.');
        }
    }

    const min = [0, 0, 0];
    const max = [0, 0, 0];
    if (vertexCount > 0) {
        for (let axis = 0; axis < 3; axis++) {
            min[axis] = vertices[axis];
            max[axis] = vertices[axis];
        }
        for (let i = 1; i < vertexCount; i++) {
            for (let axis = 0; axis < 3; axis++) {
                min[axis] = Math.min(min[axis], vertices[(3 * i) + axis]);
                max[axis] = Math.max(max[axis], vertices[(3 * i) + axis]);
            }
        }
    }

    return {
        nameOffset: header.getUint32(0, true),
        descriptionOffset: header.getUint32(4, true),
        flags: header.getUint32(8, true),
        boundsMin: vector(header, 12),
        boundsMax: vector(header, 24),
        portalStart: header.getUint16(36, true),
        portalCount: header.getUint16(38, true),
        transitionBatchCount: header.getUint16(40, true),
        interiorBatchCount: header.getUint16(42, true),
        exteriorBatchCount: header.getUint16(44, true),
        fogIds: header.getUint32(48, true),
        liquidType: header.getUint32(52, true),
        groupId: header.getUint32(56, true),
        triangleFlags,
        triangleMaterials,
        indices,
        vertices,
        doodadReferences,
        liquid,
        triangleCount: triangleFlags.length,
        vertexBounds: { min, max },
    };
}
