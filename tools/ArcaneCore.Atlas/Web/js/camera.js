// The camera model. Functions direction..project are an exact port of tools/ArcaneCore.Atlas/Core/CameraMath.cs, so the
// browser, the headless renderer and the pick rays the server builds agree: world space is WoW's (x north, y west,
// z up, right-handed); yaw turns from +X toward +Y; pitch is degrees below the horizon, so the look direction is
// (cos p cos y, cos p sin y, -sin p). Orbit: the eye sits `distance` behind the target. Fly: the eye is the target.
// Top: orthographic, straight down, screen-up along the yaw heading, `distance` yards from the centre to the top edge.
// Matrices are column-major (element [col * 4 + row]), the layout WebGL's uniformMatrix4fv takes. The view matrix has no
// translation: renderers subtract the eye (in double) from every chunk origin first.
//
// A camera is {target: [x, y, z], distance, yaw, pitch, fov, mode} with mode 'orbit' | 'fly' | 'top' (the view JSON).
// Points are plain [x, y, z] arrays.
//
// Below the port: the interaction maths (drag, pan, zoom toward a point, key movement, mode switches, framing), kept
// here and pure so they can be tested against the port.

import { DEG, clamp, wrapDegrees } from './util.js';

export const TOP_EYE_HEIGHT = 5000;
export const MIN_DISTANCE = 0.5;
export const MAX_DISTANCE = 40000;
export const MIN_FOV = 15;
export const MAX_FOV = 110;
export const MAX_PITCH = 89;

const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const scale = (p, k) => [p[0] * k, p[1] * k, p[2] * k];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const length = (p) => Math.sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
const normalize = (p) => {
    const l = length(p);
    return l > 0 ? scale(p, 1 / l) : p;
};

export const vec = { add, sub, scale, dot, cross, length, normalize };

export function direction(camera) {
    if (camera.mode === 'top') {
        return [0, 0, -1];
    }
    const yaw = camera.yaw * Math.PI / 180;
    const pitch = camera.pitch * Math.PI / 180;
    return [Math.cos(pitch) * Math.cos(yaw), Math.cos(pitch) * Math.sin(yaw), -Math.sin(pitch)];
}

export function eye(camera) {
    switch (camera.mode) {
        case 'fly':
            return [...camera.target];
        case 'top':
            return [camera.target[0], camera.target[1], camera.target[2] + TOP_EYE_HEIGHT];
        default:
            return sub(camera.target, scale(direction(camera), camera.distance));
    }
}

/** Screen up in world space (world +Z, except top-down, where it is the yaw heading). */
export function up(camera) {
    if (camera.mode === 'top') {
        const yaw = camera.yaw * Math.PI / 180;
        return [Math.cos(yaw), Math.sin(yaw), 0];
    }
    return [0, 0, 1];
}

/** Near and far clip distances, yards. */
export function planes(camera) {
    switch (camera.mode) {
        case 'top':
            return { near: 1, far: TOP_EYE_HEIGHT + 2000 };
        case 'fly':
            return { near: 0.1, far: 6000 };
        default:
            return { near: clamp(camera.distance / 500, 0.05, 5), far: Math.max(6000, camera.distance * 4) };
    }
}

function basis(camera) {
    const f = normalize(direction(camera));
    const upVector = up(camera);
    let s = cross(f, upVector);
    if (length(s) < 1e-9) {
        // Looking straight up or down with world up: fall back to the yaw heading.
        const yaw = camera.yaw * Math.PI / 180;
        s = cross(f, [Math.cos(yaw), Math.sin(yaw), 0]);
    }
    s = normalize(s);
    return { side: s, up: cross(s, f), forward: f };
}

/** Camera-relative view matrix (rotation only; the eye is the origin). */
export function viewMatrix(camera) {
    const { side: s, up: u, forward: f } = basis(camera);
    return [
        s[0], u[0], -f[0], 0,
        s[1], u[1], -f[1], 0,
        s[2], u[2], -f[2], 0,
        0, 0, 0, 1,
    ];
}

/** Projection matrix (OpenGL clip space, z in [-1, 1]) for a viewport of the given width/height ratio. */
export function projectionMatrix(camera, aspect) {
    if (!Number.isFinite(aspect) || aspect <= 0) {
        throw new RangeError('Aspect ratio must be positive.');
    }
    const { near, far } = planes(camera);
    if (camera.mode === 'top') {
        const hh = camera.distance;
        const w = hh * aspect;
        return [
            1 / w, 0, 0, 0,
            0, 1 / hh, 0, 0,
            0, 0, -2 / (far - near), 0,
            0, 0, -(far + near) / (far - near), 1,
        ];
    }
    const focal = 1 / Math.tan(camera.fov * Math.PI / 360);
    return [
        focal / aspect, 0, 0, 0,
        0, focal, 0, 0,
        0, 0, (far + near) / (near - far), -1,
        0, 0, 2 * far * near / (near - far), 0,
    ];
}

/**
 * The world-space ray under a viewport point given in normalized device coordinates (x right, y up, each in [-1, 1]).
 * The ray starts on the near plane (perspective) or the eye plane (top-down).
 */
export function rayThrough(camera, mapId, ndcX, ndcY, aspect, maxDistance = 2000) {
    const { side: s, up: u, forward: f } = basis(camera);
    const origin = eye(camera);
    if (camera.mode === 'top') {
        const hh = camera.distance;
        return {
            mapId,
            origin: add(add(origin, scale(s, ndcX * hh * aspect)), scale(u, ndcY * hh)),
            direction: f,
            maxDistance: TOP_EYE_HEIGHT + 2000,
        };
    }
    const tan = Math.tan(camera.fov * Math.PI / 360);
    const dir = normalize(add(add(f, scale(s, ndcX * tan * aspect)), scale(u, ndcY * tan)));
    return { mapId, origin, direction: dir, maxDistance };
}

function multiply(m, v) {
    const r = [0, 0, 0, 0];
    for (let row = 0; row < 4; row++) {
        r[row] = m[row] * v[0] + m[4 + row] * v[1] + m[8 + row] * v[2] + m[12 + row] * v[3];
    }
    return r;
}

/** Projects a world point to normalized device coordinates; null when it is behind the camera. */
export function project(camera, world, aspect) {
    const view = viewMatrix(camera);
    const projection = projectionMatrix(camera, aspect);
    const rel = sub(world, eye(camera));
    const v = multiply(view, [rel[0], rel[1], rel[2], 1]);
    const clip = multiply(projection, v);
    if (clip[3] <= 0) {
        return null;
    }
    return { x: clip[0] / clip[3], y: clip[1] / clip[3], depth: clip[2] / clip[3] };
}

// ---------------------------------------------------------------------------------------------------------------
// Interaction maths (UI only; not part of the shared contract).

export function makeCamera(partial = {}) {
    return normalizeCamera({
        target: [0, 0, 0],
        distance: 140,
        yaw: 45,
        pitch: 45,
        fov: 55,
        mode: 'orbit',
        ...partial,
    });
}

/** Clamps every field to the range ViewState.FromJson accepts, so a local edit is never refused by the server. */
export function normalizeCamera(camera) {
    return {
        target: [camera.target[0], camera.target[1], camera.target[2]],
        distance: clamp(camera.distance, MIN_DISTANCE, MAX_DISTANCE),
        yaw: wrapDegrees(camera.yaw),
        pitch: clamp(camera.pitch, -MAX_PITCH, MAX_PITCH),
        fov: clamp(camera.fov, MIN_FOV, MAX_FOV),
        mode: camera.mode === 'fly' || camera.mode === 'top' ? camera.mode : 'orbit',
    };
}

/** Float32 matrices for the shaders, from the double ones. */
export function toFloat32(matrix) {
    return new Float32Array(matrix);
}

export function screenToNdc(px, py, width, height) {
    return { x: (px / width) * 2 - 1, y: 1 - (py / height) * 2 };
}

export function ndcToScreen(x, y, width, height) {
    return { x: (x + 1) * 0.5 * width, y: (1 - y) * 0.5 * height };
}

/** World yards covered by one viewport pixel at `depth` yards in front of a perspective camera (or anywhere in top view). */
export function yardsPerPixel(camera, viewportHeight, depth) {
    if (camera.mode === 'top') {
        return (2 * camera.distance) / viewportHeight;
    }
    return (2 * Math.tan(camera.fov * Math.PI / 360) * depth) / viewportHeight;
}

/** Where a ray meets the horizontal plane z = planeZ, or null when it is parallel or points away. */
export function intersectPlaneZ(ray, planeZ) {
    const dz = ray.direction[2];
    if (Math.abs(dz) < 1e-9) {
        return null;
    }
    const t = (planeZ - ray.origin[2]) / dz;
    if (t < 0 || !Number.isFinite(t)) {
        return null;
    }
    return add(ray.origin, scale(ray.direction, t));
}

/**
 * Left-drag orbit. Orbit: dragging right swings the camera left around the target (yaw falls) and dragging down raises
 * it (pitch rises). Fly: mouse look, the same signs. Top: only the heading turns.
 */
export function orbitDrag(camera, dxPixels, dyPixels, degreesPerPixel = 0.25) {
    const next = { ...camera, target: [...camera.target] };
    next.yaw = wrapDegrees(camera.yaw - dxPixels * degreesPerPixel);
    if (camera.mode !== 'top') {
        next.pitch = clamp(camera.pitch + dyPixels * degreesPerPixel, -MAX_PITCH, MAX_PITCH);
    }
    return next;
}

/**
 * Right-drag pan that keeps the world point under the cursor under the cursor: `start` is the camera when the drag
 * began, (ndc0 -> ndc1) the cursor motion. Orbit and top grab a point on the plane z = start target z. A fly camera
 * sits on that plane, so it grabs the point on the plane facing the camera `max(distance, 20)` yards ahead instead,
 * which slides the camera sideways and up or down. Returns the new camera, or the start camera when a ray misses the
 * plane (a horizon-grazing view).
 */
export function panGrab(start, ndc0, ndc1, aspect) {
    if (start.mode === 'fly') {
        const forward = direction(start);
        const depth = Math.max(start.distance, 20);
        const grab = (ndc) => {
            const ray = rayThrough(start, 0, ndc.x, ndc.y, aspect);
            return add(ray.origin, scale(ray.direction, depth / dot(ray.direction, forward)));
        };
        return { ...start, target: add(start.target, sub(grab(ndc0), grab(ndc1))) };
    }
    const planeZ = start.target[2];
    const a = intersectPlaneZ(rayThrough(start, 0, ndc0.x, ndc0.y, aspect), planeZ);
    const b = intersectPlaneZ(rayThrough(start, 0, ndc1.x, ndc1.y, aspect), planeZ);
    if (!a || !b) {
        return start;
    }
    const delta = [a[0] - b[0], a[1] - b[1], 0];
    return { ...start, target: add(start.target, delta) };
}

/** Vertical pan (shift + right drag): moves the target along world Z by the dragged pixels. */
export function panVertical(start, dyPixels, viewportHeight) {
    const depth = start.mode === 'fly' ? Math.max(start.distance, 20) : start.distance;
    const yards = yardsPerPixel(start, viewportHeight, depth) * dyPixels;
    return { ...start, target: [start.target[0], start.target[1], start.target[2] + yards] };
}

/**
 * Zoom by `factor` (< 1 zooms in) while the world point `hit` stays at the same pixel. Orbit and top change the distance
 * and slide the target so the direction from the eye to `hit` is unchanged; fly moves the eye along the line to `hit`.
 */
export function zoomAt(camera, factor, hit) {
    if (camera.mode === 'fly') {
        const e = eye(camera);
        const toHit = sub(hit, e);
        const dist = length(toHit);
        const step = Math.min(dist * (1 - factor), Math.max(dist - 0.2, 0));
        return { ...camera, target: add(e, scale(normalize(toHit), step)) };
    }
    const distance = clamp(camera.distance * factor, MIN_DISTANCE, MAX_DISTANCE);
    const k = distance / camera.distance;
    const target = add(hit, scale(sub(camera.target, hit), k));
    return { ...camera, distance, target };
}

/**
 * WASD/QE movement for `seconds` at `speed` yards per second. forward/right/upAmount are -1, 0 or 1. Fly moves along the
 * look direction; orbit and top slide the target over the ground along the heading; up is always world Z.
 */
export function moveCamera(camera, forward, right, upAmount, speed, seconds) {
    const yaw = camera.yaw * DEG;
    const heading = [Math.cos(yaw), Math.sin(yaw), 0];
    const side = [Math.sin(yaw), -Math.cos(yaw), 0];
    const look = camera.mode === 'fly' ? direction(camera) : heading;
    const step = speed * seconds;
    const delta = add(add(scale(look, forward * step), scale(side, right * step)), [0, 0, upAmount * step]);
    return { ...camera, target: add(camera.target, delta) };
}

/**
 * Moves between modes without moving what the user sees. Top is the orbit camera seen straight down, so leaving it
 * restores the orbit camera at the stored yaw, pitch and distance.
 */
export function switchMode(camera, mode) {
    if (mode === camera.mode) {
        return camera;
    }
    const base = camera.mode === 'top' ? { ...camera, mode: 'orbit' } : camera;
    if (mode === 'fly') {
        return base.mode === 'fly' ? base : { ...base, mode, target: eye(base) };
    }
    if (mode === 'orbit') {
        if (base.mode === 'fly') {
            return { ...base, mode, target: add(eye(base), scale(direction(base), base.distance)) };
        }
        return { ...base, mode };
    }
    const orbit = base.mode === 'fly' ? switchMode(base, 'orbit') : base;
    return { ...orbit, mode: 'top' };
}

/** The camera looking at the box [min, max] from a distance that fits it, in the camera's own mode. */
export function frameBounds(camera, min, max, aspect = 16 / 9) {
    const centre = [(min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2];
    const sx = max[0] - min[0];
    const sy = max[1] - min[1];
    const sz = max[2] - min[2];
    const radius = Math.max(Math.sqrt(sx * sx + sy * sy + sz * sz) / 2, 5);
    if (camera.mode === 'top') {
        return { ...camera, target: centre, distance: clamp((radius * 1.1) / Math.min(1, aspect), MIN_DISTANCE, MAX_DISTANCE) };
    }
    const tan = Math.tan(camera.fov * Math.PI / 360);
    const distance = clamp((radius / tan) * 1.15, MIN_DISTANCE, MAX_DISTANCE);
    const orbit = { ...(camera.mode === 'fly' ? switchMode(camera, 'orbit') : camera), target: centre, distance };
    return camera.mode === 'fly' ? switchMode(orbit, 'fly') : orbit;
}

/** Looks at one point from `distance` yards, keeping the current heading and angle. */
export function lookAtPoint(camera, point, distance) {
    if (camera.mode === 'fly') {
        const back = scale(direction(camera), distance);
        return { ...camera, target: sub(point, back) };
    }
    return { ...camera, target: [...point], distance: clamp(distance, MIN_DISTANCE, MAX_DISTANCE) };
}

/** Unit vector from the eye through a world point, for view-space tests; null for a point at the eye. */
export function dirTo(camera, point) {
    const d = sub(point, eye(camera));
    return length(d) > 0 ? normalize(d) : null;
}
