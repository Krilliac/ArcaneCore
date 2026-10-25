// Layer catalog the UI falls back on before (or without) the host's layers.list answer: ids, groups, titles and default
// colours. It mirrors tools/ArcaneCore.Atlas/Core/LayerCatalog.cs; a test compares it with the C# catalog so the two
// cannot drift. The host's layers.list always wins where it answers (see normalizeLayerList).

export const GROUPS = [
    { id: 'World', title: 'World', side: 'both' },
    { id: 'Terrain', title: 'Terrain', side: 'server' },
    { id: 'Liquid', title: 'Liquid', side: 'server' },
    { id: 'Collision', title: 'Collision', side: 'server' },
    { id: 'Navmesh', title: 'Navmesh', side: 'server' },
    { id: 'Reference', title: 'Reference', side: 'reference' },
    { id: 'Diff', title: 'Differences', side: 'diff' },
    { id: 'Live', title: 'Live', side: 'both' },
];

const LIQUID = { water: '#4FA3E0', ocean: '#2E6FD1', magma: '#FF6B3D', slime: '#8BD450', deep: '#2A4F9E' };
const NAV = { ground: '#3CCB9A', steep: '#C9A227', water: '#49B6FF', magma: '#FF6B3D', slime: '#8BD450' };
const TILES = { ok: '#3B4E5A', empty: '#5E6B73', missing: '#2A353C', rejected: '#E5484D' };
const WMO = { indoor: '#D9C9A3', outdoor: '#E8E3D9' };
const REF_WMO = { collide: '#C6B8FF', render: '#8E7FD6' };
const DIFF = { 'only-ref': '#FF3EA5', 'only-server': '#39E5FF', expected: '#7C8A93' };
const CREATURE = { idle: '#C9D1D6', moving: '#FFD34D', chase: '#FF5A5A' };
const ROUTE = { ok: '#7FD3FF', partial: '#F5A524' };

const layer = (id, group, view, title, defaultColor, defaultVisible, attributeColors = {}, tiled = true) => ({
    id, group, view, title, description: '', defaultColor, defaultVisible, attributeColors, tiled,
});

export const FALLBACK_LAYERS = [
    layer('tiles', 'World', 'Server', 'Tile grid', '#3B4E5A', true, TILES),
    layer('terrain', 'Terrain', 'Server', 'Terrain', '#B9A77A', true),
    layer('terrain.holes', 'Terrain', 'Server', 'Terrain holes', '#E5484D', true),
    layer('terrain.noheight', 'Terrain', 'Server', 'Flat tiles', '#F5A524', true),
    layer('area', 'Terrain', 'Server', 'Area cells', '#7C8A93', false),
    layer('liquid', 'Liquid', 'Server', 'Terrain liquid', '#4FA3E0', true, LIQUID),
    layer('vmap.liquid', 'Liquid', 'Server', 'Model liquid', '#7FC8F8', false),
    layer('vmap.wmo', 'Collision', 'Server', 'WMO collision', '#E8E3D9', true, WMO),
    layer('vmap.m2', 'Collision', 'Server', 'M2 collision', '#F2A541', true),
    layer('vmap.bounds', 'Collision', 'Server', 'Model bounds', '#6E7C85', false),
    layer('nav.poly', 'Navmesh', 'Server', 'Navmesh polygons', '#3CCB9A', true, NAV),
    layer('nav.detail', 'Navmesh', 'Server', 'Navmesh detail', '#2A8F6E', false),
    layer('nav.portals', 'Navmesh', 'Server', 'Tile portals', '#FFFFFF', false),
    layer('nav.bv', 'Navmesh', 'Server', 'BV tree rejected', '#E5484D', false),
    layer('ref.terrain', 'Reference', 'Client', 'Client terrain', '#8FA9B8', false),
    layer('ref.liquid', 'Reference', 'Client', 'Client liquid', '#5DD6E8', false, LIQUID),
    layer('ref.wmo', 'Reference', 'Client', 'Client WMOs', '#C6B8FF', false, REF_WMO),
    layer('ref.m2.collision', 'Reference', 'Client', 'Client M2 collision', '#FFC97A', false),
    layer('ref.m2.render', 'Reference', 'Client', 'Client M2 meshes', '#B07AD8', false),
    layer('ref.terrain.tex', 'Reference', 'Client', 'Client terrain (textured)', '#8FA9B8', false),
    layer('ref.wmo.tex', 'Reference', 'Client', 'Client WMOs (textured)', '#C6B8FF', false),
    layer('ref.m2.tex', 'Reference', 'Client', 'Client M2 models (textured)', '#B07AD8', false),
    layer('ref.nav.poly', 'Reference', 'DotRecast', 'DotRecast polygons', '#FFD34D', false),
    layer('ref.nav.links', 'Reference', 'DotRecast', 'DotRecast links', '#FF8AD8', false),
    layer('diff.terrain', 'Diff', 'Diff', 'Terrain difference', '#FF3EA5', false, DIFF),
    layer('diff.models', 'Diff', 'Diff', 'Model difference', '#FF3EA5', false, DIFF),
    layer('diff.nav', 'Diff', 'Diff', 'Navmesh difference', '#FF3EA5', false, DIFF),
    layer('live.players', 'Live', 'Live', 'Players', '#FFFFFF', true, {}, false),
    layer('live.bots', 'Live', 'Live', 'Bots', '#7FD3FF', true, {}, false),
    layer('live.creatures', 'Live', 'Live', 'Creatures', '#C9D1D6', true, CREATURE, false),
    layer('live.gameobjects', 'Live', 'Live', 'Game objects', '#A3B1BA', false, {}, false),
    layer('live.splines', 'Live', 'Live', 'Movement splines', '#3CCB9A', true, {}, false),
    layer('live.routes', 'Live', 'Live', 'Bot routes', '#7FD3FF', true, ROUTE, false),
    layer('live.trails', 'Live', 'Live', 'Trails', '#93A1AA', false, {}, false),
    layer('live.grids', 'Live', 'Live', 'Loaded grids', '#1F5FFF', false, {}, false),
];

/** Live layers are drawn by the UI from live frames, never fetched as tiles. */
export const isLiveLayer = (id) => id.startsWith('live.');

const GROUP_ALIASES = new Map(GROUPS.map((g) => [g.id.toLowerCase(), g.id]));
GROUP_ALIASES.set('differences', 'Diff');

export function canonicalGroup(value) {
    return GROUP_ALIASES.get(String(value ?? '').toLowerCase()) ?? null;
}

export function groupSide(groupId) {
    return GROUPS.find((g) => g.id === groupId)?.side ?? 'server';
}

const asString = (v) => (typeof v === 'string' ? v : null);

/**
 * Merges the host's layers.list answer over the fallback. Accepts an array, or an object holding one in `layers`.
 * Unknown ids are kept (the host may know layers this UI was not built with) under their reported group, or Reference
 * when none is given. Field names are read tolerantly: defaultColor|color, defaultVisible|visible, attributeColors.
 * `available` is false only when the host says so (a layer with no registered source).
 */
export function normalizeLayerList(answer) {
    const merged = new Map(FALLBACK_LAYERS.map((l) => [l.id, { ...l, available: true, known: true }]));
    const items = Array.isArray(answer) ? answer : Array.isArray(answer?.layers) ? answer.layers : [];
    for (const item of items) {
        const id = asString(item?.id);
        if (!id) {
            continue;
        }
        const base = merged.get(id) ?? {
            id, group: 'Reference', view: 'Server', title: id, description: '', defaultColor: '#93A1AA',
            defaultVisible: false, attributeColors: {}, tiled: !isLiveLayer(id), available: true, known: false,
        };
        const attributeColors = item.attributeColors ?? item.attribute_colors;
        merged.set(id, {
            ...base,
            group: canonicalGroup(item.group) ?? base.group,
            view: asString(item.view) ?? base.view,
            title: asString(item.title) ?? base.title,
            description: asString(item.description) ?? base.description,
            defaultColor: asString(item.defaultColor) ?? asString(item.color) ?? base.defaultColor,
            defaultVisible: typeof item.defaultVisible === 'boolean' ? item.defaultVisible : base.defaultVisible,
            attributeColors: attributeColors && typeof attributeColors === 'object' && !Array.isArray(attributeColors)
                ? attributeColors
                : base.attributeColors,
            tiled: typeof item.tiled === 'boolean' ? item.tiled : base.tiled,
            available: layerAvailable(item) ?? base.available,
        });
    }
    return [...merged.values()];
}

function layerAvailable(item) {
    for (const key of ['available', 'hasSource', 'registered']) {
        if (typeof item[key] === 'boolean') {
            return item[key];
        }
    }
    if ('source' in item) {
        return item.source !== null && item.source !== undefined && item.source !== false && item.source !== '';
    }
    if (typeof item.sources === 'number') {
        return item.sources > 0;
    }
    if (Array.isArray(item.sources)) {
        return item.sources.length > 0;
    }
    return undefined;
}

/** Layers grouped in catalog order: [{group, layers: [...]}], empty groups omitted. */
export function groupLayers(layers) {
    const out = [];
    for (const group of GROUPS) {
        const members = layers.filter((l) => l.group === group.id);
        if (members.length > 0) {
            out.push({ group, layers: members });
        }
    }
    return out;
}
