// The client-file reader's public API and controller (design 0.5): the first-entry dialog, the stored folder choice, the
// re-allow banner, the settings panel and localTileProvider(), which answers Api.tile-shaped requests from the visitor's own
// WoW 1.12.1 folder. Everything is read in this browser, in Workers; nothing is ever sent anywhere.
//
//   showFirstEntryDialogIfNeeded() -> Promise<boolean>  true when the dialog was opened (a first visit)
//   openClientFilesDialog()        opens the dialog (Choose folder / Skip / Why?)
//   openClientFilesSettings()      opens the settings panel (Change folder / Forget folder / cache budget)
//   clientFilesStatus()            -> {state, folderName, build, version, warnings, cacheBytes, cacheBudgetBytes, archiveReads, cacheHits, error}
//   localTileProvider()            -> {tile(layer, map, x, y, signal) -> {state, detail, body}}
//
// States: none (no choice yet), skipped, needs-permission (a stored handle waits for one click), ready, error.
// Thread affinity: page thread only; parsing runs in the Workers of pool.js.

import { createFirstEntryDialog, createReallowBanner, createSettingsPanel, openFolderInput } from './dialog.js';
import { ClientFsError } from './limits.js';
import { WorkerPool, poolSizeFor } from './pool.js';
import { scanFileList, scanHandle } from './scope.js';
import { budgetSlider, formatBytes } from './settings.js';
import {
    BUDGET_KEY, HANDLE_KEY, LruCache, META_KEY, OpfsCache, chainKey, dbDelete, dbGet, dbPut, defaultCacheBudget,
} from './store.js';

export const ERROR_BLOCKED = 'Your browser blocked access - click Re-allow';

const STATES = ['Ok', 'Empty', 'Missing', 'Rejected', 'NotApplicable'];
const CACHEABLE = new Set(['Ok', 'Empty', 'Missing']);
const KEY_VERSION = 'tile/v1';

const model = {
    state: 'none',
    folderName: '',
    build: null,
    version: null,
    warnings: [],
    error: null,
    kind: null,
    handle: null,
    folder: null, // the live scan (File objects); null after a reload when only the cache is left
    archives: [], // [{name, size, lastModified}] of the chosen folder, persisted so cache keys survive a reload
    cacheHits: 0,
    budget: 0,
    quota: undefined,
    initialised: null,
};

const lru = new LruCache();
const opfs = new OpfsCache(() => model.budget);
const inflight = new Map();
let pool = null;
let dialog = null;
let banner = null;
let settings = null;
let waiters = [];
let declined = false; // Skip / Not now / closing the dialog this session: later cache misses resolve false without any UI
let generation = 0; // bumped by Forget folder so a pick that is still validating cannot store its folder afterwards

const spawn = () => new Worker(new URL('./worker.js', import.meta.url), { type: 'module' });
const getPool = () => (pool ??= new WorkerPool({ size: poolSizeFor(navigator.hardwareConcurrency), spawn }));

function resolveWaiters(value) {
    const list = waiters;
    waiters = [];
    for (const resolve of list) {
        resolve(value);
    }
}

// ---- initialisation ------------------------------------------------------------------------------------------------
async function readQuota() {
    try {
        return (await navigator.storage?.estimate?.())?.quota;
    } catch {
        return undefined;
    }
}

async function attach(handle) {
    const scan = await scanHandle(handle);
    if (!scan.hasData) {
        throw new ClientFsError('bad-file', 'The chosen folder no longer contains a Data folder.');
    }
    model.folder = scan;
    model.archives = scan.archives.map(({ name, size, lastModified }) => ({ name, size, lastModified }));
}

async function grant() {
    try {
        await attach(model.handle);
        model.state = 'ready';
        model.error = null;
    } catch (error) {
        model.state = 'error';
        model.error = error.message;
    }
}

function init() {
    model.initialised ??= (async () => {
        model.quota = await readQuota();
        const cap = defaultCacheBudget({ quota: model.quota });
        const stored = await dbGet('meta', BUDGET_KEY).catch(() => undefined);
        model.budget = Math.min(Number.isFinite(stored) ? stored : cap, Number.isFinite(model.quota) ? model.quota : Infinity);
        const meta = await dbGet('meta', META_KEY).catch(() => undefined);
        if (!meta) {
            return;
        }
        if (meta.choice === 'skipped') {
            model.state = 'skipped';
            return;
        }
        Object.assign(model, { kind: meta.kind, folderName: meta.folderName, build: meta.build, version: meta.version, warnings: meta.warnings ?? [], archives: meta.archives ?? [] });
        if (meta.kind !== 'handle') {
            model.state = 'ready';
            return;
        }
        const handle = await dbGet('handles', HANDLE_KEY).catch(() => undefined);
        if (!handle) {
            model.state = 'none';
            return;
        }
        model.handle = handle;
        // A handle that cannot even be queried (revoked, from another origin state) is treated like one that needs the user's click.
        const permission = await Promise.resolve().then(() => handle.queryPermission({ mode: 'read' })).catch(() => 'prompt');
        if (permission === 'granted') {
            await grant();
        } else {
            model.state = 'needs-permission';
        }
    })().catch(() => {
        // Never cache a rejection: whatever IndexedDB or the quota estimate threw, the visitor falls back to the first-entry dialog.
        if (model.state !== 'needs-permission' && model.state !== 'ready' && model.state !== 'error') {
            model.state = 'none';
        }
        model.budget ||= defaultCacheBudget({ quota: model.quota });
    });
    return forgetting === null ? model.initialised : Promise.all([model.initialised, forgetting]).then(() => undefined);
}

// ---- picking and validating ----------------------------------------------------------------------------------------
async function finishPick(scan, handle, started) {
    dialog?.setBusy(true);
    try {
        const result = await getPool().run('validate', { folder: scan }, { kind: 'tile' });
        if (started !== generation) {
            return;
        }
        if (!result.ok) {
            dialog?.setError(result.errors[0]);
            return;
        }
        const archives = scan.archives.map(({ name, size, lastModified }) => ({ name, size, lastModified }));
        const meta = { choice: 'folder', kind: scan.kind, folderName: scan.name, build: result.build, version: result.version, warnings: result.warnings, archives };
        if (handle) {
            await dbPut('handles', HANDLE_KEY, handle);
        } else {
            await dbDelete('handles', HANDLE_KEY);
        }
        await dbPut('meta', META_KEY, meta);
        Object.assign(model, { state: 'ready', error: null, kind: scan.kind, handle: handle ?? null, folder: scan, folderName: scan.name, build: result.build, version: result.version, warnings: result.warnings, archives });
        declined = false;
        closeDialog();
        resolveWaiters(true);
    } catch (error) {
        if (started === generation) {
            dialog?.setError(error instanceof ClientFsError ? error.message : `Could not read the folder: ${error?.message ?? error}`);
        }
    } finally {
        dialog?.setBusy(false);
    }
}

/** Runs inside the Choose folder click: the picker has to be the first thing awaited so the user gesture still counts. */
function pickFolder() {
    const started = generation;
    dialog?.setError(null);
    if (typeof window.showDirectoryPicker === 'function') {
        return (async () => {
            let handle;
            try {
                handle = await window.showDirectoryPicker({ id: 'wow', mode: 'read' });
            } catch (error) {
                if (error?.name !== 'AbortError') {
                    dialog?.setError(`The browser did not open the folder: ${error?.message ?? error}`);
                }
                return;
            }
            try {
                await finishPick(await scanHandle(handle), handle, started);
            } catch (error) {
                dialog?.setError(`Could not read the folder: ${error?.message ?? error}`);
            }
        })();
    }
    openFolderInput(dialog.backdrop, async (files) => {
        try {
            await finishPick(await scanFileList(files), null, started);
        } catch (error) {
            dialog?.setError(`Could not read the folder: ${error?.message ?? error}`);
        }
    });
    return Promise.resolve();
}

async function skip() {
    if (model.state === 'none') {
        await dbPut('meta', META_KEY, { choice: 'skipped' });
        model.state = 'skipped';
    }
    declined = true;
    closeDialog();
    resolveWaiters(false);
}

function closeDialog() {
    dialog?.close();
    dialog = null;
}

export function openClientFilesDialog() {
    declined = false; // an explicit request overrides an earlier decline
    if (dialog !== null) {
        return;
    }
    dialog = createFirstEntryDialog({
        onChoose: () => { pickFolder(); },
        onSkip: () => { skip(); },
        onDismiss: () => {
            declined = true;
            closeDialog();
            resolveWaiters(false);
        },
    });
    dialog.show();
}

function showBanner() {
    if (banner !== null) {
        return;
    }
    banner = createReallowBanner({
        onReallow: () => {
            // requestPermission needs the click's user activation, so it is the first call made.
            model.handle.requestPermission({ mode: 'read' }).then(async (permission) => {
                if (permission !== 'granted') {
                    banner?.setError(ERROR_BLOCKED);
                    return;
                }
                await grant();
                if (model.state === 'ready') {
                    declined = false;
                    banner?.close();
                    banner = null;
                    resolveWaiters(true);
                } else {
                    banner?.setError(model.error);
                }
            }, () => banner?.setError(ERROR_BLOCKED));
        },
        onLater: () => {
            declined = true;
            banner?.close();
            banner = null;
            resolveWaiters(false);
        },
    });
    banner.show();
}

export async function showFirstEntryDialogIfNeeded() {
    await init();
    if (model.state === 'none') {
        openClientFilesDialog();
        return true;
    }
    if (model.state === 'needs-permission') {
        showBanner();
    }
    return false;
}

// ---- settings ------------------------------------------------------------------------------------------------------
let forgetting = null; // the running Forget folder, awaited by init() so a status read never sees the half-deleted store

function forget() {
    const run = forgetNow().finally(() => {
        if (forgetting === run) {
            forgetting = null;
        }
    });
    forgetting = run;
    return run;
}

async function forgetNow() {
    // Everything that could still write to the cache is cut off before the first await: the generation bump makes a tile that is
    // already in flight drop its result instead of caching it, the pool is closed and the model no longer names a folder.
    generation++;
    declined = false;
    settings?.close();
    settings = null;
    closeDialog();
    resolveWaiters(false);
    pool?.close();
    pool = null;
    inflight.clear();
    Object.assign(model, { state: 'none', folderName: '', build: null, version: null, warnings: [], error: null, kind: null, handle: null, folder: null, archives: [], cacheHits: 0 });
    await dbDelete('handles', HANDLE_KEY);
    await dbDelete('meta', META_KEY);
    lru.clear();
    await opfs.clear();
}

export async function openClientFilesSettings() {
    await init();
    if (settings !== null) {
        return;
    }
    if (model.state === 'none' || model.state === 'skipped') {
        openClientFilesDialog();
        return;
    }
    const slider = budgetSlider({ budget: model.budget, quota: model.quota });
    const label = (bytes) => `${formatBytes(bytes)} (up to ${formatBytes(slider.max)})`;
    settings = createSettingsPanel({
        folderName: model.folderName,
        build: model.build,
        version: model.version,
        cacheText: formatBytes(await opfs.size()),
        budget: slider,
        budgetText: label(model.budget),
    }, {
        onChange: () => {
            settings?.close();
            settings = null;
            openClientFilesDialog();
            pickFolder();
        },
        onForget: () => { forget(); },
        onBudget: (bytes, budgetLabel) => {
            model.budget = Math.max(0, Math.min(bytes, model.quota ?? bytes));
            budgetLabel.textContent = label(model.budget);
            dbPut('meta', BUDGET_KEY, model.budget).then(() => opfs.enforce(model.budget)).catch(() => {});
        },
        onClose: () => {
            settings?.close();
            settings = null;
        },
    });
    settings.show();
}

// ---- status --------------------------------------------------------------------------------------------------------
export async function clientFilesStatus() {
    await init();
    return {
        state: model.state,
        folderName: model.folderName,
        build: model.build,
        version: model.version,
        warnings: [...model.warnings],
        cacheBytes: await opfs.size(),
        cacheBudgetBytes: model.budget,
        archiveReads: pool?.archiveReads ?? 0,
        cacheHits: model.cacheHits,
        error: model.error,
    };
}

// ---- tiles ---------------------------------------------------------------------------------------------------------
function envelope(parts) {
    const total = parts.reduce((n, p) => n + 4 + p.byteLength, 4);
    const out = new Uint8Array(total);
    const view = new DataView(out.buffer);
    view.setUint32(0, parts.length, true);
    let offset = 4;
    for (const part of parts) {
        view.setUint32(offset, part.byteLength, true);
        out.set(part, offset + 4);
        offset += 4 + part.byteLength;
    }
    return out.buffer;
}

const emptyResult = (state, detail) => ({ state, detail, body: envelope([]) });

/** state, detail and body in one Uint8Array: [state index u8][detail length u32][detail utf8][body]. */
function encodeResult(result) {
    const detail = new TextEncoder().encode(result.detail ?? '');
    const body = new Uint8Array(result.body);
    const out = new Uint8Array(5 + detail.length + body.length);
    out[0] = STATES.indexOf(result.state);
    new DataView(out.buffer).setUint32(1, detail.length, true);
    out.set(detail, 5);
    out.set(body, 5 + detail.length);
    return out;
}

function decodeResult(bytes) {
    const length = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(1, true);
    const detail = new TextDecoder().decode(bytes.subarray(5, 5 + length));
    const body = bytes.slice(5 + length).buffer;
    return { state: STATES[bytes[0]] ?? 'Rejected', detail, body };
}

function waitForFiles() {
    if (model.folder !== null) {
        return Promise.resolve(true);
    }
    if (declined) {
        return Promise.resolve(false);
    }
    if (model.state === 'needs-permission') {
        showBanner();
    } else {
        openClientFilesDialog();
    }
    return new Promise((resolve) => waiters.push(resolve));
}

async function buildOrLoad(layer, map, x, y) {
    const started = generation; // Forget folder bumps it; a result from before that must not be cached under the old folder's key
    const forgotten = () => started !== generation;
    const logical = `${KEY_VERSION}/${layer}/${map}/${x}/${y}`;
    const key = chainKey(logical, model.folder?.archives ?? model.archives);
    const hot = lru.get(key);
    if (hot !== undefined) {
        model.cacheHits++;
        return { state: hot.state, detail: hot.detail, body: hot.body.slice(0) };
    }
    const stored = await opfs.get(key).catch(() => null);
    if (forgotten()) {
        return emptyResult('NotApplicable', 'The client folder was forgotten.');
    }
    if (stored !== null) {
        const result = decodeResult(stored);
        lru.set(key, result, result.body.byteLength);
        model.cacheHits++;
        return { state: result.state, detail: result.detail, body: result.body.slice(0) };
    }

    if (!await waitForFiles()) {
        return emptyResult('NotApplicable', 'No client files are available.');
    }
    const folder = model.folder;
    const liveKey = chainKey(logical, folder.archives);
    let tile;
    try {
        tile = await getPool().run('tile', { archives: folder.archives, layer, map, x, y }, { kind: 'tile', key: liveKey });
    } catch (error) {
        if (forgotten()) {
            return emptyResult('NotApplicable', 'The client folder was forgotten.');
        }
        if (error instanceof ClientFsError) {
            return emptyResult('Rejected', error.message);
        }
        throw error;
    }
    if (forgotten()) {
        return emptyResult('NotApplicable', 'The client folder was forgotten.');
    }
    const result = { state: tile.state, detail: tile.detail, body: envelope(tile.parts) };
    if (CACHEABLE.has(result.state)) {
        lru.set(liveKey, result, result.body.byteLength);
        await opfs.set(liveKey, encodeResult(result)).catch(() => false);
    }
    return { state: result.state, detail: result.detail, body: result.body.slice(0) };
}

/** Rejects as soon as the signal aborts; the shared job keeps running for other callers and its late result is dropped. */
function raceAbort(job, signal) {
    if (!signal || typeof signal.addEventListener !== 'function') {
        return job;
    }
    return new Promise((resolve, reject) => {
        const onAbort = () => reject(signal.reason ?? new DOMException('The operation was aborted.', 'AbortError'));
        if (signal.aborted) {
            onAbort();
            return;
        }
        signal.addEventListener('abort', onAbort, { once: true });
        job.then(resolve, reject).finally(() => signal.removeEventListener('abort', onAbort));
    });
}

export function localTileProvider() {
    return {
        async tile(layer, map, x, y, signal) {
            signal?.throwIfAborted?.();
            await init();
            if (model.state === 'none' || model.state === 'skipped') {
                return emptyResult('NotApplicable', 'No client files were chosen.');
            }
            if (model.state === 'error') {
                return emptyResult('NotApplicable', model.error ?? 'The client folder cannot be read.');
            }
            const id = `${layer}/${map}/${x}/${y}`;
            let job = inflight.get(id);
            if (job === undefined) {
                job = buildOrLoad(layer, map, x, y).finally(() => inflight.delete(id));
                inflight.set(id, job);
            }
            const result = await raceAbort(job, signal);
            signal?.throwIfAborted?.();
            return { state: result.state, detail: result.detail, body: result.body.slice(0) };
        },
    };
}
