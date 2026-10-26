// Live insight in the UI: the replay gate, the perf HUD's rows (with and without the renderer's texture numbers), the inspector rows, watches
// and, in a real browser against the mock host, the Insight tab and the HUD. The pure parts run under node alone; the browser part runs only
// when ARCANECORE_TEST_BROWSER names a Chrome or Edge executable. Synthetic data only.

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { App } from '../../../tools/ArcaneCore.Atlas/Web/js/app.js';
import { flagText, inspectSections, remainingText } from '../../../tools/ArcaneCore.Atlas/Web/js/insight/inspect.js';
import {
    FrameTimes, LiveRate, RENDERER_ROWS, REPORT_KEYS, hudRows, percentile, reportPayload, serverNumbers,
} from '../../../tools/ArcaneCore.Atlas/Web/js/insight/perfhud.js';
import {
    REPLAY_BADGE, acceptsLiveFrame, acceptsReplayFrame, createReplayState, noteReplayFrame, reduceReplayState,
} from '../../../tools/ArcaneCore.Atlas/Web/js/insight/replay.js';
import { WATCHABLE, WatchController, watchRate, watchSpec } from '../../../tools/ArcaneCore.Atlas/Web/js/insight/watch.js';
import { Emitter } from '../../../tools/ArcaneCore.Atlas/Web/js/util.js';
import { Browser, browserFromEnvironment } from './support/browser.mjs';
import { MOCK_COMMANDS } from './support/mockhost.mjs';
import { startScenario } from './support/scenario.mjs';

// ---- the replay gate ----

function appWithSocket() {
    const api = new Emitter();
    const socket = new Emitter();
    socket.connection = null;
    socket.origin = null;
    socket.start = () => {};
    socket.stop = () => {};
    const app = new App({ api, socket });
    app.start();
    return { app, socket };
}

const liveFrame = (seq, x) => ({
    op: 'frame', seq, clockMs: seq * 250, map: 0, instance: 0, grids: [],
    entities: [{ g: 'A1', k: 'creature', n: 'Boar', x, y: 2, z: 3, hp: 100 }],
});

test('replay state follows the host and notes what a replay does not show', () => {
    let state = createReplayState();
    assert.equal(acceptsLiveFrame(state), true);
    assert.equal(acceptsReplayFrame(state), false);

    state = reduceReplayState(state, { replaying: true, playing: true, seq: 7 });
    assert.deepEqual([state.active, state.playing, state.seq], [true, true, 7]);
    assert.equal(acceptsLiveFrame(state), false);
    assert.equal(acceptsReplayFrame(state), true);

    state = reduceReplayState(state, { replaying: true, playing: false, seq: 8 });
    assert.deepEqual([state.active, state.playing, state.seq], [true, false, 8]);

    state = noteReplayFrame(state, { seq: 9, entities: [], replay: { fields: ['guid', 'x', 3, 'd'], note: 'replay: positions only. Not replayed: splines.' } });
    assert.equal(state.seq, 9);
    assert.equal(state.frames, 1);
    assert.deepEqual(state.fields, ['guid', 'x', 'd']);
    assert.match(state.note, /positions only/);
    assert.match(REPLAY_BADGE, /positions only/);

    state = reduceReplayState(state, { replaying: false, playing: true, seq: 9 });
    assert.deepEqual([state.active, state.playing], [false, false], 'a stopped replay is never playing');
    assert.equal(acceptsLiveFrame(state), true);
});

test('replay state ignores malformed payloads', () => {
    const state = reduceReplayState(createReplayState(), { replaying: true, seq: 3 });
    for (const bad of [null, undefined, 5, 'x', {}, { replaying: 'yes' }]) {
        assert.equal(reduceReplayState(state, bad), state);
    }
    assert.equal(noteReplayFrame(state, null), state);
    assert.equal(noteReplayFrame(state, { seq: 1 }), state);
});

test('app: live frames are ignored while a replay is on, replayed frames are applied, and stopping returns to live', () => {
    const { app, socket } = appWithSocket();
    const positionOfBoar = () => app.live.latest?.entities.get('A1')?.pos[0];

    socket.emit('event', 'live.frame', liveFrame(1, 10));
    assert.equal(positionOfBoar(), 10);
    assert.equal(app.live.framesSeen, 1);

    // A replayed frame that arrives with no replay on (a late frame after stop) is dropped.
    socket.emit('event', 'insight.replay.frame', liveFrame(5, 999));
    assert.equal(positionOfBoar(), 10);
    assert.equal(app.live.framesSeen, 1);

    socket.emit('event', 'insight.replay.state', { replaying: true, playing: false, seq: 5 });
    socket.emit('event', 'insight.replay.frame', { ...liveFrame(5, 50), replay: { fields: ['guid'], note: 'replay: positions only' } });
    assert.equal(positionOfBoar(), 50);
    assert.equal(app.replay.active, true);
    assert.equal(app.replay.frames, 1);

    socket.emit('event', 'live.frame', liveFrame(2, 20));
    socket.emit('event', 'live.frame', liveFrame(3, 30));
    assert.equal(positionOfBoar(), 50, 'live frames must not move entities during a replay');
    assert.equal(app.live.framesSeen, 2, 'ignored live frames are not counted as applied');

    socket.emit('event', 'insight.replay.state', { replaying: false, playing: false, seq: 5 });
    socket.emit('event', 'live.frame', liveFrame(4, 40));
    assert.equal(positionOfBoar(), 40);
    assert.equal(app.replay.active, false);
});

test('app: the replay state is announced to panels', () => {
    const { app, socket } = appWithSocket();
    const seen = [];
    app.events.on('replay', (state) => seen.push([state.active, state.seq]));

    socket.emit('event', 'insight.replay.state', { replaying: true, playing: true, seq: 1 });
    socket.emit('event', 'insight.replay.frame', liveFrame(2, 5));
    socket.emit('event', 'insight.replay.state', { replaying: false, playing: false, seq: 2 });

    assert.deepEqual(seen, [[true, 1], [true, 2], [false, 2]]);
});

// ---- the perf HUD ----

const labels = (rows) => rows.map(([key]) => key);

test('hud rows show only the numbers that exist: no texture bytes, no texture row', () => {
    const stats = { drawCalls: 120, triangles: 456_000, lines: 10, chunks: 4, gpuBytes: 300 * 1024 * 1024 };

    const rows = hudRows({ frame: { fps: 59.5, p50: 16.2, p95: 21 }, stats, live: { hz: 4, lagMs: 120, entities: 1000, online: 3 } });

    assert.deepEqual(labels(rows), ['fps', 'frameMsP50', 'frameMsP95', 'drawCalls', 'triangles', 'gpuBytes', 'liveHz', 'feedLagMs', 'entities', 'online']);
    assert.ok(!labels(rows).includes('textureBytes'));
    assert.ok(!labels(rows).includes('textures'));
    const byKey = Object.fromEntries(rows.map(([key, , text]) => [key, text]));
    assert.equal(byKey.fps, '59.5');
    assert.equal(byKey.frameMsP50, '16.2 ms');
    assert.equal(byKey.feedLagMs, '120 ms');
    assert.match(byKey.gpuBytes, /300/);
});

test('hud rows add every pinned renderer stat the renderer reports', () => {
    const stats = {
        drawCalls: 1, triangles: 2, gpuBytes: 1024, textureBytes: 5 * 1024 * 1024, textures: 3, texturesPending: 1, texturesMissing: 0,
        instances: 900, modelGroups: 12, modelFallbacks: 2, composites: 4, compositeFallbacks: 0,
    };

    const rows = hudRows({ stats });

    assert.deepEqual(labels(rows), RENDERER_ROWS.map(([name]) => name));
    const byKey = Object.fromEntries(rows.map(([key, , text]) => [key, text]));
    assert.match(byKey.textureBytes, /5/);
    assert.equal(byKey.texturesMissing, '0', 'a zero the renderer reports is shown; only absent numbers are hidden');
});

test('hud rows ignore values that are not finite numbers', () => {
    const rows = hudRows({ frame: { fps: Number.NaN, p50: '16', p95: null }, stats: { drawCalls: Infinity, triangles: undefined, textureBytes: '5' }, live: { hz: -Infinity } });

    assert.deepEqual(rows, []);
    assert.deepEqual(hudRows(), []);
});

test('the perf report carries only existing numbers, in the units the host expects', () => {
    const payload = reportPayload({
        frame: { fps: 60, p50: 16, p95: 20 },
        stats: { drawCalls: 5, triangles: 6, gpuBytes: 2 * 1024 * 1024 },
        live: { hz: 4, lagMs: 100, entities: 7, online: 1 },
        server: { dropped: 0, truncated: 2 },
    });

    assert.deepEqual(payload, {
        fps: 60, frameMsP50: 16, frameMsP95: 20, drawCalls: 5, triangles: 6, gpuMb: 2, liveHz: 4, feedLagMs: 100, entities: 7, online: 1, dropped: 0, truncated: 2,
    });
    assert.ok(Object.keys(payload).every((k) => REPORT_KEYS.includes(k)));
    assert.deepEqual(reportPayload({ stats: { textureBytes: 3 * 1024 * 1024 } }), { textureMb: 3 });
    assert.deepEqual(reportPayload({ frame: { fps: -1 } }), {}, 'negative numbers are refused by the host, so never sent');
    assert.deepEqual(reportPayload(), {});
});

test('percentiles use the nearest rank, and the frame ring is bounded', () => {
    assert.equal(percentile([], 0.5), null);
    assert.equal(percentile([5], 0.95), 5);
    assert.equal(percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.5), 5);
    assert.equal(percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.95), 10);
    assert.equal(percentile([10, 1, 5, Number.NaN, 'x'], 0.5), 5);

    const frames = new FrameTimes(4);
    assert.equal(frames.fps(), null);
    [10, 10, 10, 10, 40, 40].forEach((ms) => frames.push(ms));
    frames.push(-5);
    frames.push(Number.NaN);
    assert.equal(frames.count, 4);
    assert.equal(frames.p50(), 10);
    assert.equal(frames.p95(), 40);
    assert.equal(frames.fps(), 1000 / 25);
});

test('the live rate is measured over the last five seconds', () => {
    const rate = new LiveRate();
    assert.equal(rate.hz(), null);
    assert.equal(rate.lagMs(1000), null);
    rate.note(0);
    assert.equal(rate.hz(), null, 'one frame has no rate');
    [250, 500, 750, 1000].forEach((t) => rate.note(t));
    assert.equal(rate.hz(), 4);
    assert.equal(rate.lagMs(1300), 300);
    rate.note(20_000);
    assert.equal(rate.hz(), null, 'older frames are forgotten');
});

test('server numbers come from the host counters and stay absent when the host sends none', () => {
    assert.deepEqual(serverNumbers({ droppedForCapacity: 3, truncatedLines: 2, oversizeLines: 1, diffMs: 4.5 }), { dropped: 3, truncated: 3, diffMs: 4.5 });
    assert.deepEqual(serverNumbers({}), { dropped: undefined, truncated: undefined, diffMs: undefined });
    assert.deepEqual(serverNumbers(null), { dropped: undefined, truncated: undefined, diffMs: undefined });
    assert.deepEqual(hudRows({ server: serverNumbers({}) }), []);
});

// ---- the inspector ----

const BOAR = {
    kind: 'inspect', guid: '1234ABCD', entity: 'creature', entry: 299, name: 'Diseased Wolf', map: 0, instance: 0, p: [-9462.66, 16.2, 57],
    unit: true, level: 7, hp: 120, maxHp: 150, power: { type: 'Mana', value: 40, max: 80 }, faction: 38, flags: 0x8000, stand: 'Stand', combat: true,
    target: 'ABC', victim: null,
    threat: [{ g: '1', name: 'Testplayer', t: 120.5 }, { g: '2', name: null, t: 3 }],
    aurasAvailable: true,
    auras: [
        { spell: 133, name: 'Fireball', stacks: 1, remainingMs: 3200, durationMs: 4000, caster: 'ABC', positive: false },
        { spell: 99999, name: null, stacks: 3, remainingMs: -1, durationMs: -1, caster: null, positive: true },
    ],
    motion: { current: 'Chase', default: 'Random', stack: ['Chase', 'Random'] },
    ai: 'SmartAI', aiName: 'wolf_ai', bot: { routePoints: 12, next: 3, flags: ['stuck'], goal: [1, 2, 3] },
};

const section = (sections, title) => sections.find((s) => s.title === title);
const row = (s, key) => s.rows.find(([k]) => k === key)?.[1];

test('inspector rows: identity, state, threat, auras with names, behaviour and bot route', () => {
    const sections = inspectSections(BOAR);

    assert.deepEqual(sections.map((s) => s.title), ['Identity', 'State', 'Threat', 'Auras', 'Behaviour']);
    const identity = section(sections, 'Identity');
    assert.equal(row(identity, 'Name'), 'Diseased Wolf');
    assert.equal(row(identity, 'Guid'), '1234ABCD');
    assert.equal(row(identity, 'Entry'), '299');
    assert.equal(row(identity, 'Position'), '-9462.7, 16.2, 57.0');
    const state = section(sections, 'State');
    assert.equal(row(state, 'Level'), '7');
    assert.equal(row(state, 'Health'), '120 / 150');
    assert.equal(row(state, 'Mana'), '40 / 80');
    assert.equal(row(state, 'Unit flags'), '0x8000');
    assert.equal(row(state, 'In combat'), 'yes');
    assert.equal(row(state, 'Target'), 'ABC');
    assert.equal(row(state, 'Victim'), undefined, 'a null victim has no row');
    const threat = section(sections, 'Threat');
    assert.deepEqual(threat.rows, [['Testplayer', '120.5'], ['2', '3.0']]);
    const auras = section(sections, 'Auras');
    assert.equal(auras.rows[0][0], 'Fireball');
    assert.match(auras.rows[0][1], /#133 · 3\.2 s · by ABC/);
    assert.equal(auras.rows[1][0], 'spell 99999', 'an aura without a name shows its id');
    assert.match(auras.rows[1][1], /x3 · permanent/);
    const behaviour = section(sections, 'Behaviour');
    assert.equal(row(behaviour, 'AI'), 'SmartAI (wolf_ai)');
    assert.equal(row(behaviour, 'Motion stack'), 'Chase > Random');
    assert.equal(row(behaviour, 'Bot route'), '12 points, next 3');
    assert.equal(row(behaviour, 'Route flags'), 'stuck');
    assert.equal(row(behaviour, 'Goal'), '1.0, 2.0, 3.0');
});

test('inspector rows for a game object are identity only, and absent sections are not invented', () => {
    const sections = inspectSections({ kind: 'inspect', guid: 'F1', entity: 'gameobject', entry: 5, name: 'Chest', p: [1, 2, 3], unit: false });
    assert.deepEqual(sections.map((s) => s.title), ['Identity']);

    const bare = inspectSections({ guid: 'F2', unit: true, aurasAvailable: false, auras: [], threat: [], motion: null, ai: null, bot: null });
    assert.deepEqual(bare.map((s) => s.title), ['Identity', 'State', 'Auras']);
    assert.match(row(section(bare, 'Auras'), ''), /no aura service/);
    assert.deepEqual(inspectSections(null), []);
    assert.deepEqual(inspectSections('x'), []);
});

test('aura times and unit flags read plainly', () => {
    assert.equal(remainingText(-1), 'permanent');
    assert.equal(remainingText(0), '0.0 s');
    assert.equal(remainingText(59_900), '59.9 s');
    assert.equal(remainingText(120_000), '2.0 min');
    assert.equal(remainingText(undefined), 'unknown');
    assert.equal(flagText(0), 'none');
    assert.equal(flagText(0x40), '0x40');
    assert.equal(flagText(-1), 'unknown');
    assert.equal(flagText('x'), 'unknown');
});

// ---- watches ----

test('a watch spec is the command name, plus its arguments as JSON when it has any', () => {
    assert.equal(watchSpec('live.highlight', { kind: 'bot', hpBelow: 30 }), 'live.highlight {"kind":"bot","hpBelow":30}');
    assert.equal(watchSpec('live.anomalies', {}), 'live.anomalies');
    assert.equal(watchSpec('live.heatmap', { metric: 'density', cellSize: undefined, map: null, name: '' }), 'live.heatmap {"metric":"density"}');
    assert.throws(() => watchSpec('live.status', {}), /cannot be watched/);
    assert.deepEqual(WATCHABLE, ['live.anomalies', 'live.heatmap', 'live.highlight']);
    assert.equal(watchRate(5), 2);
    assert.equal(watchRate(0.5), 0.5);
    assert.equal(watchRate(0), 1);
    assert.equal(watchRate(Number.NaN), 1);
});

test('the watch controller starts one watch, replaces it on the next start and stops it by id', async () => {
    const calls = [];
    let n = 0;
    const app = {
        runCommand: async (name, args) => {
            calls.push([name, args]);
            return args.action === 'start' ? { ok: true, data: { id: `w${++n}` } } : { ok: true, data: { stopped: 1 } };
        },
    };
    const watch = new WatchController(app);

    assert.equal(await watch.start('live.highlight', { kind: 'bot' }, 1), true);
    assert.equal(watch.active, true);
    assert.equal(await watch.start('live.highlight', { kind: 'creature' }, 9), true);
    assert.equal(await watch.stop(), true);
    assert.equal(watch.active, false);
    assert.equal(await watch.stop(), true, 'stopping an idle controller does nothing');

    assert.deepEqual(calls, [
        ['insight.watch', { action: 'start', commands: ['live.highlight {"kind":"bot"}'], hz: 1 }],
        ['insight.watch', { action: 'stop', id: 'w1' }],
        ['insight.watch', { action: 'start', commands: ['live.highlight {"kind":"creature"}'], hz: 2 }],
        ['insight.watch', { action: 'stop', id: 'w2' }],
    ]);
});

test('a refused watch leaves the controller idle', async () => {
    const watch = new WatchController({ runCommand: async () => ({ ok: false, error: { message: 'no' } }) });

    assert.equal(await watch.start('live.highlight', { kind: 'bot' }), false);
    assert.equal(watch.active, false);
});

// ---- the Insight tab and the HUD in a real browser ----

const executable = browserFromEnvironment();
const skip = executable ? false : 'ARCANECORE_TEST_BROWSER does not name a Chrome or Edge executable';

const schema = (properties, required = []) => ({ type: 'object', properties, ...(required.length ? { required } : {}), additionalProperties: false });
const INSIGHT_COMMANDS = [
    { name: 'live.inspect', summary: 'Inspect an entity.', surfaces: ['Palette'], schema: schema({ guid: { type: 'string' } }, ['guid']) },
    { name: 'live.anomalies', summary: 'List anomalies.', surfaces: ['Palette'], schema: schema({ limit: { type: 'integer' } }) },
    { name: 'live.heatmap', summary: 'Heatmap.', surfaces: ['Palette'], schema: schema({ metric: { type: 'string' } }, ['metric']) },
    { name: 'live.highlight', summary: 'Highlight entities.', surfaces: ['Palette'], schema: schema({ kind: { type: 'string' } }) },
    { name: 'live.record', summary: 'Record.', surfaces: ['Palette'], schema: schema({ on: { type: 'boolean' } }) },
    { name: 'live.replay', summary: 'Replay.', surfaces: ['Palette'], schema: schema({ at: { type: 'number' } }) },
    { name: 'insight.watch', summary: 'Watch.', surfaces: ['Palette'], schema: schema({ action: { type: 'string' } }, ['action']) },
    { name: 'perf.report', summary: 'Perf report.', surfaces: ['Palette'], schema: schema({ stats: { type: 'object' } }, ['stats']) },
    { name: 'perf.stats', summary: 'Perf stats.', surfaces: ['Palette'], schema: schema({}) },
];

test('the Insight tab and the perf HUD in a real browser', { skip }, async (t) => {
    MOCK_COMMANDS.push(...INSIGHT_COMMANDS);
    const browser = await Browser.launch(executable);
    t.after(() => browser.close());
    const s = await startScenario(executable, { browser });
    t.after(() => s.close());
    const { page, host } = s;
    const ev = (expression) => page.evaluate(expression);
    host.handle('live.inspect', () => BOAR);
    host.handle('perf.report', () => ({ accepted: 3 }));
    host.handle('perf.stats', () => ({ process: {}, live: { stats: { droppedForCapacity: 2, truncatedLines: 1, oversizeLines: 0 } }, recorder: {}, ui: null }));
    await page.waitFor("window.__atlas.app.hasCommand('live.inspect')", 'the insight commands');

    await t.test('the inspector shows the rows of a mock result', async () => {
        await ev("document.getElementById('tab-insight').click()");
        await page.waitFor("document.querySelector('#panel-insight') && !document.querySelector('#panel-insight').hidden", 'the Insight tab');
        await ev("(() => { const i = document.querySelector('#panel-insight input[aria-label=\"Entity guid\"]'); i.value = '1234ABCD'; })()");
        await ev("[...document.querySelectorAll('#panel-insight button')].find((b) => b.textContent === 'Inspect').click()");
        await page.waitFor("document.querySelector('#panel-insight .inspect-result')", 'the inspector result');
        const text = await ev("document.querySelector('#panel-insight .inspect-result').textContent");
        for (const expected of ['Diseased Wolf', '120 / 150', 'Fireball', 'spell 99999', 'permanent', 'Testplayer', 'SmartAI (wolf_ai)', 'Route flags']) {
            assert.ok(text.includes(expected), `the inspector is missing '${expected}': ${text}`);
        }
        const call = host.commands.find((c) => c.name === 'live.inspect');
        assert.deepEqual(call.args, { guid: '1234ABCD' });
    });

    await t.test('the HUD hides rows the renderer does not report and shows them when it does', async () => {
        await page.press('p');
        await page.waitFor("!document.querySelector('.perfhud').hidden", 'the HUD to open');
        await page.waitFor("document.querySelectorAll('.perfhud dt').length > 0", 'HUD rows');
        const stats = () => ev("[...document.querySelectorAll('.perfhud dd')].map((d) => d.dataset.stat).join(',')");
        assert.ok(!(await stats()).includes('textureBytes'), await stats());
        await ev('window.__atlas.viewport.renderer.stats.textureBytes = 5 * 1048576; window.__atlas.hud.refresh()');
        assert.ok((await stats()).includes('textureBytes'), await stats());
        await ev('delete window.__atlas.viewport.renderer.stats.textureBytes; window.__atlas.hud.refresh()');
        assert.ok(!(await stats()).includes('textureBytes'), await stats());
    });

    await t.test('the HUD posts its numbers with perf.report every two seconds', async () => {
        await page.waitFor("window.__atlas.hud.visible", 'the HUD');
        const deadline = Date.now() + 15_000;
        while (!host.commands.some((c) => c.name === 'perf.report') && Date.now() < deadline) {
            await new Promise((resolve) => setTimeout(resolve, 200));
        }
        const report = host.commands.find((c) => c.name === 'perf.report');
        assert.ok(report, 'no perf.report arrived');
        assert.ok(Object.keys(report.args.stats).length > 0);
        assert.ok(Object.keys(report.args.stats).every((k) => REPORT_KEYS.includes(k)), JSON.stringify(report.args));
        assert.ok(!('textureMb' in report.args.stats), 'the renderer has no texture bytes, so none is reported');
        await page.press('p');
        await page.waitFor('document.querySelector(".perfhud").hidden', 'the HUD to close');
    });

    await t.test('replay gating: live frames are ignored while a replay is on', async () => {
        const entity = (x) => ({ g: 'a1', k: 'creature', n: 'Boar', x, y: 16, z: 57, hp: 100 });
        const frame = (seq, x, extra = {}) => ({ seq, clockMs: seq * 250, map: 0, instance: 0, grids: [], counters: {}, entities: [entity(x)], ...extra });
        const x = () => ev("window.__atlas.app.live.latest?.entities.get('A1')?.pos[0] ?? null");
        host.broadcast('live.frame', frame(1, -9460));
        await page.waitFor('window.__atlas.app.live.latest?.entities.get("A1")', 'the live entity');
        host.broadcast('insight.replay.state', { replaying: true, playing: false, seq: 3 });
        host.broadcast('insight.replay.frame', frame(3, -9400, { replay: { fields: ['guid', 'x'], note: 'replay: positions only' } }));
        await page.waitFor('window.__atlas.app.live.latest.entities.get("A1").pos[0] === -9400', 'the replayed position');
        await page.waitFor("!document.querySelector('.replay-badge').hidden", 'the replay badge');
        assert.match(await ev("document.querySelector('.replay-badge').textContent"), /positions only/);
        host.broadcast('live.frame', frame(2, -9300));
        await new Promise((resolve) => setTimeout(resolve, 400));
        assert.equal(await x(), -9400);
        host.broadcast('insight.replay.state', { replaying: false, playing: false, seq: 3 });
        host.broadcast('live.frame', frame(4, -9200));
        await page.waitFor('window.__atlas.app.live.latest.entities.get("A1").pos[0] === -9200', 'live to resume');
        assert.ok(await ev("document.querySelector('.replay-badge').hidden"));
    });

    await t.test('screenshot of the tab and the HUD, kept only when ARCANECORE_TEST_RENDER_OUT_DIR names a folder', async () => {
        const out = process.env.ARCANECORE_TEST_RENDER_OUT_DIR;
        if (!out) {
            return;
        }
        await ev("document.getElementById('tab-insight').click()");
        await ev("[...document.querySelectorAll('#panel-insight button')].find((b) => b.textContent === 'Inspect').click()");
        await page.waitFor("document.querySelector('#panel-insight .inspect-result')", 'the inspector result');
        await page.press('p');
        await page.waitFor("document.querySelectorAll('.perfhud dt').length > 0", 'HUD rows');
        fs.mkdirSync(out, { recursive: true });
        const png = await page.screenshot();
        fs.writeFileSync(path.join(out, 'atlas-a6-insight-tab.png'), png);
        assert.ok(png.length > 1000);
    });
});
