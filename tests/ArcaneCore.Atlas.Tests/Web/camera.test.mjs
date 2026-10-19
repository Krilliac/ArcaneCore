// The camera interaction maths (orbit drag, grab-pan, zoom toward a point, key movement, mode switches, framing). The
// shared projection maths is held to the C# values in golden.test.mjs; these tests hold the interactions to their promises
// using that projection: a grabbed point stays under the cursor, a zoom keeps the pointed-at spot still, switching modes
// does not move what the user sees.

import assert from 'node:assert/strict';
import test from 'node:test';
import * as cam from '../../../tools/ArcaneCore.Atlas/Web/js/camera.js';

const ASPECT = 16 / 9;
const INN = [-9462.66, 16.1915, 57.0459];
const orbit = (extra = {}) => cam.makeCamera({ target: INN, distance: 140, yaw: 45, pitch: 45, fov: 55, mode: 'orbit', ...extra });
const near = (a, b, tolerance, what) => {
    assert.equal(a.length, b.length, what);
    for (let i = 0; i < a.length; i++) {
        assert.ok(Math.abs(a[i] - b[i]) <= tolerance, `${what}[${i}]: ${a[i]} vs ${b[i]}`);
    }
};

test('makeCamera and normalizeCamera clamp to what the server accepts', () => {
    const c = cam.normalizeCamera({ target: [1, 2, 3], distance: 1e9, yaw: -450, pitch: 120, fov: 200, mode: 'spin' });
    assert.deepEqual(c, { target: [1, 2, 3], distance: 40000, yaw: 270, pitch: 89, fov: 110, mode: 'orbit' });
    const low = cam.normalizeCamera({ target: [0, 0, 0], distance: 0, yaw: 360, pitch: -120, fov: 1, mode: 'top' });
    assert.deepEqual(low, { target: [0, 0, 0], distance: 0.5, yaw: 0, pitch: -89, fov: 15, mode: 'top' });
    assert.equal(cam.makeCamera().mode, 'orbit');
    assert.deepEqual(cam.makeCamera({ yaw: 90 }).target, [0, 0, 0]);
    const source = { target: [1, 2, 3], distance: 10, yaw: 1, pitch: 1, fov: 55, mode: 'fly' };
    cam.normalizeCamera(source).target[0] = 99;
    assert.equal(source.target[0], 1, 'normalizeCamera must not alias the target array');
});

test('screen and NDC coordinates invert each other', () => {
    for (const [px, py] of [[0, 0], [1440, 900], [720, 450], [100.5, 333.25]]) {
        const ndc = cam.screenToNdc(px, py, 1440, 900);
        const back = cam.ndcToScreen(ndc.x, ndc.y, 1440, 900);
        assert.ok(Math.abs(back.x - px) < 1e-9 && Math.abs(back.y - py) < 1e-9);
    }
    assert.deepEqual(cam.screenToNdc(0, 0, 100, 50), { x: -1, y: 1 });
    assert.deepEqual(cam.screenToNdc(100, 50, 100, 50), { x: 1, y: -1 });
});

test('yardsPerPixel is the ground covered by one pixel', () => {
    assert.equal(cam.yardsPerPixel({ mode: 'top', distance: 450 }, 900, 0), 1);
    const ypp = cam.yardsPerPixel({ mode: 'orbit', fov: 90, distance: 100 }, 1000, 100);
    assert.ok(Math.abs(ypp - 0.2) < 1e-12, `${ypp}`);
});

test('intersectPlaneZ refuses parallel rays and planes behind the origin', () => {
    assert.equal(cam.intersectPlaneZ({ origin: [0, 0, 10], direction: [1, 0, 0] }, 0), null);
    assert.equal(cam.intersectPlaneZ({ origin: [0, 0, 10], direction: [0, 0, 1] }, 0), null);
    assert.deepEqual(cam.intersectPlaneZ({ origin: [1, 2, 10], direction: [0, 0, -1] }, 4), [1, 2, 4]);
});

test('orbit drag turns the heading and tilts, except that a top view only turns', () => {
    const c = orbit({ yaw: 10, pitch: 80 });
    const dragged = cam.orbitDrag(c, 40, 20);
    assert.equal(dragged.yaw, 0, 'dragging right lowers the yaw by 0.25 degrees a pixel');
    assert.equal(dragged.pitch, 85);
    assert.equal(cam.orbitDrag(c, 0, 1000).pitch, 89, 'the pitch stops at 89');
    assert.equal(cam.orbitDrag(c, -2000, 0).yaw, (10 + 500) % 360, 'the yaw wraps');
    assert.deepEqual(c.target, INN, 'the input camera is not changed');
    const top = cam.orbitDrag(orbit({ mode: 'top', pitch: 45 }), 8, 80);
    assert.equal(top.pitch, 45);
    assert.equal(top.yaw, 43);
});

test('a grab-pan keeps the grabbed ground point under the cursor in every mode', () => {
    const from = { x: 0.31, y: -0.22 };
    const to = { x: -0.18, y: 0.12 };
    for (const mode of ['orbit', 'top']) {
        const start = orbit({ mode, pitch: 55 });
        const planeZ = start.target[2];
        const grabbed = cam.intersectPlaneZ(cam.rayThrough(start, 0, from.x, from.y, ASPECT), planeZ);
        assert.ok(grabbed, `${mode}: the ray reaches the ground plane`);
        const moved = cam.panGrab(start, from, to, ASPECT);
        const after = cam.intersectPlaneZ(cam.rayThrough(moved, 0, to.x, to.y, ASPECT), planeZ);
        near(after, grabbed, 1e-6, `${mode}: the point under the cursor after the pan`);
        assert.notDeepEqual(moved.target, start.target, `${mode}: the camera moved`);
        assert.equal(moved.target[2], start.target[2], `${mode}: a grab pan stays on its plane`);
        assert.equal(moved.yaw, start.yaw);
        assert.equal(moved.distance, start.distance);
    }

    // A fly camera sits on its own target plane, so it grabs the plane ahead of it (the right-drag must not be a no-op).
    const fly = orbit({ mode: 'fly', pitch: 40, distance: 30 });
    const ahead = (c, ndc) => {
        const ray = cam.rayThrough(c, 0, ndc.x, ndc.y, ASPECT);
        return cam.vec.add(ray.origin, cam.vec.scale(ray.direction, 30 / cam.vec.dot(ray.direction, cam.direction(c))));
    };
    const movedFly = cam.panGrab(fly, from, to, ASPECT);
    assert.notDeepEqual(movedFly.target, fly.target, 'fly: the camera moved');
    near(ahead(movedFly, to), ahead(fly, from), 1e-6, 'fly: the point under the cursor after the pan');
    assert.equal(movedFly.yaw, fly.yaw);
    assert.equal(movedFly.pitch, fly.pitch);

    // Ten degrees down with the cursor high in the view: the ray runs above the horizon and never meets the ground plane.
    const horizon = orbit({ pitch: 10 });
    assert.equal(cam.panGrab(horizon, { x: 0, y: 0.9 }, { x: 0, y: 0.95 }, ASPECT), horizon, 'a ray that misses the plane leaves the camera alone');
});

test('vertical pan moves the target by the yards the dragged pixels cover', () => {
    const c = orbit({ distance: 200, fov: 90 });
    const moved = cam.panVertical(c, 50, 1000);
    assert.ok(Math.abs(moved.target[2] - (c.target[2] + 50 * 0.4)) < 1e-9);
    assert.deepEqual([moved.target[0], moved.target[1]], [c.target[0], c.target[1]]);
    const fly = cam.panVertical(orbit({ mode: 'fly', distance: 2, fov: 90 }), 10, 1000);
    assert.ok(Math.abs(fly.target[2] - (INN[2] + 10 * 0.04)) < 1e-9, 'a fly camera pans as if it were 20 yards from the scene');
});

test('zooming at a point keeps that point still on screen', () => {
    const hit = [INN[0] + 22, INN[1] - 31, INN[2] - 12];
    for (const mode of ['orbit', 'top', 'fly']) {
        const start = orbit({ mode, pitch: 50 });
        for (const factor of [0.8, 1.25]) {
            const zoomed = cam.zoomAt(start, factor, hit);
            if (mode === 'fly') {
                near(cam.dirTo(zoomed, hit), cam.dirTo(start, hit), 1e-9, 'fly: the eye slides along its line to the point');
                assert.ok(cam.vec.length(cam.vec.sub(hit, cam.eye(zoomed))) < cam.vec.length(cam.vec.sub(hit, cam.eye(start))) === (factor < 1), 'fly: zooming in gets nearer');
                continue;
            }
            assert.ok(Math.abs(zoomed.distance - start.distance * factor) < 1e-9, `${mode}: distance scales by the factor`);
            const before = cam.project(start, hit, ASPECT);
            const after = cam.project(zoomed, hit, ASPECT);
            assert.ok(Math.abs(before.x - after.x) < 1e-9 && Math.abs(before.y - after.y) < 1e-9, `${mode}: the point's pixel changed`);
        }
    }
    const tight = cam.zoomAt(orbit({ distance: 0.6 }), 0.1, hit);
    assert.equal(tight.distance, 0.5, 'zooming in stops at the minimum distance');
    assert.equal(cam.zoomAt(orbit({ distance: 39000 }), 10, hit).distance, 40000, 'zooming out stops at the maximum distance');
    const close = cam.zoomAt(orbit({ mode: 'fly', target: [0, 0, 0] }), 0.0001, [0, 0, 0.05]);
    assert.ok(cam.vec.length(cam.vec.sub(cam.eye(close), [0, 0, 0.05])) <= 0.05 + 1e-12, 'a fly camera does not pass through the point it zooms at');
});

test('key movement follows the heading, or the look direction when flying', () => {
    const c = orbit({ yaw: 0, pitch: 30, target: [0, 0, 0] });
    near(cam.moveCamera(c, 1, 0, 0, 10, 1).target, [10, 0, 0], 1e-9, 'forward over the ground (yaw 0 is +X)');
    near(cam.moveCamera(c, -1, 0, 0, 10, 0.5).target, [-5, 0, 0], 1e-9, 'backward for half a second');
    near(cam.moveCamera(c, 0, 1, 0, 10, 1).target, [0, -10, 0], 1e-9, 'right is -Y when facing +X');
    near(cam.moveCamera(c, 0, 0, 1, 10, 1).target, [0, 0, 10], 1e-9, 'up is world Z');
    const turned = orbit({ yaw: 90, pitch: 30, target: [0, 0, 0] });
    near(cam.moveCamera(turned, 1, 0, 0, 10, 1).target, [0, 10, 0], 1e-9, 'forward at yaw 90 is +Y');
    const fly = orbit({ mode: 'fly', yaw: 0, pitch: 30, target: [0, 0, 0] });
    const d = cam.direction(fly);
    near(cam.moveCamera(fly, 1, 0, 0, 10, 1).target, [d[0] * 10, d[1] * 10, d[2] * 10], 1e-9, 'a fly camera moves along its look direction, down when pitched down');
    assert.ok(cam.moveCamera(fly, 1, 0, 0, 10, 1).target[2] < 0);
});

test('switching modes does not move what the user sees', () => {
    const o = orbit({ yaw: 120, pitch: 35, distance: 90 });
    const fly = cam.switchMode(o, 'fly');
    assert.equal(fly.mode, 'fly');
    near(cam.eye(fly), cam.eye(o), 1e-9, 'orbit to fly keeps the eye');
    near(cam.direction(fly), cam.direction(o), 1e-12, 'and the look direction');
    const back = cam.switchMode(fly, 'orbit');
    near(back.target, o.target, 1e-9, 'fly to orbit returns to the original target');
    assert.equal(back.distance, o.distance);

    const top = cam.switchMode(o, 'top');
    assert.equal(top.mode, 'top');
    assert.deepEqual(top.target, o.target, 'top looks straight down at the orbit target');
    const restored = cam.switchMode(top, 'orbit');
    assert.deepEqual(restored, o, 'top to orbit restores the orbit camera, angles and all');
    const viaFly = cam.switchMode(top, 'fly');
    near(cam.eye(viaFly), cam.eye(o), 1e-9, 'top to fly starts from the orbit eye');

    assert.equal(cam.switchMode(o, 'orbit'), o, 'switching to the current mode returns the same camera');
    assert.equal(cam.switchMode(fly, 'fly'), fly);
});

test('framing a box fits all of it on screen', () => {
    const min = [-9500, -30, 20];
    const max = [-9400, 80, 90];
    const corners = [];
    for (const x of [min[0], max[0]]) {
        for (const y of [min[1], max[1]]) {
            for (const z of [min[2], max[2]]) {
                corners.push([x, y, z]);
            }
        }
    }
    const centre = [(min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2];
    for (const mode of ['orbit', 'top', 'fly']) {
        for (const aspect of [16 / 9, 1]) {
            const framed = cam.frameBounds(orbit({ mode, distance: 10, pitch: 40 }), min, max, aspect);
            assert.equal(framed.mode, mode, 'framing keeps the mode');
            const c = mode === 'fly' ? cam.switchMode(framed, 'orbit') : framed;
            near(c.target, centre, 1e-9, `${mode}: the box centre is the target`);
            for (const corner of corners) {
                const p = cam.project(framed, corner, aspect);
                assert.ok(p, `${mode}: ${corner} is in front of the camera`);
                assert.ok(Math.abs(p.x) <= 1 && Math.abs(p.y) <= 1, `${mode} aspect ${aspect}: corner ${corner} is off screen at ${p.x}, ${p.y}`);
            }
        }
    }
    const tiny = cam.frameBounds(orbit(), [0, 0, 0], [0, 0, 0]);
    assert.ok(tiny.distance > 5, 'a single point is framed as at least a five-yard radius');
});

test('looking at a point', () => {
    const o = cam.lookAtPoint(orbit(), [1, 2, 3], 30);
    assert.deepEqual(o.target, [1, 2, 3]);
    assert.equal(o.distance, 30);
    assert.equal(cam.lookAtPoint(orbit(), [1, 2, 3], 0.01).distance, 0.5);
    const fly = cam.lookAtPoint(orbit({ mode: 'fly', yaw: 30, pitch: 20 }), [1, 2, 3], 25);
    const toPoint = cam.dirTo(fly, [1, 2, 3]);
    near(toPoint, cam.direction(fly), 1e-9, 'the fly camera looks at the point');
    assert.ok(Math.abs(cam.vec.length(cam.vec.sub([1, 2, 3], cam.eye(fly))) - 25) < 1e-9, 'from the requested distance');
    assert.equal(cam.dirTo(orbit({ mode: 'fly' }), INN), null, 'no direction to the eye itself');
});

test('toFloat32 keeps a matrix usable as a uniform', () => {
    const f = cam.toFloat32(cam.projectionMatrix(orbit(), ASPECT));
    assert.ok(f instanceof Float32Array && f.length === 16);
    assert.throws(() => cam.projectionMatrix(orbit(), 0), RangeError);
    assert.throws(() => cam.projectionMatrix(orbit(), Number.NaN), RangeError);
});

test('a straight-down pitch falls back to the heading for the side vector', () => {
    const c = orbit({ pitch: 90, yaw: 30 });
    const v = cam.viewMatrix(c);
    const side = [v[0], v[4], v[8]];
    const length = Math.hypot(...side);
    assert.ok(Math.abs(length - 1) < 1e-9, 'the side vector is a unit vector, not zero');
    assert.ok(Number.isFinite(v[1]) && Number.isFinite(v[5]));
});
