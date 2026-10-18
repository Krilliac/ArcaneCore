// Tolerant readers for the host's list answers (maps.list, bookmarks.list, GET /api/commands, status). The design doc
// describes each as a list or an object holding one; field names are read with the aliases the design doc uses, and
// anything else is ignored rather than guessed at.

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

function listOf(answer, ...keys) {
    if (Array.isArray(answer)) {
        return answer;
    }
    if (isObject(answer)) {
        for (const key of keys) {
            if (Array.isArray(answer[key])) {
                return answer[key];
            }
        }
    }
    return [];
}

/** maps.list -> [{id, name, tiles: {layerId: count}}] sorted by id. */
export function normalizeMaps(answer) {
    const maps = [];
    for (const item of listOf(answer, 'maps')) {
        const id = Number(isObject(item) ? item.map ?? item.id ?? item.mapId : item);
        if (!Number.isInteger(id) || id < 0) {
            continue;
        }
        const counts = isObject(item) ? item.layers ?? item.tiles ?? item.counts ?? {} : {};
        const tiles = {};
        if (isObject(counts)) {
            for (const [layer, n] of Object.entries(counts)) {
                const count = typeof n === 'number' ? n : isObject(n) ? Number(n.tiles ?? n.count ?? n.available) : NaN;
                if (Number.isFinite(count)) {
                    tiles[layer] = count;
                }
            }
        }
        maps.push({ id, name: isObject(item) && typeof item.name === 'string' ? item.name : '', tiles });
    }
    return maps.sort((a, b) => a.id - b.id);
}

/** Display text of a map: "0 - Eastern Kingdoms" or "0". */
export function mapLabel(map) {
    return map.name ? `${map.id} · ${map.name}` : String(map.id);
}

/** bookmarks.list -> [{name, map, at, note}] (map and at are null when the host does not report them). */
export function normalizeBookmarks(answer) {
    const out = [];
    for (const item of listOf(answer, 'bookmarks')) {
        const name = typeof item === 'string' ? item : isObject(item) ? item.name : null;
        if (typeof name !== 'string' || name === '') {
            continue;
        }
        const at = isObject(item) && Array.isArray(item.at) && item.at.length === 3 && item.at.every(Number.isFinite) ? item.at : null;
        out.push({
            name,
            map: isObject(item) && Number.isInteger(item.map) ? item.map : null,
            at,
            note: isObject(item) && typeof item.note === 'string' ? item.note : '',
            builtIn: isObject(item) && item.builtIn === true,
        });
    }
    return out;
}

/** GET /api/commands -> [{name, summary, surfaces: [lower-case], schema}] sorted by name. */
export function normalizeCommands(answer) {
    const out = [];
    for (const item of listOf(answer, 'commands')) {
        if (!isObject(item) || typeof item.name !== 'string') {
            continue;
        }
        const surfaces = Array.isArray(item.surfaces)
            ? item.surfaces.map((s) => String(s).toLowerCase())
            : typeof item.surfaces === 'string' ? item.surfaces.toLowerCase().split(/[\s,|]+/).filter(Boolean) : null;
        out.push({
            name: item.name,
            summary: typeof item.summary === 'string' ? item.summary : '',
            surfaces,
            schema: isObject(item.schema) ? item.schema : { type: 'object', properties: {} },
        });
    }
    return out.sort((a, b) => a.name.localeCompare(b.name));
}

/** Commands the palette lists: those carrying the Palette surface, or all when the host reports no surfaces. */
export function paletteCommands(commands) {
    return commands.filter((c) => c.surfaces === null || c.surfaces.includes('palette') || c.surfaces.includes('all'));
}

/** status -> what the empty states need. */
export function normalizeStatus(answer) {
    const s = isObject(answer) ? answer : {};
    const paths = isObject(s.paths) ? s.paths : s;
    const text = (v) => (typeof v === 'string' && v !== '' ? v : null);
    return {
        loaded: isObject(answer),
        problems: Array.isArray(s.problems) ? s.problems.map(String) : [],
        terrainData: text(paths.terrainData ?? paths.TerrainData ?? paths.dataDirectory ?? paths.data),
        clientRoot: text(paths.clientRoot ?? paths.ClientRoot ?? paths.clientDirectory ?? paths.client),
        version: text(s.version),
        modules: Array.isArray(s.modules) ? s.modules.map((m) => (typeof m === 'string' ? m : m?.name)).filter(Boolean) : [],
        cacheBytes: Number.isFinite(s.cacheBytes) ? s.cacheBytes : null,
        live: isObject(s.live) ? s.live : null,
    };
}

/** The probe pairs: for each `query.X` / `ref.query.X` found, {kind, server, reference}. `query.pick` is the inspector's. */
export function probeKinds(commands) {
    const byName = new Map(commands.map((c) => [c.name, c]));
    const kinds = new Map();
    for (const c of commands) {
        const server = /^query\.(.+)$/.exec(c.name);
        const reference = /^ref\.query\.(.+)$/.exec(c.name);
        const kind = server?.[1] ?? reference?.[1];
        if (!kind || kind === 'pick') {
            continue;
        }
        if (!kinds.has(kind)) {
            kinds.set(kind, { kind, server: byName.get(`query.${kind}`) ?? null, reference: byName.get(`ref.query.${kind}`) ?? null });
        }
    }
    const order = ['height', 'los', 'path', 'floorstack', 'nearest'];
    return [...kinds.values()].sort((a, b) => {
        const ia = order.indexOf(a.kind);
        const ib = order.indexOf(b.kind);
        return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.kind.localeCompare(b.kind);
    });
}

const isPoint3 = (p) => Array.isArray(p) && p.length === 3 && p.every((v) => typeof v === 'number' && Number.isFinite(v));

function readHit(raw) {
    if (!isObject(raw) || typeof raw.layer !== 'string' || !isPoint3(raw.point)) {
        return null;
    }
    const tile = Array.isArray(raw.tile) && raw.tile.length === 3 && raw.tile.every(Number.isInteger)
        ? { map: raw.tile[0], x: raw.tile[1], y: raw.tile[2] }
        : null;
    return {
        layer: raw.layer,
        tile,
        distance: Number.isFinite(raw.distance) ? raw.distance : null,
        point: raw.point,
        primitive: typeof raw.primitive === 'string' ? raw.primitive : '',
        attribute: Number.isInteger(raw.attribute) ? raw.attribute : null,
        details: isObject(raw.details) ? raw.details : {},
    };
}

/**
 * query.pick answer -> {hit, all}. The host returns PickHit.ToJson() fields (layer, tile, distance, point, primitive,
 * attribute, details), either at the top level or under `hit`; a `hits` list is kept as `all`. No hit is {hit: null}.
 */
export function normalizePick(data) {
    const body = isObject(data) ? data : {};
    const hit = readHit(isObject(body.hit) ? body.hit : body);
    const all = (Array.isArray(body.hits) ? body.hits : []).map(readHit).filter(Boolean);
    return { hit, all: hit && all.length === 0 ? [hit] : all };
}

/** Short text for the readout: the layer title and what the chunk's attribute table says about the primitive. */
export function describeSurface(layerTitle, record, attributeId) {
    const parts = [layerTitle];
    if (isObject(record)) {
        if (typeof record.colorKey === 'string' && record.colorKey !== '') {
            parts.push(record.colorKey);
        }
        let extra = 0;
        for (const [key, value] of Object.entries(record)) {
            if (key === 'colorKey' || extra >= 3) {
                continue;
            }
            if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
                parts.push(`${key} ${value}`);
                extra++;
            }
        }
    } else if (Number.isInteger(attributeId)) {
        parts.push(`attribute ${attributeId}`);
    }
    return parts.join(' · ');
}
