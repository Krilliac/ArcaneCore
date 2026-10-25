// The browser's texture memory: fetches ATX1 textures from the host (GET /api/bin/texture), uploads them to WebGL and keeps what the
// frames use inside a byte budget.
//
//   get(key) answers at once: a ready entry, or an entry that stands in (a placeholder GL texture) while the fetch runs. A key is
//     fetched once however many batches ask for it, at most four fetches run at a time, and a key the host does not have is
//     remembered as missing (and drawn as a magenta and black checker) instead of being asked for again.
//   beginFrame()/endFrame() bracket a frame. At endFrame the store evicts down to the budget: textures the frame did not use go
//     first, least recently used first (a texture used one frame ago goes after every older one, so it is the last unused one to go).
//     Making room for a generated array (acquireArray/growArray) is stricter: textures used this frame or the one before are pinned
//     and are not evicted for it; the budget at endFrame is a hard limit, so there the pin yields to LRU order. When the frame's own textures exceed the budget the newest arrivals are dropped and their
//     keys marked starved (drawn flat, not fetched again) so a working set that does not fit settles instead of being fetched,
//     evicted and fetched again every frame. Starved keys are retried when the working set shrinks or the budget grows.
//   Memory is counted as the bytes the levels occupy on the wire (compressed size for DXT): what the host sent is what is resident.
//   When the textures a frame uses do not fit, the store also asks the host for textures one maxSize step smaller until the working
//   set has been under 80 % of the budget for 120 frames (design section 4, anti-thrash rule 3).
//   TEXTURE_2D_ARRAY: a texture with several layers uploads as an array, and acquireArray/growArray/uploadLayer/release let a lane
//   generate arrays (terrain layers, character composites) inside the same budget. Arrays grow in steps of 64 layers.
//
// Nothing here writes to browser storage of any kind: textures live in GPU memory for the page's lifetime only.

import { FLAG, FORMAT, decodeTexture } from '../atx1.js';

const MAX_CONCURRENT_FETCHES = 4;
const TRANSIENT_RETRY_MS = 5000;
const PLACEHOLDER_SIZE = 8;
const S3TC_EXTENSION = 'WEBGL_compressed_texture_s3tc';
const ARRAY_LAYER_STEP = 64;
const RECOVERY_FRAMES = 120;
const RECOVERY_FRACTION = 0.8;
const MIN_STEP_SIZE = 16;

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
    #arrayReallocs = 0;
    #largestEdge = 0;
    #steppedDown = false;
    #underBudgetFrames = 0;
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
        if (entry.dirty) {
            this.#flushMips(entry);
        }
        return entry;
    }

    /**
     * Allocates a generated TEXTURE_2D_ARRAY of RGBA8 layers (full mip chain) inside the budget. Returns the entry
     * `{key, ready, texture, target, width, height, layers, capacity, bytes, owned}` or null when it cannot fit even after evicting
     * unused textures, or when the context cannot hold that many layers. `layers` counts the layers written so far, `capacity` is
     * rounded up to a multiple of 64. The entry stays until `release(key)`; it is never evicted.
     */
    acquireArray(key, { width, height, layers = 1 }) {
        if (this.#disposed) {
            return null;
        }
        const existing = this.#entries.get(key);
        if (existing) {
            return existing.owned ? existing : null;
        }
        if (!isEdge(width) || !isEdge(height) || !Number.isInteger(layers) || layers < 1) {
            throw new RangeError('An array needs positive integer width, height and layers.');
        }
        const capacity = this.#capacityFor(layers);
        if (capacity === 0) {
            return null;
        }
        const bytes = arrayBytes(width, height, capacity);
        if (!this.#makeRoom(bytes)) {
            return null;
        }
        const gl = this.#gl;
        const handle = gl.createTexture();
        this.#allocateArray(handle, width, height, capacity);
        const entry = this.#newEntry(key);
        Object.assign(entry, {
            ready: true,
            texture: handle,
            target: gl.TEXTURE_2D_ARRAY,
            mips: true,
            width,
            height,
            layers: 0,
            capacity,
            bytes,
            owned: true,
            dirty: false,
            arrival: ++this.#arrivals,
        });
        this.#entries.set(key, entry);
        this.#bytes += bytes;
        return entry;
    }

    /**
     * Makes room for `layers` layers in an array from acquireArray: a new array of capacity + 64 is allocated, the used layers are
     * copied on the GPU, the mips are rebuilt and the old array is deleted. Both exist for a moment, so old + new bytes must fit the
     * budget; otherwise nothing changes and false is returned (the caller falls back). True when the capacity already sufficed.
     */
    growArray(key, layers) {
        const entry = this.#entries.get(key);
        if (!entry?.owned || !entry.ready) {
            return false;
        }
        if (layers <= entry.capacity) {
            return true;
        }
        const capacity = this.#capacityFor(layers);
        if (capacity === 0) {
            return false;
        }
        const bytes = arrayBytes(entry.width, entry.height, capacity);
        // The old array is still resident while the new one exists, so the new one must fit on top of it.
        if (!this.#makeRoom(bytes)) {
            return false;
        }
        const gl = this.#gl;
        const handle = gl.createTexture();
        this.#allocateArray(handle, entry.width, entry.height, capacity);
        const framebuffer = gl.createFramebuffer();
        gl.bindFramebuffer(gl.READ_FRAMEBUFFER, framebuffer);
        gl.bindTexture(gl.TEXTURE_2D_ARRAY, handle);
        for (let layer = 0; layer < entry.layers; layer++) {
            gl.framebufferTextureLayer(gl.READ_FRAMEBUFFER, gl.COLOR_ATTACHMENT0, entry.texture, 0, layer);
            gl.copyTexSubImage3D(gl.TEXTURE_2D_ARRAY, 0, 0, 0, layer, 0, 0, entry.width, entry.height);
        }
        gl.bindFramebuffer(gl.READ_FRAMEBUFFER, null);
        gl.deleteFramebuffer(framebuffer);
        gl.deleteTexture(entry.texture);
        this.#bytes += bytes - entry.bytes;
        entry.texture = handle;
        entry.capacity = capacity;
        entry.bytes = bytes;
        entry.dirty = true;
        this.#arrayReallocs++;
        this.#flushMips(entry);
        return true;
    }

    /** Writes the RGBA8 pixels of layer `layer` (level 0, width x height x 4 bytes); the mips are rebuilt when the entry is next got. */
    uploadLayer(key, layer, pixels) {
        const entry = this.#entries.get(key);
        if (!entry?.owned || !entry.ready || !Number.isInteger(layer) || layer < 0 || layer >= entry.capacity) {
            return false;
        }
        if (pixels.byteLength !== entry.width * entry.height * 4) {
            throw new RangeError('A layer needs width x height x 4 bytes of RGBA.');
        }
        const gl = this.#gl;
        gl.bindTexture(gl.TEXTURE_2D_ARRAY, entry.texture);
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
        gl.texSubImage3D(gl.TEXTURE_2D_ARRAY, 0, 0, 0, layer, entry.width, entry.height, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
        entry.layers = Math.max(entry.layers, layer + 1);
        entry.dirty = true;
        return true;
    }

    /** Deletes the GL texture of `key` (client or generated) and forgets the key. */
    release(key) {
        const entry = this.#entries.get(key);
        if (!entry) {
            return false;
        }
        this.#release(entry);
        this.#entries.delete(key);
        return true;
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
            arrayReallocs: this.#arrayReallocs,
            steppedDown: this.#steppedDown,
            effectiveMaxSize: this.#effectiveMaxSize(),
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
            target: this.#gl.TEXTURE_2D,
            layers: 1,
            capacity: 1,
            owned: false,
            dirty: false,
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
        const options = { fmt: this.usesDxt ? 'dxt' : 'rgba', maxSize: this.#effectiveMaxSize(), signal: abort.signal };
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
        const compressed = texture.format >= FORMAT.DXT1 && texture.format <= FORMAT.DXT5;
        let internalFormat = 0;
        if (compressed) {
            if (!this.usesDxt) {
                throw new Error('The host sent a compressed texture but this browser has no S3TC support.');
            }
            // DXT1 without 1-bit alpha is opaque (the CPU decoder forces it): the RGB format keeps c0 <= c1 index 3 black, not clear.
            internalFormat = texture.format === FORMAT.DXT1
                ? ((texture.flags & FLAG.HAS_ALPHA) !== 0
                    ? this.#s3tc.COMPRESSED_RGBA_S3TC_DXT1_EXT
                    : this.#s3tc.COMPRESSED_RGB_S3TC_DXT1_EXT)
                : texture.format === FORMAT.DXT3
                    ? this.#s3tc.COMPRESSED_RGBA_S3TC_DXT3_EXT
                    : this.#s3tc.COMPRESSED_RGBA_S3TC_DXT5_EXT;
        } else if (texture.format === FORMAT.R8) {
            internalFormat = gl.R8;
        } else {
            internalFormat = gl.RGBA8 ?? gl.RGBA;
        }
        const layered = texture.layers > 1;
        const target = layered ? gl.TEXTURE_2D_ARRAY : gl.TEXTURE_2D;

        const handle = gl.createTexture();
        gl.bindTexture(target, handle);
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
        if (layered) {
            gl.texStorage3D(target, texture.levels.length, internalFormat, texture.width, texture.height, texture.layers);
        }
        let bytes = 0;
        texture.levels.forEach((level, i) => {
            const width = Math.max(1, texture.width >> i);
            const height = Math.max(1, texture.height >> i);
            if (layered) {
                // A level holds its layers one after the other, each the same size.
                const perLayer = level.byteLength / texture.layers;
                for (let layer = 0; layer < texture.layers; layer++) {
                    const data = level.subarray(layer * perLayer, (layer + 1) * perLayer);
                    if (compressed) {
                        gl.compressedTexSubImage3D(target, i, 0, 0, layer, width, height, 1, internalFormat, data);
                    } else {
                        const format = texture.format === FORMAT.R8 ? gl.RED : gl.RGBA;
                        gl.texSubImage3D(target, i, 0, 0, layer, width, height, 1, format, gl.UNSIGNED_BYTE, data);
                    }
                }
            } else if (compressed) {
                gl.compressedTexImage2D(target, i, internalFormat, width, height, 0, level);
            } else if (texture.format === FORMAT.R8) {
                gl.texImage2D(target, i, gl.R8, width, height, 0, gl.RED, gl.UNSIGNED_BYTE, level);
            } else {
                gl.texImage2D(target, i, gl.RGBA8 ?? gl.RGBA, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, level);
            }
            bytes += level.byteLength;
        });
        const mips = texture.levels.length > 1;
        gl.texParameteri(target, gl.TEXTURE_BASE_LEVEL, 0);
        gl.texParameteri(target, gl.TEXTURE_MAX_LEVEL, texture.levels.length - 1);
        gl.texParameteri(target, gl.TEXTURE_MIN_FILTER, mips ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR);
        gl.texParameteri(target, gl.TEXTURE_MAG_FILTER, gl.LINEAR);

        entry.texture = handle;
        entry.target = target;
        entry.layers = texture.layers;
        entry.capacity = texture.layers;
        entry.ready = true;
        entry.missing = false;
        entry.starved = false;
        entry.mips = mips;
        entry.width = texture.width;
        entry.height = texture.height;
        entry.bytes = bytes;
        entry.arrival = ++this.#arrivals;
        this.#largestEdge = Math.max(this.#largestEdge, texture.width, texture.height);
        this.#bytes += bytes;
    }

    /** The edge cap sent to the host: the configured one, or one step (half) smaller while the working set does not fit. */
    #effectiveMaxSize() {
        if (!this.#steppedDown) {
            return this.#maxSize;
        }
        const base = this.#maxSize > 0 ? this.#maxSize : this.#largestEdge;
        return Math.max(MIN_STEP_SIZE, base >> 1);
    }

    /** Layer capacity for `layers` used layers, in steps of 64, or 0 when the context cannot hold that many. */
    #capacityFor(layers) {
        const capacity = Math.ceil(layers / ARRAY_LAYER_STEP) * ARRAY_LAYER_STEP;
        const limit = this.#gl.getParameter(this.#gl.MAX_ARRAY_TEXTURE_LAYERS);
        if (Number.isFinite(limit) && limit > 0 && capacity > limit) {
            return layers <= limit ? limit : 0;
        }
        return capacity;
    }

    #allocateArray(handle, width, height, capacity) {
        const gl = this.#gl;
        gl.bindTexture(gl.TEXTURE_2D_ARRAY, handle);
        gl.texStorage3D(gl.TEXTURE_2D_ARRAY, levelCount(width, height), gl.RGBA8, width, height, capacity);
        gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
        gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    }

    #flushMips(entry) {
        const gl = this.#gl;
        gl.bindTexture(gl.TEXTURE_2D_ARRAY, entry.texture);
        gl.generateMipmap(gl.TEXTURE_2D_ARRAY);
        entry.dirty = false;
    }

    /**
     * Evicts unowned textures idle for two frames or more (least recently used first) until `extra` more bytes fit; false when they
     * cannot. Textures used by this frame or the one before are pinned: the two-frame pin of the design.
     */
    #makeRoom(extra) {
        if (this.#bytes + extra <= this.#budget) {
            return true;
        }
        const idle = [...this.#entries.values()]
            .filter((e) => e.ready && !e.owned && e.lastUsed < this.#frame - 1)
            .sort((a, b) => a.lastUsed - b.lastUsed || a.arrival - b.arrival);
        for (const entry of idle) {
            if (this.#bytes + extra <= this.#budget) {
                break;
            }
            this.#release(entry);
            this.#evictions++;
            this.#entries.delete(entry.key);
        }
        return this.#bytes + extra <= this.#budget;
    }

    #release(entry) {
        if (entry.ready) {
            this.#gl.deleteTexture(entry.texture);
            this.#bytes -= entry.bytes;
        }
        entry.ready = false;
        entry.dirty = false;
        entry.texture = this.#placeholder;
        entry.bytes = 0;
    }

    #enforceBudget() {
        let evictedUsed = false;
        if (this.#bytes > this.#budget) {
            const resident = [...this.#entries.values()].filter((e) => e.ready && !e.owned);
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
        if (evictedUsed && !this.#steppedDown) {
            // Rule 3: the pinned set alone exceeds the budget. Ask for smaller textures from now on, and let the starved keys try once
            // more at that size (they are not released again until the working set has shrunk, so this cannot loop).
            this.#steppedDown = true;
            this.#underBudgetFrames = 0;
            this.#releaseStarved();
            this.#overBudget = true;
        } else {
            this.#overBudget = evictedUsed || (this.#overBudget && this.#hasStarved());
        }
        this.#recoverSize();
        if (!evictedUsed && this.#hasStarved() && this.#hasIdleResident()) {
            this.#releaseStarved();
        }
        if (!this.#hasStarved()) {
            this.#overBudget = false;
        }
    }

    /** Hysteresis: the normal edge cap returns after 120 frames with the textures the frame uses under 80 % of the budget and none starved. */
    #recoverSize() {
        if (!this.#steppedDown) {
            return;
        }
        if (this.#usedBytes() < RECOVERY_FRACTION * this.#budget && !this.#hasStarved()) {
            if (++this.#underBudgetFrames >= RECOVERY_FRAMES) {
                this.#steppedDown = false;
                this.#underBudgetFrames = 0;
            }
        } else {
            this.#underBudgetFrames = 0;
        }
    }

    /** Bytes of the textures the current frame asked for. */
    #usedBytes() {
        let total = 0;
        for (const entry of this.#entries.values()) {
            if (entry.ready && entry.lastUsed === this.#frame) {
                total += entry.bytes;
            }
        }
        return total;
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

function isEdge(value) {
    return Number.isInteger(value) && value > 0;
}

function levelCount(width, height) {
    return Math.floor(Math.log2(Math.max(width, height))) + 1;
}

/** Bytes of an RGBA8 array with a full mip chain. */
function arrayBytes(width, height, layers) {
    let total = 0;
    for (let i = 0; i < levelCount(width, height); i++) {
        total += Math.max(1, width >> i) * Math.max(1, height >> i) * 4 * layers;
    }
    return total;
}
