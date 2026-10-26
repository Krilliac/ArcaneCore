// PKWARE Data Compression Library "explode" for MPQ sectors: a port of tools/ArcaneCore.Atlas/Reference/Mpq/PkwareExplode.cs.
// Stream: byte 0 = literal mode (0 binary, 1 ASCII), byte 1 = dictionary size as bits (4 to 6), then an LSB-first bit stream.
// Each token starts with one flag bit: 0 = literal (8 bits), 1 = back-reference (length code, extra length bits, distance code,
// low distance bits). A back-reference of length 519 ends the stream. ASCII literal mode is refused (MPQ writers use binary).
// ref: StormLib src/pklib/explode.c:34-69 (tables), :277-389 (tokens), :486-505 (header)

import { badFile, ClientFsError } from './limits.js';

const DIST_BITS = Uint8Array.of(
    0x02, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
    0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
    0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
    0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08);
const DIST_CODE = Uint8Array.of(
    0x03, 0x0D, 0x05, 0x19, 0x09, 0x11, 0x01, 0x3E, 0x1E, 0x2E, 0x0E, 0x36, 0x16, 0x26, 0x06, 0x3A,
    0x1A, 0x2A, 0x0A, 0x32, 0x12, 0x22, 0x42, 0x02, 0x7C, 0x3C, 0x5C, 0x1C, 0x6C, 0x2C, 0x4C, 0x0C,
    0x74, 0x34, 0x54, 0x14, 0x64, 0x24, 0x44, 0x04, 0x78, 0x38, 0x58, 0x18, 0x68, 0x28, 0x48, 0x08,
    0xF0, 0x70, 0xB0, 0x30, 0xD0, 0x50, 0x90, 0x10, 0xE0, 0x60, 0xA0, 0x20, 0xC0, 0x40, 0x80, 0x00);
const EXTRA_LENGTH_BITS = Uint8Array.of(0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8);
const LENGTH_BASE = Uint16Array.of(0x0000, 0x0001, 0x0002, 0x0003, 0x0004, 0x0005, 0x0006, 0x0007, 0x0008, 0x000A, 0x000E, 0x0016, 0x0026, 0x0046, 0x0086, 0x0106);
const LENGTH_BITS = Uint8Array.of(0x03, 0x02, 0x03, 0x03, 0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05, 0x06, 0x06, 0x06, 0x07, 0x07);
const LENGTH_CODE = Uint8Array.of(0x05, 0x03, 0x01, 0x06, 0x0A, 0x02, 0x0C, 0x14, 0x04, 0x18, 0x08, 0x30, 0x10, 0x20, 0x40, 0x00);
const END_OF_STREAM = 0x205;

function lookup(startIndexes, lengthBits) {
    const positions = new Uint8Array(0x100);
    for (let symbol = 0; symbol < startIndexes.length; symbol++) {
        const step = 1 << lengthBits[symbol];
        for (let index = startIndexes[symbol]; index < 0x100; index += step) {
            positions[index] = symbol;
        }
    }
    return positions;
}

const DIST_LOOKUP = lookup(DIST_CODE, DIST_BITS);
const LENGTH_LOOKUP = lookup(LENGTH_CODE, LENGTH_BITS);

/**
 * Expands `input` into `output` (a preallocated buffer of the exact expected size) and returns the byte count.
 * Throws ClientFsError 'bad-file' for a truncated, malformed or over-long stream, 'unsupported-compression' for ASCII mode.
 */
export function explodeInto(input, output) {
    if (input.length <= 4) {
        throw badFile('PKWARE stream is too short.');
    }
    const mode = input[0];
    const dictBits = input[1];
    if (dictBits < 4 || dictBits > 6) {
        throw badFile(`PKWARE stream has an invalid dictionary size field ${dictBits} (expected 4, 5 or 6).`);
    }
    if (mode === 1) {
        throw new ClientFsError('unsupported-compression', 'PKWARE ASCII literal mode is not supported: MPQ archives are written in binary mode only.');
    }
    if (mode !== 0) {
        throw badFile(`PKWARE stream has an invalid literal mode ${mode}.`);
    }

    const length = input.length;
    const totalBits = (length - 2) * 8;
    let bit = 0;
    let overrun = false;
    const peek = (n) => {
        const at = 2 + (bit >> 3);
        const b0 = at < length ? input[at] : 0;
        const b1 = at + 1 < length ? input[at + 1] : 0;
        const b2 = at + 2 < length ? input[at + 2] : 0;
        return (((b0 | (b1 << 8) | (b2 << 16)) >>> (bit & 7)) & ((1 << n) - 1));
    };
    const skip = (n) => {
        if (bit + n > totalBits) {
            overrun = true;
            bit = totalBits;
            return;
        }
        bit += n;
    };
    const read = (n) => {
        const value = peek(n);
        skip(n);
        return value;
    };

    let written = 0;
    for (;;) {
        if (read(1) === 0) {
            const literal = read(8);
            if (overrun) {
                throw badFile('PKWARE stream ends inside a literal.');
            }
            if (written >= output.length) {
                throw badFile('PKWARE stream expands past the expected size.');
            }
            output[written++] = literal;
            continue;
        }

        const lengthSymbol = LENGTH_LOOKUP[peek(8)];
        skip(LENGTH_BITS[lengthSymbol]);
        let lengthValue = LENGTH_BASE[lengthSymbol];
        const extra = EXTRA_LENGTH_BITS[lengthSymbol];
        if (extra !== 0) {
            lengthValue += read(extra);
        }
        if (lengthValue === END_OF_STREAM) {
            return written;
        }

        const repeat = lengthValue + 2;
        const distSymbol = DIST_LOOKUP[peek(8)];
        skip(DIST_BITS[distSymbol]);
        const distance = (repeat === 2 ? ((distSymbol << 2) | read(2)) : ((distSymbol << dictBits) | read(dictBits))) + 1;
        if (overrun) {
            throw badFile('PKWARE stream ends inside a back-reference.');
        }
        if (distance > written) {
            throw badFile(`PKWARE back-reference reaches ${distance} bytes back with only ${written} written.`);
        }
        if (written + repeat > output.length) {
            throw badFile('PKWARE stream expands past the expected size.');
        }
        let from = written - distance;
        for (let i = 0; i < repeat; i++) {
            output[written++] = output[from++];
        }
    }
}

/** Expands a PKWARE stream to exactly `expectedSize` bytes. */
export function explode(streamBytes, expectedSize) {
    if (!Number.isSafeInteger(expectedSize) || expectedSize < 0) {
        throw badFile(`PKWARE expected size ${expectedSize} is not a size.`);
    }
    const output = new Uint8Array(expectedSize);
    const produced = explodeInto(streamBytes, output);
    if (produced !== expectedSize) {
        throw badFile(`imploded data expanded to ${produced} bytes, expected ${expectedSize}`);
    }
    return output;
}
