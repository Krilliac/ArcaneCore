// Right rail, Inspector tab: what the host says is under the clicked point (query.pick), where it sits in the tile
// grid, which files back it, and the state of each visible layer's tile there.

import { lookAtPoint } from '../camera.js';
import { layerFile } from '../notices.js';
import { flatten } from '../results.js';
import { describePosition } from '../tiles.js';
import { fill, formatNumber, formatPoint, h } from '../util.js';
import { fileState } from './hud.js';
import { button, copyText, emptyState, keyValues } from './widgets.js';

const STATE_TEXT = {
    Ok: 'loaded',
    Empty: 'empty tile (no primitives)',
    Missing: 'no file for this tile',
    Rejected: 'rejected by the reader',
    Error: 'could not be loaded',
    Unknown: 'not checked yet',
    NotApplicable: 'does not apply here',
};

export class Inspector {
    constructor(app, viewport) {
        this.app = app;
        this.viewport = viewport;
        this.element = h('div', { class: 'panel-body inspector', role: 'region', aria: { label: 'Inspector' } });
        this.tileSection = null;
        this.timer = null;
        app.events.on('selection', () => this.render());
        app.events.on('tiles', () => this.#scheduleTiles());
        app.events.on('layers', () => this.render());
        this.render();
    }

    #scheduleTiles() {
        if (this.timer === null) {
            this.timer = setTimeout(() => {
                this.timer = null;
                this.#renderTiles();
            }, 300);
        }
    }

    render() {
        const { app } = this;
        const sel = app.selection;
        if (!sel.point) {
            fill(this.element, emptyState(
                'Nothing selected',
                'Click the map to ask the host what is under that point. Double-click to centre the camera there.',
            ));
            this.tileSection = null;
            return;
        }
        const [x, y, z] = sel.point;
        const d = describePosition(app.view.map, x, y);
        const copy = button('Copy x, y, z', async () => {
            const ok = await copyText(`${x.toFixed(3)}, ${y.toFixed(3)}, ${z.toFixed(3)}`);
            app.toast(ok ? 'Copied the coordinates.' : 'The browser refused to copy.', ok ? 'info' : 'warn');
        });
        const frame = button('Centre here', () => app.setCamera(lookAtPoint(app.view.camera, sel.point, Math.max(Math.min(app.view.camera.distance, 60), 8)), { immediate: true }));
        const useA = button('Use as probe point A', () => app.events.emit('use-point', { slot: 0, point: sel.point }), { title: 'Fill the first point field of the probe forms' });
        const useB = button('Use as probe point B', () => app.events.emit('use-point', { slot: 1, point: sel.point }), { title: 'Fill the second point field of the probe forms' });

        const position = keyValues([
            ['x', formatNumber(x, 3)],
            ['y', formatNumber(y, 3)],
            ['z', formatNumber(z, 3)],
            ['Map', String(app.view.map)],
            ['Tile', d.tile ? `${d.tile.x}, ${d.tile.y}` : 'outside the 64 × 64 grid'],
            ['Cell', d.cell ? `${d.cell.x}, ${d.cell.y}` : '-'],
            ['Square', d.square ? `${d.square.x}, ${d.square.y}` : '-'],
        ], 'num');

        this.tileSection = h('section', { class: 'panel-section' });
        fill(this.element, 
            h('section', { class: 'panel-section' }, h('h3', { class: 'section-title', text: 'Point' }), position, h('div', { class: 'button-row' }, copy, frame, useA, useB)),
            this.#hitSection(sel),
            this.tileSection);
        this.#renderTiles();
    }

    #hitSection(sel) {
        const { app } = this;
        const section = h('section', { class: 'panel-section' }, h('h3', { class: 'section-title', text: 'Surface under the click' }));
        if (sel.pending) {
            section.append(h('p', { class: 'muted', text: 'Asking the host.' }));
            return section;
        }
        const hit = sel.hit;
        if (!hit) {
            section.append(h('p', { class: 'muted', text: sel.note || 'The host found nothing there.' }));
            return section;
        }
        const info = app.layerInfo(hit.layer);
        section.append(keyValues([
            ['Layer', info ? `${info.title} (${hit.layer})` : hit.layer],
            ['Primitive', hit.primitive || '-'],
            ['Attribute', hit.attribute === null ? '-' : String(hit.attribute)],
            ['Distance', hit.distance === null ? '-' : `${formatNumber(hit.distance, 2)} yd`],
            ['Hit point', formatPoint(hit.point, 3)],
            ['Tile', hit.tile ? `${hit.tile.x}, ${hit.tile.y} (map ${hit.tile.map})` : '-'],
        ], 'num'));
        const rows = flatten(hit.details, { skip: [] });
        if (rows.length > 0) {
            const table = h('dl', { class: 'kv kv-tree num' });
            for (const row of rows) {
                table.append(h('dt', { class: 'kv-key', style: { paddingLeft: `${row.depth * 12}px` }, text: row.key }), h('dd', { text: row.text ?? '' }));
            }
            section.append(h('h4', { class: 'subsection-title', text: 'Details from the host' }), table);
        }
        if (sel.all && sel.all.length > 1) {
            section.append(h('h4', { class: 'subsection-title', text: 'Also along the ray' }),
                h('ul', { class: 'plain-list' }, sel.all.slice(1, 8).map((a) => h('li', { class: 'num', text: `${a.layer} at ${formatNumber(a.distance, 1)} yd` }))));
        }
        return section;
    }

    #renderTiles() {
        if (!this.tileSection) {
            return;
        }
        const { app } = this;
        const tile = app.selection.tile;
        const section = this.tileSection;
        fill(section, h('h3', { class: 'section-title', text: 'Tile status' }));
        if (!tile || !Number.isInteger(tile.x)) {
            section.append(h('p', { class: 'muted', text: 'The point is outside the tile grid.' }));
            return;
        }
        const scene = app.scene;
        const list = h('ul', { class: 'status-list' });
        const resolved = app.resolved().filter((l) => l.visible && !l.live && l.info.tiled !== false);
        for (const layer of resolved) {
            const s = scene?.statusOf(layer.id, tile.x, tile.y) ?? null;
            const state = s?.state ?? 'Unknown';
            const file = layerFile(layer.id, app.view.map, tile.x, tile.y);
            list.append(h('li', { class: `status-row state-${state.toLowerCase()}` },
                h('span', { class: 'status-layer', text: layer.info.title }),
                h('span', { class: 'status-state', text: STATE_TEXT[state] ?? state }),
                file ? h('span', { class: 'status-file num', text: file.name }) : null,
                s?.detail ? h('span', { class: 'status-detail', text: s.detail }) : null));
        }
        if (resolved.length === 0) {
            section.append(h('p', { class: 'muted', text: 'No tiled layers are visible. Tick a layer to see its tile state here.' }));
        } else {
            section.append(list);
        }
        const x = app.selection.point[0];
        const y = app.selection.point[1];
        const files = describePosition(app.view.map, x, y).files;
        if (files) {
            const statusOf = (layer, tx, ty) => scene?.statusOf(layer, tx, ty) ?? null;
            const chip = (name, kind) => h('span', { class: `file-chip file-${fileState(kind, tile, statusOf).toLowerCase()}`, title: name },
                h('span', { class: 'file-dot', aria: { hidden: 'true' } }), h('span', { class: 'file-name num', text: name }));
            section.append(h('div', { class: 'readout-files' }, chip(files.map, 'map'), chip(files.vmap, 'vmap'), chip(files.mmap, 'mmap')));
        }
    }
}
