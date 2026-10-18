// Test-only ATG1 writer: the same bytes as GeometryWire.Write (Core/GeometryChunk.cs) for a chunk given as plain
// arrays. The golden test compares it with the C# writer byte for byte; the browser tests use it to serve tiles.

const MAGIC = 0x31475441;
const HEADER_BYTES = 48;

/**
 * @param {object} chunk {layer, tile:[map,x,y], part?, origin:[x,y,z], positions, triangles?, triangleAttributes?, lines?,
 *   lineAttributes?, points?, pointAttributes?, attributes?: {id: object}, stats?: object}
 * @returns {Uint8Array}
 */
export function encodeChunk(chunk) {
    const positions = Float32Array.from(chunk.positions);
    const triangles = Uint32Array.from(chunk.triangles ?? []);
    const triangleAttributes = Uint32Array.from(chunk.triangleAttributes ?? []);
    const lines = Uint32Array.from(chunk.lines ?? []);
    const lineAttributes = Uint32Array.from(chunk.lineAttributes ?? []);
    const points = Uint32Array.from(chunk.points ?? []);
    const pointAttributes = Uint32Array.from(chunk.pointAttributes ?? []);
    const json = new TextEncoder().encode(JSON.stringify({
        layer: chunk.layer,
        tile: chunk.tile,
        part: chunk.part ?? 0,
        attributes: chunk.attributes ?? {},
        stats: chunk.stats ?? {},
    }));
    const arrays = [positions, triangles, triangleAttributes, lines, lineAttributes, points, pointAttributes];
    const size = HEADER_BYTES + arrays.reduce((n, a) => n + a.byteLength, 0) + 4 + json.length;
    const out = new Uint8Array(size);
    const view = new DataView(out.buffer);
    const triCount = triangles.length / 3;
    const lineCount = lines.length / 2;
    view.setUint32(0, MAGIC, true);
    view.setUint16(4, 1, true);
    view.setUint16(6, (triCount > 0 ? 1 : 0) | (lineCount > 0 ? 2 : 0) | (points.length > 0 ? 4 : 0), true);
    view.setFloat64(8, chunk.origin[0], true);
    view.setFloat64(16, chunk.origin[1], true);
    view.setFloat64(24, chunk.origin[2], true);
    view.setUint32(32, positions.length / 3, true);
    view.setUint32(36, triCount, true);
    view.setUint32(40, lineCount, true);
    view.setUint32(44, points.length, true);
    let offset = HEADER_BYTES;
    for (const array of arrays) {
        out.set(new Uint8Array(array.buffer, array.byteOffset, array.byteLength), offset);
        offset += array.byteLength;
    }
    view.setUint32(offset, json.length, true);
    out.set(json, offset + 4);
    return out;
}

/** The tile envelope GET /api/tile answers with: u32 partCount, then per part u32 length + bytes. */
export function encodeEnvelope(parts) {
    const size = 4 + parts.reduce((n, p) => n + 4 + p.length, 0);
    const out = new Uint8Array(size);
    const view = new DataView(out.buffer);
    view.setUint32(0, parts.length, true);
    let offset = 4;
    for (const part of parts) {
        view.setUint32(offset, part.length, true);
        out.set(part, offset + 4);
        offset += 4 + part.length;
    }
    return out;
}

/**
 * A rolling terrain-like grid centred on (cx, cy): n x n quads over `size` yards, heights from `height(x, y)`. Every
 * triangle carries `attribute`; edges are the grid lines (shared edges once), as GeometryBuilder.AddTriangle makes them.
 */
export function gridChunk({ layer, tile, origin, cx, cy, size, n, height, attribute = 0, attributes = {}, stats = {} }) {
    const positions = [];
    for (let j = 0; j <= n; j++) {
        for (let i = 0; i <= n; i++) {
            const x = cx - size / 2 + (size * i) / n;
            const y = cy - size / 2 + (size * j) / n;
            positions.push(x - origin[0], y - origin[1], height(x, y) - origin[2]);
        }
    }
    const at = (i, j) => j * (n + 1) + i;
    const triangles = [];
    const triangleAttributes = [];
    const lines = [];
    const lineAttributes = [];
    const seen = new Set();
    const edge = (a, b) => {
        const key = a < b ? `${a}:${b}` : `${b}:${a}`;
        if (!seen.has(key)) {
            seen.add(key);
            lines.push(a, b);
            lineAttributes.push(attribute);
        }
    };
    for (let j = 0; j < n; j++) {
        for (let i = 0; i < n; i++) {
            const a = at(i, j);
            const b = at(i + 1, j);
            const c = at(i + 1, j + 1);
            const d = at(i, j + 1);
            triangles.push(a, b, c, a, c, d);
            triangleAttributes.push(attribute, attribute);
            edge(a, b); edge(b, c); edge(c, a); edge(c, d); edge(d, a);
        }
    }
    return { layer, tile, origin, positions, triangles, triangleAttributes, lines, lineAttributes, attributes, stats };
}

/** An axis-aligned box as 12 triangles with its 12 edges (a stand-in for a building). */
export function boxChunk({ layer, tile, origin, min, max, attribute = 0, attributes = {} }) {
    const corners = [];
    for (const z of [min[2], max[2]]) {
        for (const y of [min[1], max[1]]) {
            for (const x of [min[0], max[0]]) {
                corners.push(x - origin[0], y - origin[1], z - origin[2]);
            }
        }
    }
    // corner index = x + 2 * y + 4 * z (each 0 = min, 1 = max)
    const quads = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
    const triangles = [];
    const triangleAttributes = [];
    for (const [a, b, c, d] of quads) {
        triangles.push(a, b, c, a, c, d);
        triangleAttributes.push(attribute, attribute);
    }
    const lines = [0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7];
    return { layer, tile, origin, positions: corners, triangles, triangleAttributes, lines, lineAttributes: new Array(12).fill(attribute), attributes };
}
