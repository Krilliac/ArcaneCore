// Textured terrain from the visitor's own client files (layer `ref.terrain.tex`), read in a Worker.
//
// The ADT's MTEX (tileset names), MCLY (per MCNK texture layers) and MCAL (4-bit alpha maps) are read here; the heights, holes and
// geometry come from adt.js. Per MCNK the layers are blended on the CPU into one 128x128 RGBA texture (the base layer, then every
// further layer lerped in by its alpha map, each layer tiled 8 times across the chunk), delivered to the page as an ATX1 texture under
// the key `cft:<map>/<x>/<y>/<chunk>`. The tile itself is an ATG1 version 2 chunk with one `terrain` material per MCNK that names its
// baked texture, so the renderer needs no splatting shader: the `terrain` kind of materials/terrain.js draws it like a clamped,
// lit, opaque textured batch.
//
// References (layout only, nothing copied): wowdev.wiki ADT/v18 (MTEX, MCLY entries of 16 bytes {textureId, flags, offsetInMCAL, effectId},
// MCAL 64x64 4-bit alpha, low nibble first, the layer flag 0x100 "use alpha map"); the MCNK header offsets (+0x0C layer count, +0x1C MCLY,
// +0x24 MCAL, +0x28 MCAL size) are relative to the MCNK chunk start, as the MCVT offset already used by adt.js. Compressed alpha maps
// (layer flag 0x200) do not occur in 1.12.1 data: such a layer is skipped and counted in stats.skippedLayers.
//
// Orientation (UNVERIFIED against a real client in CI): the alpha map and the layer textures share the chunk's (column, row) axes,
// u along the MCVT column index and v along its row index.
// Thread affinity: pure functions plus a per-chain cache (WeakMap) that the owning Worker touches only.

import { boxDown, decodeBlp } from './blp.js';
import { inner, isHole, locateAdt, outer, parseAdt } from './adt.js';
import { GeometryBuilder, SQUARE_SIZE, encodeJson, tileOrigin } from './geometry.js';
import { ClientFsError, badFile } from './limits.js';

export const LAYER_TERRAIN_TEX = 'ref.terrain.tex';
export const TEXTURE_KEY_PREFIX = 'cft:';
/** Edge of the baked texture of one MCNK, in pixels. */
export const BAKE_SIZE = 128;
/** A layer texture repeats this many times across an MCNK. */
export const REPEATS = 8;
export const ALPHA_SIZE = 64;
export const MAX_LAYERS = 4;
/** MCLY layer flags. */
export const LAYER_FLAG = Object.freeze({ USE_ALPHA: 0x100, ALPHA_COMPRESSED: 0x200 });
/** Colour of a layer whose BLP is missing or damaged (R, G, B). */
export const FALLBACK_COLOUR = Object.freeze([112, 104, 92]);

const CHUNK_TOTAL = 256;
const MCLY_ENTRY = 16;
const MATERIAL_FLAGS = 0x20 | 0x40; // CLAMP_S | CLAMP_T: a baked chunk texture covers exactly its own MCNK
const ATG1_MAGIC = 0x31475441;
const ATX1_MAGIC = 0x31585441;
const HEADER_V2 = 64;
const STREAM_UV0 = 1 << 1;
const BLP_CACHE = 64;
const TILE_CACHE = 4;

export const textureKey = (map, x, y, chunk) => `${TEXTURE_KEY_PREFIX}${map}/${x}/${y}/${chunk}`;

/** The {map, x, y, chunk} of a texture key, or null when it is not a well-formed `cft:` key. */
export function parseTextureKey(key) {
    const match = /^cft:(\d{1,9})\/(\d{1,2})\/(\d{1,2})\/(\d{1,3})$/.exec(String(key));
    if (match === null) {
        return null;
    }
    const [map, x, y, chunk] = match.slice(1).map(Number);
    return x > 63 || y > 63 || chunk >= CHUNK_TOTAL ? null : { map, x, y, chunk };
}

// ---- MTEX / MCLY / MCAL -------------------------------------------------------------------------------------------------
function need(bytes, offset, count, what) {
    if (offset < 0 || count < 0 || offset + count > bytes.length) {
        throw badFile(`ADT ${what}: needs ${count} bytes at byte ${offset} but the file is ${bytes.length} bytes long.`);
    }
}

const tagAt = (bytes, offset) => String.fromCharCode(bytes[offset + 3], bytes[offset + 2], bytes[offset + 1], bytes[offset]);

/**
 * The tileset names and the texture layers of every MCNK of an ADT that parseAdt already accepted.
 * @returns {{names: string[], chunks: {layers: {texture: number, flags: number, alpha: Uint8Array|null}[], skippedLayers: number}[]}}
 *   `alpha` is 64x64 bytes (0 to 255), null for the base layer and for a layer without a usable map.
 */
export function readTextureLayers(bytes) {
    need(bytes, 0, 20 + 64, 'header');
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const mhdrData = 8 + view.getUint32(4, true) + 8; // MVER chunk, then the MHDR header
    need(bytes, mhdrData, 64, 'MHDR');
    const mcinOffset = view.getUint32(mhdrData + 4, true);
    const mtexOffset = view.getUint32(mhdrData + 8, true);

    const names = [];
    if (mtexOffset !== 0) {
        const start = mhdrData + mtexOffset;
        need(bytes, start, 8, 'MTEX');
        if (tagAt(bytes, start) !== 'MTEX') {
            throw badFile('ADT MTEX: the MHDR offset does not point at an MTEX chunk.');
        }
        const size = view.getUint32(start + 4, true);
        need(bytes, start + 8, size, 'MTEX');
        let from = start + 8;
        const end = start + 8 + size;
        for (let at = from; at < end; at++) {
            if (bytes[at] === 0) {
                // An empty string is kept: MCLY texture indices count every NUL-terminated entry.
                names.push(new TextDecoder('latin1').decode(bytes.subarray(from, at)));
                from = at + 1;
            }
        }
    }

    if (mcinOffset === 0) {
        throw badFile('ADT MCIN: the MHDR has no MCIN.');
    }
    const mcin = mhdrData + mcinOffset + 8;
    need(bytes, mcin, CHUNK_TOTAL * 16, 'MCIN');
    const chunks = [];
    for (let index = 0; index < CHUNK_TOTAL; index++) {
        const mcnk = view.getUint32(mcin + (index * 16), true);
        const header = mcnk + 8;
        need(bytes, header, 128, `MCNK ${index}`);
        const layerCount = view.getUint32(header + 0x0C, true);
        const offsetLayer = view.getUint32(header + 0x1C, true);
        const offsetAlpha = view.getUint32(header + 0x24, true);
        const layers = [];
        let skipped = 0;
        if (layerCount > 0 && offsetLayer !== 0) {
            const count = Math.min(layerCount, MAX_LAYERS);
            skipped += layerCount - count;
            const mcly = mcnk + offsetLayer;
            need(bytes, mcly, 8 + (count * MCLY_ENTRY), `MCNK ${index} MCLY`);
            if (tagAt(bytes, mcly) !== 'MCLY') {
                throw badFile(`ADT MCNK ${index}: the layer offset does not point at an MCLY chunk.`);
            }
            let mcalData = null;
            if (offsetAlpha !== 0) {
                const mcal = mcnk + offsetAlpha;
                need(bytes, mcal, 8, `MCNK ${index} MCAL`);
                if (tagAt(bytes, mcal) !== 'MCAL') {
                    throw badFile(`ADT MCNK ${index}: the alpha offset does not point at an MCAL chunk.`);
                }
                const declared = view.getUint32(mcal + 4, true);
                need(bytes, mcal + 8, declared, `MCNK ${index} MCAL`);
                mcalData = { start: mcal + 8, size: declared };
            }
            for (let i = 0; i < count; i++) {
                const entry = mcly + 8 + (i * MCLY_ENTRY);
                const texture = view.getUint32(entry, true);
                const flags = view.getUint32(entry + 4, true);
                const alphaOffset = view.getUint32(entry + 8, true);
                let alpha = null;
                if (i > 0 && (flags & LAYER_FLAG.USE_ALPHA) !== 0) {
                    if ((flags & LAYER_FLAG.ALPHA_COMPRESSED) !== 0 || mcalData === null) {
                        skipped++;
                    } else if (alphaOffset + (ALPHA_SIZE * ALPHA_SIZE / 2) > mcalData.size) {
                        throw badFile(`ADT MCNK ${index}: layer ${i} reads its alpha map at ${alphaOffset} past the ${mcalData.size}-byte MCAL.`);
                    } else {
                        alpha = new Uint8Array(ALPHA_SIZE * ALPHA_SIZE);
                        const base = mcalData.start + alphaOffset;
                        for (let p = 0; p < alpha.length; p++) {
                            const packed = bytes[base + (p >> 1)];
                            alpha[p] = ((p & 1) === 0 ? packed & 0xF : packed >> 4) * 17;
                        }
                    }
                }
                if (texture >= names.length) {
                    throw badFile(`ADT MCNK ${index}: layer ${i} names texture ${texture} but MTEX lists ${names.length}.`);
                }
                layers.push({ texture, flags, alpha });
            }
        }
        chunks.push({ layers, skippedLayers: skipped });
    }
    return { names, chunks };
}

// ---- Baking -------------------------------------------------------------------------------------------------------------
/** The mip level whose texel density is closest above two texels per output pixel of one repeat (a 2:1 minification at most). */
export function pickLevel(levels, size = BAKE_SIZE) {
    const perRepeat = (size / REPEATS) * 2;
    for (let i = 0; i < levels.length; i++) {
        if (Math.max(levels[i].width, levels[i].height) <= perRepeat) {
            return levels[i];
        }
    }
    return levels[levels.length - 1];
}

/** Bilinear sample of `level` at (s, t) in repeat units (the texture wraps), written to out[0..2]. */
function sampleWrapped(level, s, t, out) {
    const w = level.width;
    const h = level.height;
    const x = (s * w) - 0.5;
    const y = (t * h) - 0.5;
    const x0 = Math.floor(x);
    const y0 = Math.floor(y);
    const fx = x - x0;
    const fy = y - y0;
    const xa = ((x0 % w) + w) % w;
    const xb = (xa + 1) % w;
    const ya = ((y0 % h) + h) % h;
    const yb = (ya + 1) % h;
    const px = level.rgba;
    const a = ((ya * w) + xa) * 4;
    const b = ((ya * w) + xb) * 4;
    const c = ((yb * w) + xa) * 4;
    const d = ((yb * w) + xb) * 4;
    for (let k = 0; k < 3; k++) {
        const top = (px[a + k] * (1 - fx)) + (px[b + k] * fx);
        const bottom = (px[c + k] * (1 - fx)) + (px[d + k] * fx);
        out[k] = (top * (1 - fy)) + (bottom * fy);
    }
}

function sampleAlpha(alpha, u, v) {
    const x = (u * ALPHA_SIZE) - 0.5;
    const y = (v * ALPHA_SIZE) - 0.5;
    const x0 = Math.max(0, Math.floor(x));
    const y0 = Math.max(0, Math.floor(y));
    const x1 = Math.min(ALPHA_SIZE - 1, x0 + 1);
    const y1 = Math.min(ALPHA_SIZE - 1, y0 + 1);
    const fx = Math.min(1, Math.max(0, x - x0));
    const fy = Math.min(1, Math.max(0, y - y0));
    const top = (alpha[(y0 * ALPHA_SIZE) + x0] * (1 - fx)) + (alpha[(y0 * ALPHA_SIZE) + x1] * fx);
    const bottom = (alpha[(y1 * ALPHA_SIZE) + x0] * (1 - fx)) + (alpha[(y1 * ALPHA_SIZE) + x1] * fx);
    return ((top * (1 - fy)) + (bottom * fy)) / 255;
}

/**
 * Blends the layers of one MCNK into RGBA8 (opaque). `layers` is [{levels: {width,height,rgba}[]|null, alpha: Uint8Array|null}] bottom
 * first; a null `levels` draws FALLBACK_COLOUR. An empty list gives the fallback colour everywhere.
 */
export function bakeChunk(layers, size = BAKE_SIZE) {
    const out = new Uint8Array(size * size * 4);
    const rgb = [0, 0, 0];
    const picked = layers.map((l) => (l.levels === null ? null : pickLevel(l.levels, size)));
    for (let py = 0; py < size; py++) {
        const v = (py + 0.5) / size;
        for (let px = 0; px < size; px++) {
            const u = (px + 0.5) / size;
            let r = FALLBACK_COLOUR[0];
            let g = FALLBACK_COLOUR[1];
            let b = FALLBACK_COLOUR[2];
            for (let i = 0; i < layers.length; i++) {
                // The base layer covers everything; a further layer without a usable alpha map is not drawn.
                const weight = i === 0 ? 1 : layers[i].alpha === null ? 0 : sampleAlpha(layers[i].alpha, u, v);
                if (weight <= 0) {
                    continue;
                }
                if (picked[i] === null) {
                    rgb[0] = FALLBACK_COLOUR[0];
                    rgb[1] = FALLBACK_COLOUR[1];
                    rgb[2] = FALLBACK_COLOUR[2];
                } else {
                    sampleWrapped(picked[i], u * REPEATS, v * REPEATS, rgb);
                }
                r += (rgb[0] - r) * weight;
                g += (rgb[1] - g) * weight;
                b += (rgb[2] - b) * weight;
            }
            const at = ((py * size) + px) * 4;
            out[at] = Math.round(r);
            out[at + 1] = Math.round(g);
            out[at + 2] = Math.round(b);
            out[at + 3] = 255;
        }
    }
    return out;
}

/** An ATX1 version 1 texture (format RGBA8, one layer, the full mip chain by box filter) of a square RGBA8 image. */
export function encodeAtx1(rgba, size, key) {
    const levels = [{ width: size, height: size, rgba }];
    while (levels[levels.length - 1].width > 1 || levels[levels.length - 1].height > 1) {
        levels.push(boxDown(levels[levels.length - 1]));
    }
    const json = new TextEncoder().encode(JSON.stringify({ key, path: '', archive: '', source: 'baked', mean: null }));
    const total = 20 + levels.reduce((n, l) => n + 4 + l.rgba.length, 0) + json.length;
    const out = new Uint8Array(total);
    const view = new DataView(out.buffer);
    view.setUint32(0, ATX1_MAGIC, true);
    view.setUint16(4, 1, true);
    view.setUint16(6, 1, true); // RGBA8
    view.setUint16(8, size, true);
    view.setUint16(10, size, true);
    view.setUint16(12, 1, true);
    out[14] = levels.length;
    out[15] = 0;
    view.setUint32(16, json.length, true);
    let at = 20;
    for (const level of levels) {
        view.setUint32(at, level.rgba.length, true);
        out.set(level.rgba, at + 4);
        at += 4 + level.rgba.length; // RGBA levels are always a multiple of 4 bytes: no padding
    }
    out.set(json, at);
    return out;
}

// ---- Geometry (ATG1 version 2) ------------------------------------------------------------------------------------------
function writeAtg1V2(c) {
    const attributes = new Map([...c.attributes.entries()].sort((a, b) => a[0] - b[0]).map(([id, value]) => [String(id), value]));
    const json = new TextEncoder().encode(encodeJson({ layer: c.layer, tile: c.tile, part: 0, attributes, stats: c.stats, materials: c.materials }));
    const triangleCount = c.triangles.length / 3;
    const words = [c.positions, c.uv0, c.triangles, c.triangleAttributes, c.batches];
    const jsonAt = HEADER_V2 + words.reduce((n, array) => n + (array.length * 4), 0);
    const out = new Uint8Array(jsonAt + 4 + json.length);
    const view = new DataView(out.buffer);
    view.setUint32(0, ATG1_MAGIC, true);
    view.setUint16(4, 2, true);
    view.setUint16(6, triangleCount > 0 ? 1 : 0, true);
    view.setFloat64(8, c.origin[0], true);
    view.setFloat64(16, c.origin[1], true);
    view.setFloat64(24, c.origin[2], true);
    view.setUint32(32, c.positions.length / 3, true);
    view.setUint32(36, triangleCount, true);
    view.setUint32(40, 0, true);
    view.setUint32(44, 0, true);
    view.setUint32(48, STREAM_UV0, true);
    view.setUint32(52, c.batches.length / 4, true);
    let offset = HEADER_V2;
    for (const array of words) {
        out.set(new Uint8Array(array.buffer, array.byteOffset, array.byteLength), offset);
        offset += array.length * 4;
    }
    view.setUint32(jsonAt, json.length, true);
    out.set(json, jsonAt + 4);
    return out;
}

/**
 * The textured terrain chunk of a parsed ADT: one batch and one `terrain` material per MCNK with a visible square, the MCNK's own
 * vertices (not shared across MCNKs, because their UVs differ) with UV (column / 8, row / 8), triangles in the order of the plain
 * terrain chunk. Returns the ATG1 version 2 bytes.
 */
export function buildTexturedTerrainAtg1(tile, adt, layerInfo) {
    const origin = tileOrigin(tile.x, tile.y);
    const builder = new GeometryBuilder(LAYER_TERRAIN_TEX, [tile.map, tile.x, tile.y], origin);
    const uv0 = [];
    const batches = [];
    const materials = [];
    const attributes = new Map();
    let drawn = 0;
    let holes = 0;
    let skippedLayers = 0;
    for (const chunk of adt.chunks) {
        const corners = new Int32Array(81).fill(-1);
        const centres = new Int32Array(64).fill(-1);
        const row0 = chunk.row * 8;
        const col0 = chunk.column * 8;
        const cornerAt = (y, x) => {
            const slot = (y * 9) + x;
            if (corners[slot] < 0) {
                const height = outer(chunk, y, x);
                corners[slot] = builder.addVertex(origin[0] - ((row0 + y) * SQUARE_SIZE), origin[1] - ((col0 + x) * SQUARE_SIZE), height);
                uv0.push(Math.fround(x / 8), Math.fround(y / 8));
            }
            return corners[slot];
        };
        const first = builder.triangles.length / 3;
        for (let y = 0; y < 8; y++) {
            for (let x = 0; x < 8; x++) {
                if (isHole(chunk, y, x)) {
                    holes++;
                    continue;
                }
                const p1 = cornerAt(y, x);
                const p2 = cornerAt(y + 1, x);
                const p3 = cornerAt(y, x + 1);
                const p4 = cornerAt(y + 1, x + 1);
                const centre = builder.addVertex(
                    origin[0] - ((row0 + y + 0.5) * SQUARE_SIZE),
                    origin[1] - ((col0 + x + 0.5) * SQUARE_SIZE),
                    inner(chunk, y, x));
                uv0.push(Math.fround((x + 0.5) / 8), Math.fround((y + 0.5) / 8));
                centres[(y * 8) + x] = centre;
                builder.addTriangle(p1, p2, centre, chunk.index);
                builder.addTriangle(p1, centre, p3, chunk.index);
                builder.addTriangle(p2, p4, centre, chunk.index);
                builder.addTriangle(p3, centre, p4, chunk.index);
                drawn++;
            }
        }
        const count = (builder.triangles.length / 3) - first;
        if (count === 0) {
            continue;
        }
        const info = layerInfo.chunks[chunk.index];
        skippedLayers += info.skippedLayers;
        materials.push({
            kind: 'terrain',
            blend: 0,
            flags: MATERIAL_FLAGS,
            textures: [{ key: textureKey(tile.map, tile.x, tile.y, chunk.index), slot: 0, wrap: [1, 1] }],
        });
        batches.push(first, count, materials.length - 1, chunk.index);
        attributes.set(chunk.index, { chunk: [chunk.row, chunk.column], layers: info.layers.length });
    }
    return writeAtg1V2({
        layer: LAYER_TERRAIN_TEX,
        tile: [tile.map, tile.x, tile.y],
        origin,
        positions: Float32Array.from(builder.positions),
        uv0: Float32Array.from(uv0),
        triangles: Uint32Array.from(builder.triangles),
        triangleAttributes: Uint32Array.from(builder.triangleAttributes),
        batches: Uint32Array.from(batches),
        materials,
        attributes,
        stats: { source: 'adt', chunks: batches.length / 4, squares: drawn, holeSquares: holes, skippedLayers, bakeSize: BAKE_SIZE },
    });
}

// ---- Worker entry points ------------------------------------------------------------------------------------------------
const caches = new WeakMap();

function cacheOf(chain) {
    let cache = caches.get(chain);
    if (cache === undefined) {
        cache = { blp: new Map(), tiles: new Map() };
        caches.set(chain, cache);
    }
    return cache;
}

function remember(map, key, value, limit) {
    map.delete(key);
    map.set(key, value);
    while (map.size > limit) {
        map.delete(map.keys().next().value);
    }
}

/** The decoded mip chain of a tileset BLP, or null when the file is missing or damaged (the layer then draws FALLBACK_COLOUR). */
async function loadLevels(chain, path) {
    const cache = cacheOf(chain).blp;
    if (cache.has(path)) {
        const hit = cache.get(path);
        remember(cache, path, hit, BLP_CACHE);
        return hit;
    }
    let levels = null;
    try {
        const bytes = await chain.read(path);
        if (bytes !== null && bytes.length > 0) {
            levels = decodeBlp(bytes).levels;
        }
    } catch (error) {
        if (!(error instanceof ClientFsError && error.code === 'bad-file')) {
            throw error;
        }
    }
    remember(cache, path, levels, BLP_CACHE);
    return levels;
}

const result = (state, detail, parts = []) => ({ state, detail, parts });

/** One tile of ref.terrain.tex from the archive chain: {state, detail, parts} like adt.js buildTile. */
export async function buildTexturedTile(chain, map, x, y) {
    const found = await locateAdt(chain, map, x, y);
    if (found.failure) {
        return found.failure;
    }
    try {
        const adt = parseAdt(found.bytes);
        const info = readTextureLayers(found.bytes);
        return result('Ok', null, [buildTexturedTerrainAtg1({ map, x, y }, adt, info)]);
    } catch (error) {
        if (error instanceof ClientFsError && error.code === 'bad-file') {
            return result('Rejected', `${found.path}: ${error.message}`);
        }
        throw error;
    }
}

/** The ATX1 bytes of the baked texture named by a `cft:` key. Throws ClientFsError 'bad-file' for a bad key, tile or chunk. */
export async function bakeTexture(chain, key) {
    const parsed = parseTextureKey(key);
    if (parsed === null) {
        throw badFile(`'${key}' is not a client terrain texture key.`);
    }
    const cache = cacheOf(chain).tiles;
    const tileId = `${parsed.map}/${parsed.x}/${parsed.y}`;
    let info = cache.get(tileId);
    if (info === undefined) {
        const found = await locateAdt(chain, parsed.map, parsed.x, parsed.y);
        if (found.failure) {
            throw badFile(`${tileId}: ${found.failure.detail}`);
        }
        parseAdt(found.bytes); // the same acceptance as the tile itself
        info = readTextureLayers(found.bytes);
    }
    remember(cache, tileId, info, TILE_CACHE);
    const layers = [];
    for (const layer of info.chunks[parsed.chunk].layers) {
        layers.push({ levels: await loadLevels(chain, info.names[layer.texture]), alpha: layer.alpha });
    }
    return encodeAtx1(bakeChunk(layers), BAKE_SIZE, key);
}
