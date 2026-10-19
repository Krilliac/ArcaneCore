// Pure helper modules: util, forms, goto, colors and results. No DOM, no network, no clock.

import assert from 'node:assert/strict';
import test from 'node:test';
import { buildPalette, creatureMotionClass, divergingRamp, LIVE_COLORS, packColor, resolveColorKey, styleRgb, styleRgbF } from '../../../tools/ArcaneCore.Atlas/Web/js/colors.js';
import {
    acceptsArg, autoFill, buildArgs, defaultText, fieldsFromSchema, filterArgs, formatPointText, parseField, parseNumber, parsePoint,
} from '../../../tools/ArcaneCore.Atlas/Web/js/forms.js';
import { parseGoto } from '../../../tools/ArcaneCore.Atlas/Web/js/goto.js';
import { differences, flatten, inlineArray, overlaySummary, probeTitle, scalarText } from '../../../tools/ArcaneCore.Atlas/Web/js/results.js';
import * as u from '../../../tools/ArcaneCore.Atlas/Web/js/util.js';

test('util: numbers, angles and colours', () => {
    assert.equal(u.clamp(5, 0, 3), 3);
    assert.equal(u.clamp(-5, 0, 3), 0);
    assert.equal(u.clamp(2, 0, 3), 2);
    assert.equal(u.lerp(10, 20, 0.25), 12.5);
    assert.equal(u.wrapDegrees(-90), 270);
    assert.equal(u.wrapDegrees(360), 0);
    assert.equal(u.wrapDegrees(725), 5);
    assert.equal(u.wrapDegrees(359.5), 359.5);
    assert.equal(u.isFiniteNumber(1), true);
    assert.equal(u.isFiniteNumber('1'), false);
    assert.equal(u.isFiniteNumber(Number.NaN), false);
    assert.equal(u.isFiniteNumber(Infinity), false);

    assert.deepEqual(u.parseHexColor('#ff00AA'), [255, 0, 170]);
    assert.equal(u.parseHexColor('#fff'), null);
    assert.equal(u.parseHexColor('ff00aa'), null);
    assert.equal(u.parseHexColor(5), null);
    assert.equal(u.toHexColor(255, 0.4, 300), '#FF00FF');
    assert.equal(u.toHexColor(-4, 127.5, 16), '#008010');
    assert.equal(u.packRgba(1, 2, 3, 1), 0xFF030201);
    assert.equal(u.packRgba(1, 2, 3, 0), 0x00030201);
    assert.equal(u.packRgba(255, 255, 255, 0.5), 0x80FFFFFF);
    assert.equal(u.packRgba(0, 0, 0, 7), 0xFF000000, 'alpha is clamped');

    assert.equal(u.niceNumber(130), 200);
    assert.equal(u.niceNumber(100), 100);
    assert.equal(u.niceNumber(0.03), 0.05);
    assert.equal(u.niceNumber(5.1), 10);
    assert.equal(u.niceNumber(-1), 1);
    assert.equal(u.niceNumber(Number.NaN), 1);
    assert.equal(u.niceFloor(130), 100);
    assert.equal(u.niceFloor(250), 200);
    assert.equal(u.niceFloor(999), 500);
    assert.equal(u.niceFloor(0), 1);
});

test('util: number formatting', () => {
    assert.equal(u.formatNumber(3.14159, 2), '3.14');
    assert.equal(u.formatNumber(Infinity), '-');
    assert.equal(u.formatNumber('7'), '-');
    assert.equal(u.formatPoint([1, 2, 3.456]), '1.00, 2.00, 3.46');
    assert.equal(u.formatPoint([1, 2]), '-');
    assert.equal(u.formatPoint(null), '-');
    assert.equal(u.formatCount(1234), '1,234');
    assert.equal(u.formatCount(999), '999');
    assert.equal(u.formatCount(1_250_000), '1.25M');
    assert.equal(u.formatCount(2.5e9), '2.50B');
    assert.equal(u.formatCount(Number.NaN), '-');
    assert.equal(u.formatBytes(12), '12 B');
    assert.equal(u.formatBytes(1536), '1.5 KiB');
    assert.equal(u.formatBytes(5 * 1024 ** 3), '5.0 GiB');
    assert.equal(u.formatBytes(2048 * 1024 ** 3), '2048.0 GiB');
    assert.equal(u.formatBytes(null), '-');
    assert.equal(u.formatDuration(0.5), '0.50 ms');
    assert.equal(u.formatDuration(12.34), '12.3 ms');
    assert.equal(u.formatDuration(250.4), '250 ms');
    assert.equal(u.formatDuration(12345), '12.3 s');
    assert.equal(u.formatDuration(undefined), '-');
});

test('util: deep clone and equality', () => {
    const original = { a: [1, { b: 2 }], c: null };
    const copy = u.deepClone(original);
    copy.a[1].b = 99;
    assert.equal(original.a[1].b, 2);
    assert.equal(u.deepClone(undefined), undefined);
    assert.equal(u.deepEqual({ a: [1, 2], b: { c: 3 } }, { b: { c: 3 }, a: [1, 2] }), true);
    assert.equal(u.deepEqual({ a: 1 }, { a: 1, b: undefined }), false, 'a different key count differs');
    assert.equal(u.deepEqual([1, 2], { 0: 1, 1: 2 }), false, 'an array is not an object');
    assert.equal(u.deepEqual({ a: null }, { a: {} }), false);
    assert.equal(u.deepEqual(null, null), true);
    assert.equal(u.deepEqual({ a: 1 }, { a: 2 }), false);
});

test('util: merge patches (RFC 7396)', () => {
    const target = { a: { b: 1, c: [1, 2] }, d: 'x' };
    const frozen = JSON.stringify(target);
    const merged = u.mergePatch(target, { a: { b: null, c: [3] }, e: { f: 1 } });
    assert.deepEqual(merged, { a: { c: [3] }, d: 'x', e: { f: 1 } });
    assert.equal(JSON.stringify(target), frozen, 'mergePatch must not change its input');
    assert.deepEqual(u.mergePatch({ a: 1 }, { b: null }), { a: 1 }, 'deleting a member that is not there changes nothing');
    assert.deepEqual(u.mergePatch(5, { a: 1 }), { a: 1 }, 'a non-object target is replaced by the patch object');
    assert.deepEqual(u.mergePatch({ a: 1 }, [1]), [1]);
    assert.equal(u.mergePatch({ a: 1 }, null), null);

    // Composition: applying the composed patch once is applying both in turn.
    const cases = [
        [{ a: { b: 1 } }, { a: { c: 2 } }],
        [{ a: { b: 1 } }, { a: null }],
        [{ camera: { target: [1, 2, 3] } }, { camera: { yaw: 10 }, layers: { x: { visible: true } } }],
        [{ x: null }, { x: null }],
    ];
    const base = { a: { z: 9 }, camera: { target: [0, 0, 0], yaw: 0 }, layers: { x: { visible: false } }, x: 4 };
    for (const [first, second] of cases) {
        const both = u.mergePatch(u.mergePatch(base, first), second);
        assert.deepEqual(u.mergePatch(base, u.composePatch(first, second)), both, `${JSON.stringify(first)} then ${JSON.stringify(second)}`);
    }
    assert.deepEqual(u.composePatch({ a: null }, { a: 1 }), { a: 1 });
    assert.deepEqual(u.composePatch({ a: { b: 1 } }, { a: null }), { a: null }, 'a null stays in the composition so it still deletes');
    // Merge patches cannot say "replace this member": a member deleted or set to a scalar and then given an object composes to
    // just the object, which merges into whatever the member held. The UI never sends such a pair (its patches only nest
    // objects or replace arrays and scalars), and the composition says so rather than guessing.
    assert.deepEqual(u.composePatch({ a: null }, { a: { b: 2 } }), { a: { b: 2 } });
    assert.deepEqual(u.composePatch({ a: 1 }, { a: { b: 2 } }), { a: { b: 2 } });
    assert.deepEqual(u.mergePatch({ a: { z: 9 } }, u.composePatch({ a: 1 }, { a: { b: 2 } })), { a: { z: 9, b: 2 } });
});

test('util: the smallest patch between two values', () => {
    const from = { a: 1, b: { c: 2, d: 3 }, e: [1, 2], f: 'gone' };
    const to = { a: 1, b: { c: 2, d: 4 }, e: [1, 2, 3], g: true };
    const patch = u.diffPatch(from, to);
    assert.deepEqual(patch, { b: { d: 4 }, e: [1, 2, 3], f: null, g: true });
    assert.deepEqual(u.mergePatch(from, patch), to, 'applying the diff gives the target');
    assert.equal(u.diffPatch(from, structuredClone(from)), undefined, 'equal values have no patch');
    assert.equal(u.diffPatch(1, 2), 2);
    assert.deepEqual(u.diffPatch({ a: 1 }, { a: null }), { a: null }, 'a null that is a value, not a deletion, is indistinguishable in a merge patch');
});

test('util: throttle fires at most once per interval and always delivers the last value', () => {
    let now = 0;
    const timers = [];
    const calls = [];
    const schedule = (fn, ms) => {
        timers.push({ fn, at: now + ms, live: true });
        return timers.length - 1;
    };
    const cancel = (id) => {
        timers[id].live = false;
    };
    const advance = (to) => {
        now = to;
        for (const t of timers) {
            if (t.live && t.at <= now) {
                t.live = false;
                t.fn();
            }
        }
    };
    const f = u.throttle((x) => calls.push(x), 100, () => now, schedule, cancel);
    f(1);
    assert.deepEqual(calls, [1], 'the first call goes straight through');
    now = 10;
    f(2);
    f(3);
    assert.deepEqual(calls, [1], 'calls inside the interval wait');
    advance(99);
    assert.deepEqual(calls, [1]);
    advance(100);
    assert.deepEqual(calls, [1, 3], 'the trailing call carries the latest value only');
    now = 250;
    f(4);
    assert.deepEqual(calls, [1, 3, 4]);
    f(5);
    f.cancel();
    advance(400);
    assert.deepEqual(calls, [1, 3, 4], 'a cancelled call never fires');
    f(6);
    assert.deepEqual(calls, [1, 3, 4, 6], 'after the interval has passed a call goes straight through again');
    f(7);
    f(8);
    assert.deepEqual(calls, [1, 3, 4, 6], 'calls inside the new interval wait');
    f.flush();
    assert.deepEqual(calls, [1, 3, 4, 6, 8], 'flush delivers the pending value now');
    f.flush();
    assert.deepEqual(calls, [1, 3, 4, 6, 8], 'flush with nothing pending does nothing');
});

test('util: an event emitter isolates its handlers', () => {
    const e = new u.Emitter();
    const seen = [];
    const off = e.on('x', (a, b) => seen.push(['one', a, b]));
    e.on('x', () => {
        throw new Error('handler failure');
    });
    e.on('x', (a) => seen.push(['three', a]));
    const original = console.error;
    const logged = [];
    console.error = (...args) => logged.push(args.join(' '));
    try {
        e.emit('x', 1, 2);
    } finally {
        console.error = original;
    }
    assert.deepEqual(seen, [['one', 1, 2], ['three', 1]], 'a throwing handler does not stop the others');
    assert.equal(logged.length, 1);
    assert.match(logged[0], /handler for 'x' failed/);
    off();
    e.emit('x', 5, 6);
    assert.equal(seen.length, 3 - 1 + 1, 'the unsubscribed handler no longer runs, the third still does');
    e.emit('nobody listens');
});

const SCHEMA = {
    type: 'object',
    required: ['from', 'map'],
    properties: {
        map: { type: 'integer', description: 'Map id.' },
        from: { type: 'array', items: { type: 'number' }, minItems: 3, maxItems: 3, description: 'Start.' },
        to: { type: 'array', items: { type: 'number' }, minItems: 3, maxItems: 3, default: [1, 2, 3] },
        x: { type: 'number' },
        y: { type: 'number' },
        mode: { type: 'string', enum: ['fast', 'exact'], default: 'fast' },
        name: { type: 'string' },
        both: { type: 'boolean' },
        layers: { type: 'array', items: { type: 'string' } },
        extra: { type: 'object' },
        nested: { type: 'array', items: { type: 'object' } },
    },
};

test('forms: fields come from the schema in order, with their kinds', () => {
    const fields = fieldsFromSchema(SCHEMA);
    assert.deepEqual(fields.map((f) => [f.name, f.kind, f.required]), [
        ['map', 'integer', true], ['from', 'point3', true], ['to', 'point3', false], ['x', 'number', false], ['y', 'number', false],
        ['mode', 'choice', false], ['name', 'string', false], ['both', 'boolean', false], ['layers', 'stringList', false],
        ['extra', 'json', false], ['nested', 'json', false],
    ]);
    assert.equal(fields[0].description, 'Map id.');
    assert.deepEqual(fields[5].choices, ['fast', 'exact']);
    assert.deepEqual(fieldsFromSchema(undefined), []);
    assert.deepEqual(fieldsFromSchema({ properties: { z: {} } }).map((f) => f.kind), ['json']);
});

test('forms: default texts', () => {
    const by = Object.fromEntries(fieldsFromSchema(SCHEMA).map((f) => [f.name, f]));
    assert.equal(defaultText(by.to), '1, 2, 3');
    assert.equal(defaultText(by.mode), 'fast');
    assert.equal(defaultText(by.map), '');
    assert.equal(defaultText({ kind: 'json', default: { a: 1 } }), '{"a":1}');
    assert.equal(defaultText({ kind: 'stringList', default: ['a', 'b'] }), 'a, b');
    assert.equal(defaultText({ kind: 'integer', default: 0 }), '0');
});

test('forms: numbers and points are parsed strictly', () => {
    for (const [text, value] of [['12', 12], [' -3.5 ', -3.5], ['+.5', 0.5], ['1e3', 1000], ['2.', 2], ['-1E-2', -0.01]]) {
        assert.equal(parseNumber(text), value, text);
    }
    for (const text of ['', 'abc', '0x10', 'Infinity', '1,5', '1 2', '--1', '1e', 'NaN', '1e999']) {
        assert.equal(parseNumber(text), null, `"${text}" must be refused`);
    }
    assert.deepEqual(parsePoint('1, 2, 3'), [1, 2, 3]);
    assert.deepEqual(parsePoint('[1 2 3]'), [1, 2, 3]);
    assert.deepEqual(parsePoint('(-9462.66; 16.19; 57.05)'), [-9462.66, 16.19, 57.05]);
    assert.equal(parsePoint('1, 2'), null);
    assert.equal(parsePoint('1, 2, 3, 4'), null);
    assert.equal(parsePoint('1, two, 3'), null);
    assert.equal(formatPointText([1, 2.3456, -3], 3), '1.000, 2.346, -3.000');
    assert.equal(formatPointText(null), '');
});

test('forms: every field kind parses, or says what to type', () => {
    const f = (kind, extra = {}) => ({ name: 'v', kind, required: false, choices: null, ...extra });
    assert.deepEqual(parseField(f('string'), ' hi '), { value: ' hi ' }, 'strings keep their spaces');
    assert.deepEqual(parseField(f('integer'), '7'), { value: 7 });
    assert.match(parseField(f('integer'), '7.5').error, /whole number/);
    assert.deepEqual(parseField(f('number'), '7.5'), { value: 7.5 });
    assert.match(parseField(f('number'), 'x').error, /must be a number/);
    assert.deepEqual(parseField(f('boolean'), 'true'), { value: true });
    assert.deepEqual(parseField(f('boolean'), 'false'), { value: false });
    assert.match(parseField(f('boolean'), 'yes').error, /true or false/);
    assert.deepEqual(parseField(f('choice', { choices: ['a', 'b'] }), 'b'), { value: 'b' });
    assert.match(parseField(f('choice', { choices: ['a', 'b'] }), 'c').error, /one of a, b/);
    assert.deepEqual(parseField(f('point3'), '1 2 3'), { value: [1, 2, 3] });
    assert.match(parseField(f('point3'), '1 2').error, /x, y, z/);
    assert.deepEqual(parseField(f('stringList'), 'a, b ,, c'), { value: ['a', 'b', 'c'] });
    assert.deepEqual(parseField(f('json'), '{"k": [1]}'), { value: { k: [1] } });
    assert.match(parseField(f('json'), '[1]').error, /JSON object/);
    assert.match(parseField(f('json'), '{').error, /valid JSON/);
    assert.deepEqual(parseField(f('integer'), ''), { skip: true }, 'an empty optional field is left out');
    assert.deepEqual(parseField(f('integer'), '   '), { skip: true });
    assert.match(parseField(f('integer', { required: true }), '').error, /v is required/);
    assert.deepEqual(parseField(f('boolean'), true), { value: true }, 'a checkbox hands over a real boolean');
});

test('forms: arguments are built from field texts, with a message per bad field', () => {
    const fields = fieldsFromSchema(SCHEMA);
    const good = buildArgs(fields, { map: '0', from: '1,2,3', mode: 'exact', both: 'true', layers: 'a,b' });
    assert.deepEqual(good.errors, {});
    assert.deepEqual(good.args, { map: 0, from: [1, 2, 3], mode: 'exact', both: true, layers: ['a', 'b'] });
    const bad = buildArgs(fields, { map: 'x', from: '', x: '1..2' });
    assert.deepEqual(Object.keys(bad.errors).sort(), ['from', 'map', 'x']);
    assert.match(bad.errors.from, /required/);
});

test('forms: a probe passes only the arguments its command declares', () => {
    assert.equal(acceptsArg(SCHEMA, 'map'), true);
    assert.equal(acceptsArg(SCHEMA, 'toString'), false, 'inherited names are not arguments');
    assert.equal(acceptsArg(undefined, 'map'), false);
    assert.deepEqual(filterArgs(SCHEMA, { map: 1, bogus: 2, x: 3 }), { map: 1, x: 3 });
});

test('forms: clicked points fill the point fields, latest last', () => {
    const fields = fieldsFromSchema(SCHEMA);
    const two = autoFill(fields, { clicks: [[3, 4, 5], [1, 2, 3]], map: 1 });
    assert.equal(two.from, '1.000, 2.000, 3.000', 'the earlier click fills the first point');
    assert.equal(two.to, '3.000, 4.000, 5.000', 'the latest click fills the second');
    assert.equal(two.x, '3');
    assert.equal(two.y, '4');
    assert.equal(two.map, '1');
    const one = autoFill(fields, { clicks: [[3, 4, 5]], map: 0 });
    assert.equal(one.from, '3.000, 4.000, 5.000');
    assert.equal(one.to, undefined);
    const single = autoFill(fieldsFromSchema({ properties: { at: { type: 'array', items: { type: 'number' }, minItems: 3, maxItems: 3 } } }), { clicks: [[9, 8, 7], [1, 1, 1]], map: 0 });
    assert.deepEqual(single, { at: '9.000, 8.000, 7.000' }, 'a single point field takes the latest click');
    assert.deepEqual(autoFill(fields, { clicks: [], map: 5 }), { map: '5' }, 'without a click only the map is filled');
    assert.equal(autoFill(fields, { clicks: [[1.23456, 9, 0]], map: 0 }).x, '1.235');
});

test('goto: the coordinate box accepts what it documents', () => {
    assert.deepEqual(parseGoto('-9462.66, 16.19, 57.05', 0), { map: 0, x: -9462.66, y: 16.19, z: 57.05 });
    assert.deepEqual(parseGoto('100 200', 1), { map: 1, x: 100, y: 200, z: null });
    assert.deepEqual(parseGoto('1 -9462.66 16.19 57.05', 0), { map: 1, x: -9462.66, y: 16.19, z: 57.05 });
    assert.deepEqual(parseGoto('[ 1; 2; 3 ]', 7), { map: 7, x: 1, y: 2, z: 3 });
    assert.deepEqual(parseGoto('(1,2,3)', 7), { map: 7, x: 1, y: 2, z: 3 });
    assert.match(parseGoto('5', 0).error, /x, y, z or map x y z/);
    assert.match(parseGoto('1 2 3 4 5', 0).error, /x, y, z or map x y z/);
    assert.match(parseGoto('a, b', 0).error, /plain numbers/);
    assert.match(parseGoto('2000000, 0', 0).error, /out of range/);
    assert.match(parseGoto('1.5 1 2 3', 0).error, /map id/);
    assert.match(parseGoto('-1 1 2 3', 0).error, /map id/);
});

test('colors: attribute colour keys resolve through the layer table or as literal colours', () => {
    const table = { water: '#4FA3E0', bad: 'nope' };
    assert.deepEqual(resolveColorKey({ colorKey: 'water' }, table), [0x4F, 0xA3, 0xE0]);
    assert.deepEqual(resolveColorKey({ colorKey: '#102030' }, table), [16, 32, 48], 'a literal colour key is used as is');
    assert.equal(resolveColorKey({ colorKey: 'unknown' }, table), null);
    assert.equal(resolveColorKey({ colorKey: 'bad' }, table), null, 'a mapped value that is not a colour is refused');
    assert.equal(resolveColorKey({ colorKey: '' }, table), null);
    assert.equal(resolveColorKey({}, table), null);
    assert.equal(resolveColorKey(undefined, table), null);
    assert.deepEqual(resolveColorKey({ colorKey: '#ABCDEF' }, undefined), [0xAB, 0xCD, 0xEF]);
    assert.deepEqual(styleRgb('#FF8000'), [255, 128, 0]);
    assert.deepEqual(styleRgb('orange'), [147, 161, 170], 'not a colour: the neutral grey');
    assert.deepEqual(styleRgbF('#FF0000'), [1, 0, 0]);
    assert.equal(packColor('#102030', 0.5), u.packRgba(16, 32, 48, 0.5));
});

test('colors: the palette a shader reads', () => {
    const attributes = new Map([
        [0, { colorKey: 'ground' }],
        [2, { colorKey: 'water' }],
        [3, { note: 'no colour' }],
    ]);
    const colors = { ground: '#3CCB9A', water: '#49B6FF' };
    const direct = buildPalette([Uint32Array.of(0, 2, 3, 2)], attributes, colors);
    assert.equal(direct.remapped, false);
    assert.equal(direct.size, 4);
    assert.equal(direct.paletteOf(3), 3);
    assert.equal(direct.attributeOf(2), 2);
    assert.equal(direct.rows, 1);
    assert.equal(direct.rgba.length, 256 * 4);
    assert.deepEqual([...direct.rgba.subarray(0, 4)], [0x3C, 0xCB, 0x9A, 255]);
    assert.deepEqual([...direct.rgba.subarray(4, 8)], [0, 0, 0, 0], 'an attribute that was never described has no colour of its own');
    assert.deepEqual([...direct.rgba.subarray(8, 12)], [0x49, 0xB6, 0xFF, 255]);
    assert.deepEqual([...direct.rgba.subarray(12, 16)], [0, 0, 0, 0], 'a described attribute without a colour key too');

    const sparse = buildPalette([Uint32Array.of(5, 9_000_000, 5, 7_000_000)], new Map([[9_000_000, { colorKey: 'water' }]]), colors, 4096);
    assert.equal(sparse.remapped, true);
    assert.equal(sparse.size, 3, 'distinct large ids are numbered in order of first appearance');
    assert.equal(sparse.paletteOf(5), 0);
    assert.equal(sparse.paletteOf(9_000_000), 1);
    assert.equal(sparse.paletteOf(7_000_000), 2);
    assert.equal(sparse.attributeOf(1), 9_000_000);
    assert.equal(sparse.paletteOf(123), 3, 'an unknown id gets the empty entry past the table');
    assert.deepEqual([...sparse.rgba.subarray(4, 8)], [0x49, 0xB6, 0xFF, 255]);

    const empty = buildPalette([new Uint32Array(0)], new Map(), {});
    assert.equal(empty.size, 1);
    assert.equal(empty.rows, 1);
    const wide = buildPalette([Uint32Array.of(300)], new Map(), {});
    assert.equal(wide.rows, 2, '301 entries need two 256-wide rows');
    assert.equal(wide.rgba.length, 2 * 256 * 4);
});

test('colors: ramps and motion classes', () => {
    assert.deepEqual(divergingRamp(-1), [58, 134, 255]);
    assert.deepEqual(divergingRamp(0), [235, 240, 243]);
    assert.deepEqual(divergingRamp(1), [229, 72, 77]);
    assert.deepEqual(divergingRamp(-5), divergingRamp(-1), 'values past the ends are clamped');
    assert.deepEqual(divergingRamp(0.5), [232, 156, 160]);
    for (const [motion, klass] of [['Idle', 'idle'], ['', 'idle'], [undefined, 'idle'], ['Distract', 'idle'], ['Chase', 'chase'], ['ChaseMovementGenerator', 'chase'],
        ['Waypoint', 'moving'], ['Random', 'moving']]) {
        assert.equal(creatureMotionClass(motion), klass, String(motion));
    }
    assert.equal(LIVE_COLORS.chase, '#FF5A5A');
});

test('results: scalar and array text', () => {
    assert.equal(scalarText(null), 'none');
    assert.equal(scalarText(undefined), 'none');
    assert.equal(scalarText(3), '3');
    assert.equal(scalarText(3.14159265), '3.142');
    assert.equal(scalarText(2.5), '2.5');
    assert.equal(scalarText(true), 'true');
    assert.equal(scalarText('ok'), 'ok');
    assert.equal(scalarText(Infinity), 'Infinity');
    assert.equal(inlineArray([1, 2.5, 'x']), '1, 2.5, x');
    assert.equal(inlineArray(Array.from({ length: 15 }, (_, i) => i)), '0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, and 3 more');
});

test('results: a command result flattens to rows a panel can show', () => {
    const rows = flatten({
        height: 57.0459,
        found: true,
        point: [1, 2, 3],
        none: [],
        hit: { layer: 'terrain', tile: [0, 49, 31], deep: { a: { b: { c: { d: 1 } } } } },
        list: [{ id: 1 }, { id: 2 }],
        overlay: { points: [] },
    });
    const text = Object.fromEntries(rows.map((r) => [r.path, r.text]));
    assert.equal(text.height, '57.046');
    assert.equal(text.found, 'true');
    assert.equal(text.point, '1, 2, 3');
    assert.equal(text.none, 'empty');
    assert.equal(text.hit, null, 'an object is a heading row');
    assert.equal(text['hit.layer'], 'terrain');
    assert.equal(text['hit.tile'], '0, 49, 31');
    assert.equal(text['hit.deep.a.b.c'], '{"d":1}', 'past the depth limit an object is shown as JSON');
    assert.equal(text['list[1].id'], '2');
    assert.equal(rows.some((r) => r.path.startsWith('overlay')), false, 'the overlay is drawn on the map, not listed');
    assert.equal(rows.find((r) => r.path === 'hit.layer').depth, 1);
    assert.equal(rows.find((r) => r.path === 'hit.layer').key, 'layer');
    assert.equal(flatten({ overlay: 1 }, { skip: [] }).length, 1);
    assert.deepEqual(flatten(7), [{ path: '(result)', depth: 0, key: '(result)', text: '7' }]);
    assert.deepEqual(flatten(undefined), []);
    const long = flatten({ items: Array.from({ length: 30 }, (_, i) => ({ i })) });
    assert.equal(long.at(-1).text, '6 more', 'a long list is cut with a count');
    assert.equal(long.filter((r) => r.key === 'i').length, 24);
});

test('results: server and reference answers differ only where they really disagree', () => {
    const server = flatten({ z: 57.0459, ok: true, n: 3, only: 1, name: 'a' });
    const reference = flatten({ z: 57.0501, ok: false, n: 3.004, name: 'a' });
    assert.deepEqual([...differences(server, reference)].sort(), ['ok'], 'rounding-sized differences and fields only one side has are not disagreements');
    assert.deepEqual([...differences(server, reference, 0.001)].sort(), ['n', 'ok', 'z']);
    const headings = flatten({ a: { b: 1 } });
    assert.equal(differences(headings, flatten({ a: { b: 2 } })).has('a.b'), true);
    assert.equal(differences(headings, headings).size, 0);
});

test('results: summaries and titles', () => {
    assert.equal(overlaySummary({ points: 3, segments: 0, polylines: 1, boxes: 2, dropped: 0 }), '3 points, 1 polyline, 2 boxes');
    assert.equal(overlaySummary({ points: 1, segments: 1, polylines: 0, boxes: 0, dropped: 2 }), '1 point, 1 segment (2 malformed dropped)');
    assert.equal(overlaySummary({ points: 0, segments: 0, polylines: 0, boxes: 0, dropped: 0 }), 'nothing to draw');
    assert.equal(probeTitle('los'), 'Line of sight');
    assert.equal(probeTitle('height'), 'Height');
    assert.equal(probeTitle('floorstack'), 'Floor stack');
    assert.equal(probeTitle('weird'), 'Weird');
});
