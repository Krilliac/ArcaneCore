// Test-only ATX1 writer: the layout TextureWire.Write produces (Textures/TextureWire.cs, lane A1, design section 2.2), written from
// the C# byte-offset tests (TextureWireTests). The golden test compares it with the C# writer: every byte before the JSON must be
// equal and the JSON must parse to the same object. The browser tests use it to serve textures from the mock host.
//
//   u32 magic 0x31585441 "ATX1" | u16 version 1 | u16 format (1 RGBA8, 2 DXT1, 3 DXT3, 4 DXT5, 5 R8) | u16 width | u16 height
//   u16 layers | u8 levels | u8 flags (bit0 hasAlpha, bit1 alpha1bit, bit2 generated, bit3 srgbHint) | u32 jsonBytes   (20 bytes)
//   per level: u32 byteLength (unpadded) + payload padded to 4 bytes; then the JSON object.

export const MAGIC = 0x31585441;
export const HEADER_BYTES = 20;
export const FORMAT = { RGBA8: 1, DXT1: 2, DXT3: 3, DXT5: 4, R8: 5 };
export const FLAG = { HAS_ALPHA: 1, ALPHA_1BIT: 2, GENERATED: 4, SRGB_HINT: 8 };

/** @param {object} t {format, width, height, layers?, flags?, levels: Uint8Array[], meta?} */
export function encodeTexture(t) {
    const json = new TextEncoder().encode(JSON.stringify(t.meta ?? {}));
    let size = HEADER_BYTES + json.length;
    for (const level of t.levels) {
        size += 4 + Math.ceil(level.length / 4) * 4;
    }
    const out = new Uint8Array(size);
    const view = new DataView(out.buffer);
    view.setUint32(0, MAGIC, true);
    view.setUint16(4, 1, true);
    view.setUint16(6, t.format, true);
    view.setUint16(8, t.width, true);
    view.setUint16(10, t.height, true);
    view.setUint16(12, t.layers ?? 1, true);
    out[14] = t.levels.length;
    out[15] = t.flags ?? 0;
    view.setUint32(16, json.length, true);
    let offset = HEADER_BYTES;
    for (const level of t.levels) {
        view.setUint32(offset, level.length, true);
        out.set(level, offset + 4);
        offset += 4 + Math.ceil(level.length / 4) * 4;
    }
    out.set(json, offset);
    return out;
}

/** A 2x2 RGBA test texture, row-major from the top-left: red, green, blue, white (the same four texels the C# CPU tests use). */
export function probeTexture(key = 'client:textures\\probe.blp') {
    return encodeTexture({
        format: FORMAT.RGBA8,
        width: 2,
        height: 2,
        flags: 0,
        levels: [Uint8Array.from([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255]), Uint8Array.from([128, 128, 128, 255])],
        meta: { key },
    });
}
