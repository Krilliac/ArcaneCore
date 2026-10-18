// Decoder for the "ATG1" geometry chunk and the tile response envelope.
// Authority: tools/ArcaneCore.Atlas/Core/GeometryChunk.cs (GeometryWire) and the host route GET /api/tile (design doc section 13).
//
//   u32 magic 0x31475441 ("ATG1") | u16 version 1 | u16 flags (bit0 triangles, bit1 lines, bit2 points)
//   f64 originX | f64 originY | f64 originZ
//   u32 vertexCount | u32 triangleCount | u32 lineCount | u32 pointCount            (header = 48 bytes)
//   f32[3*vertexCount] | u32[3*triangleCount] | u32[triangleCount] | u32[2*lineCount] | u32[lineCount]
//   u32[pointCount] | u32[pointCount] | u32 jsonBytes | UTF-8 JSON {layer, tile:[map,x,y], part, attributes, stats}
//
// All little-endian. Typed-array views need 4-byte alignment, so a part that starts on an odd offset of the response
// is copied once; otherwise the arrays are views onto the response buffer.

export const MAGIC = 0x31475441;
export const VERSION = 1;
export const HEADER_BYTES = 48;

const LITTLE_ENDIAN_HOST = new Uint8Array(new Uint32Array([1]).buffer)[0] === 1;

export class Atg1Error extends Error {
    constructor(message) {
        super(message);
        this.name = 'Atg1Error';
    }
}

/**
 * Decodes one chunk. `source` is an ArrayBuffer or Uint8Array. Throws Atg1Error for anything the C# reader would also
 * refuse: wrong magic or version, truncation, metadata length mismatch, an index past the vertices, a non-finite position.
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
    if (view.getUint32(0, true) !== MAGIC || view.getUint16(4, true) !== VERSION) {
        throw new Atg1Error('Not an ATG1 version 1 chunk.');
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

    let offset = HEADER_BYTES;
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
    const triangles = take(triangleCount * 3, Uint32Array);
    const triangleAttributes = take(triangleCount, Uint32Array);
    const lines = take(lineCount * 2, Uint32Array);
    const lineAttributes = take(lineCount, Uint32Array);
    const points = take(pointCount, Uint32Array);
    const pointAttributes = take(pointCount, Uint32Array);
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

    return {
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
