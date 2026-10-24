// Helpers over the view JSON (ViewState.ToJson in tools/ArcaneCore.Atlas/Core/ViewState.cs):
//   {map, camera:{target:[x,y,z], distance, yaw, pitch, fov, mode}, layers:{id:{visible,color,opacity,mode,colorByAttribute}},
//    compare, split, loadRadius, theme, hud, follow, textureRadius, textureBudgetMb}
// Enum values are lower-case names: modes orbit|fly|top, layer modes wire|fill|both|xray, compare server|reference|split|diff,
// themes graphite|vellum.

import { FALLBACK_LAYERS } from './catalog.js';
import { clamp, deepClone, isFiniteNumber } from './util.js';
import { normalizeCamera } from './camera.js';

export const CAMERA_MODES = ['orbit', 'fly', 'top'];
export const LAYER_MODES = ['wire', 'fill', 'both', 'xray'];
export const COMPARE_MODES = ['server', 'reference', 'split', 'diff'];
export const THEMES = ['graphite', 'vellum'];
export const MAX_LOAD_RADIUS = 4;
export const MAX_TEXTURE_RADIUS = 2;
export const MIN_TEXTURE_BUDGET_MB = 64;
export const MAX_TEXTURE_BUDGET_MB = 2048;

/** ViewState.Default: Goldshire's Lion's Pride Inn (tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:57). */
export function defaultView() {
    const layers = {};
    for (const l of FALLBACK_LAYERS) {
        layers[l.id] = {
            visible: l.defaultVisible,
            color: l.defaultColor,
            opacity: 1,
            mode: 'wire',
            colorByAttribute: Object.keys(l.attributeColors).length > 0,
        };
    }
    return {
        map: 0,
        camera: { target: [-9462.66, 16.1915, 57.0459], distance: 140, yaw: 45, pitch: 45, fov: 55, mode: 'orbit' },
        layers,
        compare: 'server',
        split: 0.5,
        loadRadius: 1,
        theme: 'graphite',
        hud: true,
        follow: null,
        textureRadius: 1,
        textureBudgetMb: 384,
    };
}

const pick = (value, allowed, fallback) => (allowed.includes(value) ? value : fallback);

/**
 * Makes a view from whatever the host sent: every field is checked and a missing or malformed one keeps its default,
 * so a partial answer cannot leave the renderer with an undefined camera or layer table.
 */
export function normalizeView(json, base = defaultView()) {
    const out = deepClone(base);
    if (json === null || typeof json !== 'object') {
        return out;
    }
    if (isFiniteNumber(json.map) && json.map >= 0) {
        out.map = Math.trunc(json.map);
    }
    const c = json.camera;
    if (c && typeof c === 'object') {
        const t = Array.isArray(c.target) && c.target.length === 3 && c.target.every(isFiniteNumber) ? c.target : out.camera.target;
        out.camera = normalizeCamera({
            target: t,
            distance: isFiniteNumber(c.distance) ? c.distance : out.camera.distance,
            yaw: isFiniteNumber(c.yaw) ? c.yaw : out.camera.yaw,
            pitch: isFiniteNumber(c.pitch) ? c.pitch : out.camera.pitch,
            fov: isFiniteNumber(c.fov) ? c.fov : out.camera.fov,
            mode: pick(c.mode, CAMERA_MODES, out.camera.mode),
        });
    }
    if (json.layers && typeof json.layers === 'object') {
        for (const [id, s] of Object.entries(json.layers)) {
            if (s === null || typeof s !== 'object') {
                continue;
            }
            const prev = out.layers[id] ?? { visible: false, color: '#93A1AA', opacity: 1, mode: 'wire', colorByAttribute: false };
            out.layers[id] = {
                visible: typeof s.visible === 'boolean' ? s.visible : prev.visible,
                color: typeof s.color === 'string' && /^#[0-9a-fA-F]{6}$/.test(s.color) ? s.color.toUpperCase() : prev.color,
                opacity: isFiniteNumber(s.opacity) ? clamp(s.opacity, 0, 1) : prev.opacity,
                mode: pick(s.mode, LAYER_MODES, prev.mode),
                colorByAttribute: typeof s.colorByAttribute === 'boolean' ? s.colorByAttribute : prev.colorByAttribute,
            };
        }
    }
    out.compare = pick(json.compare, COMPARE_MODES, out.compare);
    out.split = isFiniteNumber(json.split) ? clamp(json.split, 0.05, 0.95) : out.split;
    out.loadRadius = isFiniteNumber(json.loadRadius) ? clamp(Math.trunc(json.loadRadius), 0, MAX_LOAD_RADIUS) : out.loadRadius;
    out.theme = pick(json.theme, THEMES, out.theme);
    out.hud = typeof json.hud === 'boolean' ? json.hud : out.hud;
    out.textureRadius = isFiniteNumber(json.textureRadius) ? clamp(Math.trunc(json.textureRadius), 0, MAX_TEXTURE_RADIUS) : out.textureRadius;
    out.textureBudgetMb = isFiniteNumber(json.textureBudgetMb)
        ? clamp(Math.trunc(json.textureBudgetMb), MIN_TEXTURE_BUDGET_MB, MAX_TEXTURE_BUDGET_MB)
        : out.textureBudgetMb;
    out.follow = typeof json.follow === 'string' && json.follow !== '' ? json.follow : json.follow === null ? null : out.follow;
    return out;
}

/** The camera in `view.set` patch form (a merge patch replaces arrays whole, so the target is always complete). */
export function cameraPatch(camera) {
    return { camera: { target: [...camera.target], distance: camera.distance, yaw: camera.yaw, pitch: camera.pitch, fov: camera.fov, mode: camera.mode } };
}

/** Style of one layer in a view, with a neutral fallback for ids the view does not mention. */
export function layerStyle(view, id, info) {
    return view.layers[id] ?? {
        visible: info?.defaultVisible ?? false,
        color: info?.defaultColor ?? '#93A1AA',
        opacity: 1,
        mode: 'wire',
        colorByAttribute: false,
    };
}
