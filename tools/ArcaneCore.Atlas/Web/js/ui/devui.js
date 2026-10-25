// The Atlas dev UI for the local operator: selection (click, shift-click, Alt+drag box), the right-click context menu built from the
// daemon's action catalog, the GM command palette, the action log with undo, bulk confirmation, debug overlays and the keyboard map.
// Every action goes through an `action.*` / `devui.*` command of the Atlas host, so the command line and MCP behave the same; the browser
// holds no privilege of its own and sends only action requests. Nothing here changes viewport.js, hotkeys.js or the frozen panels: it listens
// on the stage in the capture phase (Alt+drag) and on the canvas and document otherwise.

import { ndcToScreen, project } from '../camera.js';
import { pickEntity } from '../live.js';
import { Emitter, fill, h } from '../util.js';
import { confirmDialog, promptArgs, targetLines } from './devui-confirm.js';
import { installKeys } from './devui-keys.js';
import { ActionLogModel, ActionLogPanel } from './devui-log.js';
import { buildMenu, openMenu } from './devui-menu.js';
import { DebugOverlays } from './devui-overlays.js';
import { openDevPalette } from './devui-palette.js';
import { closeTopDialog, hasOpenDialog } from './popover.js';
import { copyText } from './widgets.js';

/** A right click opens the menu only when it is a click: the pointer moved less than this far (px, the same threshold nav.js uses to start a pan) and was down at most this long (ms). */
export const CLICK_MAX_MOVE = 4;
export const CLICK_MAX_MS = 300;

/** The most entities one selection holds (the daemon accepts 50 targets per action). */
export const MAX_SELECTION = 50;

/** A box smaller than this (px) in both directions is a click, not a box. */
const MIN_BOX = 4;

/** True when a pointer press and release make a click (as opposed to a drag, which pans). `down` and `up` are `{x, y, t}`. Pure. */
export function isContextClick(down, up) {
    return Math.hypot(up.x - down.x, up.y - down.y) < CLICK_MAX_MOVE && up.t - down.t <= CLICK_MAX_MS;
}

/** The normalised rectangle of two corner points. Pure. */
export function rectOf(a, b) {
    return { x0: Math.min(a.x, b.x), y0: Math.min(a.y, b.y), x1: Math.max(a.x, b.x), y1: Math.max(a.y, b.y) };
}

/** The entities whose projected screen position lies inside `rect`. `project(pos)` returns `{x, y}` or null. Pure. */
export function entitiesInRect(entities, projectPos, rect) {
    const found = [];
    for (const entity of entities) {
        const p = projectPos(entity.pos);
        if (p && p.x >= rect.x0 && p.x <= rect.x1 && p.y >= rect.y0 && p.y <= rect.y1) {
            found.push(entity);
        }
    }
    return found;
}

/** The selection model: an ordered set of upper-case hex guids, at most MAX_SELECTION. Emits `change` with the list. */
export class DevSelection extends Emitter {
    #guids = [];

    get guids() {
        return [...this.#guids];
    }

    has(guid) {
        return this.#guids.includes(guid);
    }

    #apply(next) {
        const capped = next.slice(0, MAX_SELECTION);
        if (capped.length === this.#guids.length && capped.every((g, i) => g === this.#guids[i])) {
            return false;
        }
        this.#guids = capped;
        this.emit('change', this.guids);
        return true;
    }

    set(list) {
        return this.#apply([...new Set(list)]);
    }

    add(list) {
        return this.#apply([...new Set([...this.#guids, ...list])]);
    }

    remove(list) {
        const drop = new Set(list);
        return this.#apply(this.#guids.filter((g) => !drop.has(g)));
    }

    toggle(guid) {
        return this.has(guid) ? this.remove([guid]) : this.add([guid]);
    }

    clear() {
        return this.#apply([]);
    }
}

const sameList = (a, b) => a.length === b.length && a.every((g, i) => g === b[i]);

export class DevUi {
    constructor({ app, viewport }) {
        this.app = app;
        this.viewport = viewport;
        this.selection = new DevSelection();
        this.log = new ActionLogModel();
        this.overlays = new DebugOverlays({ app });
        this.active = false;
        this.catalogCache = null;
        this.menu = null;
        this.paletteOpen = false;
        this.pointerAt = { x: 0, y: 0, clientX: 0, clientY: 0 };
        this.down = null;
        this.shiftAtDown = false;
        this.picking = null;
        this.box = null;
        this.listeners = [];
        this.frame = 0;
        this.syncing = false;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Start

    /** Activates once the host lists the action commands (an older host never does, and the dev UI stays out of the way). */
    start() {
        const ready = () => {
            if (!this.active && this.app.hasCommand('action.catalog')) {
                this.#activate();
            }
        };
        this.app.events.on('commands', ready);
        this.app.events.on('connection', (c) => c.state === 'unauthorized' && this.dispose());
        ready();
    }

    #on(target, type, handler, options) {
        target.addEventListener(type, handler, options);
        this.listeners.push(() => target.removeEventListener(type, handler, options));
    }

    #activate() {
        this.active = true;
        const root = this.viewport.root;
        const sheet = h('link', { rel: 'stylesheet', href: '/app/devui.css', id: 'devui-css' });
        document.head.append(sheet);

        this.ring = h('canvas', { class: 'devui-rings', aria: { hidden: 'true' } });
        this.band = h('div', { class: 'devui-band', hidden: true, aria: { hidden: 'true' } });
        this.panel = new ActionLogPanel({ model: this.log, onUndo: (entry) => this.undo(entry), onClose: () => this.panel.toggle(false) });
        this.bar = h('div', { class: 'devui-bar', role: 'toolbar', aria: { label: 'Dev tools' } },
            h('button', { class: 'button', type: 'button', text: 'GM', title: 'GM command palette (Ctrl+Shift+P)', on: { click: () => this.togglePalette() } }),
            h('button', { class: 'button', type: 'button', text: 'Log', title: 'Action log (Alt+L)', on: { click: () => this.panel.toggle() } }));
        root.append(this.ring, this.band, this.bar, this.panel.element);

        // Pointer: right click without drag opens the menu; Alt+left-drag is a box; clicks feed the selection.
        const canvas = this.viewport.canvas;
        this.#on(root, 'pointerdown', (e) => this.#captureDown(e), true);
        this.#on(canvas, 'pointerdown', (e) => this.#down(e));
        this.#on(canvas, 'pointerup', (e) => this.#up(e));
        this.#on(canvas, 'pointermove', (e) => this.#moved(e));
        this.#on(canvas, 'contextmenu', (e) => e.preventDefault());
        this.#on(document, 'keydown', (e) => this.#pickKey(e), true);
        this.removeKeys = installKeys({ run: (id, e) => this.#key(id, e) });

        this.selection.on('change', (guids) => this.#selectionChanged(guids));
        this.app.events.on('selection', (sel) => this.#appSelection(sel));
        this.app.events.on('event', (topic, data) => this.#event(topic, data));
        this.app.events.on('connection', (c) => c.state === 'open' && this.refreshLog());
        this.app.live.on('change', () => this.#redrawSoon());
        this.app.events.on('view', () => this.#redrawSoon());
        new ResizeObserver(() => this.#redrawSoon()).observe(root);

        this.refreshLog();
        if (window.__atlas) {
            window.__atlas.devui = this;
        }
    }

    /** Removes every listener and element (the host refused the token, or a test ended). */
    dispose() {
        this.overlays.stop();
        this.menu?.close();
        this.removeKeys?.();
        for (const off of this.listeners) {
            off();
        }
        this.listeners = [];
        this.ring?.remove();
        this.band?.remove();
        this.bar?.remove();
        this.panel?.element.remove();
        document.getElementById('devui-css')?.remove();
        cancelAnimationFrame(this.frame);
        this.active = false;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Geometry

    #local(e) {
        const rect = this.viewport.canvas.getBoundingClientRect();
        return { x: e.clientX - rect.left, y: e.clientY - rect.top, w: rect.width, h: rect.height };
    }

    /** World position -> viewport pixel, or null when behind the camera. */
    #screenOf(pos) {
        const w = this.viewport.cssWidth;
        const hgt = this.viewport.cssHeight;
        const n = project(this.app.view.camera, pos, w / hgt);
        return n ? ndcToScreen(n.x, n.y, w, hgt) : null;
    }

    /** The live entity under a viewport pixel, as `{guid, kind, name, pos}`, or null. */
    #entityAt(x, y) {
        const hit = pickEntity(this.viewport.liveEntities, (p) => this.#screenOf(p), x, y, 14);
        return hit ? { guid: hit.guid, kind: hit.kind, name: hit.name, pos: hit.pos } : null;
    }

    #kindOf(guid) {
        return this.app.live.find(guid)?.entity.kind ?? null;
    }

    #nameOf(guid) {
        return this.app.live.find(guid)?.entity.name ?? '';
    }

    // ------------------------------------------------------------------------------------------------------------
    // Pointer

    #moved(e) {
        const p = this.#local(e);
        this.pointerAt = { x: p.x, y: p.y, clientX: e.clientX, clientY: e.clientY };
    }

    /** Capture phase on the stage: Alt+left starts a box (the orbit drag never sees it); while picking a destination any left press is the pick. */
    #captureDown(e) {
        if (e.button !== 0 || e.target !== this.viewport.canvas) {
            return;
        }
        if (this.picking) {
            e.preventDefault();
            e.stopPropagation();
            const p = this.#local(e);
            const picking = this.picking;
            this.#endPick();
            const world = this.viewport.pickWorld(p.x, p.y);
            picking.resolve(world.point);
            return;
        }
        if (e.altKey && !e.ctrlKey && !e.metaKey) {
            e.preventDefault();
            e.stopPropagation();
            this.#beginBox(e);
        }
    }

    #down(e) {
        this.shiftAtDown = e.shiftKey;
        if (e.button === 2) {
            this.down = { x: e.clientX, y: e.clientY, t: performance.now() };
        }
    }

    #up(e) {
        if (e.button !== 2 || !this.down) {
            return;
        }
        const down = this.down;
        this.down = null;
        if (isContextClick(down, { x: e.clientX, y: e.clientY, t: performance.now() })) {
            const p = this.#local(e);
            this.openMenuAt(p.x, p.y, e.clientX, e.clientY);
        }
    }

    #beginBox(e) {
        const canvas = this.viewport.canvas;
        const start = this.#local(e);
        const additive = e.shiftKey;
        this.box = { start, current: start };
        canvas.setPointerCapture(e.pointerId);
        const move = (ev) => {
            const p = this.#local(ev);
            this.box.current = p;
            const r = rectOf(start, p);
            this.band.hidden = false;
            this.band.style.left = `${r.x0}px`;
            this.band.style.top = `${r.y0}px`;
            this.band.style.width = `${r.x1 - r.x0}px`;
            this.band.style.height = `${r.y1 - r.y0}px`;
        };
        const finish = (ev, cancelled) => {
            canvas.removeEventListener('pointermove', move);
            canvas.removeEventListener('pointerup', up);
            canvas.removeEventListener('pointercancel', cancel);
            if (canvas.hasPointerCapture(e.pointerId)) {
                canvas.releasePointerCapture(e.pointerId);
            }
            this.band.hidden = true;
            const end = this.#local(ev);
            this.box = null;
            if (cancelled) {
                return;
            }
            const r = rectOf(start, end);
            if (r.x1 - r.x0 < MIN_BOX && r.y1 - r.y0 < MIN_BOX) {
                return;
            }
            const hits = entitiesInRect(this.viewport.liveEntities, (pos) => this.#screenOf(pos), r).map((en) => en.guid);
            if (additive) {
                this.selection.add(hits);
            } else {
                this.selection.set(hits);
            }
        };
        const up = (ev) => finish(ev, false);
        const cancel = (ev) => finish(ev, true);
        canvas.addEventListener('pointermove', move);
        canvas.addEventListener('pointerup', up);
        canvas.addEventListener('pointercancel', cancel);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Selection

    /** A click on the map (the viewport's own click handler ran): an entity sets, shift toggles, empty ground clears. */
    #appSelection(sel) {
        if (this.syncing) {
            return;
        }
        const guid = sel.entityGuid ? String(sel.entityGuid).toUpperCase() : null;
        if (guid) {
            if (this.shiftAtDown) {
                this.selection.toggle(guid);
            } else {
                this.selection.set([guid]);
            }
        } else if (!this.shiftAtDown) {
            this.selection.clear();
        }
    }

    #selectionChanged(guids) {
        this.#redrawSoon();
        if (this.syncing) {
            return;
        }
        this.app.runCommand('devui.select', { guids, mode: 'set' }, { quiet: true, noRefresh: true });
    }

    #event(topic, data) {
        if (topic === 'devui.selection' && Array.isArray(data?.guids)) {
            const guids = data.guids.map((g) => String(g).toUpperCase());
            if (!sameList(guids, this.selection.guids)) {
                this.syncing = true;
                try {
                    this.selection.set(guids);
                } finally {
                    this.syncing = false;
                }
            }
        } else if (topic === 'devui.action' && data && typeof data.id === 'number') {
            this.log.ingest(data);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Selection rings

    #redrawSoon() {
        if (!this.active || this.frame) {
            return;
        }
        this.frame = requestAnimationFrame(() => {
            this.frame = 0;
            this.#drawRings();
        });
    }

    #drawRings() {
        const root = this.viewport.root;
        const dpr = window.devicePixelRatio || 1;
        const w = root.clientWidth;
        const hgt = root.clientHeight;
        if (this.ring.width !== Math.round(w * dpr) || this.ring.height !== Math.round(hgt * dpr)) {
            this.ring.width = Math.round(w * dpr);
            this.ring.height = Math.round(hgt * dpr);
        }
        const ctx = this.ring.getContext('2d');
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, w, hgt);
        ctx.lineWidth = 2;
        ctx.strokeStyle = '#FFE066';
        ctx.fillStyle = 'rgba(255, 224, 102, 0.16)';
        for (const guid of this.selection.guids) {
            const found = this.app.live.find(guid);
            const at = found ? this.#screenOf(found.entity.pos) : null;
            if (!at) {
                continue;
            }
            ctx.beginPath();
            ctx.arc(at.x, at.y, 13, 0, Math.PI * 2);
            ctx.fill();
            ctx.stroke();
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Catalog and the context menu

    /** The `action.catalog` data, reused for two seconds; `force` re-asks. Throws when the host refused. */
    async catalog(force = false) {
        const now = performance.now();
        if (!force && this.catalogCache && now - this.catalogCache.at < 2000) {
            return this.catalogCache.data;
        }
        const result = await this.app.runCommand('action.catalog', {}, { quiet: true, noRefresh: true });
        if (!result.ok) {
            throw new Error(result.error?.message ?? 'action.catalog failed');
        }
        this.catalogCache = { at: now, data: result.data };
        return result.data;
    }

    /** Opens the context menu for the pixel (x, y) of the viewport, drawn at the client position. */
    async openMenuAt(x, y, clientX, clientY) {
        this.menu?.close();
        let catalog;
        try {
            catalog = await this.catalog();
        } catch (error) {
            this.app.toast(`The dev menu is unavailable: ${error.message}`, 'warn');
            return null;
        }
        const entity = this.#entityAt(x, y);
        let target;
        if (entity) {
            if (!this.selection.has(entity.guid)) {
                this.selection.set([entity.guid]);
            }
            target = { type: 'entity', entity, point: entity.pos };
        } else {
            const world = this.viewport.pickWorld(x, y);
            target = { type: 'point', point: world.point };
            this.app.recordClick(world.point);
        }
        const items = catalog.actions === false ? [] : buildMenu({
            catalog,
            target,
            selection: this.selection.guids,
            kindOf: (g) => this.#kindOf(g),
            hasCommand: (name) => this.app.hasCommand(name),
        });
        const heading = entity ? `${entity.name || entity.kind} (${entity.guid})` : `${target.point.map((n) => n.toFixed(1)).join(', ')}`;
        this.menu = openMenu({
            items,
            x: clientX,
            y: clientY,
            heading,
            emptyText: catalog.actions === false ? `Actions are off on the daemon: ${catalog.whyOff ?? 'no reason given'}.` : 'No actions are available here.',
            onPick: (item) => this.execute(item.run),
        });
        return this.menu;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Running things

    /** Runs a menu or palette descriptor; resolves with the action's result data, or null when cancelled. */
    async execute(run) {
        switch (run.type) {
            case 'client':
                return this.#client(run);
            case 'run':
                return this.#send('action.run', { command: run.command, ...(run.targets?.length ? { targets: run.targets } : {}), ...this.#where(run.point) }, run.command, run.targets ?? []);
            default:
                return this.#action(run);
        }
    }

    #where(point) {
        return point ? { at: point, map: this.app.view.map } : {};
    }

    async #action(run) {
        const catalog = await this.catalog().catch(() => null);
        const item = catalog?.items?.find((i) => i.id === run.actionId);
        if (!item) {
            this.app.toast(`The daemon does not offer '${run.actionId}'.`, 'warn');
            return null;
        }
        if (run.actionId === 'debug') {
            await this.overlays.toggle(run.targets[0]);
            return { ok: true };
        }
        let point = run.point;
        if (run.pick === 'point' || (item.needsPoint && !point)) {
            point = await this.#pickPoint(item.label);
            if (!point) {
                return null;
            }
        }
        const args = { ...run.args };
        if (run.argSpecs?.length > 0 && Object.keys(args).length < run.argSpecs.length) {
            const values = await promptArgs({
                title: item.label.replace(/\.\.\.$/, ''),
                specs: run.argSpecs.map((s) => ({ ...s, default: s.default ?? undefined })),
                extra: item.id === 'place.creature' || item.id === 'place.gobject' ? this.#lookupExtra(item.id === 'place.creature' ? 'creature' : 'object') : null,
            });
            if (!values) {
                return null;
            }
            Object.assign(args, values);
        }
        const targets = run.targets ?? [];
        if (item.id === 'despawn' && targets.length === 1) {
            // Several targets are confirmed by the host's confirmation request (with the count and the list); one is asked here.
            const sure = await confirmDialog({
                title: 'Despawn',
                summary: `Despawn ${this.#nameOf(targets[0]) || targets[0]}? A temporary creature is removed; a database spawn is refused.`,
                confirmLabel: 'Despawn',
            });
            if (!sure) {
                return null;
            }
        }
        const body = { template: item.id, ...(targets.length > 0 ? { targets } : {}), ...(Object.keys(args).length > 0 ? { args } : {}), ...this.#where(point) };
        return this.#send(item.scope === 'point' ? 'action.at' : 'action.on', body, item.label, targets);
    }

    /** Sends an action command; handles a confirmation request, toasts the outcome and refreshes the log. */
    async #send(command, body, label, targets) {
        let result = await this.app.runCommand(command, body, { quiet: true, noRefresh: true });
        for (let round = 0; round < 3; round++) {
            if (!result.ok) {
                this.app.toast(`${label}: ${result.error?.message ?? 'failed'}`, 'error');
                return { ok: false, message: result.error?.message ?? 'failed' };
            }
            if (result.data?.code !== 'confirm-required' || !result.data.confirm) {
                break;
            }
            const sure = await confirmDialog({
                title: 'Confirm',
                summary: result.data.confirm.summary,
                lines: targetLines(targets, (g) => this.#nameOf(g)),
                confirmLabel: 'Run',
            });
            if (!sure) {
                return null;
            }
            result = await this.app.runCommand('action.confirm', { code: result.data.confirm.code }, { quiet: true, noRefresh: true });
        }
        const data = result.data;
        if (data.ok) {
            this.app.toast(`${label}: done${data.replies?.[0] ? ` (${String(data.replies[0]).slice(0, 80)})` : ''}`, 'info');
        } else {
            this.app.toast(`${label}: ${data.message || data.code || 'failed'}`, 'warn');
        }
        this.refreshLog();
        return data;
    }

    /** Asks the operator to click the destination; resolves with the world point, or null on Escape. */
    #pickPoint(label) {
        this.app.toast(`${label}: click the destination on the map (Esc cancels).`, 'info');
        this.viewport.root.classList.add('devui-picking');
        return new Promise((resolve) => {
            this.picking = { resolve };
        });
    }

    #endPick() {
        this.picking = null;
        this.viewport.root.classList.remove('devui-picking');
    }

    #pickKey(e) {
        if (this.picking && e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            const picking = this.picking;
            this.#endPick();
            picking.resolve(null);
        }
    }

    /** The "Spawn creature..." field helper: search the daemon's templates by name through `.lookup`, click a line to use its entry. */
    #lookupExtra(kind) {
        const list = h('ul', { class: 'devui-lookup plain-list', aria: { label: 'Matches' } });
        const input = h('input', { class: 'input', type: 'text', placeholder: `Search ${kind} by name`, autocomplete: 'off', spellcheck: false });
        const search = async () => {
            const name = input.value.trim();
            if (!/^[A-Za-z0-9 '_-]{2,40}$/.test(name)) {
                fill(list, h('li', { class: 'muted', text: 'Enter 2 to 40 letters or digits.' }));
                return;
            }
            const result = await this.app.runCommand('action.run', { command: `lookup ${kind} ${name}` }, { quiet: true, noRefresh: true });
            const lines = result.ok ? (result.data.replies ?? []).slice(0, 12) : [result.error?.message ?? 'failed'];
            fill(list, ...lines.map((line) => {
                const entry = /\b(\d{1,10})\b/.exec(line);
                return h('li', null, h('button', {
                    class: 'link-button', type: 'button', text: line, title: entry ? `Use entry ${entry[1]}` : '',
                    on: { click: () => { const field = document.getElementById('devui-arg-entry'); if (field && entry) { field.value = entry[1]; } } },
                }));
            }));
        };
        return h('div', { class: 'devui-lookup-box' },
            h('div', { class: 'devui-lookup-row' }, input, h('button', { class: 'button', type: 'button', text: 'Search', on: { click: search } })),
            list);
    }

    async #client(run) {
        const app = this.app;
        switch (run.name) {
            case 'copy':
                app.toast((await copyText(run.text)) ? 'Copied.' : 'Copy failed: the browser refused clipboard access.', 'info');
                return { ok: true };
            case 'select':
                if (run.on) {
                    this.selection.add([run.guid]);
                } else {
                    this.selection.remove([run.guid]);
                }
                return { ok: true };
            case 'follow':
                return (await app.runCommand('live.follow', { guid: run.guid }, { noRefresh: true })).data ?? null;
            case 'inspect':
                app.events.emit('focus-tab', 'live');
                return (await app.runCommand('live.inspect', { guid: run.guid }, { noRefresh: true })).data ?? null;
            case 'query': {
                const map = app.view.map;
                const args = run.kind === 'height' ? { kind: 'height', a: run.point, map } : { kind: run.kind, a: this.app.live.find(run.from)?.entity.pos ?? run.point, b: run.point, map };
                return (await app.runCommand('live.query', args, { noRefresh: true })).data ?? null;
            }
            case 'bookmark': {
                const values = await promptArgs({ title: 'Bookmark here', specs: [{ name: 'name', type: 'text', label: 'Name' }], submitLabel: 'Save' });
                if (!values) {
                    return null;
                }
                const moved = await app.runCommand('camera.goto', { map: app.view.map, at: run.point }, { noRefresh: true });
                return moved.ok ? ((await app.runCommand('bookmark.add', { name: values.name }, { noRefresh: true })).data ?? null) : null;
            }
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Log and undo

    async refreshLog() {
        const result = await this.app.runCommand('action.log', { limit: 100 }, { quiet: true, noRefresh: true });
        if (result.ok && Array.isArray(result.data?.entries)) {
            this.log.replace(result.data.entries);
        }
    }

    async undo(entry) {
        const result = await this.app.runCommand('action.undo', { id: entry.id }, { quiet: true, noRefresh: true });
        if (!result.ok) {
            this.app.toast(`Undo failed: ${result.error?.message ?? 'refused'}`, 'warn');
        } else if (!result.data.ok) {
            this.app.toast(`Undo failed: ${result.data.message || result.data.code}`, 'warn');
        } else {
            this.app.toast(`Undid #${entry.id}.`, 'info');
        }
        this.refreshLog();
        return result;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Palette and keys

    togglePalette() {
        if (this.paletteOpen) {
            closeTopDialog();
            return;
        }
        if (hasOpenDialog()) {
            return;
        }
        this.paletteOpen = true;
        openDevPalette({
            onClose: () => {
                this.paletteOpen = false;
            },
            loadCatalog: () => this.catalog(true),
            selection: () => ({ guids: this.selection.guids, names: this.selection.guids.map((g) => this.#nameOf(g)) }),
            lastPoint: () => this.app.clicks[0] ?? null,
            run: async (descriptor) => (await this.execute(descriptor)) ?? { ok: false, message: 'cancelled' },
        });
    }

    async #selectionAction(id) {
        const guids = this.selection.guids;
        if (guids.length === 0) {
            return false;
        }
        const catalog = await this.catalog().catch(() => null);
        const item = catalog?.items?.find((i) => i.id === id);
        if (!item) {
            return false;
        }
        const targets = guids.filter((g) => item.kinds.includes(this.#kindOf(g)));
        if (targets.length === 0) {
            this.app.toast(`${item.label}: nothing selected can be targeted.`, 'info');
            return true;
        }
        await this.execute({ type: 'action', actionId: id, targets, point: null, args: {}, argSpecs: item.args, pick: null });
        return true;
    }

    #key(id, e) {
        switch (id) {
            case 'palette':
                this.togglePalette();
                return true;
            case 'log':
                this.panel.toggle();
                return true;
            case 'menu':
                this.openMenuAt(this.pointerAt.x, this.pointerAt.y, this.pointerAt.clientX, this.pointerAt.clientY);
                return true;
            case 'clear':
                this.selection.clear();
                return false;
            case 'undo': {
                const last = this.log.lastUndoable();
                if (!last) {
                    return false;
                }
                this.undo(last);
                return true;
            }
            case 'follow': {
                const guid = this.selection.guids[0];
                if (!guid) {
                    return false;
                }
                this.execute({ type: 'client', name: 'follow', guid });
                return true;
            }
            case 'tele.me':
                this.execute({ type: 'action', actionId: 'tele.me', targets: [], point: this.viewport.pickWorld(this.pointerAt.x, this.pointerAt.y).point, args: {}, argSpecs: [], pick: null });
                return true;
            default:
                if (this.selection.guids.length === 0) {
                    return false;
                }
                this.#selectionAction(id);
                return true;
        }
    }
}

/** Installs the dev UI. Returns the instance (its `dispose()` removes everything). */
export function installDevUi({ app, viewport }) {
    const devui = new DevUi({ app, viewport });
    devui.start();
    return devui;
}
