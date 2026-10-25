// The help-line parser of the GM command palette: the grammar (`#n`, `$name`, `[opt]`, `a|b`, `key=default`, `--flag`, `<name>`), the line it
// builds, and its coverage of the committed command reference (docs/reference/gm-commands.md).

import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { buildLine, decodeHelp, leafHelpLines, parseSyntax } from '../../../tools/ArcaneCore.Atlas/Web/js/ui/devui-syntax.js';

const REFERENCE = fileURLToPath(new URL('../../../docs/reference/gm-commands.md', import.meta.url));

/** Share of the reference's leaf help lines the parser must read (clean or with a free tail). */
const REQUIRED_SHARE = 0.95;

const names = (parsed) => parsed.args.map((a) => `${a.kind}:${a.name}${a.optional ? '?' : ''}`);

test('numbers, text, optional arguments and choices', () => {
    const p = parseSyntax('Syntax: .cheat god on\\|off [$name] The player takes damage but cannot drop below 1 health.', 'cheat god');
    assert.equal(p.ok, true);
    assert.equal(p.partial, false);
    assert.deepEqual(names(p), ['choice:on|off', 'text:name?']);
    assert.deepEqual(p.args[0].options, ['on', 'off']);

    const q = parseSyntax('Syntax: .alter respawn #seconds Set the delay of the selected creature\'s next respawn (once).', 'alter respawn');
    assert.deepEqual(names(q), ['number:seconds']);

    const r = parseSyntax('Syntax: .go xyz #x #y [#z [#mapid]] — teleport to a position; without #z, to the ground there.', 'go xyz');
    assert.deepEqual(names(r), ['number:x', 'number:y', 'number:z?', 'number:mapid?']);
});

test('the command path is not an argument, and prose after the syntax is not either', () => {
    const p = parseSyntax('Syntax: .honor reset Forget all honor of the selected player (or yourself).', 'honor reset');
    assert.equal(p.ok, true);
    assert.deepEqual(p.args, []);
    const q = parseSyntax('Syntax: .instance stats — loaded instance maps and stored saves.', 'instance stats');
    assert.deepEqual(q.args, []);
    const s = parseSyntax('Syntax: .learn all_recipes $profession', 'learn all_recipes');
    assert.deepEqual(names(s), ['text:profession']);
});

test('keyed defaults, flags, angle brackets and the guid placeholder', () => {
    const a = parseSyntax('Syntax: .debug alloc start [seconds=30] Start sampling allocations.', 'debug alloc start');
    assert.deepEqual(a.args[0], { kind: 'number', name: 'seconds', optional: true, default: '30', keyed: true });
    const b = parseSyntax('Syntax: .botctl take <bots> [passive|defensive] [--start] [--force] Take the bots over.', 'botctl take');
    assert.deepEqual(names(b), ['text:bots', 'choice:passive|defensive?', 'flag:--start?', 'flag:--force?']);
    const c = parseSyntax('Syntax: .atlas tele 0xGUID #map $x $y $z Teleport the player to the point.', 'atlas tele');
    assert.deepEqual(names(c), ['text:guid', 'number:map', 'text:x', 'text:y', 'text:z']);
    const d = parseSyntax('Syntax: .botctl emote &lt;bots&gt; &lt;text emote id&gt; [unit] A text emote.', 'botctl emote');
    assert.deepEqual(names(d), ['text:bots', 'text:text emote id', 'literal:unit?']);
});

test('an alternative form is cut off and flagged, not misread', () => {
    const p = parseSyntax('Syntax: .botctl move <bots> <x> <y> <z> [tolerance] | <bots> <unit> [tolerance] Walk to a point.', 'botctl move');
    assert.equal(p.ok, true);
    assert.equal(p.partial, true);
    assert.deepEqual(names(p), ['text:bots', 'text:x', 'text:y', 'text:z', 'literal:tolerance?']);
});

test('text that is not a syntax line is refused so the palette offers a free-text field', () => {
    assert.equal(parseSyntax('Learn all eligible class spells of the selected player.', 'learn all').ok, false);
    assert.equal(parseSyntax('Syntax: .other thing #n', 'cheat god').ok, false);
    assert.equal(parseSyntax('Syntax: .cheat god [on', 'cheat god').ok, false);
    assert.equal(parseSyntax('Syntax: .cheat god on)', 'cheat god').ok, false);
});

test('help text is decoded: escaped pipes, HTML entities and runs of space', () => {
    assert.equal(decodeHelp('a\\|b &lt;x&gt;  &amp; &quot;y&quot;'), 'a|b <x> & "y"');
});

test('the line is built from the values and refuses what a command line cannot carry', () => {
    const p = parseSyntax('Syntax: .cheat god on\\|off [$name] The player.', 'cheat god');
    assert.equal(buildLine('cheat god', p.args, { 'on|off': 'on' }), 'cheat god on');
    assert.equal(buildLine('.cheat god', p.args, { 'on|off': 'off', name: 'Alpha' }), 'cheat god off Alpha');
    assert.equal(buildLine('cheat god', p.args, {}), null, 'a required argument is missing');
    assert.equal(buildLine('cheat god', p.args, { 'on|off': 'on', name: 'a\n.server shutdown' }), null, 'a line break would run a second command');
    assert.equal(buildLine('cheat god', p.args, { 'on|off': 'on' }, 'extra tail'), 'cheat god on extra tail');
    assert.equal(buildLine('cheat god', p.args, { 'on|off': 'on' }, 'x\ny'), null);
    const k = parseSyntax('Syntax: .debug alloc start [seconds=30] Start.', 'debug alloc start');
    assert.equal(buildLine('debug alloc start', k.args, {}), 'debug alloc start');
    assert.equal(buildLine('debug alloc start', k.args, { seconds: '60' }), 'debug alloc start seconds=60');
    const f = parseSyntax('Syntax: .botctl take <bots> [--start] Take.', 'botctl take');
    assert.equal(buildLine('botctl take', f.args, { bots: '0x1', '--start': true }), 'botctl take 0x1 --start');
});

test('the parser reads at least 95% of the leaf help lines of the committed command reference', (t) => {
    const rows = leafHelpLines(fs.readFileSync(REFERENCE, 'utf8'));
    assert.ok(rows.length >= 400, `only ${rows.length} leaf rows were found in the reference; the table reader stopped matching`);
    let clean = 0;
    let partial = 0;
    const free = [];
    for (const row of rows) {
        const parsed = parseSyntax(row.help, row.path);
        if (parsed.ok && !parsed.partial) {
            clean++;
        } else if (parsed.ok) {
            partial++;
        } else {
            free.push(row.path);
        }
    }
    const share = (clean + partial) / rows.length;
    const summary = `leaf help lines ${rows.length}: ${clean} read cleanly, ${partial} read with a free tail, ${free.length} free-text only (${(share * 100).toFixed(1)}% handled)`;
    t.diagnostic(summary);
    console.log(summary);
    assert.ok(share >= REQUIRED_SHARE, `${summary}; free-text only: ${free.join(', ')}`);
    // The rows with no Syntax: line at all are exactly the ones left to the free-text field.
    for (const path of free) {
        assert.ok(!/^Syntax:/.test(rows.find((r) => r.path === path).help), `${path} has a Syntax: line the parser should read`);
    }
});

test('the reference table reader skips group rows and keeps escaped pipes inside the help cell', () => {
    const md = [
        '| Command | Retail level | Minimum account | Level source | Help |',
        '|---|---|---|---|---|',
        '| `.account` ... | 6 | Administrator | stored level through the map | Account login security. |',
        '| `.cheat god` | 3 | GameMaster | declared retail level | Syntax: .cheat god on\\|off [$name] The player. |',
    ].join('\n');
    const rows = leafHelpLines(md);
    assert.equal(rows.length, 1);
    assert.equal(rows[0].path, '.cheat god');
    assert.equal(rows[0].level, 3);
    assert.equal(rows[0].help, 'Syntax: .cheat god on\\|off [$name] The player.');
});
