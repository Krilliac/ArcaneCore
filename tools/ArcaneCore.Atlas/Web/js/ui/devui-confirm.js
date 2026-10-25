// Confirmation dialogs of the dev UI: the bulk confirmation (an action on more than one entity names the count and the targets before it
// runs) and the argument prompt of an action that takes values (spawn entry, level, speed...).

import { h } from '../util.js';
import { openDialog } from './popover.js';
import { button } from './widgets.js';

/** How many target names the dialog lists before "and N more". */
export const LISTED_TARGETS = 12;

/** The lines the bulk dialog shows for a target list: names (or guids) up to the cap, then a count of the rest. */
export function targetLines(targets, nameOf) {
    const shown = targets.slice(0, LISTED_TARGETS).map((g) => nameOf(g) || g);
    return targets.length > LISTED_TARGETS ? [...shown, `and ${targets.length - LISTED_TARGETS} more`] : shown;
}

/**
 * Asks before an action runs on several entities (or before one that destroys something). Resolves true on Confirm, false on Cancel,
 * Escape or a click outside.
 * @param {{title: string, summary: string, lines?: string[], confirmLabel?: string}} o
 */
export function confirmDialog({ title, summary, lines = [], confirmLabel = 'Confirm' }) {
    return new Promise((resolve) => {
        let answered = false;
        const answer = (value) => {
            if (!answered) {
                answered = true;
                resolve(value);
            }
            dialog.close();
        };
        const list = lines.length > 0 ? h('ul', { class: 'devui-targets plain-list', aria: { label: 'Targets' } }, lines.map((l) => h('li', { text: l }))) : null;
        const confirm = button(confirmLabel, () => answer(true), { primary: true, className: 'devui-confirm-yes' });
        const cancel = button('Cancel', () => answer(false), { className: 'devui-confirm-no' });
        const content = h('div', { class: 'devui-confirm' },
            h('p', { class: 'devui-confirm-summary', text: summary }),
            list,
            h('div', { class: 'form-actions' }, cancel, confirm));
        const dialog = openDialog({ title, content, initialFocus: () => cancel, onClose: () => answer(false) });
    });
}

/**
 * Asks for the values of an action's arguments. `specs` are the catalog's `{name, type, label, min?, max?, default?}`. Resolves with
 * `{name: text}` (the server validates again) or null when cancelled.
 * @param {{title: string, specs: object[], extra?: Node|null, submitLabel?: string}} o
 */
export function promptArgs({ title, specs, extra = null, submitLabel = 'Run' }) {
    return new Promise((resolve) => {
        let answered = false;
        const answer = (value) => {
            if (!answered) {
                answered = true;
                resolve(value);
            }
            dialog.close();
        };
        const inputs = new Map();
        const error = h('div', { class: 'field-error', role: 'alert', hidden: true });
        const rows = specs.map((spec) => {
            const input = h('input', {
                class: 'input',
                type: spec.type === 'number' || spec.type === 'uint' ? 'number' : 'text',
                id: `devui-arg-${spec.name}`,
                name: spec.name,
                value: spec.default ?? '',
                min: spec.min ?? undefined,
                max: spec.max ?? undefined,
                step: spec.type === 'number' ? 'any' : '1',
                autocomplete: 'off',
                spellcheck: false,
            });
            inputs.set(spec.name, input);
            return h('label', { class: 'field', for: input.id }, h('span', { class: 'field-name', text: spec.label }), input);
        });
        const submit = () => {
            const values = {};
            for (const spec of specs) {
                const input = inputs.get(spec.name);
                if (input.value.trim() === '' || (input.type === 'number' && !input.checkValidity())) {
                    error.textContent = `${spec.label}: enter a value${spec.min !== undefined && spec.max !== undefined ? ` from ${spec.min} to ${spec.max}` : ''}.`;
                    error.hidden = false;
                    input.focus();
                    return;
                }
                values[spec.name] = input.value.trim();
            }
            answer(values);
        };
        const form = h('form', { class: 'devui-args', on: { submit: (e) => { e.preventDefault(); submit(); } } },
            rows, extra, error,
            h('div', { class: 'form-actions' }, button('Cancel', () => answer(null)), h('button', { class: 'button button-primary', type: 'submit', text: submitLabel })));
        const dialog = openDialog({ title, content: form, initialFocus: () => inputs.values().next().value, onClose: () => answer(null) });
    });
}
