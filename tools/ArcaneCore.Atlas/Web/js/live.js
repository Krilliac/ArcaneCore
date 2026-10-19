// Live view model: entities, splines, routes, trails and loaded grids from the map feed (design doc section 9).
// A frame is {op:"frame", seq, clockMs, map, instance, grids:[[gx,gy]...], counters, entities:[E...]} with
//   E = {g:"<hex guid>", k:"player|bot|creature|gameobject", e:entry, n:name, x, y, z, o, hp?, mt?, tg?, home?:[x,y,z],
//        sp?:{id, start:[x,y,z], pts:[[x,y,z]...], t0:clockMs, dur:ms, run}, route?:{pts:[[x,y,z]...], next:i, flags:[...]}}
// The UI receives frames on the `live.frame` event topic and the connection on `live.status`.
// A frame is a complete snapshot of one map instance: an entity missing from the next frame has gone.

import { LIVE_COLORS, creatureMotionClass, packColor } from './colors.js';
import { LineBuilder, MarkerBuilder, SHAPES } from './geom.js';
import { tileBounds, tileOfGrid } from './tiles.js';
import { Emitter, clamp, isFiniteNumber } from './util.js';

export const KINDS = ['player', 'bot', 'creature', 'gameobject'];
const TRAIL_POINTS = 48;
const TRAIL_MIN_STEP = 1.0;
const TRAIL_LIFETIME_MS = 60_000;

const isPoint = (p) => Array.isArray(p) && p.length >= 3 && isFiniteNumber(p[0]) && isFiniteNumber(p[1]) && isFiniteNumber(p[2]);

/** One feed entity -> the UI's record, or null when it has no usable position. */
export function normalizeEntity(e) {
    if (e === null || typeof e !== 'object' || typeof e.g !== 'string' || !isFiniteNumber(e.x) || !isFiniteNumber(e.y) || !isFiniteNumber(e.z)) {
        return null;
    }
    const kind = KINDS.includes(e.k) ? e.k : 'creature';
    let spline = null;
    if (e.sp && isPoint(e.sp.start) && Array.isArray(e.sp.pts) && e.sp.pts.every(isPoint) && isFiniteNumber(e.sp.t0) && isFiniteNumber(e.sp.dur)) {
        spline = { id: e.sp.id, path: [e.sp.start.slice(0, 3), ...e.sp.pts.map((p) => p.slice(0, 3))], t0: e.sp.t0, dur: e.sp.dur, run: e.sp.run === true };
    }
    let route = null;
    if (e.route && Array.isArray(e.route.pts) && e.route.pts.length >= 2 && e.route.pts.every(isPoint)) {
        route = {
            pts: e.route.pts.map((p) => p.slice(0, 3)),
            next: isFiniteNumber(e.route.next) ? e.route.next : 0,
            flags: Array.isArray(e.route.flags) ? e.route.flags.map(String) : [],
        };
    }
    return {
        guid: e.g.toUpperCase(),
        kind,
        entry: isFiniteNumber(e.e) ? e.e : null,
        name: typeof e.n === 'string' ? e.n : '',
        pos: [e.x, e.y, e.z],
        orientation: isFiniteNumber(e.o) ? e.o : null,
        hp: isFiniteNumber(e.hp) ? e.hp : null,
        motion: typeof e.mt === 'string' ? e.mt : '',
        target: typeof e.tg === 'string' ? e.tg.toUpperCase() : null,
        home: isPoint(e.home) ? e.home.slice(0, 3) : null,
        spline,
        route,
    };
}

/** Position `fraction` (0..1) of the way along a constant-speed polyline. */
export function pointAlong(path, fraction) {
    if (path.length === 0) {
        return null;
    }
    if (path.length === 1 || fraction <= 0) {
        return [...path[0]];
    }
    const lengths = [];
    let total = 0;
    for (let i = 0; i + 1 < path.length; i++) {
        const d = Math.hypot(path[i + 1][0] - path[i][0], path[i + 1][1] - path[i][1], path[i + 1][2] - path[i][2]);
        lengths.push(d);
        total += d;
    }
    if (fraction >= 1 || total === 0) {
        return [...path[path.length - 1]];
    }
    let remaining = fraction * total;
    for (let i = 0; i < lengths.length; i++) {
        if (remaining <= lengths[i] || i === lengths.length - 1) {
            const t = lengths[i] > 0 ? remaining / lengths[i] : 0;
            return [0, 1, 2].map((a) => path[i][a] + (path[i + 1][a] - path[i][a]) * t);
        }
        remaining -= lengths[i];
    }
    return [...path[path.length - 1]];
}

/** Where a spline's head is, given the server clock in ms. */
export function splineHead(spline, serverClockMs) {
    const f = spline.dur > 0 ? clamp((serverClockMs - spline.t0) / spline.dur, 0, 1) : 1;
    return { point: pointAlong(spline.path, f), fraction: f };
}

export class LiveModel extends Emitter {
    status = { connected: false, pid: null, sha: null, detail: '' };
    #frames = new Map();
    #trails = new Map();
    #latestKey = null;
    #framesSeen = 0;

    get framesSeen() {
        return this.#framesSeen;
    }

    applyStatus(data) {
        const d = data && typeof data === 'object' ? data : {};
        this.status = {
            connected: d.connected === true,
            pid: isFiniteNumber(d.pid) ? d.pid : null,
            sha: typeof d.sha === 'string' ? d.sha : null,
            detail: typeof d.detail === 'string' ? d.detail : '',
        };
        if (!this.status.connected) {
            this.#frames.clear();
            this.#trails.clear();
            this.#latestKey = null;
        }
        this.emit('status', this.status);
        this.emit('change');
    }

    applyFrame(raw, receivedAtMs) {
        if (raw === null || typeof raw !== 'object' || !isFiniteNumber(raw.map) || !Array.isArray(raw.entities)) {
            return false;
        }
        const instance = isFiniteNumber(raw.instance) ? raw.instance : 0;
        const key = `${raw.map}:${instance}`;
        const entities = new Map();
        for (const e of raw.entities) {
            const n = normalizeEntity(e);
            if (n) {
                entities.set(n.guid, n);
            }
        }
        const grids = [];
        for (const g of Array.isArray(raw.grids) ? raw.grids : []) {
            if (Array.isArray(g) && Number.isInteger(g[0]) && Number.isInteger(g[1])) {
                grids.push(tileOfGrid(g[0], g[1]));
            }
        }
        const clockMs = isFiniteNumber(raw.clockMs) ? raw.clockMs : 0;
        this.#frames.set(key, {
            key,
            seq: isFiniteNumber(raw.seq) ? raw.seq : 0,
            clockMs,
            map: raw.map,
            instance,
            grids,
            counters: raw.counters && typeof raw.counters === 'object' ? raw.counters : {},
            receivedAt: receivedAtMs,
            entities,
        });
        this.#latestKey = key;
        this.#framesSeen++;
        this.#advanceTrails(key, entities, clockMs, receivedAtMs);
        this.emit('frame');
        this.emit('change');
        return true;
    }

    #advanceTrails(key, entities, clockMs, receivedAtMs) {
        for (const e of entities.values()) {
            const id = `${key}/${e.guid}`;
            let trail = this.#trails.get(id);
            if (!trail) {
                trail = { points: [], seenAt: receivedAtMs };
                this.#trails.set(id, trail);
            }
            trail.seenAt = receivedAtMs;
            const last = trail.points[trail.points.length - 1];
            if (!last || Math.hypot(e.pos[0] - last[0], e.pos[1] - last[1], e.pos[2] - last[2]) >= TRAIL_MIN_STEP) {
                trail.points.push([e.pos[0], e.pos[1], e.pos[2], receivedAtMs]);
                if (trail.points.length > TRAIL_POINTS) {
                    trail.points.shift();
                }
            }
        }
        for (const [id, trail] of this.#trails) {
            if (receivedAtMs - trail.seenAt > TRAIL_LIFETIME_MS) {
                this.#trails.delete(id);
            }
        }
    }

    /** The newest frame for a map (any instance, the most recently received), or null. */
    frameFor(map) {
        let best = null;
        for (const f of this.#frames.values()) {
            if (f.map === map && (best === null || f.receivedAt >= best.receivedAt)) {
                best = f;
            }
        }
        return best;
    }

    trailsFor(frame) {
        const out = [];
        const prefix = `${frame.key}/`;
        for (const [id, trail] of this.#trails) {
            if (id.startsWith(prefix) && frame.entities.has(id.slice(prefix.length))) {
                out.push({ guid: id.slice(prefix.length), points: trail.points });
            }
        }
        return out;
    }

    /** Looks an entity up in any frame (the newest first). */
    find(guid) {
        const g = String(guid).toUpperCase();
        let best = null;
        for (const f of this.#frames.values()) {
            const e = f.entities.get(g);
            if (e && (best === null || f.receivedAt >= best.frame.receivedAt)) {
                best = { entity: e, frame: f };
            }
        }
        return best;
    }

    get latest() {
        return this.#latestKey ? this.#frames.get(this.#latestKey) : null;
    }

    summary() {
        const counts = { player: 0, bot: 0, creature: 0, gameobject: 0 };
        let total = 0;
        for (const f of this.#frames.values()) {
            for (const e of f.entities.values()) {
                counts[e.kind]++;
                total++;
            }
        }
        return { frames: this.#frames.size, total, counts };
    }
}

/** Case-insensitive match on name, guid, entry, kind or motion type. */
export function filterEntities(entities, text) {
    const q = String(text ?? '').trim().toLowerCase();
    const list = [...entities];
    if (q === '') {
        return list;
    }
    return list.filter((e) => e.name.toLowerCase().includes(q) || e.guid.toLowerCase().includes(q)
        || String(e.entry ?? '').includes(q) || e.kind.includes(q) || e.motion.toLowerCase().includes(q));
}

/** Colour key class for a creature: idle, moving or chase. */
export const motionClass = (entity) => creatureMotionClass(entity.motion);

function layerColor(styles, id, fallback) {
    return styles[id]?.color ?? fallback;
}

function creatureColor(styles, entity) {
    const s = styles['live.creatures'];
    if (!s || !s.colorByAttribute) {
        return layerColor(styles, 'live.creatures', LIVE_COLORS.idle);
    }
    const klass = motionClass(entity);
    return s.attributeColors?.[klass] ?? LIVE_COLORS[klass];
}

/**
 * Everything the renderer draws for the live layers at this instant.
 * `styles`: {layerId: {visible, color, opacity, colorByAttribute, attributeColors}}; `now` is performance.now() in ms.
 * Returns {markers, solid, dashed, labelCandidates, counts}.
 */
export function buildLiveGeometry(model, map, styles, now, selectedGuid, followGuid) {
    const frame = model.frameFor(map);
    const markers = new MarkerBuilder();
    const solid = new LineBuilder();
    const dashed = new LineBuilder();
    const labelCandidates = [];
    const counts = { entities: 0, splines: 0, routes: 0 };
    if (!frame) {
        return { markers: null, solid: null, dashed: null, labelCandidates, counts, frame: null };
    }
    const on = (id) => styles[id]?.visible === true;
    const alpha = (id) => styles[id]?.opacity ?? 1;
    const serverClock = frame.clockMs + (now - frame.receivedAt);

    for (const e of frame.entities.values()) {
        const layerId = e.kind === 'player' ? 'live.players' : e.kind === 'bot' ? 'live.bots' : e.kind === 'gameobject' ? 'live.gameobjects' : 'live.creatures';
        const highlighted = e.guid === selectedGuid || e.guid === followGuid;
        if (on(layerId)) {
            const color = e.kind === 'creature' ? creatureColor(styles, e) : layerColor(styles, layerId, LIVE_COLORS[e.kind]);
            const shape = e.kind === 'gameobject' ? SHAPES.square : e.kind === 'creature' ? SHAPES.circle : SHAPES.diamond;
            const size = e.kind === 'player' ? 11 : e.kind === 'bot' ? 10 : e.kind === 'gameobject' ? 7 : 8;
            markers.add(e.pos, packColor(color, alpha(layerId)), highlighted ? size + 4 : size, shape);
            if (highlighted) {
                markers.add(e.pos, packColor('#1F5FFF', 1), size + 12, SHAPES.ring);
            }
            if (e.orientation !== null && (e.kind === 'player' || e.kind === 'bot')) {
                // Facing tick, 2.5 yd long, in the creature's colour (orientation 0 faces +X like yaw 0).
                solid.add(e.pos, [e.pos[0] + Math.cos(e.orientation) * 2.5, e.pos[1] + Math.sin(e.orientation) * 2.5, e.pos[2]], packColor(color, alpha(layerId)));
            }
            counts.entities++;
            if (e.name && (e.kind !== 'creature' || highlighted)) {
                labelCandidates.push({ at: e.pos, text: e.name, color: packColor(color, 1), priority: highlighted ? 3 : e.kind === 'player' ? 2 : 1 });
            }
        }
        if (e.spline && on('live.splines')) {
            const c = packColor(layerColor(styles, 'live.splines', LIVE_COLORS.spline), alpha('live.splines'));
            solid.addPolyline(e.spline.path, c);
            const head = splineHead(e.spline, serverClock);
            if (head.point) {
                markers.add(head.point, c, 9, SHAPES.ring);
            }
            counts.splines++;
        }
        if (e.route && on('live.routes')) {
            const partial = e.route.flags.some((f) => /short|partial|incomplete/i.test(f));
            const routeStyle = styles['live.routes'];
            const color = routeStyle?.colorByAttribute
                ? routeStyle.attributeColors?.[partial ? 'partial' : 'ok'] ?? (partial ? LIVE_COLORS.partial : LIVE_COLORS.route)
                : layerColor(styles, 'live.routes', LIVE_COLORS.route);
            const c = packColor(color, alpha('live.routes'));
            (partial ? dashed : solid).addPolyline(e.route.pts, c);
            const next = e.route.pts[clamp(e.route.next, 0, e.route.pts.length - 1)];
            markers.add(next, c, 9, SHAPES.ring);
            counts.routes++;
        }
    }

    if (on('live.trails')) {
        for (const trail of model.trailsFor(frame)) {
            const base = styles['live.trails'].color;
            for (let i = 0; i + 1 < trail.points.length; i++) {
                const age = clamp((now - trail.points[i + 1][3]) / 20_000, 0, 1);
                const a = (1 - age) * alpha('live.trails');
                if (a > 0.02) {
                    solid.add(trail.points[i].slice(0, 3), trail.points[i + 1].slice(0, 3), packColor(base, a));
                }
            }
        }
    }

    if (on('live.grids')) {
        const c = packColor(layerColor(styles, 'live.grids', '#1F5FFF'), alpha('live.grids'));
        const z = frame.entities.size > 0 ? [...frame.entities.values()][0].pos[2] : 0;
        for (const g of frame.grids) {
            const b = tileBounds(g.x, g.y);
            solid.addPolyline([[b.maxX, b.maxY, z], [b.minX, b.maxY, z], [b.minX, b.minY, z], [b.maxX, b.minY, z]], c, true);
        }
    }

    return {
        markers: markers.count > 0 ? markers.build() : null,
        solid: solid.count > 0 ? solid.build() : null,
        dashed: dashed.count > 0 ? dashed.build() : null,
        labelCandidates,
        counts,
        frame,
    };
}

/** Nearest entity to a screen point within `maxPixels`, given a projector (world -> {x, y} pixels or null). */
export function pickEntity(entities, project, px, py, maxPixels = 12) {
    let best = null;
    let bestD = maxPixels * maxPixels;
    for (const e of entities) {
        const s = project(e.pos);
        if (!s) {
            continue;
        }
        const d = (s.x - px) ** 2 + (s.y - py) ** 2;
        if (d <= bestD) {
            bestD = d;
            best = e;
        }
    }
    return best;
}
