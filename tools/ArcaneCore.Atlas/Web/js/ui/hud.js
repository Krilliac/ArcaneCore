// Viewport furniture: crosshair, compass, scale bar, cursor readout, notices, split captions and the draggable divider.
// All plain DOM; the GL canvas and label canvas sit underneath.

import { FILE_KINDS } from '../notices.js';
import { describePosition } from '../tiles.js';
import { clamp, formatNumber, h, niceFloor } from '../util.js';
import { yardsPerPixel } from '../camera.js';

const STATE_ORDER = ['Ok', 'Rejected', 'Error', 'Empty', 'Missing', 'Unknown'];

/** The most informative state among the layers that read one kind of file. */
export function fileState(kind, tile, statusOf) {
    const info = FILE_KINDS.find((k) => k.kind === kind);
    const found = new Set();
    for (const layer of info.layers) {
        const s = statusOf(layer, tile.x, tile.y);
        if (s) {
            found.add(s.state);
        }
    }
    return STATE_ORDER.find((s) => found.has(s)) ?? 'Unknown';
}

const STATE_TEXT = { Ok: 'present', Empty: 'empty', Missing: 'missing', Rejected: 'rejected', Error: 'not loaded', Unknown: 'not checked' };

export class Hud {
    constructor(root, { onCompass, onDivider }) {
        this.root = root;
        this.crosshair = h('div', { class: 'hud-crosshair', aria: { hidden: 'true' } });
        this.rose = h('div', { class: 'compass-rose' }, ...['n', 'e', 's', 'w'].map((c) => h('span', { class: `compass-${c}`, text: c.toUpperCase() })));
        this.compass = h('button', { class: 'hud-compass', type: 'button', title: 'North is up when the needle points up. Click to face north.', aria: { label: 'Compass. Click to face north.' }, on: { click: () => onCompass() } }, this.rose);
        this.scaleBar = h('div', { class: 'scale-bar' });
        this.scaleLabel = h('span', { class: 'scale-label', text: '' });
        this.scale = h('div', { class: 'hud-scale', aria: { hidden: 'true' } }, this.scaleBar, this.scaleLabel);

        this.rx = h('span', { class: 'num', text: '-' });
        this.ry = h('span', { class: 'num', text: '-' });
        this.rz = h('span', { class: 'num', text: '-' });
        this.rEstimate = h('span', { class: 'readout-note', text: '' });
        this.rTile = h('div', { class: 'readout-line' });
        this.rSurface = h('div', { class: 'readout-line readout-surface' });
        this.rFiles = h('div', { class: 'readout-files' });
        this.readout = h('div', { class: 'hud-readout', role: 'status', aria: { label: 'Cursor readout' } },
            h('div', { class: 'readout-coords' }, h('span', { class: 'axis', text: 'x' }), this.rx, h('span', { class: 'axis', text: 'y' }), this.ry, h('span', { class: 'axis', text: 'z' }), this.rz, this.rEstimate),
            this.rTile, this.rSurface, this.rFiles);

        this.notices = h('div', { class: 'hud-notices', role: 'region', aria: { label: 'Viewport notices', live: 'polite' } });
        this.captionServer = h('span', { class: 'split-caption split-left', text: 'Server' });
        this.captionReference = h('span', { class: 'split-caption split-right', text: 'Reference' });
        this.divider = h('div', {
            class: 'hud-divider',
            role: 'separator',
            tabindex: 0,
            aria: { orientation: 'vertical', label: 'Split position: server on the left, reference on the right. Use the arrow keys to move it.', valuemin: 5, valuemax: 95, valuenow: 50 },
        });
        this.#wireDivider(onDivider);
        root.append(this.crosshair, this.scale, this.compass, this.readout, this.notices, this.captionServer, this.captionReference, this.divider);
        this.compareKey = '';
    }

    #wireDivider(onDivider) {
        let dragging = null;
        const fractionAt = (e) => {
            const rect = this.root.getBoundingClientRect();
            return clamp((e.clientX - rect.left) / rect.width, 0.05, 0.95);
        };
        this.divider.addEventListener('pointerdown', (e) => {
            e.preventDefault();
            this.divider.setPointerCapture(e.pointerId);
            dragging = e.pointerId;
            this.divider.focus();
        });
        this.divider.addEventListener('pointermove', (e) => {
            if (dragging === e.pointerId) {
                onDivider(fractionAt(e), false);
            }
        });
        const end = (e) => {
            if (dragging === e.pointerId) {
                dragging = null;
                onDivider(fractionAt(e), true);
            }
        };
        this.divider.addEventListener('pointerup', end);
        this.divider.addEventListener('pointercancel', end);
        this.divider.addEventListener('keydown', (e) => {
            const step = e.shiftKey ? 0.1 : 0.02;
            const now = Number(this.divider.getAttribute('aria-valuenow')) / 100;
            if (e.key === 'ArrowLeft') {
                e.preventDefault();
                onDivider(clamp(now - step, 0.05, 0.95), true);
            } else if (e.key === 'ArrowRight') {
                e.preventDefault();
                onDivider(clamp(now + step, 0.05, 0.95), true);
            }
        });
    }

    /** Compass needle, scale bar and visibility from the view. `depth` is the surface distance for fly mode. */
    updateView(view, cssWidth, cssHeight, depth) {
        const cam = view.camera;
        this.rose.style.transform = `rotate(${cam.yaw.toFixed(2)}deg)`;
        this.compass.setAttribute('aria-label', `Compass. Facing ${headingName(cam.yaw)}. Click to face north.`);
        const ypp = yardsPerPixel(cam, Math.max(cssHeight, 1), cam.mode === 'fly' ? depth : cam.distance);
        const length = niceFloor(ypp * 110);
        this.scaleBar.style.width = `${Math.max(8, length / ypp).toFixed(1)}px`;
        this.scaleLabel.textContent = `${cam.mode === 'fly' ? '~' : ''}${length >= 1000 ? `${(length / 1000).toString()} kyd` : `${length} yd`}`;
        const hud = view.hud !== false;
        for (const el of [this.crosshair, this.scale, this.compass, this.readout]) {
            el.hidden = !hud;
        }
        const split = view.compare === 'split';
        this.divider.hidden = !split;
        this.captionServer.hidden = !split;
        this.captionReference.hidden = !split;
        if (split) {
            const pct = clamp(view.split * 100, 5, 95);
            this.divider.style.left = `${pct}%`;
            this.divider.setAttribute('aria-valuenow', String(Math.round(pct)));
            this.captionServer.style.left = '12px';
            this.captionReference.style.left = `calc(${pct}% + 12px)`;
        }
        this.crosshair.classList.toggle('is-fly', cam.mode === 'fly');
    }

    /** Cursor readout. `hover` is {point, estimated, surface} or null; statusOf feeds the file availability marks. */
    updateReadout(hover, map, statusOf) {
        if (!hover) {
            for (const n of [this.rx, this.ry, this.rz]) {
                n.textContent = '-';
            }
            this.rEstimate.textContent = '';
            this.rTile.textContent = 'Move the cursor over the map.';
            this.rSurface.textContent = '';
            this.rFiles.replaceChildren();
            return;
        }
        const [x, y, z] = hover.point;
        this.rx.textContent = formatNumber(x, 2);
        this.ry.textContent = formatNumber(y, 2);
        this.rz.textContent = formatNumber(z, 2);
        this.rEstimate.textContent = hover.estimated ? 'on the target plane (no surface under the cursor)' : '';
        const d = describePosition(map, x, y);
        if (!d.tile) {
            this.rTile.textContent = `Map ${map} · outside the 64 × 64 tile grid`;
            this.rFiles.replaceChildren();
        } else {
            this.rTile.textContent = `Map ${map} · tile ${d.tile.x}, ${d.tile.y} · cell ${d.cell.x}, ${d.cell.y} · square ${d.square.x}, ${d.square.y}`;
            this.rFiles.replaceChildren(
                this.#file('.map', d.files.map, fileState('map', d.tile, statusOf)),
                this.#file('.vmtile', d.files.vmap, fileState('vmap', d.tile, statusOf)),
                this.#file('.mmtile', d.files.mmap, fileState('mmap', d.tile, statusOf)),
            );
        }
        this.rSurface.textContent = hover.surface ? hover.surface : hover.estimated ? '' : 'No surface under the cursor';
    }

    #file(ext, name, state) {
        return h('span', { class: `file-chip file-${state.toLowerCase()}`, title: `${name}: ${STATE_TEXT[state]}` },
            h('span', { class: 'file-dot', aria: { hidden: 'true' } }), h('span', { class: 'file-name', text: name }), h('span', { class: 'sr-only', text: ` ${ext} ${STATE_TEXT[state]}` }));
    }

    setNotices(list) {
        const key = JSON.stringify(list.map((n) => [n.id, n.text]));
        if (key === this._noticeKey) {
            return;
        }
        this._noticeKey = key;
        this.notices.replaceChildren(...list.slice(0, 4).map((n) => h('div', { class: `notice notice-${n.level}` },
            h('div', { class: 'notice-text', text: n.text }), n.hint ? h('div', { class: 'notice-hint', text: n.hint }) : null)));
        this.notices.hidden = list.length === 0;
    }
}

const HEADINGS = ['north', 'north-east', 'east', 'south-east', 'south', 'south-west', 'west', 'north-west'];

/** Compass heading for a yaw (0 = +X = north, 90 = +Y = west): north is 0, east is -90 (i.e. yaw 270). */
export function headingName(yaw) {
    const compassDegrees = ((-yaw % 360) + 360) % 360;
    return HEADINGS[Math.round(compassDegrees / 45) % 8];
}
