// The public page (design 0.6 browser test, 2.4): the API client with the CSRF header, the socket's auth and reconnect rules, the
// model that keeps only projected fields, the chart (player cells drawn, no field a guest was not given), the sign-in form and
// the page wiring. A fake DOM, fake fetch and fake WebSocket stand in for the browser; synthetic data only.

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { FakeWebSocket, installFakeDom } from './support/fakedom.mjs';

installFakeDom();

const here = path.dirname(fileURLToPath(import.meta.url));
const PUBLIC_DIR = path.resolve(here, '../../../tools/ArcaneCore.Atlas/Web/js/public');
const WEB_DIR = path.resolve(PUBLIC_DIR, '../..');
const { PublicApi, PublicApiError, PublicCommandError, PublicSocket, unwrapCommand } = await import('../../../tools/ArcaneCore.Atlas/Web/js/public/public-api.js');
const { Camera, MapModel, KIND_LAYER, drawMap, hitTest, initialLayers, FALLBACK_LAYERS } = await import('../../../tools/ArcaneCore.Atlas/Web/js/public/map.js');
const { LoginForm, checkLoginInput, loginMessage } = await import('../../../tools/ArcaneCore.Atlas/Web/js/public/login.js');
const { PublicPage, statusText } = await import('../../../tools/ArcaneCore.Atlas/Web/js/public/page.js');

const tick = () => new Promise((resolve) => setImmediate(resolve));

function jsonResponse(status, body) {
    return { ok: status >= 200 && status < 300, status, json: async () => body };
}

function fakeFetch(handler) {
    const calls = [];
    const fetchImpl = async (route, init) => {
        calls.push({ route, init });
        return handler(route, init);
    };
    fetchImpl.calls = calls;
    return fetchImpl;
}

// ---- the API client -------------------------------------------------------------------------------------------------------------

test('me adopts the session and its CSRF token; posts carry the token, gets do not', async () => {
    const fetchImpl = fakeFetch((route, init) => {
        if (route === '/api/public/me') {
            return jsonResponse(200, { ok: true, tier: 'guest', account: null, security: null, csrf: 'tok-1', idleMinutes: 30, maxHours: 12 });
        }
        return jsonResponse(200, { command: 'guest.maps', ok: true, data: [{ id: 0, name: 'Eastern Kingdoms' }] });
    });
    const api = new PublicApi({ fetchImpl });

    await api.me();
    const maps = await api.maps();

    assert.equal(api.csrf, 'tok-1');
    assert.equal(api.tier, 'guest');
    assert.deepEqual(maps, [{ id: 0, name: 'Eastern Kingdoms' }]);
    const [get, post] = fetchImpl.calls;
    assert.equal(get.init.method, 'GET');
    assert.equal(get.init.headers['X-Atlas-CSRF'], undefined);
    assert.equal(post.init.method, 'POST');
    assert.equal(post.init.headers['X-Atlas-CSRF'], 'tok-1');
    assert.equal(post.init.credentials, 'same-origin');
    assert.equal(post.init.cache, 'no-store');
    assert.deepEqual(JSON.parse(post.init.body), { name: 'guest.maps', args: {} });
});

test('login adopts the rotated token; a failure is a PublicApiError with the server code and reference', async () => {
    let attempt = 0;
    const fetchImpl = fakeFetch((route) => {
        if (route === '/api/public/login') {
            attempt++;
            return attempt === 1
                ? jsonResponse(401, { ok: false, error: { code: 'login-failed', ref: 'ab12cd34' } })
                : jsonResponse(200, { ok: true, tier: 'account', account: 'GM', security: 1, csrf: 'tok-2', idleMinutes: 30, maxHours: 12 });
        }
        return jsonResponse(200, { ok: true });
    });
    const api = new PublicApi({ fetchImpl });
    api.csrf = 'tok-1';

    await assert.rejects(api.login('gm', 'nope'), (e) => e instanceof PublicApiError && e.status === 401 && e.code === 'login-failed' && e.ref === 'ab12cd34');
    assert.equal(api.csrf, 'tok-1', 'a failed login changes nothing');

    await api.login('gm', 'pw', '123456');
    assert.deepEqual([api.tier, api.account, api.security, api.csrf], ['account', 'GM', 1, 'tok-2']);
    assert.deepEqual(JSON.parse(fetchImpl.calls[1].init.body), { user: 'gm', password: 'pw', code: '123456' });
    await api.login('gm', 'pw');
    assert.equal('code' in JSON.parse(fetchImpl.calls[2].init.body), false, 'no code, no field');
});

test('a command that answers ok:false throws its generic error; an unreachable server and a non-JSON answer are errors too', async () => {
    const api = new PublicApi({
        fetchImpl: fakeFetch(() => jsonResponse(200, { command: 'live.find', ok: false, error: { code: 'not-available', message: 'That command is not available here.' } })),
    });
    await assert.rejects(api.command('live.find', { name: 'x' }), (e) => e instanceof PublicCommandError && e.code === 'not-available' && e.message === 'That command is not available here.');

    const down = new PublicApi({ fetchImpl: async () => { throw new TypeError('network'); } });
    await assert.rejects(down.me(), (e) => e instanceof PublicApiError && e.code === 'unreachable');

    const html = new PublicApi({ fetchImpl: async () => ({ ok: true, status: 200, json: async () => { throw new SyntaxError('x'); } }) });
    await assert.rejects(html.me(), (e) => e instanceof PublicApiError && e.code === 'bad-answer');

    assert.throws(() => unwrapCommand(null, 'x'), (e) => e instanceof PublicCommandError && e.code === 'failed');
    assert.equal(unwrapCommand({ ok: true, data: 5 }), 5);
});

// ---- the socket -----------------------------------------------------------------------------------------------------------------

function socketRig({ protocol = 'http:', backoff = [100, 200] } = {}) {
    FakeWebSocket.instances.length = 0;
    const timers = [];
    const api = { csrf: 'csrf-1', meCalls: 0, meError: null, async me() { this.meCalls++; if (this.meError) { throw this.meError; } this.csrf = this.nextCsrf ?? this.csrf; } };
    const socket = new PublicSocket(api, {
        WebSocketImpl: FakeWebSocket,
        location: { protocol, host: 'atlas.test:8443' },
        setTimer: (fn, ms) => { timers.push({ fn, ms }); return timers.length; },
        clearTimer: (id) => { timers[id - 1] = null; },
        backoff,
    });
    return { api, socket, timers, ws: () => FakeWebSocket.instances.at(-1) };
}

test('the socket authenticates with the CSRF token first, then delivers hello, frames and status', async () => {
    const { socket, ws } = socketRig({ protocol: 'https:' });
    const seen = [];
    socket.on('state', (s) => seen.push(['state', s]));
    socket.on('hello', (m) => seen.push(['hello', m.tier]));
    socket.on('frame', (d) => seen.push(['frame', d.seq]));
    socket.on('status', (d) => seen.push(['status', d.detail]));

    socket.connect();
    assert.equal(ws().url, `wss:${'//'}atlas.test:8443/api/public/ws`);
    ws().open();
    assert.deepEqual(ws().sent, [{ op: 'auth', csrf: 'csrf-1' }]);
    ws().deliver({ op: 'hello', tier: 'guest', maps: [{ id: 0, name: 'A' }] });
    ws().deliver({ op: 'event', topic: 'live.status', data: { detail: 'waiting' } });
    ws().deliver({ op: 'event', topic: 'live.frame', data: { seq: 7 } });
    ws().deliver({ op: 'event', topic: 'something.else', data: {} });
    ws().onmessage({ data: 'not json' });

    assert.deepEqual(seen, [['state', 'connecting'], ['state', 'open'], ['hello', 'guest'], ['status', 'waiting'], ['frame', 7]]);
    assert.equal(socket.ready, true);
    assert.equal(new PublicSocket({}, { WebSocketImpl: FakeWebSocket, location: { protocol: 'http:', host: 'h' } }).ready, false);
});

test('a socket over plain http uses ws', () => {
    const { socket, ws } = socketRig({ protocol: 'http:' });
    socket.connect();
    assert.ok(ws().url.startsWith(`ws:${'//'}`));
});

test('commands go over the socket by id; a failure rejects; a closed socket rejects what is pending', async () => {
    const { socket, ws } = socketRig();
    socket.connect();
    ws().open();
    await assert.rejects(socket.command('guest.status'), (e) => e.code === 'closed', 'no command before hello');
    ws().deliver({ op: 'hello', tier: 'guest', maps: [] });

    const ok = socket.command('live.find', { name: 'Boar' });
    const bad = socket.command('server.console', { line: 'x' });
    const stuck = socket.command('guest.maps');
    const sent = ws().sent.filter((m) => m.op === 'command');
    assert.deepEqual(sent.map((m) => m.name), ['live.find', 'server.console', 'guest.maps']);
    assert.equal(new Set(sent.map((m) => m.id)).size, 3);
    ws().deliver({ op: 'result', id: sent[0].id, result: { ok: true, data: { count: 1 } } });
    ws().deliver({ op: 'result', id: sent[1].id, result: { ok: false, command: 'server.console', error: { code: 'not-available', message: 'That command is not available here.' } } });

    assert.deepEqual(await ok, { count: 1 });
    await assert.rejects(bad, (e) => e instanceof PublicCommandError && e.code === 'not-available');
    ws().serverClose(1008);
    await assert.rejects(stuck, (e) => e.code === 'closed');
});

test('a socket the server closes reconnects after refreshing the session, with the new token', async () => {
    const { api, socket, timers, ws } = socketRig({ backoff: [100, 200] });
    socket.connect();
    ws().open();
    ws().deliver({ op: 'hello', tier: 'guest', maps: [] });
    const first = ws();

    api.nextCsrf = 'csrf-2'; // a login happened: the server closed the guest socket (1008) and rotated the token
    first.serverClose(1008);
    assert.equal(timers.length, 1);
    assert.equal(timers[0].ms, 100);
    timers[0].fn();
    await tick();

    assert.equal(api.meCalls, 1, 'the session is refreshed before the socket reopens');
    assert.notEqual(ws(), first);
    ws().open();
    assert.deepEqual(ws().sent, [{ op: 'auth', csrf: 'csrf-2' }]);

    ws().serverClose(1006); // lost again before hello: the next delay is longer
    assert.equal(timers.at(-1).ms, 200);
});

test('a session that no longer exists is reported as expired and the socket stays closed', async () => {
    const { api, socket, timers, ws } = socketRig();
    let expired = 0;
    socket.on('expired', () => expired++);
    socket.connect();
    ws().open();
    api.meError = new PublicApiError('x', { status: 401, code: 'no-session' });
    ws().serverClose(4401);
    timers[0].fn();
    await tick();

    assert.equal(expired, 1);
    assert.equal(FakeWebSocket.instances.length, 1, 'no new socket for a dead session');
});

test('close() stops reconnecting', () => {
    const { socket, timers, ws } = socketRig();
    socket.connect();
    ws().open();
    ws().serverClose(1006);
    assert.equal(timers.length, 1);
    socket.close();
    assert.equal(timers[0], null, 'the pending retry is cancelled');
});

// ---- the model, camera and drawing ----------------------------------------------------------------------------------------------

const ID_A = '00000000000000a1';
const ID_B = '00000000000000b2';
const ID_C = '00000000000000c3';

function frame(extra = {}) {
    return {
        seq: 5,
        clockMs: 2500,
        map: 0,
        instance: 0,
        reduced: false,
        entities: [
            { g: ID_A, k: 'creature', e: 5, n: 'Boar', x: 100, y: 200, z: 10, o: 1.5, hp: 80, mt: 'chase' },
            { g: ID_B, k: 'gameobject', e: 9, n: 'Mailbox', x: 110, y: 190, z: 10, o: 0 },
            { g: ID_C, k: 'character', n: 'Player', x: 120, y: 180, z: 10, o: 3, hp: 100, mt: 'moving' },
        ],
        playerCells: [{ x: 1000, y: 2000, n: 7 }],
        cellYards: 250,
        ...extra,
    };
}

test('the model keeps only the projected fields and ignores everything else a frame carries', () => {
    const model = new MapModel();
    model.reset(0);
    const poisoned = frame();
    poisoned.entities[0].guid = 'CANARY-GUID';
    poisoned.entities[0].tg = 'CANARY-TARGET';
    poisoned.entities[0].home = [1, 2, 3];
    poisoned.entities.push({ g: 'not-an-id', k: 'creature', x: 1, y: 1 }, { g: '00000000000000d4', k: 'dragon', x: 1, y: 1 }, { g: '00000000000000e5', k: 'creature', x: 'far', y: 1 }, null, 'x');

    assert.equal(model.applyFrame(poisoned), true);

    assert.equal(model.entities.size, 3);
    const boar = model.entities.get(ID_A);
    assert.deepEqual(Object.keys(boar).sort(), ['entry', 'hp', 'id', 'kind', 'motion', 'name', 'o', 'x', 'y', 'z']);
    assert.equal(JSON.stringify([...model.entities.values()]).includes('CANARY'), false);
    assert.deepEqual(model.cells, [{ x: 1000, y: 2000, n: 7 }]);
    assert.equal(model.cellYards, 250);
    assert.equal(model.seq, 5);
});

test('a frame of another map is refused, a bad frame changes nothing, and status updates the connection', () => {
    const model = new MapModel();
    model.reset(1);
    assert.equal(model.applyFrame(frame()), false);
    assert.equal(model.applyFrame(null), false);
    assert.equal(model.applyFrame({ seq: 'x', map: 1 }), false);
    model.applyStatus({ connected: true, detail: '', map: 0 });
    assert.equal(model.connected, false, 'status for another map is ignored');
    model.applyStatus({ connected: true, detail: 'reduced', map: 1 });
    assert.deepEqual([model.connected, model.detail], [true, 'reduced']);
    assert.equal(model.applyFrame({ ...frame(), map: 1, playerCells: 'nope', entities: 'nope' }), true);
    assert.equal(model.entities.size, 0);
    assert.deepEqual(model.cells, []);
});

test('north is up and east is right; panning follows the pointer and zooming keeps the point under it', () => {
    const camera = new Camera();
    camera.centerOn(1000, 2000);
    camera.scale = 0.5;
    const [cx, cy] = camera.worldToScreen(1000, 2000, 800, 600);
    assert.deepEqual([cx, cy], [400, 300]);
    const [nx, ny] = camera.worldToScreen(1100, 2000, 800, 600);
    assert.ok(ny < cy && nx === cx, 'a larger x (north) is higher on the screen');
    const [ex, ey] = camera.worldToScreen(1000, 1900, 800, 600);
    assert.ok(ex > cx && ey === cy, 'a smaller y (east) is further right');

    const world = camera.screenToWorld(123, 456, 800, 600);
    const back = camera.worldToScreen(world[0], world[1], 800, 600);
    assert.ok(Math.abs(back[0] - 123) < 1e-9 && Math.abs(back[1] - 456) < 1e-9);

    const under = camera.screenToWorld(600, 100, 800, 600);
    camera.zoomAt(600, 100, 2, 800, 600);
    const after = camera.worldToScreen(under[0], under[1], 800, 600);
    assert.ok(Math.abs(after[0] - 600) < 1e-9 && Math.abs(after[1] - 100) < 1e-9);
    assert.equal(camera.scale, 1);

    const point = camera.worldToScreen(1050, 1950, 800, 600);
    camera.panByPixels(30, -20);
    const moved = camera.worldToScreen(1050, 1950, 800, 600);
    assert.ok(Math.abs(moved[0] - (point[0] + 30)) < 1e-9 && Math.abs(moved[1] - (point[1] - 20)) < 1e-9);

    camera.fit([[0, 0], [1000, 4000]], 800, 600);
    assert.deepEqual([camera.cx, camera.cy], [500, 2000]);
    for (const [x, y] of [[0, 0], [1000, 4000]]) {
        const [sx, sy] = camera.worldToScreen(x, y, 800, 600);
        assert.ok(sx >= 0 && sx <= 800 && sy >= 0 && sy <= 600, 'the fitted points are on screen');
    }
});

test('the chart draws the coarse player cells with their counts, and the entities of the visible layers only', () => {
    const model = new MapModel();
    model.reset(0);
    model.applyFrame(frame());
    const camera = new Camera();
    camera.centerOn(500, 1000);
    camera.scale = 0.4;
    const ctx = new (class { constructor() { this.calls = []; } })();
    const rec = new Proxy(ctx, { get: (t, n) => (n in t ? t[n] : (...a) => t.calls.push([String(n), ...a])), set: (t, n, v) => { t.calls.push(['set', String(n), v]); return true; } });

    const layers = initialLayers(FALLBACK_LAYERS);
    const drawn = drawMap(rec, model, camera, layers, FALLBACK_LAYERS, 800, 600, ID_A);

    const texts = ctx.calls.filter((c) => c[0] === 'fillText').map((c) => c[1]);
    assert.ok(texts.includes('7'), 'the cell count is drawn');
    assert.equal(ctx.calls.filter((c) => c[0] === 'strokeRect').length, 1, 'one cell outline');
    assert.equal(drawn, 3);
    assert.ok(ctx.calls.some((c) => c[0] === 'arc' && c[3] === 9), 'the selected entity has a ring');
    assert.equal(texts.some((t) => String(t).includes('CANARY')), false);

    const hidden = new Set([KIND_LAYER.creature]);
    ctx.calls.length = 0;
    assert.equal(drawMap(rec, model, camera, hidden, FALLBACK_LAYERS, 800, 600), 1, 'only the creature layer is on');
});

test('a click picks the nearest visible entity, else the cell it is inside, else nothing', () => {
    const model = new MapModel();
    model.reset(0);
    model.applyFrame(frame());
    const camera = new Camera();
    camera.centerOn(110, 190);
    camera.scale = 2;
    const layers = initialLayers(FALLBACK_LAYERS);
    const [bx, by] = camera.worldToScreen(110, 190, 800, 600);

    assert.equal(hitTest(model, camera, layers, bx + 2, by - 2, 800, 600).entity.id, ID_B);
    assert.equal(hitTest(model, camera, new Set([KIND_LAYER.creature]), bx, by, 800, 600), null, 'a hidden layer cannot be picked');

    camera.centerOn(1000, 2000);
    camera.scale = 0.2;
    const hit = hitTest(model, camera, layers, 400, 300, 800, 600);
    assert.equal(hit.type, 'cell');
    assert.equal(hit.cell.n, 7);
    assert.equal(hitTest(model, camera, layers, 5, 5, 800, 600), null);
});

// ---- the sign-in form -------------------------------------------------------------------------------------------------------------

test('login input is checked before it is sent, and the code is optional', () => {
    assert.equal(checkLoginInput({ user: '  ', password: 'x' }).field, 'user');
    assert.equal(checkLoginInput({ user: 'a'.repeat(33), password: 'x' }).field, 'user');
    assert.equal(checkLoginInput({ user: 'gm', password: '' }).field, 'password');
    assert.equal(checkLoginInput({ user: 'gm', password: 'p'.repeat(129) }).field, 'password');
    assert.equal(checkLoginInput({ user: 'gm', password: 'p', code: '1'.repeat(17) }).field, 'code');
    assert.deepEqual(checkLoginInput({ user: ' gm ', password: 'pw', code: ' ' }), { ok: true, user: 'gm', password: 'pw', code: undefined });
    assert.equal(checkLoginInput({ user: 'gm', password: 'pw', code: '123456' }).code, '123456');
});

test('one message for a failed login, whatever the cause, and none that names the cause', () => {
    const failed = loginMessage({ code: 'login-failed', status: 401 });
    assert.equal(failed, 'That name, password or code was not accepted.');
    assert.match(loginMessage({ code: 'login-failed', status: 429 }), /Too many attempts/);
    assert.match(loginMessage({ code: 'login-disabled' }), /not available/);
    assert.match(loginMessage({ code: 'unreachable' }), /could not reach/);
    assert.match(loginMessage({ code: 'weird', ref: 'ab12' }), /ab12/);
    assert.doesNotMatch(failed, /banned|locked|unknown|wrong|expired/i);
});

test('the form is a password form, empties the password as it sends it, and shows the one failure message', async () => {
    const calls = [];
    let reject = true;
    const form = new LoginForm({
        onSubmit: async (input) => {
            calls.push({ ...input });
            assert.equal(form.password.value, '', 'the field is already empty while the request runs');
            if (reject) {
                throw new PublicApiError('x', { status: 401, code: 'login-failed' });
            }
        },
    });
    assert.equal(form.password.type, 'password');
    assert.equal(form.password.autocomplete, 'current-password');
    assert.equal(form.user.autocomplete, 'username');

    form.user.value = 'gm';
    form.password.value = 'hunter2';
    form.element.dispatch('submit');
    await tick();
    await tick();

    assert.deepEqual(calls, [{ user: 'gm', password: 'hunter2', code: undefined }]);
    assert.equal(form.password.value, '');
    assert.equal(form.error.hidden, false);
    assert.equal(form.error.textContent, 'That name, password or code was not accepted.');
    assert.equal(form.submit.disabled, false);

    form.element.dispatch('submit'); // nothing typed any more: refused locally
    await tick();
    assert.equal(calls.length, 1);
    assert.match(form.error.textContent, /Enter your password/);

    reject = false;
    form.password.value = 'right';
    form.element.dispatch('submit');
    await tick();
    await tick();
    assert.equal(calls.length, 2);
    assert.equal(form.user.value, '');
    assert.equal(form.error.hidden, true);
});

// ---- the page ---------------------------------------------------------------------------------------------------------------------

class FakeApiSocket {
    constructor() {
        this.listeners = new Map();
        this.ready = true;
        this.commands = [];
        this.views = [];
        this.connects = 0;
        this.answers = new Map();
    }

    on(topic, fn) {
        if (!this.listeners.has(topic)) {
            this.listeners.set(topic, []);
        }
        this.listeners.get(topic).push(fn);
    }

    emit(topic, data) {
        for (const fn of this.listeners.get(topic) ?? []) {
            fn(data);
        }
    }

    connect() {
        this.connects++;
    }

    view(map) {
        this.views.push(map);
    }

    async command(name, args) {
        this.commands.push({ name, args });
        const answer = this.answers.get(name);
        if (answer instanceof Error) {
            throw answer;
        }
        return answer ?? {};
    }
}

function pageRig({ loginAvailable = true } = {}) {
    const api = {
        tier: 'guest',
        account: null,
        security: 0,
        csrf: 'c1',
        logins: [],
        loginError: null,
        async status() { return { login: { available: loginAvailable } }; },
        async me() {},
        async layers() { return FALLBACK_LAYERS; },
        async bookmarks() { return [{ name: 'Goldshire', map: 0, at: [-9462, 16, 53], distance: 300 }]; },
        async login(user, password, code) {
            this.logins.push({ user, password, code });
            if (this.loginError) {
                throw this.loginError;
            }
            this.tier = 'account';
            this.account = 'GM';
            this.security = 1;
        },
        async logout() { this.tier = 'guest'; this.account = null; this.security = 0; },
        async characters() { return { count: 1, characters: [{ g: 'x', n: 'Mine', map: 0, x: 5, y: 6, level: 9 }] }; },
        async command() { return {}; },
    };
    const socket = new FakeApiSocket();
    const root = document.createElement('div');
    const page = new PublicPage(root, { api, socket, schedule: (fn) => fn() });
    return { api, socket, root, page };
}

async function started(options) {
    const rig = pageRig(options);
    await rig.page.start();
    rig.page.resize(800, 600);
    rig.socket.emit('hello', { tier: 'guest', maps: [{ id: 0, name: 'Eastern Kingdoms' }, { id: 1, name: 'Kalimdor' }] });
    return rig;
}

test('the page renders the guest chart: player cells, counts, a legend, no guid anywhere', async () => {
    const { root, socket, page } = await started();
    const poisoned = frame();
    poisoned.entities[0].guid = 'CANARY-GUID';
    poisoned.entities[0].home = [9, 9, 9];

    socket.emit('state', 'open');
    socket.emit('status', { connected: true, detail: '', map: 0 });
    socket.emit('frame', poisoned);

    assert.equal(socket.connects, 1);
    assert.equal(page.counts.textContent, '1 creatures, 1 objects, 1 characters, 1 busy area');
    assert.equal(page.statusLine.textContent, 'Live.');
    assert.equal(root.textContent.includes('CANARY'), false);
    page.camera.centerOn(1000, 2000); // the fitted view frames the entities; bring the busy area on screen
    page.camera.scale = 0.4;
    page.invalidate();
    const texts = page.canvas.getContext().calls.filter((c) => c[0] === 'fillText').map((c) => c[1]);
    assert.ok(texts.includes('7'), 'the player cell count was drawn on the canvas');
    assert.equal(texts.includes('CANARY-GUID'), false);
    assert.equal(page.mapSelect.children.length, 2);
    assert.equal(page.mapSelect.value, '0');
    assert.ok(root.byText('Sign in'), 'a guest is offered sign-in');
    assert.equal(root.byText('Several players are in this area') !== null || root.textContent.includes('Several players are in this area'), true);
});

test('the status line follows the socket and the map: waiting, busy, reduced, reconnecting', () => {
    const model = new MapModel();
    model.reset(0);
    assert.equal(statusText({ socketState: 'connecting', model }).text, 'Connecting…');
    assert.equal(statusText({ socketState: 'closed', model }).level, 'warn');
    assert.equal(statusText({ socketState: 'open', model }).text, 'Waiting for the server…');
    model.applyStatus({ connected: false, detail: 'busy', map: 0 });
    assert.match(statusText({ socketState: 'open', model }).text, /in use/);
    model.applyStatus({ connected: true, detail: '', map: 0 });
    model.applyFrame({ ...frame(), reduced: true });
    assert.match(statusText({ socketState: 'open', model }).text, /hidden/);
    model.applyFrame(frame());
    assert.equal(statusText({ socketState: 'open', model }).level, 'ok');
    assert.match(statusText({ socketState: 'open', model, loginAvailable: false }).text, /not offered/);
});

test('clicking an entity asks pick.public with its opaque id and shows only what comes back; a cell shows its count', async () => {
    const { root, socket, page } = await started();
    socket.emit('status', { connected: true, detail: '', map: 0 });
    socket.emit('frame', frame());
    socket.answers.set('pick.public', { name: 'Boar', type: 'Creature', level: 3, faction: 'Hostile', hp: 80.4, motion: 'chase', secret: 'CANARY-EXTRA' });
    const boar = page.model.entities.get(ID_A);
    const [sx, sy] = page.camera.worldToScreen(boar.x, boar.y, page.width, page.height);

    page.canvas.dispatch('pointerdown', { clientX: sx, clientY: sy, pointerId: 1 });
    page.canvas.dispatch('pointerup', { clientX: sx, clientY: sy, pointerId: 1 });
    await tick();
    await tick();

    assert.deepEqual(socket.commands, [{ name: 'pick.public', args: { id: ID_A, map: 0 } }]);
    const text = page.info.textContent;
    for (const expected of ['Boar', 'Creature', 'Level', '3', 'Faction', 'Hostile', '80%', 'chase']) {
        assert.ok(text.includes(expected), `${expected} is shown`);
    }
    assert.equal(text.includes('CANARY'), false, 'a field the page does not know is not rendered');
    assert.equal(page.selectedId, ID_A);

    const [cx, cy] = page.camera.worldToScreen(1000, 2000, page.width, page.height);
    page.canvas.dispatch('pointerdown', { clientX: cx, clientY: cy, pointerId: 1 });
    page.canvas.dispatch('pointerup', { clientX: cx, clientY: cy, pointerId: 1 });
    await tick();
    assert.ok(page.info.textContent.includes('7 in this area'));
    assert.equal(socket.commands.length, 1, 'a cell click asks the server nothing');
    assert.equal(root.textContent.includes('CANARY'), false);
});

test('dragging pans the chart and does not pick', async () => {
    const { socket, page } = await started();
    socket.emit('frame', frame());
    const before = [page.camera.cx, page.camera.cy];

    page.canvas.dispatch('pointerdown', { clientX: 100, clientY: 100, pointerId: 1 });
    page.canvas.dispatch('pointermove', { clientX: 160, clientY: 130, pointerId: 1 });
    page.canvas.dispatch('pointerup', { clientX: 160, clientY: 130, pointerId: 1 });
    await tick();

    assert.notDeepEqual([page.camera.cx, page.camera.cy], before);
    assert.equal(socket.commands.length, 0);
});

test('find validates the text, asks live.find for the viewed map and lists what comes back', async () => {
    const { socket, page } = await started();
    socket.answers.set('live.find', { entities: [{ id: ID_A, kind: 'creature', name: 'Boar', at: [100, 200, 10] }] });

    await page.find('x; .server shutdown');
    assert.equal(socket.commands.length, 0, 'text outside the allowed set is never sent');
    assert.equal(page.message.hidden, false);

    await page.find('Boar');
    assert.deepEqual(socket.commands, [{ name: 'live.find', args: { name: 'Boar', map: 0 } }]);
    const button = page.results.byTag('button')[0];
    assert.equal(button.textContent, 'Boar (Creature)');
    button.dispatch('click');
    assert.deepEqual([page.camera.cx, page.camera.cy], [100, 200]);
    assert.equal(page.selectedId, ID_A);
});

test('choosing a map tells the socket and resets the chart; frames of the old map are dropped', async () => {
    const { socket, page } = await started();
    socket.emit('frame', frame());
    assert.equal(page.model.entities.size, 3);

    page.mapSelect.value = '1';
    page.mapSelect.dispatch('change');
    assert.deepEqual(socket.views, [1]);
    assert.equal(page.model.entities.size, 0);
    socket.emit('frame', frame());
    assert.equal(page.model.entities.size, 0, 'a late frame of map 0 does not draw on map 1');
    socket.emit('frame', { ...frame(), map: 1, seq: 9 });
    assert.equal(page.model.entities.size, 3);

    page.mapSelect.value = '99';
    page.mapSelect.dispatch('change');
    assert.deepEqual(socket.views, [1], 'a map the server did not list is never requested');
});

test('layer checkboxes hide what they name', async () => {
    const { socket, page } = await started();
    socket.emit('frame', frame());
    const boxes = page.layerBox.byTag('input');
    assert.equal(boxes.length, 3);

    boxes[1].checked = false; // creatures
    boxes[1].dispatch('change');

    assert.ok(page.counts.textContent.startsWith('0 creatures'));
});

test('sign-in: the form opens, a wrong password shows the one message, a right one signs in and lists the account\'s characters', async () => {
    const { api, root, page } = await started();
    root.byText('Sign in').dispatch('click');
    assert.equal(page.loginHost.hidden, false);
    const form = page.loginForm;

    api.loginError = new PublicApiError('x', { status: 401, code: 'login-failed' });
    form.user.value = 'gm';
    form.password.value = 'wrong';
    form.element.dispatch('submit');
    await tick();
    await tick();
    assert.equal(form.error.textContent, 'That name, password or code was not accepted.');
    assert.equal(api.tier, 'guest');
    assert.equal(form.password.value, '');

    api.loginError = null;
    form.password.value = 'right';
    form.element.dispatch('submit');
    await tick();
    await tick();
    await tick();

    assert.deepEqual(api.logins.at(-1), { user: 'gm', password: 'right', code: undefined });
    assert.equal(page.loginHost.hidden, true);
    assert.equal(page.account.textContent.includes('GM · Moderator'), true);
    assert.equal(page.charactersPanel.hidden, false);
    assert.equal(page.characterList.byTag('button')[0].textContent, 'Mine · level 9');
    assert.equal(form.password.value, '');
    root.byText('Sign out').dispatch('click');
    await tick();
    await tick();
    assert.equal(api.tier, 'guest');
    assert.equal(page.charactersPanel.hidden, true);
    assert.ok(root.byText('Sign in'));
});

test('a server that offers no sign-in shows no sign-in button', async () => {
    const { root } = await started({ loginAvailable: false });
    assert.equal(root.byText('Sign in'), null);
});

test('an expired visit is renewed and the socket reconnects', async () => {
    const { socket } = await started();
    socket.emit('expired', null);
    await tick();
    await tick();
    assert.equal(socket.connects, 2);
});

// ---- the sources ------------------------------------------------------------------------------------------------------------------

test('no source of the public page reads a guid, builds markup from strings, or reaches outside this server', () => {
    const files = fs.readdirSync(PUBLIC_DIR).filter((f) => f.endsWith('.js'));
    assert.deepEqual(files.sort(), ['boot.js', 'login.js', 'map.js', 'page.js', 'public-api.js']);
    for (const file of files) {
        const source = fs.readFileSync(path.join(PUBLIC_DIR, file), 'utf8');
        assert.doesNotMatch(source, /guid/i, `${file} mentions a guid`);
        assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|document\.write|\beval\s*\(|new Function/, `${file} builds markup or code from strings`);
        assert.doesNotMatch(source, /localStorage|sessionStorage|document\.cookie/, `${file} stores something in the browser`);
    }
});

test('public.html loads only this server\'s files and runs no inline code', () => {
    const html = fs.readFileSync(path.join(WEB_DIR, 'public.html'), 'utf8');
    assert.match(html, /<script type="module" src="\/app\/js\/public\/boot\.js"><\/script>/);
    assert.doesNotMatch(html, /index\.html|js\/main\.js/);
    assert.doesNotMatch(html, /<script(?![^>]*\bsrc=)|<style|\son[a-z]+\s*=|\sstyle\s*=/i);
    for (const [, target] of html.matchAll(/\b(?:src|href)="([^"]*)"/g)) {
        assert.ok(target.startsWith('/app/') || target.startsWith('data:'), `${target} points outside /app/`);
        if (target.startsWith('/app/')) {
            assert.ok(fs.existsSync(path.join(WEB_DIR, target.slice('/app/'.length))), `${target} does not exist`);
        }
    }
});
