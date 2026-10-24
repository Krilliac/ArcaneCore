// The browser's texture memory: fetches ATX1 textures from the host (GET /api/bin/texture), uploads them to WebGL and keeps what the
// frames use inside a byte budget.
//
//   get(key) answers at once: a ready entry, or an entry that stands in (a placeholder GL texture) while the fetch runs. A key is
//     fetched once however many batches ask for it, at most four fetches run at a time, and a key the host does not have is
//     remembered as missing (and drawn as a magenta and black checker) instead of being asked for again.
//   beginFrame()/endFrame() bracket a frame. At endFrame the store evicts down to the budget: textures the frame did not use go
//     first, least recently used first. When the frame's own textures exceed the budget the newest arrivals are dropped and their
//     keys marked starved (drawn flat, not fetched again) so a working set that does not fit settles instead of being fetched,
//     evicted and fetched again every frame. Starved keys are retried when the working set shrinks or the budget grows.
//   Memory is counted as the bytes the levels occupy on the wire (compressed size for DXT): what the host sent is what is resident.
//
// Nothing here touches IndexedDB, localStorage or the Cache API: textures live in GPU memory for the page's lifetime only.

import { FORMAT, decodeTexture } from './atx1.js';

const MAX_CONCURRENT_FETCHES = 4;
const TRANSIENT_RETRY_MS = 5000;
const PLACEHOLDER_SIZE = 8;
const S3TC_EXTENSION = 'WEBGL_compressed_texture_s3tc';

export class TextureStore {
    #gl;
    #fetchTexture;
    #budget;
    #maxSize;
    #allowDxt;
    #onChange;
    #s3tc;
    #entries = new Map();
    #queue = [];
    #idleWaiters = [];
    #inFlight = 0;
    #frame = 0;
    #fetches = 0;
    #evictions = 0;
    #bytes = 0;
    #arrivals = 0;
    #overBudget = false;
    #disposed = false;
    #placeholder = null;
    #aborts = new Set();

    /**
     * @param {object} options
     * @param {WebGL2RenderingContext} options.gl
     * @param {(key: string, options: {fmt: 'rgba'|'dxt', maxSize: number, signal: AbortSignal}) => Promise<Uint8Array|ArrayBuffer>} options.fetchTexture
     * @param {number} options.budgetBytes resident texture bytes to stay under
     * @param {number} [options.maxSize] largest texture edge to ask the host for (0 = no cap)
     * @param {boolean} [options.allowDxt] ask for compressed textures when the browser supports S3TC (default true)
     * @param {() => void} [options.onChange] called after a texture arrives or fails, so the caller can redraw
     */
    constructor({ gl, fetchTexture, budgetBytes, maxSize = 0, allowDxt = true, onChange = null } = {}) {
        if (!gl || typeof gl !== 'object') {
            throw new TypeError('TextureStore needs a WebGL context.');
        }
        if (typeof fetchTexture !== 'function') {
            throw new TypeError('TextureStore needs a fetchTexture function.');
        }
        assertBudget(budgetBytes);
        this.#gl = gl;
        this.#fetchTexture = fetchTexture;
        this.#budget = budgetBytes;
        this.#maxSize = Number.isInteger(maxSize) && maxSize > 0 ? maxSize : 0;
        this.#allowDxt = allowDxt !== false;
        this.#onChange = typeof onChange === 'function' ? onChange : null;
        this.#s3tc = this.#allowDxt ? gl.getExtension(S3TC_EXTENSION) : null;
        this.#placeholder = this.#makePlaceholder();
    }

    /** True when compressed textures are requested from the host. */
    get usesDxt() {
        return this.#s3tc !== null && this.#s3tc !== undefined;
    }

    /** The checker texture drawn for a key the host does not have. */
    get placeholder() {
        return this.#placeholder;
    }

    beginFrame() {
        this.#frame++;
    }

    /**
     * The entry for `key`: `{key, ready, missing, starved, texture, mips, width, height}`. `texture` is always usable: the real GL
     * texture when `ready`, the shared placeholder otherwise. Asking marks the key as used by the current frame.
     */
    get(key) {
        let entry = this.#entries.get(key);
        if (!entry) {
            entry = this.#newEntry(key);
            this.#entries.set(key, entry);
            this.#queue.push(entry);
            this.#pump();
        } else if (entry.missing && entry.retryAt !== 0 && performance.now() >= entry.retryAt) {
            entry.missing = false;
            entry.retryAt = 0;
            this.#queue.push(entry);
            this.#pump();
        }
        entry.lastUsed = this.#frame;
        return entry;
    }

    endFrame() {
        this.#enforceBudget();
    }

    setBudget(budgetBytes) {
        assertBudget(budgetBytes);
        if (budgetBytes > this.#budget) {
            this.#releaseStarved();
        }
        this.#budget = budgetBytes;
    }

    /** Resolves when no fetch is running or queued (and every arrived texture has been uploaded). */
    whenIdle() {
        if (this.#isIdle()) {
            return Promise.resolve();
        }
        return new Promise((resolve) => this.#idleWaiters.push(resolve));
    }

    stats() {
        let resident = 0;
        let missing = 0;
        let starved = 0;
        for (const entry of this.#entries.values()) {
            if (entry.ready) {
                resident++;
            } else if (entry.missing) {
                missing++;
            } else if (entry.starved) {
                starved++;
            }
        }
        return {
            resident,
            bytes: this.#bytes,
            inFlight: this.#inFlight,
            queued: this.#queue.length,
            pending: this.#inFlight + this.#queue.length,
            missing,
            starved,
            fetches: this.#fetches,
            evictions: this.#evictions,
            budgetBytes: this.#budget,
            overBudget: this.#overBudget,
        };
    }

    /** Deletes every GL texture, the placeholder included, and ignores fetches still in flight. */
    dispose() {
        this.#disposed = true;
        for (const abort of this.#aborts) {
            abort.abort();
        }
        this.#aborts.clear();
        for (const entry of this.#entries.values()) {
            this.#release(entry);
        }
        this.#entries.clear();
        this.#queue.length = 0;
        if (this.#placeholder) {
            this.#gl.deleteTexture(this.#placeholder);
            this.#placeholder = null;
        }
        this.#bytes = 0;
        this.#settleIdle();
    }

    // ---------------------------------------------------------------------------------------------------------------------

    #newEntry(key) {
        return {
            key,
            ready: false,
            missing: false,
            starved: false,
            texture: this.#placeholder,
            mips: false,
            width: 0,
            height: 0,
            bytes: 0,
            lastUsed: this.#frame,
            arrival: 0,
            retryAt: 0,
        };
    }

    #makePlaceholder() {
        const gl = this.#gl;
        const texture = gl.createTexture();
        const pixels = new Uint8Array(PLACEHOLDER_SIZE * PLACEHOLDER_SIZE * 4);
        for (let y = 0; y < PLACEHOLDER_SIZE; y++) {
            for (let x = 0; x < PLACEHOLDER_SIZE; x++) {
                const magenta = ((x + y) & 1) === 0;
                const at = (y * PLACEHOLDER_SIZE + x) * 4;
                pixels[at] = magenta ? 255 : 0;
                pixels[at + 1] = 0;
                pixels[at + 2] = magenta ? 255 : 0;
                pixels[at + 3] = 255;
            }
        }
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8 ?? gl.RGBA, PLACEHOLDER_SIZE, PLACEHOLDER_SIZE, 0, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        return texture;
    }

    #isIdle() {
        return this.#inFlight === 0 && this.#queue.length === 0;
    }

    #settleIdle() {
        if (this.#isIdle() && this.#idleWaiters.length > 0) {
            const waiters = this.#idleWaiters.splice(0);
            for (const resolve of waiters) {
                resolve();
            }
        }
    }

    #pump() {
        while (!this.#disposed && this.#inFlight < MAX_CONCURRENT_FETCHES && this.#queue.length > 0) {
            this.#start(this.#queue.shift());
        }
    }

    #start(entry) {
        this.#inFlight++;
        this.#fetches++;
        const abort = new AbortController();
        this.#aborts.add(abort);
        const options = { fmt: this.usesDxt ? 'dxt' : 'rgba', maxSize: this.#maxSize, signal: abort.signal };
        const run = async () => this.#fetchTexture(entry.key, options);
        run().then(
            (bytes) => this.#arrive(entry, bytes),
            (error) => this.#fail(entry, error),
        ).finally(() => {
            this.#aborts.delete(abort);
            this.#inFlight--;
            this.#pump();
            this.#settleIdle();
        });
    }

    #arrive(entry, bytes) {
        if (this.#disposed || this.#entries.get(entry.key) !== entry) {
            return;
        }
        try {
            this.#upload(entry, decodeTexture(bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes)));
        } catch (error) {
            this.#fail(entry, Object.assign(error instanceof Error ? error : new Error(String(error)), { permanent: true }));
            return;
        }
        this.#onChange?.();
    }

    #fail(entry, error) {
        if (this.#disposed || this.#entries.get(entry.key) !== entry) {
            return;
        }
        const status = Number(error?.status);
        const permanent = error?.permanent === true || (status >= 400 && status < 500);
        entry.missing = true;
        entry.ready = false;
        entry.retryAt = permanent || error?.name === 'AbortError' ? 0 : performance.now() + TRANSIENT_RETRY_MS;
        this.#onChange?.();
    }

    #upload(entry, texture) {
        const gl = this.#gl;
        if (texture.layers !== 1) {
            throw new Error('Texture arrays are not supported by this view.');
        }
        const compressed = texture.format >= FORMAT.DXT1 && texture.format <= FORMAT.DXT5;
        let internalFormat = 0;
        if (compressed) {
            if (!this.usesDxt) {
                throw new Error('The host sent a compressed texture but this browser has no S3TC support.');
            }
            internalFormat = texture.format === FORMAT.DXT1
                ? this.#s3tc.COMPRESSED_RGBA_S3TC_DXT1_EXT
                : texture.format === FORMAT.DXT3
                    ? this.#s3tc.COMPRESSED_RGBA_S3TC_DXT3_EXT
                    : this.#s3tc.COMPRESSED_RGBA_S3TC_DXT5_EXT;
        }

        const handle = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, handle);
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
        let bytes = 0;
        texture.levels.forEach((level, i) => {
            const width = Math.max(1, texture.width >> i);
            const height = Math.max(1, texture.height >> i);
            if (compressed) {
                gl.compressedTexImage2D(gl.TEXTURE_2D, i, internalFormat, width, height, 0, level);
            } else if (texture.format === FORMAT.R8) {
                gl.texImage2D(gl.TEXTURE_2D, i, gl.R8, width, height, 0, gl.RED, gl.UNSIGNED_BYTE, level);
            } else {
                gl.texImage2D(gl.TEXTURE_2D, i, gl.RGBA8 ?? gl.RGBA, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, level);
            }
            bytes += level.byteLength;
        });
        const mips = texture.levels.length > 1;
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_BASE_LEVEL, 0);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAX_LEVEL, texture.levels.length - 1);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, mips ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);

        entry.texture = handle;
        entry.ready = true;
        entry.missing = false;
        entry.starved = false;
        entry.mips = mips;
        entry.width = texture.width;
        entry.height = texture.height;
        entry.bytes = bytes;
        entry.arrival = ++this.#arrivals;
        this.#bytes += bytes;
    }

    #release(entry) {
        if (entry.ready) {
            this.#gl.deleteTexture(entry.texture);
            this.#bytes -= entry.bytes;
        }
        entry.ready = false;
        entry.texture = this.#placeholder;
        entry.bytes = 0;
    }

    #enforceBudget() {
        let evictedUsed = false;
        if (this.#bytes > this.#budget) {
            const resident = [...this.#entries.values()].filter((e) => e.ready);
            const used = (e) => e.lastUsed === this.#frame;
            // Not used by this frame: least recently used first. Used by this frame: the newest arrival first, so the textures the
            // view settled on stay and the ones that no longer fit are the ones left out.
            resident.sort((a, b) => {
                if (used(a) !== used(b)) {
                    return used(a) ? 1 : -1;
                }
                return used(a) ? b.arrival - a.arrival : a.lastUsed - b.lastUsed || a.arrival - b.arrival;
            });
            for (const entry of resident) {
                if (this.#bytes <= this.#budget) {
                    break;
                }
                const wasUsed = used(entry);
                this.#release(entry);
                this.#evictions++;
                if (wasUsed) {
                    entry.starved = true;
                    evictedUsed = true;
                } else {
                    this.#entries.delete(entry.key);
                }
            }
        }
        this.#overBudget = evictedUsed || (this.#overBudget && this.#hasStarved());
        if (!evictedUsed && this.#hasStarved() && this.#hasIdleResident()) {
            this.#releaseStarved();
        }
        if (!this.#hasStarved()) {
            this.#overBudget = false;
        }
    }

    #hasStarved() {
        for (const entry of this.#entries.values()) {
            if (entry.starved) {
                return true;
            }
        }
        return false;
    }

    #hasIdleResident() {
        for (const entry of this.#entries.values()) {
            if (entry.ready && entry.lastUsed !== this.#frame) {
                return true;
            }
        }
        return false;
    }

    /** Forgets the starved keys so the next request for one fetches it again. */
    #releaseStarved() {
        for (const [key, entry] of this.#entries) {
            if (entry.starved) {
                this.#entries.delete(key);
            }
        }
        this.#overBudget = false;
    }
}

function assertBudget(budgetBytes) {
    if (typeof budgetBytes !== 'number' || !Number.isFinite(budgetBytes) || budgetBytes <= 0) {
        throw new RangeError('The texture budget must be a positive number of bytes.');
    }
}
