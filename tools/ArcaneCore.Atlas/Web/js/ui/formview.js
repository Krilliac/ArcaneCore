// DOM for the argument forms that forms.js describes (probe cards and the command palette). The form never guesses: a
// field's text is parsed strictly by parseField and a problem is shown next to that field with what to type.

import { buildArgs, defaultText, formatPointText } from '../forms.js';
import { h } from '../util.js';

let formCounter = 0;

/**
 * @param {object} o
 * @param {Array} o.fields                 fieldsFromSchema() result
 * @param {Record<string,string>} [o.initial]  starting texts by field name
 * @param {Array<{label: string, title?: string, get: () => (number[]|null)}>} [o.pointSources]  buttons that fill 3-number fields
 * @param {string} [o.submitLabel]
 * @param {(args: object) => void} o.onSubmit  called with parsed args when every field is valid
 * @param {Array<Node>} [o.actions]        extra buttons placed next to the submit button
 */
export function buildForm({ fields, initial = {}, pointSources = [], submitLabel = 'Run', onSubmit, actions = [] }) {
    const id = ++formCounter;
    const inputs = new Map();
    const errorNodes = new Map();
    const rows = [];

    for (const field of fields) {
        const inputId = `f${id}-${field.name}`;
        const start = initial[field.name] ?? defaultText(field);
        const input = makeInput(field, inputId, start);
        input.dataset.auto = '0';
        const markTouched = () => {
            input.dataset.auto = '0';
        };
        input.addEventListener('input', markTouched);
        input.addEventListener('change', markTouched);
        inputs.set(field.name, input);
        const error = h('div', { class: 'field-error', id: `${inputId}-error`, role: 'alert', hidden: true });
        errorNodes.set(field.name, error);
        input.setAttribute('aria-describedby', `${inputId}-error${field.description ? ` ${inputId}-help` : ''}`);
        const label = h('label', { class: 'field-label', for: inputId },
            h('span', { class: 'field-name', text: field.name }),
            field.required ? null : h('span', { class: 'field-optional', text: 'optional' }));
        const sourceButtons = field.kind === 'point3' && pointSources.length > 0
            ? h('div', { class: 'field-sources' }, pointSources.map((src) => h('button', {
                class: 'chip-button',
                type: 'button',
                title: src.title ?? src.label,
                text: src.label,
                on: {
                    click: () => {
                        const p = src.get();
                        if (p) {
                            input.value = formatPointText(p);
                            input.dataset.auto = '0';
                            input.dispatchEvent(new Event('change', { bubbles: true }));
                        }
                    },
                },
            })))
            : null;
        const help = field.description ? h('div', { class: 'field-help', id: `${inputId}-help`, text: field.description }) : null;
        if (field.kind === 'boolean') {
            rows.push(h('div', { class: 'field field-boolean' }, h('label', { class: 'check', for: inputId }, input, h('span', { text: field.name })), help, error));
        } else {
            rows.push(h('div', { class: 'field' }, label, h('div', { class: 'field-control' }, input, sourceButtons), help, error));
        }
    }

    const submit = h('button', { class: 'button button-primary', type: 'submit', text: submitLabel });
    const element = h('form', {
        class: 'form',
        attrs: { novalidate: '' },
        on: {
            submit: (e) => {
                e.preventDefault();
                api.run();
            },
        },
    }, rows, h('div', { class: 'form-actions' }, submit, actions));

    const api = {
        element,
        submit,
        texts() {
            const out = {};
            for (const field of fields) {
                const input = inputs.get(field.name);
                out[field.name] = field.kind === 'boolean' ? (input.checked ? 'true' : '') : input.value;
            }
            return out;
        },
        /** Parses and shows problems; returns the args or null. */
        parse() {
            const { args, errors } = buildArgs(fields, api.texts());
            for (const field of fields) {
                const node = errorNodes.get(field.name);
                const message = errors[field.name];
                node.textContent = message ?? '';
                node.hidden = !message;
                inputs.get(field.name).setAttribute('aria-invalid', message ? 'true' : 'false');
            }
            const firstBad = fields.find((f) => errors[f.name]);
            if (firstBad) {
                inputs.get(firstBad.name).focus();
                return null;
            }
            return args;
        },
        run() {
            const args = api.parse();
            if (args) {
                onSubmit(args);
            }
        },
        /** Sets a field's text. `auto` marks it as filled by the app, so a later auto value may replace it but typing wins. */
        set(name, text, { auto = false } = {}) {
            const input = inputs.get(name);
            if (!input) {
                return;
            }
            if (auto && input.dataset.auto !== '1' && input.value !== '') {
                return;
            }
            if (input.type === 'checkbox') {
                input.checked = text === 'true';
            } else {
                input.value = text;
            }
            input.dataset.auto = auto ? '1' : '0';
        },
        get(name) {
            return inputs.get(name) ?? null;
        },
        isAuto(name) {
            return inputs.get(name)?.dataset.auto === '1';
        },
        focusFirst() {
            const first = fields.length > 0 ? inputs.get(fields[0].name) : submit;
            first.focus();
        },
        setBusy(busy) {
            submit.disabled = busy;
            element.setAttribute('aria-busy', busy ? 'true' : 'false');
        },
    };
    return api;
}

function makeInput(field, id, start) {
    switch (field.kind) {
        case 'boolean':
            return h('input', { id, type: 'checkbox', checked: start === 'true' });
        case 'choice': {
            const select = h('select', { id, class: 'select' }, !field.required ? h('option', { value: '', text: '(default)' }) : null,
                field.choices.map((c) => h('option', { value: c, text: c })));
            select.value = start;
            return select;
        }
        case 'json':
            return h('textarea', { id, class: 'input', rows: 3, spellcheck: false, value: start, placeholder: '{ }' });
        case 'point3':
            return h('input', { id, class: 'input num', type: 'text', value: start, spellcheck: false, autocomplete: 'off', placeholder: 'x, y, z', attrs: { inputmode: 'decimal' } });
        case 'integer':
        case 'number':
            return h('input', { id, class: 'input num', type: 'text', value: start, spellcheck: false, autocomplete: 'off', attrs: { inputmode: 'decimal' } });
        default:
            return h('input', { id, class: 'input', type: 'text', value: start, spellcheck: false, autocomplete: 'off' });
    }
}
