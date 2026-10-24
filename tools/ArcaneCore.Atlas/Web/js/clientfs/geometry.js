// The geometry builder and the "ATG1" writer of the browser-side terrain port: the same chunk shape and the same bytes as
// tools/ArcaneCore.Atlas/Core/GeometryChunk.cs (GeometryBuilder, GeometryWire.Write, version 1), so the decoder in ../atg1.js
// reads the output unchanged and the C# tests can compare it byte for byte.
//
// The metadata trailer is JSON written the way System.Text.Json's default writer writes a JsonObject: members in insertion
// order, strings escaped with the default encoder (ASCII only, \uXXXX uppercase, HTML-sensitive characters escaped), integers
// plain and single-precision floats as the shortest string that reads back as the same float.

import { badFile } from './limits.js';

// ref: src/ArcaneCore.Game/Maps/Grid/GridDefines.cs:25,52 (533.33333f widened to double; 128 squares per tile edge)
export const TILE_SIZE = Math.fround(533.33333);
export const MAP_RESOLUTION = 128;
export const SQUARE_SIZE = TILE_SIZE / MAP_RESOLUTION;

const MAGIC = 0x31475441;
const HEADER_BYTES = 48;
const MAX_CHUNK_BYTES = 64 * 1024 * 1024;

/** The tile's north-east corner (Core/WorldGrid.cs Origin): rows advance toward -X and columns toward -Y. */
export const tileOrigin = (x, y) => [(32 - x) * TILE_SIZE, (32 - y) * TILE_SIZE, 0];

// ---- JSON ----------------------------------------------------------------------------------------------------------
/** A value written as a single-precision float. */
export class F32 {
    constructor(value) {
        this.value = Math.fround(value);
    }
}

/** Shortest decimal string that parses back to the same float, in the notation .NET's default float formatting uses. */
export function formatFloat32(x) {
    if (!Number.isFinite(x)) {
        throw badFile(`The value ${x} cannot be written as JSON.`);
    }
    if (x === 0) {
        return Object.is(x, -0) ? '-0' : '0';
    }
    let digits = '';
    let exponent = 0;
    for (let precision = 1; precision <= 9; precision++) {
        const text = Math.abs(x).toExponential(precision - 1);
        if (Math.fround(Number(text)) === Math.abs(x) || precision === 9) {
            const [mantissa, exp] = text.split('e');
            digits = mantissa.replace('.', '');
            exponent = Number(exp);
            break;
        }
    }
    digits = digits.replace(/0+$/, '') || '0';
    const position = exponent + 1;
    let body;
    if (position > 7 || position < -3) {
        body = `${digits[0]}${digits.length > 1 ? `.${digits.slice(1)}` : ''}E${exponent < 0 ? '-' : '+'}${String(Math.abs(exponent)).padStart(2, '0')}`;
    } else if (position <= 0) {
        body = `0.${'0'.repeat(-position)}${digits}`;
    } else if (position < digits.length) {
        body = `${digits.slice(0, position)}.${digits.slice(position)}`;
    } else {
        body = digits + '0'.repeat(position - digits.length);
    }
    return x < 0 ? `-${body}` : body;
}

const ESCAPED = new Set(['"', '&', "'", '+', '<', '>', '`', '\\']);
const SHORT = new Map([[0x08, '\\b'], [0x09, '\\t'], [0x0A, '\\n'], [0x0C, '\\f'], [0x0D, '\\r'], [0x5C, '\\\\']]);

function quote(text) {
    let out = '"';
    for (let i = 0; i < text.length; i++) {
        const code = text.charCodeAt(i);
        const short = SHORT.get(code);
        if (short !== undefined) {
            out += short;
        } else if (code < 0x20 || code > 0x7E || ESCAPED.has(text[i])) {
            out += `\\u${code.toString(16).toUpperCase().padStart(4, '0')}`;
        } else {
            out += text[i];
        }
    }
    return `${out}"`;
}

/** Objects keep insertion order, Maps are written as objects with their keys stringified, arrays as arrays. */
export function encodeJson(value) {
    if (value === null || value === undefined) {
        return 'null';
    }
    if (value instanceof F32) {
        return formatFloat32(value.value);
    }
    switch (typeof value) {
        case 'boolean':
            return value ? 'true' : 'false';
        case 'string':
            return quote(value);
        case 'number':
            if (!Number.isSafeInteger(value)) {
                throw badFile(`The number ${value} is not an integer; wrap floats in F32.`);
            }
            return String(value);
        default:
            break;
    }
    if (Array.isArray(value)) {
        return `[${value.map(encodeJson).join(',')}]`;
    }
    const entries = value instanceof Map ? [...value.entries()] : Object.entries(value);
    return `{${entries.map(([k, v]) => `${quote(String(k))}:${encodeJson(v)}`).join(',')}}`;
}

// ---- Builder -------------------------------------------------------------------------------------------------------
export class GeometryBuilder {
    constructor(layer, tile, origin) {
        this.layer = layer;
        this.tile = tile;
        this.origin = origin;
        this.positions = [];
        this.triangles = [];
        this.triangleAttributes = [];
        this.lines = [];
        this.lineAttributes = [];
        this.edges = new Set();
        this.attributes = new Map();
        this.stats = {};
    }

    get vertexCount() {
        return this.positions.length / 3;
    }

    get isEmpty() {
        return this.triangles.length === 0 && this.lines.length === 0;
    }

    addVertex(x, y, z) {
        if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z)) {
            throw badFile(`${this.layer}: vertex (${x}, ${y}, ${z}) is not finite.`);
        }
        const index = this.vertexCount;
        this.positions.push(Math.fround(x - this.origin[0]), Math.fround(y - this.origin[1]), Math.fround(z - this.origin[2]));
        return index;
    }

    addTriangle(a, b, c, attribute) {
        this.triangles.push(a, b, c);
        this.triangleAttributes.push(attribute);
        this.#edge(a, b, attribute);
        this.#edge(b, c, attribute);
        this.#edge(c, a, attribute);
    }

    #edge(a, b, attribute) {
        if (a === b) {
            return;
        }
        const key = a < b ? (a * 4294967296) + b : (b * 4294967296) + a;
        if (!this.edges.has(key)) {
            this.edges.add(key);
            this.lines.push(a, b);
            this.lineAttributes.push(attribute);
        }
    }

    hasAttribute(attribute) {
        return this.attributes.has(attribute);
    }

    describeAttribute(attribute, describe) {
        if (!this.attributes.has(attribute)) {
            this.attributes.set(attribute, describe());
        }
    }

    build() {
        return {
            layer: this.layer,
            tile: this.tile,
            part: 0,
            origin: this.origin,
            positions: Float32Array.from(this.positions),
            triangles: Uint32Array.from(this.triangles),
            triangleAttributes: Uint32Array.from(this.triangleAttributes),
            lines: Uint32Array.from(this.lines),
            lineAttributes: Uint32Array.from(this.lineAttributes),
            points: new Uint32Array(0),
            pointAttributes: new Uint32Array(0),
            attributes: this.attributes,
            stats: this.stats,
        };
    }
}

// ---- Wire ----------------------------------------------------------------------------------------------------------
/** GeometryWire.Write: header, arrays on 4-byte boundaries, then u32 length + UTF-8 JSON {layer, tile, part, attributes, stats}. */
export function writeAtg1(chunk) {
    const attributes = new Map([...chunk.attributes.entries()].sort((a, b) => a[0] - b[0]).map(([id, value]) => [String(id), value]));
    const json = new TextEncoder().encode(encodeJson({ layer: chunk.layer, tile: chunk.tile, part: chunk.part, attributes, stats: chunk.stats }));
    const arrays = [chunk.positions, chunk.triangles, chunk.triangleAttributes, chunk.lines, chunk.lineAttributes, chunk.points, chunk.pointAttributes];
    const size = HEADER_BYTES + arrays.reduce((n, a) => n + (a.length * 4), 0) + 4 + json.length;
    if (size > MAX_CHUNK_BYTES) {
        throw badFile(`${chunk.layer}: chunk is ${size} bytes, over ${MAX_CHUNK_BYTES}.`);
    }
    const buffer = new ArrayBuffer(size);
    const view = new DataView(buffer);
    const triangleCount = chunk.triangles.length / 3;
    const lineCount = chunk.lines.length / 2;
    const pointCount = chunk.points.length;
    view.setUint32(0, MAGIC, true);
    view.setUint16(4, 1, true);
    view.setUint16(6, (triangleCount > 0 ? 1 : 0) | (lineCount > 0 ? 2 : 0) | (pointCount > 0 ? 4 : 0), true);
    view.setFloat64(8, chunk.origin[0], true);
    view.setFloat64(16, chunk.origin[1], true);
    view.setFloat64(24, chunk.origin[2], true);
    view.setUint32(32, chunk.positions.length / 3, true);
    view.setUint32(36, triangleCount, true);
    view.setUint32(40, lineCount, true);
    view.setUint32(44, pointCount, true);
    let offset = HEADER_BYTES;
    for (const array of arrays) {
        new Uint8Array(buffer, offset, array.length * 4).set(new Uint8Array(array.buffer, array.byteOffset, array.byteLength));
        offset += array.length * 4;
    }
    view.setUint32(offset, json.length, true);
    new Uint8Array(buffer, offset + 4, json.length).set(json);
    return new Uint8Array(buffer);
}
