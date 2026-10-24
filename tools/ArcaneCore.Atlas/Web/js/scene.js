// Tile window manager: decides which (layer, tile) chunks to hold for the current map and camera, fetches them with a
// bounded number of concurrent requests (nearest tile first), decodes ATG1, uploads to the GPU within a per-frame time
// budget, evicts what has left the window, and remembers every tile's status (ok, empty, missing, rejected, error) so
// the UI can say why a tile shows nothing: a missing file is never presented as empty space.

import { Atg1Error, decodeChunk, splitTileResponse } from './atg1.js';
import { FALLBACK_LAYERS } from './catalog.js';
import { GLOBAL_INDEX, inGrid, tileKey, wantedTiles } from './tiles.js';
import { Emitter } from './util.js';

export const STATES = ['Ok', 'Empty', 'Missing', 'Rejected', 'Unknown', 'NotApplicable', 'Error'];
const DEFAULT_CONCURRENCY = 6;
const RETRY_AFTER_MS = 5000;
const MAX_GPU_BYTES = 1.2 * 1024 ** 3;

const fallbackInfo = new Map(FALLBACK_LAYERS.map((l) => [l.id, l]));

/** Header text -> a known state name (case-insensitive); anything else is 'Unknown'. */
export function normalizeState(text) {
    const t = String(text ?? '').trim().toLowerCase();
    return STATES.find((s) => s.toLowerCase() === t) ?? 'Unknown';
}

export class Scene extends Emitter {
    constructor({ api, renderer, concurrency = DEFAULT_CONCURRENCY, clock = () => performance.now() }) {
        super();
        this.api = api;
        this.renderer = renderer;
        this.concurrency = concurrency;
        this.clock = clock;
        this.infos = new Map();
        this.map = null;
        this.focus = null;
        this.radius = 1;
        this.generation = 0;
        this.entries = new Map();
        this.statuses = new Map();
        this.inflight = new Map();
        this.queue = [];
        this.uploads = [];
        this.failedAt = new Map();
        this.wantedKeys = new Set();
        this.errors = [];
    }

    noteError(message) {
        this.errors.push(message);
        if (this.errors.length > 50) {
            this.errors.shift();
        }
    }

    setLayerInfos(infos) {
        this.infos = new Map(infos.map((i) => [i.id, i]));
    }

    infoOf(id) {
        return this.infos.get(id) ?? fallbackInfo.get(id) ?? null;
    }

    /**
     * Declares the window: `map`, the focus tile centre (world x, y or null), the radius in tiles and the layer ids whose
     * tiles are wanted. `layerRadius` ({layerId: tiles}) narrows the window of single layers (the textured layers load fewer
     * tiles than the plain ones). Cheap when nothing changed.
     */
    update({ map, focusTile, radius, layers, layerRadius = null }) {
        if (this.map !== map) {
            this.reset();
            this.map = map;
        }
        const now = this.clock();
        this.focus = focusTile;
        this.radius = radius;
        const wanted = new Map();
        const windows = new Map();
        const tilesOf = (layer) => {
            const r = Math.min(radius, layerRadius?.[layer] ?? radius);
            if (!windows.has(r)) {
                windows.set(r, focusTile ? wantedTiles(focusTile, r) : []);
            }
            return windows.get(r);
        };
        for (const layer of layers) {
            wanted.set(tileKey(layer, map, GLOBAL_INDEX, GLOBAL_INDEX), { layer, x: GLOBAL_INDEX, y: GLOBAL_INDEX, priority: -1 });
            for (const t of tilesOf(layer)) {
                wanted.set(tileKey(layer, map, t.x, t.y), { layer, x: t.x, y: t.y, priority: t.d2 });
            }
        }
        this.wantedKeys = new Set(wanted.keys());
        for (const [key, entry] of this.entries) {
            if (wanted.has(key)) {
                entry.lastWanted = now;
            }
        }

        // Abort what is in flight but no longer wanted.
        for (const [key, job] of this.inflight) {
            if (!wanted.has(key)) {
                job.controller.abort();
            }
        }
        this.queue = this.queue.filter((q) => wanted.has(q.key));

        // Queue what is missing. A tile the host has answered (any state but a transport or decode error) is final until the
        // host announces a change (invalidate), including one whose chunks are decoded and waiting for pump to upload them.
        const queued = new Set(this.queue.map((q) => q.key));
        for (const [key, w] of wanted) {
            if (this.entries.has(key) || this.inflight.has(key) || queued.has(key)) {
                continue;
            }
            const failed = this.failedAt.get(key);
            if (failed !== undefined && now - failed < RETRY_AFTER_MS) {
                continue;
            }
            const status = this.statuses.get(key);
            if (status && status.fetched && status.state !== 'Error') {
                continue;
            }
            this.queue.push({ key, ...w });
        }
        this.queue.sort((a, b) => a.priority - b.priority);
        this.#evict(now);
        this.#startFetches();
    }

    reset() {
        this.generation++;
        for (const job of this.inflight.values()) {
            job.controller.abort();
        }
        this.inflight.clear();
        this.queue = [];
        this.uploads = [];
        for (const entry of this.entries.values()) {
            for (const chunk of entry.chunks) {
                this.renderer.destroyChunk(chunk);
            }
        }
        this.entries.clear();
        this.statuses.clear();
        this.failedAt.clear();
        this.wantedKeys = new Set();
        this.errors = [];
        this.emit('change');
    }

    /** Drops a tile (the host says it changed) and refetches it if it is wanted. */
    invalidate(layer, map, x, y) {
        if (map !== this.map) {
            return;
        }
        const key = tileKey(layer, map, x, y);
        const entry = this.entries.get(key);
        if (entry) {
            for (const chunk of entry.chunks) {
                this.renderer.destroyChunk(chunk);
            }
            this.entries.delete(key);
        }
        this.inflight.get(key)?.controller.abort();
        this.statuses.delete(key);
        this.failedAt.delete(key);
        this.emit('change');
    }

    invalidateLayer(layer) {
        for (const key of [...this.entries.keys()]) {
            if (key.startsWith(`${layer}|`)) {
                const [, map, x, y] = key.split('|');
                this.invalidate(layer, Number(map), Number(x), Number(y));
            }
        }
    }

    #startFetches() {
        while (this.inflight.size < this.concurrency && this.queue.length > 0) {
            const job = this.queue.shift();
            this.#fetch(job);
        }
    }

    async #fetch({ key, layer, x, y }) {
        const generation = this.generation;
        const map = this.map;
        const controller = new AbortController();
        this.inflight.set(key, { controller });
        this.setStatus(key, layer, map, x, y, { state: 'Unknown', detail: 'loading', loading: true });
        try {
            const response = await this.api.tile(layer, map, x, y, controller.signal);
            if (generation !== this.generation) {
                return;
            }
            const state = normalizeState(response.state);
            const decoded = [];
            let rejection = null;
            if (state === 'Ok' || state === 'Empty') {
                try {
                    for (const part of splitTileResponse(response.body)) {
                        decoded.push(decodeChunk(part));
                    }
                } catch (error) {
                    if (!(error instanceof Atg1Error)) {
                        throw error;
                    }
                    rejection = `The host sent a chunk this UI refuses: ${error.message}`;
                }
            }
            if (rejection) {
                this.failedAt.set(key, this.clock());
                this.setStatus(key, layer, map, x, y, { state: 'Error', detail: rejection, fetched: true });
                this.noteError(`${layer} ${map}/${x}/${y}: ${rejection}`);
            } else {
                const primitives = decoded.reduce((n, d) => n + d.triangleCount + d.lineCount + d.pointCount, 0);
                this.setStatus(key, layer, map, x, y, { state, detail: response.detail, primitives, fetched: true });
                this.uploads.push({ key, layer, map, x, y, decoded, generation });
            }
        } catch (error) {
            if (error?.name === 'AbortError') {
                if (generation === this.generation) {
                    this.statuses.delete(key);
                }
            } else if (generation === this.generation) {
                this.failedAt.set(key, this.clock());
                this.setStatus(key, layer, map, x, y, { state: 'Error', detail: error.message, fetched: true });
                this.noteError(`${layer} ${map}/${x}/${y}: ${error.message}`);
            }
        } finally {
            if (this.inflight.get(key)?.controller === controller) {
                this.inflight.delete(key);
            }
            if (generation === this.generation) {
                this.#startFetches();
                this.emit('change');
            }
        }
    }

    setStatus(key, layer, map, x, y, fields) {
        this.statuses.set(key, { layer, map, x, y, state: 'Unknown', detail: '', primitives: 0, ...fields });
    }

    /** Merges peeked states (GET /api/tiles) without overwriting anything a fetch already established. */
    mergePeek(layer, answer) {
        let changed = false;
        for (const t of Array.isArray(answer?.tiles) ? answer.tiles : []) {
            if (!Array.isArray(t.tile) || t.tile.length !== 3 || t.tile[0] !== this.map) {
                continue;
            }
            const key = tileKey(layer, t.tile[0], t.tile[1], t.tile[2]);
            const existing = this.statuses.get(key);
            if (existing && (existing.fetched || existing.loading)) {
                continue;
            }
            this.setStatus(key, layer, t.tile[0], t.tile[1], t.tile[2], {
                state: normalizeState(t.state),
                detail: typeof t.detail === 'string' ? t.detail : '',
                primitives: Number(t.primitives) || 0,
                peeked: true,
            });
            changed = true;
        }
        if (changed) {
            this.emit('change');
        }
    }

    /** Uploads decoded chunks until the time budget (ms) is spent; true while more remain. */
    pump(budgetMs) {
        const start = this.clock();
        let touched = false;
        while (this.uploads.length > 0 && this.clock() - start < budgetMs) {
            const job = this.uploads.shift();
            if (job.generation !== this.generation) {
                continue;
            }
            touched = true;
            const chunks = [];
            try {
                for (const decoded of job.decoded) {
                    chunks.push(this.renderer.createChunk(decoded, this.infoOf(job.layer)));
                }
            } catch (error) {
                for (const c of chunks) {
                    this.renderer.destroyChunk(c);
                }
                this.failedAt.set(job.key, this.clock());
                this.setStatus(job.key, job.layer, job.map, job.x, job.y, { state: 'Error', detail: `GPU upload failed: ${error.message}`, fetched: true });
                this.noteError(`${job.layer} ${job.map}/${job.x}/${job.y}: GPU upload failed: ${error.message}`);
                continue;
            }
            this.entries.set(job.key, { layer: job.layer, x: job.x, y: job.y, chunks, lastWanted: this.clock() });
        }
        if (touched && this.uploads.length === 0) {
            this.#evict(this.clock());
        }
        if (touched) {
            this.emit('change');
        }
        return this.uploads.length > 0;
    }

    #evict(now) {
        const keep = (entry) => {
            if (entry.x === GLOBAL_INDEX && entry.y === GLOBAL_INDEX) {
                return true;
            }
            if (!this.focus) {
                return true;
            }
            const d = Math.max(Math.abs(entry.x - this.focus.x), Math.abs(entry.y - this.focus.y));
            return d <= this.radius + 1 && inGrid(entry.x, entry.y);
        };
        for (const [key, entry] of this.entries) {
            if (!this.wantedKeys.has(key) && !keep(entry) && now - entry.lastWanted > 500) {
                this.#drop(key, entry);
            }
        }
        let bytes = this.renderer.stats.gpuBytes;
        if (bytes > MAX_GPU_BYTES) {
            const idle = [...this.entries].filter(([key]) => !this.wantedKeys.has(key)).sort((a, b) => a[1].lastWanted - b[1].lastWanted);
            for (const [key, entry] of idle) {
                if (bytes <= MAX_GPU_BYTES * 0.8) {
                    break;
                }
                bytes -= entry.chunks.reduce((n, c) => n + c.bytes, 0);
                this.#drop(key, entry);
            }
        }
    }

    #drop(key, entry) {
        for (const chunk of entry.chunks) {
            this.renderer.destroyChunk(chunk);
        }
        this.entries.delete(key);
        const status = this.statuses.get(key);
        if (status) {
            // Forget the fetch so that coming back refetches (the status of an unloaded tile is not "known").
            this.statuses.delete(key);
        }
    }

    /** Loaded GPU chunks of one layer on the current map. */
    chunksFor(layerId) {
        const out = [];
        for (const entry of this.entries.values()) {
            if (entry.layer === layerId) {
                out.push(...entry.chunks);
            }
        }
        return out;
    }

    /** Every loaded chunk. */
    allChunks() {
        const out = [];
        for (const entry of this.entries.values()) {
            out.push(...entry.chunks);
        }
        return out;
    }

    statusOf(layer, x, y) {
        return this.statuses.get(tileKey(layer, this.map, x, y)) ?? null;
    }

    /** Counts of tile states for one layer over the window ring (plus the global tile when it has a real status). */
    countsFor(layerId) {
        const counts = { Ok: 0, Empty: 0, Missing: 0, Rejected: 0, Unknown: 0, NotApplicable: 0, Error: 0, total: 0 };
        if (!this.focus) {
            return counts;
        }
        for (const t of wantedTiles(this.focus, this.radius)) {
            const s = this.statuses.get(tileKey(layerId, this.map, t.x, t.y));
            counts[s ? s.state : 'Unknown']++;
            counts.total++;
        }
        const g = this.statuses.get(tileKey(layerId, this.map, GLOBAL_INDEX, GLOBAL_INDEX));
        if (g && (g.state === 'Ok' || g.state === 'Rejected' || g.state === 'Error')) {
            counts[g.state]++;
            counts.total++;
        }
        return counts;
    }

    /** Totals for the status bar over the layers given. */
    totals(layerIds) {
        const t = { loaded: 0, missing: 0, rejected: 0, empty: 0, errors: 0, triangles: 0, lines: 0, loading: this.inflight.size + this.queue.length + this.uploads.length };
        for (const id of layerIds) {
            const c = this.countsFor(id);
            t.loaded += c.Ok;
            t.missing += c.Missing;
            t.rejected += c.Rejected;
            t.empty += c.Empty;
            t.errors += c.Error;
            for (const chunk of this.chunksFor(id)) {
                t.triangles += chunk.triangleCount;
                t.lines += chunk.lineCount;
            }
        }
        return t;
    }

    get busy() {
        return this.inflight.size > 0 || this.queue.length > 0 || this.uploads.length > 0;
    }

    /** Resolves when nothing is loading, or after timeoutMs. */
    idle(timeoutMs = 10_000) {
        if (!this.busy) {
            return Promise.resolve(true);
        }
        return new Promise((resolve) => {
            const timer = setTimeout(() => {
                off();
                resolve(false);
            }, timeoutMs);
            const off = this.on('change', () => {
                if (!this.busy) {
                    clearTimeout(timer);
                    off();
                    resolve(true);
                }
            });
        });
    }

    /** Union of the bounds of loaded chunks (optionally one layer), or null. */
    loadedBounds(layerId) {
        let lo = null;
        let hi = null;
        for (const entry of this.entries.values()) {
            if (layerId && entry.layer !== layerId) {
                continue;
            }
            for (const c of entry.chunks) {
                if (!c.bounds) {
                    continue;
                }
                lo = lo ? lo.map((v, i) => Math.min(v, c.bounds.min[i])) : [...c.bounds.min];
                hi = hi ? hi.map((v, i) => Math.max(v, c.bounds.max[i])) : [...c.bounds.max];
            }
        }
        return lo ? { min: lo, max: hi } : null;
    }
}
