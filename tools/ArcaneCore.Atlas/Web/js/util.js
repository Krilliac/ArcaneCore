// Shared helpers. Everything here is pure except `h`, which builds DOM nodes when called (never at import time),
// so the modules that tests import under node stay free of browser globals.

export const clamp = (value, lo, hi) => (value < lo ? lo : value > hi ? hi : value);

export const lerp = (a, b, t) => a + (b - a) * t;

export const DEG = Math.PI / 180;

/** Wraps degrees into [0, 360), the way ViewState.FromJson wraps yaw. */
export function wrapDegrees(deg) {
    const r = deg % 360;
    return r < 0 ? r + 360 : r;
}

/** True for a number that is neither NaN nor infinite (Number.isFinite without coercion). */
export const isFiniteNumber = (v) => typeof v === 'number' && Number.isFinite(v);

export function deepClone(value) {
    return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

export function deepEqual(a, b) {
    if (a === b) {
        return true;
    }
    if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) {
        return false;
    }
    if (Array.isArray(a) !== Array.isArray(b)) {
        return false;
    }
    const ka = Object.keys(a);
    const kb = Object.keys(b);
    if (ka.length !== kb.length) {
        return false;
    }
    for (const k of ka) {
        if (!Object.prototype.hasOwnProperty.call(b, k) || !deepEqual(a[k], b[k])) {
            return false;
        }
    }
    return true;
}

/**
 * RFC 7396 section 2, as ViewState.MergePatch does it: objects merge recursively, null deletes a member, anything
 * else (arrays included) replaces. Never mutates its arguments.
 */
export function mergePatch(target, patch) {
    if (patch === null || typeof patch !== 'object' || Array.isArray(patch)) {
        return deepClone(patch);
    }
    const result = target !== null && typeof target === 'object' && !Array.isArray(target) ? deepClone(target) : {};
    for (const [name, value] of Object.entries(patch)) {
        if (value === null) {
            delete result[name];
        } else {
            result[name] = mergePatch(result[name], value);
        }
    }
    return result;
}

/**
 * Composes two merge patches into one that has the effect of applying `first` then `second` (nulls are kept, because a
 * null in the combined patch must still delete the member when it is finally applied). One pair cannot be composed
 * exactly: a member deleted or replaced by a scalar in `first` and given an object in `second`, because a merge patch
 * cannot say "replace" and the object would merge into whatever the member held. The view patches the UI sends never
 * contain such a pair.
 */
export function composePatch(first, second) {
    const plain = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
    if (!plain(first) || !plain(second)) {
        return deepClone(second);
    }
    const out = deepClone(first);
    for (const [key, value] of Object.entries(second)) {
        out[key] = plain(value) && plain(out[key]) ? composePatch(out[key], value) : deepClone(value);
    }
    return out;
}

/**
 * The smallest patch that turns `from` into `to` (objects recurse, a removed member becomes null, other differences
 * replace). Returns undefined when nothing differs.
 */
export function diffPatch(from, to) {
    if (deepEqual(from, to)) {
        return undefined;
    }
    const plain = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
    if (!plain(from) || !plain(to)) {
        return deepClone(to);
    }
    const patch = {};
    for (const key of Object.keys(to)) {
        const d = diffPatch(from[key], to[key]);
        if (d !== undefined) {
            patch[key] = d;
        }
    }
    for (const key of Object.keys(from)) {
        if (!(key in to)) {
            patch[key] = null;
        }
    }
    return patch;
}

/** Calls `fn` at most once per `ms`, with a trailing call so the last value always lands. */
export function throttle(fn, ms, clock = () => performance.now(), schedule = setTimeout, cancel = clearTimeout) {
    let last = -Infinity;
    let timer = null;
    let pendingArgs = null;
    const fire = () => {
        timer = null;
        last = clock();
        const args = pendingArgs;
        pendingArgs = null;
        if (args) {
            fn(...args);
        }
    };
    const throttled = (...args) => {
        pendingArgs = args;
        const wait = last + ms - clock();
        if (wait <= 0) {
            if (timer !== null) {
                cancel(timer);
            }
            fire();
        } else if (timer === null) {
            timer = schedule(fire, wait);
        }
    };
    throttled.flush = () => {
        if (timer !== null) {
            cancel(timer);
        }
        fire();
    };
    throttled.cancel = () => {
        if (timer !== null) {
            cancel(timer);
            timer = null;
        }
        pendingArgs = null;
    };
    return throttled;
}

/** A fixed number of decimals; "-" for anything that is not a finite number. */
export function formatNumber(value, digits = 2) {
    return isFiniteNumber(value) ? value.toFixed(digits) : '-';
}

export function formatPoint(p, digits = 2) {
    return Array.isArray(p) && p.length >= 3 ? p.slice(0, 3).map((v) => formatNumber(v, digits)).join(', ') : '-';
}

/** 1234 -> "1,234"; 1_250_000 -> "1.25M". */
export function formatCount(value) {
    if (!isFiniteNumber(value)) {
        return '-';
    }
    const abs = Math.abs(value);
    if (abs >= 1e9) {
        return `${(value / 1e9).toFixed(2)}B`;
    }
    if (abs >= 1e6) {
        return `${(value / 1e6).toFixed(2)}M`;
    }
    return Math.round(value).toLocaleString('en-US');
}

export function formatBytes(value) {
    if (!isFiniteNumber(value)) {
        return '-';
    }
    const units = ['B', 'KiB', 'MiB', 'GiB'];
    let v = value;
    let i = 0;
    while (v >= 1024 && i < units.length - 1) {
        v /= 1024;
        i++;
    }
    return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

export function formatDuration(ms) {
    if (!isFiniteNumber(ms)) {
        return '-';
    }
    return ms < 1 ? `${ms.toFixed(2)} ms` : ms < 100 ? `${ms.toFixed(1)} ms` : ms < 10000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`;
}

/** "#RRGGBB" (any case) -> [r, g, b] in 0..255, or null. */
export function parseHexColor(text) {
    if (typeof text !== 'string' || !/^#[0-9a-fA-F]{6}$/.test(text)) {
        return null;
    }
    return [parseInt(text.slice(1, 3), 16), parseInt(text.slice(3, 5), 16), parseInt(text.slice(5, 7), 16)];
}

export function toHexColor(r, g, b) {
    const part = (v) => clamp(Math.round(v), 0, 255).toString(16).padStart(2, '0');
    return `#${part(r)}${part(g)}${part(b)}`.toUpperCase();
}

/** Packs r, g, b (0..255) and alpha (0..1) into the little-endian RGBA8 word the line and marker shaders unpack. */
export function packRgba(r, g, b, a = 1) {
    return ((Math.round(clamp(a, 0, 1) * 255) << 24) | (b << 16) | (g << 8) | r) >>> 0;
}

/** Rounds up to the next 1, 2 or 5 times a power of ten (scale bars). */
export function niceNumber(value) {
    if (!(value > 0) || !Number.isFinite(value)) {
        return 1;
    }
    const exp = Math.floor(Math.log10(value));
    const base = 10 ** exp;
    const f = value / base;
    const nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
    return nice * base;
}

/** Rounds down to 1, 2 or 5 times a power of ten. */
export function niceFloor(value) {
    if (!(value > 0) || !Number.isFinite(value)) {
        return 1;
    }
    const exp = Math.floor(Math.log10(value));
    const base = 10 ** exp;
    const f = value / base;
    const nice = f >= 5 ? 5 : f >= 2 ? 2 : 1;
    return nice * base;
}

/** Tiny pub/sub. Handlers run synchronously; an exception in one is logged and does not stop the others. */
export class Emitter {
    #handlers = new Map();

    on(type, handler) {
        let set = this.#handlers.get(type);
        if (!set) {
            set = new Set();
            this.#handlers.set(type, set);
        }
        set.add(handler);
        return () => set.delete(handler);
    }

    emit(type, ...args) {
        const set = this.#handlers.get(type);
        if (!set) {
            return;
        }
        for (const handler of [...set]) {
            try {
                handler(...args);
            } catch (error) {
                console.error(`handler for '${type}' failed`, error);
            }
        }
    }
}

/**
 * Builds an element. `props`: class, text, id, title, type, value, checked, disabled, hidden, tabindex, role, for, name,
 * min, max, step, placeholder, aria (object of aria-* names without the prefix), data (dataset), on (event map),
 * style (CSS custom properties or properties through the CSSOM) and attrs (anything else).
 * Children may be nodes, strings, numbers or arrays of those; null and false are skipped.
 */
export function h(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props ?? {})) {
        if (value === undefined || value === null || value === false) {
            if (key === 'checked' || key === 'disabled' || key === 'hidden' || (key === 'spellcheck' && value === false)) {
                node[key] = false;
            }
            continue;
        }
        switch (key) {
            case 'class':
                node.className = value;
                break;
            case 'text':
                node.textContent = String(value);
                break;
            case 'checked':
            case 'disabled':
            case 'hidden':
            case 'value':
            case 'id':
            case 'title':
            case 'type':
            case 'name':
            case 'placeholder':
            case 'min':
            case 'max':
            case 'step':
            case 'rows':
            case 'spellcheck':
            case 'autocomplete':
                node[key] = value;
                break;
            case 'for':
                node.htmlFor = value;
                break;
            case 'tabindex':
                node.tabIndex = value;
                break;
            case 'role':
                node.setAttribute('role', value);
                break;
            case 'aria':
                for (const [name, v] of Object.entries(value)) {
                    node.setAttribute(`aria-${name}`, String(v));
                }
                break;
            case 'data':
                for (const [name, v] of Object.entries(value)) {
                    node.dataset[name] = String(v);
                }
                break;
            case 'on':
                for (const [name, fn] of Object.entries(value)) {
                    node.addEventListener(name, fn);
                }
                break;
            case 'style':
                for (const [name, v] of Object.entries(value)) {
                    if (name.startsWith('--')) {
                        node.style.setProperty(name, String(v));
                    } else {
                        node.style[name] = v;
                    }
                }
                break;
            case 'attrs':
                for (const [name, v] of Object.entries(value)) {
                    node.setAttribute(name, String(v));
                }
                break;
            default:
                node.setAttribute(key, String(value));
        }
    }
    appendChildren(node, children);
    return node;
}

function appendChildren(node, children) {
    for (const child of children) {
        if (child === null || child === undefined || child === false) {
            continue;
        }
        if (Array.isArray(child)) {
            appendChildren(node, child);
        } else if (child instanceof Node) {
            node.appendChild(child);
        } else {
            node.appendChild(document.createTextNode(String(child)));
        }
    }
}

/** Replaces a node's children with the given ones (null, undefined and false are skipped, arrays are flattened). */
export function fill(node, ...children) {
    node.replaceChildren();
    appendChildren(node, children);
    return node;
}

/** Removes every child of a node. */
export function clear(node) {
    while (node.firstChild) {
        node.removeChild(node.firstChild);
    }
    return node;
}
