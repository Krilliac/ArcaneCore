// The module Worker of the client-file reader. Every parse of the visitor's files happens here, never on the page thread:
//   ping      -> 'pong'
//   validate  {folder}                         -> validateClientFolder result
//   tile      {archives, layer, map, x, y}     -> {state, detail, parts: [Uint8Array]} (Api.tile's state names)
// Requests are {id, op, ...payload}; replies are {id, ok: true, result, reads} or {id, ok: false, error: {code, message}}.
// `reads` (present only when above 0) is the number of archive range reads the job made (the page's "archive reads" counter).
// The Worker is started from /app/js/clientfs/worker.js, which the page's script-src 'self' (and the public listener's
// worker-src 'self') allows; it fetches nothing and posts nothing but results back to the page.

import { buildTile } from './adt.js';
import { ClientFsError } from './limits.js';
import { createChain, openArchive, stats } from './mpq.js';
import { validateClientFolder } from './validate.js';

/** Opened archives, kept for the life of the Worker: tables are parsed once per (name, size, lastModified). */
const opened = new Map();
const chains = new Map();

async function chainOf(archives) {
    const id = archives.map((a) => `${a.name}|${a.size}|${a.lastModified}`).join('&');
    let chain = chains.get(id);
    if (chain === undefined) {
        const list = [];
        for (const entry of archives) {
            const key = `${entry.name}|${entry.size}|${entry.lastModified}`;
            let archive = opened.get(key);
            if (archive === undefined) {
                archive = await openArchive(entry.blob, { name: entry.name });
                opened.set(key, archive);
            }
            list.push(archive);
        }
        chain = createChain(list);
        chains.set(id, chain);
    }
    return chain;
}

const handlers = {
    ping: () => ({ value: 'pong' }),
    validate: async ({ folder }) => ({ value: await validateClientFolder(folder) }),
    tile: async ({ archives, layer, map, x, y }) => {
        const chain = await chainOf(archives);
        const tile = await buildTile(chain, layer, map, x, y);
        return { value: tile, transfer: tile.parts.map((p) => p.buffer) };
    },
};

self.onmessage = async (event) => {
    const { id, op, ...payload } = event.data ?? {};
    const before = stats.reads;
    try {
        const handler = handlers[op];
        if (handler === undefined) {
            throw new ClientFsError('bad-file', `Unknown Worker operation '${op}'.`);
        }
        const { value, transfer = [] } = await handler(payload);
        const reads = stats.reads - before;
        self.postMessage(reads > 0 ? { id, ok: true, result: value, reads } : { id, ok: true, result: value }, transfer);
    } catch (error) {
        const code = error instanceof ClientFsError ? error.code : 'bad-file';
        const message = error instanceof ClientFsError ? error.message : `${error?.name ?? 'Error'}: ${error?.message ?? error}`;
        self.postMessage({ id, ok: false, error: { code, message }, reads: stats.reads - before });
    }
};
