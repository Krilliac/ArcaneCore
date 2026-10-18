// Builds the instance data of thick screen-space lines for probe overlays, selections and live paths.
// Layout per segment (7 words, 28 bytes), the same as the line instances built for tile chunks:
//   f32 ax, ay, az, bx, by, bz   relative to the set's origin
//   u32 packed RGBA8 (r | g << 8 | b << 16 | a << 24)
// Coordinates are collected in double precision and made origin-relative only when built, so a set far from the world
// origin keeps its precision.

export const LINE_WORDS = 7;
export const LINE_STRIDE = LINE_WORDS * 4;

export class LineBuilder {
    #coords = [];
    #colors = [];

    get count() {
        return this.#colors.length;
    }

    add(a, b, rgba) {
        this.#coords.push(a[0], a[1], a[2], b[0], b[1], b[2]);
        this.#colors.push(rgba >>> 0);
        return this;
    }

    addPolyline(points, rgba, closed = false) {
        for (let i = 0; i + 1 < points.length; i++) {
            this.add(points[i], points[i + 1], rgba);
        }
        if (closed && points.length > 2) {
            this.add(points[points.length - 1], points[0], rgba);
        }
        return this;
    }

    /** The twelve edges of an axis-aligned box. */
    addBox(min, max, rgba) {
        const c = [
            [min[0], min[1], min[2]], [max[0], min[1], min[2]], [max[0], max[1], min[2]], [min[0], max[1], min[2]],
            [min[0], min[1], max[2]], [max[0], min[1], max[2]], [max[0], max[1], max[2]], [min[0], max[1], max[2]],
        ];
        for (const [i, j] of [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]]) {
            this.add(c[i], c[j], rgba);
        }
        return this;
    }

    /** Three axis-aligned strokes through a point. */
    addCross(p, size, rgba) {
        this.add([p[0] - size, p[1], p[2]], [p[0] + size, p[1], p[2]], rgba);
        this.add([p[0], p[1] - size, p[2]], [p[0], p[1] + size, p[2]], rgba);
        this.add([p[0], p[1], p[2] - size], [p[0], p[1], p[2] + size], rgba);
        return this;
    }

    /** A horizontal ring. */
    addRing(centre, radius, rgba, segments = 32) {
        let prev = [centre[0] + radius, centre[1], centre[2]];
        for (let i = 1; i <= segments; i++) {
            const a = (i / segments) * Math.PI * 2;
            const next = [centre[0] + Math.cos(a) * radius, centre[1] + Math.sin(a) * radius, centre[2]];
            this.add(prev, next, rgba);
            prev = next;
        }
        return this;
    }

    /** A segment drawn as dashes in world space (for hosts that want dashes independent of the screen). */
    addDashed(a, b, rgba, dash, gap) {
        const dx = b[0] - a[0];
        const dy = b[1] - a[1];
        const dz = b[2] - a[2];
        const len = Math.hypot(dx, dy, dz);
        if (!(len > 0)) {
            return this;
        }
        const period = dash + gap;
        for (let s = 0; s < len; s += period) {
            const e = Math.min(s + dash, len);
            this.add(
                [a[0] + (dx * s) / len, a[1] + (dy * s) / len, a[2] + (dz * s) / len],
                [a[0] + (dx * e) / len, a[1] + (dy * e) / len, a[2] + (dz * e) / len],
                rgba,
            );
        }
        return this;
    }

    /**
     * Origin-relative instance data. `origin` defaults to the first segment's start. Returns
     * {origin, buffer, count}: `buffer` is an ArrayBuffer of count * 28 bytes.
     */
    build(origin) {
        const n = this.#colors.length;
        const o = origin ?? (n > 0 ? [this.#coords[0], this.#coords[1], this.#coords[2]] : [0, 0, 0]);
        const buffer = new ArrayBuffer(n * LINE_STRIDE);
        const f = new Float32Array(buffer);
        const u = new Uint32Array(buffer);
        for (let i = 0; i < n; i++) {
            const c = i * 6;
            const w = i * LINE_WORDS;
            f[w] = this.#coords[c] - o[0];
            f[w + 1] = this.#coords[c + 1] - o[1];
            f[w + 2] = this.#coords[c + 2] - o[2];
            f[w + 3] = this.#coords[c + 3] - o[0];
            f[w + 4] = this.#coords[c + 4] - o[1];
            f[w + 5] = this.#coords[c + 5] - o[2];
            u[w + 6] = this.#colors[i];
        }
        return { origin: [o[0], o[1], o[2]], buffer, count: n };
    }
}

/** Instance data for markers: 6 words (x, y, z relative to origin; packed colour; size px; shape). */
export const MARKER_WORDS = 6;
export const MARKER_STRIDE = MARKER_WORDS * 4;

export const SHAPES = { circle: 0, diamond: 1, square: 2, ring: 3 };

export class MarkerBuilder {
    #coords = [];
    #meta = [];

    get count() {
        return this.#meta.length / 3;
    }

    add(p, rgba, size, shape = SHAPES.circle) {
        this.#coords.push(p[0], p[1], p[2]);
        this.#meta.push(rgba >>> 0, size, shape);
        return this;
    }

    build(origin) {
        const n = this.count;
        const o = origin ?? (n > 0 ? [this.#coords[0], this.#coords[1], this.#coords[2]] : [0, 0, 0]);
        const buffer = new ArrayBuffer(n * MARKER_STRIDE);
        const f = new Float32Array(buffer);
        const u = new Uint32Array(buffer);
        for (let i = 0; i < n; i++) {
            const w = i * MARKER_WORDS;
            f[w] = this.#coords[i * 3] - o[0];
            f[w + 1] = this.#coords[i * 3 + 1] - o[1];
            f[w + 2] = this.#coords[i * 3 + 2] - o[2];
            u[w + 3] = this.#meta[i * 3];
            f[w + 4] = this.#meta[i * 3 + 1];
            f[w + 5] = this.#meta[i * 3 + 2];
        }
        return { origin: [o[0], o[1], o[2]], buffer, count: n };
    }
}
