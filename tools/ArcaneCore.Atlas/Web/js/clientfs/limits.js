// Parser limits of the browser-side client-file reader (design 0.5 "Parser limits") and the typed error every parser throws.
// Every number here is a hard stop: a damaged or hostile file is refused with a ClientFsError, never allowed to hang the
// Worker or exhaust memory.

export const LIMITS = Object.freeze({
    /** Largest file a parser will materialise (`fileSize` in the block table). */
    maxFileBytes: 64 * 1024 * 1024,
    /** Largest hash or block table, in entries. */
    maxEntries: 1 << 20,
    /** `sectorSize = 512 << shift`; shift above this is refused. */
    maxSectorShift: 15,
    /** Largest archive the reader will open (bytes). Far above the 1.12.1 client's biggest archive. */
    maxArchiveBytes: 2 ** 40,
    /** Worker job timeouts: a single file, and one terrain tile. */
    fileTimeoutMs: 5000,
    tileTimeoutMs: 30000,
    /** DBC rows (WdbcFile.MaxRecords). */
    maxDbcRecords: 1 << 24,
    /** DBC cells per row (WdbcFile: fields > 4096 are refused). */
    maxDbcFields: 4096,
});

/** Codes: bad-archive | bad-file | unsupported-compression | timeout | unknown-layer. */
export class ClientFsError extends Error {
    constructor(code, message) {
        super(message);
        this.name = 'ClientFsError';
        this.code = code;
    }
}

export const badArchive = (message) => new ClientFsError('bad-archive', message);
export const badFile = (message) => new ClientFsError('bad-file', message);
