// Probe overlays (design doc section 14). Any command result may carry
//   "overlay": {"points":[{at, label?, color?, colorKey?}], "segments":[{from, to, label?, color?, dashed?}],
//               "polylines":[{pts, label?, color?, dashed?}], "boxes":[{min, max, label?, color?}]}
// in world coordinates. The latest overlay per command name is kept, drawn as thick lines over everything, and listed
// in the Probes tab where each can be toggled.

import { LineBuilder, MarkerBuilder, SHAPES } from './geom.js';
import { packColor } from './colors.js';
import { Emitter, isFiniteNumber } from './util.js';

const isPoint = (p) => Array.isArray(p) && p.length === 3 && p.every(isFiniteNumber);
const hex = (c) => (typeof c === 'string' && /^#[0-9a-fA-F]{6}$/.test(c) ? c.toUpperCase() : undefined);
const text = (s) => (typeof s === 'string' && s !== '' ? s.slice(0, 200) : undefined);

/** Overlay JSON -> clean arrays; malformed entries are dropped and counted so the UI can say so. */
export function normalizeOverlay(raw) {
    const out = { points: [], segments: [], polylines: [], boxes: [], dropped: 0 };
    if (raw === null || typeof raw !== 'object') {
        return out;
    }
    const collect = (list, target, accept) => {
        for (const item of Array.isArray(list) ? list : []) {
            const shape = item !== null && typeof item === 'object' ? accept(item) : null;
            if (!shape) {
                out.dropped++;
                continue;
            }
            target.push({
                ...shape,
                label: text(item.label),
                color: hex(item.color),
                dashed: item.dashed === true,
                colorKey: typeof item.colorKey === 'string' ? item.colorKey : undefined,
            });
        }
    };
    collect(raw.points, out.points, (i) => (isPoint(i.at) ? { at: [...i.at] } : null));
    collect(raw.segments, out.segments, (i) => (isPoint(i.from) && isPoint(i.to) ? { from: [...i.from], to: [...i.to] } : null));
    collect(raw.polylines, out.polylines, (i) => (Array.isArray(i.pts) && i.pts.length >= 2 && i.pts.every(isPoint) ? { pts: i.pts.map((p) => [...p]) } : null));
    collect(raw.boxes, out.boxes, (i) => (isPoint(i.min) && isPoint(i.max) ? { min: [...i.min], max: [...i.max] } : null));
    return out;
}

export const overlayIsEmpty = (o) => o.points.length + o.segments.length + o.polylines.length + o.boxes.length === 0;

/** Default colour by who answered: reference commands (`ref.` prefix) in reference amber, server commands in navmesh green. */
export function baseColorFor(commandName) {
    return commandName.startsWith('ref.') ? '#FFD34D' : '#3CCB9A';
}

/**
 * Colour-key table for overlay items: every layer's attributeColors, later layers overriding earlier ones.
 */
export function colorKeyTable(layers) {
    const table = {};
    for (const l of layers) {
        Object.assign(table, l.attributeColors ?? {});
    }
    return table;
}

/**
 * Geometry for drawing one overlay: {solid, dashed, markers, labels, bounds}. `solid` and `dashed` are LineBuilder
 * outputs (null when empty); `labels` carry the world position and text for the 2D layer.
 */
export function buildOverlayGeometry(commandName, overlay, keyTable = {}) {
    const base = baseColorFor(commandName);
    const colorOf = (item) => item.color ?? hex(keyTable[item.colorKey]) ?? base;
    const solid = new LineBuilder();
    const dashed = new LineBuilder();
    const markers = new MarkerBuilder();
    const labels = [];
    const lo = [Infinity, Infinity, Infinity];
    const hi = [-Infinity, -Infinity, -Infinity];
    const grow = (p) => {
        for (let a = 0; a < 3; a++) {
            lo[a] = Math.min(lo[a], p[a]);
            hi[a] = Math.max(hi[a], p[a]);
        }
    };
    const label = (p, item, rgba) => {
        if (item.label) {
            labels.push({ at: p, text: item.label, color: rgba });
        }
    };

    for (const s of overlay.segments) {
        const c = packColor(colorOf(s));
        (s.dashed ? dashed : solid).add(s.from, s.to, c);
        grow(s.from);
        grow(s.to);
        label([(s.from[0] + s.to[0]) / 2, (s.from[1] + s.to[1]) / 2, (s.from[2] + s.to[2]) / 2], s, c);
    }
    for (const line of overlay.polylines) {
        const c = packColor(colorOf(line));
        (line.dashed ? dashed : solid).addPolyline(line.pts, c);
        line.pts.forEach(grow);
        label(line.pts[Math.floor(line.pts.length / 2)], line, c);
    }
    for (const box of overlay.boxes) {
        const c = packColor(colorOf(box));
        (box.dashed ? dashed : solid).addBox(box.min, box.max, c);
        grow(box.min);
        grow(box.max);
        label([(box.min[0] + box.max[0]) / 2, (box.min[1] + box.max[1]) / 2, box.max[2]], box, c);
    }
    for (const p of overlay.points) {
        const c = packColor(colorOf(p));
        markers.add(p.at, c, 11, SHAPES.diamond);
        grow(p.at);
        label(p.at, p, c);
    }
    const any = lo[0] !== Infinity;
    return {
        solid: solid.count > 0 ? solid.build() : null,
        dashed: dashed.count > 0 ? dashed.build() : null,
        markers: markers.count > 0 ? markers.build() : null,
        labels,
        bounds: any ? { min: lo, max: hi } : null,
    };
}

/** The latest overlay per command name, each toggleable. */
export class OverlayStore extends Emitter {
    #entries = new Map();
    #version = 0;

    get version() {
        return this.#version;
    }

    entries() {
        return [...this.#entries.values()];
    }

    get(name) {
        return this.#entries.get(name);
    }

    /** Takes a command result's data; stores its overlay (replacing the previous one for that command). Returns the entry or null. */
    ingest(commandName, data, keyTable) {
        if (data === null || typeof data !== 'object' || data.overlay === undefined) {
            return null;
        }
        const overlay = normalizeOverlay(data.overlay);
        const previous = this.#entries.get(commandName);
        const entry = {
            name: commandName,
            visible: previous?.visible ?? true,
            overlay,
            geometry: buildOverlayGeometry(commandName, overlay, keyTable),
            counts: {
                points: overlay.points.length,
                segments: overlay.segments.length,
                polylines: overlay.polylines.length,
                boxes: overlay.boxes.length,
                dropped: overlay.dropped,
            },
            receivedAt: Date.now(),
            version: ++this.#version,
            gpu: null,
        };
        this.#entries.set(commandName, entry);
        if (previous) {
            this.emit('remove', previous);
        }
        this.emit('change', entry);
        return entry;
    }

    setVisible(name, visible) {
        const e = this.#entries.get(name);
        if (e && e.visible !== visible) {
            e.visible = visible;
            this.emit('change', e);
        }
    }

    remove(name) {
        const e = this.#entries.get(name);
        if (e && this.#entries.delete(name)) {
            this.emit('remove', e);
            this.emit('change', null);
        }
    }

    clear() {
        const all = [...this.#entries.values()];
        this.#entries.clear();
        for (const e of all) {
            this.emit('remove', e);
        }
        this.emit('change', null);
    }
}
