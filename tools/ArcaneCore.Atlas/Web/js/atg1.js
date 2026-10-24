// Decoder for the "ATG1" geometry chunk and the tile response envelope.
// Authority: tools/ArcaneCore.Atlas/Core/GeometryChunk.cs (GeometryWire) and the host route GET /api/tile (design doc section 13).
//
//   u32 magic 0x31475441 ("ATG1") | u16 version 1 or 2 | u16 flags (bit0 triangles, bit1 lines, bit2 points)
//   f64 originX | f64 originY | f64 originZ
//   u32 vertexCount | u32 triangleCount | u32 lineCount | u32 pointCount            (version 1 header = 48 bytes)
//   version 2 only: u32 streamMask | u32 batchCount | u32 reserved | u32 reserved   (version 2 header = 64 bytes)
//   f32[3*vertexCount] positions
//   version 2 only, per set bit of streamMask in bit order: 0 normal f32x3, 1 uv0 f32x2, 2 uv1 f32x2, 3 color0 u32, 4 color1 u32,
//     5 boneIndices u32, 6 boneWeights u32, 7 submesh (u16 plus 2 pad bytes, read as u32)
//   u32[3*triangleCount] | u32[triangleCount] | u32[2*lineCount] | u32[lineCount] | u32[pointCount] | u32[pointCount]
//   version 2 only: batches, 4 x u32 each: firstTriangle, triangleCount, material, tag
//   u32 jsonBytes | UTF-8 JSON {layer, tile:[map,x,y], part, attributes, stats, materials?, model?}
//
// ref: tools/ArcaneCore.Atlas/Core/GeometryChunk.cs:235  Magic 0x31475441, Version 1, HeaderBytes 48
// ref: tools/ArcaneCore.Atlas/Core/GeometryChunk.cs:260  flags bit0 triangles, bit1 lines, bit2 points
// ref: tools/ArcaneCore.Atlas/Core/GeometryChunk.cs:261  header fields in order, little-endian, through offset 44
// ref: tools/ArcaneCore.Atlas/Core/GeometryChunk.cs:272  arrays in order, then u32 JSON length and the UTF-8 JSON (:279)
// ref: D:/ArcaneCore-lanes/_wf/ATLAS-2026-10-18.md:285   tile envelope: u32 partCount, per part u32 length + ATG1 bytes
// All little-endian. Typed-array views need 4-byte alignment, so a part that starts on an odd offset of the response
// is copied once; otherwise the arrays are views onto the response buffer.

export const MAGIC = 0x31475441;
export const VERSION = 1;
export const VERSION_V2 = 2;
export const HEADER_BYTES = 48;
export const HEADER_BYTES_V2 = 64;

/** The per-vertex streams of version 2, in stream-mask bit order: [name, words per vertex, array type]. */
const STREAMS = [
    ['normals', 3, Float32Array],
    ['uv0', 2, Float32Array],
    ['uv1', 2, Float32Array],
    ['color0', 1, Uint32Array],
    ['color1', 1, Uint32Array],
    ['boneIndices', 1, Uint32Array],
    ['boneWeights', 1, Uint32Array],
    ['submesh', 1, Uint32Array],
];
const KNOWN_STREAMS = (1 << STREAMS.length) - 1;

const LITTLE_ENDIAN_HOST = new Uint8Array(new Uint32Array([1]).buffer)[0] === 1;

export class Atg1Error extends Error {
    constructor(message) {
        super(message);
        this.name = 'Atg1Error';
    }
}

/**
 * Decodes one chunk (version 1 or 2). `source` is an ArrayBuffer or Uint8Array. Throws Atg1Error for anything the C# reader would
 * also refuse: wrong magic or version, truncation, metadata length mismatch, an index past the vertices, a non-finite position,
 * normal or texture coordinate, an unknown vertex stream, or batches that do not cover the triangles with valid materials.
 * Version 2 chunks add the stream arrays (`normals`, `uv0`, ... each null when absent), `batches` ({firstTriangle, triangleCount,
 * material, tag}), `materials` and `model`; a version 1 chunk has null streams and empty `batches` and `materials`.
 */
export function decodeChunk(source) {
    if (!LITTLE_ENDIAN_HOST) {
        throw new Atg1Error('ATG1 chunks need a little-endian browser host.');
    }
    let bytes = source instanceof Uint8Array ? source : new Uint8Array(source);
    if (bytes.byteOffset % 4 !== 0) {
        bytes = bytes.slice();
    }
    if (bytes.length < HEADER_BYTES + 4) {
        throw new Atg1Error('ATG1 chunk is shorter than its header.');
    }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const version = view.getUint16(4, true);
    if (view.getUint32(0, true) !== MAGIC || (version !== VERSION && version !== VERSION_V2)) {
        throw new Atg1Error('Not an ATG1 version 1 or 2 chunk.');
    }
    const v2 = version === VERSION_V2;
    const headerBytes = v2 ? HEADER_BYTES_V2 : HEADER_BYTES;
    if (bytes.length < headerBytes + 4) {
        throw new Atg1Error('ATG1 chunk is shorter than its header.');
    }
    const streamMask = v2 ? view.getUint32(48, true) : 0;
    const batchCount = v2 ? view.getUint32(52, true) : 0;
    if ((streamMask & ~KNOWN_STREAMS) !== 0) {
        throw new Atg1Error(`ATG1 chunk declares unknown vertex streams 0x${(streamMask & ~KNOWN_STREAMS).toString(16)}.`);
    }
    const flags = view.getUint16(6, true);
    const origin = [view.getFloat64(8, true), view.getFloat64(16, true), view.getFloat64(24, true)];
    const vertexCount = view.getUint32(32, true);
    const triangleCount = view.getUint32(36, true);
    const lineCount = view.getUint32(40, true);
    const pointCount = view.getUint32(44, true);
    if (!origin.every(Number.isFinite)) {
        throw new Atg1Error('ATG1 origin is not finite.');
    }

    let offset = headerBytes;
    const take = (count, Type) => {
        const length = count * 4;
        if (offset + length > bytes.length) {
            throw new Atg1Error('ATG1 chunk is truncated.');
        }
        const array = new Type(bytes.buffer, bytes.byteOffset + offset, count);
        offset += length;
        return array;
    };
    const positions = take(vertexCount * 3, Float32Array);
    const streams = {};
    STREAMS.forEach(([name, words, Type], bit) => {
        streams[name] = (streamMask & (1 << bit)) !== 0 ? take(vertexCount * words, Type) : null;
    });
    const triangles = take(triangleCount * 3, Uint32Array);
    const triangleAttributes = take(triangleCount, Uint32Array);
    const lines = take(lineCount * 2, Uint32Array);
    const lineAttributes = take(lineCount, Uint32Array);
    const points = take(pointCount, Uint32Array);
    const pointAttributes = take(pointCount, Uint32Array);
    const batchWords = take(batchCount * 4, Uint32Array);
    if (offset + 4 > bytes.length) {
        throw new Atg1Error('ATG1 chunk is truncated.');
    }
    const jsonBytes = view.getUint32(offset, true);
    offset += 4;
    if (offset + jsonBytes !== bytes.length) {
        throw new Atg1Error('ATG1 metadata length does not match the chunk size.');
    }

    for (let i = 0; i < positions.length; i++) {
        const p = positions[i];
        if (p - p !== 0) {
            throw new Atg1Error('ATG1 position is not finite.');
        }
    }
    checkIndices(triangles, vertexCount, 'triangle');
    checkIndices(lines, vertexCount, 'line');
    checkIndices(points, vertexCount, 'point');
    for (const name of ['normals', 'uv0', 'uv1']) {
        const array = streams[name];
        if (array) {
            for (let i = 0; i < array.length; i++) {
                if (array[i] - array[i] !== 0) {
                    throw new Atg1Error(`ATG1 ${name} value is not finite.`);
                }
            }
        }
    }

    let meta;
    try {
        meta = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(offset, offset + jsonBytes)));
    } catch (error) {
        throw new Atg1Error(`ATG1 metadata is not valid JSON: ${error.message}`);
    }
    if (meta === null || typeof meta !== 'object' || typeof meta.layer !== 'string' || !Array.isArray(meta.tile) || meta.tile.length !== 3) {
        throw new Atg1Error('ATG1 metadata lacks layer or tile.');
    }

    const attributes = new Map();
    for (const [key, value] of Object.entries(meta.attributes ?? {})) {
        attributes.set(Number(key), value ?? {});
    }

    const materials = meta.materials ?? [];
    if (!Array.isArray(materials) || !materials.every((m) => m !== null && typeof m === 'object' && !Array.isArray(m))) {
        throw new Atg1Error('ATG1 materials must be an array of objects.');
    }
    const batches = [];
    let next = 0;
    for (let i = 0; i < batchCount; i++) {
        const batch = {
            firstTriangle: batchWords[i * 4],
            triangleCount: batchWords[i * 4 + 1],
            material: batchWords[i * 4 + 2],
            tag: batchWords[i * 4 + 3],
        };
        if (batch.triangleCount === 0 || batch.firstTriangle !== next) {
            throw new Atg1Error('ATG1 batches must be non-empty, in order and contiguous.');
        }
        next += batch.triangleCount;
        if (next > triangleCount) {
            throw new Atg1Error(`ATG1 batch runs past the ${triangleCount} triangles.`);
        }
        if (batch.material >= materials.length) {
            throw new Atg1Error(`ATG1 batch uses material ${batch.material} but there are ${materials.length}.`);
        }
        batches.push(batch);
    }
    if (batchCount > 0 && next !== triangleCount) {
        throw new Atg1Error(`ATG1 batches cover ${next} of ${triangleCount} triangles; they must cover all or none.`);
    }

    return {
        version,
        layer: meta.layer,
        tile: { map: meta.tile[0], x: meta.tile[1], y: meta.tile[2] },
        part: meta.part ?? 0,
        flags,
        origin,
        positions,
        triangles,
        triangleAttributes,
        lines,
        lineAttributes,
        points,
        pointAttributes,
        ...streams,
        batches,
        materials,
        model: meta.model ?? null,
        attributes,
        stats: meta.stats ?? {},
        vertexCount,
        triangleCount,
        lineCount,
        pointCount,
        byteLength: bytes.length,
    };
}

function checkIndices(indices, vertexCount, what) {
    for (let i = 0; i < indices.length; i++) {
        if (indices[i] >= vertexCount) {
            throw new Atg1Error(`ATG1 ${what} index ${indices[i]} points past the ${vertexCount} vertices.`);
        }
    }
}

/**
 * Splits the GET /api/tile body: u32 partCount, then per part u32 length followed by that many ATG1 bytes. Returns
 * views onto `buffer` (decodeChunk copies the ones that start on an odd offset).
 */
export function splitTileResponse(buffer) {
    const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
    if (bytes.length < 4) {
        throw new Atg1Error('Tile response is shorter than its part count.');
    }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const count = view.getUint32(0, true);
    const parts = [];
    let offset = 4;
    for (let i = 0; i < count; i++) {
        if (offset + 4 > bytes.length) {
            throw new Atg1Error('Tile response is truncated before a part length.');
        }
        const length = view.getUint32(offset, true);
        offset += 4;
        if (offset + length > bytes.length) {
            throw new Atg1Error('Tile response is truncated inside a part.');
        }
        parts.push(bytes.subarray(offset, offset + length));
        offset += length;
    }
    if (offset !== bytes.length) {
        throw new Atg1Error('Tile response has bytes after its last part.');
    }
    return parts;
}

/** Axis-aligned bounds of a chunk in world space (double precision), or null when it has no vertices. */
export function chunkBounds(chunk) {
    const p = chunk.positions;
    if (p.length === 0) {
        return null;
    }
    const min = [Infinity, Infinity, Infinity];
    const max = [-Infinity, -Infinity, -Infinity];
    for (let i = 0; i < p.length; i += 3) {
        for (let a = 0; a < 3; a++) {
            const v = p[i + a];
            if (v < min[a]) {
                min[a] = v;
            }
            if (v > max[a]) {
                max[a] = v;
            }
        }
    }
    return {
        min: [min[0] + chunk.origin[0], min[1] + chunk.origin[1], min[2] + chunk.origin[2]],
        max: [max[0] + chunk.origin[0], max[1] + chunk.origin[1], max[2] + chunk.origin[2]],
    };
}
