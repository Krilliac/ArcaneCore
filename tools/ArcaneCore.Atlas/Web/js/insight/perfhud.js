// Perf HUD (hotkey P): frame rate and frame time percentiles, what the renderer reports, the live feed's rate and lag, and what the host
// says. Every number is shown only when it exists: a renderer without texture work has no `textureBytes`, and the row is then absent rather
// than a fake zero. The numbers are also posted to the host with `perf.report` every 2 s so the CLI and MCP (`perf.stats`) see what the UI sees.

import { Emitter, formatBytes, formatCount, formatNumber, h, isFiniteNumber } from '../util.js';

/** Renderer stats that are shown with a label when the renderer reports them as numbers (the pinned names of the model design, section 4). */
export const RENDERER_ROWS = [
    ['drawCalls', 'Draw calls', 'count'],
    ['triangles', 'Triangles', 'count'],
    ['gpuBytes', 'GPU memory', 'bytes'],
    ['textureBytes', 'Texture memory', 'bytes'],
    ['textures', 'Textures', 'count'],
    ['texturesPending', 'Textures loading', 'count'],
    ['texturesMissing', 'Textures missing', 'count'],
    ['instances', 'Model instances', 'count'],
    ['modelGroups', 'Model groups', 'count'],
    ['modelFallbacks', 'Model fallbacks', 'count'],
    ['composites', 'Composites', 'count'],
    ['compositeFallbacks', 'Composite fallbacks', 'count'],
];

/** The keys `perf.report` accepts (the host refuses any other). */
export const REPORT_KEYS = ['fps', 'frameMsP50', 'frameMsP95', 'drawCalls', 'triangles', 'gpuMb', 'textureMb', 'liveHz', 'feedLagMs', 'entities', 'dropped', 'truncated', 'diffMs', 'online'];

const MB = 1024 * 1024;

/** The p-th percentile (0..1, nearest rank) of numbers; null for none. */
export function percentile(values, p) {
    const sorted = values.filter(isFiniteNumber).sort((a, b) => a - b);
    if (sorted.length === 0) {
        return null;
    }
    const rank = Math.min(sorted.length - 1, Math.max(0, Math.ceil(p * sorted.length) - 1));
    return sorted[rank];
}

/** A ring of the last frame durations (ms). */
export class FrameTimes {
    #values = [];

    constructor(capacity = 240) {
        this.capacity = capacity;
    }

    push(ms) {
        if (isFiniteNumber(ms) && ms >= 0) {
            this.#values.push(ms);
            if (this.#values.length > this.capacity) {
                this.#values.shift();
            }
        }
    }

    get count() {
        return this.#values.length;
    }

    p50() {
        return percentile(this.#values, 0.5);
    }

    p95() {
        return percentile(this.#values, 0.95);
    }

    /** Frames per second implied by the average frame time, or null when nothing was pushed. */
    fps() {
        if (this.#values.length === 0) {
            return null;
        }
        const mean = this.#values.reduce((a, b) => a + b, 0) / this.#values.length;
        return mean > 0 ? 1000 / mean : null;
    }
}

/**
 * The HUD's rows from what is known now: `frame` {fps, p50, p95}, the renderer's `stats` object, the `live` numbers {hz, lagMs, entities, online}
 * and the `server` numbers {dropped, truncated, diffMs}. Each is [key, label, text]; a row exists only when its number does.
 */
export function hudRows({ frame = {}, stats = {}, live = {}, server = {} } = {}) {
    const rows = [];
    const add = (key, label, value, format) => {
        if (isFiniteNumber(value)) {
            rows.push([key, label, format(value)]);
        }
    };
    add('fps', 'Frames per second', frame.fps, (v) => formatNumber(v, 1));
    add('frameMsP50', 'Frame time p50', frame.p50, (v) => `${formatNumber(v, 1)} ms`);
    add('frameMsP95', 'Frame time p95', frame.p95, (v) => `${formatNumber(v, 1)} ms`);
    for (const [name, label, kind] of RENDERER_ROWS) {
        add(name, label, stats?.[name], kind === 'bytes' ? formatBytes : formatCount);
    }
    add('liveHz', 'Live frames per second', live.hz, (v) => formatNumber(v, 1));
    add('feedLagMs', 'Feed lag', live.lagMs, (v) => `${formatNumber(v, 0)} ms`);
    add('entities', 'Entities', live.entities, formatCount);
    add('online', 'Online', live.online, formatCount);
    add('dropped', 'Dropped by the host', server.dropped, formatCount);
    add('truncated', 'Truncated by the host', server.truncated, formatCount);
    add('diffMs', 'Server diff time', server.diffMs, (v) => `${formatNumber(v, 1)} ms`);
    return rows;
}

/** The `perf.report` payload for the same inputs: only the keys that exist, as plain non-negative numbers. */
export function reportPayload({ frame = {}, stats = {}, live = {}, server = {} } = {}) {
    const out = {};
    const put = (key, value, scale = 1) => {
        if (isFiniteNumber(value) && value >= 0) {
            out[key] = value / scale;
        }
    };
    put('fps', frame.fps);
    put('frameMsP50', frame.p50);
    put('frameMsP95', frame.p95);
    put('drawCalls', stats?.drawCalls);
    put('triangles', stats?.triangles);
    put('gpuMb', stats?.gpuBytes, MB);
    put('textureMb', stats?.textureBytes, MB);
    put('liveHz', live.hz);
    put('feedLagMs', live.lagMs);
    put('entities', live.entities);
    put('online', live.online);
    put('dropped', server.dropped);
    put('truncated', server.truncated);
    put('diffMs', server.diffMs);
    return out;
}

/** The host-side numbers the HUD shows, from `perf.stats` -> live.stats: frames dropped for capacity, lines it had to truncate or refuse, and the server diff time when it reports one. */
export function serverNumbers(counters) {
    const sum = (...names) => (names.every((n) => !isFiniteNumber(counters?.[n])) ? undefined : names.reduce((a, n) => a + (isFiniteNumber(counters[n]) ? counters[n] : 0), 0));
    return {
        dropped: sum('droppedForCapacity'),
        truncated: sum('truncatedLines', 'oversizeLines'),
        diffMs: isFiniteNumber(counters?.diffMs) ? counters.diffMs : undefined,
    };
}

/** Measures the live frame rate and lag from the live model's frame events. */
export class LiveRate {
    #times = [];

    /** Records the arrival of a frame at `nowMs`. */
    note(nowMs) {
        this.#times.push(nowMs);
        while (this.#times.length > 1 && nowMs - this.#times[0] > 5000) {
            this.#times.shift();
        }
    }

    /** Frames per second over the last seconds; null with fewer than two frames. */
    hz() {
        if (this.#times.length < 2) {
            return null;
        }
        const span = this.#times[this.#times.length - 1] - this.#times[0];
        return span > 0 ? ((this.#times.length - 1) * 1000) / span : null;
    }

    /** Milliseconds since the last frame at `nowMs`; null before any. */
    lagMs(nowMs) {
        return this.#times.length === 0 ? null : Math.max(0, nowMs - this.#times[this.#times.length - 1]);
    }
}

/** The HUD overlay on the stage. Created hidden; `toggle()` shows it and starts the 2 s report. */
export class PerfHud extends Emitter {
    constructor(app, viewport, { now = () => performance.now(), raf = (fn) => requestAnimationFrame(fn), caf = (id) => cancelAnimationFrame(id) } = {}) {
        super();
        this.app = app;
        this.viewport = viewport;
        this.now = now;
        this.raf = raf;
        this.caf = caf;
        this.frames = new FrameTimes();
        this.rate = new LiveRate();
        this.server = {};
        this.visible = false;
        this.timer = null;
        this.last = null;
        this.handle = null;
        this.list = h('dl', { class: 'kv perfhud-list' });
        this.element = h('aside', { class: 'perfhud', hidden: true, aria: { label: 'Performance' }, role: 'status' },
            h('div', { class: 'perfhud-title', text: 'Performance' }), this.list);
        app.live.on('frame', () => this.rate.note(this.now()));
    }

    toggle() {
        this.visible ? this.hide() : this.show();
        return this.visible;
    }

    show() {
        this.visible = true;
        this.element.hidden = false;
        this.last = null;
        const tick = (time) => {
            if (this.last !== null) {
                this.frames.push(time - this.last);
            }
            this.last = time;
            this.handle = this.raf(tick);
        };
        this.handle = this.raf(tick);
        this.timer = setInterval(() => this.#every2s(), 2000);
        this.refresh();
    }

    hide() {
        this.visible = false;
        this.element.hidden = true;
        if (this.handle !== null) {
            this.caf(this.handle);
            this.handle = null;
        }
        clearInterval(this.timer);
        this.timer = null;
    }

    inputs() {
        const latest = this.app.live.latest;
        const entities = latest ? latest.entities.size : null;
        let online = null;
        if (latest) {
            online = 0;
            for (const e of latest.entities.values()) {
                if (e.kind === 'player' || e.kind === 'bot') {
                    online++;
                }
            }
        }
        const now = this.now();
        return {
            frame: { fps: this.frames.fps(), p50: this.frames.p50(), p95: this.frames.p95() },
            stats: this.viewport.renderer?.stats ?? {},
            live: { hz: this.rate.hz(), lagMs: this.rate.lagMs(now), entities, online },
            server: this.server,
        };
    }

    refresh() {
        const rows = hudRows(this.inputs());
        const nodes = [];
        for (const [key, label, text] of rows) {
            nodes.push(h('dt', { text: label, data: { stat: key } }), h('dd', { class: 'num', text, data: { stat: key } }));
        }
        this.list.replaceChildren(...nodes);
        this.emit('refresh', rows);
        return rows;
    }

    async #every2s() {
        this.refresh();
        if (!this.app.hasCommand('perf.report')) {
            return;
        }
        const stats = reportPayload(this.inputs());
        if (Object.keys(stats).length > 0) {
            await this.app.runCommand('perf.report', { stats }, { quiet: true, noRefresh: true });
        }
        if (this.app.hasCommand('perf.stats')) {
            const result = await this.app.runCommand('perf.stats', {}, { quiet: true, noRefresh: true });
            const counters = result.ok ? result.data?.live?.stats : null;
            if (counters && typeof counters === 'object') {
                this.server = serverNumbers(counters);
            }
        }
    }
}
