// Multi-session presence in the UI: the pure presence module (normalizePresence, PresenceStore, presenceMessage,
// peerLabelCandidates) and the App wiring (ui.presence events, per-UI cameras). No DOM, no network; the socket is a fake.

import assert from 'node:assert/strict';
import test from 'node:test';
import { App } from '../../../tools/ArcaneCore.Atlas/Web/js/app.js';
import {
    MAX_COORDINATE, PEER_COLOR, PEER_PRIORITY, PresenceStore, cleanName, normalizePresence, peerLabelCandidates, presenceMessage,
} from '../../../tools/ArcaneCore.Atlas/Web/js/presence.js';
import { defaultView } from '../../../tools/ArcaneCore.Atlas/Web/js/view.js';
import { Emitter } from '../../../tools/ArcaneCore.Atlas/Web/js/util.js';

const peer = (over = {}) => ({ id: 'abcd1234ef56', name: 'Ann', map: 1, at: [1, 2, 3], yaw: 90, mode: 'orbit', ...over });

// ---- normalizePresence ----

test('normalizePresence: a well-formed entry is copied and its yaw wrapped into [0, 360)', () => {
    const raw = peer({ yaw: -90 });
    const got = normalizePresence(raw);
    assert.deepEqual(got, { id: 'abcd1234ef56', name: 'Ann', map: 1, at: [1, 2, 3], yaw: 270, mode: 'orbit', kind: 'ui' });
    assert.notEqual(got.at, raw.at, 'the position array is copied');
    assert.equal(normalizePresence(peer({ yaw: 360 })).yaw, 0);
    for (const mode of ['orbit', 'fly', 'top']) {
        assert.equal(normalizePresence(peer({ mode })).mode, mode);
    }
    assert.equal(normalizePresence(peer({ map: 0 })).map, 0);
    assert.equal(normalizePresence(peer({ map: 0xFFFFFFFF })).map, 0xFFFFFFFF);
});

test('normalizePresence: a missing or blank name falls back to "session " plus the first four id chars', () => {
    assert.equal(normalizePresence(peer({ name: undefined })).name, 'session abcd');
    assert.equal(normalizePresence(peer({ name: '   ' })).name, 'session abcd');
    assert.equal(normalizePresence(peer({ name: 7 })).name, 'session abcd');
    assert.equal(normalizePresence(peer({ name: '' })).name, 'session abcd');
});

test('normalizePresence: malformed entries are null', () => {
    const bad = [
        null, undefined, 5, 'x',
        peer({ id: undefined }), peer({ id: '' }), peer({ id: 4 }),
        peer({ map: -1 }), peer({ map: 1.5 }), peer({ map: 0x100000000 }), peer({ map: '1' }), peer({ map: undefined }),
        peer({ at: undefined }), peer({ at: [1, 2] }), peer({ at: [1, 2, 3, 4] }), peer({ at: [1, 2, '3'] }),
        peer({ at: [1, Number.NaN, 3] }), peer({ at: [1, 2, Infinity] }),
        peer({ yaw: Number.NaN }), peer({ yaw: '5' }), peer({ yaw: undefined }),
        peer({ mode: 'swim' }), peer({ mode: undefined }), peer({ mode: 'ORBIT' }),
    ];
    for (const raw of bad) {
        assert.equal(normalizePresence(raw), null, JSON.stringify(raw));
    }
});

// ---- PresenceStore ----

test('PresenceStore.reset: replaces the list, skips this connection and malformed entries', () => {
    const store = new PresenceStore();
    store.reset('me', [peer({ id: 'old1' })]);
    store.reset('me', [peer({ id: 'me' }), peer({ id: 'b222', name: 'Bob' }), { id: 'bad' }, null, peer({ id: 'c333', map: 0 })]);
    assert.deepEqual(store.peers().map((p) => p.id), ['b222', 'c333']);

    store.reset('me', undefined);
    assert.deepEqual(store.peers(), []);
    store.reset(null, 'not an array');
    assert.deepEqual(store.peers(), []);
});

test('PresenceStore.apply: adds, updates, reports change and ignores own and malformed entries', () => {
    const store = new PresenceStore();
    store.reset('me', []);
    assert.equal(store.apply(peer({ id: 'b222' })), true, 'new peer');
    assert.equal(store.apply(peer({ id: 'b222' })), false, 'identical entry is not a change');
    assert.equal(store.apply(peer({ id: 'b222', yaw: 91 })), true, 'yaw');
    assert.equal(store.apply(peer({ id: 'b222', yaw: 91, at: [1, 2, 4] })), true, 'position');
    assert.equal(store.apply(peer({ id: 'b222', yaw: 91, at: [1, 2, 4], name: 'Zed' })), true, 'name');
    assert.equal(store.apply(peer({ id: 'b222', yaw: 91, at: [1, 2, 4], name: 'Zed', map: 2 })), true, 'map');
    assert.equal(store.apply(peer({ id: 'b222', yaw: 91, at: [1, 2, 4], name: 'Zed', map: 2, mode: 'fly' })), true, 'mode');
    assert.equal(store.peers().length, 1);
    assert.deepEqual(store.peers()[0], { id: 'b222', name: 'Zed', map: 2, at: [1, 2, 4], yaw: 91, mode: 'fly', kind: 'ui' });

    assert.equal(store.apply(peer({ id: 'me' })), false, 'own id');
    assert.equal(store.apply({ id: 'x' }), false, 'malformed');
    assert.equal(store.apply(null), false);
    assert.equal(store.peers().length, 1);
});

test('PresenceStore.remove: true only for a known peer', () => {
    const store = new PresenceStore();
    store.reset('me', [peer({ id: 'b222' })]);
    assert.equal(store.remove('nope'), false);
    assert.equal(store.remove('b222'), true);
    assert.equal(store.remove('b222'), false);
    assert.deepEqual(store.peers(), []);
});

test('PresenceStore.onMap: only the peers on that map', () => {
    const store = new PresenceStore();
    store.reset('me', [peer({ id: 'a111', map: 0 }), peer({ id: 'b222', map: 1 }), peer({ id: 'c333', map: 1 })]);
    assert.deepEqual(store.onMap(1).map((p) => p.id), ['b222', 'c333']);
    assert.deepEqual(store.onMap(0).map((p) => p.id), ['a111']);
    assert.deepEqual(store.onMap(530), []);
});

// ---- presenceMessage ----

test('presenceMessage: orbit and top publish the camera target, fly publishes the eye', () => {
    const view = defaultView();
    view.map = 1;
    view.camera = { target: [10, 20, 30], distance: 140, yaw: 45, pitch: 45, fov: 55, mode: 'orbit' };
    assert.deepEqual(presenceMessage(view), { op: 'presence', name: '', map: 1, at: [10, 20, 30], yaw: 45, mode: 'orbit' });

    view.camera = { ...view.camera, mode: 'top' };
    assert.deepEqual(presenceMessage(view).at, [10, 20, 30]);
    assert.equal(presenceMessage(view).mode, 'top');

    // In fly mode the camera's eye is its target (camera.js eye()), and the message must carry a copy of it.
    view.camera = { ...view.camera, mode: 'fly' };
    const fly = presenceMessage(view);
    assert.deepEqual(fly.at, [10, 20, 30]);
    assert.equal(fly.mode, 'fly');
    assert.notEqual(fly.at, view.camera.target);
});

test('presenceMessage: the name is always sent, and an empty one asks the host for the default', () => {
    const view = defaultView();
    assert.equal(presenceMessage(view).name, '');
    assert.equal(presenceMessage(view, '').name, '');
    assert.equal(presenceMessage(view, 5).name, '');
    assert.equal(presenceMessage(view, 'Ann').name, 'Ann');
});

test('presenceMessage: positions are clamped to the range the host accepts', () => {
    const view = defaultView();
    view.camera = { ...view.camera, target: [250000, -250000, 12.5] };
    assert.deepEqual(presenceMessage(view).at, [MAX_COORDINATE, -MAX_COORDINATE, 12.5]);
    assert.equal(MAX_COORDINATE, 100000);
});

test('cleanName: strips control and format characters, trims and caps at 32 characters', () => {
    assert.equal(cleanName('  Ann\u0001\tBo\nb  '), 'AnnBob');
    assert.equal(cleanName('a\u202Eb\u200Bc\uFEFF'), 'abc', 'bidi override, zero-width space and BOM are format characters');
    assert.equal(cleanName('n'.repeat(40)), 'n'.repeat(32));
    assert.equal(cleanName('x'.repeat(31) + '\u{1F600}'), 'x'.repeat(31), 'never ends on half of a surrogate pair');
    assert.equal(cleanName(null), '');
    assert.equal(cleanName('\u0001 \t'), '');
});

// ---- peerLabelCandidates ----

test('peerLabelCandidates: only peers on the map, text is the name', () => {
    const peers = [
        normalizePresence(peer({ id: 'a111', name: 'Ann', map: 0, at: [1, 2, 3] })),
        normalizePresence(peer({ id: 'b222', name: 'Bob', map: 1, at: [4, 5, 6] })),
    ];
    const labels = peerLabelCandidates(peers, 1);
    assert.deepEqual(labels, [{ at: [4, 5, 6], text: 'Bob', color: PEER_COLOR, priority: PEER_PRIORITY }]);
    assert.deepEqual(peerLabelCandidates(peers, 2), []);
    assert.deepEqual(peerLabelCandidates([], 0), []);
});

// ---- App wiring ----

// Node may ship a Web Storage global; the app reads the stored session name from it, so give every test an empty one.
try {
    Object.defineProperty(globalThis, 'localStorage', { value: { getItem: () => null, setItem: () => {} }, configurable: true });
} catch {
    // A runtime without the property needs no stub.
}

function makeApp(selfId = 'me0000000000') {
    const api = new Emitter();
    const socket = new Emitter();
    socket.connection = selfId;
    socket.origin = `ui:${selfId}`;
    socket.state = 'open';
    socket.sent = [];
    socket.send = (m) => {
        socket.sent.push(m);
        return true;
    };
    socket.commands = [];
    socket.respond = (name) => ({ command: name, ok: false, error: { code: 'failed', message: 'not available in this test' }, elapsedMs: 0 });
    socket.command = async (name, args) => {
        socket.commands.push({ name, args });
        return socket.respond(name, args);
    };
    socket.start = () => {};
    socket.stop = () => {};
    const app = new App({ api, socket });
    app.start();
    return { app, socket };
}

const camera = (x) => ({ target: [x, 0, 0], distance: 140, yaw: 10, pitch: 45, fov: 55, mode: 'orbit' });

test('app: a view event from another UI keeps the local camera but takes the shared state', () => {
    const { app, socket } = makeApp();
    const mine = camera(111);
    app.view = { ...app.view, camera: mine };
    socket.emit('event', 'view', { version: 1, origin: 'ui:other0000000', view: { ...app.view, map: 1, compare: 'split', camera: camera(999) } });

    assert.equal(app.view.map, 1);
    assert.equal(app.view.compare, 'split');
    assert.deepEqual(app.view.camera, mine);
});

test('app: a view event from a non-UI origin moves the camera', () => {
    for (const origin of ['cli', 'mcp', 'http', 'script']) {
        const { app, socket } = makeApp();
        app.view = { ...app.view, camera: camera(111) };
        socket.emit('event', 'view', { version: 1, origin, view: { ...app.view, map: 1, camera: camera(999) } });

        assert.equal(app.view.map, 1, origin);
        assert.equal(app.view.camera.target[0], 999, origin);
    }
});

test('app: the own echoed view event and a stale version change nothing', () => {
    const { app, socket } = makeApp();
    socket.emit('event', 'view', { version: 5, origin: 'cli', view: { ...app.view, map: 1 } });
    assert.equal(app.view.map, 1);
    socket.emit('event', 'view', { version: 6, origin: socket.origin, view: { ...app.view, map: 2 } });
    assert.equal(app.view.map, 1, 'own origin');
    socket.emit('event', 'view', { version: 5, origin: 'cli', view: { ...app.view, map: 3 } });
    assert.equal(app.view.map, 1, 'stale version');
});

test('app: ui.presence and ui.presence.left events update app.presence and announce changes', () => {
    const { app, socket } = makeApp();
    // The hello tells the store which connection is this one.
    socket.emit('hello', { connection: 'me0000000000', version: 'x', viewVersion: 1, view: defaultView(), sessions: [] });
    let announced = 0;
    app.events.on('presence', () => announced++);

    socket.emit('event', 'ui.presence', peer({ id: 'b222' }));
    assert.deepEqual(app.presence.peers().map((p) => p.id), ['b222']);
    assert.equal(announced, 1);

    socket.emit('event', 'ui.presence', peer({ id: 'b222' }));
    assert.equal(announced, 1, 'an unchanged entry announces nothing');

    socket.emit('event', 'ui.presence', peer({ id: 'b222', at: [9, 9, 9] }));
    assert.deepEqual(app.presence.peers()[0].at, [9, 9, 9]);
    assert.equal(announced, 2);

    socket.emit('event', 'ui.presence', peer({ id: 'me0000000000' }));
    socket.emit('event', 'ui.presence', { id: 'junk' });
    assert.equal(app.presence.peers().length, 1);
    assert.equal(announced, 2);

    socket.emit('event', 'ui.presence.left', { id: 'unknown' });
    socket.emit('event', 'ui.presence.left', null);
    socket.emit('event', 'ui.presence.left', { id: 5 });
    assert.equal(announced, 2);

    socket.emit('event', 'ui.presence.left', { id: 'b222' });
    assert.deepEqual(app.presence.peers(), []);
    assert.equal(announced, 3);
});

test('app: hello resets presence from its sessions list (without this connection) and publishes our own', () => {
    const { app, socket } = makeApp();
    socket.emit('event', 'ui.presence', peer({ id: 'stale0000000' }));

    socket.emit('hello', {
        connection: 'me0000000000', version: 'x', name: 'session me00', viewVersion: 1, view: defaultView(),
        sessions: [peer({ id: 'b222' }), peer({ id: 'me0000000000' })],
    });

    assert.deepEqual(app.presence.peers().map((p) => p.id), ['b222']);
    assert.equal(app.defaultName, 'session me00');
    const sent = socket.sent.filter((m) => m.op === 'presence');
    assert.equal(sent.length, 1);
    assert.equal(sent[0].map, 0);
    assert.equal(sent[0].mode, 'orbit');
});

// ---- host-assisted camera rules ----

const settle = async () => {
    for (let i = 0; i < 8; i++) {
        await new Promise((resolve) => setImmediate(resolve));
    }
};

const hostView = (app, over = {}) => ({ ...app.view, camera: camera(999), ...over });

const FAILED = (name) => ({ command: name, ok: false, error: { code: 'failed', message: 'n/a' }, elapsedMs: 0 });

/** Makes the fake host answer view.get with `view` and succeed at `succeeds`; everything else fails. */
function hostAnswersView(socket, view, succeeds = null) {
    socket.respond = (name) => {
        if (name === 'view.get') {
            return { command: name, ok: true, data: { version: 100, view }, elapsedMs: 0 };
        }
        return name === succeeds ? { command: name, ok: true, data: {}, elapsedMs: 0 } : FAILED(name);
    };
}

test('app: a peer moves, then a cli layer.set (cameraChanged false) arrives: the local camera is kept', () => {
    const { app, socket } = makeApp();
    app.view = { ...app.view, camera: camera(111) };
    socket.emit('event', 'view', { version: 1, origin: 'ui:other0000000', cameraChanged: true, view: hostView(app) });
    socket.emit('event', 'view', { version: 2, origin: 'cli', cameraChanged: false, view: hostView(app, { map: 1 }) });

    assert.equal(app.view.map, 1, 'the shared change is taken');
    assert.deepEqual(app.view.camera, camera(111), 'the peer camera the host still holds is not');
});

test('app: a cli event with cameraChanged true, or without the flag, moves the camera', () => {
    for (const flag of [{ cameraChanged: true }, {}]) {
        const { app, socket } = makeApp();
        app.view = { ...app.view, camera: camera(111) };
        socket.emit('event', 'view', { version: 1, origin: 'cli', ...flag, view: hostView(app) });
        assert.equal(app.view.camera.target[0], 999, JSON.stringify(flag));
    }
});

test('app: follow stays local for peer events and for cameraChanged false, and is taken with a camera change', () => {
    const { app, socket } = makeApp();
    socket.emit('event', 'view', { version: 1, origin: 'ui:other0000000', cameraChanged: true, view: hostView(app, { follow: '0xAA' }) });
    assert.equal(app.view.follow, null, 'a peer follow is not adopted');
    socket.emit('event', 'view', { version: 2, origin: 'cli', cameraChanged: false, view: hostView(app, { follow: '0xAA' }) });
    assert.equal(app.view.follow, null, 'nor one the host still holds from before');
    socket.emit('event', 'view', { version: 3, origin: 'mcp', cameraChanged: true, view: hostView(app, { follow: '0xAA' }) });
    assert.equal(app.view.follow, '0xAA');
});

test('app.refreshView keeps the local camera and follow by default and takes the host ones with takeCamera', async () => {
    const { app, socket } = makeApp();
    app.view = { ...app.view, camera: camera(111) };
    hostAnswersView(socket, hostView(app, { map: 3, follow: '0xBB' }));

    await app.refreshView();
    assert.equal(app.view.map, 3);
    assert.deepEqual(app.view.camera, camera(111));
    assert.equal(app.view.follow, null);

    await app.refreshView({ takeCamera: true });
    assert.equal(app.view.camera.target[0], 999);
    assert.equal(app.view.follow, '0xBB');
});

test('app.runCommand: camera.goto, camera.frame, bookmark.go and view.set with a camera take the host camera on refresh', async () => {
    const runs = [['camera.goto', { at: [1, 2, 3] }], ['camera.frame', { tile: '1,2' }], ['bookmark.go', { name: 'x' }], ['view.set', { camera: { yaw: 5 } }]];
    for (const [name, args] of runs) {
        const { app, socket } = makeApp();
        app.view = { ...app.view, camera: camera(111) };
        hostAnswersView(socket, hostView(app), name);
        await app.runCommand(name, args, { quiet: true });
        await settle();
        assert.equal(app.view.camera.target[0], 999, name);
    }
});

test('app.runCommand: view.set without a camera, and other commands, keep the local camera', async () => {
    for (const [name, args] of [['view.set', { hud: false }], ['layer.set', { id: 'terrain', visible: true }]]) {
        const { app, socket } = makeApp();
        app.view = { ...app.view, camera: camera(111) };
        hostAnswersView(socket, hostView(app, { map: 4 }), name);
        await app.runCommand(name, args, { quiet: true });
        await settle();
        assert.equal(app.view.map, 4, `${name}: the shared change is taken`);
        assert.deepEqual(app.view.camera, camera(111), name);
    }
});

test('app.runCommand: bookmark.add from the UI names the map and camera of this UI', async () => {
    const { app, socket } = makeApp();
    app.view = { ...app.view, map: 2, camera: camera(111) };
    await app.runCommand('bookmark.add', { name: 'spot' }, { quiet: true, noRefresh: true });
    const call = socket.commands.find((c) => c.name === 'bookmark.add');
    assert.equal(call.args.map, 2);
    assert.deepEqual(call.args.camera.target, [111, 0, 0]);
    assert.equal(call.args.name, 'spot');
    assert.equal(socket.commands.some((c) => c.name === 'view.set'), false, 'no view.set pre-step');

    await app.runCommand('bookmark.add', { name: 'other', map: 7, camera: { target: [1, 2, 3] } }, { quiet: true, noRefresh: true });
    const given = socket.commands.filter((c) => c.name === 'bookmark.add')[1];
    assert.equal(given.args.map, 7, 'arguments the caller gave are kept');
    assert.deepEqual(given.args.camera, { target: [1, 2, 3] });
});

test('app: a mid-gesture catch-up takes the camera only after a non-UI camera change', async () => {
    for (const [origin, cameraChanged, expected] of [['ui:other0000000', true, 111], ['cli', false, 111], ['cli', true, 999]]) {
        const { app, socket } = makeApp();
        app.view = { ...app.view, camera: camera(111) };
        hostAnswersView(socket, hostView(app, { map: 5 }));
        app.beginInteraction();
        socket.emit('event', 'view', { version: 1, origin, cameraChanged, view: hostView(app, { map: 5 }) });
        assert.equal(app.view.map, 0, 'nothing is applied during the gesture');
        await app.endInteraction();
        assert.equal(app.view.map, 5, origin);
        assert.equal(app.view.camera.target[0], expected, `${origin} ${cameraChanged}`);
    }
});

test('app: the first hello takes the whole view; a reconnect hello keeps the local camera and follow', async () => {
    const { app, socket } = makeApp();
    socket.emit('hello', { connection: 'me0000000000', version: 'x', viewVersion: 1, view: hostView(app, { follow: '0xCC' }), sessions: [] });
    await settle();
    assert.equal(app.view.camera.target[0], 999);
    assert.equal(app.view.follow, '0xCC');

    app.view = { ...app.view, camera: camera(111), follow: null };
    socket.emit('hello', { connection: 'me1111111111', version: 'x', viewVersion: 2, view: hostView(app, { map: 2, follow: '0xDD' }), sessions: [] });
    await settle();
    assert.equal(app.view.map, 2, 'the shared state is taken');
    assert.deepEqual(app.view.camera, camera(111));
    assert.equal(app.view.follow, null);
});

test('app: a reconnect while following follows again, and drops the local follow when the host refuses', async () => {
    for (const ok of [true, false]) {
        const { app, socket } = makeApp();
        socket.respond = (name) => ({ command: name, ok, data: null, error: ok ? null : { code: 'unavailable', message: 'no feed' }, elapsedMs: 0 });
        socket.emit('hello', { connection: 'me0000000000', version: 'x', viewVersion: 1, view: hostView(app, { follow: null }), sessions: [] });
        await settle();
        app.view = { ...app.view, follow: '0xEE' };
        socket.commands.length = 0;
        // The old connection's departure released the follow on the host, so the reconnect hello has none.
        socket.emit('hello', { connection: 'me1111111111', version: 'x', viewVersion: 2, view: hostView(app, { follow: null }), sessions: [] });
        await settle();
        const follows = socket.commands.filter((c) => c.name === 'live.follow');
        assert.equal(follows.length, 1, String(ok));
        assert.equal(follows[0].args.guid, '0xEE');
        assert.equal(app.view.follow, ok ? '0xEE' : null, String(ok));
        app._publishPresence.cancel();
    }
});

test('app.setSessionName: every publish goes through the throttle, and an empty name is sent as name ""', () => {
    const { app, socket } = makeApp();
    socket.emit('hello', { connection: 'me0000000000', version: 'x', viewVersion: 1, view: defaultView(), sessions: [] });
    const sentNames = () => socket.sent.filter((m) => m.op === 'presence').map((m) => m.name);
    assert.deepEqual(sentNames(), ['']);

    app.setSessionName('  Ann\u0001 ');
    app._publishPresence.flush();
    assert.deepEqual(sentNames(), ['', 'Ann']);

    app.setSessionName('');
    app._publishPresence.flush();
    assert.deepEqual(sentNames(), ['', 'Ann', '']);
    assert.equal(app.sessionName, '');
    app._publishPresence.cancel();
});

test('app: the socket leaving "open" clears the markers and announces it', () => {
    const { app, socket } = makeApp();
    socket.emit('hello', { connection: 'me0000000000', version: 'x', viewVersion: 1, view: defaultView(), sessions: [peer({ id: 'b222' })] });
    assert.equal(app.presence.peers().length, 1);
    let announced = 0;
    app.events.on('presence', () => announced++);

    socket.state = 'closed';
    socket.emit('state', 'closed');

    assert.deepEqual(app.presence.peers(), []);
    assert.equal(announced, 1);
    socket.emit('state', 'connecting');
    assert.equal(announced, 1, 'nothing to clear the second time');
    app._publishPresence.cancel();
});

test('normalizePresence: kind "agent" is kept, anything else (or no kind) is "ui"', () => {
    assert.equal(normalizePresence(peer({ kind: 'agent' })).kind, 'agent');
    assert.equal(normalizePresence(peer({ kind: 'ui' })).kind, 'ui');
    assert.equal(normalizePresence(peer()).kind, 'ui');
    assert.equal(normalizePresence(peer({ kind: 7 })).kind, 'ui');
});

test('PresenceStore: an agent session is a peer like any other, and a kind change counts as a change', () => {
    const store = new PresenceStore();
    store.reset('me', [peer({ id: 'a999', kind: 'agent', name: 'Grok Bot' })]);
    assert.deepEqual(store.peers().map((p) => [p.id, p.kind]), [['a999', 'agent']]);
    assert.equal(store.apply(peer({ id: 'a999', kind: 'agent', name: 'Grok Bot' })), false);
    assert.equal(store.apply(peer({ id: 'a999', kind: 'ui', name: 'Grok Bot' })), true);
    assert.equal(peerLabelCandidates(store.peers(), 1)[0].text, 'Grok Bot');
});

test('app: ui.presence events and hello sessions with a kind are applied', () => {
    const { app, socket } = makeApp();
    socket.emit('hello', { connection: 'me0000000000', version: 'x', viewVersion: 1, view: defaultView(), sessions: [peer({ id: 'a999', kind: 'agent' })] });
    assert.equal(app.presence.peers()[0].kind, 'agent');
    socket.emit('event', 'ui.presence', peer({ id: 'b222', kind: 'agent', at: [5, 5, 5] }));
    assert.deepEqual(app.presence.peers().map((p) => p.id), ['a999', 'b222']);
    app._publishPresence.cancel();
});

/** A per-frame patch of the live feed, as the host publishes it: origin `live`, the camera moved, the follow unchanged. */
const liveFrame = (app, version, followOrigin) => ({ version, origin: 'live', cameraChanged: true, followOrigin, view: hostView(app, { follow: '0xAA' }) });

test('app: live frames are ignored while another UI owns the follow, and taken when we, the CLI or nobody own it', () => {
    const other = 'ui:other0000000';
    {
        const { app, socket } = makeApp();
        app.view = { ...app.view, camera: camera(111) };
        socket.emit('event', 'view', { version: 1, origin: other, cameraChanged: true, followOrigin: other, view: hostView(app, { follow: '0xAA' }) });
        socket.emit('event', 'view', liveFrame(app, 2, other));
        assert.deepEqual(app.view.camera, camera(111), 'a peer owns the follow: the frame does not move our camera');
        assert.equal(app.view.follow, null, 'nor start a follow here');
    }
    for (const [who, followOrigin] of [['we own it', 'ui:me0000000000'], ['the CLI owns it', 'cli'], ['mcp owns it', 'mcp'], ['nobody is recorded', undefined], ['null is recorded', null]]) {
        const { app, socket } = makeApp();
        app.view = { ...app.view, camera: camera(111) };
        socket.emit('event', 'view', liveFrame(app, 1, followOrigin));
        assert.equal(app.view.camera.target[0], 999, who);
        assert.equal(app.view.follow, '0xAA', who);
    }
});

test('app: the follow owner is read from every view event, so ownership can pass between a peer and the CLI', () => {
    const other = 'ui:other0000000';
    const { app, socket } = makeApp();
    app.view = { ...app.view, camera: camera(111) };
    socket.emit('event', 'view', { version: 1, origin: other, cameraChanged: true, followOrigin: other, view: hostView(app, { follow: '0xAA' }) });
    socket.emit('event', 'view', liveFrame(app, 2, other));
    assert.deepEqual(app.view.camera, camera(111));
    socket.emit('event', 'view', { version: 3, origin: 'cli', cameraChanged: true, followOrigin: 'cli', view: hostView(app, { follow: '0xCC' }) });
    assert.equal(app.view.follow, '0xCC', 'the CLI takes the follow over');
    socket.emit('event', 'view', liveFrame(app, 4, 'cli'));
    assert.equal(app.view.camera.target[0], 999, 'its frames now move this UI');
});

test('app.runCommand: view.set with a follow takes the host camera and follow on refresh, and a bare layer change does not', async () => {
    const { app, socket } = makeApp();
    app.view = { ...app.view, camera: camera(111) };
    hostAnswersView(socket, hostView(app, { follow: '0xAA' }), 'view.set');
    await app.runCommand('view.set', { follow: '0xAA' }, { quiet: true });
    await settle();
    assert.equal(app.view.camera.target[0], 999);
    assert.equal(app.view.follow, '0xAA');

    const second = makeApp();
    second.app.view = { ...second.app.view, camera: camera(111) };
    hostAnswersView(second.socket, hostView(second.app, { follow: '0xAA', map: 2 }), 'view.set');
    await second.app.runCommand('view.set', { map: 2 }, { quiet: true });
    await settle();
    assert.equal(second.app.view.map, 2);
    assert.deepEqual(second.app.view.camera, camera(111));
    assert.equal(second.app.view.follow, null);
});

test('cleanName: unpaired surrogates are dropped and valid pairs kept', () => {
    assert.equal(cleanName('Ann\ud800'), 'Ann');
    assert.equal(cleanName('\udc00Ann'), 'Ann');
    assert.equal(cleanName('\ud83dx\ude00'), 'x');
    assert.equal(cleanName('A😀B'), 'A\u{1F600}B');
    assert.equal(cleanName('\ud800'), '');
});

test('app.runCommand: live.follow from the UI takes the host camera and follow on refresh, and stopping it is reflected', async () => {
    const { app, socket } = makeApp();
    app.view = { ...app.view, camera: camera(111) };
    hostAnswersView(socket, hostView(app, { follow: '0xAA' }), 'live.follow');
    await app.runCommand('live.follow', { guid: '0xAA' });
    await settle();
    assert.equal(app.view.camera.target[0], 999, 'the initial jump');
    assert.equal(app.view.follow, '0xAA');

    hostAnswersView(socket, hostView(app, { follow: null }), 'live.follow');
    await app.runCommand('live.follow', {});
    await settle();
    assert.equal(app.view.follow, null, 'stopping is reflected');
});

test('app: a first hello whose follow belongs to another UI starts at the shared camera but adopts no follow, and ignores its live frames', () => {
    const other = 'ui:other0000000';
    const { app, socket } = makeApp();
    app.view = { ...app.view, camera: camera(111) };
    socket.emit('hello', { connection: 'me0000000000', version: 'x', viewVersion: 1, followOrigin: other, view: hostView(app, { follow: '0xAA', map: 2 }), sessions: [] });
    assert.equal(app.view.map, 2, 'the shared state is taken');
    assert.equal(app.view.camera.target[0], 999, 'the first hello starts where the shared view is');
    assert.equal(app.view.follow, null);
    const start = app.view.camera;
    socket.emit('event', 'view', liveFrame(app, 2, other));
    assert.deepEqual(app.view.camera, start, 'the follower\'s frames do not move this UI');
    app._publishPresence.cancel();
});

test('app: a first hello whose follow is owned by the CLI, or by nobody, takes the whole view', () => {
    for (const followOrigin of ['cli', null, undefined]) {
        const { app, socket } = makeApp();
        app.view = { ...app.view, camera: camera(111) };
        socket.emit('hello', { connection: 'me0000000000', version: 'x', viewVersion: 1, followOrigin, view: hostView(app, { follow: '0xAA' }), sessions: [] });
        assert.equal(app.view.camera.target[0], 999, String(followOrigin));
        assert.equal(app.view.follow, '0xAA', String(followOrigin));
        app._publishPresence.cancel();
    }
});
