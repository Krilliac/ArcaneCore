// The Worker pool of the client-file reader: jobs {id, op, ...payload} go to module Workers, replies {id, ok, result} or
// {id, ok: false, error: {code, message}} come back. A job that outlives its limit (5 s for a file, 30 s for a terrain tile)
// gets its Worker terminated and replaced, and its key is marked bad for the rest of the session, so a hostile or damaged
// file costs one timeout, not a hang (design 0.5).

import { ClientFsError, LIMITS } from './limits.js';

/** min(4, hardwareConcurrency - 1), at least 1. */
export function poolSizeFor(hardwareConcurrency) {
    const cores = Number.isFinite(hardwareConcurrency) ? hardwareConcurrency : 2;
    return Math.max(1, Math.min(4, cores - 1));
}

export class WorkerPool {
    #size;
    #spawn;
    #fileTimeoutMs;
    #tileTimeoutMs;
    #workers = [];
    #queue = [];
    #bad = new Set();
    #nextId = 1;
    #spawned = 0;
    #archiveReads = 0;

    /**
     * @param {object} options
     * @param {number} options.size number of Workers
     * @param {() => Worker} options.spawn creates a Worker
     * @param {number} [options.fileTimeoutMs]
     * @param {number} [options.tileTimeoutMs]
     */
    constructor({ size, spawn, fileTimeoutMs = LIMITS.fileTimeoutMs, tileTimeoutMs = LIMITS.tileTimeoutMs }) {
        this.#size = Math.max(1, size);
        this.#spawn = spawn;
        this.#fileTimeoutMs = fileTimeoutMs;
        this.#tileTimeoutMs = tileTimeoutMs;
    }

    /** Number of Workers created so far (replacements included). */
    get spawned() {
        return this.#spawned;
    }

    /** Archive range reads reported by the Workers' replies. */
    get archiveReads() {
        return this.#archiveReads;
    }

    isBad(key) {
        return this.#bad.has(key);
    }

    /**
     * @param {string} op
     * @param {object} payload
     * @param {{kind?: 'file'|'tile', key?: string}} [options] kind picks the timeout; key names the file for the bad list
     */
    async run(op, payload, { kind = 'file', key = null } = {}) {
        if (key !== null && this.#bad.has(key)) {
            throw new ClientFsError('bad-file', `${key} timed out earlier in this session and is not tried again.`);
        }
        return new Promise((resolve, reject) => {
            this.#queue.push({ op, payload, key, limit: kind === 'tile' ? this.#tileTimeoutMs : this.#fileTimeoutMs, resolve, reject });
            this.#pump();
        });
    }

    /** Terminates every Worker; pending jobs are rejected. */
    close() {
        for (const slot of this.#workers) {
            slot.worker.terminate();
            slot.job?.reject(new ClientFsError('timeout', 'The Worker pool was closed.'));
            clearTimeout(slot.job?.timer);
        }
        this.#workers = [];
        for (const job of this.#queue.splice(0)) {
            job.reject(new ClientFsError('timeout', 'The Worker pool was closed.'));
        }
    }

    #create() {
        const worker = this.#spawn();
        this.#spawned++;
        const slot = { worker, job: null };
        worker.onmessage = (event) => this.#reply(slot, event.data);
        worker.onerror = (event) => this.#crash(slot, event?.message ?? 'the Worker failed');
        this.#workers.push(slot);
        return slot;
    }

    #pump() {
        while (this.#queue.length > 0) {
            let slot = this.#workers.find((s) => s.job === null);
            if (slot === undefined && this.#workers.length < this.#size) {
                slot = this.#create();
            }
            if (slot === undefined) {
                return;
            }
            const job = this.#queue.shift();
            job.id = this.#nextId++;
            slot.job = job;
            job.timer = setTimeout(() => this.#timeout(slot, job), job.limit);
            slot.worker.postMessage({ id: job.id, op: job.op, ...job.payload });
        }
    }

    #reply(slot, data) {
        const job = slot.job;
        if (job === null || data?.id !== job.id) {
            return;
        }
        clearTimeout(job.timer);
        slot.job = null;
        if (typeof data.reads === 'number') {
            this.#archiveReads += data.reads;
        }
        if (data.ok) {
            job.resolve(data.result);
        } else {
            job.reject(new ClientFsError(data.error?.code ?? 'bad-file', data.error?.message ?? 'The Worker failed.'));
        }
        this.#pump();
    }

    #replace(slot) {
        slot.worker.terminate();
        const index = this.#workers.indexOf(slot);
        if (index >= 0) {
            this.#workers.splice(index, 1);
        }
        this.#create();
    }

    #timeout(slot, job) {
        if (slot.job !== job) {
            return;
        }
        slot.job = null;
        if (job.key !== null) {
            this.#bad.add(job.key);
        }
        this.#replace(slot);
        job.reject(new ClientFsError('timeout', `The Worker did not answer '${job.op}' within ${job.limit} ms.`));
        this.#pump();
    }

    #crash(slot, message) {
        const job = slot.job;
        if (job === null) {
            return;
        }
        clearTimeout(job.timer);
        slot.job = null;
        if (job.key !== null) {
            this.#bad.add(job.key);
        }
        this.#replace(slot);
        job.reject(new ClientFsError('bad-file', `The Worker stopped while reading: ${message}`));
        this.#pump();
    }
}
