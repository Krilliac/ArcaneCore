// Application state and the wiring between the host (REST + WebSocket) and the panels. The server owns the view:
// local edits are applied immediately for responsiveness, sent as `view.set` merge patches throttled to about 10 Hz,
// and every `view` event that did not originate from this connection (`ui:<connection>`) replaces the local copy.

import { Api, ApiError, Socket, unwrap } from './api.js';
import { normalizeLayerList } from './catalog.js';
import { cameraPatch, defaultView, normalizeView } from './view.js';
import { resolveLayers } from './layerstate.js';
import { LiveModel } from './live.js';
import { REPLAY_FRAME_TOPIC, REPLAY_STATE_TOPIC, acceptsLiveFrame, acceptsReplayFrame, createReplayState, noteReplayFrame, reduceReplayState } from './insight/replay.js';
import { normalizeBookmarks, normalizeCommands, normalizeMaps, normalizeStatus } from './normalize.js';
import { OverlayStore, colorKeyTable } from './overlays.js';
import { tileOf } from './tiles.js';
import { Emitter, composePatch, deepEqual, throttle, mergePatch } from './util.js';

/** Commands whose result cannot have changed the shared view, so no view refresh follows them. */
const VIEW_NEUTRAL = /^(view\.get|status|help|maps\.list|layers\.list|bookmarks\.list|tiles\.status|query\.|ref\.query\.|live\.(status|entities|query)|cache\.|export\.)/;

export class App {
    constructor({ api, socket }) {
        this.api = api;
        this.socket = socket;
        this.events = new Emitter();
        this.view = defaultView();
        this.viewVersion = 0;
        this.layers = normalizeLayerList(null);
        this.commands = [];
        this.status = normalizeStatus(null);
        this.maps = [];
        this.bookmarks = [];
        this.connection = { state: 'connecting', id: null, version: null, message: '' };
        this.live = new LiveModel();
        this.replay = createReplayState();
        this.overlays = new OverlayStore();
        this.scene = null;
        this.selection = { hit: null, entityGuid: null, point: null, tile: null };
        this.clicks = [];
        this.hover = null;
        this.lastCommand = null;
        this.interacting = false;
        this.captureHandler = null;
        this.stats = {
            fps: 0, triangles: 0, lines: 0, drawCalls: 0, gpuBytes: 0,
            tiles: { loaded: 0, missing: 0, rejected: 0, empty: 0, errors: 0, triangles: 0, lines: 0, loading: 0 },
        };
        this.webgl = { ok: true, message: '' };
        this._pending = null;
        this._staleRemote = false;
        this._flushPatch = throttle(() => this.#flush(), 100);
        this._resolved = null;
        this.stopped = false;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Startup and connection

    start() {
        const s = this.socket;
        s.on('state', (state) => {
            if (this.stopped) {
                return;
            }
            this.connection = { ...this.connection, state, id: s.connection };
            if (state !== 'open') {
                this.connection.message = state === 'unauthorized' ? 'The host refused the token.' : state === 'closed' ? 'Reconnecting to the host.' : 'Connecting.';
            }
            this.events.emit('connection', this.connection);
        });
        s.on('error', (message) => {
            this.connection = { ...this.connection, message };
            this.events.emit('connection', this.connection);
            if (this.connection.state !== 'unauthorized') {
                this.toast(message, 'warn');
            }
        });
        s.on('hello', (m) => this.#onHello(m));
        s.on('event', (topic, data) => this.#onEvent(topic, data));
        s.on('capture', (m) => this.captureHandler?.(m));
        this.api.on('unauthorized', () => {
            if (this.stopped) {
                return;
            }
            this.connection = { ...this.connection, state: 'unauthorized', message: 'The host refused the token.' };
            this.events.emit('connection', this.connection);
        });
        s.start();
    }

    /**
     * Ends this app for good (the host refused its token): closes the socket, drops a pending view patch and stops
     * announcing connection changes, so a request still in flight cannot reopen the token gate over a newer app.
     */
    stop() {
        this.stopped = true;
        this._pending = null;
        this.socket.stop();
    }

    async #onHello(m) {
        this.connection = { state: 'open', id: m.connection ?? null, version: m.version ?? null, message: '' };
        this.viewVersion = Number.isFinite(m.viewVersion) ? m.viewVersion : this.viewVersion;
        if (m.view) {
            this.#replaceView(normalizeView(m.view, this.view), 'hello');
        }
        this.events.emit('connection', this.connection);
        await Promise.allSettled([this.refreshCommands(), this.refreshStatus(), this.refreshLayers(), this.refreshMaps(), this.refreshBookmarks()]);
    }

    #onEvent(topic, data) {
        switch (topic) {
            case 'view':
                this.#onViewEvent(data);
                break;
            case 'command':
                this.#onCommandEvent(data);
                break;
            case 'live.frame':
                if (acceptsLiveFrame(this.replay)) {
                    this.live.applyFrame(data, performance.now());
                }
                break;
            case REPLAY_STATE_TOPIC:
                this.replay = reduceReplayState(this.replay, data);
                this.events.emit('replay', this.replay);
                break;
            case REPLAY_FRAME_TOPIC:
                if (acceptsReplayFrame(this.replay)) {
                    this.replay = noteReplayFrame(this.replay, data);
                    this.live.applyFrame(data, performance.now());
                    this.events.emit('replay', this.replay);
                }
                break;
            case 'live.status':
                this.live.applyStatus(data);
                break;
            case 'tile.invalidated':
                if (data && typeof data.layer === 'string' && Array.isArray(data.tile)) {
                    this.scene?.invalidate(data.layer, data.tile[0], data.tile[1], data.tile[2]);
                }
                break;
            default:
                this.events.emit('event', topic, data);
        }
    }

    #onViewEvent(data) {
        if (!data || typeof data !== 'object') {
            return;
        }
        const version = Number(data.version);
        if (Number.isFinite(version)) {
            if (version <= this.viewVersion) {
                return;
            }
            this.viewVersion = version;
        }
        if (data.origin && data.origin === this.socket.origin) {
            return;
        }
        if (this.interacting || this._pending) {
            this._staleRemote = true;
            return;
        }
        this.#replaceView(normalizeView(data.view, this.view), 'remote');
    }

    #onCommandEvent(data) {
        if (!data || typeof data !== 'object' || typeof data.command !== 'string') {
            return;
        }
        const own = data.origin && data.origin === this.socket.origin;
        if (!(own && data.command === 'view.set')) {
            this.lastCommand = {
                name: data.command,
                ok: data.ok === true,
                origin: String(data.origin ?? ''),
                elapsedMs: Number(data.elapsedMs),
                message: data.ok === true ? '' : String(data.error?.message ?? ''),
                at: Date.now(),
            };
            this.events.emit('lastCommand', this.lastCommand);
        }
        if (!own && data.ok === true && data.data) {
            if (this.overlays.ingest(data.command, data.data, colorKeyTable(this.layers))) {
                this.events.emit('overlays');
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Loading lists

    async refreshCommands() {
        try {
            this.commands = normalizeCommands(await this.api.commandList());
            this.events.emit('commands', this.commands);
        } catch (error) {
            this.#loadFailed('the command list', error);
        }
    }

    async refreshStatus() {
        try {
            this.status = normalizeStatus(await this.api.status());
            this.events.emit('status', this.status);
        } catch (error) {
            this.#loadFailed('the host status', error);
        }
    }

    async refreshLayers() {
        const result = await this.runCommand('layers.list', {}, { quiet: true });
        this.layers = normalizeLayerList(result.ok ? result.data : null);
        if (!result.ok) {
            this.toast(`Layer details unavailable (${result.error?.message ?? 'layers.list failed'}); using built-in defaults.`, 'warn');
        }
        this._resolved = null;
        this.events.emit('layers', this.layers);
    }

    async refreshMaps() {
        const result = await this.runCommand('maps.list', {}, { quiet: true });
        this.maps = result.ok ? normalizeMaps(result.data) : [];
        this.events.emit('maps', this.maps);
    }

    async refreshBookmarks() {
        const result = await this.runCommand('bookmarks.list', {}, { quiet: true });
        this.bookmarks = result.ok ? normalizeBookmarks(result.data) : [];
        this.events.emit('bookmarks', this.bookmarks);
    }

    #loadFailed(what, error) {
        if (error?.code !== 'unauthorized') {
            this.toast(`Could not load ${what}: ${error.message}`, 'warn');
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // View

    /** Layers resolved against the current view and compare mode (cached until either changes). */
    resolved() {
        if (!this._resolved || this._resolved.view !== this.view || this._resolved.layers !== this.layers) {
            this._resolved = { view: this.view, layers: this.layers, list: resolveLayers(this.view, this.layers) };
        }
        return this._resolved.list;
    }

    layerInfo(id) {
        return this.layers.find((l) => l.id === id) ?? null;
    }

    #replaceView(next, source) {
        const changed = new Set();
        for (const key of new Set([...Object.keys(this.view), ...Object.keys(next)])) {
            if (!deepEqual(this.view[key], next[key])) {
                changed.add(key);
            }
        }
        this.view = next;
        if (changed.size > 0) {
            this.events.emit('view', this.view, changed, source);
        }
    }

    /** Applies a merge patch locally at once and queues it for the host (about 10 Hz; `immediate` flushes now). */
    patchView(patch, { immediate = false } = {}) {
        this.#replaceView(normalizeView(mergePatch(this.view, patch), this.view), 'local');
        this._pending = this._pending ? composePatch(this._pending, patch) : patch;
        this._flushPatch(this);
        if (immediate) {
            this._flushPatch.flush();
        }
    }

    setCamera(camera, options) {
        this.patchView(cameraPatch(camera), options);
    }

    /** Marks the start of a continuous gesture (drag, key flight): remote view events wait until it ends. */
    beginInteraction() {
        this.interacting = true;
    }

    /** Ends a gesture: sends the final patch now and catches up on any remote change that arrived meanwhile. */
    async endInteraction() {
        this.interacting = false;
        this._flushPatch.flush();
        if (this._staleRemote) {
            this._staleRemote = false;
            await this.refreshView();
        }
    }

    async #flush() {
        const patch = this._pending;
        this._pending = null;
        if (!patch) {
            return;
        }
        const result = await this.runCommand('view.set', patch, { quiet: true, noRefresh: true });
        if (!result.ok) {
            this.toast(`The host refused a view change: ${result.error?.message ?? 'view.set failed'}`, 'warn');
            await this.refreshView();
        } else if (this._staleRemote && !this.interacting && !this._pending) {
            this._staleRemote = false;
            await this.refreshView();
        }
    }

    async refreshView() {
        const result = await this.runCommand('view.get', {}, { quiet: true, noRefresh: true });
        if (result.ok) {
            const body = result.data?.view ?? result.data;
            if (Number.isFinite(result.data?.version)) {
                this.viewVersion = Math.max(this.viewVersion, result.data.version);
            }
            this.#replaceView(normalizeView(body, this.view), 'refresh');
        }
    }

    setLayer(id, fields, options) {
        this.patchView({ layers: { [id]: fields } }, options);
    }

    setGroupVisible(layerIds, visible) {
        const layers = {};
        for (const id of layerIds) {
            layers[id] = { visible };
        }
        this.patchView({ layers }, { immediate: true });
    }

    /** The map's tile under the camera focus (the eye in fly mode, else the target), or null. */
    focusTile() {
        const c = this.view.camera;
        return tileOf(c.target[0], c.target[1]);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Commands

    /**
     * Runs a registry command: over the WebSocket when open (origin ui:<connection>), else POST /api/command. Always
     * resolves with the CommandResult JSON; failures become a toast unless `quiet`. Overlays in the result are kept.
     */
    async runCommand(name, args = {}, { quiet = false, noRefresh = false } = {}) {
        let result;
        try {
            if (this.socket.state === 'open') {
                try {
                    result = await this.socket.command(name, args);
                } catch (error) {
                    if (error?.code !== 'closed') {
                        throw error;
                    }
                    result = await this.api.command(name, args);
                }
            } else {
                result = await this.api.command(name, args);
            }
        } catch (error) {
            result = { command: name, ok: false, error: { code: error instanceof ApiError ? error.code ?? 'network' : 'failed', message: error.message }, elapsedMs: 0 };
        }
        if (result.ok) {
            if (this.overlays.ingest(name, result.data, colorKeyTable(this.layers))) {
                this.events.emit('overlays');
            }
        } else if (!quiet) {
            this.toast(`${name}: ${result.error?.message ?? 'failed'}`, 'error');
        }
        this.events.emit('command-result', { name, args, result });
        if (!noRefresh && !VIEW_NEUTRAL.test(name)) {
            this.refreshView().catch(() => {});
        }
        return result;
    }

    /** runCommand that returns the data or throws CommandError (for flows that need the value). */
    async commandData(name, args, options) {
        return unwrap(await this.runCommand(name, args, { quiet: true, ...options }));
    }

    hasCommand(name) {
        return this.commands.some((c) => c.name === name);
    }

    commandNamed(name) {
        return this.commands.find((c) => c.name === name) ?? null;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Selection and notices

    setHover(hover) {
        this.hover = hover;
        this.events.emit('hover', hover);
    }

    /** Records a click: remembers the point for probe forms (latest first, two kept). */
    recordClick(point) {
        this.clicks = [point, ...this.clicks].slice(0, 2);
        this.events.emit('clicks', this.clicks);
    }

    select(selection) {
        this.selection = { hit: null, entityGuid: null, point: null, tile: null, ...selection };
        this.events.emit('selection', this.selection);
    }

    toast(message, kind = 'info') {
        this.events.emit('toast', { message, kind });
    }
}

export function createApp(token, locationLike) {
    const api = new Api(token);
    const socket = new Socket(token, Socket.urlFor(locationLike));
    return new App({ api, socket });
}
