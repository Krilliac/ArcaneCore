// The dev UI: its pure parts under node (selection, gestures, the key map against hotkeys.js, the catalog-driven menu, the palette model, the
// log, the debug overlay refresh) and its behaviour in a real browser against a mock host (right click versus right drag, the menu, spawn,
// box select with bulk confirmation, undo, the palette). The catalog the menus are built from is support/devui-catalog.json, which a C# test
// holds equal to what the action.catalog command returns. The browser half runs only when ARCANECORE_TEST_BROWSER names Chrome or Edge.

import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { SHORTCUTS } from '../../../tools/ArcaneCore.Atlas/Web/js/hotkeys.js';
import { ActionLogModel } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/devui-log.js';
import { confirmDialog, targetLines } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/devui-confirm.js';
import { DEVUI_KEYS, comboOf, hotkeyCombos, matchKey } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/devui-keys.js';
import { buildMenu, groupItems } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/devui-menu.js';
import { DebugOverlays, MIN_INTERVAL_MS } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/devui-overlays.js';
import { filterEntries, paletteEntries } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/devui-palette.js';
import { CLICK_MAX_MOVE, CLICK_MAX_MS, DevSelection, MAX_SELECTION, entitiesInRect, isContextClick, rectOf } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/devui.js';
import { Browser, browserFromEnvironment } from './support/browser.mjs';
import { MOCK_COMMANDS } from './support/mockhost.mjs';
import { INN, startScenario } from './support/scenario.mjs';

const FIXTURE = JSON.parse(fs.readFileSync(fileURLToPath(new URL('./support/devui-catalog.json', import.meta.url)), 'utf8'));
const ITEMS = FIXTURE.items;
const GATED = ITEMS.filter((i) => i.gated).map((i) => i.id);

const CREATURE = { guid: 'F130000000000001', kind: 'creature', name: 'Wolf', pos: [1, 2, 3] };
const kinds = { F130000000000001: 'creature', F130000000000002: 'creature', 1000000000000002: 'bot', 1000000000000003: 'bot', 1000000000000009: 'player' };
const kindOf = (g) => kinds[g] ?? null;
const hasCommand = (name) => ['live.query', 'live.follow', 'live.inspect', 'camera.goto', 'bookmark.add'].includes(name);
const menuOf = (target, selection = [], catalog = { items: ITEMS }, has = hasCommand) => buildMenu({ catalog, target, selection, kindOf, hasCommand: has });
const ids = (menu) => menu.map((i) => i.key);

// ---- pure parts -------------------------------------------------------------------------------------------------------------

test('the selection model sets, adds, removes, toggles and clears, announces only real changes, and holds at most 50', () => {
    const s = new DevSelection();
    const seen = [];
    s.on('change', (list) => seen.push(list));
    assert.equal(s.set(['A', 'B', 'A']), true);
    assert.deepEqual(s.guids, ['A', 'B']);
    assert.equal(s.set(['A', 'B']), false, 'the same list is not a change');
    assert.equal(s.add(['C', 'B']), true);
    assert.equal(s.remove(['A']), true);
    assert.equal(s.toggle('B'), true);
    assert.equal(s.toggle('D'), true);
    assert.deepEqual(s.guids, ['C', 'D']);
    assert.equal(s.clear(), true);
    assert.equal(s.clear(), false);
    assert.equal(seen.length, 6);
    s.set(Array.from({ length: 80 }, (_, i) => `G${i}`));
    assert.equal(s.guids.length, MAX_SELECTION);
    s.guids.push('X');
    assert.equal(s.guids.length, MAX_SELECTION, 'the list handed out is a copy');
});

test('a right press is a click only when it barely moved and was quick: a 10 px drag pans instead', () => {
    const down = { x: 100, y: 100, t: 0 };
    assert.equal(isContextClick(down, { x: 100, y: 100, t: 120 }), true);
    assert.equal(isContextClick(down, { x: 102, y: 101, t: 299 }), true);
    assert.equal(isContextClick(down, { x: 110, y: 100, t: 120 }), false, 'a 10 px drag is a pan');
    assert.equal(isContextClick(down, { x: 100 + CLICK_MAX_MOVE, y: 100, t: 50 }), false, 'the pan starts at the same distance, so both never happen');
    assert.equal(isContextClick(down, { x: 100, y: 100, t: CLICK_MAX_MS + 1 }), false, 'a long press is not a click');
});

test('a box selects the entities whose projection falls inside it', () => {
    const entities = [{ guid: 'A', pos: [0, 0, 0] }, { guid: 'B', pos: [10, 0, 0] }, { guid: 'C', pos: [20, 0, 0] }, { guid: 'D', pos: [99, 0, 0] }];
    const project = (p) => (p[0] === 99 ? null : { x: p[0] * 10, y: 50 });
    assert.deepEqual(rectOf({ x: 120, y: 80 }, { x: 40, y: 10 }), { x0: 40, y0: 10, x1: 120, y1: 80 });
    assert.deepEqual(entitiesInRect(entities, project, rectOf({ x: 90, y: 0 }, { x: 210, y: 100 })).map((e) => e.guid), ['B', 'C']);
    assert.deepEqual(entitiesInRect(entities, project, rectOf({ x: 0, y: 0 }, { x: 1000, y: 100 })).map((e) => e.guid), ['A', 'B', 'C'], 'a point behind the camera is not selected');
});

test('the dev UI key map collides with neither hotkeys.js nor the movement keys', () => {
    const taken = new Set(SHORTCUTS.flatMap(([label]) => hotkeyCombos(label)));
    for (const key of ['W', 'A', 'S', 'D', 'Q', 'E']) {
        assert.ok(taken.has(key), `hotkeys.js lost ${key}; the test's idea of the movement keys is stale`);
    }
    for (const entry of DEVUI_KEYS) {
        if (entry.cooperative) {
            assert.equal(entry.combo, 'Escape', 'only Escape is shared on purpose');
            continue;
        }
        assert.ok(!taken.has(entry.combo), `${entry.combo} (${entry.id}) is already a hotkeys.js or movement key`);
    }
    const combos = DEVUI_KEYS.map((k) => k.combo);
    assert.equal(new Set(combos).size, combos.length, 'a combination is listed twice');
    const hotkeysSource = fs.readFileSync(fileURLToPath(new URL('../../../tools/ArcaneCore.Atlas/Web/js/hotkeys.js', import.meta.url)), 'utf8');
    assert.match(hotkeysSource, /e\.ctrlKey \|\| e\.metaKey \|\| e\.altKey/, 'hotkeys.js must keep ignoring Alt-modified keys, which is what frees the Alt combinations');
    const navSource = fs.readFileSync(fileURLToPath(new URL('../../../tools/ArcaneCore.Atlas/Web/js/nav.js', import.meta.url)), 'utf8');
    assert.match(navSource, /e\.ctrlKey \|\| e\.metaKey \|\| e\.altKey/, 'nav.js must keep ignoring Alt-modified keys');
    assert.deepEqual(DEVUI_KEYS.filter((k) => /^Alt\+/.test(k.combo)).map((k) => k.combo).sort(), ['Alt+F', 'Alt+H', 'Alt+K', 'Alt+L', 'Alt+T']);
});

test('key events map to their combination', () => {
    const ev = (key, mods = {}) => ({ key, ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, ...mods });
    assert.equal(comboOf(ev('P', { ctrlKey: true, shiftKey: true })), 'Ctrl+Shift+P');
    assert.equal(comboOf(ev('k', { altKey: true })), 'Alt+K');
    assert.equal(comboOf(ev('Delete')), 'Delete');
    assert.equal(comboOf(ev('F10', { shiftKey: true })), 'Shift+F10');
    assert.equal(comboOf(ev('Alt', { altKey: true })), 'Alt', 'a bare modifier press matches nothing');
    assert.equal(matchKey(ev('p', { ctrlKey: true, shiftKey: true })).id, 'palette');
    assert.equal(matchKey(ev('z', { ctrlKey: true })).id, 'undo');
    assert.equal(matchKey(ev('k')), null, 'plain K belongs to hotkeys.js');
    assert.deepEqual(hotkeyCombos('W A S D'), ['W', 'A', 'S', 'D']);
    assert.deepEqual(hotkeyCombos('Ctrl K'), ['Ctrl+K']);
    assert.deepEqual(hotkeyCombos('1 to 8'), ['1', '2', '3', '4', '5', '6', '7', '8']);
});

test('the point menu is the catalog: place, teleport, spawns, and the view helpers when the host has them', () => {
    const menu = menuOf({ type: 'point', point: [10, 20, 30] });
    const keys = ids(menu);
    for (const wanted of ['action:tele.me', 'action:place.creature', 'action:place.gobject', 'action:spawns', 'action:tele.selected', 'action:moveto.selected', 'client:height', 'client:copy', 'client:bookmark']) {
        assert.ok(keys.includes(wanted), `${wanted} missing from ${keys.join(', ')}`);
    }
    const place = menu.find((i) => i.key === 'action:place.creature');
    assert.deepEqual(place.run.point, [10, 20, 30]);
    assert.equal(place.run.argSpecs[0].name, 'entry');
    assert.equal(menu.find((i) => i.key === 'action:tele.selected').disabled, true, 'nothing selected yet');
    assert.equal(menu.find((i) => i.key === 'client:los').disabled, true);
    const bare = ids(menuOf({ type: 'point', point: [0, 0, 0] }, [], { items: ITEMS }, () => false));
    assert.ok(!bare.includes('client:height') && !bare.includes('client:bookmark'), 'view helpers need their Atlas commands');
    assert.ok(bare.includes('client:copy'));
});

test('selected bots become the targets of the selection items at a point', () => {
    const menu = menuOf({ type: 'point', point: [1, 2, 3] }, ['1000000000000002', '1000000000000003', 'F130000000000001']);
    const tele = menu.find((i) => i.key === 'action:tele.selected');
    assert.equal(tele.disabled, false);
    assert.deepEqual(tele.run.targets, ['1000000000000002', '1000000000000003']);
    assert.equal(tele.run.bulk, true);
    const move = menu.find((i) => i.key === 'action:moveto.selected');
    assert.deepEqual(move.run.targets, ['1000000000000002', '1000000000000003', 'F130000000000001']);
    assert.equal(menu.find((i) => i.key === 'client:los').run.from, '1000000000000002');
});

test('the entity menu shows what applies to the kind, and acts on the whole selection when the entity is part of it', () => {
    const alone = menuOf({ type: 'entity', entity: CREATURE, point: CREATURE.pos });
    const aloneKeys = ids(alone);
    for (const wanted of ['action:kill', 'action:heal', 'action:respawn', 'action:despawn', 'action:setlevel', 'action:debug', 'action:moveto.pick', 'client:follow', 'client:inspect', 'client:copyguid', 'client:select']) {
        assert.ok(aloneKeys.includes(wanted), `${wanted} missing from ${aloneKeys.join(', ')}`);
    }
    assert.ok(!aloneKeys.includes('action:bot.take') && !aloneKeys.includes('action:revive'), 'bot and player items do not apply to a creature');
    assert.deepEqual(alone.find((i) => i.key === 'action:kill').run.targets, [CREATURE.guid]);
    assert.equal(alone.find((i) => i.key === 'action:moveto.pick').run.pick, 'point');
    assert.equal(alone.find((i) => i.key === 'action:moveto.pick').run.point, null, 'the destination is chosen with a second click');

    const group = menuOf({ type: 'entity', entity: CREATURE, point: CREATURE.pos }, [CREATURE.guid, 'F130000000000002', '1000000000000002']);
    const kill = group.find((i) => i.key === 'action:kill');
    assert.deepEqual(kill.run.targets, [CREATURE.guid, 'F130000000000002'], 'the bot is not a creature');
    assert.equal(kill.run.bulk, true);
    assert.match(kill.label, /\(2\)/);
});

test('bot items: take control on a bot, and selected bots can be told to attack the clicked unit', () => {
    const bot = { guid: '1000000000000002', kind: 'bot', name: 'Alphabot', pos: [0, 0, 0] };
    const keys = ids(menuOf({ type: 'entity', entity: bot, point: bot.pos }));
    assert.ok(keys.includes('action:bot.take') && keys.includes('action:bot.release') && keys.includes('action:bot.follow') && keys.includes('action:bot.goto'));
    assert.ok(!keys.includes('action:bot.attack'), 'no bot is selected to give the order to');
    const attack = menuOf({ type: 'entity', entity: CREATURE, point: CREATURE.pos }, ['1000000000000002']).find((i) => i.key === 'action:bot.attack');
    assert.deepEqual(attack.run.targets, ['1000000000000002']);
    assert.deepEqual(attack.run.args, { unit: CREATURE.guid });
    assert.deepEqual(attack.run.argSpecs, [], 'the unit is the clicked entity, so it is not asked for');
});

test('items the daemon refuses (I8 gated) are not in the menu: the menu holds only what the catalog lists', () => {
    assert.deepEqual(GATED.sort(), ['ai.pause', 'ai.resume']);
    const offered = menuOf({ type: 'entity', entity: CREATURE, point: CREATURE.pos });
    assert.ok(ids(offered).includes('action:ai.pause'), 'the full catalog offers AI pause');
    const refused = menuOf({ type: 'entity', entity: CREATURE, point: CREATURE.pos }, [], { items: ITEMS.filter((i) => !i.gated) });
    assert.ok(!ids(refused).some((k) => k.startsWith('action:ai.')), 'a catalog without the gated items gives a menu without them');
    assert.ok(ids(refused).includes('action:kill'));
    assert.deepEqual(menuOf({ type: 'point', point: [0, 0, 0] }, [], { items: [] }, () => false).map((i) => i.key), ['client:copy'], 'a catalog with nothing (actions off) leaves only the local helper');
});

test('every catalog action appears in some menu, and runs through an action.at or action.on call', () => {
    const selection = ['1000000000000002', '1000000000000003', 'F130000000000001', '1000000000000009'];
    const seen = new Set();
    const contexts = [
        { type: 'point', point: [1, 2, 3] },
        ...['creature', 'bot', 'player'].map((kind) => ({ type: 'entity', entity: { guid: kind === 'bot' ? '1000000000000002' : kind === 'player' ? '1000000000000009' : CREATURE.guid, kind, name: 'x', pos: [0, 0, 0] }, point: [0, 0, 0] })),
    ];
    for (const target of contexts) {
        for (const item of menuOf(target, selection)) {
            if (item.run.type === 'action') {
                seen.add(item.run.actionId);
                const entry = ITEMS.find((i) => i.id === item.run.actionId);
                assert.ok(entry, `${item.key} is not in the catalog`);
                assert.equal(entry.scope === 'point', target.type === 'point', `${item.key} is in the wrong menu`);
            }
        }
    }
    assert.deepEqual([...seen].sort(), ITEMS.map((i) => i.id).sort());
    assert.ok(ITEMS.length >= 20);
});

test('menu sections come out in menu order with no empty heading', () => {
    const grouped = groupItems(menuOf({ type: 'point', point: [0, 0, 0] }));
    assert.deepEqual(grouped.map(([heading]) => heading), ['Move', 'Spawn', 'Debug', 'View']);
    assert.ok(grouped.every(([, list]) => list.length > 0));
});

test('palette entries autocomplete on path words, rank prefixes first and always offer the typed line', () => {
    const catalog = {
        items: ITEMS,
        commands: [
            { path: '.cheat god', security: 'GameMaster', write: true, help: 'Syntax: .cheat god on\\|off [$name] The player takes damage.' },
            { path: '.cheat power', security: 'GameMaster', write: true, help: 'Syntax: .cheat power on\\|off [$name] No power cost.' },
            { path: '.creature kill', security: 'GameMaster', write: true, help: 'Syntax: .creature kill — kill the selected creature.' },
            { path: '.honor show', security: 'Player', write: false, help: 'Syntax: .honor show Show the honor.' },
        ],
    };
    const entries = paletteEntries(catalog);
    assert.equal(entries.filter((e) => e.kind === 'action').length, ITEMS.length);
    const cheat = filterEntries(entries, 'ch go');
    assert.equal(cheat[0].path, 'cheat god');
    assert.equal(cheat.at(-1).kind, 'raw', 'the typed line is always the last choice');
    assert.equal(cheat.at(-1).command, 'ch go');
    assert.equal(filterEntries(entries, '.crea').find((e) => e.kind === 'command').path, 'creature kill');
    assert.deepEqual(filterEntries(entries, '').map((e) => e.kind).filter((k) => k === 'raw'), [], 'nothing typed, no raw entry');
    assert.equal(entries.find((e) => e.key === 'cmd:cheat god').parsed.args[0].kind, 'choice');
    assert.equal(entries.find((e) => e.key === 'cmd:honor show').write, false);
    assert.equal(filterEntries(entries, 'zzzzqq').length, 1, 'only the raw entry matches nonsense');
});

test('the action log model keeps ids ascending, marks undone entries and finds the last undoable', () => {
    const log = new ActionLogModel();
    let changes = 0;
    log.on('change', () => changes++);
    log.replace([{ id: 2, ok: true, undoable: true }, { id: 1, ok: true, undoable: false }]);
    assert.deepEqual(log.entries.map((e) => e.id), [1, 2]);
    assert.equal(log.lastUndoable().id, 2);
    log.ingest({ id: 3, ok: true, undoable: true, undoOf: 2 });
    assert.equal(log.entries.find((e) => e.id === 2).undoneBy, 3);
    assert.equal(log.entries.find((e) => e.id === 2).undoable, false);
    assert.equal(log.lastUndoable().id, 3);
    log.ingest({ id: 3, ok: true, undoable: false });
    assert.equal(log.lastUndoable(), null);
    assert.equal(log.entries.length, 3, 'an entry seen twice is stored once');
    assert.equal(changes, 3);
});

test('the bulk confirmation names the targets up to a cap, then counts the rest', () => {
    const guids = Array.from({ length: 15 }, (_, i) => `G${i}`);
    const lines = targetLines(guids, (g) => (g === 'G0' ? 'Wolf' : ''));
    assert.equal(lines.length, 13);
    assert.equal(lines[0], 'Wolf');
    assert.equal(lines[1], 'G1', 'an unknown name falls back to the guid');
    assert.equal(lines.at(-1), 'and 3 more');
    assert.deepEqual(targetLines(['A', 'B'], () => 'x'), ['x', 'x']);
    assert.equal(typeof confirmDialog, 'function');
});

function fakeApp(results) {
    const calls = [];
    const toasts = [];
    const removed = [];
    return {
        calls, toasts, removed,
        events: { emit: () => {} },
        overlays: { remove: (name) => removed.push(name) },
        toast: (message, kind) => toasts.push({ message, kind }),
        runCommand: async (name, args, options) => {
            calls.push({ name, args, options });
            return results.shift() ?? { ok: true, data: { ok: true } };
        },
    };
}

test('the debug overlay refreshes through action.on at no more than twice a second and stops when the daemon refuses', async () => {
    const app = fakeApp([{ ok: true, data: { ok: true, overlay: { points: [] } } }, { ok: true, data: { ok: false, message: 'target gone' } }]);
    const timers = [];
    const cleared = [];
    const overlays = new DebugOverlays({ app, intervalMs: 50, setTimer: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }, clearTimer: (t) => cleared.push(t) });
    assert.equal(overlays.intervalMs, MIN_INTERVAL_MS, 'faster than 2 Hz is clamped');
    assert.equal(await overlays.watch('F1'), true);
    assert.deepEqual(app.calls[0], { name: 'action.on', args: { template: 'debug', guid: 'F1' }, options: { quiet: true, noRefresh: true } });
    assert.equal(timers[0].ms, 500);
    assert.equal(overlays.watched, 'F1');
    assert.equal(await overlays.refresh(), false, 'the daemon refused the second run');
    assert.equal(overlays.watched, null);
    assert.deepEqual(cleared, [1]);
    assert.deepEqual(app.removed, ['action.on']);
    assert.match(app.toasts[0].message, /AI overlay stopped: target gone/);
    assert.equal(await overlays.refresh(), false, 'nothing is watched, nothing is sent');
    assert.equal(app.calls.length, 2);
});

test('toggling the same creature again switches the overlay off, another creature switches it over', async () => {
    const app = fakeApp([]);
    const overlays = new DebugOverlays({ app, setTimer: () => 1, clearTimer: () => {} });
    await overlays.toggle('A');
    assert.equal(overlays.watched, 'A');
    await overlays.toggle('B');
    assert.equal(overlays.watched, 'B');
    assert.equal(await overlays.toggle('B'), false);
    assert.equal(overlays.watched, null);
    assert.deepEqual(app.removed, ['action.on', 'action.on']);
});

// ---- in a real browser ------------------------------------------------------------------------------------------------------

const executable = browserFromEnvironment();
const skip = executable ? false : 'ARCANECORE_TEST_BROWSER does not name a Chrome or Edge executable';
const ALT = 1;
const CTRL = 2;
const SHIFT = 8;

for (const name of ['action.catalog', 'action.run', 'action.at', 'action.on', 'action.confirm', 'action.log', 'action.undo', 'devui.select', 'devui.selection']) {
    if (!MOCK_COMMANDS.some((c) => c.name === name)) {
        MOCK_COMMANDS.push({ name, summary: `Dev UI ${name}.`, surfaces: ['Palette'], schema: { type: 'object', properties: {}, additionalProperties: true } });
    }
}

/** A scripted Atlas host for the action commands: records calls, asks for a confirmation on a bulk kill, keeps a log with undo. */
function installActionHost(host, items) {
    const state = { calls: [], log: [], next: 1, pending: null, catalogs: 0 };
    const entry = (action, lines, extra = {}) => ({
        id: state.next++, at: new Date().toISOString(), actor: 'atlas-local:test#1', action, lines, targets: [], ok: true,
        replies: ['done'], writes: [], undoable: true, ...extra,
    });
    const finish = (args, template) => {
        const e = entry(template, [`template ${template}`], { targets: args.targets ?? [] });
        state.log.push(e);
        host.broadcast('devui.action', e);
        return { id: e.id, logId: e.id, ok: true, action: template, lines: e.lines, replies: e.replies, settled: true, targets: (args.targets ?? []).map((g) => ({ guid: g, ok: true })), writes: [], undoable: true };
    };
    host.handle('action.catalog', () => {
        state.catalogs++;
        return { actions: true, role: 'local', liveServer: false, serverCatalog: false, templates: [], items, commands: [], selection: { guids: [], count: 0, version: 0 }, map: 0 };
    });
    host.handle('action.at', (args) => {
        state.calls.push({ name: 'action.at', args });
        return finish(args, args.template);
    });
    host.handle('action.on', (args) => {
        state.calls.push({ name: 'action.on', args });
        if (args.template === 'kill' && (args.targets?.length ?? 0) > 1 && !args.confirm) {
            state.pending = args;
            return { id: 0, ok: false, code: 'confirm-required', message: 'confirm', lines: [], confirm: { code: 'AB12CD', expiresIn: 60, summary: `Kill on ${args.targets.length} targets`, source: 'daemon' } };
        }
        return finish(args, args.template);
    });
    host.handle('action.confirm', (args) => {
        state.calls.push({ name: 'action.confirm', args });
        const pending = state.pending;
        state.pending = null;
        return finish(pending, pending.template);
    });
    host.handle('action.run', (args) => {
        state.calls.push({ name: 'action.run', args });
        return finish({ ...args, targets: args.targets ?? [] }, 'run');
    });
    host.handle('action.log', () => ({ entries: state.log, count: state.log.length }));
    host.handle('action.undo', (args) => {
        state.calls.push({ name: 'action.undo', args });
        const original = state.log.find((e) => e.id === args.id);
        original.undoable = false;
        const e = entry('undo', ['creature respawn'], { undoable: false, undoOf: args.id });
        original.undoneBy = e.id;
        state.log.push(e);
        host.broadcast('devui.action', e);
        return { id: e.id, logId: e.id, ok: true, action: 'undo', lines: e.lines, replies: ['done'], settled: true, targets: [], writes: [], undoable: false };
    });
    host.handle('devui.select', (args) => {
        state.calls.push({ name: 'devui.select', args });
        return { guids: args.guids, count: args.guids.length, version: state.calls.length };
    });
    host.handle('devui.selection', () => ({ guids: [], count: 0, version: 0 }));
    return state;
}

test('the dev UI in a real browser', { skip }, async (t) => {
    const browser = await Browser.launch(executable);
    t.after(() => browser.close());
    let actions;
    const s = await startScenario(executable, { browser, configure: (host) => { actions = installActionHost(host, ITEMS.filter((i) => !i.gated)); } });
    t.after(() => s.close());
    const { page, host } = s;
    const ev = (expression) => page.evaluate(expression);
    await page.waitFor('window.__atlas?.devui?.active === true', 'the dev UI to start');

    const stage = JSON.parse(await ev('JSON.stringify(document.querySelector(".stage").getBoundingClientRect())'));
    const at = (fx, fy) => ({ x: Math.round(stage.left + stage.width * fx), y: Math.round(stage.top + stage.height * fy) });
    const rightPress = async (p) => page.mouse('mousePressed', p.x, p.y, { button: 'right', buttons: 2, clickCount: 1 });
    const rightRelease = async (p) => page.mouse('mouseReleased', p.x, p.y, { button: 'right', buttons: 0, clickCount: 1 });
    const screenOf = async (pos) => JSON.parse(await ev(`(async () => {
        const c = await import('/app/js/camera.js');
        const v = window.__atlas.viewport;
        const n = c.project(window.__atlas.app.view.camera, ${JSON.stringify(pos)}, v.cssWidth / v.cssHeight);
        const p = c.ndcToScreen(n.x, n.y, v.cssWidth, v.cssHeight);
        const r = v.canvas.getBoundingClientRect();
        return JSON.stringify({ x: Math.round(p.x + r.left), y: Math.round(p.y + r.top) });
    })()`));
    const menuText = () => ev("[...document.querySelectorAll('.devui-menu-item')].map((b) => b.textContent).join('|')");
    const clickButton = (selector, text) => ev(`[...document.querySelectorAll(${JSON.stringify(selector)})].find((b) => b.textContent.startsWith(${JSON.stringify(text)})).click()`);

    await t.test('a right drag of 10 px pans and opens no menu', async () => {
        const before = await ev('JSON.stringify(window.__atlas.app.view.camera.target)');
        const p = at(0.3, 0.75);
        await page.mouse('mouseMoved', p.x, p.y);
        await rightPress(p);
        await page.mouse('mouseMoved', p.x + 5, p.y, { button: 'right', buttons: 2 });
        await page.mouse('mouseMoved', p.x + 10, p.y, { button: 'right', buttons: 2 });
        await rightRelease({ x: p.x + 10, y: p.y });
        await new Promise((r) => setTimeout(r, 300));
        assert.notEqual(await ev('JSON.stringify(window.__atlas.app.view.camera.target)'), before, 'the camera panned');
        assert.equal(await ev("document.querySelector('.devui-menu') === null"), true, 'no menu after a drag');
    });

    await t.test('a right click without movement opens the catalog-driven menu at the cursor', async () => {
        const p = at(0.3, 0.75);
        await page.mouse('mouseMoved', p.x, p.y);
        await rightPress(p);
        await rightRelease(p);
        await page.waitFor("document.querySelector('.devui-menu')", 'the menu');
        const box = JSON.parse(await ev('JSON.stringify(document.querySelector(".devui-menu").getBoundingClientRect())'));
        assert.ok(Math.abs(box.left - p.x) <= 2, `menu left ${box.left}, cursor ${p.x}`);
        assert.ok(Math.abs(box.top - p.y) <= 2 || (box.top <= p.y && box.bottom <= 900), `the menu starts at the cursor, or is moved up just enough to stay on screen: ${box.top}..${box.bottom}, cursor ${p.y}`);
        const text = await menuText();
        assert.match(text, /Teleport me here/);
        assert.match(text, /Spawn creature/);
        assert.match(text, /Copy coordinates/);
        assert.doesNotMatch(text, /AI: pause/, 'the catalog the host sent has no gated item');
        await page.press('Escape');
        await page.waitFor("document.querySelector('.devui-menu') === null", 'Escape to close the menu');
    });

    await t.test('spawn from the menu asks for the entry and sends action.at with the clicked point', async () => {
        const p = at(0.3, 0.75);
        await page.mouse('mouseMoved', p.x, p.y);
        await rightPress(p);
        await rightRelease(p);
        await page.waitFor("document.querySelector('.devui-menu')", 'the menu');
        await clickButton('.devui-menu-item', 'Spawn creature');
        await page.waitFor("document.querySelector('#devui-arg-entry')", 'the entry prompt');
        await ev("(() => { const i = document.querySelector('#devui-arg-entry'); i.value = '299'; })()");
        await ev("document.querySelector('.devui-args').requestSubmit()");
        await page.waitFor('true', 'nothing');
        const call = await page.waitFor('true', 'tick').then(() => actions.calls.find((c) => c.name === 'action.at'));
        assert.ok(call, 'the host saw action.at');
        assert.equal(call.args.template, 'place.creature');
        assert.equal(String(call.args.args.entry), '299');
        assert.equal(call.args.at.length, 3);
        assert.ok(call.args.at.every((n) => Number.isFinite(n)));
    });

    await t.test('click selects, Alt+drag boxes, Alt+K asks once for the whole selection and Undo reverses it', async () => {
        host.broadcast('live.status', { connected: true, pid: 1, sha: 'abc' });
        const mobs = [0, 1, 2].map((i) => ({ g: (0x5000 + i).toString(16), k: 'creature', e: 299, n: `Wolf ${i}`, x: INN[0] - 6 + i * 6, y: INN[1] + 4, z: 57, o: 0, hp: 100, mt: 'Idle' }));
        const far = { g: '6000', k: 'creature', e: 299, n: 'Far', x: INN[0] + 400, y: INN[1] + 400, z: 57, o: 0, hp: 100, mt: 'Idle' };
        host.broadcast('live.frame', { seq: 9, clockMs: 9000, map: 0, instance: 0, grids: [[14, 32]], counters: {}, entities: [...mobs, far] });
        await page.waitFor('window.__atlas.viewport.liveEntities.length >= 3', 'the live entities');
        const screens = [];
        for (const m of mobs) {
            screens.push(await screenOf([m.x, m.y, m.z]));
        }
        // A plain click selects one.
        await page.mouse('mouseMoved', screens[0].x, screens[0].y);
        await page.mouse('mousePressed', screens[0].x, screens[0].y, { button: 'left', buttons: 1, clickCount: 1 });
        await page.mouse('mouseReleased', screens[0].x, screens[0].y, { button: 'left', buttons: 0, clickCount: 1 });
        await page.waitFor("window.__atlas.devui.selection.guids.length === 1", 'the click to select');
        // Alt+drag a box around all three.
        const xs = screens.map((p) => p.x);
        const ys = screens.map((p) => p.y);
        const from = { x: Math.min(...xs) - 30, y: Math.min(...ys) - 30 };
        const to = { x: Math.max(...xs) + 30, y: Math.max(...ys) + 30 };
        await page.mouse('mouseMoved', from.x, from.y, { modifiers: ALT });
        await page.mouse('mousePressed', from.x, from.y, { button: 'left', buttons: 1, clickCount: 1, modifiers: ALT });
        await page.mouse('mouseMoved', (from.x + to.x) / 2, (from.y + to.y) / 2, { button: 'left', buttons: 1, modifiers: ALT });
        await page.mouse('mouseMoved', to.x, to.y, { button: 'left', buttons: 1, modifiers: ALT });
        assert.equal(await ev("document.querySelector('.devui-band').hidden"), false, 'the rubber band shows while dragging');
        await page.mouse('mouseReleased', to.x, to.y, { button: 'left', buttons: 0, clickCount: 1, modifiers: ALT });
        await page.waitFor("window.__atlas.devui.selection.guids.length === 3", 'the box to select three');
        assert.deepEqual(await ev('window.__atlas.devui.selection.guids.slice().sort()'), mobs.map((m) => m.g.toUpperCase()).sort());
        assert.ok(actions.calls.some((c) => c.name === 'devui.select' && c.args.guids.length === 3), 'the selection reached the host');

        // Alt+K: one confirmation with the count and the names, then it runs once.
        await page.press('k', { modifiers: ALT });
        await page.waitFor("document.querySelector('.devui-confirm')", 'the bulk confirmation');
        assert.match(await ev("document.querySelector('.devui-confirm-summary').textContent"), /Kill on 3 targets/);
        assert.equal(await ev("document.querySelectorAll('.devui-targets li').length"), 3);
        assert.match(await ev("document.querySelector('.devui-targets').textContent"), /Wolf 0/);
        assert.ok(!actions.calls.some((c) => c.name === 'action.confirm'), 'nothing ran before the operator agreed');
        await ev("document.querySelector('.devui-confirm-yes').click()");
        await page.waitFor('true', 'tick');
        await page.waitFor("window.__atlas.devui.log.entries.length >= 1", 'the log entry');
        assert.equal(actions.calls.filter((c) => c.name === 'action.confirm').length, 1);

        // The log panel offers Undo; running it calls action.undo.
        await page.press('l', { modifiers: ALT });
        await page.waitFor("!document.querySelector('.devui-log').hidden", 'the log panel');
        await page.waitFor("document.querySelector('.devui-log-undo')", 'the Undo button');
        const undoButtons = await ev("document.querySelectorAll('.devui-log-undo').length");
        await ev("document.querySelector('.devui-log-undo').click()");
        await page.waitFor('true', 'tick');
        const undo = await page.waitFor('true', 'tick').then(() => actions.calls.find((c) => c.name === 'action.undo'));
        assert.ok(undo, 'the host saw action.undo');
        await page.waitFor(`document.querySelectorAll('.devui-log-undo').length === ${undoButtons - 1}`, 'the undone entry to lose its button');
        // Esc clears the selection.
        await ev('document.querySelector(".stage").focus()');
        await page.press('Escape');
        await page.waitFor('window.__atlas.devui.selection.guids.length === 0', 'Escape to clear the selection');
    });

    await t.test('Ctrl+Shift+P opens the GM palette, filters the catalog and sends a typed command', async () => {
        await page.press('P', { modifiers: CTRL | SHIFT });
        await page.waitFor("document.querySelector('.devui-palette .palette-input')", 'the palette');
        await ev("(() => { const i = document.querySelector('.devui-palette .palette-input'); i.value = 'kill'; i.dispatchEvent(new Event('input')); })()");
        const names = await ev("[...document.querySelectorAll('.devui-palette .palette-name')].map((n) => n.textContent).join('|')");
        assert.match(names, /Kill/);
        assert.match(names, /Run as a command: \.kill/);
        await ev("(() => { const i = document.querySelector('.devui-palette .palette-input'); i.value = 'creature respawn'; i.dispatchEvent(new Event('input')); })()");
        await page.press('ArrowDown');
        await page.press('Enter');
        await page.waitFor("document.querySelector('.devui-palette .palette-result .result-status')", 'the result');
        assert.ok(actions.calls.some((c) => c.name === 'action.run'), 'the host saw action.run');
        assert.equal(actions.calls.find((c) => c.name === 'action.run').args.command.length > 0, true);
        await page.press('P', { modifiers: CTRL | SHIFT });
        await page.waitFor("!document.querySelector('.backdrop')", 'the palette to close');
    });

    await t.test('no console errors, exceptions or blocked requests', async () => {
        assert.deepEqual(page.problems.filter((p) => !p.includes('ERR_ABORTED')), []);
    });
});
