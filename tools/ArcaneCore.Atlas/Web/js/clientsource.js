// Where the Local page gets textured terrain from. The host answers `ref.terrain.tex` tiles and `/api/bin/texture` keys itself when it
// can; when it cannot (it answers NotApplicable, Missing or does not know the layer), the browser-side client-file reader
// (clientfs/index.js) builds the tile and the baked terrain textures from the visitor's own WoW 1.12.1 folder, in Workers, and
// nothing leaves the browser. The reader module is imported on first need, so a page that never asks loads none of it.
//
// Thread affinity: page thread. `loadReader` and `schedule` are injectable for tests.

import { ApiError } from './api.js';

export const TEXTURED_TERRAIN_LAYER = 'ref.terrain.tex';
export const CLIENT_TEXTURE_PREFIX = 'cft:';

const EMPTY_ENVELOPE = () => new Uint8Array(4).buffer; // u32 partCount = 0

const isOk = (state) => state === 'Ok' || state === 'Empty';

/** How often, and for how long, the folder choice is watched after the settings were opened. */
const WATCH_INTERVAL_MS = 1500;
const WATCH_LIMIT = 400;

export class ClientFilesSource {
    #api;
    #loadReader;
    #reader = null;
    #onChange;
    #schedule;
    #watching = false;

    /**
     * @param {object} options
     * @param {object} options.api the host API (tile, texture)
     * @param {() => Promise<object>} [options.loadReader] imports the reader module
     * @param {() => void} [options.onChange] called when the visitor's folder choice changed, so tiles the host and the reader both refused can be asked again
     * @param {(fn: () => void, ms: number) => void} [options.schedule] a timer (setTimeout)
     */
    constructor({ api, loadReader = () => import('./clientfs/index.js'), onChange = () => {}, schedule = (fn, ms) => setTimeout(fn, ms) }) {
        this.#api = api;
        this.#loadReader = loadReader;
        this.#onChange = onChange;
        this.#schedule = schedule;
    }

    async #readerModule() {
        this.#reader ??= this.#loadReader().catch((error) => {
            this.#reader = null;
            throw error;
        });
        return this.#reader;
    }

    /** Opens the settings panel of the reader (Change folder, Forget folder, cache budget). */
    async openSettings() {
        const reader = await this.#readerModule();
        const before = await this.#signature(reader);
        await reader.openClientFilesSettings();
        this.#watch(reader, before);
    }

    async #signature(reader) {
        const status = await reader.clientFilesStatus();
        return `${status.state}|${status.folderName}`;
    }

    /** The reader has no change event: poll its status until the folder choice differs from `before`, then tell the page once. */
    #watch(reader, before) {
        if (this.#watching) {
            return;
        }
        this.#watching = true;
        let polls = 0;
        const poll = async () => {
            polls++;
            let changed = false;
            try {
                changed = (await this.#signature(reader)) !== before;
            } catch {
                changed = false; // a status read that fails is retried on the next tick
            }
            if (changed) {
                this.#watching = false;
                this.#onChange();
            } else if (polls >= WATCH_LIMIT) {
                this.#watching = false;
            } else {
                this.#schedule(poll, WATCH_INTERVAL_MS);
            }
        };
        this.#schedule(poll, WATCH_INTERVAL_MS);
    }

    /** Api.tile-shaped: host first; for the textured terrain layer the visitor's files answer when the host has none. */
    async tile(layer, map, x, y, signal) {
        if (layer !== TEXTURED_TERRAIN_LAYER) {
            return this.#api.tile(layer, map, x, y, signal);
        }
        let hostAnswer = null;
        try {
            hostAnswer = await this.#api.tile(layer, map, x, y, signal);
        } catch (error) {
            if (!(error instanceof ApiError) || error.status !== 404) {
                throw error;
            }
        }
        if (hostAnswer !== null && isOk(hostAnswer.state)) {
            return hostAnswer;
        }
        const reader = await this.#readerModule();
        const local = await reader.localTileProvider().tile(layer, map, x, y, signal);
        if (local.state === 'NotApplicable' && hostAnswer !== null) {
            return hostAnswer; // no files chosen: the host's own reason is the better explanation
        }
        return local.state === 'NotApplicable' ? { state: 'NotApplicable', detail: local.detail, body: EMPTY_ENVELOPE() } : local;
    }

    /** Api.texture-shaped: baked terrain textures come from the reader, every other key from the host. */
    async texture(key, options = {}) {
        if (!key.startsWith(CLIENT_TEXTURE_PREFIX)) {
            return this.#api.texture(key, options);
        }
        const reader = await this.#readerModule();
        return reader.localTextureProvider().texture(key, options);
    }
}
