// Text labels for the live entities and probe overlays, drawn on a 2D canvas above the GL canvas. The layout is pure
// (project, de-overlap, cap the count); drawing takes any 2D context, so a capture can composite the same labels.

import { project, ndcToScreen } from './camera.js';

export const LABEL_FONT = '"Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, sans-serif';

/**
 * Projects label candidates ({at, text, color, priority?}) to pixels and drops those that are behind the camera, off
 * screen or overlap a label already placed (higher priority, then nearer the centre, wins). Returns
 * [{x, y, text, color}] with at most `max` entries. `measure(text)` gives the text width in pixels.
 */
export function layoutLabels(candidates, camera, width, height, measure, max = 120, lineHeight = 14) {
    const aspect = width / height;
    const projected = [];
    for (const c of candidates) {
        const p = project(camera, c.at, aspect);
        if (!p || p.depth > 1 || p.depth < -1) {
            continue;
        }
        const s = ndcToScreen(p.x, p.y, width, height);
        if (s.x < -50 || s.x > width + 50 || s.y < -20 || s.y > height + 20) {
            continue;
        }
        const centre = Math.hypot(p.x, p.y);
        projected.push({ ...c, x: s.x, y: s.y, centre });
    }
    projected.sort((a, b) => (b.priority ?? 0) - (a.priority ?? 0) || a.centre - b.centre);
    const placed = [];
    const out = [];
    for (const c of projected) {
        if (out.length >= max) {
            break;
        }
        const w = measure(c.text) + 6;
        const rect = { x0: c.x + 8, y0: c.y - 8 - lineHeight, x1: c.x + 8 + w, y1: c.y - 8 };
        if (placed.some((r) => rect.x0 < r.x1 && rect.x1 > r.x0 && rect.y0 < r.y1 && rect.y1 > r.y0)) {
            continue;
        }
        placed.push(rect);
        out.push({ x: rect.x0 + 3, y: rect.y1 - 3, text: c.text, color: c.color });
    }
    return out;
}

/** Unpacks the little-endian RGBA8 word (r | g << 8 | b << 16 | a << 24) to a CSS colour. */
export function cssFromPacked(word) {
    const r = word & 255;
    const g = (word >>> 8) & 255;
    const b = (word >>> 16) & 255;
    return `rgb(${r}, ${g}, ${b})`;
}

/** Draws laid-out labels with a dark halo so they read on any background. `scale` multiplies sizes (captures). */
export function drawLabels(ctx, labels, scale = 1) {
    ctx.save();
    ctx.font = `${11 * scale}px ${LABEL_FONT}`;
    ctx.textBaseline = 'alphabetic';
    ctx.lineJoin = 'round';
    ctx.lineWidth = 3 * scale;
    for (const l of labels) {
        ctx.strokeStyle = 'rgba(8, 13, 17, 0.85)';
        ctx.strokeText(l.text, l.x, l.y);
        ctx.fillStyle = cssFromPacked(l.color);
        ctx.fillText(l.text, l.x, l.y);
    }
    ctx.restore();
}

/** The 2D overlay canvas that sits over the GL canvas. */
export class LabelLayer {
    constructor(canvas) {
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.width = 1;
        this.height = 1;
        this.dpr = 1;
    }

    resize(cssWidth, cssHeight, dpr) {
        this.width = cssWidth;
        this.height = cssHeight;
        this.dpr = dpr;
        const w = Math.max(1, Math.round(cssWidth * dpr));
        const h = Math.max(1, Math.round(cssHeight * dpr));
        if (this.canvas.width !== w || this.canvas.height !== h) {
            this.canvas.width = w;
            this.canvas.height = h;
        }
    }

    draw(candidates, camera) {
        const ctx = this.ctx;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
        if (candidates.length === 0 || this.width < 2 || this.height < 2) {
            return 0;
        }
        ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
        ctx.font = `11px ${LABEL_FONT}`;
        const labels = layoutLabels(candidates, camera, this.width, this.height, (t) => ctx.measureText(t).width);
        drawLabels(ctx, labels, 1);
        return labels.length;
    }
}
