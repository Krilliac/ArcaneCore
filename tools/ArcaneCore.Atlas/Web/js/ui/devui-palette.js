// The GM command palette (Ctrl+Shift+P): the dev UI's catalog actions and, when the daemon publishes its command catalog, every command it
// will run for this actor, with autocomplete on the path words, a security and a write badge, and an argument form read from the command's
// `Syntax:` help. What is typed and matches nothing can still be sent as a raw command line. The target and point fill in from the
// selection and the last click. This does not replace the Ctrl+K palette of Atlas commands (ui/palette.js); it sits beside it.

import { fill, formatPoint, h } from '../util.js';
import { buildLine, parseSyntax } from './devui-syntax.js';
import { openDialog } from './popover.js';
import { button } from './widgets.js';

const MAX_SHOWN = 60;

/** The palette's entries from an `action.catalog` result: catalog actions, then the daemon's commands. */
export function paletteEntries(catalog) {
    const entries = [];
    for (const item of Array.isArray(catalog?.items) ? catalog.items : []) {
        entries.push({
            kind: 'action',
            key: `action:${item.id}`,
            title: item.label.replace(/\.\.\.$/, ''),
            path: item.path ?? `${item.id}`,
            group: item.group,
            write: item.write === true,
            security: '',
            item,
        });
    }
    for (const command of Array.isArray(catalog?.commands) ? catalog.commands : []) {
        if (!command || typeof command.path !== 'string') {
            continue;
        }
        const path = command.path.replace(/^\./, '');
        const help = command.syntax ?? command.help ?? '';
        entries.push({
            kind: 'command',
            key: `cmd:${path}`,
            title: `.${path}`,
            path,
            group: path.split(' ')[0],
            write: command.write === true,
            security: command.security ?? '',
            help,
            parsed: parseSyntax(help, path),
        });
    }
    return entries;
}

/** Score of `entry` for `query` (higher is better), or 0 when a query word is nowhere in it. Pure. */
export function scoreEntry(entry, query) {
    const words = query.toLowerCase().replace(/^\./, '').split(/\s+/).filter((w) => w !== '');
    if (words.length === 0) {
        return 1;
    }
    const path = entry.path.toLowerCase();
    const haystack = `${path} ${entry.title.toLowerCase()} ${entry.group ?? ''}`;
    let score = 0;
    for (const word of words) {
        if (!haystack.includes(word)) {
            return 0;
        }
        score += path.split(' ').some((p) => p.startsWith(word)) ? 4 : 1;
    }
    if (path.startsWith(words.join(' '))) {
        score += 10;
    }
    return score + 1 / (1 + path.length);
}

/** The entries matching `query`, best first, with a raw-command entry last when something was typed. */
export function filterEntries(entries, query, limit = MAX_SHOWN) {
    const ranked = entries
        .map((entry) => ({ entry, score: scoreEntry(entry, query) }))
        .filter((r) => r.score > 0)
        .sort((a, b) => b.score - a.score || a.entry.path.localeCompare(b.entry.path))
        .slice(0, limit)
        .map((r) => r.entry);
    const typed = query.trim().replace(/^[.!]/, '').trim();
    if (typed !== '') {
        ranked.push({ kind: 'raw', key: 'raw', title: `Run as a command: .${typed}`, path: typed, group: 'raw', write: false, security: '', command: typed });
    }
    return ranked;
}

/** What an argument's form control holds as text, from the control. */
const valueOf = (control) => (control.type === 'checkbox' ? control.checked : control.value);

/**
 * Opens the palette.
 * @param {object} o
 * @param {() => Promise<object>} o.loadCatalog  returns the `action.catalog` data
 * @param {() => {guids: string[], names: string[]}} o.selection  the current selection
 * @param {() => (number[]|null)} o.lastPoint  the last clicked world point
 * @param {(descriptor: object) => Promise<object>} o.run  executes a `{type: 'action'|'run', ...}` descriptor and resolves with the result data
 * @param {() => void} [o.onClose]  runs when the dialog closes
 */
export async function openDevPalette({ loadCatalog, selection, lastPoint, run, onClose }) {
    const body = h('div', { class: 'palette devui-palette' }, h('p', { class: 'muted', text: 'Loading the daemon catalog.' }));
    const dialog = openDialog({ title: 'GM commands', content: body, wide: true, initialFocus: () => body.querySelector('input'), onClose });
    let catalog;
    try {
        catalog = await loadCatalog();
    } catch (error) {
        fill(body, h('p', { class: 'field-error', role: 'alert', text: `The catalog could not be loaded: ${error.message}` }));
        return dialog;
    }
    const entries = paletteEntries(catalog);
    const context = () => {
        const sel = selection();
        const point = lastPoint();
        return h('div', { class: 'devui-chips' },
            h('span', { class: 'devui-chip', text: sel.guids.length === 0 ? 'Target: none selected' : `Target: ${sel.names[0] || sel.guids[0]}${sel.guids.length > 1 ? ` +${sel.guids.length - 1}` : ''}` }),
            h('span', { class: 'devui-chip', text: point ? `Point: ${formatPoint(point, 1)}` : 'Point: click the map first' }));
    };

    const showResult = (box, data) => {
        const lines = [...(data?.replies ?? []), ...(data?.message ? [data.message] : [])];
        fill(box,
            h('div', { class: 'result-head' }, h('span', { class: `result-status ${data?.ok ? 'is-ok' : 'is-fail'}`, text: data?.ok ? 'ok' : (data?.code ?? 'failed') })),
            lines.length > 0 ? h('pre', { class: 'result-json num', tabindex: 0, text: lines.join('\n') }) : null);
    };

    const showForm = (entry) => {
        const resultBox = h('div', { class: 'palette-result', role: 'status' });
        const controls = new Map();
        let specs;
        if (entry.kind === 'action') {
            specs = entry.item.args.map((a) => ({ name: a.name, kind: a.type === 'number' || a.type === 'uint' ? 'number' : 'text', optional: false, default: a.default, label: a.label, min: a.min, max: a.max }));
        } else {
            specs = entry.parsed.args.filter((a) => a.kind !== 'literal').map((a) => ({ ...a, label: a.hint ? `${a.name} (${a.hint})` : a.name }));
        }
        const rows = specs.map((spec) => {
            let control;
            if (spec.kind === 'choice') {
                control = h('select', { class: 'input', name: spec.name }, spec.optional ? h('option', { value: '', text: '(none)' }) : null, spec.options.map((o) => h('option', { value: o, text: o })));
            } else if (spec.kind === 'flag') {
                control = h('input', { type: 'checkbox', name: spec.name });
            } else {
                control = h('input', { class: 'input', type: spec.kind === 'number' && entry.kind === 'action' ? 'number' : 'text', name: spec.name, value: spec.default ?? '', min: spec.min ?? undefined, max: spec.max ?? undefined, step: 'any', autocomplete: 'off', spellcheck: false, placeholder: spec.optional ? 'optional' : '' });
            }
            controls.set(spec.name, control);
            return h('label', { class: 'field' }, h('span', { class: 'field-name', text: spec.label ?? spec.name }), control);
        });
        const partial = entry.kind === 'command' && (entry.parsed.partial || !entry.parsed.ok);
        const tail = partial ? h('input', { class: 'input', type: 'text', name: 'tail', placeholder: 'arguments as typed (this command has more forms)', autocomplete: 'off', spellcheck: false }) : null;
        const submit = async () => {
            const sel = selection();
            const point = lastPoint();
            fill(resultBox, h('p', { class: 'muted', text: 'Running.' }));
            let descriptor;
            if (entry.kind === 'action') {
                const args = {};
                for (const [name, control] of controls) {
                    args[name] = valueOf(control);
                }
                descriptor = { type: 'action', actionId: entry.item.id, targets: entry.item.scope === 'entity' || entry.item.subject === 'selection' ? sel.guids : [], point: entry.item.needsPoint ? point : null, args, pick: null };
            } else {
                const values = {};
                for (const [name, control] of controls) {
                    values[name] = valueOf(control);
                }
                const line = buildLine(entry.path, entry.parsed.args, values, tail?.value ?? '');
                if (line === null) {
                    fill(resultBox, h('p', { class: 'field-error', role: 'alert', text: 'Fill in every required argument (one line, no line breaks).' }));
                    return;
                }
                descriptor = { type: 'run', command: line, targets: sel.guids, point };
            }
            showResult(resultBox, await run(descriptor));
        };
        const form = h('form', { class: 'devui-args', on: { submit: (e) => { e.preventDefault(); submit(); } } },
            h('div', { class: 'palette-command' },
                h('code', { class: 'palette-name', text: entry.title }),
                entry.security ? h('span', { class: 'badge', text: entry.security }) : null,
                entry.write ? h('span', { class: 'badge badge-write', text: 'write' }) : null,
                entry.help ? h('p', { class: 'muted', text: entry.help }) : null),
            context(), rows, tail,
            h('div', { class: 'form-actions' }, button('Back', () => showList(), { title: 'Back to the list' }), h('button', { class: 'button button-primary', type: 'submit', text: 'Run' })),
            resultBox);
        fill(body, form);
        form.querySelector('input, select')?.focus();
    };

    const choose = async (entry) => {
        if (entry.kind === 'raw') {
            const resultBox = h('div', { class: 'palette-result', role: 'status' }, h('p', { class: 'muted', text: 'Running.' }));
            fill(body, h('div', { class: 'palette-command' }, h('code', { class: 'palette-name', text: entry.title })), context(), resultBox, h('div', { class: 'form-actions' }, button('Back', () => showList())));
            showResult(resultBox, await run({ type: 'run', command: entry.command, targets: selection().guids, point: lastPoint() }));
            return;
        }
        const needsForm = entry.kind === 'command' ? entry.parsed.args.some((a) => a.kind !== 'literal') || entry.parsed.partial || !entry.parsed.ok : entry.item.args.length > 0;
        if (needsForm) {
            showForm(entry);
        } else {
            const sel = selection();
            const resultBox = h('div', { class: 'palette-result', role: 'status' }, h('p', { class: 'muted', text: 'Running.' }));
            fill(body, h('div', { class: 'palette-command' }, h('code', { class: 'palette-name', text: entry.title })), context(), resultBox, h('div', { class: 'form-actions' }, button('Back', () => showList())));
            const descriptor = entry.kind === 'command'
                ? { type: 'run', command: entry.path, targets: sel.guids, point: lastPoint() }
                : { type: 'action', actionId: entry.item.id, targets: entry.item.scope === 'entity' || entry.item.subject === 'selection' ? sel.guids : [], point: entry.item.needsPoint ? lastPoint() : null, args: {}, pick: entry.item.pick ?? null };
            showResult(resultBox, await run(descriptor));
        }
    };

    function showList(text = '') {
        const input = h('input', {
            class: 'input palette-input', type: 'text', value: text, spellcheck: false, autocomplete: 'off', role: 'combobox',
            placeholder: entries.length === 0 ? 'Type a command line to send it' : `Search ${entries.length} commands and actions, or type a command line`,
            aria: { expanded: 'true', autocomplete: 'list', label: 'Search GM commands' },
        });
        const list = h('ul', { class: 'palette-list', role: 'listbox', aria: { label: 'GM commands' } });
        let matches = [];
        let active = 0;
        const render = () => {
            matches = filterEntries(entries, input.value);
            active = Math.min(active, Math.max(matches.length - 1, 0));
            fill(list, ...matches.map((m, i) => h('li', {
                class: `palette-item${i === active ? ' is-active' : ''}`,
                role: 'option',
                aria: { selected: i === active ? 'true' : 'false' },
                data: { key: m.key },
                on: { mousedown: (e) => e.preventDefault(), click: () => choose(m) },
            },
            h('span', { class: 'palette-name', text: m.title }),
            m.security ? h('span', { class: 'badge', text: m.security }) : null,
            m.write ? h('span', { class: 'badge badge-write', text: 'write' }) : null,
            m.kind === 'action' ? h('span', { class: 'palette-summary', text: m.item.template ? `template ${m.item.template}` : m.path }) : null)));
            list.children[active]?.scrollIntoView({ block: 'nearest' });
        };
        input.addEventListener('input', () => {
            active = 0;
            render();
        });
        input.addEventListener('keydown', (e) => {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                active = Math.min(active + 1, matches.length - 1);
                render();
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                active = Math.max(active - 1, 0);
                render();
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (matches[active]) {
                    choose(matches[active]);
                }
            }
        });
        fill(body, context(), input, list);
        render();
        input.focus();
    }

    showList();
    return dialog;
}
