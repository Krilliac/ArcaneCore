// MPQ reader for the 1.12.1 client's archives (format versions 0 and 1), run inside a Worker. A port of the C# reference in
// tools/ArcaneCore.Atlas/Reference/Mpq (MpqArchive, MpqCrypto, MpqCompression, MpqSparse, MpqFileSystem) with the parser
// limits of design 0.5: every size read from the file is checked against the archive before anything is allocated, every
// decompressor writes into a buffer of the exact expected size, and every failure is a typed ClientFsError.
//
// Archives are Blobs (File objects) and are read by range; nothing is uploaded and nothing outside the archive is touched.
// zlib sectors go through DecompressionStream('deflate') (a zlib-wrapped deflate stream, as StormLib's Decompress_ZLIB).
// bzip2, Huffman and ADPCM are refused with 'unsupported-compression' (design: bzip2 stays refused until the census has run).

import { explodeInto } from './explode.js';
import { badArchive, badFile, ClientFsError, LIMITS } from './limits.js';

/** Counts of range reads made on archives in this realm; the Worker reports the delta of every job. */
export const stats = { reads: 0, bytes: 0 };

// ---- Flags (MpqFileFlags) ------------------------------------------------------------------------------------------
const FLAG_IMPLODE = 0x00000100;
const FLAG_COMPRESS = 0x00000200;
const FLAG_COMPRESS_MASK = 0x0000FF00;
const FLAG_ENCRYPTED = 0x00010000;
const FLAG_FIX_KEY = 0x00020000;
const FLAG_PATCH = 0x00100000;
const FLAG_SINGLE_UNIT = 0x01000000;
const FLAG_DELETE = 0x02000000;
const FLAG_EXISTS = 0x80000000;

const HASH_FREE = 0xFFFFFFFF;
const HASH_DELETED = 0xFFFFFFFE;
const BLOCK_INDEX_MASK = 0x0FFFFFFF;
const PREFERRED_LOCALE = 0x0409;
const HEADER_MAGIC = 0x1A51504D;
const USER_DATA_MAGIC = 0x1B51504D;
const HEADER_V1 = 0x20;
const HEADER_V2 = 0x2C;
const HEADER_SEARCH = 1 << 20;
const TWO32 = 4294967296;

// ---- Crypto (MpqCrypto) --------------------------------------------------------------------------------------------
const HASH_INDEX = 0x000;
const HASH_NAME_A = 0x100;
const HASH_NAME_B = 0x200;
const HASH_FILE_KEY = 0x300;
const HASH_KEY_MIX = 0x400;
const HASH_TABLE_KEY = 0xC3AF3770;
const BLOCK_TABLE_KEY = 0xEC83B3A3;

const CRYPT = (() => {
    const table = new Uint32Array(0x500);
    let seed = 0x00100001;
    for (let index1 = 0; index1 < 0x100; index1++) {
        let index2 = index1;
        for (let i = 0; i < 5; i++, index2 += 0x100) {
            seed = ((seed * 125) + 3) % 0x2AAAAB;
            const high = ((seed & 0xFFFF) << 16) >>> 0;
            seed = ((seed * 125) + 3) % 0x2AAAAB;
            const low = seed & 0xFFFF;
            table[index2] = (high | low) >>> 0;
        }
    }
    return table;
})();

function hashString(name, type) {
    let seed1 = 0x7FED7FED;
    let seed2 = 0xEEEEEEEE;
    for (let i = 0; i < name.length; i++) {
        let ch = name.charCodeAt(i);
        if (ch > 0xFF) {
            ch = 0x3F;
        }
        if (ch >= 0x61 && ch <= 0x7A) {
            ch -= 0x20;
        } else if (ch === 0x2F) {
            ch = 0x5C;
        }
        seed1 = (CRYPT[type + ch] ^ ((seed1 + seed2) >>> 0)) >>> 0;
        seed2 = (ch + seed1 + seed2 + ((seed2 << 5) >>> 0) + 3) >>> 0;
    }
    return seed1;
}

/** In place, whole little-endian words only (a trailing 1-3 bytes stay as stored). */
function decrypt(data, key) {
    const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    let key1 = key >>> 0;
    let key2 = 0xEEEEEEEE;
    const words = data.length >> 2;
    for (let i = 0; i < words; i++) {
        key2 = (key2 + CRYPT[HASH_KEY_MIX + (key1 & 0xFF)]) >>> 0;
        const plain = (view.getUint32(i * 4, true) ^ ((key1 + key2) >>> 0)) >>> 0;
        view.setUint32(i * 4, plain, true);
        key1 = (((~key1 << 0x15) + 0x11111111) | (key1 >>> 0x0B)) >>> 0;
        key2 = (plain + key2 + ((key2 << 5) >>> 0) + 3) >>> 0;
    }
}

function fileKey(path, blockOffset, fileSize, fixKey) {
    const cut = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
    const plain = cut >= 0 ? path.slice(cut + 1) : path;
    const key = hashString(plain, HASH_FILE_KEY);
    return fixKey ? (((key + (blockOffset % TWO32)) >>> 0) ^ fileSize) >>> 0 : key;
}

const normalize = (path) => path.replaceAll('/', '\\').replace(/^\\+/, '');

function hashOf(path) {
    return { index: hashString(path, HASH_INDEX), a: hashString(path, HASH_NAME_A), b: hashString(path, HASH_NAME_B) };
}

// ---- Range reads ---------------------------------------------------------------------------------------------------
async function readRange(blob, start, length, what) {
    if (length === 0) {
        return new Uint8Array(0);
    }
    stats.reads++;
    stats.bytes += length;
    let buffer;
    try {
        buffer = await blob.slice(start, start + length).arrayBuffer();
    } catch (error) {
        throw badArchive(`${what}: cannot read ${length} bytes at 0x${start.toString(16)} (${error?.message ?? error}).`);
    }
    if (buffer.byteLength !== length) {
        throw badArchive(`${what}: unexpected end of file reading ${length} bytes at 0x${start.toString(16)}.`);
    }
    return new Uint8Array(buffer);
}

// ---- Decompression -------------------------------------------------------------------------------------------------
/** Inflates a zlib stream into `output`, cancelling the stream as soon as it would write past the expected size. */
async function inflate(input, output) {
    const reader = new Blob([input]).stream().pipeThrough(new DecompressionStream('deflate')).getReader();
    let total = 0;
    try {
        for (;;) {
            let step;
            try {
                step = await reader.read();
            } catch {
                throw badFile('zlib sector is corrupt or truncated.');
            }
            if (step.done) {
                return total;
            }
            if (total + step.value.length > output.length) {
                throw badFile('zlib sector expands past the expected size.');
            }
            output.set(step.value, total);
            total += step.value.length;
        }
    } finally {
        await reader.cancel().catch(() => {});
    }
}

/** MPQ "sparse" (mask 0x20): big-endian length, then literal runs (high bit) and zero runs. ref: Reference/Mpq/MpqSparse.cs */
function sparseInto(input, output) {
    if (input.length < 5) {
        throw badFile('Sparse stream is shorter than its 5-byte minimum.');
    }
    const declared = ((input[0] << 24) | (input[1] << 16) | (input[2] << 8) | input[3]) >>> 0;
    if (declared !== output.length) {
        throw badFile(`Sparse stream declares ${declared} bytes, expected ${output.length}.`);
    }
    let read = 4;
    let written = 0;
    while (read < input.length) {
        const op = input[read++];
        if ((op & 0x80) !== 0) {
            const count = (op & 0x7F) + 1;
            if (read + count > input.length) {
                throw badFile('Sparse stream ends inside a literal run.');
            }
            if (written + count > output.length) {
                throw badFile('Sparse literal run passes the declared length.');
            }
            output.set(input.subarray(read, read + count), written);
            read += count;
            written += count;
        } else {
            const count = Math.min((op & 0x7F) + 3, output.length - written);
            output.fill(0, written, written + count);
            written += count;
        }
    }
    if (written !== output.length) {
        throw badFile(`Sparse stream produced ${written} of ${output.length} bytes.`);
    }
    return written;
}

const hexMask = (mask) => `0x${mask.toString(16).padStart(2, '0').toUpperCase()}`;

const withLabel = (error, label) => (error instanceof ClientFsError ? new ClientFsError(error.code, `${label}: ${error.message}`) : error);

/** Expands one sector or single unit into `output` (the expected size). `stored` of the same size was never compressed. */
async function decompressUnit(flags, stored, output, label) {
    if (stored.length === output.length) {
        output.set(stored);
        return;
    }
    if ((flags & FLAG_COMPRESS) !== 0) {
        if (stored.length < 2 || stored.length > output.length) {
            throw badFile(`${label}: compressed sector of ${stored.length} bytes cannot expand to ${output.length} bytes.`);
        }
        const mask = stored[0];
        if ((mask & 0x04) !== 0 || mask === 0) {
            throw badFile(`${label}: compression mask ${hexMask(mask)} contains no known method or an unknown bit.`);
        }
        if ((mask & 0x10) !== 0) {
            throw new ClientFsError('unsupported-compression', `${label}: bzip2 compression (mask ${hexMask(mask)}) is not supported.`);
        }
        if ((mask & 0xC1) !== 0) {
            throw new ClientFsError('unsupported-compression', `${label}: Huffman/ADPCM compression (mask ${hexMask(mask)}) is not supported.`);
        }
        // Undo order of MpqCompression: PKWARE, then zlib, then sparse; each stage feeds the next.
        const stages = [];
        if ((mask & 0x08) !== 0) {
            stages.push('pkware');
        }
        if ((mask & 0x02) !== 0) {
            stages.push('zlib');
        }
        if ((mask & 0x20) !== 0) {
            stages.push('sparse');
        }
        let current = stored.subarray(1);
        for (let i = 0; i < stages.length; i++) {
            const last = i === stages.length - 1;
            const target = last ? output : new Uint8Array(output.length);
            let produced;
            try {
                produced = stages[i] === 'pkware' ? explodeInto(current, target)
                    : stages[i] === 'zlib' ? await inflate(current, target)
                        : sparseInto(current, target);
            } catch (error) {
                throw withLabel(error, label);
            }
            if (last && produced !== output.length) {
                throw badFile(`${label}: ${hexMask(mask)} sector expanded to ${produced} bytes, expected ${output.length}.`);
            }
            current = target.subarray(0, produced);
        }
        return;
    }
    if ((flags & FLAG_IMPLODE) !== 0) {
        let produced;
        try {
            produced = explodeInto(stored, output);
        } catch (error) {
            throw withLabel(error, label);
        }
        if (produced !== output.length) {
            throw badFile(`${label}: imploded data expanded to ${produced} bytes, expected ${output.length}`);
        }
        return;
    }
    throw new ClientFsError('unsupported-compression', `${label}: compression flags 0x${(flags & FLAG_COMPRESS_MASK).toString(16)} name neither implode nor multi-method compression.`);
}

// ---- Header --------------------------------------------------------------------------------------------------------
const u32At = (buffer, at) => (buffer.length - at >= 4 ? new DataView(buffer.buffer, buffer.byteOffset + at, 4).getUint32(0, true) : 0);
const isHeaderAt = (buffer, at) => u32At(buffer, at) === HEADER_MAGIC && u32At(buffer, at + 4) >= HEADER_V1;

function parseHeader(buffer, at, position, size, name) {
    const available = buffer.length - at;
    if (available < HEADER_V1) {
        throw badArchive(`${name}: the header is cut short.`);
    }
    const dv = new DataView(buffer.buffer, buffer.byteOffset + at, available);
    const headerSize = dv.getUint32(4, true);
    const archiveSize = dv.getUint32(8, true);
    const version = dv.getUint16(12, true);
    let shift = dv.getUint16(14, true);
    let hashOffset = dv.getUint32(16, true);
    let blockOffset = dv.getUint32(20, true);
    const hashCount = dv.getUint32(24, true);
    const blockCount = dv.getUint32(28, true);
    let hiOffset = 0;
    if (version === 0) {
        shift &= 0xFF;
    } else if (version === 1) {
        if (headerSize < HEADER_V2 || available < HEADER_V2) {
            throw badArchive(`${name}: format version 1 needs a ${HEADER_V2}-byte header, found ${headerSize}.`);
        }
        hiOffset = dv.getUint32(32, true) + (dv.getUint32(36, true) * TWO32);
        hashOffset += dv.getUint16(40, true) * TWO32;
        blockOffset += dv.getUint16(42, true) * TWO32;
    } else {
        throw badArchive(`${name}: MPQ format version ${version} is not used by the 1.12.1 client and is not supported.`);
    }
    if (shift > LIMITS.maxSectorShift) {
        throw badArchive(`${name}: sector size exponent ${shift} is out of range.`);
    }
    if (hashCount === 0 || (hashCount & (hashCount - 1)) !== 0 || hashCount > LIMITS.maxEntries) {
        throw badArchive(`${name}: hash table size ${hashCount} is not a power of two up to ${LIMITS.maxEntries}.`);
    }
    if (blockCount > LIMITS.maxEntries) {
        throw badArchive(`${name}: block table size ${blockCount} is out of range.`);
    }
    const hashEnd = position + hashOffset + (hashCount * 16);
    const blockEnd = position + blockOffset + (blockCount * 16);
    if (hashEnd > size || (blockCount > 0 && blockEnd > size) || position + blockOffset > size) {
        throw badArchive(`${name}: a table extends past the end of the file.`);
    }
    if (archiveSize !== 0) {
        const declaredEnd = position + archiveSize;
        if (declaredEnd > size) {
            throw badArchive(`${name}: the header declares ${archiveSize} bytes but the file has only ${size - position}.`);
        }
        if (hashEnd > declaredEnd || (blockCount > 0 && blockEnd > declaredEnd)) {
            throw badArchive(`${name}: a table lies outside the declared archive size ${archiveSize}.`);
        }
    }
    return { position, headerSize, archiveSize, version, shift, hashOffset, blockOffset, hashCount, blockCount, hiOffset };
}

async function findHeader(blob, size, name) {
    if (size < HEADER_V1) {
        throw badArchive(`${name}: ${size} bytes is too small for an MPQ header.`);
    }
    const first = await readRange(blob, 0, Math.min(size, HEADER_V2), name);
    if (isHeaderAt(first, 0)) {
        return parseHeader(first, 0, 0, size, name);
    }
    // The header is searched on 0x200 boundaries in the first MiB; user data ('MPQ\x1B') may point at it.
    const limit = Math.min(size, HEADER_SEARCH);
    const window = await readRange(blob, 0, Math.min(size, limit + HEADER_V2), name);
    for (let position = 0; position + HEADER_V1 <= size && position < limit; position += 0x200) {
        if (u32At(window, position) === USER_DATA_MAGIC && window.length - position >= 16) {
            const dataSize = u32At(window, position + 4);
            const headerOffset = u32At(window, position + 8);
            const userHeaderSize = u32At(window, position + 12);
            if (userHeaderSize <= dataSize && dataSize <= headerOffset && position + headerOffset + HEADER_V1 < size) {
                const headerPosition = position + headerOffset;
                const inner = await readRange(blob, headerPosition, Math.min(HEADER_V2, size - headerPosition), name);
                if (isHeaderAt(inner, 0)) {
                    return parseHeader(inner, 0, headerPosition, size, name);
                }
                continue;
            }
        }
        if (isHeaderAt(window, position)) {
            return parseHeader(window, position, position, size, name);
        }
    }
    throw badArchive(`${name}: no MPQ header found in the first ${limit} bytes.`);
}

function countSector(flags, bytes, bump) {
    if ((flags & FLAG_COMPRESS) !== 0) {
        bump(hexMask(bytes[0]));
    } else if ((flags & FLAG_IMPLODE) !== 0) {
        bump('implode');
    }
}

// ---- Archive -------------------------------------------------------------------------------------------------------
/**
 * Opens one archive: header, hash table and block table are read and validated; file data is read on demand.
 * @param {Blob} blob the archive file
 * @param {{name?: string}} [options]
 */
export async function openArchive(blob, { name = '' } = {}) {
    const size = blob.size;
    if (size > LIMITS.maxArchiveBytes) {
        throw badArchive(`${name}: ${size} bytes is larger than the reader accepts.`);
    }
    const header = await findHeader(blob, size, name);
    const { position, hashCount, blockCount } = header;
    const sectorSize = 0x200 << header.shift;
    // Version 0 offsets are 32-bit and wrap (StormLib); version 1 offsets are 48-bit.
    const absolute = (offset) => (header.version === 0 ? (position + offset) % TWO32 : position + offset);

    const hashRaw = await readRange(blob, absolute(header.hashOffset), hashCount * 16, name);
    decrypt(hashRaw, HASH_TABLE_KEY);
    const hv = new DataView(hashRaw.buffer, hashRaw.byteOffset, hashRaw.byteLength);
    const hNameA = new Uint32Array(hashCount);
    const hNameB = new Uint32Array(hashCount);
    const hLocale = new Uint16Array(hashCount);
    const hPlatform = new Uint8Array(hashCount);
    const hBlock = new Uint32Array(hashCount);
    for (let i = 0; i < hashCount; i++) {
        const o = i * 16;
        hNameA[i] = hv.getUint32(o, true);
        hNameB[i] = hv.getUint32(o + 4, true);
        hLocale[i] = hv.getUint16(o + 8, true);
        hPlatform[i] = hashRaw[o + 10];
        hBlock[i] = hv.getUint32(o + 12, true);
    }

    const bOffset = new Float64Array(blockCount);
    const bCompressed = new Uint32Array(blockCount);
    const bSize = new Uint32Array(blockCount);
    const bFlags = new Uint32Array(blockCount);
    if (blockCount > 0) {
        const blockRaw = await readRange(blob, absolute(header.blockOffset), blockCount * 16, name);
        decrypt(blockRaw, BLOCK_TABLE_KEY);
        const bv = new DataView(blockRaw.buffer, blockRaw.byteOffset, blockRaw.byteLength);
        let hi = null;
        if (header.hiOffset !== 0) {
            if (position + header.hiOffset + (blockCount * 2) > size) {
                throw badArchive(`${name}: the hi-block table extends past the end of the file.`);
            }
            const hiRaw = await readRange(blob, absolute(header.hiOffset), blockCount * 2, name);
            hi = new DataView(hiRaw.buffer, hiRaw.byteOffset, hiRaw.byteLength);
        }
        for (let i = 0; i < blockCount; i++) {
            const o = i * 16;
            bOffset[i] = bv.getUint32(o, true) + (hi ? hi.getUint16(i * 2, true) * TWO32 : 0);
            bCompressed[i] = bv.getUint32(o + 4, true);
            bSize[i] = bv.getUint32(o + 8, true);
            bFlags[i] = bv.getUint32(o + 12, true);
        }
    }

    const isValidBlock = (blockIndex) => {
        if (blockIndex >= HASH_DELETED) {
            return false;
        }
        const index = blockIndex & BLOCK_INDEX_MASK;
        return index < blockCount && (bFlags[index] & FLAG_EXISTS) !== 0;
    };

    /** StormLib's chain walk with the preferred locale enUS (MpqArchive.TryFind). Returns the block index or -1. */
    const lookup = (hash) => {
        const wrap = hashCount - 1;
        const start = hash.index & wrap;
        let found = -1;
        let index = start;
        do {
            if (hNameA[index] === hash.a && hNameB[index] === hash.b && isValidBlock(hBlock[index])) {
                const exact = hLocale[index] === PREFERRED_LOCALE && hPlatform[index] === 0;
                if (exact || ((hLocale[index] === 0 || hLocale[index] === PREFERRED_LOCALE) && hPlatform[index] === 0)) {
                    found = hBlock[index] & BLOCK_INDEX_MASK;
                    if (exact) {
                        return found;
                    }
                }
            } else if (hBlock[index] === HASH_FREE) {
                break;
            }
            index = (index + 1) & wrap;
        } while (index !== start);
        return found;
    };

    async function readBlock(block, path) {
        const flags = bFlags[block];
        const fileSize = bSize[block];
        const csize = bCompressed[block];
        const offset = bOffset[block];
        const tag = `${name}: '${path}'`;
        if ((flags & FLAG_PATCH) !== 0) {
            throw badFile(`${tag} is an incremental patch file, which the 1.12.1 client does not use.`);
        }
        if (fileSize === 0) {
            return new Uint8Array(0);
        }
        if (fileSize > LIMITS.maxFileBytes) {
            throw badFile(`${tag} claims ${fileSize} bytes (limit ${LIMITS.maxFileBytes}).`);
        }
        if (csize > LIMITS.maxFileBytes) {
            throw badFile(`${tag} stores ${csize} bytes (limit ${LIMITS.maxFileBytes}).`);
        }
        const start = absolute(offset);
        if (start + csize > size) {
            throw badFile(`${tag}: ${csize} bytes at 0x${start.toString(16)} lie outside the ${size}-byte archive.`);
        }
        const encrypted = (flags & FLAG_ENCRYPTED) !== 0;
        const key = encrypted ? fileKey(path, offset, fileSize, (flags & FLAG_FIX_KEY) !== 0) : 0;
        const result = new Uint8Array(fileSize);

        if ((flags & FLAG_SINGLE_UNIT) !== 0) {
            const compressed = (flags & FLAG_COMPRESS_MASK) !== 0;
            if (!compressed && csize < fileSize) {
                throw badFile(`${tag} stores ${csize} bytes for a ${fileSize}-byte file.`);
            }
            const stored = await readRange(blob, start, compressed ? csize : fileSize, name);
            if (encrypted) {
                decrypt(stored, key);
            }
            if (compressed) {
                await decompressUnit(flags, stored, result, tag);
            } else {
                result.set(stored);
            }
            return result;
        }

        if ((flags & FLAG_COMPRESS_MASK) === 0) {
            if (fileSize > csize) {
                throw badFile(`${tag} stores ${csize} bytes for a ${fileSize}-byte file.`);
            }
            const raw = await readRange(blob, start, fileSize, name);
            if (encrypted) {
                for (let index = 0, at = 0; at < raw.length; index++, at += sectorSize) {
                    decrypt(raw.subarray(at, Math.min(at + sectorSize, raw.length)), (key + index) >>> 0);
                }
            }
            result.set(raw);
            return result;
        }

        const sectorCount = Math.ceil(fileSize / sectorSize);
        const tableBytes = (sectorCount + 1) * 4;
        if (tableBytes > csize) {
            throw badFile(`${tag}: the sector offset table (${tableBytes} bytes) does not fit the ${csize}-byte block.`);
        }
        const tableRaw = await readRange(blob, start, tableBytes, name);
        if (encrypted) {
            decrypt(tableRaw, (key - 1) >>> 0);
        }
        const tv = new DataView(tableRaw.buffer, tableRaw.byteOffset, tableRaw.byteLength);
        const offsets = new Uint32Array(sectorCount + 1);
        for (let i = 0; i <= sectorCount; i++) {
            offsets[i] = tv.getUint32(i * 4, true);
        }
        if (offsets[0] < tableBytes) {
            throw badFile(`${tag}: the sector table is corrupt (first sector at ${offsets[0]} inside the ${tableBytes}-byte table).`);
        }
        for (let i = 0; i < sectorCount; i++) {
            if (offsets[i + 1] < offsets[i] || offsets[i + 1] - offsets[i] > sectorSize) {
                throw badFile(`${tag}: the sector table is corrupt at sector ${i}${encrypted ? ' (wrong file key?)' : ''}.`);
            }
        }
        const total = offsets[sectorCount];
        if (total > csize) {
            throw badFile(`${tag}: sectors end at ${total}, past the stored size ${csize}.`);
        }
        const stored = await readRange(blob, start, total, name);
        for (let i = 0; i < sectorCount; i++) {
            const from = offsets[i];
            const rawLength = offsets[i + 1] - from;
            const outputStart = i * sectorSize;
            const expected = Math.min(sectorSize, fileSize - outputStart);
            const sector = stored.subarray(from, from + rawLength);
            if (encrypted) {
                decrypt(sector, (key + i) >>> 0);
            }
            if (rawLength === 0) {
                continue;
            }
            if (rawLength > expected) {
                throw badFile(`${tag}: sector ${i} stores ${rawLength} bytes for ${expected}.`);
            }
            await decompressUnit(flags, sector, result.subarray(outputStart, outputStart + expected), `${tag} sector ${i}`);
        }
        return result;
    }

    /** Counts the compression-method masks of every compressed sector and unit (design 0.5 census). */
    async function census() {
        const counts = {};
        const bump = (key) => {
            counts[key] = (counts[key] ?? 0) + 1;
        };
        for (let block = 0; block < blockCount; block++) {
            const flags = bFlags[block];
            if ((flags & FLAG_EXISTS) === 0 || (flags & (FLAG_DELETE | FLAG_PATCH)) !== 0 || bSize[block] === 0 || (flags & FLAG_COMPRESS_MASK) === 0) {
                continue;
            }
            if ((flags & FLAG_ENCRYPTED) !== 0) {
                bump('encrypted');
                continue;
            }
            const fileSize = bSize[block];
            const csize = bCompressed[block];
            const start = absolute(bOffset[block]);
            if (fileSize > LIMITS.maxFileBytes || csize > LIMITS.maxFileBytes || start + csize > size) {
                bump('damaged');
                continue;
            }
            if ((flags & FLAG_SINGLE_UNIT) !== 0) {
                if (csize > 0 && csize < fileSize) {
                    countSector(flags, await readRange(blob, start, 1, name), bump);
                }
                continue;
            }
            const sectorCount = Math.ceil(fileSize / sectorSize);
            if ((sectorCount + 1) * 4 > csize) {
                bump('damaged');
                continue;
            }
            const raw = await readRange(blob, start, csize, name);
            const dv = new DataView(raw.buffer, raw.byteOffset, raw.byteLength);
            for (let i = 0; i < sectorCount; i++) {
                const from = dv.getUint32(i * 4, true);
                const to = dv.getUint32((i + 1) * 4, true);
                const expected = Math.min(sectorSize, fileSize - (i * sectorSize));
                if (to < from || to > raw.length) {
                    bump('damaged');
                    break;
                }
                if (to > from && to - from < expected) {
                    countSector(flags, raw.subarray(from, to), bump);
                }
            }
        }
        return counts;
    }

    return {
        name,
        size,
        sectorSize,
        header,
        has: (path) => {
            const block = lookup(hashOf(normalize(path)));
            return block >= 0 && (bFlags[block] & FLAG_DELETE) === 0;
        },
        lookup,
        isDeleted: (block) => (bFlags[block] & FLAG_DELETE) !== 0,
        readBlock,
        census,
        async read(path) {
            const normalized = normalize(path);
            const block = lookup(hashOf(normalized));
            if (block < 0 || (bFlags[block] & FLAG_DELETE) !== 0) {
                return null;
            }
            return readBlock(block, normalized);
        },
    };
}

/** Compression-mask counts of an archive: {'0x02': n, ...}; see design 0.5 (the bzip2 census). */
export function censusMasks(archive) {
    return archive.census();
}

// ---- Load order and the patch chain (MpqFileSystem) ----------------------------------------------------------------
const BASE_CHAIN = ['base.mpq', 'dbc.mpq', 'interface.mpq', 'misc.mpq', 'model.mpq', 'sound.mpq', 'speech.mpq', 'terrain.mpq', 'texture.mpq', 'wmo.mpq'];

function patchLevel(fileName) {
    const lower = fileName.toLowerCase();
    if (lower === 'patch.mpq') {
        return 1;
    }
    const match = /^patch-([2-9]|[1-9][0-9])\.mpq$/.exec(lower);
    return match ? Number(match[1]) : null;
}

const baseName = (full) => full.slice(Math.max(full.lastIndexOf('/'), full.lastIndexOf('\\')) + 1);

/** Archive names in mount order, lowest priority first; backup.MPQ is never mounted. ref: MpqFileSystem.LoadOrder */
export function loadOrder(names) {
    const ranked = [];
    for (const full of names) {
        const name = baseName(full);
        const lower = name.toLowerCase();
        if (lower === 'backup.mpq') {
            continue;
        }
        const chain = BASE_CHAIN.indexOf(lower);
        const level = chain >= 0 ? null : patchLevel(name);
        const cls = chain >= 0 ? 0 : level === null ? 1 : 2;
        ranked.push({ cls, rank: chain >= 0 ? chain : level ?? 0, full, key: name.toUpperCase() });
    }
    ranked.sort((a, b) => a.cls - b.cls || a.rank - b.rank || (a.key < b.key ? -1 : a.key > b.key ? 1 : 0));
    return ranked.map((r) => r.full);
}

/** The mounted archives (already in load order) as one read-only tree: the highest-priority archive that has a name decides; a deletion marker hides it. */
export function createChain(archives) {
    const resolve = (path) => {
        const normalized = normalize(path);
        if (normalized.length === 0) {
            return null;
        }
        const hash = hashOf(normalized);
        for (let i = archives.length - 1; i >= 0; i--) {
            const block = archives[i].lookup(hash);
            if (block >= 0) {
                return { archive: archives[i], block, deleted: archives[i].isDeleted(block), normalized };
            }
        }
        return null;
    };
    return {
        order: archives.map((a) => a.name),
        archives,
        has: (path) => {
            const r = resolve(path);
            return r !== null && !r.deleted;
        },
        archiveOf: (path) => {
            const r = resolve(path);
            return r !== null && !r.deleted ? r.archive.name : null;
        },
        async read(path) {
            const r = resolve(path);
            return r === null || r.deleted ? null : r.archive.readBlock(r.block, r.normalized);
        },
    };
}

/** @param {Array<{name: string, blob: Blob}>} entries archives in any order; they are mounted in load order */
export async function openChain(entries) {
    const byName = new Map(entries.map((e) => [e.name, e]));
    const archives = [];
    for (const entryName of loadOrder(entries.map((e) => e.name))) {
        const entry = byName.get(entryName);
        archives.push(await openArchive(entry.blob, { name: entry.name }));
    }
    return createChain(archives);
}
