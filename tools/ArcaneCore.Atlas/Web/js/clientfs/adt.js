// ADT terrain tiles of the 1.12.1 client, read in a Worker: a port of Reference/Client/Terrain (AdtFile, ClientChunks,
// AdtGeometry, ClientTerrainGeometry, ClientMapPaths) that produces the ref.terrain and ref.liquid ATG1 chunks byte for byte
// as the C# core does (GeometryWire.Write of the raw Build* chunk; the server adds path/archive/bytes/buildMs stats on top,
// which a browser build has no use for and which are nondeterministic).
//
// Every offset and size is checked against the file before it is used; a damaged file is refused with ClientFsError
// 'bad-file' and never reads outside the buffer. Heights are single-precision exactly as in C# (Math.fround at every float
// operation), vertex positions are double arithmetic narrowed to float, so the output is identical to the bit.

import { F32, GeometryBuilder, SQUARE_SIZE, TILE_SIZE, tileOrigin, writeAtg1 } from './geometry.js';
import { parseDbc } from './dbc.js';
import { badFile, ClientFsError } from './limits.js';

export const LAYER_TERRAIN = 'ref.terrain';
export const LAYER_LIQUID = 'ref.liquid';

const FORMAT_VERSION = 18;
const CHUNKS_PER_SIDE = 16;
const CHUNK_TOTAL = 256;
const HEADER_BYTES = 8;
const MHDR_PAYLOAD = 64;
const MCVT_FLOATS = 145;
const MCVT_BYTES = MCVT_FLOATS * 4;
const MCNK_HEADER = 128;
const STRIDE = 17;
const LAYER_BYTES = 804;
const HIDDEN_SQUARE = 0x0F;
const DARK_WATER = 0x80;
const DOODAD_BYTES = 36;
const WMO_BYTES = 64;
const KIND_NAMES = ['river', 'ocean', 'magma', 'slime'];
const KIND_FLAGS = [0x04, 0x08, 0x10, 0x20];
const KIND_COLORS = ['water', 'ocean', 'magma', 'slime'];
const DEEP_WATER = 'deep';

const MAP_TABLE = 'DBFilesClient\\Map.dbc';
const AREA_TABLE = 'DBFilesClient\\AreaTable.dbc';
export const MAP_FIELD_COUNT = 42;
export const AREA_FIELD_COUNT = 25;

// ---- Chunk container (ClientChunks) -------------------------------------------------------------------------------
class Reader {
    constructor(bytes) {
        this.length = bytes.length;
        this.bytes = bytes;
        this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    }

    need(offset, count, context) {
        if (offset < 0 || count < 0 || offset + count > this.length) {
            throw badFile(`${context}: needs ${count} bytes at byte ${offset} but the data is ${this.length} bytes long.`);
        }
    }

    u32(offset) {
        return this.view.getUint32(offset, true);
    }

    u16(offset) {
        return this.view.getUint16(offset, true);
    }

    f32(offset) {
        return this.view.getFloat32(offset, true);
    }

    tag(offset) {
        const b = this.bytes;
        return String.fromCharCode(b[offset + 3], b[offset + 2], b[offset + 1], b[offset]);
    }

    /** The chunk header at `start`; its payload must lie inside the data. */
    chunk(start, context) {
        this.need(start, HEADER_BYTES, context);
        const id = this.tag(start);
        const size = this.u32(start + 4);
        const available = this.length - (start + HEADER_BYTES);
        if (size > available) {
            throw badFile(`${context}: chunk ${id} at byte ${start} declares ${size} bytes but only ${available} remain.`);
        }
        return { id, start, dataStart: start + HEADER_BYTES, size, end: start + HEADER_BYTES + size };
    }

    expect(start, expectedId, context) {
        const chunk = this.chunk(start, context);
        if (chunk.id !== expectedId) {
            throw badFile(`${context}: expected chunk ${expectedId} at byte ${start}, found ${chunk.id}.`);
        }
        return chunk;
    }
}

/** The number of NUL-terminated strings in a name chunk; an unterminated one is refused (ClientChunks.Strings). */
function checkStrings(reader, chunk, context) {
    let position = chunk.dataStart;
    let count = 0;
    while (position < chunk.end) {
        let stop = position;
        while (stop < chunk.end && reader.bytes[stop] !== 0) {
            stop++;
        }
        if (stop === chunk.end) {
            throw badFile(`${context}: a name at byte ${position - chunk.dataStart} has no terminator inside its chunk.`);
        }
        count++;
        position = stop + 1;
    }
    return count;
}

function stringAtOk(reader, chunk, offset, context) {
    if (offset >= chunk.size) {
        throw badFile(`${context}: name offset ${offset} is outside the ${chunk.size}-byte name chunk.`);
    }
    let stop = chunk.dataStart + offset;
    while (stop < chunk.end && reader.bytes[stop] !== 0) {
        stop++;
    }
    if (stop === chunk.end) {
        throw badFile(`${context}: the name at offset ${offset} has no terminator inside its chunk.`);
    }
}

// ---- Parse (AdtFile) -----------------------------------------------------------------------------------------------
function locate(reader, mhdr, header, field, id, context) {
    const relative = header[field];
    if (relative === 0) {
        return null;
    }
    const start = mhdr.dataStart + relative;
    if (start > reader.length - HEADER_BYTES) {
        throw badFile(`${context}: MHDR offset ${relative} for ${id} points past the ${reader.length}-byte file.`);
    }
    return reader.expect(start, id, `${context} MHDR`);
}

function readNames(reader, mhdr, header, nameField, nameId, indexField, indexId, context) {
    const names = locate(reader, mhdr, header, nameField, nameId, context);
    const index = locate(reader, mhdr, header, indexField, indexId, context);
    if (names === null && index === null) {
        return 0;
    }
    if (names === null || index === null) {
        throw badFile(`${context}: ${nameId} and ${indexId} must both be present or both absent.`);
    }
    if (index.size % 4 !== 0) {
        throw badFile(`${context}: ${indexId} holds ${index.size} bytes, not a whole number of u32 offsets.`);
    }
    checkStrings(reader, names, `${context} ${nameId}`);
    const paths = index.size / 4;
    for (let i = 0; i < paths; i++) {
        stringAtOk(reader, names, reader.u32(index.dataStart + (i * 4)), `${context} ${indexId}[${i}]`);
    }
    return paths;
}

function readPlacements(reader, chunk, entryBytes, nameCount, what, context) {
    if (chunk.size % entryBytes !== 0) {
        throw badFile(`${context}: ${what} is ${chunk.size} bytes, not a whole number of ${entryBytes}-byte entries.`);
    }
    const count = chunk.size / entryBytes;
    for (let i = 0; i < count; i++) {
        const index = reader.u32(chunk.dataStart + (i * entryBytes));
        if (index >= nameCount) {
            throw badFile(`${context}: ${what} entry ${i} names model ${index} but the name table has ${nameCount}.`);
        }
    }
    return count;
}

function popCount(value) {
    let v = value >>> 0;
    let n = 0;
    while (v !== 0) {
        n += v & 1;
        v >>>= 1;
    }
    return n;
}

function readLiquid(reader, mcnk, flags, offsetMclq, sizeMclq, context) {
    const expected = popCount(flags & 0x3C);
    let stored = 0;
    let layersStart = 0;
    if (offsetMclq !== 0) {
        const start = mcnk.start + offsetMclq;
        reader.need(start, HEADER_BYTES, context);
        const tag = reader.tag(start);
        if (tag !== 'MCLQ') {
            throw badFile(`${context}: the liquid offset points at ${tag}, not MCLQ.`);
        }
        if (sizeMclq > HEADER_BYTES) {
            const payload = sizeMclq - HEADER_BYTES;
            if (payload % LAYER_BYTES !== 0) {
                throw badFile(`${context}: the MCLQ size ${sizeMclq} is not 8 plus a whole number of ${LAYER_BYTES}-byte layers.`);
            }
            stored = payload / LAYER_BYTES;
            layersStart = start + HEADER_BYTES;
            reader.need(layersStart, stored * LAYER_BYTES, context);
        }
    }
    if (stored !== expected) {
        throw badFile(`${context}: the flags 0x${flags.toString(16).toUpperCase()} select ${expected} liquid layer(s) but MCLQ stores ${stored}.`);
    }
    const layers = [];
    for (let kind = 0; kind < 4; kind++) {
        if ((flags & KIND_FLAGS[kind]) === 0) {
            continue;
        }
        const at = layersStart + (layers.length * LAYER_BYTES);
        const heights = new Float32Array(81);
        for (let i = 0; i < 81; i++) {
            heights[i] = reader.f32(at + 8 + (i * 8) + 4);
        }
        layers.push({
            kind,
            min: reader.f32(at),
            max: reader.f32(at + 4),
            heights,
            flags: reader.bytes.slice(at + 8 + (81 * 8), at + 8 + (81 * 8) + 64),
        });
    }
    return layers;
}

function readChunk(reader, index, mcnk) {
    const context = `ADT MCNK ${index}`;
    if (mcnk.size < MCNK_HEADER) {
        throw badFile(`${context}: holds ${mcnk.size} bytes, the header alone is ${MCNK_HEADER}.`);
    }
    const h = mcnk.dataStart;
    const flags = reader.u32(h);
    const indexX = reader.u32(h + 0x04);
    const indexY = reader.u32(h + 0x08);
    const textureLayers = reader.u32(h + 0x0C);
    const offsetMcvt = reader.u32(h + 0x14);
    const areaId = reader.u32(h + 0x34);
    const holes = reader.u16(h + 0x3C);
    const offsetMclq = reader.u32(h + 0x60);
    const sizeMclq = reader.u32(h + 0x64);
    const baseX = reader.f32(h + 0x68);
    const baseY = reader.f32(h + 0x6C);
    const baseHeight = reader.f32(h + 0x70);
    if (indexX !== index % CHUNKS_PER_SIDE || indexY !== Math.floor(index / CHUNKS_PER_SIDE)) {
        throw badFile(`${context}: header says column ${indexX}, row ${indexY} but its MCIN slot is column ${index % CHUNKS_PER_SIDE}, row ${Math.floor(index / CHUNKS_PER_SIDE)}.`);
    }
    if (!Number.isFinite(baseX) || !Number.isFinite(baseY) || !Number.isFinite(baseHeight)) {
        throw badFile(`${context}: the header position (${baseX}, ${baseY}, ${baseHeight}) is not finite.`);
    }
    const mcvt = new Float32Array(MCVT_FLOATS);
    const hasHeights = offsetMcvt !== 0;
    if (hasHeights) {
        const start = mcnk.start + offsetMcvt;
        if (start > reader.length - HEADER_BYTES) {
            throw badFile(`${context}: MCVT offset ${offsetMcvt} points past the file.`);
        }
        const chunk = reader.expect(start, 'MCVT', context);
        if (chunk.size !== MCVT_BYTES) {
            throw badFile(`${context}: MCVT holds ${chunk.size} bytes, expected ${MCVT_BYTES}.`);
        }
        for (let i = 0; i < MCVT_FLOATS; i++) {
            const value = reader.f32(chunk.dataStart + (i * 4));
            if (!Number.isFinite(value)) {
                throw badFile(`${context}: MCVT holds a height that is not finite.`);
            }
            mcvt[i] = value;
        }
    }
    const layers = readLiquid(reader, mcnk, flags, offsetMclq, sizeMclq, context);
    return { index, row: Math.floor(index / CHUNKS_PER_SIDE), column: index % CHUNKS_PER_SIDE, flags, areaId, holes, textureLayers, baseX, baseY, baseHeight, hasHeights, mcvt, layers };
}

/** Parses a 1.12.1 ADT (MVER 18). Throws ClientFsError 'bad-file' with the reason for anything inconsistent. */
export function parseAdt(bytes) {
    const context = 'ADT';
    if (!(bytes instanceof Uint8Array) || bytes.length === 0) {
        throw badFile(`${context}: the file is empty.`);
    }
    const reader = new Reader(bytes);
    const mver = reader.expect(0, 'MVER', context);
    if (mver.size < 4) {
        throw badFile(`${context}: MVER holds ${mver.size} bytes, a version needs 4.`);
    }
    const version = reader.u32(mver.dataStart);
    if (version !== FORMAT_VERSION) {
        throw badFile(`${context}: MVER is ${version}, 1.12.1 ADTs are ${FORMAT_VERSION}.`);
    }
    const mhdr = reader.expect(mver.end, 'MHDR', context);
    if (mhdr.size !== MHDR_PAYLOAD) {
        throw badFile(`${context}: MHDR holds ${mhdr.size} bytes, expected ${MHDR_PAYLOAD}.`);
    }
    const header = new Uint32Array(MHDR_PAYLOAD / 4);
    for (let i = 0; i < header.length; i++) {
        header[i] = reader.u32(mhdr.dataStart + (i * 4));
    }
    if (header[10] !== 0) {
        throw badFile(`${context}: MHDR points at an MH2O liquid chunk, which build 5875 never writes (WotLK data?).`);
    }
    const mcin = locate(reader, mhdr, header, 1, 'MCIN', context);
    if (mcin === null) {
        throw badFile(`${context}: MHDR has no MCIN.`);
    }
    if (mcin.size !== CHUNK_TOTAL * 16) {
        throw badFile(`${context}: MCIN holds ${mcin.size} bytes, expected ${CHUNK_TOTAL * 16}.`);
    }
    const mtex = locate(reader, mhdr, header, 2, 'MTEX', context);
    if (mtex !== null) {
        checkStrings(reader, mtex, `${context} MTEX`);
    }
    const doodadNames = readNames(reader, mhdr, header, 3, 'MMDX', 4, 'MMID', context);
    const wmoNames = readNames(reader, mhdr, header, 5, 'MWMO', 6, 'MWID', context);
    let doodads = 0;
    const mddf = locate(reader, mhdr, header, 7, 'MDDF', context);
    if (mddf !== null) {
        doodads = readPlacements(reader, mddf, DOODAD_BYTES, doodadNames, 'MDDF', context);
    }
    let wmos = 0;
    const modf = locate(reader, mhdr, header, 8, 'MODF', context);
    if (modf !== null) {
        wmos = readPlacements(reader, modf, WMO_BYTES, wmoNames, 'MODF', context);
    }

    const chunks = Array.from({ length: CHUNK_TOTAL });
    for (let index = 0; index < CHUNK_TOTAL; index++) {
        const entry = mcin.dataStart + (index * 16);
        const offset = reader.u32(entry);
        const size = reader.u32(entry + 4);
        if (offset > bytes.length) {
            throw badFile(`${context}: MCIN entry ${index} points at byte ${offset}, past the ${bytes.length}-byte file.`);
        }
        const mcnk = reader.expect(offset, 'MCNK', `${context} MCIN entry ${index}`);
        if (size !== mcnk.size + HEADER_BYTES) {
            throw badFile(`${context}: MCIN entry ${index} says ${size} bytes but its MCNK spans ${mcnk.size + HEADER_BYTES}.`);
        }
        chunks[index] = readChunk(reader, index, mcnk);
    }
    return { byteLength: bytes.length, doodads, wmos, chunks };
}

// ---- Geometry (AdtGeometry, ClientTerrainGeometry) -----------------------------------------------------------------
const outer = (chunk, y, x) => Math.fround(chunk.baseHeight + chunk.mcvt[(y * STRIDE) + x]);
const inner = (chunk, y, x) => Math.fround(chunk.baseHeight + chunk.mcvt[(y * STRIDE) + 9 + x]);
const isHole = (chunk, y, x) => (chunk.holes & (1 << (((y >> 1) * 4) + (x >> 1)))) !== 0;
const holeSquares = (chunk) => popCount(chunk.holes) * 4;
const visible = (layer, y, x) => layer.flags[(y * 8) + x] !== HIDDEN_SQUARE;
const darkWater = (layer, y, x) => {
    const flags = layer.flags[(y * 8) + x];
    return flags !== HIDDEN_SQUARE && (flags & DARK_WATER) !== 0;
};

const bitsView = new DataView(new ArrayBuffer(4));
function floatBits(value) {
    bitsView.setFloat32(0, value, true);
    return bitsView.getUint32(0, true);
}

const terrainAttribute = (chunkIndex) => chunkIndex;
const liquidAttribute = (chunkIndex, kind, dark) => (((chunkIndex * 4) + kind) * 2) + (dark ? 1 : 0);

function finite(value, chunk, what, y, x) {
    if (!Number.isFinite(value)) {
        throw badFile(`ADT MCNK ${chunk.index}: the ${what} height at (${y}, ${x}) is ${value}, not a finite number (base ${chunk.baseHeight} plus a stored offset).`);
    }
}

function positionMismatches(adt, tile, tolerance = 0.01) {
    const origin = tileOrigin(tile.x, tile.y);
    const chunkSize = TILE_SIZE / CHUNKS_PER_SIDE;
    let mismatches = 0;
    for (const chunk of adt.chunks) {
        const expectedX = origin[0] - (chunk.row * chunkSize);
        const expectedY = origin[1] - (chunk.column * chunkSize);
        if (Math.abs(chunk.baseX - expectedX) > tolerance || Math.abs(chunk.baseY - expectedY) > tolerance) {
            mismatches++;
        }
    }
    return mismatches;
}

/** ClientTerrainGeometry.BuildTerrain: four triangles around the centre sample of every square the hole bitmap keeps. */
export function buildTerrainChunk(tile, adt, areaName) {
    const origin = tileOrigin(tile.x, tile.y);
    const builder = new GeometryBuilder(LAYER_TERRAIN, [tile.map, tile.x, tile.y], origin);
    const corners = new Map();
    let drawn = 0;
    let holes = 0;
    let flat = 0;
    let minZ = Infinity;
    let maxZ = -Infinity;

    const corner = (chunk, y, x) => {
        const height = outer(chunk, y, x);
        finite(height, chunk, 'corner', y, x);
        const row = (chunk.row * 8) + y;
        const column = (chunk.column * 8) + x;
        const key = (((row * 129) + column) * 4294967296) + floatBits(height);
        let id = corners.get(key);
        if (id === undefined) {
            id = builder.addVertex(origin[0] - (row * SQUARE_SIZE), origin[1] - (column * SQUARE_SIZE), height);
            corners.set(key, id);
        }
        return id;
    };

    for (const chunk of adt.chunks) {
        if (!chunk.hasHeights) {
            flat++;
        }
        const attribute = terrainAttribute(chunk.index);
        for (let y = 0; y < 8; y++) {
            for (let x = 0; x < 8; x++) {
                if (isHole(chunk, y, x)) {
                    holes++;
                    continue;
                }
                if (!builder.hasAttribute(attribute)) {
                    builder.describeAttribute(attribute, () => ({
                        chunk: [chunk.row, chunk.column],
                        areaId: chunk.areaId,
                        area: areaName(chunk.areaId) ?? null,
                        flags: `0x${chunk.flags.toString(16).toUpperCase()}`,
                        holeSquares: holeSquares(chunk),
                        hasHeights: chunk.hasHeights,
                        textureLayers: chunk.textureLayers,
                        liquid: chunk.layers.map((l) => KIND_NAMES[l.kind]),
                    }));
                }
                const p1 = corner(chunk, y, x);
                const p2 = corner(chunk, y + 1, x);
                const p3 = corner(chunk, y, x + 1);
                const p4 = corner(chunk, y + 1, x + 1);
                const centreHeight = inner(chunk, y, x);
                finite(centreHeight, chunk, 'centre', y, x);
                const centre = builder.addVertex(
                    origin[0] - ((((chunk.row * 8) + y) + 0.5) * SQUARE_SIZE),
                    origin[1] - ((((chunk.column * 8) + x) + 0.5) * SQUARE_SIZE),
                    centreHeight);
                builder.addTriangle(p1, p2, centre, attribute);
                builder.addTriangle(p1, centre, p3, attribute);
                builder.addTriangle(p2, p4, centre, attribute);
                builder.addTriangle(p3, centre, p4, attribute);
                drawn++;
                minZ = Math.min(minZ, centreHeight);
                maxZ = Math.max(maxZ, centreHeight);
            }
        }
    }

    const stats = builder.stats;
    stats.source = 'adt';
    stats.chunks = adt.chunks.length;
    stats.squares = drawn;
    stats.holeSquares = holes;
    stats.chunksWithoutHeights = flat;
    stats.positionMismatches = positionMismatches(adt, tile);
    stats.doodads = adt.doodads;
    stats.wmos = adt.wmos;
    if (drawn > 0) {
        stats.minZ = new F32(minZ);
        stats.maxZ = new F32(maxZ);
    }
    return builder.build();
}

/** ClientTerrainGeometry.BuildLiquid: two triangles per visible liquid square, layers kept apart; null when nothing shows. */
export function buildLiquidChunk(tile, adt) {
    const origin = tileOrigin(tile.x, tile.y);
    const builder = new GeometryBuilder(LAYER_LIQUID, [tile.map, tile.x, tile.y], origin);
    const corners = new Map();
    const layersByKind = [0, 0, 0, 0];
    const squaresByKind = [0, 0, 0, 0];
    let dark = 0;
    let stacked = 0;
    let minLevel = Infinity;
    let maxLevel = -Infinity;

    const corner = (chunk, layer, y, x) => {
        const height = layer.heights[(y * 9) + x];
        if (!Number.isFinite(height)) {
            throw badFile(`ADT MCNK ${chunk.index}: the ${KIND_NAMES[layer.kind]} liquid layer shows a square at corner (${y}, ${x}) whose height ${height} is not finite.`);
        }
        const row = (chunk.row * 8) + y;
        const column = (chunk.column * 8) + x;
        const key = `${layer.kind}:${(row * 129) + column}:${floatBits(height)}`;
        let id = corners.get(key);
        if (id === undefined) {
            id = builder.addVertex(origin[0] - (row * SQUARE_SIZE), origin[1] - (column * SQUARE_SIZE), height);
            corners.set(key, id);
        }
        return id;
    };

    for (const chunk of adt.chunks) {
        if (chunk.layers.length > 1) {
            stacked++;
        }
        for (const layer of chunk.layers) {
            layersByKind[layer.kind]++;
            for (let y = 0; y < 8; y++) {
                for (let x = 0; x < 8; x++) {
                    if (!visible(layer, y, x)) {
                        continue;
                    }
                    const isDark = darkWater(layer, y, x);
                    const attribute = liquidAttribute(chunk.index, layer.kind, isDark);
                    if (!builder.hasAttribute(attribute)) {
                        builder.describeAttribute(attribute, () => ({
                            colorKey: isDark ? DEEP_WATER : KIND_COLORS[layer.kind],
                            liquid: KIND_NAMES[layer.kind],
                            darkWater: isDark,
                            chunk: [chunk.row, chunk.column],
                            areaId: chunk.areaId,
                            layerMin: Number.isFinite(layer.min) ? new F32(layer.min) : null,
                            layerMax: Number.isFinite(layer.max) ? new F32(layer.max) : null,
                            layersInChunk: chunk.layers.length,
                        }));
                    }
                    const a = corner(chunk, layer, y, x);
                    const b = corner(chunk, layer, y + 1, x);
                    const c = corner(chunk, layer, y, x + 1);
                    const d = corner(chunk, layer, y + 1, x + 1);
                    builder.addTriangle(a, b, c, attribute);
                    builder.addTriangle(b, d, c, attribute);
                    squaresByKind[layer.kind]++;
                    dark += isDark ? 1 : 0;
                    for (const h of [layer.heights[(y * 9) + x], layer.heights[((y + 1) * 9) + x], layer.heights[(y * 9) + x + 1], layer.heights[((y + 1) * 9) + x + 1]]) {
                        minLevel = Math.min(minLevel, h);
                        maxLevel = Math.max(maxLevel, h);
                    }
                }
            }
        }
    }

    if (builder.isEmpty) {
        return null;
    }
    const stats = builder.stats;
    stats.source = 'adt';
    stats.merged = false;
    stats.stackedChunks = stacked;
    stats.darkWaterSquares = dark;
    stats.minLevel = new F32(minLevel);
    stats.maxLevel = new F32(maxLevel);
    stats.layers = Object.fromEntries(KIND_NAMES.map((name, i) => [name, layersByKind[i]]));
    stats.squares = Object.fromEntries(KIND_NAMES.map((name, i) => [name, squaresByKind[i]]));
    return builder.build();
}

/**
 * The ATG1 bytes of the tile's terrain chunk, equal to GeometryWire.Write(ClientTerrainGeometry.BuildTerrain(..)).
 * @param {Uint8Array} adtBytes the file (a view with any byteOffset)
 * @param {{map: number, x: number, y: number}} tile server tile indices
 * @param {(areaId: number) => string|null|undefined} areaName AreaTable.dbc lookup
 */
export function buildTerrainAtg1(adtBytes, tile, areaName) {
    return writeAtg1(buildTerrainChunk(tile, parseAdt(adtBytes), areaName));
}

/** The ATG1 bytes of the tile's liquid chunk, or null when no layer shows a square. */
export function buildLiquidAtg1(adtBytes, tile) {
    const chunk = buildLiquidChunk(tile, parseAdt(adtBytes));
    return chunk === null ? null : writeAtg1(chunk);
}

// ---- Tiles from the archive chain (ClientTerrainService / ClientTerrainLayerSource) --------------------------------
const tables = new WeakMap();

/** Map.dbc directories and AreaTable.dbc names of a chain, parsed once per chain (ClientMapDirectory.Load). */
async function loadDirectory(chain) {
    let loaded = tables.get(chain);
    if (loaded === undefined) {
        loaded = (async () => {
            const maps = new Map();
            const areas = new Map();
            const seenAreas = new Set(); // every AreaTable id, named or not: a repeated id refuses the table like ClientMapDirectory.FromTables
            const read = async (path, fields) => {
                const bytes = await chain.read(path);
                if (bytes === null) {
                    return { missing: `${path} is not in the client files.` };
                }
                try {
                    const table = parseDbc(bytes);
                    if (table.fieldCount !== fields) {
                        throw badFile(`${path} has ${table.fieldCount} fields; the build 5875 (1.12.1) layout has ${fields}. Other builds are not supported.`);
                    }
                    return { table };
                } catch (error) {
                    if (error instanceof ClientFsError) {
                        return { rejected: error.message.startsWith(path) ? error.message : `${path}: ${error.message}` };
                    }
                    throw error;
                }
            };
            const map = await read(MAP_TABLE, MAP_FIELD_COUNT);
            if (map.table) {
                for (let r = 0; r < map.table.recordCount; r++) {
                    const id = map.table.cell(r, 0);
                    if (maps.has(id)) {
                        return { maps, areas, problem: `${MAP_TABLE} lists map id ${id} twice.`, rejected: true };
                    }
                    maps.set(id, map.table.string(map.table.cell(r, 1)));
                }
            }
            const area = await read(AREA_TABLE, AREA_FIELD_COUNT);
            if (area.table) {
                for (let r = 0; r < area.table.recordCount; r++) {
                    const id = area.table.cell(r, 0);
                    if (seenAreas.has(id)) {
                        return { maps, areas, problem: `${AREA_TABLE} lists area id ${id} twice.`, rejected: true };
                    }
                    seenAreas.add(id);
                    const name = area.table.localized(r, 11);
                    if (name.length > 0) {
                        areas.set(id, name);
                    }
                }
            }
            return { maps, areas, problem: map.missing ?? map.rejected ?? area.missing ?? area.rejected ?? null, rejected: Boolean(map.rejected ?? area.rejected) };
        })();
        tables.set(chain, loaded);
        // A rejected load (a thrown read, not a refused table, which resolves with a problem) must not be remembered for the chain.
        loaded.catch(() => {
            if (tables.get(chain) === loaded) {
                tables.delete(chain);
            }
        });
    }
    return loaded;
}

const result = (state, detail, parts = []) => ({ state, detail, parts });

/**
 * Builds one tile of ref.terrain or ref.liquid from the archive chain: {state, detail, parts} with Api.tile's state names
 * (Ok | Empty | Missing | Rejected | NotApplicable) and the ATG1 chunks as parts.
 */
export async function buildTile(chain, layer, map, x, y) {
    if (layer !== LAYER_TERRAIN && layer !== LAYER_LIQUID) {
        throw new ClientFsError('unknown-layer', `The client files do not build layer '${layer}'.`);
    }
    if (x === -1 && y === -1) {
        return result('NotApplicable', 'Terrain has no untiled content; the global tile only holds a WMO.');
    }
    if (!Number.isInteger(x) || !Number.isInteger(y) || x < 0 || x > 63 || y < 0 || y > 63) {
        return result('Rejected', `Tile (${x}, ${y}) is outside the 64x64 grid.`);
    }
    let directory;
    try {
        directory = await loadDirectory(chain);
    } catch (error) {
        if (error instanceof ClientFsError && error.code === 'bad-file') {
            return result('Rejected', error.message);
        }
        throw error;
    }
    if (directory.problem !== null) {
        return result(directory.rejected ? 'Rejected' : 'Missing', directory.problem);
    }
    const folder = directory.maps.get(map);
    if (folder === undefined) {
        return result('Missing', `Map ${map} is not in Map.dbc.`);
    }
    if (folder.length === 0 || /[\\/:]/.test(folder) || folder === '.' || folder === '..') {
        return result('Rejected', `Map directory '${folder}' is not a single folder name.`);
    }
    const path = `World\\Maps\\${folder}\\${folder}_${y}_${x}.adt`;
    const bytes = await chain.read(path);
    if (bytes === null) {
        return result('Missing', `${path} is not in the client files.`);
    }
    if (bytes.length === 0) {
        return result('Missing', `${path} is zero bytes long (a patch archive deleted it).`);
    }
    const tile = { map, x, y };
    try {
        if (layer === LAYER_TERRAIN) {
            return result('Ok', null, [buildTerrainAtg1(bytes, tile, (id) => directory.areas.get(id) ?? null)]);
        }
        const wet = buildLiquidAtg1(bytes, tile);
        return wet === null ? result('Empty', `${path} shows no liquid square.`) : result('Ok', null, [wet]);
    } catch (error) {
        if (error instanceof ClientFsError && error.code === 'bad-file') {
            return result('Rejected', `${path}: ${error.message}`);
        }
        throw error;
    }
}
