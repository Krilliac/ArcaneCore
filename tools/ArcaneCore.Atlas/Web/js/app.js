// Application state and the wiring between the host (REST + WebSocket) and the panels. The server owns the view:
// local edits are applied immediately for responsiveness, sent as `view.set` merge patches throttled to about 10 Hz,
// and every `view` event that did not originate from this connection (`ui:<connection>`) replaces the local copy.
// Several UIs may be open at once and each flies its own camera. A `view` event from another UI (`ui:<other>`) updates the
// shared parts (map, layers, compare mode, ...) but neither this UI's camera nor its follow target (follow drives the
// camera). An event from anything else (cli, mcp, script, http) carries `cameraChanged`; only when it is true (or missing,
// an older host) does it move the camera and follow. The live feed moves the camera of a followed entity every frame (origin
// `live`); `followOrigin` says who asked for the follow, and only that UI (or any non-UI asker) takes those frames. `refreshView` keeps the local camera unless asked to take the host's,
// which happens after this UI ran a camera command. The follow is shared with ownership: the host holds one, and `followOrigin` names the
// UI (or the CLI, MCP, a script) that owns it. A UI that does not own it keeps no local follow and never sends `follow: null`. Each UI also publishes where it is (`presence`, at most about 5 Hz,
// only when it moved) and keeps the other sessions in `app.presence`, which the viewport draws as labelled markers.

import { Api, ApiError, Socket, unwrap } from './api.js';
import { normalizeLayerList } from './catalog.js';
import { cameraPatch, defaultView, normalizeView } from './view.js';
import { resolveLayers } from './layerstate.js';
import { LiveModel } from './live.js';
import { REPLAY_FRAME_TOPIC, REPLAY_STATE_TOPIC, acceptsLiveFrame, acceptsReplayFrame, createReplayState, noteReplayFrame, reduceReplayState } from './insight/replay.js';
import { normalizeBookmarks, normalizeCommands, normalizeMaps, normalizeStatus } from './normalize.js';
import { OverlayStore, colorKeyTable } from './overlays.js';
import { PresenceStore, cleanName, presenceMessage } from './presence.js';
import { tileOf } from './tiles.js';
import { Emitter, composePatch, deepEqual, throttle, mergePatch } from './util.js';

/** sessionStorage slot of the user-chosen session name (it lasts for this tab; the web assets keep no persistent storage). */
const NAME_SLOT = 'atlas.name';

/** Minimum spacing of presence messages; the host drops closer ones (50 ms), so this stays well clear of that. */
const PRESENCE_MS = 200;

/** Host commands that move the camera (every one goes through `ViewCommands`), so the view refresh after them takes the host's camera. */
const CAMERA_COMMANDS = /^(camera\.(goto|frame)|bookmark\.go|live\.follow)$/;

/** True when running this command from this UI moves the host's camera or follow: a camera command, or `view.set` with a camera or a follow. */
function movesCamera(name, args) {
    return CAMERA_COMMANDS.test(name)
        || (name === 'view.set' && args !== null && typeof args === 'object' && ('camera' in args || 'follow' in args));
}

function readStoredName() {
    try {
        return typeof sessionStorage === 'undefined' ? '' : (sessionStorage.getItem(NAME_SLOT) ?? '');
    } catch {
        return '';
    }
}

function writeStoredName(name) {
    try {
        if (typeof sessionStorage !== 'undefined') {
            sessionStorage.setItem(NAME_SLOT, name);
        }
    } catch {
        // Storage can be blocked; the name then only lasts for this page.
    }
}

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
        this._staleCamera = false;
        /** Counts view events (and hellos) that took the shared camera, so a view.get result can tell whether one landed since its request. */
        this._cameraTakes = 0;
        this._peerFollow = false;
        this._helloing = false;
        this._lastConnection = null;
        this.presence = new PresenceStore();
        this.defaultName = '';
        this.sessionName = cleanName(readStoredName());
        this._lastPresence = '';
        this._publishPresence = throttle(() => this.#publishPresence(), PRESENCE_MS);
        this.events.on('view', (view, changed) => {
            if (!this._helloing && (changed.has('camera') || changed.has('map'))) {
                this._publishPresence();
            }
        });
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
                this.#clearPresence();
            }
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
        this._publishPresence.cancel();
        this.socket.stop();
    }

    async #onHello(m) {
        this.connection = { state: 'open', id: m.connection ?? null, version: m.version ?? null, message: '' };
        this.defaultName = typeof m.name === 'string' ? m.name : '';
        this.presence.reset(m.connection ?? null, m.sessions ?? []);
        this.events.emit('presence');
        this.viewVersion = Number.isFinite(m.viewVersion) ? m.viewVersion : this.viewVersion;
        // The first hello takes the whole view. After a reconnect this UI keeps flying where it was: the host's camera and
        // follow are those of whoever moved last.
        const reconnect = this._lastConnection !== null;
        this._lastConnection = m.connection ?? '';
        // A follow owned by another UI is neither adopted nor allowed to drag this camera, on the first hello as on a reconnect.
        this._peerFollow = typeof m.followOrigin === 'string' && m.followOrigin.startsWith('ui:') && m.followOrigin !== `ui:${m.connection}`;
        this._cameraTakes++;
        this._helloing = true;
        try {
            if (m.view) {
                this.#replaceView(this.#mergeRemote(normalizeView(m.view, this.view), !reconnect), 'hello');
            }
        } finally {
            this._helloing = false;
        }
        // The old connection's departure released this UI's follow on the host: follow again, or drop the stale local follow.
        const hostFollow = m.view && typeof m.view.follow === 'string' ? m.view.follow : null;
        if (reconnect && this.view.follow && this.view.follow !== hostFollow) {
            const guid = this.view.follow;
            this.runCommand('live.follow', { guid }, { quiet: true }).then((result) => {
                if (!result?.ok && this.view.follow === guid) {
                    this.#replaceView({ ...this.view, follow: null }, 'refresh');
                }
            }).catch(() => {});
        }
        this.events.emit('connection', this.connection);
        this._lastPresence = '';
        this._publishPresence.cancel();
        this._publishPresence();
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
            case 'ui.presence':
                if (this.presence.apply(data)) {
                    this.events.emit('presence');
                }
                break;
            case 'ui.presence.left':
                if (data && typeof data.id === 'string' && this.presence.remove(data.id)) {
                    this.events.emit('presence');
                }
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
        // Who owns the follow: the live feed moves the camera of the followed entity every frame with origin `live`, which belongs
        // to whoever started the follow (`followOrigin`), not to the live feed. Noted before the own-origin return below.
        const followedByPeer = typeof data.followOrigin === 'string' && data.followOrigin.startsWith('ui:') && data.followOrigin !== this.socket.origin;
        this._peerFollow = followedByPeer;
        if (followedByPeer && this.view.follow) {
            // Another UI took the follow over: a follow kept here would be stale, freeze this camera and cancel theirs when it ended.
            this.#replaceView({ ...this.view, follow: null }, 'remote');
        }
        if (data.origin && data.origin === this.socket.origin) {
            return;
        }
        // Another UI never moves this camera, and neither do the live feed's frames while another UI follows. Anything else does,
        // unless the host says the change left the camera alone (an event without the flag comes from an older host, which cannot
        // tell, so it counts as a camera change).
        const fromPeerUi = typeof data.origin === 'string' && data.origin.startsWith('ui:') && data.origin !== this.socket.origin;
        const takeCamera = !fromPeerUi && !(data.origin === 'live' && followedByPeer) && data.cameraChanged !== false;
        if (takeCamera) {
            this._cameraTakes++;
        }
        if (this.interacting || this._pending) {
            this._staleRemote = true;
            this._staleCamera = this._staleCamera || takeCamera;
            return;
        }
        this.#replaceView(this.#mergeRemote(normalizeView(data.view, this.view), takeCamera), 'remote');
    }

    /** `next` with this UI's camera and follow kept, unless `takeCamera`; the follow of another UI is never adopted, and no follow is kept while another UI owns it. */
    #mergeRemote(next, takeCamera) {
        if (!takeCamera) {
            return { ...next, camera: this.view.camera, follow: this._peerFollow ? null : this.view.follow };
        }
        return this._peerFollow ? { ...next, follow: null } : next;
    }

    /** True when this UI has a follow that it owns (none is kept while another UI owns the host's follow). */
    ownsFollow() {
        return Boolean(this.view.follow) && !this._peerFollow;
    }

    /** Stops this UI's own follow (a drag or a key flight took the camera); sends nothing when another UI owns the host's follow. */
    releaseFollow() {
        if (this.ownsFollow()) {
            this.patchView({ follow: null });
        }
    }

    #clearPresence() {
        this._lastPresence = '';
        if (this.presence.peers().length > 0) {
            this.presence.reset(null, []);
            this.events.emit('presence');
        }
    }

    /** Sends this session's presence when it changed since the last send; false when nothing was sent. */
    #publishPresence() {
        if (this.stopped || this.socket.state !== 'open') {
            return false;
        }
        const message = presenceMessage(this.view, this.sessionName);
        const key = JSON.stringify(message);
        if (key === this._lastPresence) {
            return false;
        }
        if (this.socket.send(message)) {
            this._lastPresence = key;
            return true;
        }
        return false;
    }

    /**
     * Sets the display name other sessions see ('' falls back to the host's default) and publishes it with the next presence message.
     * The name is remembered for this tab unless `remember` is false (a join link's name applies to that join only).
     */
    setSessionName(name, { remember = true } = {}) {
        this.sessionName = cleanName(name);
        if (remember) {
            writeStoredName(this.sessionName);
        }
        this._publishPresence();
        this.events.emit('sessionName', this.sessionName);
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
        const next = normalizeView(mergePatch(this.view, patch), this.view);
        // A merge patch deletes a null member instead of setting it, so the normalizer would keep the old follow: stop it here.
        this.#replaceView(patch !== null && typeof patch === 'object' && patch.follow === null ? { ...next, follow: null } : next, 'local');
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
            await this.#catchUp();
        }
    }

    /** Fetches the host's view after remote changes arrived mid-gesture; the camera is taken only if a non-UI origin moved it. */
    async #catchUp() {
        const takeCamera = this._staleCamera;
        this._staleRemote = false;
        this._staleCamera = false;
        await this.refreshView({ takeCamera });
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
            await this.refreshView({ takeCamera: true });
        } else if (this._staleRemote && !this.interacting && !this._pending) {
            await this.#catchUp();
        }
    }

    /**
     * Re-reads the host's view. The host's camera and follow are those of whichever client moved last, so this UI keeps its
     * own unless `takeCamera` (after it ran a camera command, or when the host refused its patch).
     */
    async refreshView({ takeCamera = false } = {}) {
        const takesBefore = this._cameraTakes;
        const result = await this.runCommand('view.get', {}, { quiet: true, noRefresh: true });
        if (result.ok) {
            const body = result.data?.view ?? result.data;
            const version = Number(result.data?.version);
            if (Number.isFinite(version) && version < this.viewVersion) {
                // A result older than the last view event applied must not undo a newer follow, ownership or shared state. This UI's own
                // camera command is the exception for the camera alone: its camera never arrives as an event (own-origin events are
                // ignored), so it is still taken unless an event moved this camera after the request was sent.
                if (takeCamera && this._cameraTakes === takesBefore) {
                    this.#replaceView({ ...this.view, camera: normalizeView(body, this.view).camera }, 'refresh');
                }
                return;
            }
            // The host says who owns the follow it holds (`followOrigin`, present when something is followed), so a command result that
            // beats the view event cannot leave a stale peer-ownership flag that would drop this UI's own follow. A host that sends
            // no owner for a held follow (an older one) leaves the flag as the last event set it. A result older than the last view
            // event applied took only the camera above, so it cannot undo a newer follow or view state that arrived in the meantime.
            const held = typeof body?.follow === 'string' && body.follow.length > 0;
            const owner = result.data?.followOrigin;
            if (!held) {
                this._peerFollow = false;
            } else if (typeof owner === 'string' || owner === null) {
                this._peerFollow = typeof owner === 'string' && owner.startsWith('ui:') && owner !== this.socket.origin;
            }
            this.#replaceView(this.#mergeRemote(normalizeView(body, this.view), takeCamera), 'refresh');
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
        if (name === 'bookmark.add' && args !== null && typeof args === 'object' && !('map' in args) && !('camera' in args)) {
            // The host's camera is that of whoever moved last; a bookmark saves where this user is.
            args = { ...args, map: this.view.map, camera: cameraPatch(this.view.camera).camera };
        }
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
            this.refreshView({ takeCamera: movesCamera(name, args) }).catch(() => {});
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
