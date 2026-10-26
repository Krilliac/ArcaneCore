// Host API client (design doc section 13): REST with a bearer token, and the WebSocket (auth, hello, events,
// commands, capture requests). The token arrives in the page URL fragment (#t=...), is moved to sessionStorage and the
// fragment is cleared, so the token never appears in a request line or a log.

import { Emitter } from './util.js';

const SESSION_SLOT = 'atlas.session';

/**
 * Moves "#t=<token>" from the URL fragment into session storage and clears the fragment. Dependencies are injected so
 * the logic runs under node: {hash, storage:{getItem,setItem}, clearFragment()}. Returns the token in force, or null.
 */
export function adoptToken({ hash, storage, clearFragment }) {
    const match = /(?:^#|&)t=([A-Za-z0-9_-]+)/.exec(hash ?? '');
    if (match) {
        storage.setItem(SESSION_SLOT, match[1]);
        clearFragment();
        return match[1];
    }
    return storage.getItem(SESSION_SLOT);
}

/**
 * The display name a join link carries (`#t=<token>&name=Grok%20Bot`), decoded, or null when the fragment has none (or it is
 * empty or malformed). Pure; read it before `adoptToken` clears the fragment. The app cleans it like any typed name.
 */
export function fragmentName(hash) {
    const match = /(?:^#|&)name=([^&]*)/.exec(hash ?? '');
    if (!match) {
        return null;
    }
    try {
        const name = decodeURIComponent(match[1]);
        return name.trim().length > 0 ? name : null;
    } catch {
        return null;
    }
}

export function forgetToken(storage) {
    storage.removeItem(SESSION_SLOT);
}

export function rememberToken(storage, value) {
    storage.setItem(SESSION_SLOT, value);
}

export class ApiError extends Error {
    constructor(message, { status = 0, code = null } = {}) {
        super(message);
        this.name = 'ApiError';
        this.status = status;
        this.code = code;
    }
}

/** A failed command (ok:false) as an exception: `code` is bad-args | unknown-command | unavailable | failed. */
export class CommandError extends Error {
    constructor(command, code, message) {
        super(message);
        this.name = 'CommandError';
        this.command = command;
        this.code = code;
    }
}

/** Throws CommandError unless a CommandResult JSON is ok; returns its data. */
export function unwrap(result) {
    if (result && result.ok === true) {
        return result.data;
    }
    throw new CommandError(result?.command ?? '?', result?.error?.code ?? 'failed', result?.error?.message ?? 'The command failed.');
}

function decodeHeader(value) {
    if (value === null || value === undefined) {
        return '';
    }
    try {
        return decodeURIComponent(value);
    } catch {
        return value;
    }
}

export class Api extends Emitter {
    #token;
    #fetch;

    constructor(token, fetchImpl = (...a) => fetch(...a)) {
        super();
        this.#token = token;
        this.#fetch = fetchImpl;
    }

    get token() {
        return this.#token;
    }

    async #request(path, init = {}) {
        const headers = { ...(init.headers ?? {}), Authorization: `Bearer ${this.#token}` };
        let response;
        try {
            response = await this.#fetch(path, { ...init, headers });
        } catch (error) {
            if (error?.name === 'AbortError') {
                throw error;
            }
            throw new ApiError(`Cannot reach the Atlas host: ${error.message}`, { code: 'network' });
        }
        if (response.status === 401) {
            this.emit('unauthorized');
            throw new ApiError('The Atlas host refused the token (401). Open the link printed by "arcane-atlas serve" again.', { status: 401, code: 'unauthorized' });
        }
        return response;
    }

    async json(path, init) {
        const response = await this.#request(path, init);
        if (!response.ok) {
            throw new ApiError(`${init?.method ?? 'GET'} ${path} answered ${response.status}.`, { status: response.status });
        }
        return response.json();
    }

    status() {
        return this.json('/api/status');
    }

    commandList() {
        return this.json('/api/commands');
    }

    /** POST /api/command; resolves with the CommandResult JSON (also for ok:false). */
    async command(name, args = {}, signal) {
        return this.json('/api/command', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name, args }),
            signal,
        });
    }

    /** Peeks tile states (no builds): {tiles:[{layer, tile:[map,x,y], state, detail, primitives}]}. */
    tiles(layer, map, x, y, radius, signal) {
        const query = new URLSearchParams({ layer, map: String(map), x: String(x), y: String(y), radius: String(radius) });
        return this.json(`/api/tiles?${query}`, { signal });
    }

    /**
     * GET /api/tile/<layer>/<map>/<x>/<y>: {state, detail, body}. `body` is the raw envelope (u32 partCount, then
     * u32 length + ATG1 bytes per part). Missing, rejected and empty tiles answer 200 with zero parts.
     */
    async tile(layer, map, x, y, signal) {
        const path = `/api/tile/${encodeURIComponent(layer)}/${map}/${x}/${y}`;
        const response = await this.#request(path, { signal });
        if (response.status === 404) {
            throw new ApiError(`Layer '${layer}' is unknown to the host.`, { status: 404, code: 'unknown-layer' });
        }
        if (!response.ok) {
            throw new ApiError(`GET ${path} answered ${response.status}.`, { status: response.status });
        }
        return {
            state: response.headers.get('X-Atlas-Tile-State') ?? 'Unknown',
            detail: decodeHeader(response.headers.get('X-Atlas-Tile-Detail')),
            body: await response.arrayBuffer(),
        };
    }

    /**
     * GET /api/bin/texture: one ATX1 texture as bytes. `fmt` is 'rgba' or 'dxt' (compressed blocks, only when the browser
     * advertises S3TC); `maxSize` (a power of two, 0 = none) drops the top mip levels host side. A key the host does not have
     * answers 404, which surfaces as an ApiError with that status.
     */
    async texture(key, { fmt = 'rgba', maxSize = 0, signal } = {}) {
        const query = new URLSearchParams({ key, fmt });
        if (maxSize > 0) {
            query.set('maxSize', String(maxSize));
        }
        const response = await this.#request(`/api/bin/texture?${query}`, { signal });
        if (!response.ok) {
            throw new ApiError(`GET /api/bin/texture answered ${response.status} for '${key}'.`, { status: response.status });
        }
        return new Uint8Array(await response.arrayBuffer());
    }

    async postCapture(id, blob) {
        const response = await this.#request(`/api/capture/${encodeURIComponent(id)}`, {
            method: 'POST',
            headers: { 'Content-Type': 'image/png' },
            body: blob,
        });
        if (!response.ok) {
            throw new ApiError(`The host refused the capture (${response.status}).`, { status: response.status });
        }
    }
}

const BACKOFF_MS = [400, 800, 1600, 3200, 6400];

/**
 * The WebSocket. Events: 'state' (connecting | open | closed | unauthorized), 'hello' (message), 'event' (topic, data),
 * 'capture' (message), 'error' (message). Reconnects with backoff until the host refuses the token (close code 4401).
 */
export class Socket extends Emitter {
    #token;
    #url;
    #factory;
    #ws = null;
    #state = 'closed';
    #attempt = 0;
    #timer = null;
    #nextId = 1;
    #pending = new Map();
    #stopped = false;
    #connection = null;

    constructor(token, url, factory = (u) => new WebSocket(u)) {
        super();
        this.#token = token;
        this.#url = url;
        this.#factory = factory;
    }

    static urlFor(locationLike) {
        const proto = locationLike.protocol === 'https:' ? 'wss:' : 'ws:';
        return `${proto}//${locationLike.host}/api/ws`;
    }

    get state() {
        return this.#state;
    }

    get connection() {
        return this.#connection;
    }

    get origin() {
        return this.#connection ? `ui:${this.#connection}` : null;
    }

    start() {
        this.#stopped = false;
        this.#connect();
    }

    stop() {
        this.#stopped = true;
        clearTimeout(this.#timer);
        this.#ws?.close(1000);
    }

    #setState(state) {
        if (this.#state !== state) {
            this.#state = state;
            this.emit('state', state);
        }
    }

    #connect() {
        this.#setState('connecting');
        let ws;
        try {
            ws = this.#factory(this.#url);
        } catch (error) {
            this.emit('error', `Cannot open the WebSocket: ${error.message}`);
            this.#scheduleReconnect();
            return;
        }
        this.#ws = ws;
        ws.onopen = () => {
            ws.send(JSON.stringify({ op: 'auth', token: this.#token }));
        };
        ws.onmessage = (event) => this.#onMessage(event.data);
        ws.onerror = () => {};
        ws.onclose = (event) => {
            if (this.#ws !== ws) {
                return;
            }
            this.#ws = null;
            this.#connection = null;
            this.#failPending('The connection to the Atlas host closed.');
            if (event.code === 4401) {
                this.#setState('unauthorized');
                this.emit('error', 'The Atlas host refused the token. Open the link printed by "arcane-atlas serve" again.');
                return;
            }
            this.#setState('closed');
            this.#scheduleReconnect();
        };
    }

    #scheduleReconnect() {
        if (this.#stopped) {
            return;
        }
        const wait = BACKOFF_MS[Math.min(this.#attempt, BACKOFF_MS.length - 1)];
        this.#attempt++;
        this.#timer = setTimeout(() => this.#connect(), wait);
    }

    #onMessage(data) {
        let message;
        try {
            message = JSON.parse(data);
        } catch {
            this.emit('error', 'The host sent a message that is not JSON.');
            return;
        }
        switch (message.op) {
            case 'hello':
                this.#attempt = 0;
                this.#connection = message.connection ?? null;
                this.#setState('open');
                this.emit('hello', message);
                break;
            case 'event':
                this.emit('event', message.topic, message.data);
                break;
            case 'result': {
                const pending = this.#pending.get(message.id);
                if (pending) {
                    this.#pending.delete(message.id);
                    pending.resolve(message.result);
                }
                break;
            }
            case 'capture':
                this.emit('capture', message);
                break;
            case 'error':
                this.emit('error', message.message ?? 'The host reported an error.');
                break;
            default:
                break;
        }
    }

    #failPending(reason) {
        for (const pending of this.#pending.values()) {
            pending.reject(new ApiError(reason, { code: 'closed' }));
        }
        this.#pending.clear();
    }

    /** Sends a JSON message; false when the socket is not open. */
    send(message) {
        if (this.#state !== 'open' || !this.#ws) {
            return false;
        }
        this.#ws.send(JSON.stringify(message));
        return true;
    }

    /** Runs a command over the socket (origin ui:<connection>). Rejects when it is not open. */
    command(name, args = {}, timeoutMs = 30_000) {
        if (this.#state !== 'open') {
            return Promise.reject(new ApiError('The WebSocket is not open.', { code: 'closed' }));
        }
        const id = this.#nextId++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.#pending.delete(id);
                reject(new ApiError(`${name} did not answer within ${Math.round(timeoutMs / 1000)} s.`, { code: 'timeout' }));
            }, timeoutMs);
            this.#pending.set(id, {
                resolve: (value) => {
                    clearTimeout(timer);
                    resolve(value);
                },
                reject: (error) => {
                    clearTimeout(timer);
                    reject(error);
                },
            });
            this.send({ op: 'command', id, name, args });
        });
    }

    sendCaptureFailed(id, message) {
        return this.send({ op: 'captureFailed', id, message });
    }
}
