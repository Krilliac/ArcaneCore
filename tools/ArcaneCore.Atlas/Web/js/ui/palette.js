// Ctrl+K command palette: lists the host's registered commands (GET /api/commands), filters as you type, builds an
// argument form from the command's JSON schema and runs it through the same registry the CLI and MCP use.

import { autoFill, fieldsFromSchema } from '../forms.js';
import { filterCommands, paletteCommands } from '../normalize.js';
import { fill, formatDuration, h } from '../util.js';
import { buildForm } from './formview.js';
import { openDialog } from './popover.js';
import { button, emptyState } from './widgets.js';

const MAX_RESULT_CHARS = 20_000;
let paletteCounter = 0;

/** Opens the palette. `initial` may name a command to open straight into its form. */
export function openPalette(app, { initial = null } = {}) {
    const id = ++paletteCounter;
    const body = h('div', { class: 'palette' });
    const dialog = openDialog({ title: 'Commands', content: body, wide: true, initialFocus: () => body.querySelector('input') });

    const showList = (text = '') => {
        const commands = paletteCommands(app.commands);
        const input = h('input', {
            class: 'input palette-input',
            type: 'text',
            value: text,
            placeholder: commands.length === 0 ? 'No commands yet' : `Search ${commands.length} commands`,
            spellcheck: false,
            autocomplete: 'off',
            role: 'combobox',
            aria: { expanded: 'true', controls: `palette-${id}-list`, autocomplete: 'list', label: 'Search commands' },
        });
        const list = h('ul', { class: 'palette-list', id: `palette-${id}-list`, role: 'listbox', aria: { label: 'Commands' } });
        let matches = [];
        let active = 0;
        const render = () => {
            matches = filterCommands(commands, input.value);
            active = Math.min(active, Math.max(matches.length - 1, 0));
            if (commands.length === 0) {
                fill(list, h('li', { class: 'empty-line', text: 'The host has not sent its command list yet. Check the connection in the status bar.' }));
                return;
            }
            if (matches.length === 0) {
                fill(list, h('li', { class: 'empty-line', text: `No command matches "${input.value}".` }));
                return;
            }
            fill(list, ...matches.slice(0, 80).map((c, i) => h('li', {
                class: `palette-item${i === active ? ' is-active' : ''}`,
                id: `palette-${id}-opt-${i}`,
                role: 'option',
                aria: { selected: i === active ? 'true' : 'false' },
                on: { mousedown: (e) => e.preventDefault(), click: () => showCommand(c) },
            }, h('span', { class: 'palette-name', text: c.name }), h('span', { class: 'palette-summary', text: c.summary }))));
            input.setAttribute('aria-activedescendant', `palette-${id}-opt-${active}`);
            list.children[active]?.scrollIntoView({ block: 'nearest' });
        };
        input.addEventListener('input', () => {
            active = 0;
            render();
        });
        input.addEventListener('keydown', (e) => {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                active = Math.min(active + 1, Math.min(matches.length, 80) - 1);
                render();
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                active = Math.max(active - 1, 0);
                render();
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (matches[active]) {
                    showCommand(matches[active]);
                }
            }
        });
        fill(body, input, list);
        render();
        input.focus();
    };

    const showCommand = (command) => {
        const fields = fieldsFromSchema(command.schema);
        const resultBox = h('div', { class: 'palette-result', role: 'status' });
        const form = buildForm({
            fields,
            submitLabel: 'Run',
            onSubmit: async (args) => {
                form.setBusy(true);
                fill(resultBox, h('p', { class: 'muted', text: 'Running.' }));
                const result = await app.runCommand(command.name, args, { quiet: true });
                form.setBusy(false);
                const text = JSON.stringify(result.ok ? result.data : result.error, null, 2) ?? '';
                fill(resultBox, 
                    h('div', { class: 'result-head' },
                        h('span', { class: `result-status ${result.ok ? 'is-ok' : 'is-fail'}`, text: result.ok ? 'ok' : 'failed' }),
                        h('span', { class: 'result-time num', text: formatDuration(result.elapsedMs) }),
                        result.ok && result.data?.overlay ? h('span', { class: 'muted', text: 'Its overlay is drawn on the map.' }) : null),
                    h('pre', { class: 'result-json num', tabindex: 0, text: text.length > MAX_RESULT_CHARS ? `${text.slice(0, MAX_RESULT_CHARS)}\n… ${text.length - MAX_RESULT_CHARS} more characters` : text }));
            },
            actions: [button('Back', () => showList(), { title: 'Back to the command list' })],
        });
        for (const [name, value] of Object.entries(autoFill(fields, { clicks: app.clicks, map: app.view.map }))) {
            form.set(name, value, { auto: true });
        }
        fill(body, 
            h('div', { class: 'palette-command' }, h('code', { class: 'palette-name', text: command.name }), h('p', { class: 'muted', text: command.summary })),
            fields.length === 0 ? emptyState('No arguments', 'Press Run to send the command.') : null,
            form.element,
            resultBox);
        form.focusFirst();
    };

    if (initial && app.commandNamed(initial)) {
        showCommand(app.commandNamed(initial));
    } else {
        showList();
    }
    return dialog;
}
