// Right rail, Live tab: the connection to the world server's map feed, the subscription, and the entities on the
// current map with a filter, a detail view and follow.

import { lookAtPoint } from '../camera.js';
import { fieldsFromSchema } from '../forms.js';
import { filterEntities, splineHead } from '../live.js';
import { clear, fill, formatNumber, formatPoint, h } from '../util.js';
import { buildForm } from './formview.js';
import { button, keyValues } from './widgets.js';

const MAX_ROWS = 200;
const HOTSPOT_CELL_YARDS = 150;
const MAX_HOTSPOTS = 5;
const KIND_TEXT = { player: 'Player', bot: 'Bot', creature: 'Creature', gameobject: 'Object' };
const KIND_ORDER = { player: 0, bot: 1, creature: 2, gameobject: 3 };

function isEntityInCombat(e) {
    return Boolean(e.inCombat || e.combat || e.target || /chase|combat|attack/i.test(e.motion ?? ''));
}

export function computeHotspots(entities, cellSize = HOTSPOT_CELL_YARDS, limit = MAX_HOTSPOTS) {
    const cells = new Map();
    for (const e of entities) {
        if (!Array.isArray(e.pos) || e.pos.length < 3) {
            continue;
        }
        const cx = Math.floor(e.pos[0] / cellSize);
        const cy = Math.floor(e.pos[1] / cellSize);
        const key = `${cx}:${cy}`;
        let cell = cells.get(key);
        if (!cell) {
            cell = { key, cx, cy, count: 0, players: 0, creatures: 0, combat: 0, sumX: 0, sumY: 0, sumZ: 0 };
            cells.set(key, cell);
        }
        cell.count++;
        if (e.kind === 'player' || e.kind === 'bot') {
            cell.players++;
        } else if (e.kind === 'creature') {
            cell.creatures++;
        }
        if (isEntityInCombat(e)) {
            cell.combat++;
        }
        cell.sumX += e.pos[0];
        cell.sumY += e.pos[1];
        cell.sumZ += e.pos[2];
    }
    return [...cells.values()]
        .map((c) => ({
            ...c,
            center: [c.sumX / c.count, c.sumY / c.count, c.sumZ / c.count],
        }))
        .sort((a, b) => b.count - a.count || b.combat - a.combat || b.players - a.players)
        .slice(0, limit);
}

export class LivePanel {
    constructor(app, viewport) {
        this.app = app;
        this.viewport = viewport;
        this.active = false;
        this.filterText = '';
        this.timer = null;

        this.statusBox = h('section', { class: 'panel-section' });
        this.subscribeBox = h('section', { class: 'panel-section' });
        this.filter = h('input', {
            class: 'input',
            type: 'search',
            placeholder: 'Filter name, guid, entry, combat...',
            spellcheck: false,
            aria: { label: 'Filter entities' },
            on: {
                input: () => {
                    this.filterText = this.filter.value;
                    this.renderEntities();
                },
            },
        });
        this.summary = h('div', { class: 'muted num' });
        this.hotspotsBox = h('div', { class: 'live-hotspots', hidden: true });
        this.list = h('ul', { class: 'entity-list', role: 'listbox', aria: { label: 'Entities on this map' } });
        this.detail = h('section', { class: 'panel-section' });
        this.entityBox = h('section', { class: 'panel-section' },
            h('h3', { class: 'section-title', text: 'Entities' }),
            this.filter,
            this.summary,
            this.hotspotsBox,
            this.list);
        this.element = h('div', { class: 'panel-body live', role: 'region', aria: { label: 'Live' } }, this.statusBox, this.subscribeBox, this.detail, this.entityBox);

        app.live.on('status', () => this.renderStatus());
        app.live.on('frame', () => this.#schedule());
        app.events.on('selection', () => {
            this.renderDetail();
            this.renderEntities();
        });
        app.events.on('view', (view, changed) => {
            if (changed.has('follow') || changed.has('map')) {
                this.renderDetail();
                this.renderEntities();
            }
        });
        app.events.on('commands', () => {
            this.renderStatus();
            this.renderSubscribe();
        });
        this.renderStatus();
        this.renderSubscribe();
        this.renderDetail();
        this.renderEntities();
    }

    setActive(active) {
        this.active = active;
        if (active) {
            this.renderEntities();
            this.renderDetail();
        }
    }

    #schedule() {
        if (!this.active || this.timer !== null) {
            return;
        }
        this.timer = setTimeout(() => {
            this.timer = null;
            this.renderEntities();
            this.renderDetail();
        }, 400);
    }

    renderStatus() {
        const { app } = this;
        const s = app.live.status;
        const has = (name) => app.hasCommand(name);
        const pid = h('input', { class: 'input num', type: 'text', placeholder: 'pid (optional)', spellcheck: false, aria: { label: 'World server process id' }, attrs: { inputmode: 'numeric' } });
        const connect = async () => {
            const text = pid.value.trim();
            const args = {};
            if (text !== '') {
                const n = Number(text);
                if (!Number.isInteger(n) || n <= 0) {
                    app.toast('The process id must be a positive whole number.', 'warn');
                    pid.focus();
                    return;
                }
                args.pid = n;
            }
            const result = await app.runCommand('live.connect', args, { quiet: true });
            if (!result.ok) {
                app.toast(`live.connect: ${result.error?.message ?? 'failed'}`, 'error');
            }
            await app.runCommand('live.status', {}, { quiet: true });
        };
        const disconnect = async () => {
            await app.runCommand('live.disconnect', {}, { quiet: true });
            await app.runCommand('live.status', {}, { quiet: true });
        };
        const connectedText = s.connected
            ? `Connected to world server${s.pid !== null ? ` (pid ${s.pid})` : ''}${s.sha ? `, build ${s.sha.slice(0, 8)}` : ''}`
            : 'Not connected';
        const empty = !s.connected
            ? h('div', { class: 'empty-hint', text: s.detail || (has('live.connect')
                ? 'Turn on the map feed in the world server configuration, then press Connect. Live is off by default.'
                : 'This host has no live.connect command, so live data is not available.') })
            : null;
        fill(this.statusBox,
            h('h3', { class: 'section-title', text: 'Connection' }),
            h('div', { class: 'live-state' }, h('span', { class: `dot ${s.connected ? 'dot-ok' : 'dot-off'}`, aria: { hidden: 'true' } }), h('span', { text: connectedText })),
            empty,
            has('live.connect') || has('live.disconnect')
                ? h('div', { class: 'button-row' }, has('live.connect') ? [pid, button('Connect', connect, { primary: !s.connected })] : null,
                    has('live.disconnect') ? button('Disconnect', disconnect) : null)
                : null);
    }

    renderSubscribe() {
        const { app } = this;
        const command = app.commandNamed('live.subscribe');
        if (!command) {
            clear(this.subscribeBox);
            this.subscribeBox.hidden = true;
            return;
        }
        this.subscribeBox.hidden = false;
        const fields = fieldsFromSchema(command.schema);
        const form = buildForm({
            fields,
            initial: fields.some((f) => f.name === 'map') ? { map: String(app.view.map) } : {},
            submitLabel: 'Subscribe',
            onSubmit: async (args) => {
                form.setBusy(true);
                const result = await app.runCommand('live.subscribe', args, { quiet: true });
                form.setBusy(false);
                app.toast(result.ok ? 'Subscribed.' : `live.subscribe: ${result.error?.message ?? 'failed'}`, result.ok ? 'info' : 'error');
            },
        });
        fill(this.subscribeBox, h('h3', { class: 'section-title', text: 'Subscription' }),
            command.summary ? h('p', { class: 'muted', text: command.summary }) : null, form.element);
    }

    #entitiesOfMap() {
        const { app } = this;
        if (typeof app.live?.entities === 'function') {
            const list = app.live.entities(app.view.map);
            if (Array.isArray(list)) {
                return list;
            }
        }
        const frame = app.live.frameFor(app.view.map);
        return frame ? [...frame.entities.values()] : [];
    }

    #filterMatches(all, text) {
        const q = String(text ?? '').trim().toLowerCase();
        if (q === '') {
            return [...all];
        }
        if (q === 'combat' || q === 'in-combat' || q === 'incombat') {
            return all.filter((e) => isEntityInCombat(e) || filterEntities([e], q).length > 0);
        }
        const base = new Set(filterEntities(all, q));
        if ('combat'.includes(q)) {
            for (const e of all) {
                if (isEntityInCombat(e)) {
                    base.add(e);
                }
            }
        }
        return [...base];
    }

    renderEntities() {
        if (!this.active) {
            return;
        }
        const { app } = this;
        const all = this.#entitiesOfMap();
        if (!app.live.status.connected && all.length === 0) {
            this.summary.textContent = '';
            this.hotspotsBox.hidden = true;
            clear(this.hotspotsBox);
            fill(this.list, h('li', { class: 'empty-line', text: 'No live data. Connect above to see players, bots, creatures and objects.' }));
            return;
        }
        if (all.length === 0) {
            this.summary.textContent = '';
            this.hotspotsBox.hidden = true;
            clear(this.hotspotsBox);
            fill(this.list, h('li', { class: 'empty-line', text: `Connected, but no entities on map ${app.view.map} yet. Pick another map or wait for the next frame.` }));
            return;
        }
        const counts = { player: 0, bot: 0, creature: 0, gameobject: 0, combat: 0 };
        for (const e of all) {
            counts[e.kind] = (counts[e.kind] ?? 0) + 1;
            if (isEntityInCombat(e)) {
                counts.combat++;
            }
        }
        this.summary.textContent = `${counts.player} players · ${counts.bot} bots · ${counts.creature} creatures · ${counts.gameobject} objects · ${counts.combat} in combat`;

        const hotspots = computeHotspots(all);
        if (hotspots.length > 0) {
            this.hotspotsBox.hidden = false;
            fill(this.hotspotsBox,
                h('div', { class: 'subsection-title muted', text: `Hotspots (${HOTSPOT_CELL_YARDS} yd cells)` }),
                h('div', { class: 'hotspot-list' }, hotspots.map((spot, idx) => h('button', {
                    class: 'hotspot-badge',
                    type: 'button',
                    title: `Pan camera to (${formatPoint(spot.center, 0)}) — ${spot.count} entities (${spot.players} players, ${spot.creatures} creatures, ${spot.combat} in combat)`,
                    text: `#${idx + 1} (${Math.round(spot.center[0])}, ${Math.round(spot.center[1])}) · ${spot.count}${spot.combat > 0 ? ` ⚔${spot.combat}` : ''}`,
                    on: {
                        click: () => app.setCamera(lookAtPoint(app.view.camera, spot.center, 65), { immediate: true }),
                    },
                }))));
        } else {
            this.hotspotsBox.hidden = true;
            clear(this.hotspotsBox);
        }

        const target = app.view.camera.target;
        const matches = this.#filterMatches(all, this.filterText)
            .map((e) => ({ e, d: Math.hypot(e.pos[0] - target[0], e.pos[1] - target[1]) }))
            .sort((a, b) => KIND_ORDER[a.e.kind] - KIND_ORDER[b.e.kind] || a.d - b.d);
        const shown = matches.slice(0, MAX_ROWS);
        const selected = app.selection.entityGuid;
        const rows = shown.map(({ e, d }) => {
            const on = e.guid === selected;
            return h('li', { class: `entity-row${on ? ' is-selected' : ''}`, role: 'option', aria: { selected: on ? 'true' : 'false' } },
                h('button', {
                    class: 'entity-button',
                    type: 'button',
                    title: `${e.guid}${e.motion ? ` · ${e.motion}` : ''}`,
                    on: {
                        click: () => app.select({ entityGuid: e.guid, point: e.pos, tile: app.focusTile() }),
                        dblclick: () => app.setCamera(lookAtPoint(app.view.camera, e.pos, 35), { immediate: true }),
                    },
                },
                h('span', { class: `kind kind-${e.kind}`, text: KIND_TEXT[e.kind] }),
                h('span', { class: 'entity-name', text: e.name || (e.entry !== null ? `Entry ${e.entry}` : e.guid.slice(-8)) }),
                h('span', { class: 'entity-meta num', text: e.hp !== null ? `${Math.round(e.hp)}%` : '' }),
                h('span', { class: 'entity-meta num', text: `${Math.round(d)} yd` })));
        });
        if (matches.length === 0) {
            rows.push(h('li', { class: 'empty-line', text: 'No entity matches the filter.' }));
        } else if (matches.length > shown.length) {
            rows.push(h('li', { class: 'empty-line', text: `Showing the nearest ${shown.length} of ${matches.length}. Type in the filter to narrow the list.` }));
        }
        fill(this.list, ...rows);
    }

    renderDetail() {
        const { app } = this;
        const guid = app.selection.entityGuid;
        const found = guid ? app.live.find(guid) : null;
        if (!found) {
            clear(this.detail);
            this.detail.hidden = true;
            return;
        }
        this.detail.hidden = false;
        const e = found.entity;
        const following = app.view.follow === e.guid;
        const pairs = [
            ['Name', e.name || '-'],
            ['Kind', KIND_TEXT[e.kind]],
            ['Entry', e.entry === null ? '-' : String(e.entry)],
            ['GUID', e.guid],
            ['Position', formatPoint(e.pos, 2)],
            ['Facing', e.orientation === null ? '-' : `${formatNumber((e.orientation * 180) / Math.PI, 0)}°`],
            ['Health', e.hp === null ? '-' : `${Math.round(e.hp)}%`],
            ['Motion', e.motion || '-'],
            ['Target', e.target ?? '-'],
            ['Home', e.home ? formatPoint(e.home, 1) : '-'],
        ];
        if (e.spline) {
            const head = splineHead(e.spline, found.frame.clockMs + (performance.now() - found.frame.receivedAt));
            pairs.push(['Spline', `${e.spline.path.length} points, ${Math.round(head.fraction * 100)}% along, ${e.spline.run ? 'running' : 'walking'}`]);
        }
        if (e.route) {
            pairs.push(['Route', `${e.route.pts.length} points, next ${e.route.next}${e.route.flags.length > 0 ? `, ${e.route.flags.join(', ')}` : ''}`]);
        }
        fill(this.detail, 
            h('h3', { class: 'section-title', text: e.name || KIND_TEXT[e.kind] }),
            keyValues(pairs, 'num'),
            h('div', { class: 'button-row' },
                button(following ? 'Stop following' : 'Follow', () => app.patchView({ follow: following ? null : e.guid }, { immediate: true }), { primary: !following, title: 'Keep the camera on this entity (dragging or moving the camera stops it)' }),
                button('Centre', () => app.setCamera(lookAtPoint(app.view.camera, e.pos, 35), { immediate: true })),
                button('Use as probe point', () => app.events.emit('use-point', { slot: 0, point: e.pos }))));
    }
}

