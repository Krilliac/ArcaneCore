// Decoder for the "ATX1" texture wire (GET /api/bin/texture). Authority: Textures/TextureWire.cs.
//
//   u32 magic 0x31585441 ("ATX1") | u16 version 1 | u16 format (1 RGBA8, 2 DXT1, 3 DXT3, 4 DXT5, 5 R8)
//   u16 width | u16 height | u16 layers | u8 levels | u8 flags (bit0 hasAlpha, bit1 alpha1bit, bit2 generated, bit3 srgbHint)
//   u32 jsonBytes                                                                           (header = 20 bytes)
//   per level: u32 byteLength (unpadded) + payload padded to 4 bytes; then the JSON object (jsonBytes bytes, UTF-8)
//
// The levels returned are views onto the response buffer (no copy); a caller that keeps them must keep the buffer alive.

export const MAGIC = 0x31585441;
export const VERSION = 1;
export const HEADER_BYTES = 20;
export const FORMAT = Object.freeze({ RGBA8: 1, DXT1: 2, DXT3: 3, DXT5: 4, R8: 5 });
export const FLAG = Object.freeze({ HAS_ALPHA: 1, ALPHA_1BIT: 2, GENERATED: 4, SRGB_HINT: 8 });

const MAX_LEVELS = 16;
const MAX_LAYERS = 4096;

export class Atx1Error extends Error {
    constructor(message) {
        super(message);
        this.name = 'Atx1Error';
    }
}

/** Bytes of one level of a texture (all layers), the size the wire implies for its format. */
export function levelBytes(format, width, height, layers, level) {
    const w = Math.max(1, width >> level);
    const h = Math.max(1, height >> level);
    let perLayer;
    switch (format) {
        case FORMAT.RGBA8:
            perLayer = w * h * 4;
            break;
        case FORMAT.R8:
            perLayer = w * h;
            break;
        case FORMAT.DXT1:
            perLayer = Math.ceil(w / 4) * Math.ceil(h / 4) * 8;
            break;
        case FORMAT.DXT3:
        case FORMAT.DXT5:
            perLayer = Math.ceil(w / 4) * Math.ceil(h / 4) * 16;
            break;
        default:
            throw new Atx1Error(`Unknown ATX1 format ${format}.`);
    }
    return perLayer * layers;
}

/**
 * Decodes one texture. `source` is an ArrayBuffer or Uint8Array. Throws Atx1Error for anything TextureWire.Read refuses: wrong magic,
 * version or format, a bad size, layer, level or flag value, a level whose length is not the one its format implies, truncation, or
 * a metadata length that does not match the rest of the file.
 * @returns {{format:number,width:number,height:number,layers:number,flags:number,levels:Uint8Array[],meta:object,byteLength:number}}
 */
export function decodeTexture(source) {
    const bytes = source instanceof Uint8Array ? source : new Uint8Array(source);
    if (bytes.length < HEADER_BYTES) {
        throw new Atx1Error('ATX1 texture is shorter than its header.');
    }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    if (view.getUint32(0, true) !== MAGIC) {
        throw new Atx1Error('Not an ATX1 texture (bad magic).');
    }
    if (view.getUint16(4, true) !== VERSION) {
        throw new Atx1Error('Unsupported ATX1 version.');
    }
    const format = view.getUint16(6, true);
    if (format < FORMAT.RGBA8 || format > FORMAT.R8) {
        throw new Atx1Error('Unknown ATX1 format.');
    }
    const width = view.getUint16(8, true);
    const height = view.getUint16(10, true);
    const layers = view.getUint16(12, true);
    const levelCount = bytes[14];
    const flags = bytes[15];
    const jsonBytes = view.getUint32(16, true);
    if (width < 1 || height < 1 || layers < 1 || layers > MAX_LAYERS || levelCount < 1 || levelCount > MAX_LEVELS || (flags & ~0xF) !== 0) {
        throw new Atx1Error('ATX1 header has an invalid size, layer count, level count or flags.');
    }

    const levels = [];
    let offset = HEADER_BYTES;
    for (let i = 0; i < levelCount; i++) {
        if (offset + 4 > bytes.length) {
            throw new Atx1Error('ATX1 texture is truncated in a level header.');
        }
        const length = view.getUint32(offset, true);
        const expected = levelBytes(format, width, height, layers, i);
        if (length !== expected) {
            throw new Atx1Error(`ATX1 level ${i} is ${length} bytes, not the ${expected} its format implies.`);
        }
        const padded = (length + 3) & ~3;
        if (offset + 4 + padded > bytes.length) {
            throw new Atx1Error('ATX1 texture is truncated in a level.');
        }
        levels.push(bytes.subarray(offset + 4, offset + 4 + length));
        offset += 4 + padded;
    }
    if (jsonBytes !== bytes.length - offset) {
        throw new Atx1Error('ATX1 metadata length does not match the texture size.');
    }

    let meta;
    try {
        meta = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(offset)));
    } catch (error) {
        throw new Atx1Error(`ATX1 metadata is not valid JSON: ${error.message}`);
    }
    if (meta === null || typeof meta !== 'object' || Array.isArray(meta)) {
        throw new Atx1Error('ATX1 metadata is not a JSON object.');
    }
    return { format, width, height, layers, flags, levels, meta, byteLength: bytes.length };
}
