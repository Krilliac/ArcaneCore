// Support for clientfs.test.mjs: loads the synthetic client folder that ClientFsFixtureTests.cs writes (nothing here is
// committed or derived from a client file), builds in-memory folder trees, handle and FileList mocks that count every
// call, a loopback harness server that records every request, and the canary matcher.

import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const WEB_ROOT = fileURLToPath(new URL('../../../../tools/ArcaneCore.Atlas/Web/', import.meta.url));
export const FIXTURE_VARIABLE = 'ARCANECORE_ATLAS_CLIENTFS';
export const REAL_VARIABLE = 'ARCANECORE_ATLAS_CLIENTFS_REAL';

/** Imports a module of the lane under test; a missing module fails the calling test, not the whole file. */
export const clientfs = (name) => import(new URL(`js/clientfs/${name}.js`, pathToFileURL(WEB_ROOT)).href);

// ------------------------------------------------------------------------------------------------------------------
// Fixtures

export function loadFixtures() {
    const dir = process.env[FIXTURE_VARIABLE];
    if (!dir || !fs.existsSync(path.join(dir, 'manifest.json'))) {
        throw new Error(`${FIXTURE_VARIABLE} does not name a fixture directory; run this file through ClientFsFixtureTests (it writes the synthetic client folder first).`);
    }
    const manifest = JSON.parse(fs.readFileSync(path.join(dir, 'manifest.json'), 'utf8'));
    const read = (relative) => new Uint8Array(fs.readFileSync(path.join(dir, relative)));
    return {
        dir,
        manifest,
        read,
        blob: (relative) => new Blob([read(relative)]),
        sha256: (bytes) => crypto.createHash('sha256').update(bytes).digest('hex'),
        /** The file tree of the good client: path -> Uint8Array. */
        tree: (overrides = {}) => buildTree(read, manifest.tree, overrides),
    };
}

/** path -> bytes for a tree; `overrides` maps a path to a fixture-relative file, to bytes, or to null (removed). */
export function buildTree(read, tree, overrides = {}) {
    const files = new Map();
    for (const [p, relative] of Object.entries(tree)) {
        files.set(p, read(relative));
    }
    for (const [p, value] of Object.entries(overrides)) {
        if (value === null) {
            files.delete(p);
        } else {
            files.set(p, typeof value === 'string' ? read(value) : value);
        }
    }
    return files;
}

export const hex = (bytes) => Buffer.from(bytes).toString('hex');

// ------------------------------------------------------------------------------------------------------------------
// Mock File System Access handles and FileList. Every call is appended to `log` as {op, path}.

const NOT_FOUND = () => Object.assign(new Error('A requested file or directory could not be found.'), { name: 'NotFoundError' });

class CountingFile extends File {
    constructor(bytes, name, lastModified, log, fullPath) {
        super([bytes], name, { lastModified });
        this.fullPath = fullPath;
        this.log = log;
    }

    slice(start, end, type) {
        this.log.push({ op: 'read', path: this.fullPath });
        return super.slice(start, end, type);
    }

    arrayBuffer() {
        this.log.push({ op: 'read', path: this.fullPath });
        return super.arrayBuffer();
    }

    stream() {
        this.log.push({ op: 'read', path: this.fullPath });
        return super.stream();
    }

    text() {
        this.log.push({ op: 'read', path: this.fullPath });
        return super.text();
    }
}

class MockFileHandle {
    constructor(node, log, fullPath) {
        this.kind = 'file';
        this.name = node.name;
        this.node = node;
        this.log = log;
        this.fullPath = fullPath;
    }

    async getFile() {
        this.log.push({ op: 'getFile', path: this.fullPath });
        return new CountingFile(this.node.bytes, this.node.name, this.node.lastModified, this.log, this.fullPath);
    }
}

class MockDirectoryHandle {
    constructor(node, log, fullPath) {
        this.kind = 'directory';
        this.name = node.name;
        this.node = node;
        this.log = log;
        this.fullPath = fullPath;
    }

    #child(child) {
        const p = this.fullPath ? `${this.fullPath}/${child.name}` : child.name;
        return child.children ? new MockDirectoryHandle(child, this.log, p) : new MockFileHandle(child, this.log, p);
    }

    async *entries() {
        this.log.push({ op: 'entries', path: this.fullPath });
        for (const child of this.node.children.values()) {
            yield [child.name, this.#child(child)];
        }
    }

    async *values() {
        this.log.push({ op: 'values', path: this.fullPath });
        for (const child of this.node.children.values()) {
            yield this.#child(child);
        }
    }

    async *keys() {
        this.log.push({ op: 'keys', path: this.fullPath });
        for (const child of this.node.children.values()) {
            yield child.name;
        }
    }

    [Symbol.asyncIterator]() {
        this.log.push({ op: 'iterate', path: this.fullPath });
        const inner = (async function* iterate(self) {
            for (const child of self.node.children.values()) {
                yield [child.name, self.#child(child)];
            }
        })(this);
        return inner;
    }

    async getDirectoryHandle(name) {
        this.log.push({ op: 'getDirectoryHandle', path: this.fullPath ? `${this.fullPath}/${name}` : name });
        const child = this.node.children.get(name);
        if (!child?.children) {
            throw NOT_FOUND();
        }
        return this.#child(child);
    }

    async getFileHandle(name) {
        this.log.push({ op: 'getFileHandle', path: this.fullPath ? `${this.fullPath}/${name}` : name });
        const child = this.node.children.get(name);
        if (!child || child.children) {
            throw NOT_FOUND();
        }
        return this.#child(child);
    }

    async queryPermission() {
        return 'granted';
    }

    async requestPermission() {
        return 'granted';
    }
}

/**
 * A case-sensitive mock of a FileSystemDirectoryHandle over `files` (path -> bytes) with root folder name `rootName`.
 * `emptyDirectories` lists extra folder paths to create (for example 'Data/enUS'). `log` receives every call.
 */
export function mockHandle(files, { rootName = 'WoW', emptyDirectories = [], log = [], lastModified = 1_700_000_000_000 } = {}) {
    const root = { name: rootName, children: new Map() };
    const ensure = (segments) => {
        let node = root;
        for (const segment of segments) {
            if (!node.children.has(segment)) {
                node.children.set(segment, { name: segment, children: new Map() });
            }
            node = node.children.get(segment);
        }
        return node;
    };
    for (const [p, bytes] of files) {
        const segments = p.split('/');
        const name = segments.pop();
        ensure(segments).children.set(name, { name, bytes, lastModified });
    }
    for (const d of emptyDirectories) {
        ensure(d.split('/'));
    }
    return { handle: new MockDirectoryHandle(root, log, ''), log };
}

/** A webkitdirectory-style FileList (an array of File-likes with webkitRelativePath) over `files`. */
export function mockFileList(files, { rootName = 'WoW', log = [], lastModified = 1_700_000_000_000 } = {}) {
    const list = [];
    for (const [p, bytes] of files) {
        const full = `${rootName}/${p}`;
        const file = new CountingFile(bytes, p.split('/').pop(), lastModified, log, full);
        Object.defineProperty(file, 'webkitRelativePath', { value: full });
        list.push(file);
    }
    list.item = (i) => list[i] ?? null;
    return { list, log };
}

// ------------------------------------------------------------------------------------------------------------------
// Canaries

/** All the byte strings a canary can show up as: raw, hex (both cases), base64 (all three alignments, also url-safe), percent-encoded. */
export function canaryNeedles(canary) {
    const bytes = Buffer.from(canary.hex, 'hex');
    const needles = new Map();
    const add = (kind, value) => needles.set(`${kind}:${value}`, { kind, value });
    add('raw', bytes.toString('latin1'));
    add('hex', bytes.toString('hex'));
    add('HEX', bytes.toString('hex').toUpperCase());
    for (let k = 0; k < 3; k++) {
        const total = k + bytes.length;
        const full = Buffer.concat([Buffer.alloc(k), bytes]).toString('base64').replace(/=+$/, '');
        const start = [0, 2, 3][k];
        const end = full.length - (total % 3 === 0 ? 0 : 1);
        const piece = full.slice(start, end);
        if (piece.length >= 6) {
            add('base64', piece);
            add('base64url', piece.replaceAll('+', '-').replaceAll('/', '_'));
        }
    }
    const percent = [...bytes].map((b) => `%${b.toString(16).padStart(2, '0')}`).join('');
    add('percent', percent);
    add('PERCENT', percent.toUpperCase());
    if (/^[\x20-\x7e]+$/.test(bytes.toString('latin1'))) {
        add('encodeURIComponent', encodeURIComponent(bytes.toString('latin1')));
    }
    return [...needles.values()];
}

/** Returns [{canary, kind}] for every canary found in `haystack` (a Buffer or string, compared as latin1). */
export function findCanaries(haystack, canaries) {
    const text = Buffer.isBuffer(haystack) ? haystack.toString('latin1') : String(haystack);
    const found = [];
    for (const canary of canaries) {
        for (const needle of canaryNeedles(canary)) {
            if (text.includes(needle.value)) {
                found.push({ canary: canary.label, kind: needle.kind });
            }
        }
    }
    return found;
}

// ------------------------------------------------------------------------------------------------------------------
// Harness server: serves Web/ under /app/, the fixture folder under /__fixtures/, a fake login and a WebSocket, and
// records every request (method, URL, headers, body) and every WebSocket frame in both directions.

const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8' };
const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

/** The Content-Security-Policy of Local's index.html meta tag (read from the real file, so this cannot drift). */
export function localCsp() {
    const html = fs.readFileSync(path.join(WEB_ROOT, 'index.html'), 'utf8');
    const m = /http-equiv="Content-Security-Policy"\s+content="([^"]+)"/.exec(html);
    if (!m) {
        throw new Error('index.html has no Content-Security-Policy meta tag.');
    }
    return m[1];
}

/** The public listener's header: the meta policy plus worker-src and frame-ancestors (design 3, security headers). */
export function publicCsp() {
    return `${localCsp()}; worker-src 'self'; frame-ancestors 'none'`;
}

export class Harness {
    /** @param {'public'|'local'} mode public: CSP header on every response; local: the meta tag in the page only. */
    constructor({ mode, fixtureDir }) {
        this.mode = mode;
        this.fixtureDir = fixtureDir;
        this.requests = [];
        this.frames = [];
        this.sockets = new Set();
        this.server = http.createServer((req, res) => this.#handle(req, res));
        this.server.on('upgrade', (req, socket) => this.#upgrade(req, socket));
    }

    get origin() {
        return `http://127.0.0.1:${this.server.address().port}`;
    }

    async start() {
        await new Promise((resolve) => this.server.listen(0, '127.0.0.1', resolve));
        return this;
    }

    async stop() {
        for (const socket of this.sockets) {
            socket.destroy();
        }
        this.server.closeAllConnections?.();
        await new Promise((resolve) => this.server.close(resolve));
    }

    #page() {
        const meta = this.mode === 'local' ? `<meta http-equiv="Content-Security-Policy" content="${localCsp()}">` : '';
        return `<!doctype html><html><head><meta charset="utf-8">${meta}<link rel="stylesheet" href="/app/app.css"></head>` +
            '<body><div id="app"></div><script type="module" src="/__harness/boot.js"></script></body></html>';
    }

    #boot() {
        return "try { window.cf = await import('/app/js/clientfs/index.js'); window.__bootDone = true; } " +
            'catch (error) { window.__bootError = String(error && error.stack || error); }\n';
    }

    #handle(req, res) {
        const chunks = [];
        req.on('data', (c) => chunks.push(c));
        req.on('end', () => {
            const body = Buffer.concat(chunks);
            this.requests.push({ method: req.method, url: req.url, headers: { ...req.headers }, body });
            const headers = { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' };
            if (this.mode === 'public') {
                headers['Content-Security-Policy'] = publicCsp();
            }
            const url = new URL(req.url, 'http://harness');
            const send = (status, type, data) => {
                res.writeHead(status, { ...headers, 'Content-Type': type });
                res.end(data);
            };
            if (url.pathname === '/') {
                return send(200, MIME['.html'], this.#page());
            }
            if (url.pathname === '/favicon.ico') {
                res.writeHead(204, headers);
                return res.end();
            }
            if (url.pathname === '/__harness/boot.js') {
                return send(200, MIME['.js'], this.#boot());
            }
            if (url.pathname === '/api/public/login' && req.method === 'POST') {
                return send(200, 'application/json', '{"ok":true}');
            }
            const file = url.pathname.startsWith('/app/') ? this.#safe(WEB_ROOT, url.pathname.slice(5))
                : url.pathname.startsWith('/__fixtures/') ? this.#safe(this.fixtureDir, decodeURIComponent(url.pathname.slice(12))) : null;
            if (file && fs.existsSync(file) && fs.statSync(file).isFile()) {
                return send(200, MIME[path.extname(file).toLowerCase()] ?? 'application/octet-stream', fs.readFileSync(file));
            }
            return send(404, 'text/plain', 'not found');
        });
    }

    #safe(root, relative) {
        const full = path.resolve(root, relative);
        return full.startsWith(path.resolve(root)) ? full : null;
    }

    #upgrade(req, socket) {
        this.requests.push({ method: 'GET', url: req.url, headers: { ...req.headers }, body: Buffer.alloc(0), upgrade: true });
        const key = req.headers['sec-websocket-key'];
        socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${crypto.createHash('sha1').update(key + GUID).digest('base64')}\r\n\r\n`);
        this.sockets.add(socket);
        socket.on('close', () => this.sockets.delete(socket));
        socket.on('error', () => {});
        let buffer = Buffer.alloc(0);
        socket.on('data', (data) => {
            buffer = Buffer.concat([buffer, data]);
            for (;;) {
                const frame = parseFrame(buffer);
                if (!frame) {
                    return;
                }
                buffer = buffer.subarray(frame.consumed);
                this.frames.push({ direction: 'client', opcode: frame.opcode, data: frame.payload });
                if (frame.opcode === 1) {
                    const reply = Buffer.from('{"type":"welcome"}');
                    this.frames.push({ direction: 'server', opcode: 1, data: reply });
                    socket.write(Buffer.concat([Buffer.from([0x81, reply.length]), reply]));
                } else if (frame.opcode === 8) {
                    socket.end(Buffer.from([0x88, 0]));
                }
            }
        });
    }

    /** Everything the harness saw as one list of {where, text} haystacks, for the canary scan. */
    haystacks() {
        const out = this.requests.map((r) => ({
            where: `${r.method} ${r.url}`,
            data: Buffer.concat([Buffer.from(`${r.method} ${r.url}\n${Object.entries(r.headers).map(([k, v]) => `${k}: ${v}`).join('\n')}\n`, 'latin1'), r.body]),
        }));
        for (const f of this.frames) {
            out.push({ where: `ws ${f.direction} frame`, data: f.data });
        }
        return out;
    }

    maxBodyBytes() {
        return Math.max(0, ...this.requests.map((r) => r.body.length), ...this.frames.map((f) => f.data.length));
    }
}

function parseFrame(buffer) {
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
    return { opcode, payload, consumed: offset + maskBytes + length };
}

// ------------------------------------------------------------------------------------------------------------------
// Page-side helpers (installed before any page script runs). They make a real OPFS folder look like the visitor's WoW
// folder, mock the picker and the permission calls, and log every directory-handle call so the read scope can be audited.

export const PAGE_HELPERS = `(() => {
  const TREE_ROOT = '__testwow';
  const calls = (window.__fsCalls = []);
  const patch = (proto, name) => {
    const original = proto[name];
    if (typeof original !== 'function') return;
    proto[name] = function (...args) {
      calls.push({ op: name, dir: this.name, arg: typeof args[0] === 'string' ? args[0] : null });
      return original.apply(this, args);
    };
  };
  if (window.FileSystemDirectoryHandle) {
    for (const n of ['entries', 'values', 'keys', 'getDirectoryHandle', 'getFileHandle']) patch(FileSystemDirectoryHandle.prototype, n);
    const it = FileSystemDirectoryHandle.prototype[Symbol.asyncIterator];
    if (it) FileSystemDirectoryHandle.prototype[Symbol.asyncIterator] = function () { calls.push({ op: 'iterate', dir: this.name, arg: null }); return it.call(this); };
  }
  window.__perm = { query: 'granted', request: 'granted', requestCalls: [] };
  if (window.FileSystemHandle) {
    FileSystemHandle.prototype.queryPermission = async function () { return window.__perm.query; };
    FileSystemHandle.prototype.requestPermission = async function (options) {
      window.__perm.requestCalls.push({ options: options ?? null, active: navigator.userActivation.isActive });
      if (window.__perm.request === 'granted') window.__perm.query = 'granted';
      return window.__perm.request;
    };
  }
  window.__pickerCalls = [];
  window.__pickerTarget = TREE_ROOT;
  if (window.showDirectoryPicker) {
    window.showDirectoryPicker = async (options) => {
      window.__pickerCalls.push(options ?? null);
      const root = await navigator.storage.getDirectory();
      return root.getDirectoryHandle(window.__pickerTarget);
    };
  }
  const split = (p) => p.split('/');
  window.__t = {
    treeRoot: TREE_ROOT,
    async stage(files, name = TREE_ROOT) {
      const root = await navigator.storage.getDirectory();
      try { await root.removeEntry(name, { recursive: true }); } catch { /* not there yet */ }
      const top = await root.getDirectoryHandle(name, { create: true });
      for (const [p, relative] of Object.entries(files)) {
        const response = await fetch('/__fixtures/' + relative);
        const bytes = new Uint8Array(await response.arrayBuffer());
        const segments = split(p);
        let dir = top;
        for (const s of segments.slice(0, -1)) dir = await dir.getDirectoryHandle(s, { create: true });
        const handle = await dir.getFileHandle(segments.at(-1), { create: true });
        const writable = await handle.createWritable();
        await writable.write(bytes);
        await writable.close();
      }
      calls.length = 0; // staging is the test's own doing, not the code under test's
      return true;
    },
    async makeDirectory(p, name = TREE_ROOT) {
      const root = await navigator.storage.getDirectory();
      let dir = await root.getDirectoryHandle(name, { create: true });
      for (const s of split(p)) dir = await dir.getDirectoryHandle(s, { create: true });
      calls.length = 0;
      return true;
    },
    async rewrite(p, name = TREE_ROOT) {
      // Writes the same bytes again, which moves lastModified.
      const root = await navigator.storage.getDirectory();
      let dir = await root.getDirectoryHandle(name);
      const segments = split(p);
      for (const s of segments.slice(0, -1)) dir = await dir.getDirectoryHandle(s);
      const handle = await dir.getFileHandle(segments.at(-1));
      const bytes = new Uint8Array(await (await handle.getFile()).arrayBuffer());
      await new Promise((r) => setTimeout(r, 30));
      const writable = await handle.createWritable();
      await writable.write(bytes);
      await writable.close();
      calls.length = 0;
      return true;
    },
    async opfsNames() {
      const root = await navigator.storage.getDirectory();
      const names = [];
      for await (const [n] of root.entries()) names.push(n);
      return names.filter((n) => n !== TREE_ROOT);
    },
    async handleRecords() {
      const db = await new Promise((resolve, reject) => {
        const request = indexedDB.open('atlas-client');
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        request.onupgradeneeded = () => { request.transaction.abort(); resolve(null); };
      }).catch(() => null);
      if (!db) return { database: false, count: 0, stores: [] };
      const stores = [...db.objectStoreNames];
      if (!stores.includes('handles')) { db.close(); return { database: true, count: 0, stores }; }
      const count = await new Promise((resolve, reject) => {
        const request = db.transaction('handles').objectStore('handles').getAll();
        request.onsuccess = () => resolve(request.result.length);
        request.onerror = () => reject(request.error);
      });
      const isHandle = await new Promise((resolve, reject) => {
        const request = db.transaction('handles').objectStore('handles').getAll();
        request.onsuccess = () => resolve(request.result.some((v) => v instanceof FileSystemDirectoryHandle || (v && v.handle instanceof FileSystemDirectoryHandle)));
        request.onerror = () => reject(request.error);
      });
      db.close();
      return { database: true, count, stores, holdsHandle: isHandle };
    },
    // The webkitdirectory path: builds File objects with webkitRelativePath for every file and fires change on the input.
    async chooseFiles(files, rootName = 'WoW') {
      const input = document.querySelector('input[type=file][webkitdirectory]');
      if (!input) throw new Error('no input[type=file][webkitdirectory] in the page');
      const transfer = new DataTransfer();
      for (const [p, relative] of Object.entries(files)) {
        const response = await fetch('/__fixtures/' + relative);
        const file = new File([await response.arrayBuffer()], p.split('/').at(-1), { lastModified: 1700000000000 });
        Object.defineProperty(file, 'webkitRelativePath', { value: rootName + '/' + p });
        transfer.items.add(file);
      }
      input.files = transfer.files;
      input.dispatchEvent(new Event('change', { bubbles: true }));
      return transfer.files.length;
    },
  };
})();`;

/** The same helpers for a browser without the File System Access picker (Firefox, Safari): the picker is removed before any module runs. */
export const PAGE_HELPERS_NO_PICKER = `${PAGE_HELPERS}
delete window.showDirectoryPicker;`;

/** Clicks the first visible button/anchor whose trimmed text equals `text` with real mouse events (so user activation is real). */
export async function clickText(page, text, scope = '[data-clientfs]') {
    const rect = await page.waitFor(`(() => {
        const wanted = ${JSON.stringify(text)};
        for (const el of document.querySelectorAll(${JSON.stringify(`${scope} button, ${scope} a, button${scope}, a${scope}`)})) {
            if (el.textContent.trim() === wanted) {
                const r = el.getBoundingClientRect();
                if (r.width > 0 && r.height > 0) { el.scrollIntoView({ block: 'center' }); const b = el.getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 }; }
            }
        }
        return null; })()`, `a visible control labelled "${text}"`);
    await page.mouse('mouseMoved', rect.x, rect.y);
    await page.mouse('mousePressed', rect.x, rect.y, { button: 'left', buttons: 1, clickCount: 1 });
    await page.mouse('mouseReleased', rect.x, rect.y, { button: 'left', buttons: 0, clickCount: 1 });
}

/** The visible text of the element matching `selector`, or null. */
export const textOf = (selector) => `(() => { const el = document.querySelector(${JSON.stringify(selector)}); return el ? el.textContent.replace(/\\s+/g, ' ').trim() : null; })()`;
