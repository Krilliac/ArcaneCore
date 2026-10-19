// Left rail: layers grouped as the catalog groups them, each with a checkbox, colour swatch, opacity, mode, attribute
// colouring and the status of its tiles around the camera. Group checkboxes toggle every layer in the group.

import { groupLayers } from '../catalog.js';
import { LAYER_MODES, MAX_LOAD_RADIUS } from '../view.js';
import { fill, formatCount, h } from '../util.js';
import { checkbox } from './widgets.js';

const MODE_TEXT = { wire: 'Wire', fill: 'Fill', both: 'Fill + wire', xray: 'X-ray' };

/** "9 ok, 2 missing, 1 rejected" from a tile count record; '' when nothing is known yet. */
export function tileCountText(counts) {
    if (!counts || counts.total === 0) {
        return '';
    }
    const parts = [];
    if (counts.Ok > 0) {
        parts.push(`${counts.Ok} ok`);
    }
    if (counts.Empty > 0) {
        parts.push(`${counts.Empty} empty`);
    }
    if (counts.Missing > 0) {
        parts.push(`${counts.Missing} missing`);
    }
    if (counts.Rejected > 0) {
        parts.push(`${counts.Rejected} rejected`);
    }
    if (counts.Error > 0) {
        parts.push(`${counts.Error} failed`);
    }
    if (counts.Unknown > 0) {
        parts.push(`${counts.Unknown} pending`);
    }
    return parts.join(' · ');
}

export class LayersRail {
    constructor(app) {
        this.app = app;
        this.expanded = new Set();
        this.collapsedGroups = new Set(['Diff']);
        this.showUnavailable = new Set();
        this.groups = [];
        this.statusTimer = null;

        this.radius = h('select', {
            class: 'select select-small',
            aria: { label: 'Tiles loaded around the camera' },
            title: 'How many tiles around the camera are loaded. More tiles show more of the map and cost memory.',
            on: { change: () => app.patchView({ loadRadius: Number(this.radius.value) }, { immediate: true }) },
        }, Array.from({ length: MAX_LOAD_RADIUS + 1 }, (_, r) => h('option', { value: String(r), text: r === 0 ? '1 tile' : `${r} around (${2 * r + 1} × ${2 * r + 1})` })));
        this.body = h('div', { class: 'rail-body' });
        this.element = h('aside', { class: 'rail rail-left', aria: { label: 'Layers' } },
            h('div', { class: 'rail-head' },
                h('h2', { class: 'rail-title', text: 'Layers' }),
                h('label', { class: 'rail-extra' }, h('span', { text: 'Load' }), this.radius)),
            this.body);

        app.events.on('layers', () => this.rebuild());
        app.events.on('view', () => this.update());
        app.events.on('tiles', () => this.#scheduleStatus());
        app.live.on('status', () => this.updateStatus());
        this.rebuild();
    }

    #scheduleStatus() {
        if (this.statusTimer === null) {
            this.statusTimer = setTimeout(() => {
                this.statusTimer = null;
                this.updateStatus();
            }, 250);
        }
    }

    rebuild() {
        const { app } = this;
        this.groups = groupLayers(app.layers).map(({ group, layers }) => this.#buildGroup(group, layers));
        fill(this.body, ...this.groups.map((g) => g.element));
        this.update();
    }

    #buildGroup(group, layers) {
        const { app } = this;
        const tri = checkbox({
            label: group.title,
            title: `Show or hide every ${group.title.toLowerCase()} layer`,
            className: 'group-check',
            onChange: (checked) => app.setGroupVisible(layers.filter((l) => l.available !== false).map((l) => l.id), checked),
        });
        const count = h('span', { class: 'group-count num' });
        const listId = `layers-${group.id}`;
        const toggle = h('button', {
            class: 'disclosure',
            type: 'button',
            aria: { expanded: 'true', controls: listId, label: `${group.title} layers` },
            text: '▾',
            on: {
                click: () => {
                    if (this.collapsedGroups.has(group.id)) {
                        this.collapsedGroups.delete(group.id);
                    } else {
                        this.collapsedGroups.add(group.id);
                    }
                    this.update();
                },
            },
        });
        const rows = layers.map((info) => this.#buildRow(info));
        const usable = rows.filter((r) => r.info.available !== false);
        const missing = rows.filter((r) => r.info.available === false);
        const missingId = `${listId}-nosource`;
        const missingList = h('ul', { class: 'layer-list layer-list-nosource', id: missingId, role: 'list' }, missing.map((r) => r.element));
        const missingToggle = missing.length > 0
            ? h('button', {
                class: 'nosource-toggle',
                type: 'button',
                aria: { expanded: 'false', controls: missingId },
                on: {
                    click: () => {
                        if (this.showUnavailable.has(group.id)) {
                            this.showUnavailable.delete(group.id);
                        } else {
                            this.showUnavailable.add(group.id);
                        }
                        this.update();
                    },
                },
            })
            : null;
        const noSourceNote = missing.length > 0
            ? h('li', { class: 'nosource-note', text: 'The host has no source for these layers. Start arcane-atlas serve with --data for server layers or --client for reference layers.' })
            : null;
        missingList.prepend(...(noSourceNote ? [noSourceNote] : []));
        const list = h('ul', { class: 'layer-list', id: listId, role: 'list' }, usable.map((r) => r.element));
        const element = h('section', { class: 'layer-group' }, h('div', { class: 'group-head' }, toggle, tri.element, count), list,
            missingToggle ? h('div', { class: 'nosource' }, missingToggle, missingList) : null);
        return { group, layers, tri, count, toggle, list, rows, element, missing, missingToggle, missingList };
    }

    #buildRow(info) {
        const { app } = this;
        const id = info.id;
        const box = h('input', { type: 'checkbox', aria: { label: info.title }, on: { change: () => app.setLayer(id, { visible: box.checked }, { immediate: true }) } });
        const swatch = h('input', {
            class: 'swatch',
            type: 'color',
            title: `Colour of ${info.title}`,
            aria: { label: `Colour of ${info.title}` },
            on: {
                input: () => app.setLayer(id, { color: swatch.value.toUpperCase() }),
                change: () => app.setLayer(id, { color: swatch.value.toUpperCase() }, { immediate: true }),
            },
        });
        const more = h('button', {
            class: 'more',
            type: 'button',
            text: '⋯',
            title: `Opacity and mode of ${info.title}`,
            aria: { expanded: 'false', label: `Opacity and mode of ${info.title}` },
            on: {
                click: () => {
                    if (this.expanded.has(id)) {
                        this.expanded.delete(id);
                    } else {
                        this.expanded.add(id);
                    }
                    this.update();
                },
            },
        });
        const title = h('label', { class: 'layer-title', title: info.description || info.title }, box, h('span', { text: info.title }));
        const status = h('div', { class: 'layer-status num' });
        const opacity = h('input', {
            type: 'range',
            min: 0,
            max: 100,
            step: 5,
            aria: { label: `Opacity of ${info.title}` },
            on: {
                input: () => app.setLayer(id, { opacity: Number(opacity.value) / 100 }),
                change: () => app.setLayer(id, { opacity: Number(opacity.value) / 100 }, { immediate: true }),
            },
        });
        const opacityText = h('span', { class: 'opacity-text num' });
        const mode = h('select', {
            class: 'select select-small',
            aria: { label: `Draw mode of ${info.title}` },
            on: { change: () => app.setLayer(id, { mode: mode.value }, { immediate: true }) },
        }, LAYER_MODES.map((m) => h('option', { value: m, text: MODE_TEXT[m] })));
        const hasAttributes = Object.keys(info.attributeColors ?? {}).length > 0;
        const byAttribute = hasAttributes
            ? checkbox({ label: 'Colour by attribute', onChange: (checked) => app.setLayer(id, { colorByAttribute: checked }, { immediate: true }) })
            : null;
        const controls = h('div', { class: 'layer-controls' },
            h('label', { class: 'control-line' }, h('span', { text: 'Opacity' }), opacity, opacityText),
            h('label', { class: 'control-line' }, h('span', { text: 'Mode' }), mode),
            byAttribute?.element);
        const element = h('li', { class: 'layer-row', data: { layer: id } },
            h('div', { class: 'layer-main' }, title, swatch, more), status, controls);
        return { info, element, box, swatch, more, status, controls, opacity, opacityText, mode, byAttribute };
    }

    /** Reflects the view (checks, colours, modes) and the group tri-states. */
    update() {
        const { app } = this;
        const resolved = new Map(app.resolved().map((r) => [r.id, r]));
        this.radius.value = String(app.view.loadRadius);
        for (const g of this.groups) {
            let on = 0;
            let usable = 0;
            const collapsed = this.collapsedGroups.has(g.group.id);
            g.toggle.textContent = collapsed ? '▸' : '▾';
            g.toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
            g.list.hidden = collapsed;
            if (g.missingToggle) {
                const open = this.showUnavailable.has(g.group.id);
                g.missingToggle.textContent = `${open ? '▾' : '▸'} ${g.missing.length} without a source`;
                g.missingToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
                g.missingList.hidden = !open;
                g.missingToggle.parentElement.hidden = collapsed;
            }
            for (const row of g.rows) {
                const r = resolved.get(row.info.id);
                if (!r) {
                    continue;
                }
                const available = row.info.available !== false;
                if (available) {
                    usable++;
                    if (r.style.visible) {
                        on++;
                    }
                }
                row.box.checked = r.style.visible;
                row.box.disabled = !available;
                row.swatch.value = r.style.color.toLowerCase();
                row.opacity.value = String(Math.round(r.style.opacity * 100));
                row.opacityText.textContent = `${Math.round(r.style.opacity * 100)}%`;
                row.mode.value = r.style.mode;
                row.byAttribute?.set(r.style.colorByAttribute);
                const open = this.expanded.has(row.info.id);
                row.controls.hidden = !open;
                row.more.setAttribute('aria-expanded', open ? 'true' : 'false');
                row.element.classList.toggle('is-off', !r.style.visible);
                row.element.classList.toggle('is-unavailable', !available);
                row.element.classList.toggle('is-hidden-by-mode', r.hiddenByMode);
                row.element.title = available ? '' : 'The host has no source for this layer. Start arcane-atlas serve with --data or --client.';
            }
            g.count.textContent = `${on}/${usable}`;
            g.tri.set(usable === 0 ? false : on === usable ? true : on === 0 ? false : 'mixed');
            g.tri.input.disabled = usable === 0;
        }
        this.updateStatus();
    }

    /** Tile counts and hints under each ticked layer. */
    updateStatus() {
        const { app } = this;
        const scene = app.scene;
        const resolved = new Map(app.resolved().map((r) => [r.id, r]));
        for (const g of this.groups) {
            for (const row of g.rows) {
                const r = resolved.get(row.info.id);
                let text = '';
                let kind = '';
                if (!r) {
                    text = '';
                } else if (row.info.available === false) {
                    text = '';
                } else if (r.hiddenByMode) {
                    text = 'Not shown in this compare mode';
                    kind = 'muted';
                } else if (r.visible && r.info.tiled !== false && scene) {
                    const counts = scene.countsFor(row.info.id);
                    text = tileCountText(counts);
                    if (counts.Rejected > 0 || counts.Error > 0) {
                        kind = 'error';
                    } else if (counts.Missing > 0 && counts.Ok === 0) {
                        kind = 'warn';
                    }
                    const chunks = scene.chunksFor(row.info.id);
                    if (chunks.length > 0) {
                        const tris = chunks.reduce((n, c) => n + c.triangleCount, 0);
                        const lines = chunks.reduce((n, c) => n + c.lineCount, 0);
                        text += `${text ? ' · ' : ''}${tris > 0 ? `${formatCount(tris)} tris` : `${formatCount(lines)} lines`}`;
                    }
                } else if (r.visible && r.live) {
                    text = app.live.status.connected ? '' : 'Live is not connected';
                    kind = app.live.status.connected ? '' : 'muted';
                }
                row.status.textContent = text;
                row.status.hidden = text === '';
                row.status.className = `layer-status num${kind ? ` is-${kind}` : ''}`;
            }
        }
    }
}
