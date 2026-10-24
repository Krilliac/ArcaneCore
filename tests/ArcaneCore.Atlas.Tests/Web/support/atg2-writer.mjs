// Test-only ATG1 version 2 writer: the layout GeometryWire.Write produces for a v2 chunk (Core/GeometryChunk.cs, lane A1, design
// section 2.3), written from the C# byte-offset tests (GeometryWireV2Tests). The golden test compares it with the C# writer: every
// byte before the JSON trailer must be equal, and the trailer must parse to the same object (key order and escaping may differ).
//
//   header 64 bytes: u32 magic "ATG1" | u16 version 2 | u16 flags (bit0 triangles, bit1 lines, bit2 points)
//     f64 originX, originY, originZ | u32 vertexCount | u32 triangleCount | u32 lineCount | u32 pointCount
//     u32 streamMask (offset 48) | u32 batchCount (52) | u32 reserved 0 (56) | u32 reserved 0 (60)
//   payload: f32[3n] positions | optional per-vertex streams in bit order | u32[3t] triangles | u32[t] triangle attributes
//     | u32[2l] lines | u32[l] line attributes | u32[p] points | u32[p] point attributes | batches (4 x u32 each) | u32 jsonBytes | JSON
//   stream bits: 0 normal f32x3, 1 uv0 f32x2, 2 uv1 f32x2, 3 color0 u32, 4 color1 u32, 5 boneIndices u32, 6 boneWeights u32, 7 submesh u32

export const MAGIC = 0x31475441;
export const HEADER_BYTES_V2 = 64;

export const STREAMS = [
    ['normals', 3, Float32Array],
    ['uv0', 2, Float32Array],
    ['uv1', 2, Float32Array],
    ['color0', 1, Uint32Array],
    ['color1', 1, Uint32Array],
    ['boneIndices', 1, Uint32Array],
    ['boneWeights', 1, Uint32Array],
    ['submesh', 1, Uint32Array],
];

/**
 * @param {object} c {layer, tile:[map,x,y], part?, origin:[x,y,z], positions, triangles?, triangleAttributes?, lines?, lineAttributes?,
 *   points?, pointAttributes?, normals?, uv0?, uv1?, color0?, color1?, boneIndices?, boneWeights?, submesh?, batches?: [[first,count,material,tag]],
 *   materials?, model?, attributes?, stats?}
 */
export function encodeChunkV2(c) {
    const positions = Float32Array.from(c.positions);
    const triangles = Uint32Array.from(c.triangles ?? []);
    const triangleAttributes = Uint32Array.from(c.triangleAttributes ?? []);
    const lines = Uint32Array.from(c.lines ?? []);
    const lineAttributes = Uint32Array.from(c.lineAttributes ?? []);
    const points = Uint32Array.from(c.points ?? []);
    const pointAttributes = Uint32Array.from(c.pointAttributes ?? []);
    const batches = Uint32Array.from((c.batches ?? []).flat());
    let mask = 0;
    const streams = [];
    STREAMS.forEach(([name, , Type], bit) => {
        if (c[name]) {
            mask |= 1 << bit;
            streams.push(Type.from(c[name]));
        }
    });
    const trailer = {
        layer: c.layer,
        tile: c.tile,
        part: c.part ?? 0,
        attributes: c.attributes ?? {},
        stats: c.stats ?? {},
        materials: c.materials ?? [],
    };
    if (c.model) {
        trailer.model = c.model;
    }
    const json = new TextEncoder().encode(JSON.stringify(trailer));
    const arrays = [positions, ...streams, triangles, triangleAttributes, lines, lineAttributes, points, pointAttributes, batches];
    const size = HEADER_BYTES_V2 + arrays.reduce((n, a) => n + a.byteLength, 0) + 4 + json.length;
    const out = new Uint8Array(size);
    const view = new DataView(out.buffer);
    const triCount = triangles.length / 3;
    const lineCount = lines.length / 2;
    view.setUint32(0, MAGIC, true);
    view.setUint16(4, 2, true);
    view.setUint16(6, (triCount > 0 ? 1 : 0) | (lineCount > 0 ? 2 : 0) | (points.length > 0 ? 4 : 0), true);
    view.setFloat64(8, c.origin[0], true);
    view.setFloat64(16, c.origin[1], true);
    view.setFloat64(24, c.origin[2], true);
    view.setUint32(32, positions.length / 3, true);
    view.setUint32(36, triCount, true);
    view.setUint32(40, lineCount, true);
    view.setUint32(44, points.length, true);
    view.setUint32(48, mask, true);
    view.setUint32(52, batches.length / 4, true);
    let offset = HEADER_BYTES_V2;
    for (const array of arrays) {
        out.set(new Uint8Array(array.buffer, array.byteOffset, array.byteLength), offset);
        offset += array.byteLength;
    }
    view.setUint32(offset, json.length, true);
    out.set(json, offset + 4);
    return out;
}

/** The offset of the u32 that holds the JSON length, found from the header's counts (so a test can split bytes from trailer). */
export function trailerOffset(bytes) {
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const n = view.getUint32(32, true);
    const t = view.getUint32(36, true);
    const l = view.getUint32(40, true);
    const p = view.getUint32(44, true);
    const mask = view.getUint32(48, true);
    const b = view.getUint32(52, true);
    let size = HEADER_BYTES_V2 + 12 * n;
    STREAMS.forEach(([, width], bit) => {
        if (mask & (1 << bit)) {
            size += 4 * width * n;
        }
    });
    return size + 12 * t + 4 * t + 8 * l + 4 * l + 4 * p + 4 * p + 16 * b;
}
