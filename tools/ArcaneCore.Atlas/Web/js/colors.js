// Colour resolution for geometry and entities.
// A chunk's attribute table entry may carry a reserved `colorKey`; the layer's attributeColors (layers.list) maps keys
// to colours. A layer style with colorByAttribute colours each primitive that way; everything else uses the style colour.
// A colorKey that is itself "#RRGGBB" is used as that colour (so a source can colour by value, for example a ramp).

import { clamp, packRgba, parseHexColor } from './util.js';

/** Colour for one attribute table entry: [r, g, b] 0..255, or null when it has no colour of its own. */
export function resolveColorKey(entry, attributeColors) {
    const key = entry?.colorKey;
    if (typeof key !== 'string' || key === '') {
        return null;
    }
    const mapped = attributeColors?.[key];
    return parseHexColor(mapped) ?? parseHexColor(key);
}

/** Colour of a layer style, [r, g, b] 0..255; the neutral grey when the string is not a colour. */
export function styleRgb(color) {
    return parseHexColor(color) ?? [147, 161, 170];
}

/** Float rgb 0..1. */
export const styleRgbF = (color) => styleRgb(color).map((v) => v / 255);

/**
 * Builds the lookup table a chunk's shaders read. Returns {paletteOf, attributeOf, size, rows, rgba}: `paletteOf(attributeId)`
 * gives the palette index and `attributeOf(index)` the attribute id back; `rgba` is size * 4 bytes padded to whole 256-wide rows (alpha 0 = no colour of its own, use the layer's).
 * Attribute ids below `directLimit` index the palette directly; larger ids are numbered in order of first appearance.
 */
export function buildPalette(attributeIdArrays, attributes, attributeColors, directLimit = 4096) {
    let maxId = -1;
    for (const array of attributeIdArrays) {
        for (let i = 0; i < array.length; i++) {
            if (array[i] > maxId) {
                maxId = array[i];
            }
        }
    }
    let paletteOf;
    let attributeOf;
    let ids;
    if (maxId < directLimit) {
        const size = Math.max(maxId + 1, 1);
        ids = Array.from({ length: size }, (_, i) => i);
        paletteOf = (id) => id;
        attributeOf = (index) => index;
    } else {
        // Large ids: renumber the distinct ids in order of first appearance (at most 65535).
        const map = new Map();
        for (const array of attributeIdArrays) {
            for (let i = 0; i < array.length; i++) {
                if (!map.has(array[i]) && map.size < 65535) {
                    map.set(array[i], map.size);
                }
            }
        }
        ids = [...map.keys()];
        // An id that did not fit gets the one entry past the table, which carries no colour (the layer's own colour shows).
        paletteOf = (id) => map.get(id) ?? ids.length;
        attributeOf = (index) => ids[index] ?? null;
    }
    const rows = Math.max(1, Math.ceil((ids.length + 1) / 256));
    const rgba = new Uint8Array(rows * 256 * 4);
    for (let i = 0; i < ids.length; i++) {
        const rgb = resolveColorKey(attributes.get(ids[i]), attributeColors);
        if (rgb) {
            rgba[i * 4] = rgb[0];
            rgba[i * 4 + 1] = rgb[1];
            rgba[i * 4 + 2] = rgb[2];
            rgba[i * 4 + 3] = 255;
        }
    }
    return { paletteOf, attributeOf, size: ids.length, rows, rgba, remapped: maxId >= directLimit };
}

/** Diverging ramp (blue - white - red) for signed values, t in [-1, 1]: used for height deltas drawn by the UI. */
export function divergingRamp(t) {
    const x = clamp(t, -1, 1);
    const lerpc = (a, b, k) => a + (b - a) * k;
    const neg = [58, 134, 255];
    const mid = [235, 240, 243];
    const pos = [229, 72, 77];
    const [from, to, k] = x < 0 ? [neg, mid, x + 1] : [mid, pos, x];
    return [lerpc(from[0], to[0], k), lerpc(from[1], to[1], k), lerpc(from[2], to[2], k)].map(Math.round);
}

/** Packed RGBA8 word for a "#RRGGBB" string and alpha. */
export function packColor(color, alpha = 1) {
    const [r, g, b] = styleRgb(color);
    return packRgba(r, g, b, alpha);
}

/** Entity colours (design doc section 9). */
export const LIVE_COLORS = {
    player: '#FFFFFF',
    bot: '#7FD3FF',
    gameobject: '#A3B1BA',
    idle: '#C9D1D6',
    moving: '#FFD34D',
    chase: '#FF5A5A',
    spline: '#3CCB9A',
    route: '#7FD3FF',
    partial: '#F5A524',
};

/** Which creature colour a motion type string maps to. MovementGeneratorType names: Idle, Distract, Chase, ... */
export function creatureMotionClass(motionType) {
    const t = String(motionType ?? '').toLowerCase();
    if (t === '' || t === 'idle' || t === 'distract' || t === 'none' || t === 'stationary') {
        return 'idle';
    }
    if (t.includes('chase')) {
        return 'chase';
    }
    return 'moving';
}
