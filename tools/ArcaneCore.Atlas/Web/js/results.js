// Turns a command result's data into rows a panel can show, and finds where a server answer and a reference answer
// disagree. Pure: no DOM, so the row layout and the comparison rules are unit tested.

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const isNumber = (v) => typeof v === 'number' && Number.isFinite(v);

/** Number text: whole numbers as they are, others with up to 3 decimals and no trailing zeros. */
export function scalarText(value) {
    if (value === null || value === undefined) {
        return 'none';
    }
    if (typeof value === 'number') {
        if (!Number.isFinite(value)) {
            return String(value);
        }
        return Number.isInteger(value) ? String(value) : String(Number(value.toFixed(3)));
    }
    return String(value);
}

const isScalar = (v) => v === null || ['string', 'number', 'boolean'].includes(typeof v);

/** An array of scalars on one line ("1, 2, 3"), at most `limit` of them. */
export function inlineArray(values, limit = 12) {
    const shown = values.slice(0, limit).map(scalarText).join(', ');
    return values.length > limit ? `${shown}, and ${values.length - limit} more` : shown;
}

/**
 * Rows for a result: [{path, depth, key, text}] where `text` is null for a heading row (an object or list of objects).
 * Objects nest to `maxDepth`; deeper values are shown as JSON. `skip` lists top-level keys to leave out (the overlay is
 * drawn on the map, not listed).
 */
export function flatten(data, { maxDepth = 4, maxItems = 24, skip = ['overlay'] } = {}) {
    const rows = [];
    const walk = (value, path, depth, key) => {
        if (isScalar(value)) {
            rows.push({ path, depth, key, text: scalarText(value) });
        } else if (Array.isArray(value)) {
            if (value.every(isScalar)) {
                rows.push({ path, depth, key, text: value.length === 0 ? 'empty' : inlineArray(value) });
            } else if (depth >= maxDepth) {
                rows.push({ path, depth, key, text: JSON.stringify(value) });
            } else {
                rows.push({ path, depth, key, text: null });
                value.slice(0, maxItems).forEach((item, i) => walk(item, `${path}[${i}]`, depth + 1, `[${i}]`));
                if (value.length > maxItems) {
                    rows.push({ path: `${path}[…]`, depth: depth + 1, key: '…', text: `${value.length - maxItems} more` });
                }
            }
        } else if (depth >= maxDepth) {
            rows.push({ path, depth, key, text: JSON.stringify(value) });
        } else {
            rows.push({ path, depth, key, text: null });
            for (const [k, v] of Object.entries(value)) {
                walk(v, `${path}.${k}`, depth + 1, k);
            }
        }
    };
    if (isObject(data)) {
        for (const [k, v] of Object.entries(data)) {
            if (!skip.includes(k)) {
                walk(v, k, 0, k);
            }
        }
    } else if (data !== undefined) {
        walk(data, '(result)', 0, '(result)');
    }
    return rows;
}

/**
 * The set of paths present in both row lists whose text differs. Numbers within `tolerance` count as equal, so a
 * float32 rounding difference between the server and reference engines is not reported as a disagreement.
 */
export function differences(left, right, tolerance = 0.01) {
    const byPath = new Map(right.map((r) => [r.path, r]));
    const out = new Set();
    for (const row of left) {
        const other = byPath.get(row.path);
        if (!other || row.text === null || other.text === null) {
            continue;
        }
        if (row.text === other.text) {
            continue;
        }
        const a = Number(row.text);
        const b = Number(other.text);
        if (row.text !== '' && other.text !== '' && isNumber(a) && isNumber(b) && Math.abs(a - b) <= tolerance) {
            continue;
        }
        out.add(row.path);
    }
    return out;
}

/** "3 points, 1 polyline" for an overlay's counts. */
export function overlaySummary(counts) {
    const parts = [];
    const add = (n, one, many) => {
        if (n > 0) {
            parts.push(`${n} ${n === 1 ? one : many}`);
        }
    };
    add(counts.points, 'point', 'points');
    add(counts.segments, 'segment', 'segments');
    add(counts.polylines, 'polyline', 'polylines');
    add(counts.boxes, 'box', 'boxes');
    const text = parts.length > 0 ? parts.join(', ') : 'nothing to draw';
    return counts.dropped > 0 ? `${text} (${counts.dropped} malformed dropped)` : text;
}

/** Short title for a probe kind: height -> "Height", los -> "Line of sight". */
export function probeTitle(kind) {
    const known = { height: 'Height', los: 'Line of sight', path: 'Path', floorstack: 'Floor stack', nearest: 'Nearest polygon' };
    return known[kind] ?? `${kind.charAt(0).toUpperCase()}${kind.slice(1)}`;
}
