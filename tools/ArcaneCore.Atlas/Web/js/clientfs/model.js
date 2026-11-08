// Reads one model file of the visitor's archives and parses it with the M2 or WMO reader. Runs in the reader's Worker.
//
// Kinds: 'm2' (parseM2), 'm2-animation' (parseM2Animation), 'wmo-root' (parseWmoRoot), 'wmo-group' (parseWmoGroup). A path is an archive path with either separator.

import { badFile } from './limits.js';
import { parseM2, parseM2Animation } from './m2.js';
import { parseWmoGroup, parseWmoRoot } from './wmo.js';

export const MODEL_PARSERS = Object.freeze({ m2: parseM2, 'm2-animation': parseM2Animation, 'wmo-root': parseWmoRoot, 'wmo-group': parseWmoGroup });

/**
 * @param {{read: (path: string) => Promise<Uint8Array|null>}} chain the mounted archive chain
 * @returns the parsed file; throws ClientFsError 'bad-file' for an unknown kind, a missing file or a damaged one
 */
export async function readModel(chain, kind, path) {
    const parse = Object.hasOwn(MODEL_PARSERS, kind) ? MODEL_PARSERS[kind] : undefined;
    if (parse === undefined) {
        throw badFile(`'${kind}' is not a model kind (m2, m2-animation, wmo-root, wmo-group).`);
    }
    if (typeof path !== 'string' || path === '') {
        throw badFile('A model path is required.');
    }
    const bytes = await chain.read(path);
    if (bytes === null) {
        throw badFile(`${path} is not in the client archives.`);
    }
    return parse(bytes);
}

/** The distinct ArrayBuffers behind the typed arrays of a parsed model, so they move to the page instead of being copied. */
export function transferables(value) {
    const buffers = new Set();
    const walk = (v) => {
        if (ArrayBuffer.isView(v)) {
            buffers.add(v.buffer);
        } else if (Array.isArray(v)) {
            v.forEach(walk);
        } else if (v !== null && typeof v === 'object') {
            Object.values(v).forEach(walk);
        }
    };
    walk(value);
    return [...buffers];
}
