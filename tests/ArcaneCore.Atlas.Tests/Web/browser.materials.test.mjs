// Textured geometry in a real browser (lane A1): an ATG1 v2 quad on ref.m2.tex whose "generic" material samples a texture served
// from /api/bin/texture, probed at the four quadrant centres of a top-down view. Runs only when ARCANECORE_TEST_BROWSER names a
// Chrome or Edge executable (headless, software GL); otherwise the file is skipped with that reason, like browser.test.mjs. The
// S3TC test does not skip when the extension is missing: that is a failure, because a skipped DXT path looks like a passing one.

import assert from 'node:assert/strict';
import test from 'node:test';
import { tileOf, tileOrigin } from '../../../tools/ArcaneCore.Atlas/Web/js/tiles.js';
import { encodeChunkV2 } from './support/atg2-writer.mjs';
import { FORMAT, encodeTexture, probeTexture } from './support/atx1-writer.mjs';
import { Browser, browserFromEnvironment } from './support/browser.mjs';
import { decodePng } from './support/png.mjs';
import { CHART, INN, startScenario } from './support/scenario.mjs';

const executable = browserFromEnvironment();
const skip = executable ? false : 'ARCANECORE_TEST_BROWSER does not name a Chrome or Edge executable';
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const RED = [255, 0, 0];
const GREEN = [0, 255, 0];
const BLUE = [0, 0, 255];
const WHITE = [255, 255, 255];
const PROBE_KEY = 'client:textures\\probe.blp';
const MISSING_KEY = 'client:textures\\absent.blp';
const HALF = 15;
const VIEW_HALF = 30;

// One 4x4 DXT1 block: c0 = 0xF800 (red), c1 = 0x001F (blue), c0 > c1 so the other two are thirds: (170,0,85) and (85,0,170).
// Index rows (2 bits per texel, first texel in the low bits): [0,0,1,1] twice, then [2,2,3,3] twice.
const DXT_BLOCK = Uint8Array.from([0x00, 0xF8, 0x1F, 0x00, 0x50, 0x50, 0xFA, 0xFA]);
const DXT_EXPECTED = { topLeft: RED, topRight: BLUE, bottomLeft: [170, 0, 85], bottomRight: [85, 0, 170] };

function quadChunk(tile, origin, key, blend = 0, alphaRef = 0) {
    const [x0, y0, x1, y1] = [INN[0] - HALF, INN[1] - HALF, INN[0] + HALF, INN[1] + HALF];
    const p = (x, y) => [x - origin[0], y - origin[1], 57 - origin[2]];
    return encodeChunkV2({
        layer: 'ref.m2.tex',
        tile: [0, tile.x, tile.y],
        origin,
        positions: [...p(x0, y0), ...p(x1, y0), ...p(x1, y1), ...p(x0, y1)],
        triangles: [0, 1, 2, 0, 2, 3],
        triangleAttributes: [0, 0],
        normals: [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1],
        uv0: [0, 1, 1, 1, 1, 0, 0, 0],
        batches: [[0, 2, 0, 0]],
        materials: [{ kind: 'generic', blend, flags: 3, textures: [{ key, slot: 0, wrap: [0, 0] }], alphaRef }],
        attributes: { 0: {} },
        stats: {},
    });
}

function configure(key, { blend = 0, alphaRef = 0, dxt } = {}) {
    return (host) => {
        const tile = tileOf(INN[0], INN[1]);
        const origin = [...tileOrigin(tile.x, tile.y).slice(0, 2), 0];
        host.sourceLayers.add('ref.m2.tex');
        host.setTile('ref.m2.tex', 0, tile.x, tile.y, { state: 'Ok', parts: [quadChunk(tile, origin, key, blend, alphaRef)] });
        host.textureRequests = [];
        host.setBinary('texture', (q) => {
            host.textureRequests.push({ key: q.get('key'), fmt: q.get('fmt'), maxSize: q.get('maxSize') });
            if (q.get('key') === MISSING_KEY) {
                return { status: 404, type: 'application/json', body: JSON.stringify({ error: { code: 'not-found', message: 'no such texture' } }) };
            }
            if (q.get('fmt') === 'dxt') {
                return { body: encodeTexture({ format: FORMAT.DXT1, width: 4, height: 4, flags: dxt?.flags ?? 1, levels: [dxt?.block ?? DXT_BLOCK], meta: { key: q.get('key') } }) };
            }
            return { body: probeTexture(q.get('key')) };
        });
        for (const entry of Object.values(host.view.layers)) {
            entry.visible = false;
        }
        Object.assign(host.view.layers['ref.m2.tex'], { visible: true, mode: 'fill' }); // layers default to wire; textures need a fill
        host.view.compare = 'reference'; // the textured layers are Reference-view layers: the server view hides them
        host.view.camera = { target: [INN[0], INN[1], 57], distance: VIEW_HALF, yaw: 90, pitch: 90, fov: 55, mode: 'top' };
    };
}

async function stage(page) {
    const r = JSON.parse(await page.evaluate('JSON.stringify(document.querySelector(".stage").getBoundingClientRect())'));
    return { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) };
}

function pixel(image, x, y) {
    const i = (y * image.width + x) * 4;
    return [image.rgba[i], image.rgba[i + 1], image.rgba[i + 2]];
}

/** Colour at a point `east` and `north` yards from the view centre (orthographic: yards map straight to pixels). */
function probe(image, box, east, north) {
    const perYard = box.h / 2 / VIEW_HALF;
    return pixel(image, Math.round(box.x + box.w / 2 + east * perYard), Math.round(box.y + box.h / 2 - north * perYard));
}

function near(actual, expected, tolerance, what) {
    assert.ok(actual.every((c, i) => Math.abs(c - expected[i]) <= tolerance), `${what}: ${actual} is not within ${tolerance} of ${expected}`);
}

async function open(t, key, extraQuery = '') {
    const browser = await Browser.launch(executable);
    t.after(() => browser.close());
    const s = await startScenario(executable, { browser, extraQuery, configure: configure(key) });
    t.after(() => s.close());
    await s.page.waitFor('window.__atlas.app.stats.tiles.loaded >= 1 && window.__atlas.app.stats.triangles > 0', 'the textured tile');
    await s.page.waitFor('window.__atlas.app.stats.textures && window.__atlas.app.stats.textures.pending === 0', 'the texture requests to finish');
    await sleep(300);
    return s;
}

test('a textured quad in a real browser, RGBA path', { skip }, async (t) => {
    const s = await open(t, PROBE_KEY, '&texfmt=rgba');
    const { page, host } = s;
    const box = await stage(page);
    const image = decodePng(await page.screenshot());

    await t.test('each quadrant shows its texel', () => {
        near(probe(image, box, -7.5, 7.5), RED, 3, 'top-left');
        near(probe(image, box, 7.5, 7.5), GREEN, 3, 'top-right');
        near(probe(image, box, -7.5, -7.5), BLUE, 3, 'bottom-left');
        near(probe(image, box, 7.5, -7.5), WHITE, 3, 'bottom-right');
    });

    await t.test('outside the quad is the chart background', () => {
        near(probe(image, box, 25, 25), CHART, 3, 'outside');
    });

    await t.test('the host was asked for rgba, once, with the key the material named', () => {
        assert.deepEqual(host.textureRequests.map((r) => [r.key, r.fmt]), [[PROBE_KEY, 'rgba']]);
    });

    await t.test('a static camera does not refetch the texture', async () => {
        await sleep(800);
        assert.equal(host.textureRequests.length, 1);
    });

    await t.test('the renderer statistics carry the texture counters', async () => {
        const textures = JSON.parse(await page.evaluate('JSON.stringify(window.__atlas.app.stats.textures)'));
        for (const name of ['resident', 'bytes', 'pending', 'missing', 'fetches']) {
            assert.equal(typeof textures[name], 'number', name);
        }
        assert.equal(textures.resident, 1);
        assert.equal(textures.fetches, 1);
        assert.equal(textures.missing, 0);
        assert.equal(textures.bytes, 2 * 2 * 4 + 4);
    });

    await t.test('nothing was written to browser storage', async () => {
        assert.equal(await page.evaluate('JSON.stringify(Object.keys(localStorage))'), '[]');
        assert.equal(await page.evaluate('(async () => JSON.stringify(await caches.keys()))()'), '[]');
    });

    await t.test('the page logged no errors', () => {
        assert.deepEqual(page.problems.filter((p) => !p.includes('ERR_ABORTED')), []);
    });
});

test('a textured quad in a real browser, S3TC path', { skip }, async (t) => {
    const probeBrowser = await Browser.launch(executable);
    t.after(() => probeBrowser.close());
    const s = await startScenario(executable, { browser: probeBrowser, configure: configure(PROBE_KEY) });
    t.after(() => s.close());
    const { page, host } = s;
    const hasS3tc = await page.evaluate("Boolean(document.createElement('canvas').getContext('webgl2')?.getExtension('WEBGL_compressed_texture_s3tc'))");
    assert.equal(hasS3tc, true, 'this browser has no WEBGL_compressed_texture_s3tc, so the DXT path cannot be tested: that is a failure, not a skip');
    await page.waitFor('window.__atlas.app.stats.textures && window.__atlas.app.stats.textures.resident === 1', 'the DXT texture');
    await sleep(300);
    const box = await stage(page);
    const image = decodePng(await page.screenshot());

    await t.test('the host was asked for dxt', () => {
        assert.deepEqual(host.textureRequests.map((r) => r.fmt), ['dxt']);
    });

    await t.test('the four block colours come out of the compressed upload', () => {
        near(probe(image, box, -7.5, 7.5), DXT_EXPECTED.topLeft, 6, 'top-left');
        near(probe(image, box, 7.5, 7.5), DXT_EXPECTED.topRight, 6, 'top-right');
        near(probe(image, box, -7.5, -7.5), DXT_EXPECTED.bottomLeft, 6, 'bottom-left');
        near(probe(image, box, 7.5, -7.5), DXT_EXPECTED.bottomRight, 6, 'bottom-right');
    });

    await t.test('the compressed texture is counted at its compressed size', async () => {
        const textures = JSON.parse(await page.evaluate('JSON.stringify(window.__atlas.app.stats.textures)'));
        assert.equal(textures.bytes, 8);
    });
});

// c0 = 0x001F (blue) <= c1 = 0xF800 (red): three colours plus index 3, which is transparent black only in an RGBA DXT1 upload.
// Every texel is index 3. With hasAlpha clear the block is opaque (black), so alpha-key must not discard it.
test('an opaque DXT1 texture is uploaded as RGB: index 3 stays black under alpha-key, S3TC path', { skip }, async (t) => {
    const block = Uint8Array.from([0x1F, 0x00, 0x00, 0xF8, 0xFF, 0xFF, 0xFF, 0xFF]);
    const browser = await Browser.launch(executable);
    t.after(() => browser.close());
    const s = await startScenario(executable, { browser, configure: configure(PROBE_KEY, { blend: 1, alphaRef: 0.5, dxt: { flags: 0, block } }) });
    t.after(() => s.close());
    const { page } = s;
    const hasS3tc = await page.evaluate("Boolean(document.createElement('canvas').getContext('webgl2')?.getExtension('WEBGL_compressed_texture_s3tc'))");
    assert.equal(hasS3tc, true, 'this browser has no WEBGL_compressed_texture_s3tc, so the DXT path cannot be tested: that is a failure, not a skip');
    await page.waitFor('window.__atlas.app.stats.textures && window.__atlas.app.stats.textures.resident === 1', 'the DXT texture');
    await sleep(300);
    const box = await stage(page);
    const image = decodePng(await page.screenshot());

    near(probe(image, box, -7.5, 7.5), [0, 0, 0], 6, 'the quad (top-left)');
    near(probe(image, box, 7.5, -7.5), [0, 0, 0], 6, 'the quad (bottom-right)');
});

test('a missing texture draws the placeholder once and is counted', { skip }, async (t) => {
    const s = await open(t, MISSING_KEY, '&texfmt=rgba');
    const { page, host } = s;
    const box = await stage(page);
    const image = decodePng(await page.screenshot());

    await t.test('the quad is drawn with something, not left as a hole', () => {
        const centre = probe(image, box, 0, 0);
        assert.ok(centre.some((c, i) => Math.abs(c - CHART[i]) > 20), `the centre is still the background: ${centre}`);
    });

    await t.test('the placeholder is the magenta and black checker (8 x 8 cells over the 30 yard quad, probed at cell centres)', () => {
        const seen = new Set();
        for (const [east, north] of [[-13.125, 13.125], [-9.375, 13.125], [-13.125, 9.375], [-9.375, 9.375]]) {
            const [r, g, b] = probe(image, box, east, north);
            seen.add(r > 200 && g < 60 && b > 200 ? 'magenta' : r < 60 && g < 60 && b < 60 ? 'black' : 'other');
        }
        assert.ok(seen.has('magenta') && seen.has('black'), `checker colours seen: ${[...seen]}`);
    });

    await t.test('texturesMissing is one and the host was asked once', async () => {
        const textures = JSON.parse(await page.evaluate('JSON.stringify(window.__atlas.app.stats.textures)'));
        assert.equal(textures.missing, 1);
        await sleep(600);
        assert.equal(host.textureRequests.filter((r) => r.key === MISSING_KEY).length, 1);
    });
});
