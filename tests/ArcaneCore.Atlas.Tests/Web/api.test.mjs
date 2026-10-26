// The host API client: token hand-over, REST calls with the bearer token, tile responses, and the WebSocket (auth, hello,
// events, commands, capture failures, reconnects). A fake fetch and a fake WebSocket stand in for the network.

import assert from 'node:assert/strict';
import test from 'node:test';
import {
    Api, ApiError, CommandError, Socket, adoptToken, forgetToken, fragmentName, rememberToken, unwrap,
} from '../../../tools/ArcaneCore.Atlas/Web/js/api.js';

const memoryStorage = (initial = {}) => {
    const data = new Map(Object.entries(initial));
    return {
        data,
        getItem: (k) => (data.has(k) ? data.get(k) : null),
        setItem: (k, v) => data.set(k, String(v)),
        removeItem: (k) => data.delete(k),
    };
};

test('the token moves from the URL fragment into session storage', () => {
    const storage = memoryStorage();
    let cleared = 0;
    const clearFragment = () => {
        cleared++;
    };
    assert.equal(adoptToken({ hash: '#t=abc_DEF-123', storage, clearFragment }), 'abc_DEF-123');
    assert.equal(storage.data.get('atlas.session'), 'abc_DEF-123');
    assert.equal(cleared, 1, 'the fragment is cleared so the token stays out of history and logs');
    assert.equal(adoptToken({ hash: '', storage, clearFragment }), 'abc_DEF-123', 'a reload keeps the stored token');
    assert.equal(cleared, 1);
    assert.equal(adoptToken({ hash: '#x=1&t=second', storage, clearFragment }), 'second', 'the token may follow other fragment fields');
    assert.equal(storage.data.get('atlas.session'), 'second');
    assert.equal(adoptToken({ hash: '#other=1', storage, clearFragment }), 'second');
    assert.equal(adoptToken({ hash: undefined, storage: memoryStorage(), clearFragment }), null, 'no fragment and nothing stored: no token');
    assert.equal(adoptToken({ hash: '#not_t=1', storage: memoryStorage(), clearFragment }), null);
    forgetToken(storage);
    assert.equal(adoptToken({ hash: '', storage, clearFragment }), null);
    rememberToken(storage, 'manual');
    assert.equal(storage.data.get('atlas.session'), 'manual');
});

test('fragmentName reads and decodes the join name beside the token', () => {
    assert.equal(fragmentName('#t=abc&name=Grok%20Bot'), 'Grok Bot');
    assert.equal(fragmentName('#name=Solo&t=abc'), 'Solo');
    assert.equal(fragmentName('#x=1&t=abc&name=A%26B%3Dc'), 'A&B=c', 'reserved characters survive percent-encoding');
    assert.equal(fragmentName('#t=abc&name=%E2%9C%93%20ok'), '✓ ok', 'UTF-8 is decoded');
    assert.equal(fragmentName('#t=abc'), null);
    assert.equal(fragmentName('#t=abc&name='), null, 'an empty name is no name');
    assert.equal(fragmentName('#t=abc&name=%20%20'), null, 'a blank name is no name');
    assert.equal(fragmentName('#t=abc&name=%E0%A4%A'), null, 'a malformed escape is ignored, not thrown');
    assert.equal(fragmentName('#t=abc&rename=x'), null, 'only a field called name counts');
    assert.equal(fragmentName(''), null);
    assert.equal(fragmentName(undefined), null);
});

test('a join link keeps adoptToken working: the token is adopted whatever follows it', () => {
    const storage = memoryStorage();
    let cleared = 0;
    const clearFragment = () => {
        cleared++;
    };
    assert.equal(adoptToken({ hash: '#t=abc_DEF-123&name=Grok%20Bot', storage, clearFragment }), 'abc_DEF-123');
    assert.equal(cleared, 1);
    assert.equal(fragmentName('#t=abc_DEF-123&name=Grok%20Bot'), 'Grok Bot');
});

test('unwrap returns a command result\'s data or raises its error', () => {
    assert.deepEqual(unwrap({ ok: true, data: { a: 1 } }), { a: 1 });
    assert.equal(unwrap({ ok: true }), undefined);
    assert.throws(() => unwrap({ command: 'query.los', ok: false, error: { code: 'bad-args', message: 'from is required' } }), (e) => {
        assert.ok(e instanceof CommandError);
        assert.deepEqual([e.command, e.code, e.message], ['query.los', 'bad-args', 'from is required']);
        return true;
    });
    assert.throws(() => unwrap(null), (e) => e instanceof CommandError && e.command === '?' && e.code === 'failed');
    assert.throws(() => unwrap({ ok: false }), /The command failed/);
    assert.equal(new ApiError('m', { status: 404, code: 'x' }).status, 404);
    assert.equal(new ApiError('m').code, null);
});

function fakeFetch(handler) {
    const calls = [];
    const fetchImpl = async (path, init) => {
        calls.push({ path, init });
        return handler(path, init);
    };
    return { fetchImpl, calls };
}

const jsonResponse = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });

test('REST calls carry the bearer token and read JSON', async () => {
    const { fetchImpl, calls } = fakeFetch((path) => jsonResponse(path === '/api/status' ? { version: '1' } : { commands: [] }));
    const api = new Api('tok123', fetchImpl);
    assert.equal(api.token, 'tok123');
    assert.deepEqual(await api.status(), { version: '1' });
    assert.deepEqual(await api.commandList(), { commands: [] });
    assert.deepEqual(calls.map((c) => c.path), ['/api/status', '/api/commands']);
    assert.ok(calls.every((c) => c.init.headers.Authorization === 'Bearer tok123'));
    assert.ok(calls.every((c) => !c.path.includes('tok123')), 'the token never appears in a URL');
});

test('commands are posted as JSON and their failures come back as results', async () => {
    const { fetchImpl, calls } = fakeFetch(() => jsonResponse({ command: 'view.set', ok: false, error: { code: 'bad-args', message: 'no' } }));
    const api = new Api('t', fetchImpl);
    const result = await api.command('view.set', { camera: { yaw: 5 } });
    assert.equal(result.ok, false, 'an ok:false result is returned, not thrown');
    assert.equal(calls[0].path, '/api/command');
    assert.equal(calls[0].init.method, 'POST');
    assert.equal(calls[0].init.headers['Content-Type'], 'application/json');
    assert.equal(calls[0].init.headers.Authorization, 'Bearer t');
    assert.deepEqual(JSON.parse(calls[0].init.body), { name: 'view.set', args: { camera: { yaw: 5 } } });
    await api.command('x');
    assert.deepEqual(JSON.parse(calls[1].init.body), { name: 'x', args: {} });
});

test('a refused token raises one event and one error; other failures say what happened', async () => {
    const api = new Api('t', async () => new Response('', { status: 401 }));
    let events = 0;
    api.on('unauthorized', () => events++);
    await assert.rejects(api.status(), (e) => e instanceof ApiError && e.status === 401 && e.code === 'unauthorized' && /refused the token/.test(e.message));
    assert.equal(events, 1);

    const down = new Api('t', async () => {
        throw new TypeError('fetch failed');
    });
    await assert.rejects(down.status(), (e) => e instanceof ApiError && e.code === 'network' && /Cannot reach the Atlas host: fetch failed/.test(e.message));

    const aborted = new Api('t', async () => {
        throw Object.assign(new Error('aborted'), { name: 'AbortError' });
    });
    await assert.rejects(aborted.status(), (e) => e.name === 'AbortError', 'an abort is passed through, not wrapped');

    const broken = new Api('t', async () => new Response('x', { status: 503 }));
    await assert.rejects(broken.status(), (e) => e instanceof ApiError && e.status === 503 && /GET \/api\/status answered 503/.test(e.message));
    await assert.rejects(broken.command('x'), /POST \/api\/command answered 503/);
});

test('tile requests build the documented URLs and read the state headers', async () => {
    const { fetchImpl, calls } = fakeFetch((path) => {
        if (path.startsWith('/api/tiles?')) {
            return jsonResponse({ tiles: [] });
        }
        if (path.includes('/missing/')) {
            return new Response('', { status: 404 });
        }
        if (path.includes('/broken/')) {
            return new Response('', { status: 500 });
        }
        return new Response(Uint8Array.of(1, 0, 0, 0), { status: 200, headers: { 'X-Atlas-Tile-State': 'Rejected', 'X-Atlas-Tile-Detail': encodeURIComponent('bad magic: 50 % é') } });
    });
    const api = new Api('t', fetchImpl);
    await api.tiles('vmap.wmo', 0, 49, 31, 2);
    const query = new URLSearchParams(calls[0].path.split('?')[1]);
    assert.deepEqual(Object.fromEntries(query), { layer: 'vmap.wmo', map: '0', x: '49', y: '31', radius: '2' });

    const tile = await api.tile('terrain', 0, 49, 31);
    assert.equal(calls[1].path, '/api/tile/terrain/0/49/31');
    assert.equal(tile.state, 'Rejected');
    assert.equal(tile.detail, 'bad magic: 50 % é');
    assert.ok(tile.body instanceof ArrayBuffer);
    assert.deepEqual([...new Uint8Array(tile.body)], [1, 0, 0, 0]);
    await api.tile('a/b', 1, -1, -1);
    assert.equal(calls[2].path, '/api/tile/a%2Fb/1/-1/-1', 'the layer id is encoded');

    await assert.rejects(api.tile('missing', 0, 0, 0), (e) => e.code === 'unknown-layer' && e.status === 404);
    await assert.rejects(api.tile('broken', 0, 0, 0), (e) => e.status === 500);

    const plain = new Api('t', async () => new Response(new ArrayBuffer(4), { status: 200 }));
    const unknown = await plain.tile('terrain', 0, 1, 1);
    assert.deepEqual([unknown.state, unknown.detail], ['Unknown', '']);
    const badEscape = new Api('t', async () => new Response(new ArrayBuffer(4), { status: 200, headers: { 'X-Atlas-Tile-Detail': '100%' } }));
    assert.equal((await badEscape.tile('terrain', 0, 1, 1)).detail, '100%', 'a detail that is not valid percent-encoding is shown as sent');
});

test('a capture is posted as a PNG to its own id', async () => {
    const { fetchImpl, calls } = fakeFetch(() => new Response(null, { status: 204 }));
    const api = new Api('t', fetchImpl);
    const blob = new Blob([Uint8Array.of(137, 80, 78, 71)], { type: 'image/png' });
    await api.postCapture('a b', blob);
    assert.equal(calls[0].path, '/api/capture/a%20b');
    assert.equal(calls[0].init.method, 'POST');
    assert.equal(calls[0].init.headers['Content-Type'], 'image/png');
    assert.equal(calls[0].init.body, blob);
    const refusing = new Api('t', async () => new Response('', { status: 410 }));
    await assert.rejects(refusing.postCapture('x', blob), (e) => e.status === 410 && /refused the capture/.test(e.message));
});

class FakeWebSocket {
    static all = [];

    constructor(url) {
        this.url = url;
        this.sent = [];
        this.closedWith = null;
        FakeWebSocket.all.push(this);
    }

    send(text) {
        this.sent.push(JSON.parse(text));
    }

    close(code) {
        this.closedWith = code;
    }

    opened() {
        this.onopen();
    }

    deliver(message) {
        this.onmessage({ data: typeof message === 'string' ? message : JSON.stringify(message) });
    }

    dropped(code = 1006) {
        this.onclose({ code });
    }
}

function socketHarness(t) {
    FakeWebSocket.all = [];
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const socket = new Socket('tok', 'ws://host/api/ws', (url) => new FakeWebSocket(url));
    const log = [];
    for (const name of ['state', 'hello', 'event', 'capture', 'error']) {
        socket.on(name, (...args) => log.push([name, ...args]));
    }
    return { socket, log, sockets: FakeWebSocket.all, tick: (ms) => t.mock.timers.tick(ms) };
}

test('socket: the page authenticates first, then is open once the host says hello', (t) => {
    const { socket, log, sockets } = socketHarness(t);
    assert.equal(socket.state, 'closed');
    assert.equal(socket.send({ op: 'x' }), false, 'nothing is sent before the socket is open');
    socket.start();
    assert.equal(socket.state, 'connecting');
    assert.equal(sockets.length, 1);
    assert.equal(sockets[0].url, 'ws://host/api/ws');
    sockets[0].opened();
    assert.deepEqual(sockets[0].sent, [{ op: 'auth', token: 'tok' }], 'the first message is the token');
    assert.equal(socket.state, 'connecting', 'not open until hello');
    assert.equal(socket.origin, null);
    sockets[0].deliver({ op: 'hello', connection: 'c7', version: 3 });
    assert.equal(socket.state, 'open');
    assert.equal(socket.connection, 'c7');
    assert.equal(socket.origin, 'ui:c7', 'view events carrying this origin are the page\'s own echoes');
    assert.deepEqual(log.filter((e) => e[0] === 'state').map((e) => e[1]), ['connecting', 'open']);
    assert.equal(log.find((e) => e[0] === 'hello')[1].version, 3);
    assert.equal(socket.send({ op: 'ping' }), true);
    assert.deepEqual(sockets[0].sent.at(-1), { op: 'ping' });
});

test('socket: events, captures and errors are passed on; garbage is reported', (t) => {
    const { socket, log, sockets } = socketHarness(t);
    socket.start();
    sockets[0].opened();
    sockets[0].deliver({ op: 'hello', connection: 'c1' });
    sockets[0].deliver({ op: 'event', topic: 'view', data: { origin: 'mcp', view: {} } });
    sockets[0].deliver({ op: 'capture', id: 'cap1', width: 100, height: 50 });
    sockets[0].deliver({ op: 'error', message: 'nope' });
    sockets[0].deliver({ op: 'error' });
    sockets[0].deliver({ op: 'unknown-op' });
    sockets[0].deliver('not json {');
    assert.deepEqual(log.find((e) => e[0] === 'event').slice(1), ['view', { origin: 'mcp', view: {} }]);
    assert.equal(log.find((e) => e[0] === 'capture')[1].id, 'cap1');
    assert.deepEqual(log.filter((e) => e[0] === 'error').map((e) => e[1]), ['nope', 'The host reported an error.', 'The host sent a message that is not JSON.']);
    assert.equal(socket.sendCaptureFailed('cap1', 'too big'), true);
    assert.deepEqual(sockets[0].sent.at(-1), { op: 'captureFailed', id: 'cap1', message: 'too big' });
});

test('socket: commands run over the socket and resolve with the host\'s result', async (t) => {
    const { socket, sockets } = socketHarness(t);
    await assert.rejects(socket.command('x'), (e) => e instanceof ApiError && e.code === 'closed');
    socket.start();
    sockets[0].opened();
    sockets[0].deliver({ op: 'hello', connection: 'c1' });
    const first = socket.command('view.set', { hud: false });
    const second = socket.command('query.height', { x: 1 });
    const [c1, c2] = sockets[0].sent.slice(1);
    assert.deepEqual([c1.op, c1.name, c1.args], ['command', 'view.set', { hud: false }]);
    assert.notEqual(c1.id, c2.id, 'each command has its own id');
    sockets[0].deliver({ op: 'result', id: c2.id, result: { ok: true, data: 2 } });
    sockets[0].deliver({ op: 'result', id: 9999, result: { ok: true } });
    sockets[0].deliver({ op: 'result', id: c1.id, result: { ok: true, data: 1 } });
    assert.deepEqual(await second, { ok: true, data: 2 });
    assert.deepEqual(await first, { ok: true, data: 1 });
});

test('socket: a command that gets no answer times out', async (t) => {
    const { socket, sockets, tick } = socketHarness(t);
    socket.start();
    sockets[0].opened();
    sockets[0].deliver({ op: 'hello', connection: 'c1' });
    const pending = socket.command('slow', {}, 5000);
    const rejected = assert.rejects(pending, (e) => e.code === 'timeout' && /slow did not answer within 5 s/.test(e.message));
    tick(5000);
    await rejected;
    const late = sockets[0].sent.at(-1);
    sockets[0].deliver({ op: 'result', id: late.id, result: { ok: true } });
});

test('socket: a refused token stops reconnecting; a dropped connection reconnects with backoff', async (t) => {
    const { socket, log, sockets, tick } = socketHarness(t);
    socket.start();
    sockets[0].opened();
    sockets[0].deliver({ op: 'hello', connection: 'c1' });
    const inflight = socket.command('x', {}, 60_000);
    const rejected = assert.rejects(inflight, (e) => e.code === 'closed');
    sockets[0].dropped(1006);
    await rejected;
    assert.equal(socket.state, 'closed');
    assert.equal(socket.connection, null);
    assert.equal(sockets.length, 1);
    tick(399);
    assert.equal(sockets.length, 1, 'the first retry waits 400 ms');
    tick(1);
    assert.equal(sockets.length, 2);
    assert.equal(socket.state, 'connecting');

    sockets[1].dropped(1006);
    tick(799);
    assert.equal(sockets.length, 2);
    tick(1);
    assert.equal(sockets.length, 3, 'the second retry waits 800 ms');

    sockets[2].opened();
    sockets[2].deliver({ op: 'hello', connection: 'c2' });
    sockets[2].dropped(1001);
    tick(400);
    assert.equal(sockets.length, 4, 'a successful hello resets the backoff');

    sockets[3].dropped(4401);
    assert.equal(socket.state, 'unauthorized');
    assert.match(log.filter((e) => e[0] === 'error').at(-1)[1], /refused the token/);
    tick(120_000);
    assert.equal(sockets.length, 4, 'a refused token is not retried');

    // A socket that was replaced cannot disturb the one in use.
    const stale = sockets[0];
    stale.dropped(1006);
    assert.equal(socket.state, 'unauthorized');
});

test('socket: stop closes cleanly and does not reconnect; a factory that throws is retried', (t) => {
    const { socket, sockets, tick, log } = socketHarness(t);
    socket.start();
    sockets[0].opened();
    sockets[0].deliver({ op: 'hello', connection: 'c1' });
    socket.stop();
    assert.equal(sockets[0].closedWith, 1000);
    sockets[0].dropped(1000);
    tick(60_000);
    assert.equal(sockets.length, 1);

    let attempts = 0;
    const flaky = new Socket('t', 'ws://x', () => {
        attempts++;
        if (attempts === 1) {
            throw new Error('blocked');
        }
        return new FakeWebSocket('ws://x');
    });
    flaky.on('error', (m) => log.push(['flaky-error', m]));
    flaky.start();
    assert.equal(attempts, 1);
    assert.deepEqual(log.at(-1), ['flaky-error', 'Cannot open the WebSocket: blocked']);
    tick(400);
    assert.equal(attempts, 2);
    flaky.stop();

    assert.equal(Socket.urlFor({ protocol: 'http:', host: '127.0.0.1:5000' }), 'ws://127.0.0.1:5000/api/ws');
    assert.equal(Socket.urlFor({ protocol: 'https:', host: 'atlas.test' }), 'wss://atlas.test/api/ws');
});

test('app: once stopped, a late refusal from the old token announces nothing', async () => {
    const { App } = await import('../../../tools/ArcaneCore.Atlas/Web/js/app.js');
    const { Emitter } = await import('../../../tools/ArcaneCore.Atlas/Web/js/util.js');
    const api = new Emitter();
    const socket = new Emitter();
    socket.connection = null;
    socket.origin = null;
    socket.started = 0;
    socket.stopped = 0;
    socket.start = () => socket.started++;
    socket.stop = () => socket.stopped++;
    const app = new App({ api, socket });
    const states = [];
    app.events.on('connection', (c) => states.push(c.state));
    app.start();
    assert.equal(socket.started, 1);

    api.emit('unauthorized');
    assert.deepEqual(states, ['unauthorized']);

    app.stop();
    assert.equal(app.stopped, true);
    assert.equal(socket.stopped, 1);
    // A tile request and the socket close both still in flight when the gate came up: neither may reopen it.
    api.emit('unauthorized');
    socket.emit('state', 'unauthorized');
    assert.deepEqual(states, ['unauthorized']);
});
