// View-model modules: the layer catalog and view JSON helpers, which layers draw where, the empty-state notices, the host
// answer readers, probe overlays, line and marker geometry, the live feed model, labels, hotkeys and small UI helpers.
// Everything here runs under plain node: none of these modules touches the DOM when imported.

import assert from 'node:assert/strict';
import test from 'node:test';
import { FALLBACK_LAYERS, GROUPS, canonicalGroup, groupLayers, groupSide, isLiveLayer, normalizeLayerList } from '../../../tools/ArcaneCore.Atlas/Web/js/catalog.js';
import { project } from '../../../tools/ArcaneCore.Atlas/Web/js/camera.js';
import { packColor } from '../../../tools/ArcaneCore.Atlas/Web/js/colors.js';
import { LINE_STRIDE, LineBuilder, MARKER_STRIDE, MarkerBuilder, SHAPES } from '../../../tools/ArcaneCore.Atlas/Web/js/geom.js';
import { groupForDigit, groupToggle } from '../../../tools/ArcaneCore.Atlas/Web/js/hotkeys.js';
import { cssFromPacked, layoutLabels } from '../../../tools/ArcaneCore.Atlas/Web/js/labels.js';
import {
    DIM_FACTOR, layerSide, regionsFor, resolveLayers, wantedTileLayers, writesDepth,
} from '../../../tools/ArcaneCore.Atlas/Web/js/layerstate.js';
import {
    LiveModel, buildLiveGeometry, filterEntities, motionClass, normalizeEntity, pickEntity, pointAlong, splineHead,
} from '../../../tools/ArcaneCore.Atlas/Web/js/live.js';
import {
    describeSurface, filterCommands, mapLabel, normalizeBookmarks, normalizeCommands, normalizeMaps, normalizePick, normalizeStatus,
    paletteCommands, probeKinds,
} from '../../../tools/ArcaneCore.Atlas/Web/js/normalize.js';
import { FILE_KINDS, computeNotices, layerFile } from '../../../tools/ArcaneCore.Atlas/Web/js/notices.js';
import {
    OverlayStore, baseColorFor, buildOverlayGeometry, colorKeyTable, normalizeOverlay, overlayIsEmpty,
} from '../../../tools/ArcaneCore.Atlas/Web/js/overlays.js';
import { edgesFromTriangles } from '../../../tools/ArcaneCore.Atlas/Web/js/renderer.js';
import * as tiles from '../../../tools/ArcaneCore.Atlas/Web/js/tiles.js';
import { fileState } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/hud.js';
import { headingName } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/hud.js';
import { tileCountText } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/layers.js';
import { unionFields } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/probes.js';
import { lastCommandText } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/statusbar.js';
import { cameraPatch, defaultView, layerStyle, normalizeView } from '../../../tools/ArcaneCore.Atlas/Web/js/view.js';
import { rayTrianglePlane } from '../../../tools/ArcaneCore.Atlas/Web/js/viewport.js';
import { makeCamera } from '../../../tools/ArcaneCore.Atlas/Web/js/camera.js';

const infos = () => normalizeLayerList(null);
const viewWith = (patch = {}) => {
    const v = defaultView();
    for (const [k, value] of Object.entries(patch)) {
        if (k === 'layers') {
            for (const [id, style] of Object.entries(value)) {
                v.layers[id] = { ...v.layers[id], ...style };
            }
        } else {
            v[k] = value;
        }
    }
    return v;
};
const allOff = () => {
    const v = defaultView();
    for (const id of Object.keys(v.layers)) {
        v.layers[id].visible = false;
    }
    return v;
};

test('catalog: the host answer is merged over the built-in layer list', () => {
    const base = infos();
    assert.equal(base.length, FALLBACK_LAYERS.length);
    assert.ok(base.every((l) => l.available && l.known));

    const merged = normalizeLayerList({
        layers: [
            { id: 'terrain', title: 'Ground', defaultColor: '#112233', defaultVisible: false, sources: 0 },
            { id: 'nav.poly', color: '#445566', attribute_colors: { ground: '#010203' }, sources: ['a'] },
            { id: 'custom.layer', group: 'differences', title: 'Custom' },
            { id: 'live.extra', title: 'Extra live' },
            { title: 'no id' },
            null,
            { id: 'vmap.m2', available: false },
            { id: 'area', source: null },
            { id: 'liquid', source: 'x' },
            { id: 'ref.terrain', hasSource: true },
        ],
    });
    const by = Object.fromEntries(merged.map((l) => [l.id, l]));
    assert.equal(merged.length, FALLBACK_LAYERS.length + 2, 'unknown ids are kept, entries without an id are not');
    assert.equal(by.terrain.title, 'Ground');
    assert.equal(by.terrain.defaultColor, '#112233');
    assert.equal(by.terrain.defaultVisible, false);
    assert.equal(by.terrain.available, false, 'zero sources: the layer has no data source');
    assert.equal(by['nav.poly'].defaultColor, '#445566');
    assert.deepEqual(by['nav.poly'].attributeColors, { ground: '#010203' });
    assert.equal(by['nav.poly'].available, true);
    assert.equal(by['custom.layer'].group, 'Diff');
    assert.equal(by['custom.layer'].known, false);
    assert.equal(by['custom.layer'].tiled, true);
    assert.equal(by['live.extra'].tiled, false, 'an unknown live layer is not fetched as tiles');
    assert.equal(by['vmap.m2'].available, false);
    assert.equal(by.area.available, false);
    assert.equal(by.liquid.available, true);
    assert.equal(by['ref.terrain'].available, true);
    assert.equal(by.tiles.available, true, 'a layer the host did not mention keeps the default');
    assert.equal(normalizeLayerList([{ id: 'terrain', title: 'A' }]).find((l) => l.id === 'terrain').title, 'A', 'a bare array is accepted');
    assert.equal(normalizeLayerList('nonsense').length, FALLBACK_LAYERS.length);
});

test('catalog: groups, sides and layer kinds', () => {
    assert.equal(canonicalGroup('TERRAIN'), 'Terrain');
    assert.equal(canonicalGroup('Differences'), 'Diff');
    assert.equal(canonicalGroup('nope'), null);
    assert.equal(canonicalGroup(undefined), null);
    assert.equal(groupSide('Reference'), 'reference');
    assert.equal(groupSide('Diff'), 'diff');
    assert.equal(groupSide('World'), 'both');
    assert.equal(groupSide('unknown'), 'server');
    assert.equal(isLiveLayer('live.players'), true);
    assert.equal(isLiveLayer('liveness'), false);
    const grouped = groupLayers(infos());
    assert.deepEqual(grouped.map((g) => g.group.id), GROUPS.map((g) => g.id));
    assert.equal(grouped.reduce((n, g) => n + g.layers.length, 0), FALLBACK_LAYERS.length);
    assert.deepEqual(groupLayers(infos().filter((l) => l.group === 'Live')).map((g) => g.group.id), ['Live'], 'empty groups are omitted');
});

test('view: a partial or malformed host view keeps the defaults for what it gets wrong', () => {
    const a = normalizeView({ camera: { yaw: 'x', target: [1, 2], pitch: 500, mode: 'sideways', fov: 5 }, split: 5, loadRadius: 9, theme: 'neon', hud: 'yes', follow: '' });
    const d = defaultView();
    assert.equal(a.camera.yaw, d.camera.yaw);
    assert.deepEqual(a.camera.target, d.camera.target);
    assert.equal(a.camera.pitch, 89);
    assert.equal(a.camera.mode, 'orbit');
    assert.equal(a.camera.fov, 15);
    assert.equal(a.split, 0.95);
    assert.equal(a.loadRadius, 4);
    assert.equal(a.theme, 'graphite');
    assert.equal(a.hud, true);
    assert.equal(a.follow, null);
    assert.equal(normalizeView({ loadRadius: 2.7 }).loadRadius, 2);
    assert.equal(normalizeView({ loadRadius: -3 }).loadRadius, 0);
    assert.equal(normalizeView({ follow: 'AB12' }).follow, 'AB12');
    assert.equal(normalizeView({ follow: null }, { ...d, follow: 'AB12' }).follow, null);
    assert.equal(normalizeView({ map: -1 }).map, 0);
    assert.equal(normalizeView({ map: 530.9 }).map, 530);

    const layers = normalizeView({
        layers: {
            terrain: { visible: false, color: 'red', opacity: 2, mode: 'nonsense' },
            'ref.terrain': { visible: true, color: '#abcdef', opacity: -1, mode: 'fill', colorByAttribute: true },
            'vendor.extra': { visible: true },
            broken: 7,
        },
    }).layers;
    assert.equal(layers.terrain.visible, false);
    assert.equal(layers.terrain.color, d.layers.terrain.color, 'a colour that is not #RRGGBB keeps the previous one');
    assert.equal(layers.terrain.opacity, 1, 'opacity is clamped');
    assert.equal(layers.terrain.mode, 'wire');
    assert.deepEqual(layers['ref.terrain'], { visible: true, color: '#ABCDEF', opacity: 0, mode: 'fill', colorByAttribute: true });
    assert.deepEqual(layers['vendor.extra'], { visible: true, color: '#93A1AA', opacity: 1, mode: 'wire', colorByAttribute: false });
    assert.equal('broken' in layers, false);

    assert.deepEqual(normalizeView(null), d);
    assert.deepEqual(normalizeView('x'), d);
    const base = defaultView();
    const out = normalizeView({ map: 1 }, base);
    out.camera.target[0] = 0;
    assert.notEqual(base.camera.target[0], 0, 'normalizeView does not alias its base');
});

test('view: camera patches and layer styles', () => {
    const camera = makeCamera({ target: [1, 2, 3], yaw: 10, pitch: 20, distance: 30, fov: 40, mode: 'fly' });
    const patch = cameraPatch(camera);
    assert.deepEqual(patch, { camera: { target: [1, 2, 3], distance: 30, yaw: 10, pitch: 20, fov: 40, mode: 'fly' } });
    patch.camera.target[0] = 99;
    assert.equal(camera.target[0], 1, 'the patch owns its target array');
    const view = defaultView();
    assert.equal(layerStyle(view, 'terrain'), view.layers.terrain);
    assert.deepEqual(layerStyle(view, 'unknown.layer', { defaultVisible: true, defaultColor: '#123456' }), { visible: true, color: '#123456', opacity: 1, mode: 'wire', colorByAttribute: false });
    assert.deepEqual(layerStyle(view, 'unknown.layer'), { visible: false, color: '#93A1AA', opacity: 1, mode: 'wire', colorByAttribute: false });
    assert.equal(defaultView().camera.target[0], -9462.66);
    const a = defaultView();
    a.layers.terrain.visible = false;
    assert.equal(defaultView().layers.terrain.visible, true, 'defaultView returns a fresh object each time');
});

test('tiles: the nearest tiles load first and stay inside the grid', () => {
    const wanted = tiles.wantedTiles({ x: 49, y: 31 }, 1);
    assert.equal(wanted.length, 9);
    assert.deepEqual([wanted[0].x, wanted[0].y, wanted[0].ring], [49, 31, 0]);
    assert.deepEqual(wanted.slice(1, 5).map((t) => [t.x, t.y]), [[48, 31], [49, 30], [49, 32], [50, 31]], 'the four edge neighbours come before the corners');
    assert.deepEqual(wanted.slice(5).map((t) => t.d2), [2, 2, 2, 2]);
    assert.equal(tiles.wantedTiles({ x: 0, y: 0 }, 1).length, 4, 'the window is clipped at the map corner');
    assert.equal(tiles.wantedTiles({ x: 10, y: 10 }, 0).length, 1);
    assert.equal(tiles.tileKey('terrain', 0, 49, 31), 'terrain|0|49|31');
    assert.equal(tiles.isGlobal(-1, -1), true);
    assert.equal(tiles.isGlobal(-1, 0), false);
    assert.equal(tiles.isValidTile(-1, -1), true);
    assert.equal(tiles.isValidTile(64, 0), false);
    assert.deepEqual(tiles.tileOrigin(-1, -1), [0, 0, 0]);
    assert.deepEqual(tiles.describePosition(0, 99999, 0), { tile: null, cell: null, square: null, files: null });
    const inn = tiles.describePosition(0, -9462.66, 16.1915);
    assert.deepEqual(inn.tile, { x: 49, y: 31 });
    assert.deepEqual(inn.files, { map: '0004931.map', vmap: '000_31_49.vmtile', mmap: '0004931.mmtile' }, 'the vmap name puts tile Y first');
});

test('layers: which layers draw where for each compare mode', () => {
    assert.deepEqual(regionsFor('server', 'server'), ['full']);
    assert.deepEqual(regionsFor('reference', 'server'), []);
    assert.deepEqual(regionsFor('both', 'server'), ['full']);
    assert.deepEqual(regionsFor('diff', 'server'), []);
    assert.deepEqual(regionsFor('server', 'reference'), []);
    assert.deepEqual(regionsFor('reference', 'reference'), ['full']);
    assert.deepEqual(regionsFor('both', 'reference'), ['full']);
    assert.deepEqual(regionsFor('server', 'split'), ['left']);
    assert.deepEqual(regionsFor('reference', 'split'), ['right']);
    assert.deepEqual(regionsFor('both', 'split'), ['left', 'right']);
    assert.deepEqual(regionsFor('diff', 'split'), []);
    assert.deepEqual(regionsFor('server', 'diff'), ['full']);
    assert.deepEqual(regionsFor('diff', 'diff'), ['full']);
    assert.deepEqual(regionsFor('reference', 'diff'), []);
    assert.equal(layerSide({ group: 'Navmesh' }), 'server');
    assert.equal(layerSide({ group: 'Live' }), 'both');

    const by = (view) => Object.fromEntries(resolveLayers(view, infos()).map((l) => [l.id, l]));
    const server = by(defaultView());
    assert.equal(server.terrain.visible, true);
    assert.deepEqual(server.terrain.regions, ['full']);
    assert.equal(server['ref.terrain'].visible, false);
    assert.equal(server['live.players'].live, true);
    assert.equal(server['live.players'].visible, true, 'live layers belong to every compare mode');
    assert.equal(server.terrain.dim, false);

    const reference = by(viewWith({ compare: 'reference', layers: { 'ref.terrain': { visible: true } } }));
    assert.equal(reference.terrain.visible, false);
    assert.equal(reference.terrain.hiddenByMode, true, 'ticked but hidden by the compare mode');
    assert.equal(reference['ref.terrain'].visible, true);
    assert.equal(reference.tiles.visible, true);

    const split = by(viewWith({ compare: 'split', layers: { 'ref.terrain': { visible: true } } }));
    assert.deepEqual(split.terrain.regions, ['left']);
    assert.deepEqual(split['ref.terrain'].regions, ['right']);
    assert.deepEqual(split.tiles.regions, ['left', 'right']);

    const diff = by(viewWith({ compare: 'diff', layers: { 'diff.terrain': { visible: true }, terrain: { mode: 'fill' } } }));
    assert.equal(diff.terrain.dim, true);
    assert.equal(diff.terrain.mode, 'wire', 'server layers under a diff are forced to wire');
    assert.equal(diff.terrain.opacity, DIM_FACTOR);
    assert.equal(diff['diff.terrain'].visible, true);
    assert.equal(diff['diff.terrain'].dim, false);
    assert.equal(diff.tiles.dim, false, 'the tile grid is not dimmed');

    const unavailable = resolveLayers(defaultView(), infos().map((l) => (l.id === 'terrain' ? { ...l, available: false } : l)));
    assert.equal(unavailable.find((l) => l.id === 'terrain').visible, false);
    assert.equal(unavailable.find((l) => l.id === 'terrain').style.visible, true, 'the style keeps what the user asked for');
});

test('layers: depth writes and the tile layers to fetch', () => {
    assert.equal(writesDepth({ mode: 'wire', opacity: 1 }), true);
    assert.equal(writesDepth({ mode: 'both', opacity: 0.5 }), true);
    assert.equal(writesDepth({ mode: 'xray', opacity: 1 }), false);
    assert.equal(writesDepth({ mode: 'wire', opacity: 0 }), false);
    assert.equal(writesDepth({ mode: 'fill', opacity: 1 }), true);
    assert.equal(writesDepth({ mode: 'fill', opacity: 0.5 }), false, 'a see-through fill must not hide what is behind it');
    const wanted = wantedTileLayers(resolveLayers(defaultView(), infos()));
    assert.ok(wanted.includes('terrain') && wanted.includes('tiles') && wanted.includes('vmap.wmo'));
    assert.ok(!wanted.some((id) => id.startsWith('live.')), 'live layers are never fetched as tiles');
    assert.ok(!wanted.includes('ref.terrain'), 'hidden layers are not fetched');
    assert.deepEqual(wantedTileLayers(resolveLayers(allOff(), infos())), []);
});

test('notices: the state of the world as sentences that say what to do', () => {
    const resolvedFor = (view) => resolveLayers(view, infos());
    const base = {
        webgl: { ok: true, message: '' },
        connection: { state: 'open' },
        status: normalizeStatus({ paths: { terrainData: 'D:/terrain', clientRoot: 'D:/client' } }),
        resolved: resolvedFor(defaultView()),
        map: 0,
        focusTile: null,
        statusOf: () => null,
    };
    const ids = (overrides) => computeNotices({ ...base, ...overrides }).map((n) => n.id);
    assert.deepEqual(ids({}), [], 'a healthy session says nothing');

    const webgl = computeNotices({ ...base, webgl: { ok: false, message: 'no context' } });
    assert.deepEqual(webgl.map((n) => [n.id, n.level, n.text]), [['webgl', 'error', 'no context']]);
    assert.match(webgl[0].hint, /hardware acceleration/);
    assert.equal(computeNotices({ ...base, webgl: { ok: false, message: '' } })[0].text, 'WebGL2 is not available in this browser.');

    const auth = computeNotices({ ...base, connection: { state: 'unauthorized' } });
    assert.deepEqual(auth.map((n) => [n.id, n.level]), [['auth', 'error']]);
    assert.match(auth[0].hint, /arcane-atlas serve/);
    assert.match(computeNotices({ ...base, connection: { state: 'connecting' } })[0].text, /^Connecting/);
    assert.match(computeNotices({ ...base, connection: { state: 'closed' } })[0].text, /dropped/);

    const problem = computeNotices({ ...base, status: normalizeStatus({ problems: ['bad path'], paths: { terrainData: 'D:/t', clientRoot: 'D:/c' } }) });
    assert.deepEqual(problem.map((n) => [n.id, n.text]), [['problem-0', 'bad path']]);

    const noData = computeNotices({ ...base, status: normalizeStatus({ paths: { clientRoot: 'D:/c' } }) });
    assert.deepEqual(noData.map((n) => n.id), ['no-data']);
    assert.match(noData[0].hint, /--data/);

    const referenceView = viewWith({ compare: 'split', layers: { 'ref.terrain': { visible: true } } });
    const noClient = computeNotices({ ...base, resolved: resolvedFor(referenceView), status: normalizeStatus({ paths: { terrainData: 'D:/t' } }) });
    assert.deepEqual(noClient.map((n) => n.id), ['no-client']);
    assert.match(noClient[0].hint, /--client/);

    assert.deepEqual(ids({ status: normalizeStatus(null) }), [], 'before the status arrives the missing-directory notices wait');

    const none = computeNotices({ ...base, resolved: resolvedFor(allOff()) });
    assert.deepEqual(none.map((n) => [n.id, n.level]), [['no-layers', 'info']]);

    const hiddenView = allOff();
    hiddenView.compare = 'reference';
    hiddenView.layers.terrain.visible = true;
    const hidden = computeNotices({ ...base, resolved: resolvedFor(hiddenView) });
    assert.deepEqual(hidden.map((n) => n.id), ['compare-hidden']);
    assert.match(hidden[0].text, /The layer you ticked belongs/);
    hiddenView.layers['vmap.wmo'].visible = true;
    assert.match(computeNotices({ ...base, resolved: resolvedFor(hiddenView) })[0].text, /The layers you ticked belong/);
});

test('notices: tile states on the focus tile name the layer and the file', () => {
    const resolved = resolveLayers(defaultView(), infos());
    const states = {
        terrain: { state: 'Rejected', detail: 'bad magic' },
        liquid: { state: 'Error', detail: '' },
        'vmap.wmo': { state: 'Missing' },
        'vmap.m2': { state: 'Missing' },
        'nav.poly': { state: 'Missing' },
        'terrain.holes': { state: 'Missing' },
        tiles: { state: 'Ok' },
    };
    const notices = computeNotices({
        webgl: { ok: true, message: '' },
        connection: { state: 'open' },
        status: normalizeStatus({ paths: { terrainData: 'D:/t' } }),
        resolved,
        map: 0,
        focusTile: { x: 49, y: 31 },
        statusOf: (layer) => states[layer] ?? null,
    });
    const by = Object.fromEntries(notices.map((n) => [n.id, n]));
    assert.equal(by['rejected-terrain'].level, 'error');
    assert.equal(by['rejected-terrain'].text, 'Terrain for tile 49, 31 was rejected: bad magic');
    assert.equal(by['rejected-terrain'].hint, 'File: 0004931.map');
    assert.equal(by['rejected-liquid'].text, 'Terrain liquid for tile 49, 31 was not loaded.');
    assert.equal(by.missing.level, 'info');
    assert.equal(by.missing.text, 'No data for tile 49, 31 of map 0: Terrain holes (0004931.map), WMO collision (000_31_49.vmtile), M2 collision (000_31_49.vmtile) and 1 more layer.');
    assert.match(by.missing.hint, /not empty space/);
    assert.equal(notices.length, 3);

    assert.deepEqual(layerFile('tiles', 0, 1, 2), { kind: 'map', name: '0000102.map' });
    assert.equal(layerFile('terrain.holes', 1, 10, 20).name, '0011020.map');
    assert.equal(layerFile('area', 0, 1, 2).kind, 'map');
    assert.equal(layerFile('liquid', 0, 1, 2).kind, 'map');
    assert.deepEqual(layerFile('vmap.m2', 0, 1, 2), { kind: 'vmap', name: '000_02_01.vmtile' });
    assert.deepEqual(layerFile('nav.portals', 0, 1, 2), { kind: 'mmap', name: '0000102.mmtile' });
    assert.equal(layerFile('ref.nav.poly', 0, 1, 2).kind, 'mmap');
    for (const id of ['ref.terrain', 'diff.nav', 'live.players']) {
        assert.equal(layerFile(id, 0, 1, 2), null, id);
    }
    assert.deepEqual(FILE_KINDS.map((k) => k.kind), ['map', 'vmap', 'mmap']);
});

test('normalize: host answers are read tolerantly and never guessed at', () => {
    const maps = normalizeMaps({ maps: [{ map: 530, name: 'Outland' }, { id: 0, layers: { terrain: 5, 'vmap.wmo': { tiles: 3 }, bad: 'x' } }, 1, 'x', { map: -1 }, { mapId: 3.5 }, null] });
    assert.deepEqual(maps.map((m) => m.id), [0, 1, 530]);
    assert.deepEqual(maps[0].tiles, { terrain: 5, 'vmap.wmo': 3 });
    assert.equal(maps[2].name, 'Outland');
    assert.deepEqual(normalizeMaps('garbage'), []);
    assert.deepEqual(normalizeMaps([{ map: 1, tiles: 'many' }])[0].tiles, {});
    assert.equal(mapLabel({ id: 0, name: 'Eastern Kingdoms' }), '0 · Eastern Kingdoms');
    assert.equal(mapLabel({ id: 9, name: '' }), '9');

    const bookmarks = normalizeBookmarks({ bookmarks: ['plain', { name: 'inn', map: 0, at: [1, 2, 3], note: 'n', builtIn: true }, { name: 'bad', at: [1, 2], map: 1.5 }, { name: '' }, 5, null] });
    assert.deepEqual(bookmarks.map((b) => b.name), ['plain', 'inn', 'bad']);
    assert.deepEqual(bookmarks[1], { name: 'inn', map: 0, at: [1, 2, 3], note: 'n', builtIn: true });
    assert.deepEqual(bookmarks[2], { name: 'bad', map: null, at: null, note: '', builtIn: false });

    const commands = normalizeCommands({ commands: [{ name: 'view.set', summary: 's', surfaces: ['Palette', 'MCP'], schema: { type: 'object' } }, { name: 'a.b', surfaces: 'cli, palette' }, { name: 5 }, 'x', { name: 'no.surfaces' }] });
    assert.deepEqual(commands.map((c) => c.name), ['a.b', 'no.surfaces', 'view.set']);
    assert.deepEqual(commands[0].surfaces, ['cli', 'palette']);
    assert.deepEqual(commands[2].surfaces, ['palette', 'mcp']);
    assert.equal(commands[1].surfaces, null);
    assert.deepEqual(commands[1].schema, { type: 'object', properties: {} });
    assert.deepEqual(paletteCommands(commands).map((c) => c.name), ['a.b', 'no.surfaces', 'view.set']);
    assert.deepEqual(paletteCommands([{ surfaces: ['mcp'] }, { surfaces: ['all'] }, { surfaces: null }]).length, 2);

    const status = normalizeStatus({ problems: ['p', 5], paths: { TerrainData: 'D:/t', client: 'D:/c' }, version: '1.2', modules: ['m', { name: 'n' }, {}], cacheBytes: 12, live: { connected: false } });
    assert.deepEqual(status, { loaded: true, problems: ['p', '5'], terrainData: 'D:/t', clientRoot: 'D:/c', version: '1.2', modules: ['m', 'n'], cacheBytes: 12, live: { connected: false } });
    assert.equal(normalizeStatus({ dataDirectory: 'x' }).terrainData, 'x', 'paths may sit at the top level');
    assert.equal(normalizeStatus({ paths: { data: '' } }).terrainData, null);
    assert.deepEqual(normalizeStatus(null), { loaded: false, problems: [], terrainData: null, clientRoot: null, version: null, modules: [], cacheBytes: null, live: null });
});

test('normalize: probes pair the server and reference commands', () => {
    const named = (...names) => names.map((name) => ({ name }));
    const kinds = probeKinds(named('query.pick', 'ref.query.los', 'query.los', 'query.height', 'query.zzz', 'ref.query.path', 'view.set', 'query.floorstack'));
    assert.deepEqual(kinds.map((k) => k.kind), ['height', 'los', 'path', 'floorstack', 'zzz']);
    const los = kinds.find((k) => k.kind === 'los');
    assert.equal(los.server.name, 'query.los');
    assert.equal(los.reference.name, 'ref.query.los');
    assert.equal(kinds.find((k) => k.kind === 'height').reference, null);
    assert.equal(kinds.find((k) => k.kind === 'path').server, null);
    assert.equal(kinds.some((k) => k.kind === 'pick'), false, 'query.pick belongs to the inspector');
});

test('normalize: the pick answer, whichever way the host nests it', () => {
    const raw = { layer: 'terrain', tile: [0, 49, 31], distance: 12.5, point: [1, 2, 3], primitive: 'triangle 7', attribute: 4, details: { square: [3, 4] } };
    const expected = { layer: 'terrain', tile: { map: 0, x: 49, y: 31 }, distance: 12.5, point: [1, 2, 3], primitive: 'triangle 7', attribute: 4, details: { square: [3, 4] } };
    assert.deepEqual(normalizePick(raw).hit, expected);
    assert.deepEqual(normalizePick({ hit: raw }).hit, expected);
    assert.deepEqual(normalizePick(raw).all, [expected], 'a single hit is also the list of hits');
    const many = normalizePick({ hit: raw, hits: [raw, { ...raw, layer: 'vmap.wmo' }, { nonsense: true }] });
    assert.deepEqual(many.all.map((h) => h.layer), ['terrain', 'vmap.wmo']);
    for (const miss of [{}, null, undefined, { hit: null }, { layer: 'terrain' }, { ...raw, point: [1, 2] }, { ...raw, point: [1, 2, Number.NaN] }]) {
        assert.deepEqual(normalizePick(miss), { hit: null, all: [] });
    }
    const sparse = normalizePick({ layer: 'x', point: [0, 0, 0], tile: [0, 1.5, 2], attribute: 1.5, distance: Number.NaN, details: [1] }).hit;
    assert.deepEqual(sparse, { layer: 'x', tile: null, distance: null, point: [0, 0, 0], primitive: '', attribute: null, details: {} });

    assert.equal(describeSurface('Terrain', { colorKey: 'ground', square: 3, ok: true, name: 'n', extra: 5, more: 6 }, 7), 'Terrain · ground · square 3 · ok true · name n');
    assert.equal(describeSurface('Terrain', null, 7), 'Terrain · attribute 7');
    assert.equal(describeSurface('Terrain', null, null), 'Terrain');
    assert.equal(describeSurface('Terrain', { nested: { a: 1 } }, 7), 'Terrain');
});

test('normalize: the palette filter ranks name matches before summary matches', () => {
    const commands = [
        { name: 'query.height', summary: 'Ground height at a point' },
        { name: 'view.set', summary: 'Set the view; moves the camera to a height' },
        { name: 'camera.goto', summary: 'Go to a point' },
        { name: 'tiles.height', summary: 'Tile heights' },
    ];
    assert.deepEqual(filterCommands(commands, '').map((c) => c.name), commands.map((c) => c.name));
    assert.deepEqual(filterCommands(commands, 'height').map((c) => c.name), ['query.height', 'tiles.height', 'view.set'], 'names that contain the word come before summaries that do');
    assert.deepEqual(filterCommands(commands, 'q h').map((c) => c.name), ['query.height'], 'every word must match');
    assert.deepEqual(filterCommands(commands, 'view cam').map((c) => c.name), ['view.set']);
    assert.deepEqual(filterCommands(commands, 'zzz'), []);
    assert.deepEqual(filterCommands(commands, 'GO TO').map((c) => c.name), ['camera.goto']);
    assert.deepEqual(filterCommands(commands, 'view').map((c) => c.name), ['view.set'], 'a name match ranks first');
});

test('overlays: malformed entries are dropped and counted', () => {
    const overlay = normalizeOverlay({
        points: [{ at: [1, 2, 3], label: 'a', color: '#ff0000', colorKey: 'k' }, { at: [1, 2] }, { at: [1, 2, 'x'] }, 5, null],
        segments: [{ from: [0, 0, 0], to: [1, 1, 1], dashed: true }, { from: [0, 0, 0] }],
        polylines: [{ pts: [[0, 0, 0], [1, 0, 0]] }, { pts: [[0, 0, 0]] }, { pts: 'x' }],
        boxes: [{ min: [0, 0, 0], max: [1, 1, 1], label: 'b'.repeat(500) }, { min: [0, 0, 0] }],
    });
    assert.equal(overlay.points.length, 1);
    assert.equal(overlay.segments.length, 1);
    assert.equal(overlay.polylines.length, 1);
    assert.equal(overlay.boxes.length, 1);
    assert.equal(overlay.dropped, 4 + 1 + 2 + 1);
    assert.equal(overlay.points[0].color, '#FF0000');
    assert.equal(overlay.points[0].colorKey, 'k');
    assert.equal(overlay.segments[0].dashed, true);
    assert.equal(overlay.points[0].dashed, false);
    assert.equal(overlay.boxes[0].label.length, 200, 'labels are cut to 200 characters');
    assert.equal(normalizeOverlay({ points: [{ at: [1, 2, 3], color: 'red', label: '' }] }).points[0].color, undefined);
    assert.equal(normalizeOverlay({ points: [{ at: [1, 2, 3], label: '' }] }).points[0].label, undefined);
    assert.equal(overlayIsEmpty(normalizeOverlay(null)), true);
    assert.equal(overlayIsEmpty(overlay), false);
    assert.equal(normalizeOverlay('x').dropped, 0);
});

test('overlays: geometry is coloured by who answered and keeps far coordinates exact', () => {
    assert.equal(baseColorFor('query.los'), '#3CCB9A');
    assert.equal(baseColorFor('ref.query.los'), '#FFD34D');
    assert.deepEqual(colorKeyTable([{ attributeColors: { a: '#111111' } }, { attributeColors: { a: '#222222', b: '#333333' } }, {}]), { a: '#222222', b: '#333333' });

    const far = 1_000_000;
    const overlay = normalizeOverlay({
        segments: [{ from: [far, far, 0], to: [far + 10, far, 0], label: 'seg' }, { from: [0, 0, 0], to: [1, 0, 0], dashed: true, color: '#0000FF' }],
        polylines: [{ pts: [[0, 0, 0], [5, 0, 0], [5, 5, 0]], colorKey: 'water' }],
        boxes: [{ min: [0, 0, 0], max: [1, 1, 1] }],
        points: [{ at: [2, 3, 4], label: 'pt', color: '#00FF00' }],
    });
    const geometry = buildOverlayGeometry('query.los', overlay, { water: '#4FA3E0' });
    assert.equal(geometry.solid.count, 1 + 2 + 12, 'a segment, the polyline pieces and the twelve box edges');
    assert.equal(geometry.dashed.count, 1);
    assert.equal(geometry.markers.count, 1);
    assert.equal(geometry.solid.buffer.byteLength, geometry.solid.count * LINE_STRIDE);
    assert.deepEqual(geometry.solid.origin, [far, far, 0], 'the origin is the first segment start, in double precision');
    const f = new Float32Array(geometry.solid.buffer);
    const u32 = new Uint32Array(geometry.solid.buffer);
    assert.deepEqual([...f.subarray(0, 6)], [0, 0, 0, 10, 0, 0], 'a million yards out the ten-yard segment is still exactly ten yards');
    assert.equal(u32[6], packColor('#3CCB9A'), 'the server colour');
    assert.equal(u32[7 * 1 + 6], packColor('#4FA3E0'), 'a colour key resolves through the layer table');
    assert.equal(new Uint32Array(geometry.dashed.buffer)[6], packColor('#0000FF'));
    const m = new Float32Array(geometry.markers.buffer);
    assert.deepEqual(geometry.markers.origin, [2, 3, 4]);
    assert.equal(m[4], 11, 'marker size');
    assert.equal(m[5], SHAPES.diamond);
    assert.equal(new Uint32Array(geometry.markers.buffer)[3], packColor('#00FF00'));
    assert.deepEqual(geometry.labels.map((l) => l.text).sort(), ['pt', 'seg']);
    assert.deepEqual(geometry.labels.find((l) => l.text === 'seg').at, [far + 5, far, 0], 'a segment is labelled at its middle');
    assert.deepEqual(geometry.bounds, { min: [0, 0, 0], max: [far + 10, far, 4] });

    const reference = buildOverlayGeometry('ref.query.los', normalizeOverlay({ segments: [{ from: [0, 0, 0], to: [1, 0, 0] }] }));
    assert.equal(new Uint32Array(reference.solid.buffer)[6], packColor('#FFD34D'), 'reference answers are amber');
    const empty = buildOverlayGeometry('query.los', normalizeOverlay({}));
    assert.deepEqual([empty.solid, empty.dashed, empty.markers, empty.bounds], [null, null, null, null]);
});

test('overlays: the store keeps the latest overlay per command', () => {
    const store = new OverlayStore();
    const events = [];
    store.on('change', (e) => events.push(['change', e?.name ?? null]));
    store.on('remove', (e) => events.push(['remove', e.name]));
    assert.equal(store.ingest('query.los', { z: 1 }), null, 'a result without an overlay stores nothing');
    assert.equal(store.ingest('query.los', null), null);
    const first = store.ingest('query.los', { overlay: { points: [{ at: [0, 0, 0] }] } });
    assert.equal(first.counts.points, 1);
    assert.equal(first.visible, true);
    store.setVisible('query.los', false);
    const second = store.ingest('query.los', { overlay: { segments: [{ from: [0, 0, 0], to: [1, 1, 1] }, { bad: true }] } });
    assert.equal(second.visible, false, 'a hidden overlay stays hidden when the command runs again');
    assert.equal(second.counts.segments, 1);
    assert.equal(second.counts.dropped, 1);
    assert.ok(second.version > first.version);
    assert.equal(store.entries().length, 1);
    assert.equal(store.get('query.los'), second);
    store.ingest('ref.query.los', { overlay: {} });
    assert.equal(store.entries().length, 2);
    store.remove('ref.query.los');
    store.remove('never.ran');
    assert.equal(store.entries().length, 1);
    store.clear();
    assert.equal(store.entries().length, 0);
    assert.deepEqual(events.filter((e) => e[0] === 'remove').map((e) => e[1]), ['query.los', 'ref.query.los', 'query.los']);
    assert.equal(store.version, 3);
});

test('geometry: line instances are origin-relative floats with a packed colour', () => {
    const b = new LineBuilder();
    assert.equal(b.count, 0);
    assert.deepEqual(b.build().origin, [0, 0, 0]);
    assert.equal(b.build().count, 0);
    b.add([100.5, 200.25, 300], [101.5, 200.25, 303], 0xFF0000FF);
    const built = b.build();
    assert.deepEqual(built.origin, [100.5, 200.25, 300]);
    assert.equal(built.buffer.byteLength, LINE_STRIDE);
    assert.deepEqual([...new Float32Array(built.buffer, 0, 6)], [0, 0, 0, 1, 0, 3]);
    assert.equal(new Uint32Array(built.buffer)[6], 0xFF0000FF);
    const moved = b.build([100, 200, 300]);
    assert.deepEqual([...new Float32Array(moved.buffer, 0, 3)], [0.5, 0.25, 0]);
    assert.equal(new LineBuilder().addBox([0, 0, 0], [1, 1, 1], 1).count, 12);
    assert.equal(new LineBuilder().addCross([0, 0, 0], 1, 1).count, 3);
    assert.equal(new LineBuilder().addRing([0, 0, 0], 2, 1, 16).count, 16);
    assert.equal(new LineBuilder().addPolyline([[0, 0, 0], [1, 0, 0], [1, 1, 0]], 1).count, 2);
    assert.equal(new LineBuilder().addPolyline([[0, 0, 0], [1, 0, 0], [1, 1, 0]], 1, true).count, 3);
    assert.equal(new LineBuilder().addPolyline([[0, 0, 0], [1, 0, 0]], 1, true).count, 1, 'a two-point polyline has nothing to close');
    assert.equal(new LineBuilder().addDashed([0, 0, 0], [10, 0, 0], 1, 1, 1).count, 5);
    assert.equal(new LineBuilder().addDashed([0, 0, 0], [10.5, 0, 0], 1, 2, 1).count, 4);
    assert.equal(new LineBuilder().addDashed([1, 1, 1], [1, 1, 1], 1, 1, 1).count, 0, 'a zero-length segment has no dashes');
    const dashed = new LineBuilder().addDashed([0, 0, 0], [3, 0, 0], 7, 2, 2).build();
    assert.deepEqual([...new Float32Array(dashed.buffer, 0, 6)], [0, 0, 0, 2, 0, 0]);
    assert.equal(new Uint32Array(dashed.buffer)[6], 7);
    const wrapped = new LineBuilder().add([0, 0, 0], [1, 1, 1], -1).build();
    assert.equal(new Uint32Array(wrapped.buffer)[6], 0xFFFFFFFF, 'colours are stored unsigned');
});

test('geometry: marker instances', () => {
    const b = new MarkerBuilder();
    b.add([1000.5, 2000.5, 3000.5], 0x80FFFFFF, 9, SHAPES.ring).add([1001.5, 2000.5, 3000.5], 1, 5);
    assert.equal(b.count, 2);
    const built = b.build();
    assert.deepEqual(built.origin, [1000.5, 2000.5, 3000.5]);
    assert.equal(built.buffer.byteLength, 2 * MARKER_STRIDE);
    const f = new Float32Array(built.buffer);
    const u = new Uint32Array(built.buffer);
    assert.deepEqual([f[0], f[1], f[2], u[3], f[4], f[5]], [0, 0, 0, 0x80FFFFFF, 9, SHAPES.ring]);
    assert.deepEqual([f[6], f[7], f[8], u[9], f[10], f[11]], [1, 0, 0, 1, 5, SHAPES.circle]);
    assert.equal(new MarkerBuilder().build().count, 0);
});

test('geometry: edges are derived once per shared edge when a source sends none', () => {
    const { lines, lineAttributes } = edgesFromTriangles(Uint32Array.of(0, 1, 2, 0, 2, 3), Uint32Array.of(5, 6), 4);
    assert.equal(lines.length / 2, 5, 'two triangles sharing an edge have five distinct edges');
    assert.deepEqual([...lineAttributes], [5, 5, 5, 6, 6], 'the shared edge keeps the first triangle\'s attribute');
    assert.deepEqual([...lines], [0, 1, 1, 2, 2, 0, 2, 3, 3, 0]);
    assert.equal(edgesFromTriangles(Uint32Array.of(0, 0, 1), Uint32Array.of(0), 2).lines.length / 2, 1, 'a degenerate edge is skipped, and the two real ones are the same edge');
    assert.equal(edgesFromTriangles(new Uint32Array(0), new Uint32Array(0), 0).lines.length, 0);
});

test('picking: a hit point is the ray meeting the plane of the reported triangle', () => {
    const tri = [[0, 0, 5], [10, 0, 5], [0, 10, 5]];
    const p = rayTrianglePlane({ origin: [2, 3, 50], direction: [0, 0, -1] }, tri);
    assert.deepEqual(p, [2, 3, 5]);
    const slanted = rayTrianglePlane({ origin: [0, 0, 10], direction: [Math.SQRT1_2, 0, -Math.SQRT1_2] }, tri);
    assert.ok(Math.abs(slanted[0] - 5) < 1e-9 && Math.abs(slanted[2] - 5) < 1e-9);
    const parallel = rayTrianglePlane({ origin: [0, 0, 9], direction: [1, 0, 0] }, tri);
    assert.ok(Math.abs(parallel[0] - 10 / 3) < 1e-9 && Math.abs(parallel[1] - 10 / 3) < 1e-9, 'a grazing ray falls back to the triangle centre');
});

test('live: feed entities are validated and normalized', () => {
    assert.equal(normalizeEntity(null), null);
    assert.equal(normalizeEntity({ g: 5, x: 1, y: 2, z: 3 }), null);
    assert.equal(normalizeEntity({ g: 'ab', x: 1, y: 2 }), null);
    assert.equal(normalizeEntity({ g: 'ab', x: 1, y: 2, z: Number.NaN }), null);
    const e = normalizeEntity({
        g: 'abc123', k: 'weird', e: 1234, n: 'Wolf', x: 1, y: 2, z: 3, o: 1.5, hp: 80, mt: 'Chase', tg: 'ff00', home: [9, 9, 9, 9],
        sp: { id: 4, start: [0, 0, 0], pts: [[1, 0, 0], [2, 0, 0]], t0: 1000, dur: 500, run: true },
        route: { pts: [[0, 0, 0], [1, 1, 1]], next: 1, flags: ['Partial', 7] },
    });
    assert.equal(e.guid, 'ABC123');
    assert.equal(e.kind, 'creature', 'an unknown kind is drawn as a creature');
    assert.deepEqual(e.pos, [1, 2, 3]);
    assert.equal(e.target, 'FF00');
    assert.deepEqual(e.home, [9, 9, 9]);
    assert.deepEqual(e.spline, { id: 4, path: [[0, 0, 0], [1, 0, 0], [2, 0, 0]], t0: 1000, dur: 500, run: true });
    assert.deepEqual(e.route, { pts: [[0, 0, 0], [1, 1, 1]], next: 1, flags: ['Partial', '7'] });
    assert.equal(motionClass(e), 'chase');
    const bare = normalizeEntity({ g: 'a', k: 'player', x: 0, y: 0, z: 0, sp: { start: [0, 0, 0], pts: [[1, 'x', 0]], t0: 0, dur: 1 }, route: { pts: [[0, 0, 0]] } });
    assert.equal(bare.spline, null);
    assert.equal(bare.route, null);
    assert.equal(bare.kind, 'player');
    assert.equal(bare.orientation, null);
    assert.equal(bare.entry, null);
    assert.equal(bare.name, '');
});

test('live: splines are walked at constant speed', () => {
    const path = [[0, 0, 0], [10, 0, 0], [10, 10, 0]];
    assert.deepEqual(pointAlong(path, 0), [0, 0, 0]);
    assert.deepEqual(pointAlong(path, 0.25), [5, 0, 0]);
    assert.deepEqual(pointAlong(path, 0.5), [10, 0, 0]);
    assert.deepEqual(pointAlong(path, 0.75), [10, 5, 0]);
    assert.deepEqual(pointAlong(path, 1), [10, 10, 0]);
    assert.deepEqual(pointAlong(path, 7), [10, 10, 0]);
    assert.deepEqual(pointAlong(path, -1), [0, 0, 0]);
    assert.deepEqual(pointAlong([[3, 3, 3]], 0.5), [3, 3, 3]);
    assert.equal(pointAlong([], 0.5), null);
    assert.deepEqual(pointAlong([[1, 1, 1], [1, 1, 1]], 0.5), [1, 1, 1], 'a path of zero length stays put');
    const spline = { path, t0: 1000, dur: 2000 };
    assert.deepEqual(splineHead(spline, 500), { point: [0, 0, 0], fraction: 0 });
    assert.deepEqual(splineHead(spline, 2000), { point: [10, 0, 0], fraction: 0.5 });
    assert.equal(splineHead(spline, 99999).fraction, 1);
    assert.equal(splineHead({ path, t0: 0, dur: 0 }, 5).fraction, 1);
});

const feedFrame = (overrides = {}) => ({
    op: 'frame', seq: 1, clockMs: 5000, map: 0, instance: 0, grids: [[14, 17], [0, 0], 'x', [1.5, 2]],
    counters: { total: 3 },
    entities: [
        { g: 'aa', k: 'player', n: 'Alice', x: 0, y: 0, z: 0, o: 0 },
        { g: 'bb', k: 'creature', n: 'Wolf', e: 299, x: 5, y: 0, z: 0, mt: 'Idle', hp: 100 },
        { g: 'cc', k: 'creature', n: 'Boar', e: 300, x: 3, y: 0, z: 0, mt: 'Chase', sp: { start: [3, 0, 0], pts: [[13, 0, 0]], t0: 5000, dur: 1000 } },
        { nonsense: true },
    ],
    ...overrides,
});

test('live: frames, trails and the connection', () => {
    const model = new LiveModel();
    const events = [];
    model.on('frame', () => events.push('frame'));
    model.on('status', (s) => events.push(`status:${s.connected}`));
    assert.equal(model.applyFrame(null, 0), false);
    assert.equal(model.applyFrame({ map: 'x', entities: [] }, 0), false);
    assert.equal(model.applyFrame({ map: 0 }, 0), false);
    assert.equal(model.framesSeen, 0);
    model.applyStatus({ connected: true, pid: 12, sha: 'abc', detail: 'ok' });
    assert.deepEqual(model.status, { connected: true, pid: 12, sha: 'abc', detail: 'ok' });
    assert.equal(model.applyFrame(feedFrame(), 1000), true);
    const frame = model.frameFor(0);
    assert.equal(frame.entities.size, 3, 'an entity without a usable position is dropped');
    assert.deepEqual(frame.grids, [{ x: 49, y: 46 }, { x: 63, y: 63 }], 'grids become tiles; malformed pairs are ignored');
    assert.equal(model.frameFor(1), null);
    assert.equal(model.latest, frame);
    assert.deepEqual(model.summary(), { frames: 1, total: 3, counts: { player: 1, bot: 0, creature: 2, gameobject: 0 } });
    assert.equal(model.find('bb').entity.name, 'Wolf');
    assert.equal(model.find('missing'), null);

    // Trails: a new point only after the entity has moved a yard; a frame is a full snapshot, so absent entities leave.
    model.applyFrame(feedFrame({ seq: 2, entities: [{ g: 'aa', k: 'player', x: 0.4, y: 0, z: 0 }, { g: 'bb', k: 'creature', x: 9, y: 0, z: 0 }] }), 2000);
    const trails = Object.fromEntries(model.trailsFor(model.frameFor(0)).map((t) => [t.guid, t.points.length]));
    assert.deepEqual(trails, { AA: 1, BB: 2 });
    assert.equal(model.frameFor(0).entities.has('CC'), false, 'cc left the map');
    assert.equal(model.find('cc'), null);

    model.applyStatus({ connected: false });
    assert.equal(model.frameFor(0), null, 'disconnecting forgets the frames');
    assert.equal(model.summary().total, 0);
    assert.deepEqual(events.filter((e) => e.startsWith('status')), ['status:true', 'status:false']);
    assert.equal(events.filter((e) => e === 'frame').length, 2);

    model.applyStatus('garbage');
    assert.deepEqual(model.status, { connected: false, pid: null, sha: null, detail: '' });

    const two = new LiveModel();
    two.applyStatus({ connected: true });
    two.applyFrame(feedFrame({ instance: 1 }), 100);
    two.applyFrame(feedFrame({ instance: 2, entities: [{ g: 'zz', k: 'bot', x: 1, y: 1, z: 1 }] }), 200);
    assert.equal(two.frameFor(0).instance, 2, 'the most recently received instance of a map wins');
    assert.equal(two.summary().frames, 2);
    const old = new LiveModel();
    old.applyStatus({ connected: true });
    for (let i = 0; i < 60; i++) {
        old.applyFrame(feedFrame({ entities: [{ g: 'aa', k: 'player', x: i * 2, y: 0, z: 0 }] }), 1000 + i);
    }
    assert.ok(old.trailsFor(old.frameFor(0))[0].points.length <= 48, 'a trail keeps its newest 48 points');
    old.applyFrame(feedFrame({ entities: [{ g: 'bb', k: 'player', x: 0, y: 0, z: 0 }] }), 1000 + 70_000);
    assert.deepEqual(old.trailsFor(old.frameFor(0)).map((t) => t.guid), ['BB'], 'the trail of an entity unseen for a minute is dropped');
});

test('live: filtering and picking entities', () => {
    const model = new LiveModel();
    model.applyStatus({ connected: true });
    model.applyFrame(feedFrame(), 0);
    const entities = [...model.frameFor(0).entities.values()];
    assert.deepEqual(filterEntities(entities, '').length, 3);
    assert.deepEqual(filterEntities(entities, 'wolf').map((e) => e.guid), ['BB']);
    assert.deepEqual(filterEntities(entities, '300').map((e) => e.guid), ['CC']);
    assert.deepEqual(filterEntities(entities, 'chase').map((e) => e.guid), ['CC']);
    assert.deepEqual(filterEntities(entities, 'player').map((e) => e.guid), ['AA']);
    assert.deepEqual(filterEntities(entities, 'cc').map((e) => e.guid), ['CC'], 'a guid matches too');
    assert.deepEqual(filterEntities(entities, 'zzz'), []);
    const screen = (p) => ({ x: p[0] * 10, y: p[1] * 10 });
    assert.equal(pickEntity(entities, screen, 31, 2).guid, 'CC', 'the nearest entity within the radius');
    assert.equal(pickEntity(entities, screen, 31, 40), null, 'nothing within 12 pixels');
    assert.equal(pickEntity(entities, () => null, 0, 0), null, 'entities that do not project are skipped');
    assert.equal(pickEntity(entities, screen, 44, 0, 30).guid, 'BB', 'with a bigger radius the nearest still wins');
});

test('live: what is drawn follows the layer styles', () => {
    const model = new LiveModel();
    model.applyStatus({ connected: true });
    const entities = [
        { g: 'aa', k: 'player', n: 'Alice', x: 0, y: 0, z: 0, o: 0 },
        { g: 'bb', k: 'creature', n: 'Wolf', x: 5, y: 0, z: 0, mt: 'Chase' },
        { g: 'cc', k: 'creature', n: 'Boar', x: 3, y: 0, z: 0, sp: { start: [3, 0, 0], pts: [[13, 0, 0]], t0: 5000, dur: 1000 } },
        { g: 'dd', k: 'bot', n: 'Bot', x: 1, y: 1, z: 1, route: { pts: [[1, 1, 1], [5, 5, 5]], next: 1, flags: ['Partial'] } },
        { g: 'ee', k: 'gameobject', n: 'Chest', x: 8, y: 8, z: 0 },
    ];
    model.applyFrame(feedFrame({ entities, grids: [[14, 17]] }), 1000);
    model.applyFrame(feedFrame({ entities: entities.map((e) => ({ ...e, x: e.x + 3 })), grids: [[14, 17]] }), 1100);
    const styles = (patch = {}) => {
        const s = {};
        for (const l of FALLBACK_LAYERS.filter((x) => x.id.startsWith('live.'))) {
            s[l.id] = { visible: l.defaultVisible, color: l.defaultColor, opacity: 1, colorByAttribute: Object.keys(l.attributeColors).length > 0, attributeColors: l.attributeColors };
        }
        return { ...s, ...patch };
    };

    const none = buildLiveGeometry(model, 7, styles(), 1100, null, null);
    assert.equal(none.frame, null, 'no frame for this map');
    assert.equal(none.markers, null);

    const g = buildLiveGeometry(model, 0, styles(), 1100, null, null);
    assert.equal(g.counts.entities, 4, 'players, bots and creatures draw; game objects are off by default');
    assert.equal(g.counts.splines, 1);
    assert.equal(g.counts.routes, 1);
    assert.ok(g.dashed, 'a partial route is dashed');
    assert.ok(g.markers.count >= 4 + 1 + 1, 'an entity marker each, plus the spline head and the next route waypoint');
    assert.ok(g.solid.count >= 1 + 1, 'the spline and the player\'s facing tick');
    assert.deepEqual(g.labelCandidates.map((l) => l.text).sort(), ['Alice', 'Bot'], 'creatures are labelled only when picked');

    const picked = buildLiveGeometry(model, 0, styles(), 1100, 'BB', null);
    assert.ok(picked.labelCandidates.some((l) => l.text === 'Wolf' && l.priority === 3));
    assert.equal(picked.markers.count, g.markers.count + 1, 'the picked entity gets a selection ring');

    const off = buildLiveGeometry(model, 0, styles({ 'live.players': { visible: false }, 'live.bots': { visible: false }, 'live.creatures': { visible: false }, 'live.splines': { visible: false }, 'live.routes': { visible: false } }), 1100, null, null);
    assert.equal(off.markers, null);
    assert.equal(off.solid, null);
    assert.deepEqual(off.counts, { entities: 0, splines: 0, routes: 0 });

    const grids = buildLiveGeometry(model, 0, styles({ 'live.grids': { visible: true, color: '#1F5FFF', opacity: 1 }, 'live.players': { visible: false }, 'live.bots': { visible: false }, 'live.creatures': { visible: false }, 'live.splines': { visible: false }, 'live.routes': { visible: false } }), 1100, null, null);
    assert.equal(grids.solid.count, 4, 'a loaded grid is a closed rectangle');
    const trails = buildLiveGeometry(model, 0, styles({ 'live.trails': { visible: true, color: '#93A1AA', opacity: 1 }, 'live.players': { visible: false }, 'live.bots': { visible: false }, 'live.creatures': { visible: false }, 'live.splines': { visible: false }, 'live.routes': { visible: false } }), 1100, null, null);
    assert.ok(trails.solid.count >= 4, 'each entity that moved three yards has a trail segment');
    const stale = buildLiveGeometry(model, 0, styles({ 'live.trails': { visible: true, color: '#93A1AA', opacity: 1 }, 'live.players': { visible: false }, 'live.bots': { visible: false }, 'live.creatures': { visible: false }, 'live.splines': { visible: false }, 'live.routes': { visible: false } }), 1100 + 25_000, null, null);
    assert.equal(stale.solid, null, 'trail segments fade out after twenty seconds');
});

test('labels: placed in priority order without overlapping, and only when visible', () => {
    const camera = makeCamera({ target: [0, 0, 0], distance: 50, yaw: 0, pitch: 30, fov: 55, mode: 'orbit' });
    const measure = (text) => text.length * 6;
    const centre = project(camera, [0, 0, 0], 1);
    assert.ok(Math.abs(centre.x) < 1e-9 && Math.abs(centre.y) < 1e-9);
    const placed = layoutLabels([{ at: [0, 0, 0], text: 'centre', color: 1 }], camera, 800, 800, measure);
    assert.deepEqual(placed, [{ x: 400 + 8 + 3, y: 400 - 8 - 3, text: 'centre', color: 1 }]);

    const overlap = layoutLabels([
        { at: [0, 0, 0], text: 'low', color: 1, priority: 0 },
        { at: [0, 0, 0], text: 'high', color: 2, priority: 5 },
        { at: [0, 0, 1], text: 'also overlaps', color: 3, priority: 0 },
    ], camera, 800, 800, measure);
    assert.deepEqual(overlap.map((l) => l.text), ['high'], 'the highest priority label wins the spot');

    const behind = layoutLabels([{ at: [-5000, 0, 0], text: 'behind', color: 1 }, { at: [0, 5000, 0], text: 'off screen', color: 1 }], camera, 800, 800, measure);
    assert.deepEqual(behind, []);
    const many = Array.from({ length: 10 }, (_, i) => ({ at: [0, (i - 5) * 4, 0], text: `l${i}`, color: 1 }));
    assert.ok(layoutLabels(many, camera, 800, 800, measure).length >= 5);
    assert.equal(layoutLabels(many, camera, 800, 800, measure, 3).length, 3, 'the cap is honoured');
    assert.equal(cssFromPacked(0xFF3080FF), 'rgb(255, 128, 48)');
    assert.equal(cssFromPacked(packColor('#102030')), 'rgb(16, 32, 48)');
});

test('hotkeys: digit keys toggle groups, hiding when any layer is on', () => {
    const layers = infos();
    const view = defaultView();
    const collision = groupToggle(layers, view, 'Collision');
    assert.deepEqual(collision, { ids: ['vmap.wmo', 'vmap.m2', 'vmap.bounds'], visible: false });
    const reference = groupToggle(layers, view, 'Reference');
    assert.equal(reference.visible, true);
    assert.equal(reference.ids.length, 10, 'with no defaults, every layer of the group is shown (the 7 plus the reserved ref.terrain.tex, ref.wmo.tex, ref.m2.tex)');
    const off = allOff();
    assert.deepEqual(groupToggle(layers, off, 'Terrain'), { ids: ['terrain', 'terrain.holes', 'terrain.noheight'], visible: true }, 'showing a group restores its default layers');
    assert.equal(groupToggle(layers.map((l) => ({ ...l, available: l.group === 'Terrain' ? false : l.available })), view, 'Terrain'), null, 'a group with no available layer does nothing');
    assert.equal(groupToggle(layers, view, 'Nowhere'), null);
    assert.equal(groupForDigit('1'), 'World');
    assert.equal(groupForDigit('4'), 'Collision');
    assert.equal(groupForDigit('8'), 'Live');
    assert.equal(groupForDigit('9'), null);
    assert.equal(groupForDigit('0'), null);
});

test('ui helpers: the short texts the panels show', () => {
    assert.equal(tileCountText(null), '');
    assert.equal(tileCountText({ total: 0 }), '');
    assert.equal(tileCountText({ total: 12, Ok: 9, Empty: 0, Missing: 2, Rejected: 1, Error: 0, Unknown: 0 }), '9 ok · 2 missing · 1 rejected');
    assert.equal(tileCountText({ total: 3, Ok: 1, Empty: 1, Missing: 0, Rejected: 0, Error: 1, Unknown: 1 }), '1 ok · 1 empty · 1 failed · 1 pending');

    assert.equal(lastCommandText(null), 'No automation commands yet');
    assert.equal(lastCommandText({ name: 'view.set', origin: 'ui:abc', ok: true, elapsedMs: 3.2 }), 'view.set from ui:abc · ok · 3.2 ms');
    assert.equal(lastCommandText({ name: 'query.los', ok: false, message: 'no path', elapsedMs: Number.NaN }), 'query.los · failed: no path');
    assert.equal(lastCommandText({ name: 'x', ok: false }), 'x · failed');

    assert.equal(headingName(0), 'north');
    assert.equal(headingName(90), 'west');
    assert.equal(headingName(180), 'south');
    assert.equal(headingName(270), 'east');
    assert.equal(headingName(45), 'north-west');
    assert.equal(headingName(-45), 'north-east');
    assert.equal(headingName(359), 'north');

    const status = (map) => (layer) => map[layer] ?? null;
    assert.equal(fileState('map', { x: 1, y: 2 }, status({ terrain: { state: 'Ok' }, liquid: { state: 'Missing' } })), 'Ok');
    assert.equal(fileState('map', { x: 1, y: 2 }, status({ terrain: { state: 'Missing' }, liquid: { state: 'Rejected' } })), 'Rejected', 'the more informative state wins');
    assert.equal(fileState('vmap', { x: 1, y: 2 }, status({ 'vmap.m2': { state: 'Empty' }, 'vmap.wmo': { state: 'Missing' } })), 'Empty');
    assert.equal(fileState('mmap', { x: 1, y: 2 }, status({})), 'Unknown');

    const server = { name: 'query.los', schema: { required: ['from', 'to'], properties: { from: { type: 'array', items: { type: 'number' }, minItems: 3, maxItems: 3, description: 'Start.' }, to: { type: 'array', items: { type: 'number' }, minItems: 3, maxItems: 3 }, map: { type: 'integer' } } } };
    const reference = { name: 'ref.query.los', schema: { required: ['from'], properties: { from: { type: 'array', items: { type: 'number' }, minItems: 3, maxItems: 3 }, to: { type: 'array', items: { type: 'number' }, minItems: 3, maxItems: 3 }, strict: { type: 'boolean', description: 'Exact.' } } } };
    const union = unionFields(server, reference);
    assert.deepEqual(union.map((f) => [f.name, f.required, f.only]), [['from', true, null], ['to', false, null], ['map', false, 'server'], ['strict', false, 'reference']]);
    assert.equal(union.find((f) => f.name === 'from').description, 'Start.', 'the first description found is kept');
    assert.equal(union.find((f) => f.name === 'strict').description, 'Exact. (reference command only)');
    assert.equal(union.find((f) => f.name === 'map').description, '(server command only)');
    assert.deepEqual(unionFields(server, null).map((f) => f.only), [null, null, null], 'with one command nothing is "only"');
    assert.deepEqual(unionFields(null, null), []);
});
