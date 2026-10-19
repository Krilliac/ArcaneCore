// Static check of the module graph: every relative import resolves to a file under Web/, and every named import is
// exported by the module it names. A typo in an import is a blank page in the browser, so this fails the build instead.

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const webRoot = fileURLToPath(new URL('../../../tools/ArcaneCore.Atlas/Web/', import.meta.url));

function walk(dir) {
    const out = [];
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            out.push(...walk(full));
        } else {
            out.push(full);
        }
    }
    return out;
}

const stripComments = (source) => source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

function exportsOf(source) {
    const names = new Set();
    for (const m of source.matchAll(/^export\s+(?:async\s+)?(?:const|let|var|function\*?|class)\s+([A-Za-z0-9_$]+)/gm)) {
        names.add(m[1]);
    }
    for (const m of source.matchAll(/^export\s*\{([^}]*)\}/gm)) {
        for (const part of m[1].split(',')) {
            const name = part.trim().split(/\s+as\s+/).pop();
            if (name) {
                names.add(name);
            }
        }
    }
    return names;
}

function importsOf(source) {
    const out = [];
    for (const m of source.matchAll(/^import\s+([\s\S]*?)\s+from\s+'([^']+)';/gm)) {
        const named = /\{([\s\S]*?)\}/.exec(m[1]);
        const names = named
            ? named[1].split(',').map((p) => p.trim().split(/\s+as\s+/)[0]).filter(Boolean)
            : [];
        out.push({ spec: m[2], names, namespace: /\*\s+as\s/.test(m[1]) });
    }
    for (const m of source.matchAll(/import\(\s*'([^']+)'\s*\)/g)) {
        out.push({ spec: m[1], names: [], namespace: true });
    }
    for (const m of source.matchAll(/^import\s+'([^']+)';/gm)) {
        out.push({ spec: m[1], names: [], namespace: true });
    }
    return out;
}

const files = walk(webRoot).filter((f) => f.endsWith('.js'));

test('the Web folder has the expected entry files', () => {
    assert.ok(fs.existsSync(path.join(webRoot, 'index.html')), 'index.html');
    assert.ok(fs.existsSync(path.join(webRoot, 'app.css')), 'app.css');
    assert.ok(files.length >= 30, `found only ${files.length} modules`);
});

for (const file of files) {
    const rel = path.relative(webRoot, file).replaceAll('\\', '/');
    test(`imports of ${rel} resolve and are exported`, () => {
        const source = stripComments(fs.readFileSync(file, 'utf8'));
        for (const imp of importsOf(source)) {
            assert.ok(imp.spec.startsWith('./') || imp.spec.startsWith('../'), `${rel}: non-relative import '${imp.spec}'`);
            const target = path.resolve(path.dirname(file), imp.spec);
            assert.ok(fs.existsSync(target), `${rel}: '${imp.spec}' does not exist`);
            if (imp.names.length > 0) {
                const available = exportsOf(stripComments(fs.readFileSync(target, 'utf8')));
                for (const name of imp.names) {
                    assert.ok(available.has(name), `${rel}: '${name}' is not exported by ${imp.spec}`);
                }
            }
        }
    });
}

test('index.html loads only the app entry module and stylesheet', () => {
    const html = fs.readFileSync(path.join(webRoot, 'index.html'), 'utf8');
    const refs = [...html.matchAll(/(?:src|href)="([^"]+)"/g)].map((m) => m[1]).filter((r) => r !== 'data:,');
    assert.deepEqual(refs.sort(), ['/app/app.css', '/app/js/main.js']);
    assert.ok(!/<script(?![^>]*\bsrc=)[^>]*>/i.test(html), 'inline script');
    assert.ok(!/\sstyle="/i.test(html), 'inline style attribute');
    assert.ok(!/<style[\s>]/i.test(html), 'style element');
});
