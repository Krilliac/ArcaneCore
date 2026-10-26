// The UI in a real browser against a mock host: rendering, picking, navigation, view sync, probes, the palette, live
// data, capture, and the empty and error states. Runs only when ARCANECORE_TEST_BROWSER names a Chrome or Edge
// executable (headless, software GL); otherwise the whole file is skipped with that reason. Synthetic data only.

import assert from 'node:assert/strict';
import test from 'node:test';
import { Browser, browserFromEnvironment } from './support/browser.mjs';
import { CHART, INN, startScenario, terrainHeight } from './support/scenario.mjs';
import { countNear, countNot, decodePng, isPng } from './support/png.mjs';

const executable = browserFromEnvironment();
const skip = executable ? false : 'ARCANECORE_TEST_BROWSER does not name a Chrome or Edge executable';
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const TERRAIN = [0xB9, 0xA7, 0x7A];
const REFERENCE = [0x8F, 0xA9, 0xB8];
const quiet = (page) => page.problems.filter((p) => !p.includes('ERR_ABORTED'));

async function stageBox(page) {
    const r = JSON.parse(await page.evaluate('JSON.stringify(document.querySelector(".stage").getBoundingClientRect())'));
    return { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) };
}

async function click(page, x, y) {
    await page.mouse('mouseMoved', x, y);
    await page.mouse('mousePressed', x, y, { button: 'left', buttons: 1, clickCount: 1 });
    await page.mouse('mouseReleased', x, y, { button: 'left', buttons: 0, clickCount: 1 });
}

async function shot(page) {
    return decodePng(await page.screenshot());
}

test('the UI in a real browser', { skip }, async (t) => {
    const browser = await Browser.launch(executable);
    t.after(() => browser.close());
    const s = await startScenario(executable, { browser });
    t.after(() => s.close());
    const { page, host } = s;
    const ev = (expression) => page.evaluate(expression);
    await page.waitFor('window.__atlas.app.stats.tiles.loaded >= 2 && window.__atlas.app.stats.triangles > 0', 'the synthetic tiles to load');
    const box = await stageBox(page);

    await t.test('the token leaves the URL fragment and every API call carries it', async () => {
        assert.equal(await ev('location.hash'), '');
        assert.equal(await ev("sessionStorage.getItem('atlas.session')"), s.token);
        const api = host.requests.filter((r) => r.path.startsWith('/api/') && !r.path.startsWith('/api/ws'));
        assert.ok(api.length > 5, `only ${api.length} API requests seen`);
        assert.deepEqual(api.filter((r) => !r.authorised), []);
        assert.ok(!host.requests.some((r) => r.path.includes(s.token)), 'the token appeared in a request line');
    });

    await t.test('the first frame draws terrain and the building with hidden lines', async () => {
        await sleep(500);
        const image = await shot(page);
        assert.ok(countNot(image, CHART, 8, box) > 3000, 'the viewport is blank');
        assert.ok(countNear(image, TERRAIN, 60, box) > 400, 'no terrain wireframe colour found');
        const stats = JSON.parse(await ev('JSON.stringify(window.__atlas.app.stats)'));
        assert.equal(stats.tiles.loaded, 2);
        assert.ok(stats.tiles.missing >= 8);
        assert.equal(stats.tiles.rejected, 1);
        assert.equal(stats.triangles, 1812);
    });

    await t.test('the cursor reads the exact surface under it', async () => {
        const x = box.x + box.w / 2 + 60;
        const y = box.y + box.h * 0.75;
        await page.mouse('mouseMoved', x, y);
        await page.waitFor('window.__atlas.app.hover && !window.__atlas.app.hover.estimated', 'a surface hit under the cursor');
        const hover = JSON.parse(await ev('JSON.stringify(window.__atlas.app.hover)'));
        assert.ok(Math.abs(hover.point[2] - terrainHeight(hover.point[0], hover.point[1])) < 0.3, `z ${hover.point[2]} is off the surface`);
        assert.equal(hover.layerId, 'terrain');
        const text = await ev("document.querySelector('.hud-readout').textContent");
        assert.match(text, /tile 49, 3[12]/);
        assert.match(text, /\.map/);
    });

    await t.test('a click asks the host, shows the hit and fills the inspector', async () => {
        const x = box.x + box.w / 2 - 100;
        const y = box.y + box.h * 0.7;
        host.commands.length = 0;
        await click(page, x, y);
        await page.waitFor('window.__atlas.app.selection.hit', 'the pick answer');
        const pick = host.commands.find((c) => c.name === 'query.pick');
        assert.ok(pick, 'query.pick was not sent');
        assert.equal(pick.args.ray.origin.length, 3);
        assert.ok(Math.abs(Math.hypot(...pick.args.ray.direction) - 1) < 1e-9);
        const selection = JSON.parse(await ev('JSON.stringify(window.__atlas.app.selection)'));
        assert.equal(selection.hit.layer, 'terrain');
        assert.ok(Math.abs(selection.point[2] - terrainHeight(selection.point[0], selection.point[1])) < 0.01);
        const inspector = await ev("document.querySelector('.inspector').textContent");
        assert.match(inspector, /Elwynn Forest/);
        assert.match(inspector, /Tile status/);
    });

    await t.test('orbit, wheel and the view stay in step with the host', async () => {
        const x = box.x + box.w / 2;
        const y = box.y + box.h / 2;
        const before = JSON.parse(await ev('JSON.stringify(window.__atlas.app.view.camera)'));
        await page.mouse('mouseMoved', x, y);
        await page.mouse('mousePressed', x, y, { button: 'left', buttons: 1, clickCount: 1 });
        for (let i = 1; i <= 8; i++) {
            await page.mouse('mouseMoved', x + i * 10, y + i * 4, { button: 'left', buttons: 1 });
        }
        await page.mouse('mouseReleased', x + 80, y + 32, { button: 'left', buttons: 0, clickCount: 1 });
        const after = JSON.parse(await ev('JSON.stringify(window.__atlas.app.view.camera)'));
        assert.ok(after.yaw < before.yaw, 'dragging right should turn the yaw down');
        assert.ok(after.pitch > before.pitch, 'dragging down should raise the pitch');
        await page.waitFor(`Math.abs(window.__atlas.app.view.camera.yaw - ${after.yaw}) < 1e-9`, 'the local camera');
        await sleep(400);
        assert.ok(Math.abs(host.view.camera.yaw - after.yaw) < 1e-9, 'the host never received the final camera');
        const viewSets = host.commands.filter((c) => c.name === 'view.set').length;
        await page.mouse('mouseWheel', x, y, { deltaY: -240 });
        await page.waitFor(`window.__atlas.app.view.camera.distance < ${after.distance}`, 'the zoom');
        assert.ok(host.commands.filter((c) => c.name === 'view.set').length >= viewSets);
    });

    await t.test('a view event from another origin is applied, the UI\'s own echo is not', async () => {
        await page.waitFor('!window.__atlas.app.interacting', 'the wheel gesture to end');
        const connection = await ev('window.__atlas.app.connection.id');
        const version = host.viewVersion + 1;
        const camera = { ...host.view.camera, target: [-9400, 40, 70], distance: 222 };
        host.view = { ...host.view, camera };
        host.viewVersion = version;
        host.broadcast('view', { version, origin: 'ctl', view: host.view });
        await page.waitFor('window.__atlas.app.view.camera.distance === 222', 'the remote camera');
        const echo = { ...host.view.camera, distance: 333 };
        host.broadcast('view', { version: version + 1, origin: `ui:${connection}`, view: { ...host.view, camera: echo } });
        await sleep(300);
        assert.equal(await ev('window.__atlas.app.view.camera.distance'), 222, 'the UI applied its own echo');
        host.viewVersion = version + 1;
    });

    await t.test('keyboard: T toggles top-down, digits toggle layer groups, / focuses go-to', async () => {
        await ev('document.querySelector(".stage").focus()');
        await page.press('t');
        await page.waitFor("window.__atlas.app.view.camera.mode === 'top'", 'top mode');
        await sleep(200);
        const top = await shot(page);
        assert.ok(countNear(top, TERRAIN, 60, box) > 300, 'nothing drawn in the top-down view');
        await page.press('t');
        await page.waitFor("window.__atlas.app.view.camera.mode === 'orbit'", 'orbit mode again');
        await page.press('4');
        await page.waitFor("window.__atlas.app.view.layers['vmap.wmo'].visible === false", 'the Collision group to hide');
        await page.press('4');
        await page.waitFor("window.__atlas.app.view.layers['vmap.wmo'].visible === true", 'the Collision group to show');
        await page.press('/');
        assert.equal(await ev("document.activeElement.classList.contains('goto-input')"), true);
        await page.press('Escape');
        assert.notEqual(await ev("document.activeElement.classList.contains('goto-input')"), true);
    });

    await t.test('go-to accepts "map x y z" and reports bad input', async () => {
        await ev("document.querySelector('.goto-input').focus()");
        await page.send('Input.insertText', { text: 'banana' });
        await page.press('Enter');
        assert.match(await ev("document.querySelector('.goto-message').textContent"), /x, y, z/);
        await ev("document.querySelector('.goto-input').value = ''");
        await page.send('Input.insertText', { text: '0 -9462.66 16.1915 57.04' });
        await page.press('Enter');
        await page.waitFor('Math.abs(window.__atlas.app.view.camera.target[0] + 9462.66) < 1e-9', 'the camera to jump');
        assert.equal(await ev("document.querySelector('.goto-message').hidden"), true);
        await ev("document.querySelector('.stage').focus()");
    });

    await t.test('fly mode: W moves the eye along the look direction', async () => {
        await ev("document.querySelector('.segment[data-value=\"fly\"]').click()");
        await page.waitFor("window.__atlas.app.view.camera.mode === 'fly'", 'fly mode');
        const before = JSON.parse(await ev('JSON.stringify(window.__atlas.app.view.camera.target)'));
        await ev('document.querySelector(".stage").focus()');
        await page.key('keyDown', 'w', { text: 'w' });
        await sleep(600);
        await page.key('keyUp', 'w');
        const after = JSON.parse(await ev('JSON.stringify(window.__atlas.app.view.camera.target)'));
        assert.ok(Math.hypot(after[0] - before[0], after[1] - before[1], after[2] - before[2]) > 5, 'the eye did not move');
        await ev("document.querySelector('.segment[data-value=\"orbit\"]').click()");
        await page.waitFor("window.__atlas.app.view.camera.mode === 'orbit'", 'orbit mode');
    });

    await t.test('split compare draws server on the left and reference on the right', async () => {
        await ev("window.__atlas.app.patchView({ compare: 'split', layers: { 'ref.terrain': { visible: true } } }, { immediate: true })");
        await page.waitFor("window.__atlas.app.scene.chunksFor('ref.terrain').length > 0", 'the reference tile');
        await sleep(500);
        const image = await shot(page);
        // Rows below the notices and above the readout and scale bar, so HUD text cannot colour the count.
        const rows = { y: box.y + 130, h: box.h - 130 - 150 };
        const left = { x: box.x + 10, w: Math.floor(box.w / 2) - 40, ...rows };
        const right = { x: box.x + Math.floor(box.w / 2) + 20, w: Math.floor(box.w / 2) - 40, ...rows };
        assert.ok(countNear(image, TERRAIN, 40, left) > 300, 'no server terrain on the left');
        assert.equal(countNear(image, REFERENCE, 25, left), 0, 'reference colours leaked into the left half');
        assert.ok(countNear(image, REFERENCE, 40, right) > 300, 'no reference terrain on the right');
        assert.equal(countNear(image, TERRAIN, 25, right), 0, 'server colours leaked into the right half');
        assert.equal(await ev("document.querySelector('.hud-divider').hidden"), false);
        await ev("window.__atlas.app.patchView({ compare: 'server', layers: { 'ref.terrain': { visible: false } } }, { immediate: true })");
    });

    await t.test('probes: clicked points fill the forms, both engines run, overlays draw and differences are marked', async () => {
        await ev("document.getElementById('tab-probes').click()");
        await ev("document.querySelectorAll('.card-toggle[aria-expanded=\"false\"]').forEach((b) => b.click())");
        const a = [box.x + box.w / 2 - 90, box.y + box.h * 0.62];
        const b = [box.x + box.w / 2 + 70, box.y + box.h * 0.8];
        await click(page, ...a);
        await page.waitFor('window.__atlas.app.selection.hit', 'first pick');
        await click(page, ...b);
        await sleep(400);
        const values = JSON.parse(await ev("JSON.stringify([...document.querySelectorAll('#probe-los input[type=text]')].map((e) => e.value))"));
        assert.equal(values[0], '0', 'the map field takes the current map');
        const [pa, pb] = values.slice(1).map((v) => v.split(',').map(Number));
        assert.equal(pa.length, 3);
        assert.ok(Math.hypot(pa[0] - pb[0], pa[1] - pb[1]) > 5, 'A and B are the same point');
        await ev("document.querySelector('#probe-los button[type=submit]').click()");
        await page.waitFor("window.__atlas.app.overlays.get('query.los')", 'the LOS overlay');
        await ev("document.querySelector('#probe-height button[type=submit]').click()");
        await page.waitFor("window.__atlas.app.overlays.get('ref.query.height') && window.__atlas.app.overlays.get('query.height')", 'both height overlays');
        assert.ok(host.commands.some((c) => c.name === 'ref.query.height' && !('search' in c.args)), 'an empty optional field was sent');
        await sleep(300);
        const marked = await ev("document.querySelectorAll('#probe-height .is-diff').length");
        assert.ok(marked > 0, 'the 1.5 yd height difference was not marked');
        const text = await ev("document.querySelector('#probe-height .probe-results').textContent");
        assert.match(text, /differ/);
        const image = await shot(page);
        assert.ok(countNear(image, [0x3C, 0xCB, 0x9A], 50, box) > 80, 'the server overlay colour is not on the map');
        await ev("window.__atlas.app.overlays.clear()");
    });

    await t.test('the palette lists commands, builds a form from the schema and runs it', async () => {
        await page.press('k', { modifiers: 2 });
        await page.waitFor("document.querySelector('.palette-input')", 'the palette');
        await page.send('Input.insertText', { text: 'bookmark.go' });
        await sleep(200);
        assert.equal(await ev("document.querySelectorAll('.palette-item').length"), 1);
        await page.press('Enter');
        await page.waitFor("document.querySelector('.palette-command')", 'the command form');
        assert.equal(await ev("document.querySelectorAll('.palette .field').length"), 1);
        await ev("document.querySelector('.palette .input').focus()");
        await page.send('Input.insertText', { text: 'No such place' });
        await page.press('Enter');
        await page.waitFor("document.querySelector('.palette-result .is-fail')", 'the failure to show');
        assert.match(await ev("document.querySelector('.palette-result').textContent"), /No bookmark named No such place/);
        await page.press('Escape');
        await page.waitFor("!document.querySelector('.backdrop')", 'the palette to close');
    });

    await t.test('live: entities draw, list, select and follow', async () => {
        host.broadcast('live.status', { connected: true, pid: 4242, sha: 'abcdef123456' });
        const entities = [];
        for (let i = 0; i < 30; i++) {
            entities.push({ g: (0x4000 + i).toString(16), k: i === 0 ? 'player' : i < 4 ? 'bot' : 'creature', e: 100 + i, n: i === 0 ? 'Testplayer' : `Mob ${i}`, x: INN[0] - 30 + (i % 6) * 9, y: INN[1] - 20 + Math.floor(i / 6) * 9, z: 57, o: 0, hp: 90, mt: i % 2 ? 'Chase' : 'Idle' });
        }
        host.broadcast('live.frame', { seq: 1, clockMs: 1000, map: 0, instance: 0, grids: [[14, 32]], counters: {}, entities });
        await page.waitFor('window.__atlas.viewport.liveCounts.entities === 30', 'the live entities');
        await ev("document.getElementById('tab-live').click()");
        await page.waitFor("document.querySelectorAll('.entity-row').length === 30", 'the entity list');
        await ev("document.querySelectorAll('.entity-row .entity-button')[0].click()");
        await page.waitFor("window.__atlas.app.selection.entityGuid === '4000'", 'the selection');
        await ev("[...document.querySelectorAll('.live .button')].find((b) => b.textContent === 'Follow').click()");
        await page.waitFor("window.__atlas.app.view.follow === '4000'", 'follow');
        host.broadcast('live.frame', { seq: 2, clockMs: 2000, map: 0, instance: 0, grids: [[14, 32]], counters: {}, entities: entities.map((e) => (e.g === '4000' ? { ...e, x: INN[0] - 5, y: INN[1] + 25 } : e)) });
        await page.waitFor(`Math.abs(window.__atlas.app.view.camera.target[0] - ${INN[0] - 5}) < 0.5 && Math.abs(window.__atlas.app.view.camera.target[1] - ${INN[1] + 25}) < 0.5`, 'the camera to follow the entity');
        await ev("window.__atlas.app.patchView({ follow: null }, { immediate: true })");
    });

    await t.test('capture renders off-screen at the requested size and posts a PNG', async () => {
        const result = await host.requestCapture(960, 540);
        assert.equal(result.failed, undefined, result.failed);
        assert.ok(isPng(result.png));
        assert.equal(result.type, 'image/png');
        const image = decodePng(result.png);
        assert.equal(image.width, 960);
        assert.equal(image.height, 540);
        assert.ok(countNot(image, CHART, 8) > 5000, 'the capture is blank');
        assert.ok(countNear(image, TERRAIN, 60) > 500, 'the capture has no terrain');
        const tooBig = await host.requestCapture(60000, 40000);
        assert.ok(tooBig.failed, 'an impossible size should be refused');
        assert.match(tooBig.failed, /pixels per side|at least|whole pixels/);
    });

    await t.test('the layer rail and notices say what is missing', async () => {
        const rail = await ev("document.querySelector('.rail-left').textContent");
        assert.match(rail, /1 ok/);
        assert.match(rail, /missing/);
        assert.match(rail, /without a source/);
        const notices = await ev("document.querySelector('.hud-notices').textContent");
        assert.match(notices, /Synthetic rejection/);
    });

    await t.test('no console errors, exceptions or blocked requests', async () => {
        assert.deepEqual(quiet(page), []);
    });
});

test('the UI shows its empty states when the host has no data or client directory', { skip }, async (t) => {
    const s = await startScenario(executable, {
        configure(host) {
            host.handle('status', () => ({ paths: { terrainData: null, clientRoot: null }, problems: ['The data directory D:\\missing does not exist.'], modules: [], version: 'mock0001' }));
        },
    });
    t.after(() => s.close());
    await s.page.waitFor("window.__atlas.app.status.loaded", 'the status');
    await s.page.waitFor("document.querySelector('.hud-notices').textContent.includes('No data directory')", 'the no-data notice');
    const notices = await s.page.evaluate("document.querySelector('.hud-notices').textContent");
    assert.match(notices, /does not exist/);
    assert.match(notices, /--data/);
    await s.page.evaluate("window.__atlas.app.patchView({ compare: 'reference', layers: { 'ref.terrain': { visible: true } } }, { immediate: true })");
    await s.page.waitFor("document.querySelector('.hud-notices').textContent.includes('No client directory')", 'the no-client notice');
    assert.deepEqual(quiet(s.page), []);
});

test('the UI asks for a token when it has none, and again when the host refuses it', { skip }, async (t) => {
    const browser = await Browser.launch(executable);
    t.after(() => browser.close());
    const s = await startScenario(executable, { browser, waitConnected: false });
    t.after(() => s.close());
    await s.page.waitFor("window.__atlas?.app?.connection?.state === 'open'", 'the first UI to connect');
    // A fresh tab has no session token: it must show the gate, not a broken map.
    const bare = await browser.newPage(`${s.host.origin}/`);
    await bare.waitFor("document.querySelector('.gate-title')", 'the token gate');
    assert.match(await bare.evaluate("document.querySelector('.gate-card').textContent"), /access token/);
    await bare.evaluate("document.querySelector('#token-input').focus()");
    await bare.send('Input.insertText', { text: 'short' });
    await bare.press('Enter');
    assert.match(await bare.evaluate("document.querySelector('#token-error').textContent"), /does not look like/);
    // A well-formed but wrong token is refused by the host: back to the gate with the refusal said plainly.
    await bare.evaluate("document.querySelector('#token-input').value = ''");
    await bare.send('Input.insertText', { text: 'A'.repeat(32) });
    await bare.press('Enter');
    await bare.waitFor("document.querySelector('#token-error') && /refused/.test(document.querySelector('#token-error').textContent)", 'the refusal');
    assert.equal(await bare.evaluate("sessionStorage.getItem('atlas.session')"), null, 'a refused token was kept');
    // The right token connects.
    await bare.evaluate("document.querySelector('#token-input').value = ''");
    await bare.send('Input.insertText', { text: s.token });
    await bare.press('Enter');
    await bare.waitFor("document.querySelector('.shell')", 'the app after a valid token');
});
