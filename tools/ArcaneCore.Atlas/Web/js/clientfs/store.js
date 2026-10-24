// Caches and persistence of the client-file reader (design 0.5):
//   - cacheKey / LruCache: the in-memory LRU of parsed results (512 MB) and the key every cache entry is stored under;
//   - OpfsCache: parsed outputs in the browser's origin-private file system, under a byte budget (default 2 GiB capped by
//     navigator.storage.estimate()), least recently written first out;
//   - the IndexedDB database `atlas-client` (store `handles`: the directory handle; store `meta`: the choice, the validated
//     folder summary and the cache budget).
// Nothing here leaves the browser: OPFS and IndexedDB are private to this origin and no function in this module does I/O to
// the network.

export const LRU_DEFAULT_BYTES = 512 * 1024 * 1024;
const GIB = 1024 ** 3;
export const DEFAULT_BUDGET_BYTES = 2 * GIB;

/** Logical paths are case- and separator-insensitive; an entry is tied to the archive it came from by name, size and time. */
export function cacheKey(path, archive) {
    const logical = String(path).replaceAll('\\', '/').replace(/^\/+/, '').toLowerCase();
    return `${logical}|${archive.name}|${archive.size}|${archive.lastModified}`;
}

/** One key for a result that depends on a whole set of archives (a tile is built from the dbc, terrain and patch archives). */
export function chainKey(path, archives) {
    return archives.map((a) => cacheKey(path, a)).join('&');
}

export class LruCache {
    #maxBytes;
    #entries = new Map();
    #bytes = 0;

    constructor(maxBytes = LRU_DEFAULT_BYTES) {
        this.#maxBytes = maxBytes;
    }

    get bytes() {
        return this.#bytes;
    }

    get(key) {
        const entry = this.#entries.get(key);
        if (entry === undefined) {
            return undefined;
        }
        this.#entries.delete(key);
        this.#entries.set(key, entry);
        return entry.value;
    }

    set(key, value, bytes) {
        const old = this.#entries.get(key);
        if (old !== undefined) {
            this.#bytes -= old.bytes;
            this.#entries.delete(key);
        }
        this.#entries.set(key, { value, bytes });
        this.#bytes += bytes;
        while (this.#bytes > this.#maxBytes && this.#entries.size > 1) {
            const [oldest, entry] = this.#entries.entries().next().value;
            this.#entries.delete(oldest);
            this.#bytes -= entry.bytes;
        }
    }

    clear() {
        this.#entries.clear();
        this.#bytes = 0;
    }
}

/** The OPFS budget: 2 GiB, never more than the browser says it will let the origin store. */
export function defaultCacheBudget({ quota } = {}) {
    return Number.isFinite(quota) ? Math.max(0, Math.min(DEFAULT_BUDGET_BYTES, quota)) : DEFAULT_BUDGET_BYTES;
}

/** SHA-256 hex of a string (file names in OPFS), with a plain 64-bit mix where SubtleCrypto is unavailable (insecure context). */
export async function digestName(text) {
    const bytes = new TextEncoder().encode(text);
    if (globalThis.crypto?.subtle) {
        const hash = new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', bytes));
        return Array.from(hash, (b) => b.toString(16).padStart(2, '0')).join('');
    }
    let h1 = 0xdeadbeef;
    let h2 = 0x41c6ce57;
    for (const b of bytes) {
        h1 = Math.imul(h1 ^ b, 2654435761);
        h2 = Math.imul(h2 ^ b, 1597334677);
    }
    h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507) ^ Math.imul(h2 ^ (h2 >>> 13), 3266489909);
    h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507) ^ Math.imul(h1 ^ (h1 >>> 13), 3266489909);
    return ((h2 >>> 0).toString(16).padStart(8, '0') + (h1 >>> 0).toString(16).padStart(8, '0')) + bytes.length.toString(16).padStart(8, '0');
}

// ---- OPFS ----------------------------------------------------------------------------------------------------------
export const CACHE_DIRECTORY = 'atlas-client-cache';

export const opfsAvailable = () => typeof navigator !== 'undefined' && typeof navigator.storage?.getDirectory === 'function';

/**
 * Parsed results as files `<sha256(key)>.bin` in one OPFS directory, created on the first write (so a folder that failed
 * validation leaves nothing behind). `budget` is read through a function so the settings slider takes effect at once.
 */
export class OpfsCache {
    #budget;
    #bytes = 0;
    #index = null; // file name -> size, in write order (oldest first); null until the directory has been scanned once
    #queue = Promise.resolve(); // every mutation runs through this chain, one at a time

    constructor(budget) {
        this.#budget = budget;
    }

    /** Runs `task` after every earlier mutation has settled; a failed task does not poison the chain. */
    #serial(task) {
        const run = this.#queue.then(task);
        this.#queue = run.then(() => undefined, () => undefined);
        return run;
    }

    async #directory(create) {
        if (!opfsAvailable()) {
            return null;
        }
        try {
            const root = await navigator.storage.getDirectory();
            return await root.getDirectoryHandle(CACHE_DIRECTORY, { create });
        } catch (error) {
            if (error?.name === 'NotFoundError') {
                return null;
            }
            throw error;
        }
    }

    /** Scans the directory once into the write-ordered index; later writes and evictions keep it current without listing. */
    async #load() {
        if (this.#index !== null) {
            return;
        }
        const found = [];
        const directory = await this.#directory(false);
        if (directory !== null) {
            for await (const [name, handle] of directory.entries()) {
                if (handle.kind === 'file') {
                    const file = await handle.getFile();
                    found.push({ name, size: file.size, time: file.lastModified });
                }
            }
        }
        found.sort((x, y) => x.time - y.time);
        this.#index = new Map(found.map((f) => [f.name, f.size]));
        this.#bytes = found.reduce((n, f) => n + f.size, 0);
    }

    /** Total bytes stored (scanned once, then tracked). */
    size() {
        return this.#serial(async () => {
            await this.#load();
            return this.#bytes;
        });
    }

    /** @returns {Promise<Uint8Array|null>} the stored bytes, or null on a miss */
    async get(key) {
        const directory = await this.#directory(false);
        if (directory === null) {
            return null;
        }
        try {
            const handle = await directory.getFileHandle(`${await digestName(key)}.bin`);
            return new Uint8Array(await (await handle.getFile()).arrayBuffer());
        } catch (error) {
            if (error?.name === 'NotFoundError') {
                return null;
            }
            throw error;
        }
    }

    /**
     * Stores `bytes` under `key` unless it alone exceeds the budget; evicts the oldest entries beyond the budget. Writes, evictions
     * and clear() are serialised. `isCurrent` is asked before the write and again after its last await: when it answers false
     * (the folder was forgotten meanwhile) nothing stays behind and false is returned.
     */
    set(key, bytes, isCurrent = () => true) {
        return this.#serial(async () => {
            const budget = this.#budget();
            if (!opfsAvailable() || bytes.byteLength > budget || !isCurrent()) {
                return false;
            }
            await this.#load();
            const directory = await this.#directory(true);
            const name = `${await digestName(key)}.bin`;
            const handle = await directory.getFileHandle(name, { create: true });
            const writable = await handle.createWritable();
            try {
                await writable.write(bytes);
                await writable.close();
            } catch (error) {
                await writable.abort().catch(() => {});
                throw error;
            }
            this.#bytes += bytes.byteLength - (this.#index.get(name) ?? 0);
            this.#index.delete(name); // re-inserted last: the index stays ordered by write time
            this.#index.set(name, bytes.byteLength);
            if (!isCurrent()) {
                await this.#remove(directory, name);
                return false;
            }
            await this.#enforce(budget, name, directory);
            return true;
        });
    }

    async #remove(directory, name) {
        const size = this.#index.get(name) ?? 0;
        try {
            await directory.removeEntry(name);
        } catch (error) {
            if (error?.name !== 'NotFoundError') {
                throw error;
            }
        }
        this.#index.delete(name);
        this.#bytes -= size;
    }

    async #enforce(budget, keep, directory) {
        if (this.#bytes <= budget) {
            return;
        }
        directory ??= await this.#directory(false);
        if (directory === null) {
            return;
        }
        for (const name of [...this.#index.keys()]) {
            if (this.#bytes <= budget) {
                break;
            }
            if (name !== keep) {
                await this.#remove(directory, name);
            }
        }
    }

    /** Deletes the least recently written entries until the cache fits `budget`. */
    enforce(budget = this.#budget(), keep = null) {
        return this.#serial(async () => {
            await this.#load();
            await this.#enforce(budget, keep, null);
        });
    }

    /** Removes the directory and everything in it (after the writes already queued). */
    clear() {
        return this.#serial(async () => {
            if (opfsAvailable()) {
                const root = await navigator.storage.getDirectory();
                try {
                    await root.removeEntry(CACHE_DIRECTORY, { recursive: true });
                } catch (error) {
                    if (error?.name !== 'NotFoundError') {
                        throw error;
                    }
                }
            }
            this.#index = new Map();
            this.#bytes = 0;
        });
    }
}

// ---- IndexedDB -----------------------------------------------------------------------------------------------------
const DB_NAME = 'atlas-client';
const DB_VERSION = 1;
export const HANDLE_KEY = 'wow';
export const META_KEY = 'client';
export const BUDGET_KEY = 'budget';

let database = null;

export function openDatabase() {
    if (typeof indexedDB === 'undefined') {
        return Promise.resolve(null);
    }
    database ??= new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, DB_VERSION);
        request.onupgradeneeded = () => {
            for (const store of ['handles', 'meta']) {
                if (!request.result.objectStoreNames.contains(store)) {
                    request.result.createObjectStore(store);
                }
            }
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    }).catch(() => null);
    return database;
}

async function run(store, mode, action) {
    const db = await openDatabase();
    if (db === null) {
        return undefined;
    }
    return new Promise((resolve, reject) => {
        const transaction = db.transaction(store, mode);
        const request = action(transaction.objectStore(store));
        transaction.oncomplete = () => resolve(request?.result);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error);
    });
}

export const dbGet = (store, key) => run(store, 'readonly', (s) => s.get(key));
export const dbPut = (store, key, value) => run(store, 'readwrite', (s) => s.put(value, key));
export const dbDelete = (store, key) => run(store, 'readwrite', (s) => s.delete(key));
