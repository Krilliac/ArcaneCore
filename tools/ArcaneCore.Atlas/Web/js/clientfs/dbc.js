// A client DBC table in the 1.12.1 "WDBC" layout (port of Reference/Dbc/WdbcFile.cs): a 20-byte header (magic, record count,
// field count, record size, string block size), fixed-size records of 4-byte cells, then a string block. Every size is
// checked against the file length before anything is read, so a damaged header cannot cause an out-of-range read.

import { badFile, LIMITS } from './limits.js';

export const DBC_MAGIC = 0x43424457;
export const DBC_HEADER_BYTES = 20;
const LOCALE_SLOTS = 8;
const UTF8 = new TextDecoder('utf-8');

/**
 * @param {Uint8Array} bytes the whole file
 * @returns {{recordCount: number, fieldCount: number, recordSize: number, stringBlockSize: number,
 *   cell: (r: number, f: number) => number, string: (offset: number) => string, localized: (r: number, first: number) => string}}
 */
export function parseDbc(bytes) {
    if (!(bytes instanceof Uint8Array) || bytes.length < DBC_HEADER_BYTES) {
        throw badFile('Not a WDBC file.');
    }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    if (view.getUint32(0, true) !== DBC_MAGIC) {
        throw badFile('Not a WDBC file.');
    }
    const records = view.getUint32(4, true);
    const fields = view.getUint32(8, true);
    const recordSize = view.getUint32(12, true);
    const strings = view.getUint32(16, true);
    if (fields === 0 || fields > LIMITS.maxDbcFields || recordSize !== fields * 4) {
        throw badFile(`Unsupported DBC layout: ${fields} fields in ${recordSize}-byte records.`);
    }
    // Numbers are exact up to 2^53; records <= 2^24 and recordSize <= 2^14, so the product cannot lose precision.
    if (records > LIMITS.maxDbcRecords || DBC_HEADER_BYTES + records * recordSize + strings !== bytes.length) {
        throw badFile(`DBC size does not match its header (${records} records of ${recordSize} bytes and ${strings} string bytes in ${bytes.length} bytes).`);
    }
    const stringsOffset = DBC_HEADER_BYTES + records * recordSize;

    const cell = (record, field) => {
        if (!Number.isInteger(record) || !Number.isInteger(field) || record < 0 || record >= records || field < 0 || field >= fields) {
            throw badFile(`DBC cell (${record}, ${field}) is outside ${records} x ${fields}.`);
        }
        return view.getUint32(DBC_HEADER_BYTES + record * recordSize + field * 4, true);
    };

    const string = (offset) => {
        if (offset === 0) {
            return '';
        }
        if (!Number.isInteger(offset) || offset < 0 || offset >= strings) {
            throw badFile(`DBC string offset ${offset} is outside the ${strings}-byte string block.`);
        }
        const start = stringsOffset + offset;
        const end = stringsOffset + strings;
        let stop = start;
        while (stop < end && bytes[stop] !== 0) {
            stop++;
        }
        if (stop === end) {
            throw badFile(`DBC string at offset ${offset} is not terminated.`);
        }
        return UTF8.decode(bytes.subarray(start, stop));
    };

    const localized = (record, first) => {
        if (!Number.isInteger(first) || first < 0 || first + LOCALE_SLOTS >= fields) {
            throw badFile('A localized string needs 9 cells.');
        }
        for (let slot = 0; slot < LOCALE_SLOTS; slot++) {
            const text = string(cell(record, first + slot));
            if (text.length > 0) {
                return text;
            }
        }
        return '';
    };

    return { recordCount: records, fieldCount: fields, recordSize, stringBlockSize: strings, cell, string, localized };
}
