// The empty and error states of the viewport: what is wrong or missing, and what to do about it. Pure: it reads state and
// returns messages, so the wording is testable and a missing file is never silently drawn as empty space.

import { mapFileName, navTileFileName, vmapTileFileName } from './tiles.js';

/** The server data file that holds a layer's tile, or null for layers with no single file (client reads, diffs, live). */
export function layerFile(layerId, map, x, y) {
    if (layerId === 'tiles' || layerId === 'terrain' || layerId.startsWith('terrain.') || layerId === 'area' || layerId === 'liquid') {
        return { kind: 'map', name: mapFileName(map, x, y) };
    }
    if (layerId.startsWith('vmap.')) {
        return { kind: 'vmap', name: vmapTileFileName(map, x, y) };
    }
    if (layerId.startsWith('nav.') || layerId.startsWith('ref.nav.')) {
        return { kind: 'mmap', name: navTileFileName(map, x, y) };
    }
    return null;
}

export const FILE_KINDS = [
    { kind: 'map', label: 'terrain', layers: ['terrain', 'liquid', 'area'] },
    { kind: 'vmap', label: 'vmap', layers: ['vmap.wmo', 'vmap.m2', 'vmap.bounds'] },
    { kind: 'mmap', label: 'navmesh', layers: ['nav.poly', 'nav.detail', 'ref.nav.poly'] },
];

function plural(n, word) {
    return `${n} ${word}${n === 1 ? '' : 's'}`;
}

/**
 * @param {object} s
 * @param {{ok: boolean, message: string}} s.webgl
 * @param {{state: string}} s.connection
 * @param {object} s.status         normalizeStatus() result
 * @param {Array} s.resolved        resolveLayers() result
 * @param {number} s.map
 * @param {{x: number, y: number}|null} s.focusTile
 * @param {(layer: string, x: number, y: number) => ({state: string, detail: string}|null)} s.statusOf
 * @returns {Array<{id: string, level: 'error'|'warn'|'info', text: string, hint: string}>}
 */
export function computeNotices({ webgl, connection, status, resolved, map, focusTile, statusOf }) {
    const out = [];
    if (!webgl.ok) {
        out.push({
            id: 'webgl', level: 'error',
            text: webgl.message || 'WebGL2 is not available in this browser.',
            hint: 'Use a current Edge or Chrome with hardware acceleration on. The panels still work; captures need the GPU renderer or source=headless.',
        });
    }
    if (connection.state === 'unauthorized') {
        out.push({
            id: 'auth', level: 'error',
            text: 'The Atlas host refused this page’s token.',
            hint: 'Open the link printed by "arcane-atlas serve" again (or start it with --open) so the page gets a fresh token.',
        });
    } else if (connection.state === 'closed' || connection.state === 'connecting') {
        out.push({
            id: 'offline', level: 'warn',
            text: connection.state === 'connecting' ? 'Connecting to the Atlas host.' : 'The connection to the Atlas host dropped. Reconnecting.',
            hint: 'If this stays, check that "arcane-atlas serve" is still running.',
        });
    }
    for (const [i, problem] of status.problems.entries()) {
        out.push({ id: `problem-${i}`, level: 'warn', text: problem, hint: 'Fix the path and restart "arcane-atlas serve".' });
    }

    const drawn = resolved.filter((l) => l.visible && !l.live && l.info.tiled !== false);
    if (status.loaded) {
        if (status.terrainData === null && drawn.some((l) => l.info.view !== 'Client')) {
            out.push({
                id: 'no-data', level: 'warn',
                text: 'No data directory is configured, so server layers have nothing to read.',
                hint: 'Start "arcane-atlas serve --data <dir>" with the folder that holds maps, vmaps and mmaps.',
            });
        }
        if (status.clientRoot === null && drawn.some((l) => l.info.view === 'Client')) {
            out.push({
                id: 'no-client', level: 'warn',
                text: 'No client directory is configured, so the reference layers have nothing to read.',
                hint: 'Start "arcane-atlas serve --client <dir>" with the 1.12.1 client folder (the one with Data\\*.MPQ).',
            });
        }
    }

    const anyOn = resolved.some((l) => l.style.visible && !l.live);
    const hidden = resolved.filter((l) => l.hiddenByMode && !l.live);
    if (!anyOn && !resolved.some((l) => l.visible && l.live)) {
        out.push({ id: 'no-layers', level: 'info', text: 'No layers are on.', hint: 'Tick a layer in the Layers panel, or press a number key to toggle a group.' });
    } else if (drawn.length === 0 && hidden.length > 0) {
        out.push({
            id: 'compare-hidden', level: 'info',
            text: `${hidden.length === 1 ? 'The layer you ticked belongs' : 'The layers you ticked belong'} to a data view the current compare mode hides.`,
            hint: 'Server layers show in Server and Split; reference layers in Reference and Split; differences in Diff.',
        });
    }

    if (focusTile && status.loaded !== false) {
        const missing = [];
        for (const layer of drawn) {
            const s = statusOf(layer.id, focusTile.x, focusTile.y);
            if (!s) {
                continue;
            }
            const file = layerFile(layer.id, map, focusTile.x, focusTile.y);
            if (s.state === 'Rejected' || s.state === 'Error') {
                out.push({
                    id: `rejected-${layer.id}`, level: 'error',
                    text: `${layer.info.title} for tile ${focusTile.x}, ${focusTile.y} was ${s.state === 'Rejected' ? 'rejected' : 'not loaded'}${s.detail ? `: ${s.detail}` : '.'}`,
                    hint: file ? `File: ${file.name}` : '',
                });
            } else if (s.state === 'Missing') {
                missing.push(file ? `${layer.info.title} (${file.name})` : layer.info.title);
            }
        }
        if (missing.length > 0) {
            out.push({
                id: 'missing',
                level: 'info',
                text: `No data for tile ${focusTile.x}, ${focusTile.y} of map ${map}: ${missing.slice(0, 3).join(', ')}${missing.length > 3 ? ` and ${plural(missing.length - 3, 'more layer')}` : ''}.`,
                hint: 'Missing means no file exists for the tile. It is not empty space; the tile is simply not drawn.',
            });
        }
    }
    return out;
}
