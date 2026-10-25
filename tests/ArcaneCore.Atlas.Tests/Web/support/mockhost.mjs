// A stand-in for the Atlas host (design doc section 13) for browser tests: serves Web/ over HTTP, answers the REST
// routes and a minimal WebSocket (RFC 6455: text frames, ping, close), and records what the UI sent. Loopback only.

import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { FALLBACK_LAYERS } from '../../../../tools/ArcaneCore.Atlas/Web/js/catalog.js';
import { defaultView } from '../../../../tools/ArcaneCore.Atlas/Web/js/view.js';
import { mergePatch } from '../../../../tools/ArcaneCore.Atlas/Web/js/util.js';
import { encodeEnvelope } from './atg1-writer.mjs';

const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8' };
const SCHEME = 'http';
const BASE = `${SCHEME}://mock`;
const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

const obj = (properties, required = []) => ({ type: 'object', properties, ...(required.length > 0 ? { required } : {}), additionalProperties: false });
const point = (description) => ({ type: 'array', items: { type: 'number' }, minItems: 3, maxItems: 3, description });

export const MOCK_COMMANDS = [
    { name: 'status', summary: 'Host status.', surfaces: ['Palette', 'Ctl'], schema: obj({}) },
    { name: 'maps.list', summary: 'Maps with tile counts.', surfaces: ['Palette'], schema: obj({}) },
    { name: 'layers.list', summary: 'Layer catalog.', surfaces: ['Palette'], schema: obj({}) },
    { name: 'view.get', summary: 'The shared view.', surfaces: ['Palette'], schema: obj({}) },
    { name: 'view.set', summary: 'Merge-patch the shared view.', surfaces: ['Palette'], schema: obj({ camera: { type: 'object' }, map: { type: 'integer' } }) },
    { name: 'bookmarks.list', summary: 'Bookmarks.', surfaces: ['Palette'], schema: obj({}) },
    { name: 'bookmark.go', summary: 'Go to a bookmark.', surfaces: ['Palette'], schema: obj({ name: { type: 'string', description: 'Bookmark name.' } }, ['name']) },
    { name: 'camera.goto', summary: 'Move the camera.', surfaces: ['Palette'], schema: obj({ map: { type: 'integer' }, x: { type: 'number' }, y: { type: 'number' }, z: { type: 'number' } }) },
    { name: 'query.height', summary: 'Height probes at a point.', surfaces: ['Palette'], schema: obj({ map: { type: 'integer', description: 'Map id.' }, at: point('World point.') }, ['map', 'at']) },
    { name: 'ref.query.height', summary: 'Reference height probes.', surfaces: ['Palette'], schema: obj({ map: { type: 'integer' }, at: point('World point.'), search: { type: 'number' } }, ['map', 'at']) },
    { name: 'query.los', summary: 'Line of sight.', surfaces: ['Palette'], schema: obj({ map: { type: 'integer' }, a: point('From.'), b: point('To.') }, ['map', 'a', 'b']) },
    { name: 'query.pick', summary: 'Ray pick.', surfaces: ['Palette'], schema: obj({ ray: { type: 'object' }, ndc: { type: 'array', items: { type: 'number' } }, layers: { type: 'array', items: { type: 'string' } } }) },
    { name: 'capture', summary: 'Capture the view.', surfaces: ['Palette'], schema: obj({ width: { type: 'integer', default: 1600 }, height: { type: 'integer', default: 900 }, source: { type: 'string', enum: ['auto', 'ui', 'headless'] } }) },
    { name: 'live.connect', summary: 'Connect to the world server.', surfaces: ['Palette'], schema: obj({ pid: { type: 'integer' } }) },
    { name: 'live.disconnect', summary: 'Disconnect.', surfaces: ['Palette'], schema: obj({}) },
    { name: 'live.status', summary: 'Live feed status.', surfaces: ['Palette'], schema: obj({}) },
];

export class MockHost {
    /**
     * @param {object} o
     * @param {string} o.webRoot  folder served under /app and /
     * @param {string} o.token
     * @param {string[]} [o.layers]  layer ids that have a source
     */
    constructor({ webRoot, token, layers = ['tiles', 'terrain', 'vmap.wmo', 'liquid'] }) {
        this.webRoot = webRoot;
        this.token = token;
        this.sourceLayers = new Set(layers);
        this.view = defaultView();
        this.viewVersion = 1;
        this.tiles = new Map();
        this.commands = [];
        this.sockets = new Set();
        this.captures = new Map();
        this.failedCaptures = new Map();
        this.requests = [];
        this.handlers = new Map();
        this.binaries = new Map();
        this.bookmarks = [{ name: 'Goldshire inn', map: 0, at: [-9462.66, 16.1915, 57.0459], builtIn: true }];
        this.server = http.createServer((req, res) => this.#http(req, res));
        this.server.on('upgrade', (req, socket) => this.#upgrade(req, socket));
        this.nextSocket = 1;
    }

    get port() {
        return this.server.address().port;
    }

    get origin() {
        return `${SCHEME}://127.0.0.1:${this.port}`;
    }

    async start() {
        await new Promise((resolve) => this.server.listen(0, '127.0.0.1', resolve));
        return this.port;
    }

    async stop() {
        for (const s of this.sockets) {
            s.socket.destroy();
        }
        this.server.closeAllConnections?.();
        await new Promise((resolve) => this.server.close(resolve));
    }

    /** Registers the tile served for (layer, map, x, y). */
    setTile(layer, map, x, y, { state = 'Ok', detail = '', parts = [] }) {
        this.tiles.set(`${layer}|${map}|${x}|${y}`, { state, detail, parts });
    }

    /** Serves GET /api/bin/<name>: handler(searchParams) returns {status?, type?, body} (body a Uint8Array or string). */
    setBinary(name, handler) {
        this.binaries.set(name, handler);
    }

    /** Overrides a command: handler(args, ctx) returns data or throws {code, message}. */
    handle(name, handler) {
        this.handlers.set(name, handler);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Commands

    layersList() {
        return FALLBACK_LAYERS.map((l) => ({
            id: l.id, group: l.group, view: l.view, title: l.title, defaultColor: l.defaultColor, defaultVisible: l.defaultVisible,
            attributeColors: l.attributeColors, tiled: l.tiled, available: this.sourceLayers.has(l.id) || l.id.startsWith('live.'),
        }));
    }

    run(name, args, origin) {
        const started = performance.now();
        this.commands.push({ name, args, origin });
        const done = (data) => ({ command: name, ok: true, data, elapsedMs: Math.round((performance.now() - started) * 1000) / 1000 });
        const fail = (code, message) => ({ command: name, ok: false, error: { code, message }, elapsedMs: 0.1 });
        try {
            const custom = this.handlers.get(name);
            if (custom) {
                return done(custom(args, { origin, host: this }));
            }
            switch (name) {
                case 'status':
                    return done({ paths: { terrainData: 'D:\\mock\\terrain', clientRoot: 'D:\\mock\\client' }, problems: [], modules: ['Foundation', 'Layers', 'Host'], version: 'mock0001', live: { connected: false } });
                case 'maps.list':
                    return done([{ map: 0, name: 'Eastern Kingdoms', layers: { terrain: 64 } }, { map: 1, name: 'Kalimdor', layers: { terrain: 60 } }]);
                case 'layers.list':
                    return done(this.layersList());
                case 'view.get':
                    return done({ view: this.view, version: this.viewVersion });
                case 'view.set': {
                    this.view = mergePatch(this.view, args);
                    this.viewVersion++;
                    this.broadcast('view', { version: this.viewVersion, origin, view: this.view });
                    return done({ version: this.viewVersion });
                }
                case 'bookmarks.list':
                    return done(this.bookmarks);
                case 'bookmark.go': {
                    const b = this.bookmarks.find((x) => x.name === args.name);
                    if (!b) {
                        return fail('bad-args', `No bookmark named ${args.name}.`);
                    }
                    this.view = mergePatch(this.view, { map: b.map, camera: { target: b.at } });
                    this.viewVersion++;
                    this.broadcast('view', { version: this.viewVersion, origin, view: this.view });
                    return done({});
                }
                case 'live.status':
                    return done({ connected: false });
                default:
                    return fail('unknown-command', `The mock host has no handler for ${name}.`);
            }
        } catch (error) {
            return fail(error.code ?? 'failed', error.message);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // HTTP

    #authorised(req) {
        return req.headers.authorization === `Bearer ${this.token}`;
    }

    async #http(req, res) {
        const url = new URL(req.url, BASE);
        this.requests.push({ method: req.method, path: url.pathname + url.search, authorised: this.#authorised(req) });
        const send = (status, body, type = 'application/json', headers = {}) => {
            res.writeHead(status, { 'Content-Type': type, 'Cache-Control': 'no-store', ...headers });
            res.end(body);
        };
        const json = (status, value) => send(status, JSON.stringify(value));
        try {
            if (req.method === 'GET' && (url.pathname === '/' || url.pathname.startsWith('/app/'))) {
                const rel = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice('/app/'.length));
                const file = path.resolve(this.webRoot, rel);
                if (!file.startsWith(path.resolve(this.webRoot)) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
                    return send(404, 'not found', 'text/plain');
                }
                return send(200, fs.readFileSync(file), MIME[path.extname(file)] ?? 'application/octet-stream');
            }
            if (!url.pathname.startsWith('/api/')) {
                return send(404, 'not found', 'text/plain');
            }
            if (!this.#authorised(req)) {
                return send(401, 'unauthorised', 'text/plain');
            }
            if (req.method === 'GET' && url.pathname.startsWith('/api/bin/')) {
                const handler = this.binaries.get(url.pathname.slice('/api/bin/'.length));
                if (!handler) {
                    return json(404, { error: { code: 'not-found', message: 'No such binary endpoint.' } });
                }
                const answer = handler(url.searchParams);
                return send(answer.status ?? 200, answer.body, answer.type ?? 'application/octet-stream');
            }
            if (url.pathname === '/api/status') {
                return json(200, this.run('status', {}, 'http').data);
            }
            if (url.pathname === '/api/commands') {
                return json(200, MOCK_COMMANDS);
            }
            if (url.pathname === '/api/command' && req.method === 'POST') {
                const body = JSON.parse(await readBody(req));
                return json(200, this.run(body.name, body.args ?? {}, 'http'));
            }
            if (url.pathname === '/api/tiles') {
                const layer = url.searchParams.get('layer');
                const map = Number(url.searchParams.get('map'));
                const cx = Number(url.searchParams.get('x'));
                const cy = Number(url.searchParams.get('y'));
                const radius = Number(url.searchParams.get('radius'));
                const tiles = [];
                for (let x = cx - radius; x <= cx + radius; x++) {
                    for (let y = cy - radius; y <= cy + radius; y++) {
                        const t = this.tiles.get(`${layer}|${map}|${x}|${y}`);
                        tiles.push({ layer, tile: [map, x, y], state: t?.state ?? 'Missing', detail: t?.detail ?? '', primitives: t ? t.parts.length : 0 });
                    }
                }
                return json(200, { tiles });
            }
            const tileMatch = /^\/api\/tile\/([^/]+)\/(\d+)\/(-?\d+)\/(-?\d+)$/.exec(url.pathname);
            if (tileMatch) {
                const layer = decodeURIComponent(tileMatch[1]);
                if (!FALLBACK_LAYERS.some((l) => l.id === layer)) {
                    return send(404, 'unknown layer', 'text/plain');
                }
                const t = this.tiles.get(`${layer}|${tileMatch[2]}|${tileMatch[3]}|${tileMatch[4]}`);
                const state = t?.state ?? 'Missing';
                return send(200, Buffer.from(encodeEnvelope(t?.parts ?? [])), 'application/octet-stream', {
                    'X-Atlas-Tile-State': state,
                    'X-Atlas-Tile-Detail': encodeURIComponent(t?.detail ?? ''),
                });
            }
            const captureMatch = /^\/api\/capture\/([^/]+)$/.exec(url.pathname);
            if (captureMatch && req.method === 'POST') {
                const body = await readBody(req, true);
                this.captures.set(captureMatch[1], { body, type: req.headers['content-type'] });
                return send(200, '{}');
            }
            return send(404, 'not found', 'text/plain');
        } catch (error) {
            return send(500, String(error.stack ?? error), 'text/plain');
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // WebSocket

    #upgrade(req, socket) {
        const url = new URL(req.url, BASE);
        const key = req.headers['sec-websocket-key'];
        if (url.pathname !== '/api/ws' || !key) {
            socket.destroy();
            return;
        }
        // RFC 6455 section 4.2.2 fixes SHA-1 for Sec-WebSocket-Accept (a handshake checksum, not a security use).
        const accept = crypto.createHash('sha1').update(key + GUID).digest('base64'); // DevSkim: ignore DS126858
        socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
        const client = { socket, id: `c${this.nextSocket++}`, authed: false, buffer: Buffer.alloc(0) };
        this.sockets.add(client);
        const timer = setTimeout(() => !client.authed && closeFrame(socket, 4401), 5000);
        socket.on('data', (chunk) => {
            client.buffer = Buffer.concat([client.buffer, chunk]);
            for (;;) {
                const frame = readFrame(client.buffer);
                if (!frame) {
                    break;
                }
                client.buffer = client.buffer.subarray(frame.length);
                if (frame.opcode === 8) {
                    socket.end();
                } else if (frame.opcode === 9) {
                    socket.write(encodeFrame(frame.payload, 10));
                } else if (frame.opcode === 1) {
                    this.#message(client, JSON.parse(frame.payload.toString('utf8')));
                }
            }
        });
        socket.on('close', () => {
            clearTimeout(timer);
            this.sockets.delete(client);
        });
        socket.on('error', () => this.sockets.delete(client));
    }

    #message(client, message) {
        if (!client.authed) {
            if (message.op === 'auth' && message.token === this.token) {
                client.authed = true;
                this.send(client, { op: 'hello', connection: client.id, version: 'mock0001', view: this.view, viewVersion: this.viewVersion });
            } else {
                closeFrame(client.socket, 4401);
            }
            return;
        }
        if (message.op === 'command') {
            this.send(client, { op: 'result', id: message.id, result: this.run(message.name, message.args ?? {}, `ui:${client.id}`) });
        } else if (message.op === 'captureFailed') {
            this.failedCaptures.set(message.id, message.message);
        }
    }

    send(client, value) {
        client.socket.write(encodeFrame(Buffer.from(JSON.stringify(value), 'utf8'), 1));
    }

    /** Pushes an event to every authenticated UI. */
    broadcast(topic, data) {
        for (const c of this.sockets) {
            if (c.authed) {
                this.send(c, { op: 'event', topic, data });
            }
        }
    }

    /** Asks the UI for a capture and waits for the PNG (or the failure message). */
    async requestCapture(width, height, timeoutMs = 20_000) {
        const client = [...this.sockets].find((c) => c.authed);
        if (!client) {
            throw new Error('No UI is connected.');
        }
        const id = crypto.randomUUID();
        this.send(client, { op: 'capture', id, width, height });
        const deadline = Date.now() + timeoutMs;
        while (Date.now() < deadline) {
            if (this.captures.has(id)) {
                return { id, png: this.captures.get(id).body, type: this.captures.get(id).type };
            }
            if (this.failedCaptures.has(id)) {
                return { id, failed: this.failedCaptures.get(id) };
            }
            await new Promise((r) => setTimeout(r, 50));
        }
        throw new Error('The capture did not arrive in time.');
    }
}

function readBody(req, binary = false) {
    return new Promise((resolve, reject) => {
        const parts = [];
        req.on('data', (c) => parts.push(c));
        req.on('end', () => resolve(binary ? Buffer.concat(parts) : Buffer.concat(parts).toString('utf8')));
        req.on('error', reject);
    });
}

function encodeFrame(payload, opcode) {
    const n = payload.length;
    let header;
    if (n < 126) {
        header = Buffer.from([0x80 | opcode, n]);
    } else if (n < 65536) {
        header = Buffer.alloc(4);
        header[0] = 0x80 | opcode;
        header[1] = 126;
        header.writeUInt16BE(n, 2);
    } else {
        header = Buffer.alloc(10);
        header[0] = 0x80 | opcode;
        header[1] = 127;
        header.writeBigUInt64BE(BigInt(n), 2);
    }
    return Buffer.concat([header, payload]);
}

function closeFrame(socket, code) {
    const payload = Buffer.alloc(2);
    payload.writeUInt16BE(code, 0);
    socket.write(encodeFrame(payload, 8));
    socket.end();
}

/** Parses one client frame from the buffer: {opcode, payload, length} or null when incomplete. */
function readFrame(buffer) {
    if (buffer.length < 2) {
        return null;
    }
    const opcode = buffer[0] & 0x0f;
    const masked = (buffer[1] & 0x80) !== 0;
    let length = buffer[1] & 0x7f;
    let offset = 2;
    if (length === 126) {
        if (buffer.length < 4) {
            return null;
        }
        length = buffer.readUInt16BE(2);
        offset = 4;
    } else if (length === 127) {
        if (buffer.length < 10) {
            return null;
        }
        length = Number(buffer.readBigUInt64BE(2));
        offset = 10;
    }
    const maskBytes = masked ? 4 : 0;
    if (buffer.length < offset + maskBytes + length) {
        return null;
    }
    const mask = masked ? buffer.subarray(offset, offset + 4) : null;
    const payload = Buffer.from(buffer.subarray(offset + maskBytes, offset + maskBytes + length));
    if (mask) {
        for (let i = 0; i < payload.length; i++) {
            payload[i] ^= mask[i % 4];
        }
    }
    return { opcode, payload, length: offset + maskBytes + length };
}
