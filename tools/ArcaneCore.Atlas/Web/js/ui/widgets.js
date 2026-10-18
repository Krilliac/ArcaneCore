// Reusable controls shared by the panels.

import { h } from '../util.js';

/**
 * A segmented control (radio group): arrow keys move the choice, Tab leaves the group. `options` are
 * {value, label, title?}. `onSelect(value)` runs on click or arrow key. Call `update(value)` to reflect state.
 */
export function segmented({ label, options, onSelect, className = '' }) {
    const buttons = options.map((o) => h('button', {
        class: 'segment',
        type: 'button',
        role: 'radio',
        title: o.title ?? o.label,
        text: o.label,
        data: { value: o.value },
        aria: { checked: 'false' },
        tabindex: -1,
    }));
    const element = h('div', { class: `segmented ${className}`.trim(), role: 'radiogroup', aria: { label } }, buttons);
    let current = null;
    const apply = (value) => {
        current = value;
        buttons.forEach((b, i) => {
            const on = options[i].value === value;
            b.setAttribute('aria-checked', on ? 'true' : 'false');
            b.classList.toggle('is-on', on);
            b.tabIndex = on || (value === null && i === 0) ? 0 : -1;
        });
    };
    buttons.forEach((b, i) => {
        b.addEventListener('click', () => onSelect(options[i].value));
        b.addEventListener('keydown', (e) => {
            let next = null;
            if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
                next = (i + 1) % buttons.length;
            } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
                next = (i + buttons.length - 1) % buttons.length;
            }
            if (next !== null) {
                e.preventDefault();
                buttons[next].focus();
                onSelect(options[next].value);
            }
        });
    });
    apply(null);
    return { element, update: (value) => value !== current && apply(value), setDisabled: (off) => buttons.forEach((b) => (b.disabled = off)) };
}

/** A labelled checkbox that can show a mixed state. */
export function checkbox({ label, title, onChange, className = '' }) {
    const input = h('input', { type: 'checkbox', on: { change: () => onChange(input.checked) } });
    const element = h('label', { class: `check ${className}`.trim(), title }, input, h('span', { text: label }));
    return {
        element,
        input,
        set(state) {
            input.indeterminate = state === 'mixed';
            input.checked = state === true;
        },
    };
}

/** A small text button. */
export function button(label, onClick, { title, primary = false, className = '' } = {}) {
    return h('button', {
        class: `button${primary ? ' button-primary' : ''} ${className}`.trim(),
        type: 'button',
        title,
        text: label,
        on: { click: onClick },
    });
}

/** An empty-state block: a short statement and what to do about it. */
export function emptyState(title, hint) {
    return h('div', { class: 'empty' }, h('div', { class: 'empty-title', text: title }), hint ? h('div', { class: 'empty-hint', text: hint }) : null);
}

/** A key/value list. `pairs` is [[key, text]]; a node as the value is inserted as is. */
export function keyValues(pairs, className = '') {
    const dl = h('dl', { class: `kv ${className}`.trim() });
    for (const [key, value] of pairs) {
        dl.append(h('dt', { text: key }), h('dd', null, value instanceof Node ? value : String(value)));
    }
    return dl;
}

/** Copies text to the clipboard; resolves true on success. */
export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        const area = h('textarea', { value: text, style: { position: 'fixed', opacity: '0' } });
        document.body.append(area);
        area.select();
        const ok = document.execCommand('copy');
        area.remove();
        return ok;
    }
}
