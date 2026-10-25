// The public page's client of the public listener: one session cookie (HttpOnly, never readable here), the CSRF token the
// server hands out with /api/public/me and login, JSON over /api/public/*, and the live socket. Everything the server says
// about a visitor is whatever its projection allowed; this module reads no field the guest projection does not carry.

/** A failed request: `status` is the HTTP status, `code` the server's stable error code, `ref` the log reference to quote. */
export class PublicApiError extends Error {
    constructor(message, { status = 0, code = null, ref = null, retryAfter = null } = {}) {
        super(message);
        this.name = 'PublicApiError';
        this.status = status;
        this.code = code;
        this.ref = ref;
        /** Seconds the server asked to wait (Retry-After), or null. */
        this.retryAfter = retryAfter;
    }
}

/** A command that ran and answered `ok: false`: `code` and `message` are the server's fixed, generic ones. */
export class PublicCommandError extends Error {
    constructor(command, code, message) {
        super(message || 'The command failed.');
        this.name = 'PublicCommandError';
        this.command = command;
        this.code = code || 'failed';
    }
}

const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);

/** Unwraps a command result, returning its data or throwing the error it carries. */
export function unwrapCommand(result, command = '?') {
    if (!isObject(result) || result.ok !== true) {
        const error = isObject(result) && isObject(result.error) ? result.error : {};
        throw new PublicCommandError(isObject(result) && typeof result.command === 'string' ? result.command : command, error.code, error.message);
    }
    return result.data;
}

export class PublicApi {
    #fetch;

    /** @param {{fetchImpl?: Function}} options `fetchImpl` stands in for fetch (tests). */
    constructor({ fetchImpl } = {}) {
        this.#fetch = fetchImpl ?? ((...args) => globalThis.fetch(...args));
        this.tier = 'guest';
        this.account = null;
        this.security = 0;
        this.csrf = null;
        this.idleMinutes = 0;
        this.maxHours = 0;
    }

    async #request(method, route, body) {
        const headers = { Accept: 'application/json' };
        if (method === 'POST') {
            headers['Content-Type'] = 'application/json';
            if (this.csrf) {
                headers['X-Atlas-CSRF'] = this.csrf;
            }
        }
        let response;
        try {
            response = await this.#fetch(`/api/public/${route}`, {
                method,
                headers,
                credentials: 'same-origin',
                cache: 'no-store',
                body: body === undefined ? undefined : JSON.stringify(body),
            });
        } catch {
            throw new PublicApiError('The server could not be reached.', { code: 'unreachable' });
        }
        let json = null;
        try {
            json = await response.json();
        } catch {
            json = null;
        }
        if (!response.ok) {
            const error = isObject(json) && isObject(json.error) ? json.error : {};
            const header = Number(response.headers?.get?.('Retry-After'));
            throw new PublicApiError(`The request failed (${response.status}).`, {
                status: response.status,
                code: error.code ?? null,
                ref: error.ref ?? null,
                retryAfter: Number.isFinite(header) && header > 0 ? header : null,
            });
        }
        if (!isObject(json)) {
            throw new PublicApiError('The server sent an answer that is not JSON.', { status: response.status, code: 'bad-answer' });
        }
        return json;
    }

    #adopt(session) {
        this.tier = session.tier === 'account' ? 'account' : 'guest';
        this.account = typeof session.account === 'string' ? session.account : null;
        this.security = Number.isInteger(session.security) ? session.security : 0;
        this.csrf = typeof session.csrf === 'string' ? session.csrf : null;
        this.idleMinutes = Number.isFinite(session.idleMinutes) ? session.idleMinutes : 0;
        this.maxHours = Number.isFinite(session.maxHours) ? session.maxHours : 0;
        return session;
    }

    /** The server status (what a guest may know); the first call of a visit also creates the guest session. */
    status() {
        return this.#request('GET', 'status');
    }

    /** The current session and its CSRF token. */
    async me() {
        return this.#adopt(await this.#request('GET', 'me'));
    }

    /** Logs in. The server rotates the cookie and the token; the new token is adopted here. Failures are a PublicApiError with code `login-failed`. */
    async login(user, password, code) {
        const body = { user, password };
        if (code) {
            body.code = code;
        }
        return this.#adopt(await this.#request('POST', 'login', body));
    }

    /** Logs out into a fresh guest session. */
    async logout() {
        return this.#adopt(await this.#request('POST', 'logout'));
    }

    /** Runs a command and returns its data; an `ok: false` result throws a PublicCommandError. */
    async command(name, args = {}) {
        const result = await this.#request('POST', 'command', { name, args });
        return unwrapCommand(result, name);
    }

    maps() {
        return this.command('guest.maps');
    }

    layers() {
        return this.command('guest.layers');
    }

    bookmarks() {
        return this.command('bookmarks.list');
    }

    find(name, map) {
        return this.command('live.find', { name, map });
    }

    /** Basic information about an entity (by the opaque id of a frame) or a player cell (`[x, y]`). */
    pick({ id, cell, map }) {
        const args = { map };
        if (id) {
            args.id = id;
        }
        if (cell) {
            args.cell = cell;
        }
        return this.command('pick.public', args);
    }

    characters() {
        return this.command('me.characters');
    }
}

const DEFAULT_BACKOFF = [500, 1000, 2000, 5000, 10000];

/**
 * The live socket. It authenticates with the session's CSRF token in its first frame, then delivers `hello`, `frame`
 * (the projected map frame) and `status` events; commands go through `command()` and are answered by id. A socket the
 * server closed (login, logout, kick, expiry) reconnects after refreshing the session; `expired` says the session is gone.
 */
export class PublicSocket {
    #api;
    #ws = null;
    #listeners = new Map();
    #pending = new Map();
    #nextId = 1;
    #wantOpen = false;
    #attempt = 0;
    #timer = null;
    #WebSocket;
    #location;
    #setTimer;
    #clearTimer;
    #backoff;

    constructor(api, { WebSocketImpl, location, setTimer, clearTimer, backoff } = {}) {
        this.#api = api;
        this.#WebSocket = WebSocketImpl ?? globalThis.WebSocket;
        this.#location = location ?? globalThis.location;
        this.#setTimer = setTimer ?? ((fn, ms) => setTimeout(fn, ms));
        this.#clearTimer = clearTimer ?? ((t) => clearTimeout(t));
        this.#backoff = backoff ?? DEFAULT_BACKOFF;
        this.map = null;
        this.ready = false;
    }

    /** Subscribes to `hello`, `frame`, `status`, `state` (connecting|open|closed), `error` (server error messages) or `expired`; returns the unsubscribe function. */
    on(topic, fn) {
        if (!this.#listeners.has(topic)) {
            this.#listeners.set(topic, new Set());
        }
        this.#listeners.get(topic).add(fn);
        return () => this.#listeners.get(topic)?.delete(fn);
    }

    #emit(topic, data) {
        for (const fn of [...(this.#listeners.get(topic) ?? [])]) {
            fn(data);
        }
    }

    connect() {
        this.#wantOpen = true;
        this.#open();
    }

    close() {
        this.#wantOpen = false;
        if (this.#timer !== null) {
            this.#clearTimer(this.#timer);
            this.#timer = null;
        }
        this.#ws?.close();
    }

    #url() {
        const scheme = this.#location.protocol === 'https:' ? 'wss:' : 'ws:';
        return `${scheme}//${this.#location.host}/api/public/ws`;
    }

    #open() {
        if (this.#ws) {
            return;
        }
        this.#emit('state', 'connecting');
        const ws = new this.#WebSocket(this.#url());
        this.#ws = ws;
        ws.onopen = () => {
            ws.send(JSON.stringify({ op: 'auth', csrf: this.#api.csrf }));
        };
        ws.onmessage = (event) => this.#message(event.data);
        ws.onclose = () => this.#closed(ws);
        ws.onerror = () => {
            // A failed attempt ends in `onclose`, which schedules the retry.
        };
    }

    #message(data) {
        let message;
        try {
            message = JSON.parse(data);
        } catch {
            return;
        }
        if (!isObject(message)) {
            return;
        }
        switch (message.op) {
            case 'hello':
                this.ready = true;
                this.#attempt = 0;
                this.#emit('state', 'open');
                this.#emit('hello', message);
                break;
            case 'event':
                if (message.topic === 'live.frame') {
                    this.#emit('frame', message.data);
                } else if (message.topic === 'live.status') {
                    this.#emit('status', message.data);
                }
                break;
            case 'result': {
                const entry = this.#pending.get(message.id);
                if (entry) {
                    this.#pending.delete(message.id);
                    try {
                        entry.resolve(unwrapCommand(message.result, entry.name));
                    } catch (error) {
                        entry.reject(error);
                    }
                }
                break;
            }
            case 'error':
                this.#emit('error', { code: message.code ?? 'failed', message: message.message ?? '' });
                break;
            default:
                break;
        }
    }

    #closed(ws) {
        if (this.#ws !== ws) {
            return;
        }
        this.#ws = null;
        this.ready = false;
        for (const entry of this.#pending.values()) {
            entry.reject(new PublicCommandError(entry.name, 'closed', 'The connection closed.'));
        }
        this.#pending.clear();
        this.#emit('state', 'closed');
        if (!this.#wantOpen) {
            return;
        }
        const delay = this.#backoff[Math.min(this.#attempt, this.#backoff.length - 1)];
        this.#attempt++;
        this.#timer = this.#setTimer(() => {
            this.#timer = null;
            void this.#reconnect();
        }, delay);
    }

    async #reconnect() {
        try {
            await this.#api.me();
        } catch (error) {
            if (error instanceof PublicApiError && (error.status === 401 || error.code === 'no-session')) {
                this.#wantOpen = false;
                this.#emit('expired', null);
                return;
            }
        }
        if (this.#wantOpen) {
            this.#open();
        }
    }

    /** Chooses the public map whose frames this socket receives. */
    view(map) {
        this.map = map;
        this.#send({ op: 'view', map });
    }

    ping() {
        this.#send({ op: 'ping' });
    }

    /** Runs an allowlisted command over the socket; resolves with its data. */
    command(name, args = {}) {
        if (!this.ready) {
            return Promise.reject(new PublicCommandError(name, 'closed', 'The connection is not open.'));
        }
        const id = this.#nextId++;
        return new Promise((resolve, reject) => {
            this.#pending.set(id, { name, resolve, reject });
            this.#send({ op: 'command', id, name, args });
        });
    }

    #send(message) {
        if (this.#ws && this.#ws.readyState === 1) {
            this.#ws.send(JSON.stringify(message));
        }
    }
}
