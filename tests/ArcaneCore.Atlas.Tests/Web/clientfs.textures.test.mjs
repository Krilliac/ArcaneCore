// Lane Y39: the client-file reader's texture path in node: the BLP2 decoder (clientfs/blp.js, a port of Textures/BlpDecoder.cs), the
// MTEX/MCLY/MCAL reader, the baked terrain textures and the ref.terrain.tex tile (clientfs/terrain-tex.js). Everything is synthetic:
// the BLP files, the ADT and the Map.dbc built here are made byte by byte from the documented layouts; nothing is a client file.
// Not run in the lane that wrote it (no CI in that lane); the integration gate is the first run.

import assert from 'node:assert/strict';
import test from 'node:test';
import { decodeChunk } from '../../../tools/ArcaneCore.Atlas/Web/js/atg1.js';
import { decodeTexture, FORMAT } from '../../../tools/ArcaneCore.Atlas/Web/js/atx1.js';
import { boxDown, decodeBlp, parseBlpHeader } from '../../../tools/ArcaneCore.Atlas/Web/js/clientfs/blp.js';
import { ClientFsError } from '../../../tools/ArcaneCore.Atlas/Web/js/clientfs/limits.js';
import {
    ALPHA_SIZE,
    BAKE_SIZE,
    FALLBACK_COLOUR,
    LAYER_TERRAIN_TEX,
    bakeChunk,
    bakeTexture,
    buildTexturedTile,
    encodeAtx1,
    parseTextureKey,
    pickLevel,
    readTextureLayers,
    textureKey,
} from '../../../tools/ArcaneCore.Atlas/Web/js/clientfs/terrain-tex.js';

// ------------------------------------------------------------------------------------------------------------------
// BLP2 builders

function writeU32(bytes, offset, value) {
    new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).setUint32(offset, value, true);
}

/**
 * A BLP2 file. `levels` are the level payloads in chain order; `palette` is 256 entries [b, g, r, a] (palettized files only).
 * `sizes` overrides the stored size of a level (to build damaged files).
 */
function blp({ compression, alphaDepth = 0, alphaType = 0, hasMips = 0, width, height, levels, palette = null, type = 1, sizes = null, offsetShift = 0 }) {
    const dataStart = 148 + (compression === 1 ? 1024 : 0);
    const total = dataStart + levels.reduce((n, l) => n + l.length, 0);
    const file = new Uint8Array(total);
    file.set([0x42, 0x4C, 0x50, 0x32]);
    writeU32(file, 4, type);
    file[8] = compression;
    file[9] = alphaDepth;
    file[10] = alphaType;
    file[11] = hasMips;
    writeU32(file, 12, width);
    writeU32(file, 16, height);
    let at = dataStart;
    levels.forEach((level, i) => {
        writeU32(file, 20 + (4 * i), at + offsetShift);
        writeU32(file, 84 + (4 * i), sizes?.[i] ?? level.length);
        file.set(level, at);
        at += level.length;
    });
    if (palette !== null) {
        palette.forEach(([b, g, r, a], i) => file.set([b, g, r, a], 148 + (4 * i)));
    }
    return file;
}

const grayPalette = () => Array.from({ length: 256 }, (_, i) => [i, i + 1 > 255 ? 255 : i + 1, i + 2 > 255 ? 255 : i + 2, 7]);

function refuses(run, what) {
    let error;
    try {
        run();
    } catch (e) {
        error = e;
    }
    assert.ok(error, `${what}: nothing was thrown`);
    assert.ok(error instanceof ClientFsError, `${what}: threw ${error?.name}: ${error?.message}`);
    assert.equal(error.code, 'bad-file', what);
    return error;
}

const pixel = (image, x, y) => [...image.rgba.subarray(((y * image.width) + x) * 4, (((y * image.width) + x) * 4) + 4)];

// ------------------------------------------------------------------------------------------------------------------
// blp.js

test('blp.js decodes raw BGRA to RGBA, honours alphaDepth and generates the mips the file does not hold', () => {
    // 2x2, B G R A per pixel.
    const data = Uint8Array.from([30, 20, 10, 255, 60, 50, 40, 255, 90, 80, 70, 255, 120, 110, 100, 255]);
    const withAlpha = decodeBlp(blp({ compression: 3, alphaDepth: 8, width: 2, height: 2, levels: [data] }));
    assert.equal(withAlpha.width, 2);
    assert.equal(withAlpha.hasAlpha, true);
    assert.equal(withAlpha.validLevels, 1);
    assert.equal(withAlpha.levels.length, 2, 'a 2x2 image has levels 2x2 and 1x1');
    assert.deepEqual(pixel(withAlpha.levels[0], 0, 0), [10, 20, 30, 255]);
    assert.deepEqual(pixel(withAlpha.levels[0], 1, 1), [100, 110, 120, 255]);
    // Box filter of four opaque pixels: sum(c * 255) + (1020 >> 1), integer divided by 1020.
    const expected = (values) => Math.floor(((values.reduce((n, v) => n + (v * 255), 0)) + 510) / 1020);
    assert.deepEqual(pixel(withAlpha.levels[1], 0, 0), [expected([10, 40, 70, 100]), expected([20, 50, 80, 110]), expected([30, 60, 90, 120]), 255]);

    // alphaDepth 0: the file's own alpha byte is ignored.
    const flat = Uint8Array.from(data);
    flat[3] = 0;
    const opaque = decodeBlp(blp({ compression: 3, alphaDepth: 0, width: 2, height: 2, levels: [flat] }));
    assert.equal(opaque.hasAlpha, false);
    assert.equal(pixel(opaque.levels[0], 0, 0)[3], 255);
});

test('blp.js uses listed mip levels, and the first invalid level ends the chain', () => {
    const level0 = Uint8Array.from([1, 2, 3, 255, 1, 2, 3, 255, 1, 2, 3, 255, 1, 2, 3, 255]);
    const listed = Uint8Array.from([9, 8, 7, 255]);
    const good = decodeBlp(blp({ compression: 3, alphaDepth: 8, hasMips: 1, width: 2, height: 2, levels: [level0, listed] }));
    assert.equal(good.validLevels, 2);
    assert.deepEqual(pixel(good.levels[1], 0, 0), [7, 8, 9, 255], 'the listed 1x1 level is used as stored');

    const wrongSize = decodeBlp(blp({ compression: 3, alphaDepth: 8, hasMips: 1, width: 2, height: 2, levels: [level0, listed], sizes: [16, 3] }));
    assert.equal(wrongSize.validLevels, 1, 'a level with the wrong size ends the chain');
    assert.deepEqual(pixel(wrongSize.levels[1], 0, 0), [3, 2, 1, 255], 'and is regenerated by the box filter');

    const unlisted = decodeBlp(blp({ compression: 3, alphaDepth: 8, hasMips: 0, width: 2, height: 2, levels: [level0, listed] }));
    assert.equal(unlisted.validLevels, 1, 'hasMips 0 means only level 0 counts');
});

test('blp.js boxDown weights by alpha and averages plainly where a block is fully transparent', () => {
    const half = boxDown({ width: 2, height: 2, rgba: Uint8Array.from([200, 0, 0, 255, 0, 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]) });
    // Only the first pixel has alpha: the colour is its own, the alpha is (255 + 2) >> 2.
    assert.deepEqual([...half.rgba], [200, 0, 0, 64]);
    const clear = boxDown({ width: 2, height: 2, rgba: Uint8Array.from([4, 8, 12, 0, 4, 8, 12, 0, 4, 8, 12, 0, 8, 12, 16, 0]) });
    assert.deepEqual([...clear.rgba], [5, 9, 13, 0], 'plain average, rounded: (4+4+4+8+2)>>2');
});

test('blp.js palettized files take alpha from alphaDepth only (0, 1, 4 and 8 bit)', () => {
    const indices = Uint8Array.from([0, 1, 2, 3, 0, 1, 2, 3]); // 4x2
    const palette = grayPalette();
    const decode = (alphaDepth, plane) => decodeBlp(blp({ compression: 1, alphaDepth, width: 4, height: 2, levels: [Uint8Array.from([...indices, ...plane])], palette })).levels[0];
    const alphas = (image) => Array.from({ length: 8 }, (_, i) => image.rgba[(i * 4) + 3]);

    const none = decode(0, []);
    assert.deepEqual(alphas(none), Array(8).fill(255), 'the palette alpha byte (7) is never used');
    // Palette entry i is stored B = i, G = i + 1, R = i + 2: so pixel with index 1 is R 3, G 2, B 1.
    assert.deepEqual(pixel(none, 1, 0), [3, 2, 1, 255]);

    assert.deepEqual(alphas(decode(1, [0b10100101])), [255, 0, 255, 0, 0, 255, 0, 255], '1 bit: bit i of the plane is pixel i');
    assert.deepEqual(alphas(decode(4, [0x10, 0x32, 0x54, 0x76])), [0, 17, 34, 51, 68, 85, 102, 119], '4 bit: the low nibble is the even pixel');
    assert.deepEqual(alphas(decode(8, [5, 6, 7, 8, 9, 10, 11, 12])), [5, 6, 7, 8, 9, 10, 11, 12]);
});

test('blp.js decodes DXT1 colours, the 3-colour transparent index only with alphaDepth 1, DXT3 nibbles and DXT5 alpha', () => {
    const dxt1 = (c0, c1, rows, alphaDepth) => decodeBlp(blp({
        compression: 2,
        alphaDepth,
        alphaType: 0,
        width: 4,
        height: 4,
        levels: [Uint8Array.from([c0 & 0xFF, c0 >> 8, c1 & 0xFF, c1 >> 8, ...rows])],
    }));

    const four = dxt1(0xF800, 0x001F, [0b11100100, 0, 0, 0], 0);
    assert.equal(four.hasAlpha, false, 'DXT1 with alphaDepth 0 has no alpha');
    assert.deepEqual(pixel(four.levels[0], 0, 0), [255, 0, 0, 255]);
    assert.deepEqual(pixel(four.levels[0], 1, 0), [0, 0, 255, 255]);
    assert.deepEqual(pixel(four.levels[0], 2, 0), [170, 0, 85, 255], '(2 c0 + c1) / 3');
    assert.deepEqual(pixel(four.levels[0], 3, 0), [85, 0, 170, 255], '(c0 + 2 c1) / 3');

    const keyed = dxt1(0x0000, 0xFFFF, [0b11100100, 0, 0, 0], 1);
    assert.equal(keyed.hasAlpha, true);
    assert.equal(keyed.alpha1Bit, true);
    assert.deepEqual(pixel(keyed.levels[0], 2, 0), [127, 127, 127, 255], 'index 2 of the 3-colour block is the average');
    assert.deepEqual(pixel(keyed.levels[0], 3, 0), [0, 0, 0, 0], 'index 3 is transparent when alphaDepth is 1');
    const unkeyed = dxt1(0x0000, 0xFFFF, [0b11100100, 0, 0, 0], 0);
    assert.deepEqual(pixel(unkeyed.levels[0], 3, 0), [0, 0, 0, 255], 'and opaque black when it is not');

    // DXT3: 8 bytes of 4-bit alpha (pixel p is nibble p), then a colour block of all c0 (red).
    const alphaBytes = Array.from({ length: 8 }, (_, j) => ((2 * j + 1) << 4) | (2 * j));
    const dxt3 = decodeBlp(blp({
        compression: 2, alphaDepth: 4, alphaType: 1, width: 4, height: 4,
        levels: [Uint8Array.from([...alphaBytes, 0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0])],
    })).levels[0];
    assert.deepEqual(pixel(dxt3, 0, 0), [255, 0, 0, 0]);
    assert.deepEqual(pixel(dxt3, 3, 3), [255, 0, 0, 255], 'pixel 15 has nibble 15');
    assert.equal(pixel(dxt3, 1, 2)[3], 9 * 17, 'pixel 9');

    // DXT5, a0 > a1: eight-value interpolation. Pixel 2 uses index 2: (6 a0 + a1) / 7 = 218; pixel 8 (second 24-bit half) uses index 1 = a1.
    const dxt5 = (a0, a1, bits) => decodeBlp(blp({
        compression: 2, alphaDepth: 8, alphaType: 7, width: 4, height: 4,
        levels: [Uint8Array.from([a0, a1, ...bits, 0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0])],
    })).levels[0];
    const interpolated = dxt5(255, 0, [0x80, 0x00, 0x00, 0x01, 0x00, 0x00]);
    assert.equal(pixel(interpolated, 2, 0)[3], 218);
    assert.equal(pixel(interpolated, 0, 2)[3], 0, 'pixel 8 has index 1 = a1 = 0');
    assert.equal(pixel(interpolated, 0, 0)[3], 255, 'pixel 0 has index 0 = a0');
    // a0 <= a1: six-value interpolation plus the fixed 0 (index 6) and 255 (index 7).
    const sixValue = dxt5(0, 255, [6 | (7 << 3), 0, 0, 0, 0, 0]);
    assert.equal(pixel(sixValue, 0, 0)[3], 0, 'index 6 is 0');
    assert.equal(pixel(sixValue, 1, 0)[3], 255, 'index 7 is 255');
    assert.equal(pixel(sixValue, 2, 0)[3], 0, 'index 0 is a0 = 0');
});

test('blp.js refuses every damaged header with bad-file and a reason', () => {
    const good = () => blp({ compression: 3, alphaDepth: 8, width: 2, height: 2, levels: [new Uint8Array(16)] });
    refuses(() => decodeBlp(new Uint8Array(10)), 'shorter than the header');
    const wrongMagic = good();
    wrongMagic[3] = 0x31;
    refuses(() => decodeBlp(wrongMagic), 'bad magic');
    refuses(() => decodeBlp(blp({ compression: 3, alphaDepth: 8, width: 2, height: 2, levels: [new Uint8Array(16)], type: 0 })), 'jpeg BLP');
    refuses(() => decodeBlp(blp({ compression: 3, alphaDepth: 8, width: 2, height: 2, levels: [new Uint8Array(16)], type: 2 })), 'unknown type');
    refuses(() => decodeBlp(blp({ compression: 3, alphaDepth: 8, width: 0, height: 2, levels: [new Uint8Array(16)] })), 'zero width');
    refuses(() => decodeBlp(blp({ compression: 3, alphaDepth: 8, width: 4097, height: 2, levels: [new Uint8Array(16)] })), 'width over 4096');
    refuses(() => decodeBlp(blp({ compression: 9, width: 2, height: 2, levels: [new Uint8Array(16)] })), 'unknown compression');
    refuses(() => decodeBlp(blp({ compression: 1, alphaDepth: 3, width: 2, height: 2, levels: [new Uint8Array(4)], palette: grayPalette() })), 'palettized alpha depth 3');
    refuses(() => decodeBlp(blp({ compression: 2, alphaType: 2, width: 4, height: 4, levels: [new Uint8Array(8)] })), 'DXT alpha type 2');
    refuses(() => decodeBlp(blp({ compression: 3, alphaDepth: 8, width: 2, height: 2, levels: [new Uint8Array(16)], sizes: [15] })), 'level 0 with the wrong size');
    refuses(() => decodeBlp(blp({ compression: 3, alphaDepth: 8, width: 2, height: 2, levels: [new Uint8Array(16)], offsetShift: 1 })), 'level 0 running past the file');
    refuses(() => decodeBlp(blp({ compression: 3, alphaDepth: 8, width: 2, height: 2, levels: [new Uint8Array(16)], offsetShift: -148 })), 'level 0 inside the header');
    // 4096 x 4096 RGBA with its chain is 89 MB: over the 64 MiB decoded limit even though the header fits.
    refuses(() => parseBlpHeader(blp({ compression: 3, alphaDepth: 8, width: 4096, height: 4096, levels: [new Uint8Array(16)] })), 'decoded size over the limit');
    assert.throws(() => decodeBlp('not bytes'), ClientFsError);
});

// ------------------------------------------------------------------------------------------------------------------
// Synthetic ADT, Map.dbc and archive chain

const writeTag = (bytes, offset, tag) => {
    for (let i = 0; i < 4; i++) {
        bytes[offset + i] = tag.charCodeAt(3 - i); // chunk ids are stored reversed
    }
};

const f32 = (bytes, offset, value) => new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).setFloat32(offset, value, true);

/** One MCNK: header, MCVT (flat), MCLY and MCAL. `layers`: [{texture, flags?, alpha?: Uint8Array(2048)}]. */
function mcnkBytes(index, { layers = [], holes = 0 } = {}) {
    const alphaTotal = layers.reduce((n, l) => n + (l.alpha ? l.alpha.length : 0), 0);
    const mclyBytes = layers.length > 0 ? 8 + (16 * layers.length) : 0;
    const mcalBytes = alphaTotal > 0 ? 8 + alphaTotal : 0;
    const payload = 128 + 8 + 580 + mclyBytes + mcalBytes;
    const bytes = new Uint8Array(8 + payload);
    writeTag(bytes, 0, 'MCNK');
    writeU32(bytes, 4, payload);
    const h = 8;
    writeU32(bytes, h + 0x04, index % 16);
    writeU32(bytes, h + 0x08, Math.floor(index / 16));
    writeU32(bytes, h + 0x0C, layers.length);
    writeU32(bytes, h + 0x14, 136);
    const mclyAt = 136 + 588;
    writeU32(bytes, h + 0x1C, layers.length > 0 ? mclyAt : 0);
    const mcalAt = mclyAt + mclyBytes;
    writeU32(bytes, h + 0x24, mcalBytes > 0 ? mcalAt : 0);
    writeU32(bytes, h + 0x28, mcalBytes);
    new DataView(bytes.buffer).setUint16(h + 0x3C, holes, true);
    f32(bytes, h + 0x68, 0);
    f32(bytes, h + 0x6C, 0);
    f32(bytes, h + 0x70, 10);
    writeTag(bytes, 136, 'MCVT');
    writeU32(bytes, 140, 580);
    if (layers.length > 0) {
        writeTag(bytes, mclyAt, 'MCLY');
        writeU32(bytes, mclyAt + 4, 16 * layers.length);
        let alphaAt = 0;
        layers.forEach((layer, i) => {
            const entry = mclyAt + 8 + (16 * i);
            writeU32(bytes, entry, layer.texture);
            writeU32(bytes, entry + 4, (layer.flags ?? 0) | (layer.alpha ? 0x100 : 0));
            writeU32(bytes, entry + 8, layer.alpha ? alphaAt : 0);
            if (layer.alpha) {
                alphaAt += layer.alpha.length;
            }
        });
    }
    if (mcalBytes > 0) {
        writeTag(bytes, mcalAt, 'MCAL');
        writeU32(bytes, mcalAt + 4, alphaTotal);
        let at = mcalAt + 8;
        for (const layer of layers) {
            if (layer.alpha) {
                bytes.set(layer.alpha, at);
                at += layer.alpha.length;
            }
        }
    }
    return bytes;
}

/** A whole 1.12.1 ADT (MVER 18, MHDR, MCIN, MTEX and 256 MCNK). `spec(index)` gives each MCNK's layers and holes. */
function adtBytes(names, spec) {
    const mtexPayload = Uint8Array.from(names.flatMap((n) => [...Buffer.from(`${n}\0`, 'latin1')]));
    const chunks = Array.from({ length: 256 }, (_, i) => mcnkBytes(i, spec(i)));
    const mcinAt = 12 + 8 + 64;
    const mtexAt = mcinAt + 8 + 4096;
    let at = mtexAt + 8 + mtexPayload.length;
    const total = at + chunks.reduce((n, c) => n + c.length, 0);
    const file = new Uint8Array(total);
    writeTag(file, 0, 'MVER');
    writeU32(file, 4, 4);
    writeU32(file, 8, 18);
    writeTag(file, 12, 'MHDR');
    writeU32(file, 16, 64);
    writeU32(file, 20 + 4, mcinAt - 20); // MHDR word 1: MCIN, relative to the MHDR payload (byte 20)
    writeU32(file, 20 + 8, mtexAt - 20);
    writeTag(file, mcinAt, 'MCIN');
    writeU32(file, mcinAt + 4, 4096);
    writeTag(file, mtexAt, 'MTEX');
    writeU32(file, mtexAt + 4, mtexPayload.length);
    file.set(mtexPayload, mtexAt + 8);
    chunks.forEach((chunk, i) => {
        writeU32(file, mcinAt + 8 + (16 * i), at);
        writeU32(file, mcinAt + 8 + (16 * i) + 4, chunk.length);
        file.set(chunk, at);
        at += chunk.length;
    });
    return file;
}

/** 64x64 alpha map of 4-bit values, `value(x, y)` 0..15, packed low nibble first. */
function alphaMap(value) {
    const out = new Uint8Array(2048);
    for (let y = 0; y < 64; y++) {
        for (let x = 0; x < 64; x += 2) {
            out[(y * 32) + (x >> 1)] = (value(x, y) & 0xF) | ((value(x + 1, y) & 0xF) << 4);
        }
    }
    return out;
}

const NAMES = ['Tileset\\Red.blp', 'Tileset\\Green.blp', 'Tileset\\Absent.blp'];
const rightHalf = () => alphaMap((x) => (x >= 32 ? 15 : 0));

function sampleSpec(index) {
    switch (index) {
        case 0: return { holes: 1, layers: [{ texture: 0 }, { texture: 1, alpha: rightHalf() }] };
        case 1: return { layers: [{ texture: 0 }] };
        case 2: return { layers: [{ texture: 0 }, { texture: 1, flags: 0x300 }] };
        case 4: return { layers: [{ texture: 2 }] };
        default: return {};
    }
}

/** A solid-colour raw BLP of 4x4 pixels. */
const solidBlp = (r, g, b) => blp({ compression: 3, alphaDepth: 0, width: 4, height: 4, levels: [Uint8Array.from(Array.from({ length: 16 }, () => [b, g, r, 255]).flat())] });

function wdbc(fieldCount, records, strings) {
    const recordSize = fieldCount * 4;
    const out = new Uint8Array(20 + (records.length * recordSize) + strings.length);
    writeU32(out, 0, 0x43424457);
    writeU32(out, 4, records.length);
    writeU32(out, 8, fieldCount);
    writeU32(out, 12, recordSize);
    writeU32(out, 16, strings.length);
    records.forEach((cells, r) => cells.forEach((v, c) => writeU32(out, 20 + (r * recordSize) + (c * 4), v)));
    out.set(strings, 20 + (records.length * recordSize));
    return out;
}

/** An archive chain over a path -> bytes map (case and separator insensitive) that counts the reads of every path. */
function fakeChain(files) {
    const table = new Map([...files].map(([p, bytes]) => [p.toLowerCase().replaceAll('/', '\\'), bytes]));
    const reads = new Map();
    return {
        reads,
        async read(path) {
            const key = path.toLowerCase().replaceAll('/', '\\');
            reads.set(key, (reads.get(key) ?? 0) + 1);
            return table.get(key) ?? null;
        },
    };
}

function sampleChain({ adt = adtBytes(NAMES, sampleSpec) } = {}) {
    return fakeChain(new Map([
        ['DBFilesClient\\Map.dbc', wdbc(42, [[0, 1]], Uint8Array.from(Buffer.from('\0Test\0')))],
        ['DBFilesClient\\AreaTable.dbc', wdbc(25, [], new Uint8Array(0))],
        ['World\\Maps\\Test\\Test_31_33.adt', adt],
        ['Tileset\\Red.blp', solidBlp(255, 0, 0)],
        ['Tileset\\Green.blp', solidBlp(0, 255, 0)],
    ]));
}

const ADT_PATH = 'world\\maps\\test\\test_31_33.adt';

// ------------------------------------------------------------------------------------------------------------------
// terrain-tex.js

test('terrain-tex.js reads MTEX, MCLY and MCAL: 4-bit alpha, low nibble first, compressed maps skipped and counted', () => {
    const info = readTextureLayers(adtBytes(NAMES, sampleSpec));
    assert.deepEqual(info.names, NAMES);
    assert.equal(info.chunks.length, 256);

    const first = info.chunks[0];
    assert.equal(first.layers.length, 2);
    assert.equal(first.layers[0].alpha, null, 'the base layer has no alpha map');
    assert.equal(first.layers[1].texture, 1);
    const alpha = first.layers[1].alpha;
    assert.equal(alpha.length, ALPHA_SIZE * ALPHA_SIZE);
    assert.equal(alpha[(10 * 64) + 31], 0);
    assert.equal(alpha[(10 * 64) + 32], 255, 'nibble 15 is 15 * 17');
    assert.equal(alpha[(63 * 64) + 63], 255);

    const pattern = readTextureLayers(adtBytes(NAMES, (i) => (i === 0 ? { layers: [{ texture: 0 }, { texture: 1, alpha: alphaMap((x, y) => (x + y) & 0xF) }] } : {})));
    const map = pattern.chunks[0].layers[1].alpha;
    assert.equal(map[0], 0);
    assert.equal(map[1], 17, 'the odd pixel is the high nibble');
    assert.equal(map[(2 * 64) + 3], 5 * 17);

    assert.equal(info.chunks[2].layers.length, 2);
    assert.equal(info.chunks[2].layers[1].alpha, null, 'a compressed alpha map is not decoded');
    assert.equal(info.chunks[2].skippedLayers, 1);
    assert.equal(info.chunks[3].layers.length, 0, 'a chunk without MCLY has no layers');
    assert.equal(info.chunks[1].skippedLayers, 0);
});

test('terrain-tex.js caps a chunk at four layers and refuses a layer that names a texture MTEX lacks or reads outside its MCAL', () => {
    const five = readTextureLayers(adtBytes(NAMES, (i) => (i === 0 ? { layers: [{ texture: 0 }, { texture: 1 }, { texture: 1 }, { texture: 0 }, { texture: 1 }] } : {})));
    assert.equal(five.chunks[0].layers.length, 4);
    assert.equal(five.chunks[0].skippedLayers, 1);

    refuses(() => readTextureLayers(adtBytes(NAMES, (i) => (i === 5 ? { layers: [{ texture: 7 }] } : {}))), 'a texture id past MTEX');

    // A layer whose alpha offset runs past its MCAL: shorten the MCAL by claiming a second map that is not stored.
    const bytes = adtBytes(NAMES, (i) => (i === 0 ? { layers: [{ texture: 0 }, { texture: 1, alpha: rightHalf() }] } : {}));
    const info = readTextureLayers(bytes);
    assert.equal(info.chunks[0].layers[1].alpha.length, 4096);
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const mcnk0 = view.getUint32(12 + 8 + 64 + 8, true);
    const mcly = mcnk0 + view.getUint32(mcnk0 + 8 + 0x1C, true);
    view.setUint32(mcly + 8 + 16 + 8, 4000, true); // layer 1 alpha offset: 4000 + 2048 > 2048
    refuses(() => readTextureLayers(bytes), 'an alpha offset past the MCAL');
});

test('terrain-tex.js texture keys round-trip and refuse anything else', () => {
    assert.equal(textureKey(0, 33, 31, 17), 'cft:0/33/31/17');
    assert.deepEqual(parseTextureKey('cft:530/33/31/255'), { map: 530, x: 33, y: 31, chunk: 255 });
    for (const bad of ['cft:0/64/0/0', 'cft:0/1/64/0', 'cft:0/1/1/256', 'cft:0/1/1', 'cft:0/1/1/0/extra', 'client:Tileset\\Red.blp', 'cft:-1/1/1/1', '', 'cft:0/1/1/x']) {
        assert.equal(parseTextureKey(bad), null, `'${bad}' is not a client terrain key`);
    }
});

test('terrain-tex.js picks the mip level that is at most a 2:1 minification of one repeat', () => {
    const chain = [256, 128, 64, 32, 16, 8, 4, 2, 1].map((n) => ({ width: n, height: n, rgba: new Uint8Array(0) }));
    assert.equal(pickLevel(chain).width, 32, 'one repeat covers 16 pixels of the 128 pixel bake: 32 texels is 2:1');
    assert.equal(pickLevel(chain.slice(4)).width, 16, 'a small texture keeps its sharpest level');
    assert.equal(pickLevel(chain.slice(0, 2)).width, 128, 'a chain that never gets small enough uses its last level');
});

test('terrain-tex.js blends layers by their alpha maps, draws the fallback colour for a missing texture and for no layers', () => {
    const solid = (r, g, b) => [{ width: 2, height: 2, rgba: Uint8Array.from(Array.from({ length: 4 }, () => [r, g, b, 255]).flat()) }];
    const at = (rgba, x, y) => [...rgba.subarray(((y * BAKE_SIZE) + x) * 4, (((y * BAKE_SIZE) + x) * 4) + 4)];

    const half = new Uint8Array(ALPHA_SIZE * ALPHA_SIZE);
    for (let y = 0; y < ALPHA_SIZE; y++) {
        half.fill(255, (y * ALPHA_SIZE) + 32, (y + 1) * ALPHA_SIZE);
    }
    const split = bakeChunk([{ levels: solid(255, 0, 0), alpha: null }, { levels: solid(0, 255, 0), alpha: half }]);
    assert.equal(split.length, BAKE_SIZE * BAKE_SIZE * 4);
    assert.deepEqual(at(split, 8, 60), [255, 0, 0, 255], 'left of the map the base layer shows');
    assert.deepEqual(at(split, 120, 60), [0, 255, 0, 255], 'right of the map the second layer covers it');

    const gray = new Uint8Array(ALPHA_SIZE * ALPHA_SIZE).fill(128);
    const mixed = bakeChunk([{ levels: solid(200, 0, 0), alpha: null }, { levels: solid(0, 100, 0), alpha: gray }]);
    const [r, g] = at(mixed, 40, 40);
    assert.ok(Math.abs(r - (200 * (1 - (128 / 255)))) <= 1 && Math.abs(g - (100 * (128 / 255))) <= 1, `alpha 128 mixes the layers (${r}, ${g})`);

    const unusable = bakeChunk([{ levels: solid(255, 0, 0), alpha: null }, { levels: solid(0, 255, 0), alpha: null }]);
    assert.deepEqual(at(unusable, 50, 50), [255, 0, 0, 255], 'a layer without a usable alpha map is not drawn');

    const fallback = [...FALLBACK_COLOUR, 255];
    assert.deepEqual(at(bakeChunk([{ levels: null, alpha: null }]), 3, 3), fallback);
    assert.deepEqual(at(bakeChunk([]), 3, 3), fallback);
});

test('terrain-tex.js encodes the baked chunk as an ATX1 texture with its whole mip chain', () => {
    const rgba = new Uint8Array(BAKE_SIZE * BAKE_SIZE * 4).fill(200);
    const texture = decodeTexture(encodeAtx1(rgba, BAKE_SIZE, 'cft:0/1/2/3'));
    assert.equal(texture.format, FORMAT.RGBA8);
    assert.equal(texture.width, BAKE_SIZE);
    assert.equal(texture.height, BAKE_SIZE);
    assert.equal(texture.layers, 1);
    assert.equal(texture.levels.length, 8, '128, 64, 32, 16, 8, 4, 2, 1');
    assert.equal(texture.levels[7].length, 4);
    assert.equal(texture.meta.key, 'cft:0/1/2/3');
    assert.equal(texture.levels[7][0], 200, 'the 1x1 level is the box-filtered average');
});

test('terrain-tex.js builds the ref.terrain.tex tile: one terrain batch per MCNK, MCNK-local vertices with UVs, holes skipped', async () => {
    const chain = sampleChain();
    const tile = await buildTexturedTile(chain, 0, 33, 31);
    assert.equal(tile.state, 'Ok', tile.detail);
    assert.equal(tile.parts.length, 1);

    const chunk = decodeChunk(tile.parts[0]);
    assert.equal(chunk.version, 2);
    assert.equal(chunk.layer, LAYER_TERRAIN_TEX);
    assert.deepEqual(chunk.tile, { map: 0, x: 33, y: 31 });
    assert.equal(chunk.batches.length, 256);
    assert.equal(chunk.materials.length, 256);
    assert.equal(chunk.lineCount, 0);

    // Chunk 0 has one 2x2 hole block (4 squares): 60 squares of 4 triangles; every other chunk has 64.
    assert.equal(chunk.batches[0].triangleCount, 240);
    assert.equal(chunk.batches[1].triangleCount, 256);
    assert.equal(chunk.triangleCount, 240 + (255 * 256));
    // Vertices are created on first use and not shared between MCNKs: 145 for a full chunk; the hole leaves four corners unused.
    assert.equal(chunk.vertexCount, 137 + (255 * 145));
    chunk.batches.forEach((batch, i) => {
        assert.equal(batch.tag & 0xFFFF, i, `batch ${i} carries its MCNK index`);
        assert.equal(chunk.triangleAttributes[batch.firstTriangle], i);
        assert.equal(chunk.triangleAttributes[batch.firstTriangle + batch.triangleCount - 1], i);
    });

    const material = chunk.materials[7];
    assert.equal(material.kind, 'terrain');
    assert.equal(material.blend, 0);
    assert.equal(material.flags, 0x60, 'clamped on both axes');
    assert.deepEqual(material.textures, [{ key: 'cft:0/33/31/7', slot: 0, wrap: [1, 1] }]);

    assert.ok(chunk.uv0 instanceof Float32Array);
    assert.equal(chunk.uv0.length, chunk.vertexCount * 2);
    assert.ok(chunk.uv0.every((v) => v >= 0 && v <= 1), 'chunk-local UVs stay inside 0..1');
    assert.equal(chunk.stats.skippedLayers, 1);
    assert.equal(chunk.stats.holeSquares, 4);
    assert.equal(chunk.attributes.get(0).layers, 2);
});

test('terrain-tex.js reports a missing or damaged tile the way Api.tile does', async () => {
    const chain = sampleChain();
    const missing = await buildTexturedTile(chain, 0, 1, 1);
    assert.equal(missing.state, 'Missing');
    assert.deepEqual(missing.parts, []);
    assert.equal((await buildTexturedTile(chain, 0, -1, -1)).state, 'NotApplicable');
    assert.equal((await buildTexturedTile(chain, 0, 64, 0)).state, 'Rejected');
    assert.equal((await buildTexturedTile(chain, 5, 33, 31)).state, 'Missing', 'a map Map.dbc does not list');

    const damaged = adtBytes(NAMES, (i) => (i === 9 ? { layers: [{ texture: 99 }] } : {}));
    const rejected = await buildTexturedTile(sampleChain({ adt: damaged }), 0, 33, 31);
    assert.equal(rejected.state, 'Rejected');
    assert.match(rejected.detail, /ADT MCNK 9/);
});

test('terrain-tex.js bakes the texture of one MCNK from the tile, reads each file once and refuses a bad key or tile', async () => {
    const chain = sampleChain();

    const blended = decodeTexture(await bakeTexture(chain, 'cft:0/33/31/0'));
    assert.equal(blended.width, BAKE_SIZE);
    const px = (texture, x, y) => [...texture.levels[0].subarray(((y * BAKE_SIZE) + x) * 4, (((y * BAKE_SIZE) + x) * 4) + 4)];
    assert.deepEqual(px(blended, 8, 60), [255, 0, 0, 255], 'left half: the red base');
    assert.deepEqual(px(blended, 120, 60), [0, 255, 0, 255], 'right half: the green layer');

    const base = decodeTexture(await bakeTexture(chain, 'cft:0/33/31/1'));
    assert.deepEqual(px(base, 64, 64), [255, 0, 0, 255], 'a one-layer chunk is its base texture');
    const skipped = decodeTexture(await bakeTexture(chain, 'cft:0/33/31/2'));
    assert.deepEqual(px(skipped, 100, 100), [255, 0, 0, 255], 'a compressed-alpha layer is not drawn, the base shows');
    const none = decodeTexture(await bakeTexture(chain, 'cft:0/33/31/3'));
    assert.deepEqual(px(none, 10, 10), [...FALLBACK_COLOUR, 255], 'a chunk without layers is the fallback colour');
    const absent = decodeTexture(await bakeTexture(chain, 'cft:0/33/31/4'));
    assert.deepEqual(px(absent, 10, 10), [...FALLBACK_COLOUR, 255], 'a layer whose BLP is not in the archives is the fallback colour');

    assert.equal(chain.reads.get(ADT_PATH), 1, 'the tile was read and parsed once for five chunks');
    assert.equal(chain.reads.get('tileset\\red.blp'), 1, 'a tileset is decoded once');
    assert.equal(chain.reads.get('tileset\\absent.blp'), 1, 'a missing tileset is remembered too');

    for (const bad of ['nonsense', 'cft:0/64/0/0']) {
        await assert.rejects(() => bakeTexture(chain, bad), (error) => error instanceof ClientFsError && error.code === 'bad-file', bad);
    }
    await assert.rejects(() => bakeTexture(sampleChain(), 'cft:0/10/10/0'), (error) => error instanceof ClientFsError && error.code === 'bad-file', 'a tile that is not in the archives');
});

test('terrain-tex.js keeps an empty MTEX entry so the MCLY texture indices after it still line up', () => {
    const names = ['Tileset\\Red.blp', '', 'Tileset\\Green.blp'];
    const info = readTextureLayers(adtBytes(names, (i) => (i === 0 ? { layers: [{ texture: 2 }] } : {})));
    assert.deepEqual(info.names, names);
    assert.equal(info.names[info.chunks[0].layers[0].texture], 'Tileset\\Green.blp', 'index 2 is the third string, not the second');
});

test('terrain-tex.js picks the mip level by its larger side, so a non-square BLP is not under-sampled', () => {
    const levels = [{ width: 8, height: 64 }, { width: 4, height: 32 }, { width: 2, height: 16 }, { width: 1, height: 8 }];
    assert.equal(pickLevel(levels).height, 32, 'the 4x32 level is the first with a larger side of at most 32');
});

test('terrain-tex.js draws the fallback colour for a tileset whose archive read fails as a bad file, and lets any other failure through', async () => {
    const damaged = sampleChain();
    const read = damaged.read.bind(damaged);
    damaged.read = async (path) => {
        if (path.toLowerCase() === 'tileset\\green.blp') {
            throw new ClientFsError('bad-file', 'the archive entry is damaged');
        }
        return read(path);
    };
    const texture = decodeTexture(await bakeTexture(damaged, 'cft:0/33/31/0'));
    const at = (x, y) => [...texture.levels[0].subarray(((y * BAKE_SIZE) + x) * 4, (((y * BAKE_SIZE) + x) * 4) + 4)];
    assert.deepEqual(at(8, 60), [255, 0, 0, 255], 'the readable base still draws');
    assert.deepEqual(at(120, 60), [...FALLBACK_COLOUR, 255], 'the damaged layer is the fallback colour, the bake does not fail');

    const broken = sampleChain();
    broken.read = async (path) => {
        if (path.toLowerCase() === 'tileset\\green.blp') {
            throw new TypeError('not a clientfs error');
        }
        return read(path);
    };
    await assert.rejects(() => bakeTexture(broken, 'cft:0/33/31/0'), TypeError);
});

test('index.js: a textured terrain tile needs the live folder before any cached answer, a plain tile does not', async () => {
    const { needsLiveFolder } = await import('../../../tools/ArcaneCore.Atlas/Web/js/clientfs/index.js');
    const folder = { archives: [] };
    assert.equal(needsLiveFolder('ref.terrain.tex', null), true, 'after a reload only the cache is left, and baked textures are never cached');
    assert.equal(needsLiveFolder('ref.terrain.tex', folder), false);
    assert.equal(needsLiveFolder('ref.terrain', null), false, 'plain terrain geometry is served from the cache without files');
    assert.equal(needsLiveFolder('ref.liquid', null), false);
});
