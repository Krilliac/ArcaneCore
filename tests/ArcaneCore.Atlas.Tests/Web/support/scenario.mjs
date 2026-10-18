// The browser tests' shared world: a mock host serving synthetic terrain and a building at Goldshire's inn coordinates
// (real-scale numbers, so float32 precision problems would show), with the UI loaded in headless Chromium.

import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { tileOf, tileOrigin } from '../../../../tools/ArcaneCore.Atlas/Web/js/tiles.js';
import { boxChunk, encodeChunk, gridChunk } from './atg1-writer.mjs';
import { Browser } from './browser.mjs';
import { MockHost } from './mockhost.mjs';

export const WEB_ROOT = fileURLToPath(new URL('../../../../tools/ArcaneCore.Atlas/Web/', import.meta.url));
export const INN = [-9462.66, 16.1915, 57.0459];
export const CHART = [0x10, 0x1a, 0x21];

/** Terrain surface used by the synthetic tile; tests compare picked heights with it. */
export const terrainHeight = (x, y) => 57 + 5 * Math.sin((x + 9462) / 40) * Math.cos((y - 16) / 50);

export function buildHost(token) {
    const host = new MockHost({ webRoot: WEB_ROOT, token, layers: ['tiles', 'terrain', 'vmap.wmo', 'liquid', 'ref.terrain'] });
    const tile = tileOf(INN[0], INN[1]);
    const origin = [...tileOrigin(tile.x, tile.y).slice(0, 2), 0];
    const terrain = gridChunk({
        layer: 'terrain', tile: [0, tile.x, tile.y], origin, cx: INN[0], cy: INN[1], size: 300, n: 30, height: terrainHeight,
        attribute: 0, attributes: { 0: { name: 'Terrain', area: 12 } }, stats: { cells: 900 },
    });
    host.setTile('terrain', 0, tile.x, tile.y, { state: 'Ok', parts: [encodeChunk(terrain)] });
    const building = boxChunk({
        layer: 'vmap.wmo', tile: [0, tile.x, tile.y], origin, min: [INN[0] - 14, INN[1] - 10, 57], max: [INN[0] + 14, INN[1] + 10, 71],
        attribute: 1, attributes: { 1: { colorKey: 'indoor', name: 'Lion\'s Pride Inn' } },
    });
    host.setTile('vmap.wmo', 0, tile.x, tile.y, { state: 'Ok', parts: [encodeChunk(building)] });
    const reference = gridChunk({
        layer: 'ref.terrain', tile: [0, tile.x, tile.y], origin, cx: INN[0], cy: INN[1], size: 300, n: 30, height: (x, y) => terrainHeight(x, y) + 1.5,
        attribute: 0, attributes: { 0: {} },
    });
    host.setTile('ref.terrain', 0, tile.x, tile.y, { state: 'Ok', parts: [encodeChunk(reference)] });
    host.setTile('liquid', 0, tile.x, tile.y, { state: 'Empty', detail: 'No liquid in this tile' });
    host.setTile('tiles', 0, tile.x, tile.y, { state: 'Rejected', detail: 'Synthetic rejection for the empty-state test' });
    installQueries(host, tile);
    return { host, tile, origin };
}

/** Starts the mock host and the browser, opens the UI (with the token in the fragment) and waits until it is connected. */
export async function startScenario(executable, { extraQuery = '', configure, browser: shared = null, waitConnected = true } = {}) {
    const token = crypto.randomBytes(24).toString('base64url');
    const { host, tile, origin } = buildHost(token);
    configure?.(host);
    await host.start();
    let browser = null;
    try {
        browser = shared ?? await Browser.launch(executable);
        const page = await browser.newPage(`${host.origin}/?debug${extraQuery}#t=${token}`);
        if (waitConnected) {
            await page.waitFor("window.__atlas?.app?.connection?.state === 'open'", 'the UI to connect');
        }
        return {
            host, browser, page, token, tile, origin,
            async close() {
                if (!shared) {
                    await browser.close();
                }
                await host.stop();
            },
        };
    } catch (error) {
        if (!shared) {
            await browser?.close();
        }
        await host.stop();
        throw error;
    }
}

/** Marches a ray down to the synthetic terrain surface: the answer query.pick would give, or null. */
export function rayToTerrain(origin, direction, maxDistance = 4000) {
    const above = (t) => {
        const x = origin[0] + direction[0] * t;
        const y = origin[1] + direction[1] * t;
        const z = origin[2] + direction[2] * t;
        return z - terrainHeight(x, y);
    };
    let previous = 0;
    for (let t = 0.5; t <= maxDistance; t += 0.5) {
        if (above(t) <= 0 && above(previous) > 0) {
            let lo = previous;
            let hi = t;
            for (let i = 0; i < 40; i++) {
                const mid = (lo + hi) / 2;
                if (above(mid) > 0) {
                    lo = mid;
                } else {
                    hi = mid;
                }
            }
            return { distance: hi, point: [0, 1, 2].map((a) => origin[a] + direction[a] * hi) };
        }
        previous = t;
    }
    return null;
}

function installQueries(host, tile) {
    host.handle('query.pick', (args) => {
        const hit = args.ray ? rayToTerrain(args.ray.origin, args.ray.direction) : null;
        if (!hit) {
            return { hit: null };
        }
        return {
            layer: 'terrain', tile: [0, tile.x, tile.y], distance: hit.distance, point: hit.point, primitive: 'triangle', attribute: 0,
            details: { name: 'Terrain', area: 12, zone: { id: 12, name: 'Elwynn Forest' } },
        };
    });
    const height = (shift, label) => (args) => {
        const [x, y, z] = args.at;
        const surface = terrainHeight(x, y) + shift;
        return {
            terrain: { z: surface, hole: false, noHeight: false },
            collision: { z: surface },
            files: { map: 'mock.map' },
            overlay: {
                points: [{ at: [x, y, surface], label: `${label} z ${surface.toFixed(2)}` }],
                polylines: [{ pts: [[x, y, z - 4], [x, y, z + 4]], label }],
            },
        };
    };
    host.handle('query.height', height(0, 'server'));
    host.handle('ref.query.height', height(1.5, 'reference'));
    host.handle('query.los', (args) => ({
        spell: { clear: true },
        movement: { clear: false, hit: [(args.a[0] + args.b[0]) / 2, (args.a[1] + args.b[1]) / 2, args.a[2]] },
        overlay: { segments: [{ from: args.a, to: args.b, label: 'los' }] },
    }));
}
